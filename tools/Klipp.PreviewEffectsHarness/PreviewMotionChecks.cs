using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private static void RunEffectGraphSignatureChecks()
    {
        var clip = new EditorProjectClip { SourcePath = "synthetic.mp4" };
        var layer = new EditorProjectEffectLayer { TargetClipId = clip.Id, Filter = "mono" };
        var project = new EditorProject { EffectLayers = [layer] };
        var view = new SafeClipEditorView(project);
        var signature = view.BuildPreviewEffectSignature(clip);
        var changed = layer.Clone();
        changed.Amount = 0;
        changed.Enabled = false;
        changed.StartSeconds = 1;
        changed.EndSeconds = 3;
        changed.RegionShape = "ellipse";
        changed.RegionX = .2;
        changed.RegionWidth = .4;
        changed.RegionFeather = .6;
        changed.UpsertRegionKeyframe(1, new(.1, .2, .3, .4));
        changed.UpsertRegionKeyframe(3, new(.5, .3, .4, .5));
        project.EffectLayers = [changed];
        Check(view.BuildPreviewEffectSignature(clip) == signature,
            "A dynamic FX edit changed the graph signature and would rebuild its resources.");
        changed.Filter = "warm";
        Check(view.BuildPreviewEffectSignature(clip) != signature, "Changing a filter retained an obsolete graph signature.");
        changed.Filter = "mono";
        var second = changed.Clone(); second.Id = Guid.NewGuid().ToString("N");
        project.EffectLayers = [changed, second];
        var ordered = view.BuildPreviewEffectSignature(clip);
        Check(ordered != signature, "Adding an FX did not change the graph topology signature.");
        project.EffectLayers = [second, changed];
        Check(view.BuildPreviewEffectSignature(clip) != ordered, "Reordering FX retained their old graph topology.");
    }

    private static void RunAnimatedRegionPixelChecks(CanvasDevice device)
    {
        const int width = 128, height = 96;
        var original = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var offset = (y * width + x) * 4;
            var alpha = (byte)(x * 255 / (width - 1));
            original[offset] = (byte)(alpha * .12);
            original[offset + 1] = (byte)(alpha * .82);
            original[offset + 2] = (byte)(alpha * .26);
            original[offset + 3] = alpha;
        }
        using var input = CanvasBitmap.CreateFromBytes(device, original, width, height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        using var target = new CanvasRenderTarget(device, width, height, 96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        var referenceBlends = new List<TimelineFrameEffectBlend>();
        byte[] fullGrade;
        try
        {
            var reference = BuildTimelineFrameEffects(input, [new EditorProjectEffectLayer { Filter = "mono" }],
                width, height, referenceBlends);
            referenceBlends[0].SetAmount(1);
            fullGrade = Render(reference);
        }
        finally
        {
            foreach (var blend in referenceBlends) blend.Dispose();
        }
        foreach (var shape in new[] { "rectangle", "ellipse" })
        {
            var layer = new EditorProjectEffectLayer
            {
                Filter = "mono", StartSeconds = 4, EndSeconds = 6,
                RegionShape = shape, RegionFeather = .5
            };
            layer.UpsertRegionKeyframe(4, new(.05, .25, .3, .5));
            layer.UpsertRegionKeyframe(6, new(.65, .25, .3, .5));
            var blends = new List<TimelineFrameEffectBlend>();
            try
            {
                // Reuse the same graph and surfaces through every animation sample.
                var graph = BuildTimelineFrameEffects(input, [layer], width, height, blends);
                foreach (var seconds in new[] { 4d, 4.5, 5, 5.5, 5.999, 4 })
                {
                    var current = layer.Clone();
                    blends[0].UpdateLayer(current, seconds);
                    blends[0].SetAmount(.8);
                    var actual = Render(graph);
                    var region = current.EvaluateRegion(seconds);
                    for (var y = 0; y < height; y++)
                    for (var x = 0; x < width; x++)
                    {
                        var offset = (y * width + x) * 4;
                        var coverage = EditorEffectRegionMath.Coverage(region, shape, current.RegionFeather,
                            width, height, x + .5, y + .5);
                        Check(Math.Abs(actual[offset + 3] - original[offset + 3]) <= 1,
                            $"Animated {shape} feather changed source alpha at {seconds}.");
                        for (var channel = 0; channel < 3; channel++)
                        {
                            var expected = original[offset + channel] * (1 - .8 * coverage) +
                                fullGrade[offset + channel] * .8 * coverage;
                            Check(Math.Abs(actual[offset + channel] - expected) <= 4,
                                $"Animated {shape} coverage was stale or incorrect at {seconds}, pixel {x},{y}.");
                            if (coverage == 0)
                                Check(Math.Abs(actual[offset + channel] - original[offset + channel]) <= 1,
                                    $"Moving {shape} left changed pixels outside its current region.");
                        }
                    }
                }
            }
            finally
            {
                foreach (var blend in blends) blend.Dispose();
            }
        }

        byte[] Render(ICanvasImage graph)
        {
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Microsoft.UI.Colors.Transparent);
                if (device.IsBufferPrecisionSupported(CanvasBufferPrecision.Precision16Float))
                    drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                drawing.DrawImage(graph);
            }
            return target.GetPixelBytes();
        }
    }
}
