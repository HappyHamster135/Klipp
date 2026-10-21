using System;
using System.Runtime.InteropServices;
using Klipp.Desktop.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Klipp.Desktop;

public sealed partial class MainWindow
{
    private ClipCard? _hostedInlineCard;
    private FrameworkElement? _hostedInlineFrame;
    private ClipCard? _fullscreenInlineCard;
    private bool _returningInlineFullscreen;
    private bool _inlinePreviousFullscreen;
    private bool _inlineLibraryRefreshPending;
    private double _inlineReturnScrollOffset;
    private bool _inlineWindowClosing;
    private readonly SolidColorBrush _inlineFullscreenBackground = new(Microsoft.UI.Colors.Black);
    private readonly RectangleGeometry _inlinePreviewClip = new();

    private void OnInlineFullscreenPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        // The detached transport still belongs to its card. Fullscreen shortcuts
        // also work if focus returns to a library field while chrome is hidden.
        if (_fullscreenInlineCard?.HandleFullscreenPlaybackKey(args.Key) == true ||
            _fullscreenInlineCard is null && _hostedInlineCard?.HandleHostedPlaybackKey(args.Key) == true)
            args.Handled = true;
    }

    private void OnClipCardPreviewStarting(object sender, EventArgs args)
    {
        if (sender is not ClipCard card || _inlineWindowClosing) return;
        if (_hostedInlineCard is not null && !ReferenceEquals(_hostedInlineCard, card))
            throw new InvalidOperationException("Another preview is still attached.");
        var frame = card.HostPreviewView();
        if (frame is null) throw new InvalidOperationException("The preview card is no longer available.");
        _hostedInlineCard = card;
        _hostedInlineFrame = frame;
        try
        {
            // Attach before MediaPlayerElement is created. This parent stays the
            // same until playback stops, including every fullscreen round trip.
            InlinePreviewCanvas.Children.Add(frame);
            InlineFullscreenHost.Visibility = Visibility.Visible;
            InlinePreviewCanvas.Clip = _inlinePreviewClip;
            AppRoot.LayoutUpdated += OnInlinePreviewLayoutUpdated;
            ClipsScroller.ViewChanged += OnInlinePreviewScrolled;
            card.PreviewAnchor.UpdateLayout();
            UpdateInlinePreviewBounds();
        }
        catch
        {
            ReleaseInlinePreviewHost(card);
            throw;
        }
    }

    private void OnInlinePreviewLayoutUpdated(object? sender, object args) => UpdateInlinePreviewBounds();

    private void OnInlinePreviewScrolled(object? sender, ScrollViewerViewChangedEventArgs args) => UpdateInlinePreviewBounds();

    private void OnInlinePreviewPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (args.Handled || _hostedInlineCard is null || _fullscreenInlineCard is not null ||
            (args.KeyModifiers & (Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift)) != 0) return;
        var properties = args.GetCurrentPoint(InlineFullscreenHost).Properties;
        if (properties.IsHorizontalMouseWheel || properties.MouseWheelDelta == 0) return;
        // The video no longer descends from the scroller. Keep its unhandled
        // wheel gestures scrolling the library, using the Windows preference.
        if (!GetInlineWheelScrollLines(0x0068, 0, out var lines, 0)) lines = 3;
        if (lines == 0) return;
        var distance = lines == uint.MaxValue ? Math.Max(16, ClipsScroller.ViewportHeight) : lines * 16d;
        var offset = Math.Clamp(ClipsScroller.VerticalOffset - properties.MouseWheelDelta / 120d * distance,
            0, ClipsScroller.ScrollableHeight);
        args.Handled = ClipsScroller.ChangeView(null, offset, null, disableAnimation: true);
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetInlineWheelScrollLines(uint action, uint parameter, out uint lines, uint flags);

    private void UpdateInlinePreviewBounds()
    {
        if (_inlineWindowClosing || _hostedInlineCard is not { } card || _hostedInlineFrame is not { } frame) return;
        try
        {
            var fullscreen = ReferenceEquals(_fullscreenInlineCard, card);
            var width = InlinePreviewCanvas.ActualWidth;
            var height = InlinePreviewCanvas.ActualHeight;
            if (width <= 0 || height <= 0) return;
            Rect bounds;
            Rect clip;
            if (fullscreen)
            {
                bounds = clip = new Rect(0, 0, width, height);
            }
            else
            {
                var anchor = card.PreviewAnchor;
                var origin = anchor.TransformToVisual(InlinePreviewCanvas).TransformPoint(new Point(0, 0));
                bounds = new Rect(origin.X, origin.Y, anchor.ActualWidth, anchor.ActualHeight);
                var viewport = ClipsScroller.TransformToVisual(InlinePreviewCanvas).TransformPoint(new Point(0, 0));
                var left = Math.Max(0, viewport.X);
                var top = Math.Max(0, viewport.Y);
                clip = new Rect(left, top, Math.Max(0, Math.Min(width, viewport.X + ClipsScroller.ActualWidth) - left),
                    Math.Max(0, Math.Min(height, viewport.Y + ClipsScroller.ActualHeight) - top));
            }
            if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) || bounds.Width <= 0 || bounds.Height <= 0) return;
            // Scroll/layout events can repeat without moving the video. Avoid
            // invalidating measure or touching the decoder in those cases.
            if (!Near(Canvas.GetLeft(frame), bounds.X)) Canvas.SetLeft(frame, bounds.X);
            if (!Near(Canvas.GetTop(frame), bounds.Y)) Canvas.SetTop(frame, bounds.Y);
            if (!Near(frame.Width, bounds.Width)) frame.Width = bounds.Width;
            if (!Near(frame.Height, bounds.Height)) frame.Height = bounds.Height;
            if (_inlinePreviewClip.Rect != clip) _inlinePreviewClip.Rect = clip;
            var background = fullscreen ? _inlineFullscreenBackground : null;
            if (!ReferenceEquals(InlineFullscreenHost.Background, background)) InlineFullscreenHost.Background = background;
            var layer = fullscreen ? 30 : 10;
            if (Canvas.GetZIndex(InlineFullscreenHost) != layer) Canvas.SetZIndex(InlineFullscreenHost, layer);
        }
        catch (ArgumentException)
        {
            // The repeater may remove an anchor in the same layout pass that
            // stops it. Its stop callback releases the presentation below.
        }
        static bool Near(double a, double b) => double.IsFinite(a) && Math.Abs(a - b) < 0.01;
    }

    private void ReleaseInlinePreviewHost(ClipCard card)
    {
        if (!ReferenceEquals(_hostedInlineCard, card)) return;
        AppRoot.LayoutUpdated -= OnInlinePreviewLayoutUpdated;
        ClipsScroller.ViewChanged -= OnInlinePreviewScrolled;
        _hostedInlineCard = null;
        _hostedInlineFrame = null;
        InlinePreviewCanvas.Children.Clear();
        InlineFullscreenHost.Visibility = Visibility.Collapsed;
        card.RestoreHostedPreviewView();
    }

    private void OnClipCardFullscreenRequested(object sender, EventArgs args)
    {
        if (sender is not ClipCard card || _returningInlineFullscreen) return;
        if (_fullscreenInlineCard is not null)
        {
            ExitInlineFullscreen();
            return;
        }
        HideSideMenu();
        _inlinePreviousFullscreen = _playerFullscreen;
        _inlineReturnScrollOffset = ClipsScroller.VerticalOffset;
        // Keep the owning card realized while the window grows. Its video is
        // already in the persistent host; only the bounds change below.
        LibraryView.Width = LibraryView.ActualWidth;
        LibraryView.Height = LibraryView.ActualHeight;
        LibraryView.HorizontalAlignment = HorizontalAlignment.Left;
        LibraryView.VerticalAlignment = VerticalAlignment.Top;
        _fullscreenInlineCard = card;
        try
        {
            if (card.BeginFullscreenPreview() is null)
            {
                ExitInlineFullscreen();
                return;
            }
            SetPlayerFullscreen(true);
            UpdateInlinePreviewBounds();
            card.ResumePreviewView();
        }
        catch (Exception ex)
        {
            ExitInlineFullscreen();
            System.Diagnostics.Debug.WriteLine($"[Klipp] Could not open inline fullscreen: {ex.Message}");
        }
    }

    private void OnClipCardFullscreenExitRequested(object sender, EventArgs args)
    {
        if (ReferenceEquals(sender, _fullscreenInlineCard)) ExitInlineFullscreen();
    }

    private void ExitInlineFullscreen(bool restoreWindow = true)
    {
        if (_returningInlineFullscreen || _fullscreenInlineCard is not { } card) return;
        _returningInlineFullscreen = true;
        try
        {
            _fullscreenInlineCard = null;
            if (restoreWindow) SetPlayerFullscreen(_inlinePreviousFullscreen);
            LibraryView.Width = LibraryView.Height = double.NaN;
            LibraryView.HorizontalAlignment = HorizontalAlignment.Stretch;
            LibraryView.VerticalAlignment = VerticalAlignment.Stretch;
            card.RestoreFullscreenPreview(resumePlayback: restoreWindow);
            if (restoreWindow) ClipsScroller.ChangeView(null, _inlineReturnScrollOffset, null, disableAnimation: true);
            UpdateInlinePreviewBounds();
        }
        finally
        {
            _returningInlineFullscreen = false;
        }
    }

    private void OnClipCardPreviewStopped(object sender, EventArgs args)
    {
        if (sender is ClipCard card) ReleaseInlinePreviewHost(card);
        if (!_inlineLibraryRefreshPending || _inlineWindowClosing) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_inlineWindowClosing || _fullscreenInlineCard is not null || ClipCard.HasActivePreview) return;
            _inlineLibraryRefreshPending = false;
            ApplyLibraryFilter();
        });
    }
}
