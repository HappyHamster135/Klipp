using System;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Media.Playback;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private bool TryGetTimelineFxPreviewMap(EditorProjectEffectLayer effect, out Grid host, out EditorSourceFit fit)
        => TryGetClipPreviewMap(LayerTimeline.FindMediaClipById(effect.TargetClipId), out host, out fit);

    private bool TryGetClipPreviewMap(EditorProjectClip? clip, out Grid host, out EditorSourceFit fit)
    {
        host = null!;
        fit = default;
        var position = CurrentTimelinePosition.TotalSeconds;
        if (clip is null || PreviewSurface is null || _project?.IsTrackActive(clip.TrackId) != true ||
            position < clip.TimelineStartSeconds || position >= clip.TimelineStartSeconds + clip.EffectiveDurationSeconds)
            return false;

        MediaPlayer? player = null;
        if (GetActivePreviewClip()?.Id == clip.Id)
        {
            if (_isPreviewOpening) return false;
            host = PrimaryPreviewHost;
            player = NativePreviewPlayer.MediaPlayer;
        }
        else if (GetActiveTransitionPreviewClip()?.Id == clip.Id)
        {
            if (_isTransitionPreviewOpening) return false;
            host = TransitionPreviewHost;
            player = TransitionPreviewPlayer.MediaPlayer;
        }
        else if (GetActiveOverlayPreviewClip()?.Id == clip.Id)
        {
            if (_overlayPreviewOpening) return false;
            host = OverlayPreviewHost;
            player = OverlayPreviewPlayer.MediaPlayer;
        }
        else if (_highestOverlayOutgoing is { } highest && highest.Clip?.Id == clip.Id)
        {
            if (highest.Player.PlaybackSession.PlaybackState is MediaPlaybackState.None or MediaPlaybackState.Opening) return false;
            host = highest.Host;
            player = highest.Player;
        }
        else
        {
            foreach (var row in _additionalVideoRows.Values)
            {
                var preview = row.Incoming.Clip?.Id == clip.Id ? row.Incoming
                    : row.Outgoing?.Clip?.Id == clip.Id ? row.Outgoing : null;
                if (preview is null) continue;
                if (preview.Player.PlaybackSession.PlaybackState is MediaPlaybackState.None or MediaPlaybackState.Opening) return false;
                host = preview.Host;
                player = preview.Player;
                break;
            }
        }
        if (host is null || !IsTimelineFxPreviewHostVisible(host)) return false;

        var sourceWidth = (double)(player?.PlaybackSession.NaturalVideoWidth ?? 0);
        var sourceHeight = (double)(player?.PlaybackSession.NaturalVideoHeight ?? 0);
        if ((sourceWidth <= 0 || sourceHeight <= 0) && ReferenceEquals(host, PrimaryPreviewHost) &&
            PreviewImage.Opacity > 0 && PreviewImage.Source is BitmapSource image)
        {
            sourceWidth = image.PixelWidth;
            sourceHeight = image.PixelHeight;
        }
        // Unknown dimensions must not silently stretch a portrait source to the
        // project canvas. MediaOpened/the loaded image will supply the real fit.
        if (sourceWidth <= 0 || sourceHeight <= 0) return false;
        var width = PreviewSurface.ActualWidth > 0 ? PreviewSurface.ActualWidth : PreviewSurface.Width;
        var height = PreviewSurface.ActualHeight > 0 ? PreviewSurface.ActualHeight : PreviewSurface.Height;
        if (width <= 0 || height <= 0) return false;
        fit = EditorSourceFitMath.Calculate(width, height, sourceWidth, sourceHeight,
            clip.CropLeft, clip.CropTop, clip.CropRight, clip.CropBottom);
        return true;
    }

    private bool IsTimelineFxPreviewHostVisible(Grid host)
    {
        DependencyObject? current = host;
        while (current is not null && !ReferenceEquals(current, PreviewSurface))
        {
            if (current is UIElement element && (element.Visibility != Visibility.Visible || element.Opacity <= .0001))
                return false;
            current = VisualTreeHelper.GetParent(current);
        }
        return ReferenceEquals(current, PreviewSurface);
    }

    private bool SourceNormalizedToPreview(EditorProjectEffectLayer effect, double x, double y, out Point point)
    {
        point = default;
        if (!TryGetTimelineFxPreviewMap(effect, out var host, out var fit)) return false;
        var local = EditorEffectRegionMath.SourceToHost(fit, new(x, y));
        try
        {
            point = host.TransformToVisual(PreviewSurface).TransformPoint(new Point(local.X, local.Y));
            return double.IsFinite(point.X) && double.IsFinite(point.Y);
        }
        catch (Exception) { return false; } // The decoder's visual may be removed during a cut.
    }

    private bool PreviewToSourceNormalized(EditorProjectEffectLayer effect, Point point, out EditorEffectPoint sourcePoint)
    {
        sourcePoint = default;
        if (!TryGetTimelineFxPreviewMap(effect, out var host, out var fit)) return false;
        try
        {
            var local = PreviewSurface.TransformToVisual(host).TransformPoint(point);
            sourcePoint = EditorEffectRegionMath.HostToSource(fit, new(local.X, local.Y));
            return double.IsFinite(sourcePoint.X) && double.IsFinite(sourcePoint.Y);
        }
        catch (Exception) { return false; }
    }
}
