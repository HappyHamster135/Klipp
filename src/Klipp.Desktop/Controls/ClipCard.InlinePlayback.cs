using System;
using System.IO;
using System.Threading.Tasks;
using Klipp.Core.Playback;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;

namespace Klipp.Desktop.Controls;

public sealed partial class ClipCard
{
    // Realized library cards remain cheap: allocate a decoder only after an explicit play.
    private static WeakReference<ClipCard>? _activeInlineCard;
    private static WeakReference<ClipCard>? _pendingInlineTapCard;
    private MediaPlayer? _inlinePlayer;
    private MediaSource? _inlineSource;
    private MediaPlayerElement? _inlineElement;
    private DispatcherTimer? _inlineTimer;
    private DispatcherTimer? _inlineChromeTimer;
    private bool _inlineChromeVisible = true;
    private bool _inlineChromePointerDown;
    private bool _inlineWantsPlay;
    private bool _inlineMediaOpened;
    private PlaybackMediaIdentity? _inlineMediaIdentity;
    private DateTime _inlineLastPositionSaveUtc;
    private bool _syncingInlineUi;
    private bool _inlineScrubbing;
    private bool _inlineResumeAfterScrub;
    private bool _inlineMuted;
    private double _inlineVolume = 0.7;
    private bool _inlineFullscreen;
    private bool _inlineReturning;
    private Grid? _inlinePlaceholder;
    private Grid? _inlineThumbnailParent;
    private int _inlineThumbnailIndex;
    private bool _inlineRootHosted;
    private Visibility _inlineNewBadgeVisibility;
    private Visibility _inlineFavoriteBadgeVisibility;
    private int _inlineTapVersion;
    private DateTime _ignoreInlineTapUntilUtc;
    private DateTime _lastInlineTapUtc;
    private bool _inlineDoubleTapPlayIntent = true;

    private void InitializeInlinePreview()
    {
        // Playback stays in a root-level host in both sizes, so input follows
        // the video view instead of relying on the card's original ancestors.
        ThumbnailFrame.AddHandler(PointerMovedEvent, new PointerEventHandler(OnInlineChromePointerMoved), true);
        ThumbnailFrame.PointerEntered += (_, _) =>
        {
            _isPointerOver = true;
            ShowInlineChrome();
        };
        ThumbnailFrame.PointerExited += (_, _) =>
        {
            _isPointerOver = false;
            UpdateInlineChrome();
        };
        ThumbnailFrame.AddHandler(PointerPressedEvent, new PointerEventHandler(OnInlineChromePointerPressed), true);
        ThumbnailFrame.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnInlineChromePointerReleased), true);
        ThumbnailFrame.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnInlineChromePointerReleased), true);
        ThumbnailFrame.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnInlineChromePointerReleased), true);
        ThumbnailFrame.GotFocus += (_, _) => ShowInlineChrome();
        EffectiveViewportChanged += (_, e) =>
        {
            if (_inlinePlayer is null || _inlineFullscreen || _inlineReturning) return;
            // ItemsRepeater keeps an offscreen realization cache, so Unloaded alone
            // cannot prevent invisible playback or lingering file/decoder handles.
            var viewport = e.EffectiveViewport;
            if (viewport.Width <= 0 || viewport.Height <= 0 || viewport.Right <= 0 || viewport.Bottom <= 0 ||
                ActualWidth > 0 && viewport.X >= ActualWidth ||
                ThumbnailFrame.ActualHeight > 0 && viewport.Y >= ThumbnailFrame.ActualHeight + CardRoot.BorderThickness.Top)
                StopInlinePreview();
        };
        InlineSeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(OnInlineSeekPressed), true);
        InlineSeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnInlineSeekReleased), true);
        InlineSeekSlider.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnInlineSeekReleased), true);
        InlineSeekSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnInlineSeekReleased), true);
    }

    public static void StopActivePreview(string? filePath = null)
    {
        CancelPendingInlineTap(filePath);
        if (_activeInlineCard?.TryGetTarget(out var card) == true &&
            (filePath is null || string.Equals(filePath, card.ClipFilePath, StringComparison.OrdinalIgnoreCase)))
            card.StopInlinePreview();
    }

    private static void CancelPendingInlineTap(string? filePath = null)
    {
        if (_pendingInlineTapCard is not { } pending || !pending.TryGetTarget(out var card) || card is null) return;
        if (filePath is not null && !string.Equals(filePath, card.ClipFilePath, StringComparison.OrdinalIgnoreCase)) return;
        ++card._inlineTapVersion;
        _pendingInlineTapCard = null;
    }

    public static bool HasActivePreview => _activeInlineCard is { } active &&
        active.TryGetTarget(out var card) && card._inlinePlayer is not null;

    public static void PauseActivePreview()
    {
        CancelPendingInlineTap();
        if (_activeInlineCard is not { } active || !active.TryGetTarget(out var card) || card._inlinePlayer is null) return;
        card._inlineWantsPlay = false;
        card._inlineResumeAfterScrub = false;
        card._inlinePlayer.Pause();
        card._inlineTimer?.Stop();
        card.RememberInlinePosition();
        card.UpdateInlineUi();
        card.ShowInlineChrome();
    }

    private void ToggleInlinePlayback()
    {
        CancelPendingInlineTap();
        ++_inlineTapVersion;
        if (_inlinePlayer is null)
        {
            StartInlinePreview();
            return;
        }

        if (_inlineWantsPlay)
        {
            _inlineWantsPlay = false;
            _inlinePlayer.Pause();
            _inlineTimer?.Stop();
            RememberInlinePosition();
        }
        else
        {
            var session = _inlinePlayer.PlaybackSession;
            if (session.NaturalDuration > TimeSpan.Zero &&
                session.Position >= session.NaturalDuration - TimeSpan.FromMilliseconds(100))
                session.Position = TimeSpan.Zero;
            _inlineWantsPlay = true;
            if (_inlineMediaOpened) _inlinePlayer.Play();
            _inlineTimer?.Start();
        }
        UpdateInlineUi();
        ShowInlineChrome();
    }

    private void StartInlinePreview()
    {
        if (!File.Exists(ClipFilePath))
        {
            ShowInlineFailure();
            return;
        }

        StopActivePreview();
        try
        {
            // The host must attach the view before the presentation surface is
            // created. Fullscreen then changes bounds, never the live visual tree.
            PreviewStarting?.Invoke(this, EventArgs.Empty);
            _inlineMediaIdentity = PlaybackMediaIdentity.TryCreate(ClipFilePath);
            _inlineMediaOpened = false;
            _inlineLastPositionSaveUtc = DateTime.UtcNow;
            InlineErrorBorder.Visibility = Visibility.Collapsed;
            _inlinePlayer = new MediaPlayer { AutoPlay = false, Volume = _inlineVolume, IsMuted = _inlineMuted };
            _inlinePlayer.CommandManager.IsEnabled = false;
            _inlinePlayer.MediaOpened += OnInlineMediaOpened;
            _inlinePlayer.MediaEnded += OnInlineMediaEnded;
            _inlinePlayer.MediaFailed += OnInlineMediaFailed;
            _inlinePlayer.PlaybackSession.PlaybackStateChanged += OnInlineStateChanged;
            _inlineElement = new MediaPlayerElement
            {
                AreTransportControlsEnabled = false,
                AutoPlay = false,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                IsHitTestVisible = false
            };
            _inlineElement.SetMediaPlayer(_inlinePlayer);
            InlinePlayerHost.Children.Add(_inlineElement);
            _inlineSource = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(ClipFilePath)));
            _inlinePlayer.Source = _inlineSource;
            _inlineWantsPlay = true;
            _activeInlineCard = new WeakReference<ClipCard>(this);
            InlinePlayerHost.Visibility = Visibility.Visible;
            ThumbnailScrim.Visibility = Visibility.Collapsed;
            PlaceholderIcon.Visibility = Visibility.Collapsed;
            DurationBadge.Visibility = Visibility.Collapsed;
            _inlineTimer ??= CreateInlineTimer();
            _inlineTimer.Start();
            UpdateInlineUi();
            ShowInlineChrome();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Klipp] Inline preview failed: {ex.Message}");
            ShowInlineFailure();
        }
    }

    private DispatcherTimer CreateInlineTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => UpdateInlineUi();
        return timer;
    }

    private void OnInlineMediaOpened(MediaPlayer sender, object args) => DispatchInline(sender, () =>
    {
        // Restore history once for this source, never on a presentation change.
        if (_inlineMediaOpened) return;
        _inlineMediaOpened = true;
        sender.PlaybackSession.Position = TimeSpan.FromSeconds(PlaybackPositionHistory.Shared.GetPosition(
            _inlineMediaIdentity, sender.PlaybackSession.NaturalDuration.TotalSeconds));
        if (_inlineWantsPlay) sender.Play();
        UpdateInlineUi();
        ShowInlineChrome();
    });

    private void OnInlineMediaEnded(MediaPlayer sender, object args) => DispatchInline(sender, () =>
    {
        _inlineWantsPlay = false;
        _inlineTimer?.Stop();
        PlaybackPositionHistory.Shared.Remember(_inlineMediaIdentity, sender.PlaybackSession.NaturalDuration.TotalSeconds,
            sender.PlaybackSession.NaturalDuration.TotalSeconds);
        UpdateInlineUi();
    });

    private void OnInlineMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) =>
        DispatchInline(sender, ShowInlineFailure);

    private void OnInlineStateChanged(MediaPlaybackSession sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // Media events race disposal; only inspect the current player on its UI thread.
            if (_inlinePlayer is { } player && ReferenceEquals(sender, player.PlaybackSession))
                UpdateInlineUi();
        });
    }

    private void DispatchInline(MediaPlayer player, Action action) => DispatcherQueue.TryEnqueue(() =>
    {
        // An old media callback must not update a recycled card or its replacement player.
        if (ReferenceEquals(_inlinePlayer, player)) action();
    });

    private void UpdateInlineUi()
    {
        if (_inlinePlayer is null) return;
        var session = _inlinePlayer.PlaybackSession;
        var duration = session.NaturalDuration;
        if (_inlineMediaOpened && DateTime.UtcNow - _inlineLastPositionSaveUtc >= TimeSpan.FromSeconds(5))
            RememberInlinePosition();
        _syncingInlineUi = true;
        try
        {
            InlineSeekSlider.IsEnabled = duration > TimeSpan.Zero;
            InlineSeekSlider.Maximum = Math.Max(1, duration.TotalSeconds);
            if (!_inlineScrubbing)
                InlineSeekSlider.Value = Math.Clamp(session.Position.TotalSeconds, 0, InlineSeekSlider.Maximum);
            var position = _inlineScrubbing ? TimeSpan.FromSeconds(InlineSeekSlider.Value) : session.Position;
            InlineTimeText.Text = $"{FormatInlineTime(position)} / {FormatInlineTime(duration)}";
            InlinePlayIcon.Glyph = _inlineWantsPlay ? "\uE769" : "\uE768";
            AutomationProperties.SetName(InlinePlayButton, _inlineWantsPlay ? "Pause preview" : "Play preview");
            InlineMuteIcon.Glyph = _inlinePlayer.IsMuted ? "\uE74F" : "\uE995";
            AutomationProperties.SetName(InlineMuteButton, _inlinePlayer.IsMuted ? "Unmute preview" : "Mute preview");
        }
        finally { _syncingInlineUi = false; }
        UpdateInlineChrome();
    }

    private void UpdateInlineChrome()
    {
        var active = _inlinePlayer is not null;
        var keyboardFocus = XamlRoot is { } root &&
            FocusManager.GetFocusedElement(root) is Control { FocusState: FocusState.Keyboard } focused &&
            IsActionSource(focused);
        var playing = _inlinePlayer?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        var showControls = active && (!_inlineWantsPlay || !playing || _inlineScrubbing ||
            _inlineChromePointerDown || keyboardFocus ||
            _inlineChromeVisible && (_inlineFullscreen || _isPointerOver || _hasFocus));
        InlineControls.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        InlineControls.Opacity = showControls ? 1 : 0;
        InlineControls.IsHitTestVisible = showControls;
        InlineFullscreenHeader.Opacity = showControls ? 1 : 0;
        InlineFullscreenHeader.IsHitTestVisible = _inlineFullscreen && showControls;
        ThumbnailFrame.SetCursorHidden(_inlineFullscreen && active && !showControls);
        InlinePlayBadge.Visibility = !active && InlineErrorBorder.Visibility != Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowInlineChrome()
    {
        if (_inlinePlayer is null) return;
        _inlineChromeVisible = true;
        _inlineChromeTimer ??= CreateInlineChromeTimer();
        _inlineChromeTimer.Stop();
        _inlineChromeTimer.Start();
        UpdateInlineChrome();
    }

    private DispatcherTimer CreateInlineChromeTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_inlineScrubbing || _inlineChromePointerDown) return;
            _inlineChromeVisible = false;
            UpdateInlineChrome();
        };
        return timer;
    }

    private void OnInlineChromePointerMoved(object sender, PointerRoutedEventArgs e) => ShowInlineChrome();

    private void OnInlineChromePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _inlineChromePointerDown = true;
        ShowInlineChrome();
    }

    private void OnInlineChromePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _inlineChromePointerDown = false;
        ShowInlineChrome();
    }

    private static string FormatInlineTime(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
        : $"{Math.Max(0, (int)value.TotalMinutes)}:{Math.Max(0, value.Seconds):00}";

    private void OnInlinePlayClicked(object sender, RoutedEventArgs e)
    {
        _lastInlineTapUtc = DateTime.MinValue;
        ToggleInlinePlayback();
    }

    private void OnInlineMuteClicked(object sender, RoutedEventArgs e) => ToggleInlineMute();

    private void ToggleInlineMute()
    {
        if (_inlinePlayer is null) return;
        _inlineMuted = !_inlinePlayer.IsMuted;
        _inlinePlayer.IsMuted = _inlineMuted;
        UpdateInlineUi();
        ShowInlineChrome();
    }

    private void OnInlineExpandClicked(object sender, RoutedEventArgs e) => OpenInlineInPlayer();

    public bool HandleFullscreenPlaybackKey(VirtualKey key)
    {
        if (!_inlineFullscreen || _inlinePlayer is null) return false;
        return HandlePreviewPlaybackKey(key);
    }

    public bool HandleHostedPlaybackKey(VirtualKey key)
    {
        if (!_inlineRootHosted || _inlinePlayer is null || XamlRoot is not { } root ||
            FocusManager.GetFocusedElement(root) is not DependencyObject focused) return false;
        var inPreview = false;
        for (var current = focused; current is not null; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, this) || ReferenceEquals(current, ThumbnailFrame))
            {
                inPreview = true;
                break;
            }
        }
        return inPreview && HandlePreviewPlaybackKey(key);
    }

    private bool HandlePreviewPlaybackKey(VirtualKey key)
    {
        if (key is VirtualKey.Space or VirtualKey.Enter)
        {
            // Focused buttons and sliders retain their standard keyboard actions.
            if (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is DependencyObject focused &&
                IsActionSource(focused)) return false;
            _lastInlineTapUtc = DateTime.MinValue;
            ToggleInlinePlayback();
        }
        else if (key == VirtualKey.M) ToggleInlineMute();
        else if (key == VirtualKey.F) OpenInlineInPlayer();
        else return false;
        return true;
    }

    private void OpenInlineInPlayer()
    {
        if (_inlineFullscreen)
        {
            FullscreenExitRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (FullscreenRequested is not null)
        {
            FullscreenRequested.Invoke(this, EventArgs.Empty);
            return;
        }
        var player = _inlinePlayer;
        var args = new ClipCardClickEventArgs(ClipFilePath, player?.PlaybackSession.Position,
            player is null || _inlineWantsPlay, player?.IsMuted, player?.Volume);
        StopInlinePreview();
        Clicked?.Invoke(this, args);
    }

    private void OnCardTapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsActionSource(e.OriginalSource) || _inlineFullscreen) return;
        QueueInlineTap();
        e.Handled = true;
    }

    private void OnInlineVideoTapped(object sender, TappedRoutedEventArgs e)
    {
        if ((!_inlineFullscreen && !_inlineRootHosted) || IsActionSource(e.OriginalSource)) return;
        Focus(FocusState.Pointer);
        QueueInlineTap();
        e.Handled = true;
    }

    private async void QueueInlineTap()
    {
        var now = DateTime.UtcNow;
        if (now < _ignoreInlineTapUntilUtc) return;
        // Keep the intent from the first click, even when a slower double-click
        // arrives after its single-click feedback has already been applied.
        if ((now - _lastInlineTapUtc).TotalMilliseconds > 650)
            _inlineDoubleTapPlayIntent = _inlinePlayer is null || _inlineWantsPlay;
        _lastInlineTapUtc = now;
        CancelPendingInlineTap();
        var version = ++_inlineTapVersion;
        _pendingInlineTapCard = new WeakReference<ClipCard>(this);
        await Task.Delay(230);
        if (version == _inlineTapVersion && IsLoaded && DateTime.UtcNow >= _ignoreInlineTapUntilUtc)
        {
            _pendingInlineTapCard = null;
            ToggleInlinePlayback();
        }
    }

    private void OnInlineVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (IsActionSource(e.OriginalSource)) return;
        ++_inlineTapVersion;
        _ignoreInlineTapUntilUtc = DateTime.UtcNow.AddMilliseconds(350);
        if (_inlinePlayer is not null)
        {
            if (_inlineWantsPlay != _inlineDoubleTapPlayIntent)
            {
                _inlineWantsPlay = _inlineDoubleTapPlayIntent;
                if (_inlineWantsPlay && _inlineMediaOpened) _inlinePlayer.Play();
                else _inlinePlayer.Pause();
            }
        }
        OpenInlineInPlayer();
        e.Handled = true;
    }

    /// <summary>The library placeholder used to position the persistent playback view.</summary>
    public FrameworkElement PreviewAnchor => _inlinePlaceholder ?? (FrameworkElement)ThumbnailFrame;

    /// <summary>Hoists the view before allocating its decoder; toggles only resize this view.</summary>
    public FrameworkElement? HostPreviewView()
    {
        if (_inlineRootHosted) return ThumbnailFrame;
        if (_inlinePlayer is not null || ThumbnailFrame.Parent is not Grid parent) return null;
        _inlineThumbnailParent = parent;
        _inlineThumbnailIndex = parent.Children.IndexOf(ThumbnailFrame);
        _inlinePlaceholder = new Grid { Height = ThumbnailFrame.ActualHeight };
        _inlineRootHosted = true;
        parent.Children.Remove(ThumbnailFrame);
        parent.Children.Insert(_inlineThumbnailIndex, _inlinePlaceholder);
        return ThumbnailFrame;
    }

    /// <summary>Returns a stopped presentation to its card, after its decoder was disposed.</summary>
    public void RestoreHostedPreviewView()
    {
        if (!_inlineRootHosted) return;
        if (ThumbnailFrame.Parent is Panel host) host.Children.Remove(ThumbnailFrame);
        ThumbnailFrame.Width = double.NaN;
        ThumbnailFrame.Margin = new Thickness(0);
        if (_inlineThumbnailParent is { } parent && _inlinePlaceholder is { } placeholder)
        {
            ThumbnailFrame.Height = placeholder.Height;
            parent.Children.Remove(placeholder);
            parent.Children.Insert(Math.Min(_inlineThumbnailIndex, parent.Children.Count), ThumbnailFrame);
        }
        _inlineRootHosted = false;
        _inlinePlaceholder = null;
        _inlineThumbnailParent = null;
    }

    public FrameworkElement? BeginFullscreenPreview()
    {
        if (_inlineFullscreen) return ThumbnailFrame;
        if (_inlinePlayer is null) StartInlinePreview();
        if (_inlinePlayer is null || !_inlineRootHosted) return null;
        _inlineTapVersion++;
        _inlineFullscreen = true;
        // The host lives outside the card, so don't retain its previous press state.
        _pressedCardBody = false;
        _inlineChromePointerDown = false;
        UpdateInteractionChrome();
        ThumbnailFrame.Height = double.NaN;
        ThumbnailFrame.CornerRadius = new CornerRadius(0);
        InlineFullscreenTitle.Text = ClipTitle;
        InlineFullscreenHeader.Visibility = Visibility.Visible;
        _inlineNewBadgeVisibility = NewBadge.Visibility;
        _inlineFavoriteBadgeVisibility = FavoriteBadge.Visibility;
        NewBadge.Visibility = FavoriteBadge.Visibility = Visibility.Collapsed;
        SetFullscreenTransportSize(true);
        UpdateInlineUi();
        return ThumbnailFrame;
    }

    public void RestoreFullscreenPreview(bool resumePlayback = true)
    {
        if (!_inlineFullscreen) return;
        _inlineReturning = true;
        _inlineFullscreen = false;
        _pressedCardBody = false;
        _inlineChromePointerDown = false;
        ThumbnailFrame.CornerRadius = new CornerRadius(5, 5, 0, 0);
        InlineFullscreenHeader.Visibility = Visibility.Collapsed;
        NewBadge.Visibility = _inlineNewBadgeVisibility;
        FavoriteBadge.Visibility = _inlineFavoriteBadgeVisibility;
        SetFullscreenTransportSize(false);
        UpdateInteractionChrome();
        if (resumePlayback) ResumePreviewView();
        else UpdateInlineUi();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _inlineReturning = false);
    }

    public void ResumePreviewView()
    {
        // The same source, decoder, presentation and clock stay live in both
        // sizes. A view change must not Play, Pause or seek the media session.
        UpdateInlineUi();
        ShowInlineChrome();
        if (IsLoaded && XamlRoot is not null) Focus(FocusState.Programmatic);
    }

    private void SetFullscreenTransportSize(bool fullscreen)
    {
        var size = fullscreen ? 40 : 28;
        InlineControls.Padding = fullscreen ? new Thickness(16, 0, 16, 12) : new Thickness(4, 0, 4, 4);
        InlineControls.RowDefinitions[0].Height = new GridLength(fullscreen ? 36 : 24);
        InlineControls.RowDefinitions[1].Height = new GridLength(size);
        InlineSeekSlider.Height = InlineSeekSlider.MinHeight = fullscreen ? 36 : 24;
        InlineTimeText.FontSize = fullscreen ? 13 : 10;
        foreach (var index in new[] { 0, 2, 3 }) InlineButtonBar.ColumnDefinitions[index].Width = new GridLength(size);
        foreach (var button in new[] { InlinePlayButton, InlineMuteButton, InlineExpandButton })
            button.Width = button.Height = size;
        InlineExpandIcon.Glyph = fullscreen ? "\uE73F" : "\uE740";
        AutomationProperties.SetName(InlineExpandButton, fullscreen ? "Return to thumbnail" : "Play fullscreen");
        ToolTipService.SetToolTip(InlineExpandButton, fullscreen ? "Return to thumbnail (Escape)" : "Fullscreen (double-click or F)");
    }

    private void OnInlineSeekPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_inlinePlayer is null || _inlineScrubbing) return;
        _inlineScrubbing = true;
        _inlineResumeAfterScrub = _inlineWantsPlay;
        _inlinePlayer.Pause();
        _inlineTimer?.Stop();
    }

    private void OnInlineSeekReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_inlineScrubbing) return;
        _inlineScrubbing = false;
        if (_inlinePlayer is not null && _inlineResumeAfterScrub)
        {
            _inlinePlayer.Play();
            _inlineTimer?.Start();
        }
        _inlineResumeAfterScrub = false;
        UpdateInlineUi();
        RememberInlinePosition();
        ShowInlineChrome();
    }

    private void OnInlineSeekChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingInlineUi || _inlinePlayer is null) return;
        var session = _inlinePlayer.PlaybackSession;
        if (session.NaturalDuration <= TimeSpan.Zero) return;
        session.Position = TimeSpan.FromSeconds(Math.Clamp(e.NewValue, 0, session.NaturalDuration.TotalSeconds));
        UpdateInlineUi();
    }

    private void ShowInlineFailure()
    {
        StopInlinePreview();
        InlineErrorBorder.Visibility = Visibility.Visible;
        InlinePlayBadge.Visibility = Visibility.Collapsed;
    }

    private void RememberInlinePosition()
    {
        if (!_inlineMediaOpened || _inlinePlayer is null) return;
        var session = _inlinePlayer.PlaybackSession;
        PlaybackPositionHistory.Shared.Remember(_inlineMediaIdentity, session.Position.TotalSeconds,
            session.NaturalDuration.TotalSeconds);
        _inlineLastPositionSaveUtc = DateTime.UtcNow;
    }

    private void StopInlinePreview()
    {
        if (_pendingInlineTapCard?.TryGetTarget(out var pending) == true && ReferenceEquals(pending, this))
            _pendingInlineTapCard = null;
        ++_inlineTapVersion;
        if (_inlineFullscreen)
        {
            FullscreenExitRequested?.Invoke(this, EventArgs.Empty);
            if (_inlineFullscreen) RestoreFullscreenPreview();
        }
        RememberInlinePosition();
        var player = _inlinePlayer;
        _inlinePlayer = null;
        _inlineMediaOpened = false;
        _inlineMediaIdentity = null;
        _inlineReturning = false;
        _inlineWantsPlay = false;
        _inlineScrubbing = false;
        _inlineResumeAfterScrub = false;
        _inlineTimer?.Stop();
        _inlineChromeTimer?.Stop();
        _inlineChromeVisible = true;
        _inlineChromePointerDown = false;
        _pressedCardBody = false;
        if (_activeInlineCard?.TryGetTarget(out var card) == true && ReferenceEquals(card, this))
            _activeInlineCard = null;
        if (player is not null)
        {
            player.MediaOpened -= OnInlineMediaOpened;
            player.MediaEnded -= OnInlineMediaEnded;
            player.MediaFailed -= OnInlineMediaFailed;
            player.PlaybackSession.PlaybackStateChanged -= OnInlineStateChanged;
            _inlineMuted = player.IsMuted;
            _inlineVolume = player.Volume;
            player.Pause();
            _inlineElement?.SetMediaPlayer(null);
            player.Source = null;
            player.Dispose();
        }
        _inlineSource?.Dispose();
        _inlineSource = null;
        _inlineElement = null;
        InlinePlayerHost.Children.Clear();
        InlinePlayerHost.Visibility = Visibility.Collapsed;
        InlineErrorBorder.Visibility = Visibility.Collapsed;
        ThumbnailScrim.Visibility = Visibility.Visible;
        DurationBadge.Visibility = Visibility.Visible;
        PlaceholderIcon.Visibility = ThumbnailImage.Source is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateInteractionChrome();
        if (player is not null || _inlineRootHosted) PreviewStopped?.Invoke(this, EventArgs.Empty);
        // A standalone card may not have an owner to return its presentation.
        RestoreHostedPreviewView();
    }
}
