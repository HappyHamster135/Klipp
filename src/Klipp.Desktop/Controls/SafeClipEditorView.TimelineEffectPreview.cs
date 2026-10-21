using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Klipp.Desktop.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Foundation;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private sealed class TimelineFrameEffectBlend(EditorProjectEffectLayer layer,
        ICanvasImage source, ICanvasImage regionalEffect, ArithmeticCompositeEffect blend,
        TimelineEffectMask mask, IReadOnlyList<IDisposable> ownedNodes) : IDisposable
    {
        public EditorProjectEffectLayer Layer { get; private set; } = layer;
        public ArithmeticCompositeEffect Blend { get; } = blend;
        private bool _active;

        public void UpdateLayer(EditorProjectEffectLayer current, double timelineSeconds)
        {
            Layer = current;
            mask.Update(current.EvaluateRegion(timelineSeconds), current.RegionShape, current.RegionFeather);
        }

        public void SetAmount(double amount)
        {
            var active = amount > 0.00001;
            if (_active != active)
            {
                // Disconnect the expensive branch while outside the FX range.
                // Boundary changes keep the same decoder, masks and GPU graph.
                Blend.Source2 = active ? regionalEffect : source;
                _active = active;
            }
            Blend.Source1Amount = (float)(1 - amount);
            Blend.Source2Amount = (float)amount;
        }

        public void Dispose()
        {
            Blend.Dispose();
            for (var index = ownedNodes.Count - 1; index >= 0; index--)
                ownedNodes[index].Dispose();
        }
    }

    private IReadOnlyList<EditorProjectEffectLayer> GetPreviewTimelineEffectLayers(EditorProjectClip clip) =>
        _project?.EffectLayers.Where(layer => layer.TargetClipId == clip.Id &&
            (layer.Style is not null || layer.Filter != "none")).ToArray() ?? [];

    private bool HasPreviewEffects(EditorProjectClip clip) =>
        // Neutral clips use the same renderer as FX clips, keeping their RGB
        // range stable when FX are added, removed, bypassed or compared.
        !string.IsNullOrWhiteSpace(clip.SourcePath);

    private string BuildPreviewEffectSignature(EditorProjectClip clip)
    {
        return BuildClipEffectSignature(clip, 1) + "|timeline|" + string.Join(';',
            GetPreviewTimelineEffectLayers(clip).Select(layer => string.Join(':', layer.Id, layer.Filter,
                layer.Style is null ? string.Empty : BuildClipEffectSignature(layer.Style, 1),
                PreviewLutFileSignature(layer.Style?.LutPath))));
    }

    private static string PreviewLutFileSignature(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? $"{file.Length}/{file.LastWriteTimeUtc.Ticks}" : "missing";
        }
        catch (IOException) { return "unavailable"; }
        catch (UnauthorizedAccessException) { return "unavailable"; }
    }

    private void WarnTimelineEffectPreviewUnavailable(EditorProjectClip? clip)
    {
        if (clip is not null && GetPreviewTimelineEffectLayers(clip).Count > 0)
            ProjectStatusText.Text = "This device could not preview the FX region. Its timing and coverage are kept for export.";
    }

    private static ICanvasImage BuildTimelineFrameEffects(ICanvasImage source,
        IReadOnlyList<EditorProjectEffectLayer> layers, int width, int height,
        List<TimelineFrameEffectBlend> blends)
    {
        var current = source;
        foreach (var layer in layers)
        {
            var nodes = new List<IDisposable>();
            try
            {
                T Own<T>(T node) where T : ICanvasImage, IDisposable { nodes.Add(node); return node; }
                // Grade straight RGB as an opaque image, then restore the incoming
                // alpha. Otherwise blur/color operations can change transparency.
                var opaque = (ICanvasImage)FrameOpaqueInput(current);
                nodes.Add((IDisposable)opaque);
                ICanvasImage colored;
                if (layer.Style is { } style)
                {
                    var lut = CreatePreviewLutTable(CanvasDevice.GetSharedDevice(), style.LutPath);
                    if (lut is not null) nodes.Add(lut);
                    colored = BuildFrameEffectGraph(style, opaque, 1, lut);
                }
                else
                    colored = FramePresetEffects(opaque, layer.Filter);
                if (!ReferenceEquals(colored, opaque)) nodes.Add((IDisposable)colored);
                var filtered = Own(new AlphaMaskEffect { Source = colored, AlphaMask = current });
                // Every layer keeps one mutable mask, including full-frame FX.
                // Geometry edits update that surface without replacing the image,
                // grade graph, decoder, or previously copied video frame.
                var mask = new TimelineEffectMask(CanvasDevice.GetSharedDevice(), width, height);
                nodes.Add(mask);
                mask.Update(layer.EvaluateRegion(layer.StartSeconds), layer.RegionShape, layer.RegionFeather);
                var inside = Own(new AlphaMaskEffect { Source = filtered, AlphaMask = mask.Surface });
                var outside = Own(new ArithmeticCompositeEffect
                {
                    Source1 = current, Source2 = mask.Surface, MultiplyAmount = -1,
                    Source1Amount = 1, Source2Amount = 0, Offset = 0, ClampOutput = true
                });
                // Complementary interpolation preserves alpha at both hard and
                // feathered mask edges: A*(1-M) + A*M = A.
                var regional = Own(new ArithmeticCompositeEffect
                {
                    Source1 = outside, Source2 = inside, MultiplyAmount = 0,
                    Source1Amount = 1, Source2Amount = 1, Offset = 0, ClampOutput = true
                });
                var blend = new ArithmeticCompositeEffect
                {
                    Source1 = current, Source2 = current, MultiplyAmount = 0, Offset = 0,
                    Source1Amount = 1, Source2Amount = 0, ClampOutput = true
                };
                blends.Add(new TimelineFrameEffectBlend(layer, current, regional, blend, mask, nodes));
                current = blend;
            }
            catch
            {
                // A device/LUT failure can interrupt construction before this
                // layer reaches the owner. Release that partial branch here.
                for (var index = nodes.Count - 1; index >= 0; index--) nodes[index].Dispose();
                throw;
            }
        }
        return current;
    }
}
