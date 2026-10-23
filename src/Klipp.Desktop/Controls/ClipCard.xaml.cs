using System;
using System.IO;
using System.Threading;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Klipp.Desktop.Controls;

/// <summary>
/// Återanvändbart kort som visar ett enskilt klipp i biblioteket.
/// Klickbart — exponerar Click-event som triggers när användaren klickar någonstans på kortet.
/// </summary>
public sealed partial class ClipCard : UserControl
{
    private static readonly SemaphoreSlim ThumbnailDecodeGate = new(initialCount: 1, maxCount: 1);

    /// <summary>Triggas när användaren klickar på kortet.</summary>
    public event EventHandler<ClipCardClickEventArgs>? Clicked;
    public event EventHandler? FullscreenRequested;
    public event EventHandler? FullscreenExitRequested;
    public event EventHandler? PreviewStarting;
    public event EventHandler? PreviewStopped;
    public event EventHandler<ClipCardClickEventArgs>? OpenFolderRequested;
    public event EventHandler<ClipCardClickEventArgs>? EditRequested;
    public event EventHandler<ClipCardClickEventArgs>? FavoriteRequested;
    public event EventHandler<ClipCardClickEventArgs>? OrganizeRequested;
    public event EventHandler<ClipCardClickEventArgs>? RenameRequested;
    public event EventHandler<ClipCardClickEventArgs>? DeleteRequested;
    public event EventHandler<ClipCardClickEventArgs>? UploadRequested;

    /// <summary>
    /// Filsökväg som identifierar vilket klipp kortet representerar.
    /// Sätts via x:Bind från ClipViewModel.
    /// </summary>
    private string _clipFilePath = string.Empty;
    public string ClipFilePath
    {
        get => _clipFilePath;
        set
        {
            if (string.Equals(_clipFilePath, value, StringComparison.OrdinalIgnoreCase)) return;
            StopInlinePreview();
            _clipFilePath = value ?? string.Empty;
        }
    }

    public ClipCard()
    {
        InitializeComponent();
        InitializeInlinePreview();
        Unloaded += (_, _) =>
        {
            Interlocked.Increment(ref _thumbnailRequestVersion);
            if (!_inlineFullscreen) StopInlinePreview();
        };
    }

    public string ClipTitle
    {
        get => TitleText.Text;
        set
        {
            TitleText.Text = value;
            ToolTipService.SetToolTip(TitleText, value);
            AutomationProperties.SetName(this, string.IsNullOrWhiteSpace(value) ? "Clip" : value);
        }
    }

    public string ClipMeta
    {
        get => MetaText.Text;
        set => MetaText.Text = value;
    }

    public string Duration
    {
        get => DurationText.Text;
        set => DurationText.Text = value;
    }

    public bool IsNew
    {
        get => NewBadge.Visibility == Visibility.Visible;
        set => NewBadge.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }
    private bool _isFavorite;

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            _isFavorite = value;
            FavoriteGlyphText.Text = value ? "\uE735" : "\uE734";
            FavoriteBadge.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            var favoriteAction = value ? "Remove from favorites" : "Add to favorites";
            AutomationProperties.SetName(FavoriteButton, favoriteAction);
            ToolTipService.SetToolTip(FavoriteButton, favoriteAction);
        }
    }

    public string ClipOrganization
    {
        get => OrganizationText.Text;
        set
        {
            OrganizationText.Text = value ?? string.Empty;
            OrganizationText.Visibility = string.IsNullOrWhiteSpace(value)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }
    private string _thumbnailPath = string.Empty;
    private bool _isPointerOver;
    private bool _pressedCardBody;
    private bool _hasFocus;
    private int _thumbnailRequestVersion;

    /// <summary>
    /// Sökväg till thumbnail-bild. Laddas via stream (undviker packaged-app
    /// sandbox-problem med fil-URI:er).
    /// </summary>
    public string ThumbnailPath
    {
        get => _thumbnailPath;
        set
        {
            _thumbnailPath = value;
            ThumbnailImage.Source = null;
            ThumbnailImage.Opacity = 0;
            ThumbnailImage.Visibility = Visibility.Collapsed;
            PlaceholderIcon.Visibility = Visibility.Visible;
            var version = Interlocked.Increment(ref _thumbnailRequestVersion);
            _ = LoadThumbnailAsync(value, version);
        }
    }

    private async System.Threading.Tasks.Task LoadThumbnailAsync(string path, int version)
    {
        if (string.IsNullOrEmpty(path))
        {
            ThumbnailImage.Source = null;
            ThumbnailImage.Visibility = Visibility.Collapsed;
            PlaceholderIcon.Visibility = Visibility.Visible;
            return;
        }

        if (!System.IO.File.Exists(path))
            return;

        var requestedPath = path;
        await ThumbnailDecodeGate.WaitAsync();
        try
        {
            if (version != _thumbnailRequestVersion)
                return;

            var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            // Avkoda i kortets storlek (inte full upplösning) — snabbare scroll, mindre minne.
            bitmap.DecodePixelWidth = (int)Math.Clamp(
                Math.Ceiling(Math.Max(272, ActualWidth) * (XamlRoot?.RasterizationScale ?? 1)),
                272,
                960);
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());

            if (version != _thumbnailRequestVersion ||
                !string.Equals(_thumbnailPath, requestedPath, StringComparison.OrdinalIgnoreCase))
                return;

            ThumbnailImage.Source = bitmap;
            ThumbnailImage.Visibility = Visibility.Visible;
            ThumbnailImage.Opacity = 1;
            PlaceholderIcon.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // Behåll placeholder-ikonen om bilden inte kan laddas
        }
        finally
        {
            ThumbnailDecodeGate.Release();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // The library measures a card at its final column width before sizing each row.
        // Reserve the full thumbnail and footer during measure, rather than growing
        // the thumbnail after arrange and pushing the actions outside the card.
        if (double.IsFinite(availableSize.Width) && availableSize.Width > 0)
        {
            var scale = XamlRoot?.RasterizationScale ?? 1;
            var previewWidth = Math.Max(0, availableSize.Width - CardRoot.BorderThickness.Left - CardRoot.BorderThickness.Right);
            var previewHeight = Math.Ceiling(previewWidth * 9 / 16 * scale) / scale;
            if (_inlinePlaceholder is not null)
                _inlinePlaceholder.Height = previewHeight;
            else if (Math.Abs(ThumbnailFrame.Height - previewHeight) > 0.01)
                ThumbnailFrame.Height = previewHeight;
        }
        return base.MeasureOverride(availableSize);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        UpdateInteractionChrome();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        UpdateInteractionChrome();
    }

    private void OnCardGotFocus(object sender, RoutedEventArgs e)
    {
        _hasFocus = true;
        UpdateInteractionChrome();
        ShowInlineChrome();
    }

    private void OnCardLostFocus(object sender, RoutedEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var root = XamlRoot;
            _hasFocus = root is not null && FocusManager.GetFocusedElement(root) is DependencyObject focused &&
                IsDescendant(focused);
            UpdateInteractionChrome();
        });
    }

    private void UpdateInteractionChrome()
    {
        // Repeated hover and keyboard navigation stay immediate and never move the target.
        HoverBorder.Opacity = _hasFocus ? 0.85 : _isPointerOver ? 0.35 : 0;
        HoverOverlay.Opacity = _inlinePlayer is null && !_inlineFullscreen && _pressedCardBody ? 0.08 : 0;
        UpdateInlineChrome();
    }

    private void OnCardKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), this))
            return;

        if (e.Key is VirtualKey.Enter or VirtualKey.Space)
        {
            _lastInlineTapUtc = DateTime.MinValue;
            ToggleInlinePlayback();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.F)
        {
            OpenInlineInPlayer();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.M && _inlinePlayer is not null)
        {
            ToggleInlineMute();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.E)
        {
            EditRequested?.Invoke(this, new ClipCardClickEventArgs(ClipFilePath));
            e.Handled = true;
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsActionSource(e.OriginalSource) ||
            e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse &&
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _pressedCardBody = true;
        Focus(FocusState.Pointer);
        UpdateInteractionChrome();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _pressedCardBody = false;
        UpdateInteractionChrome();
    }

    private void OnCardPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        _pressedCardBody = false;
        UpdateInteractionChrome();
    }

    private void OnActionBarPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _pressedCardBody = false;
        e.Handled = true;
    }

    private void OnActionBarPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _pressedCardBody = false;
        e.Handled = true;
    }

    private void OnOpenFolderClicked(object sender, RoutedEventArgs e)
    {
        OpenFolderRequested?.Invoke(this, new ClipCardClickEventArgs(ClipFilePath));
    }

    private void OnEditClicked(object sender, RoutedEventArgs e)
    {
        EditRequested?.Invoke(this, new ClipCardClickEventArgs(ClipFilePath));
    }

    private void OnFavoriteClicked(object sender, RoutedEventArgs e)
    {
        FavoriteRequested?.Invoke(this, new ClipCardClickEventArgs(ClipFilePath));
    }

    private void OnOrganizeClicked(object sender, RoutedEventArgs e)
    {
        OrganizeRequested?.Invoke(this, new ClipCardClickEventArgs(ClipFilePath));
    }

    private void OnRenameClicked(object sender, RoutedEventArgs e)
    {
        RenameRequested?.Invoke(this, new ClipCardClickEventArgs(ClipFilePath));
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        DeleteRequested?.Invoke(this, new ClipCardClickEventArgs(ClipFilePath));
    }

    private void OnUploadClicked(object sender, RoutedEventArgs e)
    {
        UploadRequested?.Invoke(this, new ClipCardClickEventArgs(ClipFilePath));
    }

    private bool IsActionSource(object originalSource)
    {
        if (originalSource is not DependencyObject current)
            return false;

        while (current is not null)
        {
            if (ReferenceEquals(current, ActionBar) || ReferenceEquals(current, InlineControls) ||
                ReferenceEquals(current, InlineFullscreenHeader))
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private bool IsDescendant(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, this))
                return true;
        }
        return false;
    }

}

/// <summary>
/// Event args för ClipCard.Clicked.
/// </summary>
public sealed class ClipCardClickEventArgs : EventArgs
{
    public string FilePath { get; }
    public TimeSpan? Position { get; }
    public bool AutoPlay { get; }
    public bool? IsMuted { get; }
    public double? Volume { get; }

    public ClipCardClickEventArgs(string filePath, TimeSpan? position = null, bool autoPlay = true,
        bool? isMuted = null, double? volume = null)
    {
        FilePath = filePath;
        Position = position;
        AutoPlay = autoPlay;
        IsMuted = isMuted;
        Volume = volume;
    }
}
