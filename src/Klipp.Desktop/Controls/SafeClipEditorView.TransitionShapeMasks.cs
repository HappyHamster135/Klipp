using System;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Klipp.Core.Editing;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private sealed class TransitionShapeResources
    {
        public string Kind = "rectangle";
        public CompositionGeometricClip? Clip;
        public CompositionEllipseGeometry? Ellipse;
        public CompositionPathGeometry? Shape;
        public CanvasGeometry? Geometry;
        public CompositionPath? Path;
        public double Width, Height, Progress = -1;
        public void Clear()
        {
            Clip?.Dispose(); Ellipse?.Dispose(); Shape?.Dispose(); Geometry?.Dispose();
            Clip = null; Ellipse = null; Shape = null; Geometry = null; Path = null;
            Kind = "rectangle"; Progress = -1;
        }
    }

    private static readonly ConditionalWeakTable<Grid, TransitionShapeResources> TransitionShapeCache = new();

    private static void ClearTransitionShapeMask(Grid host)
    {
        if (!TransitionShapeCache.TryGetValue(host, out var resources) || resources.Clip is null) return;
        ElementCompositionPreview.GetElementVisual(host).Clip = null;
        resources.Clear();
    }

    private static void ApplyTransitionShapeMask(Grid host, EditorTransitionAuditionFrame frame, double width, double height)
    {
        if (frame.MaskKind is not ("circle" or "diagonal"))
        {
            ClearTransitionShapeMask(host);
            return;
        }
        var resources = TransitionShapeCache.GetValue(host, static _ => new TransitionShapeResources());
        var visual = ElementCompositionPreview.GetElementVisual(host);
        var p = Math.Clamp(frame.MaskProgress, 0, 1);
        if (resources.Kind != frame.MaskKind)
        {
            visual.Clip = null;
            resources.Clear();
            resources.Kind = frame.MaskKind;
        }
        if (resources.Clip is not null && resources.Width == width && resources.Height == height && resources.Progress == p)
            return;
        if (frame.MaskKind == "circle")
        {
            resources.Ellipse ??= visual.Compositor.CreateEllipseGeometry();
            resources.Ellipse.Center = new Vector2((float)(width / 2), (float)(height / 2));
            var radius = (float)EditorTransitionAuditionMath.GetCircleRadius(width, height, p);
            resources.Ellipse.Radius = new Vector2(radius);
            resources.Clip ??= visual.Compositor.CreateGeometricClip(resources.Ellipse);
        }
        else
        {
            // The diagonal edge sweeps from upper-left to lower-right. This is
            // the same normalized half-plane used by the export expression.
            var points = EditorTransitionAuditionMath.GetRevealPolygon(p)
                .Select(point => new Vector2((float)(point.X * width), (float)(point.Y * height))).ToArray();
            var geometry = CanvasGeometry.CreatePolygon(CanvasDevice.GetSharedDevice(), points);
            var oldGeometry = resources.Geometry;
            resources.Geometry = geometry;
            resources.Path = new CompositionPath(geometry);
            if (resources.Shape is null) resources.Shape = visual.Compositor.CreatePathGeometry(resources.Path);
            else resources.Shape.Path = resources.Path;
            resources.Clip ??= visual.Compositor.CreateGeometricClip(resources.Shape);
            oldGeometry?.Dispose();
        }
        resources.Width = width; resources.Height = height; resources.Progress = p;
        visual.Clip = resources.Clip;
    }
}
