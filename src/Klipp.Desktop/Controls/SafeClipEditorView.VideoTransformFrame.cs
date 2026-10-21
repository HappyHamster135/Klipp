using System;
using System.Linq;
using Klipp.Core.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private void UpdateVideoTransformFrame()
    {
        if (VideoTransformFrame is null || PreviewMediaHost is null)
            return;

        if (_activeTool != "general")
        {
            VideoTransformFrame.Visibility = Visibility.Collapsed;
            return;
        }

        // Match the selected visible clip, falling back to the currently visible
        // video when the selection is audio or the playhead has left that clip.
        var candidates = new[]
        {
            LayerTimeline.GetSelectedMediaClip(),
            GetActiveOverlayPreviewClip(),
            GetActivePreviewClip(),
            GetActiveTransitionPreviewClip()
        }.Concat(_additionalVideoRows.Values.SelectMany(row => new[] { row.Incoming.Clip, row.Outgoing?.Clip }));
        foreach (var clip in candidates)
        {
            if (!TryGetClipPreviewMap(clip, out var host, out var fit))
                continue;

            var source = EditorVideoSelectionMath.GetSourceBounds(fit);
            Rect bounds;
            try
            {
                // The frame shares the global video transform. Map only the
                // source fit and the clip's zoom/pan into that parent's space.
                bounds = host.TransformToVisual(PreviewMediaHost).TransformBounds(
                    new Rect(source.Left, source.Top, source.Width, source.Height));
            }
            catch (Exception) { continue; } // A decoder can be replaced at a cut.
            if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y) ||
                !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height) ||
                bounds.Width <= 0 || bounds.Height <= 0)
                continue;

            if (VideoTransformFrame.Width != bounds.Width) VideoTransformFrame.Width = bounds.Width;
            if (VideoTransformFrame.Height != bounds.Height) VideoTransformFrame.Height = bounds.Height;
            var margin = new Thickness(bounds.X, bounds.Y, 0, 0);
            if (VideoTransformFrame.Margin != margin) VideoTransformFrame.Margin = margin;

            // Keep the grips usable at every canvas zoom and video scale.
            var screenScale = Math.Max(.01, Math.Abs(PreviewStageTransform.ScaleX * _videoScale));
            var handleSize = 9 / screenScale;
            foreach (var handle in VideoTransformFrame.Children.OfType<Border>().Where(border => border.Tag is string))
            {
                if (handle.Width != handleSize) handle.Width = handleSize;
                if (handle.Height != handleSize) handle.Height = handleSize;
                var stroke = new Thickness(1 / screenScale);
                if (handle.BorderThickness != stroke) handle.BorderThickness = stroke;
            }
            if (VideoTransformOutline.BorderThickness.Left != 1 / screenScale)
                VideoTransformOutline.BorderThickness = new Thickness(1 / screenScale);
            VideoTransformFrame.Visibility = Visibility.Visible;
            return;
        }
        VideoTransformFrame.Visibility = Visibility.Collapsed;
    }
}
