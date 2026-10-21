using Klipp.Desktop.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics.DirectX;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    // Only the owner-specific lookup/status methods are stubbed; every graph,
    // preset and blend under test is linked directly from the editor source.
    private readonly EditorProject? _project = null;
    private SafeClipEditorView(EditorProject? project = null) => _project = project;
    private TextBlock ProjectStatusText => throw new NotSupportedException();
    private static bool HasGpuPreviewEffects(EditorProjectClip clip) => false;
    private static string BuildClipEffectSignature(EditorProjectClip clip, double amount) => string.Empty;
    private static void ApplySourceContentTransform(Grid host, Microsoft.UI.Xaml.FrameworkElement media) { }

    internal static void VerifyFrameServerModeAfterGraphChanges(Windows.Media.Playback.MediaPlayer player)
    {
        foreach (var effects in new IReadOnlyList<EditorProjectEffectLayer>[] { [], [new() { Filter = "glitch" }], [] })
        {
            var host = new Grid();
            using (var preview = new ClipEffectPreviewResources(player, host, new EditorProjectClip(), 1,
                effects, 0, false, "test", Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(), (_, _) => { }))
            {
                if (!player.IsVideoFrameServerEnabled)
                    throw new InvalidOperationException("Creating a neutral/FX preview changed decoder output mode.");
            }
            if (!player.IsVideoFrameServerEnabled)
                throw new InvalidOperationException("Removing/rebuilding an FX graph changed decoder output mode.");
        }
    }

    internal static byte[] RenderCopiedVideoForChecks(CanvasBitmap input, int width, int height)
    {
        using var graded = (IDisposable)BuildFrameEffectGraph(new EditorProjectClip(), input, 1);
        using var clipBlend = new ArithmeticCompositeEffect
        {
            Source1 = input, Source2 = (ICanvasImage)graded, MultiplyAmount = 0, Offset = 0,
            Source1Amount = 0, Source2Amount = 1, ClampOutput = true
        };
        var blends = new List<TimelineFrameEffectBlend>();
        try
        {
            var graph = BuildTimelineFrameEffects(clipBlend, [new EditorProjectEffectLayer
            {
                Filter = "glitch", StartSeconds = 0, EndSeconds = 30, RegionShape = "ellipse",
                RegionX = .25, RegionY = .25, RegionWidth = .5, RegionHeight = .5
            }], width, height, blends);
            foreach (var effect in blends) { effect.UpdateLayer(effect.Layer, 0); effect.SetAmount(1); }
            using var target = new CanvasRenderTarget(input.Device, width, height, 96,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Microsoft.UI.Colors.Transparent);
                drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                drawing.DrawImage(graph);
            }
            return target.GetPixelBytes();
        }
        finally
        {
            for (var index = blends.Count - 1; index >= 0; index--) blends[index].Dispose();
        }
    }

    public static void RunTimelineEffectPixelChecks()
    {
        const int width = 32, height = 24;
        VerifyNativeFallbackLifetime();
        RunEffectGraphSignatureChecks();
        using var device = CanvasDevice.GetSharedDevice();
        RunFeatherMaskChecks(device);
        RunAnimatedRegionPixelChecks(device);
        var rgba = new byte[width * height * 4];
        // Vary alpha by column, including fully transparent pixels. Store a
        // non-neutral green color in BGRA premultiplied form.
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var alpha = (byte)(x * 255 / (width - 1));
            var offset = (y * width + x) * 4;
            rgba[offset] = (byte)(alpha * 0.12);
            rgba[offset + 1] = (byte)(alpha * 0.82);
            rgba[offset + 2] = (byte)(alpha * 0.26);
            rgba[offset + 3] = alpha;
        }
        using var input = CanvasBitmap.CreateFromBytes(device, rgba, width, height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        var effect = new EditorProjectEffectLayer
        {
            Filter = "mono", StartSeconds = 1, EndSeconds = 2,
            RegionX = 0.25, RegionY = 0.25, RegionWidth = 0.5, RegionHeight = 0.5
        };
        foreach (var shape in new[] { "rectangle", "ellipse" })
        {
            effect.RegionShape = shape;
            foreach (var amount in new[] { 0d, 0.5, 1d })
            {
                effect.Amount = amount;
                var actual = Render([effect], 1.5);
                for (var pixel = 0; pixel < width * height; pixel++)
                    Check(Math.Abs(actual[pixel * 4 + 3] - rgba[pixel * 4 + 3]) <= 1,
                        $"{shape} amount {amount} altered alpha at pixel {pixel}.");
                AssertPixelUnchanged(actual, 4, 4, "Outside-region pixels changed.");
                if (amount == 0)
                    AssertSame(actual, rgba, "Zero amount changed the source.");
                else
                {
                    var center = (12 * width + 16) * 4;
                    if (amount == 1)
                        Check(Math.Abs(actual[center + 1] - actual[center + 2]) < 3 &&
                            Math.Abs(actual[center] - actual[center + 1]) < 3,
                            "Full monochrome was not neutral in the selected area.");
                    else
                        Check(actual[center + 1] < rgba[center + 1] - 3 &&
                            actual[center + 1] > actual[center + 2] + 3,
                            $"Half amount did not interpolate the selected area: {shape} BGRA {string.Join(',', actual.Skip(center).Take(4))}, original {string.Join(',', rgba.Skip(center).Take(4))}.");
                }
            }
            effect.Amount = 1;
            AssertSame(Render([effect], 0.999), rgba, "FX appeared before its start.");
            AssertSame(Render([effect], 2), rgba, "FX remained at its exclusive end.");
            AssertSame(Render([effect], 1.5, bypass: true), rgba, "Compare original retained FX.");
        }
        effect.RegionShape = "rectangle";
        effect.RegionX = effect.RegionY = 0;
        effect.RegionWidth = effect.RegionHeight = 1;
        foreach (var look in new[] { "blur", "glitch", "warm", "film", "vignette" })
        {
            effect.Filter = look;
            var actual = Render([effect], 1.5);
            for (var pixel = 0; pixel < width * height; pixel++)
                Check(Math.Abs(actual[pixel * 4 + 3] - rgba[pixel * 4 + 3]) <= 1,
                    $"Full-frame {look} changed original alpha.");
        }
        effect.Filter = "mono";
        var next = effect.Clone();
        next.Filter = "warm";
        var stacked = Render([effect, next], 1.5);
        for (var pixel = 0; pixel < width * height; pixel++)
            Check(Math.Abs(stacked[pixel * 4 + 3] - rgba[pixel * 4 + 3]) <= 1, "Stacked FX changed alpha.");
        effect.Style = new EditorProjectClip { Brightness = 0.1 };
        effect.Filter = "none";
        var customGrade = Render([effect], 1.5);
        var sample = (12 * width + 16) * 4;
        Check(customGrade[sample + 1] > rgba[sample + 1] + 8,
            "An arbitrary saved grade with Filter none did not take precedence.");
        AssertAlpha(customGrade, "Saved grade changed alpha.");
        var lutPath = Path.Combine(Path.GetTempPath(), $"Klipp-preview-{Guid.NewGuid():N}.cube");
        try
        {
            var rows = new List<string> { "LUT_3D_SIZE 2" };
            for (var blue = 0; blue < 2; blue++)
            for (var green = 0; green < 2; green++)
            for (var red = 0; red < 2; red++)
                rows.Add($"{1-red} {1-green} {1-blue}");
            File.WriteAllLines(lutPath, rows);
            effect.Style.Brightness = 0;
            effect.Style.LutPath = lutPath;
            var lutGrade = Render([effect], 1.5);
            AssertAlpha(lutGrade, "Saved LUT changed alpha.");
            for (var channel = 0; channel < 3; channel++)
                Check(Math.Abs(lutGrade[sample + channel] - (rgba[sample + 3] - rgba[sample + channel])) < 4,
                    "A saved custom LUT did not render in straight color with original alpha.");
            effect.RegionX = effect.RegionY = 0.25;
            effect.RegionWidth = effect.RegionHeight = 0.5;
            var regionalLut = Render([effect], 1.5);
            AssertPixelUnchanged(regionalLut, 4, 4, "Saved LUT changed pixels outside its region.");
            AssertAlpha(regionalLut, "Regional saved LUT changed alpha.");
        }
        finally { if (File.Exists(lutPath)) File.Delete(lutPath); }
        effect.Style = null;
        effect.Filter = "mono";
        // Recreate and dispose graphs repeatedly so detached inactive branches
        // and command-list masks are exercised through their ownership path.
        for (var iteration = 0; iteration < 40; iteration++) Render([effect, next], iteration % 3);

        void AssertAlpha(byte[] pixels, string message)
        {
            for (var pixel = 0; pixel < width * height; pixel++)
                Check(Math.Abs(pixels[pixel * 4 + 3] - rgba[pixel * 4 + 3]) <= 1, message);
        }

        byte[] Render(IReadOnlyList<EditorProjectEffectLayer> effects, double seconds, bool bypass = false)
        {
            var blends = new List<TimelineFrameEffectBlend>();
            try
            {
                var graph = BuildTimelineFrameEffects(input, effects, width, height, blends);
                foreach (var item in blends)
                {
                    item.UpdateLayer(item.Layer, seconds);
                    item.SetAmount(!bypass && item.Layer.IsActiveAt(seconds) ? item.Layer.Amount : 0);
                }
                using var target = new CanvasRenderTarget(device, width, height, 96,
                    DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
                using (var drawing = target.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    if (device.IsBufferPrecisionSupported(CanvasBufferPrecision.Precision16Float))
                        drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                    drawing.DrawImage(graph);
                }
                return target.GetPixelBytes();
            }
            finally
            {
                for (var index = blends.Count - 1; index >= 0; index--) blends[index].Dispose();
            }
        }
        void AssertPixelUnchanged(byte[] pixels, int x, int y, string message)
        {
            var offset = (y * width + x) * 4;
            for (var channel = 0; channel < 4; channel++)
                Check(Math.Abs(pixels[offset + channel] - rgba[offset + channel]) <= 1, message);
        }
    }

    private static void RunFeatherMaskChecks(CanvasDevice device)
    {
        const int width = 512, height = 384;
        using var mask = new TimelineEffectMask(device, width, height);
        foreach (var region in new Klipp.Core.Editing.EditorEffectRegion[]
        {
            new(.25, .25, .5, .5), new(.13, .2, .7, .3), new(.33, .12, .22, .7)
        })
        foreach (var shape in new[] { "rectangle", "ellipse" })
        foreach (var feather in new[] { 0d, .25, .5, 1 })
        {
            mask.Update(region, shape, feather);
            var pixels = mask.Surface.GetPixelBytes();
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var expected = Klipp.Core.Editing.EditorEffectRegionMath.Coverage(region, shape, feather,
                    width, height, x + .5, y + .5);
                var actual = pixels[(y * width + x) * 4 + 3] / 255d;
                if (shape == "ellipse" && feather == 0)
                {
                    var rx = region.Width * width / 2; var ry = region.Height * height / 2;
                    var nx = (x + .5 - (region.X * width + rx)) / rx;
                    var ny = (y + .5 - (region.Y * height + ry)) / ry;
                    // D2D's hard ellipse rasterizer approximates its boundary;
                    // evaluate coverage away from that subpixel outline.
                    if (Math.Abs(Math.Sqrt(nx * nx + ny * ny) - 1) * Math.Min(rx, ry) < 1) continue;
                }
                Check(Math.Abs(expected - actual) <= .025,
                    $"{shape} feather {feather} mask differs at {x},{y}: {actual:0.###} vs {expected:0.###}.");
            }
        }
    }

    private static void AssertSame(byte[] actual, byte[] expected, string message)
    {
        for (var index = 0; index < expected.Length; index++)
            Check(Math.Abs(actual[index] - expected[index]) <= 1, message);
    }
    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
    }
}
