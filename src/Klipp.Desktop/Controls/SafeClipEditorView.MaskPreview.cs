using System;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private sealed class MaskPreviewResources
    {
        public int Signature;
        public CanvasGeometry? Geometry;
        public CompositionPath? Path;
        public CompositionPathGeometry? Shape;
        public CompositionGeometricClip? Clip;

        public void Clear()
        {
            Clip?.Dispose(); Shape?.Dispose(); Geometry?.Dispose();
            Clip = null; Shape = null; Path = null; Geometry = null;
        }
    }

    private static readonly ConditionalWeakTable<Grid, MaskPreviewResources> MaskPreviewCache = new();
    private sealed class SourceCropPreviewState
    {
        public double Width;
        public double Height;
        public Thickness Margin;
        public EditorVideoSelectionBounds ImageBounds;
    }
    private static readonly ConditionalWeakTable<Grid, SourceCropPreviewState> SourceCropPreviewCache = new();

    private static bool ApplySourcePolygonOrInvertedMask(Grid host, EditorProjectClip? clip, double width, double height)
    {
        var resources = MaskPreviewCache.GetValue(host, static _ => new MaskPreviewResources());
        var visual = ElementCompositionPreview.GetElementVisual(host);
        if (clip is null || clip.MaskShape == "none" || (clip.MaskShape != "polygon" && !clip.MaskInverted))
        {
            if (resources.Clip is not null) visual.Clip = null;
            resources.Clear();
            return false;
        }

        // Polygon vertices are in the original source frame. Map them into the
        // same Uniform fit used by every native player before the clip transform.
        var player = FindMaskPreviewPlayer(host)?.MediaPlayer;
        var sourceWidth = player?.PlaybackSession.NaturalVideoWidth ?? 0;
        var sourceHeight = player?.PlaybackSession.NaturalVideoHeight ?? 0;
        var fit = EditorSourceFitMath.Calculate(width, height, sourceWidth, sourceHeight,
            clip.CropLeft, clip.CropTop, clip.CropRight, clip.CropBottom);
        var hash = new HashCode();
        hash.Add(width); hash.Add(height); hash.Add(sourceWidth); hash.Add(sourceHeight);
        hash.Add(clip.MaskShape); hash.Add(clip.MaskInverted); hash.Add(clip.MaskRoundness);
        hash.Add(clip.CropLeft); hash.Add(clip.CropTop); hash.Add(clip.CropRight); hash.Add(clip.CropBottom);
        foreach (var point in clip.MaskPolygon) { hash.Add(point.X); hash.Add(point.Y); }
        var signature = hash.ToHashCode();
        if (resources.Clip is not null && resources.Signature == signature)
        {
            visual.Clip = resources.Clip;
            return true;
        }

        visual.Clip = null;
        resources.Clear();
#if DEBUG
        System.Diagnostics.Debug.WriteLine($"Mask preview {clip.Id}: canvas {width}x{height}, source {sourceWidth}x{sourceHeight}, crop {clip.CropLeft}/{clip.CropTop}/{clip.CropRight}/{clip.CropBottom}, fit {fit}");
        try { System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Klipp-mask-preview.log"), $"{DateTimeOffset.Now:O} {clip.Id}: canvas {width}x{height}, source {sourceWidth}x{sourceHeight}, crop {clip.CropLeft}/{clip.CropTop}/{clip.CropRight}/{clip.CropBottom}, fit {fit}\n"); } catch { }
#endif
        var device = CanvasDevice.GetSharedDevice();
        CanvasGeometry geometry;
        if (clip.MaskShape == "polygon" && clip.MaskPolygon.Count >= 3)
            geometry = CanvasGeometry.CreatePolygon(device, clip.MaskPolygon.Select(point =>
                new Vector2((float)((fit.SourceLeft + point.X * fit.SourceWidth) * fit.RefitScale + fit.TranslateX),
                    (float)((fit.SourceTop + point.Y * fit.SourceHeight) * fit.RefitScale + fit.TranslateY))).ToArray());
        else if (clip.MaskShape == "ellipse")
            geometry = CanvasGeometry.CreateEllipse(device, (float)(width / 2), (float)(height / 2), (float)(width / 2), (float)(height / 2));
        else
        {
            var radius = (float)(Math.Min(width, height) * Math.Clamp(clip.MaskRoundness, 0, 0.5));
            geometry = CanvasGeometry.CreateRoundedRectangle(device, 0, 0, (float)width, (float)height, radius, radius);
        }
        if (clip.MaskInverted)
        {
            using var canvas = CanvasGeometry.CreateRectangle(device, 0, 0, (float)width, (float)height);
            var inverted = canvas.CombineWith(geometry, Matrix3x2.Identity, CanvasGeometryCombine.Exclude);
            geometry.Dispose();
            geometry = inverted;
        }
        resources.Signature = signature;
        resources.Geometry = geometry;
        resources.Path = new CompositionPath(geometry);
        resources.Shape = visual.Compositor.CreatePathGeometry(resources.Path);
        resources.Clip = visual.Compositor.CreateGeometricClip(resources.Shape);
        visual.Clip = resources.Clip;
        return true;
    }

    private static MediaPlayerElement? FindMaskPreviewPlayer(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is MediaPlayerElement player) return player;
            if (FindMaskPreviewPlayer(child) is { } nested) return nested;
        }
        return null;
    }

    private static void ApplySourceCropRefit(Grid host, EditorProjectClip? clip, RectangleGeometry cropGeometry, double width, double height)
    {
        var player = FindMaskPreviewPlayer(host)?.MediaPlayer;
        var sourceWidth = (double)(player?.PlaybackSession.NaturalVideoWidth ?? 0);
        var sourceHeight = (double)(player?.PlaybackSession.NaturalVideoHeight ?? 0);
        if ((sourceWidth <= 0 || sourceHeight <= 0) && FindSourcePreviewBitmap(host) is { } image)
        {
            sourceWidth = image.PixelWidth;
            sourceHeight = image.PixelHeight;
        }
        var fit = EditorSourceFitMath.Calculate(width, height, sourceWidth,
            sourceHeight, clip?.CropLeft ?? 0, clip?.CropTop ?? 0,
            clip?.CropRight ?? 0, clip?.CropBottom ?? 0);
        var bounds = EditorVideoSelectionMath.GetSourceBounds(fit);
        cropGeometry.Rect = new Windows.Foundation.Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
        // Clip the stationary parent in final canvas coordinates and transform
        // its media children. This keeps XAML's clip and native composition
        // content in the same space, including newly-created GPU frame images.
        foreach (var child in host.Children.OfType<Grid>())
        {
            // Explicit media bounds also work with MediaPlayerElement's external
            // composition surface, whose content can ignore a child transform.
            if (child.Width != width) child.Width = width;
            if (child.Height != height) child.Height = height;
            var state = SourceCropPreviewCache.GetValue(child, static _ => new SourceCropPreviewState());
            state.Width = width * fit.RefitScale;
            state.Height = height * fit.RefitScale;
            state.Margin = new Thickness(fit.TranslateX, fit.TranslateY, 0, 0);
            state.ImageBounds = EditorVideoSelectionMath.GetSourceImageBounds(fit);
            foreach (var media in child.Children.OfType<FrameworkElement>())
                ApplySourceContentTransform(child, media);
        }
    }

    private static void ApplySourceContentTransform(Grid host, FrameworkElement media)
    {
        if (!SourceCropPreviewCache.TryGetValue(host, out var state)) return;
        var width = state.Width;
        var height = state.Height;
        var margin = state.Margin;
        if (media is Image image)
        {
            // A left-aligned Uniform Image can arrange itself at the source's
            // narrower width at x=0, leaving the centered crop entirely empty.
            // Fit the source once and place that exact rectangle explicitly.
            width = state.ImageBounds.Width;
            height = state.ImageBounds.Height;
            margin = new Thickness(state.ImageBounds.Left, state.ImageBounds.Top, 0, 0);
            if (image.Stretch != Stretch.Fill) image.Stretch = Stretch.Fill;
        }
        if (media.Width != width) media.Width = width;
        if (media.Height != height) media.Height = height;
        if (media.Margin != margin) media.Margin = margin;
        media.HorizontalAlignment = HorizontalAlignment.Left;
        media.VerticalAlignment = VerticalAlignment.Top;
    }

    private static BitmapSource? FindSourcePreviewBitmap(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Image { Source: BitmapSource { PixelWidth: > 0, PixelHeight: > 0 } bitmap })
                return bitmap;
            if (FindSourcePreviewBitmap(child) is { } nested) return nested;
        }
        return null;
    }
}
