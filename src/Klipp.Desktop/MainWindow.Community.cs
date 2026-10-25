using System;
using System.Threading.Tasks;
using Klipp.Desktop.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Foundation;

namespace Klipp.Desktop;

public sealed partial class MainWindow
{
    private CommunityView? _communityView;
    private IDisposable? _communityUpdateWork;
    private bool _communityFullscreen;
    private AppWindowPresenter? _communityPreviousPresenter;
    private Visibility _communityPreviousTopBar;
    private Visibility _communityPreviousNavigation;
    private FrameworkElement? _communityPlaybackSurface;
    private Grid? _communityPlaybackAnchor;

    private CommunityView CommunityView
    {
        get
        {
            if (_communityView is not null) return _communityView;
            var view = new CommunityView();
            view.LibraryRequested += (_, _) => ShowLibrary();
            view.UploadRequested += async (_, _) => await BeginCommunityUploadAsync();
            view.AccountChanged += (_, _) => CommunityAccountLabel.Text = view.AccountLabel;
            view.FullscreenRequested += OnCommunityFullscreenRequested;
            view.PlaybackHostChanged += OnCommunityPlaybackHostChanged;
            view.PlaybackLayoutChanged += OnCommunityPlaybackLayoutChanged;
            view.BusyChanged += (_, _) =>
            {
                if (view.IsBusy)
                    _communityUpdateWork ??= BeginUpdateBlockingOperation();
                else
                {
                    _communityUpdateWork?.Dispose();
                    _communityUpdateWork = null;
                }
            };
            _communityView = view;
            MediaHost.Children.Add(view);
            return view;
        }
    }

    private void OnLibraryNavigationClicked(object sender, RoutedEventArgs e) => ShowLibrary();
    private async void OnMediaNavigationClicked(object sender, RoutedEventArgs e) => await ShowMediaAsync();
    private async void OnCommunityAccountClicked(object sender, RoutedEventArgs e) => await ShowMediaAsync(showProfile: true);

    private async Task ShowMediaAsync(bool showProfile = false)
    {
        if (_updateRestarting || _captureWindowClosing || _trayExitPending) return;
        ClipCard.StopActivePreview();
        HideSideMenu();
        SetPlayerFullscreen(false);
        StopPlayerIfCreated();
        StopEditorIfCreated();
        TopBar.Visibility = Visibility.Visible;
        SetViewVisibility(LibraryView, false);
        SetViewVisibility(PlayerHost, false);
        SetViewVisibility(EditorHost, false);
        SetViewVisibility(MediaHost, true);
        UpdateWorkspaceNavigation(media: true);
        RefreshUpdateUi();
        await CommunityView.ActivateAsync(showProfile);
    }

    private void LeaveMedia()
    {
        _communityView?.Deactivate();
        SetViewVisibility(MediaHost, false);
    }

    private void UpdateWorkspaceNavigation(bool media, bool visible = true)
    {
        WorkspaceNavigation.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        var selected = (Brush)Application.Current.Resources["KlippSurfaceElevatedBrush"];
        var border = (Brush)Application.Current.Resources["KlippBorderBrush"];
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        LibraryNavigationButton.Background = media ? transparent : selected;
        LibraryNavigationButton.BorderBrush = media ? transparent : border;
        MediaNavigationButton.Background = media ? selected : transparent;
        MediaNavigationButton.BorderBrush = media ? border : transparent;
    }

    private void DisposeCommunity()
    {
        _communityPreparation?.Cancel();
        _communityView?.Dispose();
        _communityUpdateWork?.Dispose();
        _communityUpdateWork = null;
    }

    private void OnCommunityPlaybackHostChanged(object? sender, CommunityPlaybackHostEventArgs args)
    {
        if (args.Attach)
        {
            if (ReferenceEquals(_communityPlaybackSurface, args.Surface))
            {
                args.Handled = true;
                return;
            }
            if (_hostedInlineCard is not null || _communityPlaybackSurface is not null || InlinePreviewCanvas.Children.Count != 0)
                throw new InvalidOperationException("Another video still owns the playback surface.");
            // Attach before SetMediaPlayer. Fullscreen will only resize this
            // persistent surface; it never reparents a running video element.
            args.Anchor.Children.Remove(args.Surface);
            try
            {
                InlinePreviewCanvas.Children.Add(args.Surface);
                _communityPlaybackAnchor = args.Anchor;
                _communityPlaybackSurface = args.Surface;
                InlinePreviewCanvas.Clip = _inlinePreviewClip;
                InlineFullscreenHost.Visibility = Visibility.Visible;
                AppRoot.LayoutUpdated += OnCommunityPlaybackLayoutChanged;
                args.Anchor.UpdateLayout();
                UpdateCommunityPlaybackBounds();
                args.Handled = true;
            }
            catch
            {
                AppRoot.LayoutUpdated -= OnCommunityPlaybackLayoutChanged;
                InlinePreviewCanvas.Children.Remove(args.Surface);
                _communityPlaybackAnchor = null;
                _communityPlaybackSurface = null;
                InlineFullscreenHost.Visibility = Visibility.Collapsed;
                args.Anchor.Children.Add(args.Surface);
                throw;
            }
        }
        else if (ReferenceEquals(_communityPlaybackSurface, args.Surface))
        {
            AppRoot.LayoutUpdated -= OnCommunityPlaybackLayoutChanged;
            _communityPlaybackAnchor = null;
            _communityPlaybackSurface = null;
            InlinePreviewCanvas.Children.Remove(args.Surface);
            InlineFullscreenHost.Visibility = Visibility.Collapsed;
            args.Surface.Width = double.NaN;
            args.Surface.Height = double.NaN;
            args.Anchor.Children.Add(args.Surface);
            args.Handled = true;
        }
        RefreshUpdateUi();
    }

    private void OnCommunityPlaybackLayoutChanged(object? sender, object args) => UpdateCommunityPlaybackBounds();

    private void UpdateCommunityPlaybackBounds()
    {
        if (_communityPlaybackAnchor is not { } anchor || _communityPlaybackSurface is not { } surface) return;
        try
        {
            var width = InlinePreviewCanvas.ActualWidth;
            var height = InlinePreviewCanvas.ActualHeight;
            if (width <= 0 || height <= 0) return;
            Rect bounds, clip;
            if (_communityFullscreen)
                bounds = clip = new Rect(0, 0, width, height);
            else
            {
                var origin = anchor.TransformToVisual(InlinePreviewCanvas).TransformPoint(new Point(0, 0));
                bounds = new Rect(origin.X, origin.Y, anchor.ActualWidth, anchor.ActualHeight);
                var viewport = MediaHost.TransformToVisual(InlinePreviewCanvas).TransformPoint(new Point(0, 0));
                var left = Math.Max(0, viewport.X);
                var top = Math.Max(0, viewport.Y);
                clip = new Rect(left, top, Math.Max(0, Math.Min(width, viewport.X + MediaHost.ActualWidth) - left),
                    Math.Max(0, Math.Min(height, viewport.Y + MediaHost.ActualHeight) - top));
            }
            if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || bounds.Width <= 0 || bounds.Height <= 0) return;
            if (!Near(Canvas.GetLeft(surface), bounds.X)) Canvas.SetLeft(surface, bounds.X);
            if (!Near(Canvas.GetTop(surface), bounds.Y)) Canvas.SetTop(surface, bounds.Y);
            if (!Near(surface.Width, bounds.Width)) surface.Width = bounds.Width;
            if (!Near(surface.Height, bounds.Height)) surface.Height = bounds.Height;
            if (_inlinePreviewClip.Rect != clip) _inlinePreviewClip.Rect = clip;
            var background = _communityFullscreen ? _inlineFullscreenBackground : null;
            if (!ReferenceEquals(InlineFullscreenHost.Background, background)) InlineFullscreenHost.Background = background;
            var layer = _communityFullscreen ? 30 : 10;
            if (Canvas.GetZIndex(InlineFullscreenHost) != layer) Canvas.SetZIndex(InlineFullscreenHost, layer);
        }
        catch (ArgumentException) { /* Navigation can remove an anchor in the same layout pass. */ }
        static bool Near(double a, double b) => double.IsFinite(a) && Math.Abs(a - b) < 0.01;
    }

    private void OnCommunityFullscreenRequested(object? sender, PlayerFullscreenRequestEventArgs args)
    {
        var fullscreen = args.IsFullscreen == true;
        if (_communityFullscreen == fullscreen) return;
        if (fullscreen)
        {
            if (_appWindow is null)
            {
                _communityView?.ExitFullscreen();
                return;
            }
            try
            {
                _communityPreviousPresenter = _appWindow.Presenter;
                _communityPreviousTopBar = TopBar.Visibility;
                _communityPreviousNavigation = WorkspaceNavigation.Visibility;
                _appWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
                _communityFullscreen = true;
                HideSideMenu();
                TopBar.Visibility = Visibility.Collapsed;
                WorkspaceNavigation.Visibility = Visibility.Collapsed;
            }
            catch (Exception error)
            {
                System.Diagnostics.Debug.WriteLine($"[Klipp] Could not enter Media fullscreen: {error.GetType().Name}");
                if (!_communityFullscreen) _communityPreviousPresenter = null;
                _communityView?.ExitFullscreen();
            }
        }
        else
        {
            _communityFullscreen = false;
            var previous = _communityPreviousPresenter;
            _communityPreviousPresenter = null;
            try
            {
                if (_appWindow is not null)
                {
                    if (previous is not null) _appWindow.SetPresenter(previous);
                    else _appWindow.SetPresenter(AppWindowPresenterKind.Default);
                }
            }
            catch (Exception error)
            {
                System.Diagnostics.Debug.WriteLine($"[Klipp] Could not restore Media window: {error.GetType().Name}");
            }
            finally
            {
                TopBar.Visibility = _communityPreviousTopBar;
                WorkspaceNavigation.Visibility = _communityPreviousNavigation;
            }
        }
        UpdateCommunityPlaybackBounds();
        RefreshUpdateUi();
    }
}
