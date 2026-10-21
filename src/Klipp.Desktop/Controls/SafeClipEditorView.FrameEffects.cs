using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Klipp.Core.Models;
using Klipp.Desktop.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Windows.UI;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    // A frame-server bitmap is a real Direct2D image. Unlike a sampled
    // MediaPlayerElement visual it contains the decoded video pixels, and
    // native Win2D permits shared branches and transfer-table effects.
    private static ICanvasImage BuildFrameEffectGraph(EditorProjectClip clip, ICanvasImage source, double strength,
        EffectTransferTable3D? lutTable = null)
    {
        strength = Math.Clamp(strength, 0, 1);
        if (strength <= 0.00001)
            // The caller owns/disposes the graph independently of its reusable
            // decoded frame. Never hand that bitmap back as the graph root.
            return FrameAffine(source, Vector3.One, Vector3.Zero);

        ICanvasImage current = FrameOpaqueInput(source);
        foreach (var node in clip.Effects.Where(node => node.Enabled))
            current = ApplyFrameEffectStage(current, clip, node.Kind, lutTable);

        current = new AlphaMaskEffect { Source = current, AlphaMask = source };
        if (strength >= 0.99999)
            return current;
        return new ArithmeticCompositeEffect
        {
            Source1 = source, Source2 = current,
            Source1Amount = (float)(1 - strength), Source2Amount = (float)strength,
            MultiplyAmount = 0, Offset = 0, ClampOutput = true
        };
    }

    private static ICanvasImage ApplyFrameEffectStage(ICanvasImage source, EditorProjectClip clip, string kind,
        EffectTransferTable3D? lutTable)
    {
        switch (kind)
        {
            case "look": return FramePresetEffects(source, clip.Filter);
            case "shadows" when Math.Abs(clip.Shadows) > 0.001 || Math.Abs(clip.Highlights) > 0.001:
                return FrameCurve(source, [(0, 0),
                    (0.25, Math.Clamp(0.25 + clip.Shadows * 0.20, 0.05, 0.45)),
                    (0.75, Math.Clamp(0.75 + clip.Highlights * 0.20, 0.55, 0.95)), (1, 1)]);
            case "curve" when clip.ToneCurve is { Count: >= 2 } points && points.Any(point => Math.Abs(point.Y - point.X) > 0.0005):
                return FrameCurve(source, points.Select(point => (point.X, point.Y)).ToList());
            case "color": return FrameEq(source, clip.Contrast, clip.Brightness, clip.Saturation, clip.Gamma);
            case "temperature" when Math.Abs(clip.Temperature) > 0.001:
                var temperature = (float)(Math.Clamp(clip.Temperature, -1, 1) * 0.12);
                return FrameColorBalance(source, new(temperature, 0, -temperature), Vector3.Zero, Vector3.Zero);
            case "hue" when Math.Abs(clip.HueDegrees) > 0.001: return FrameHue(source, clip.HueDegrees);
            case "sharpen" when clip.Sharpen > 0.001: return FrameUnsharp(source, Math.Clamp(clip.Sharpen, 0, 10));
            case "blur" when clip.Blur > 0.001: return FrameBlur(source, clip.Blur);
            case "vignette" when clip.Vignette > 0.001: return FrameVignette(source, clip.Vignette);
            case "lut" when lutTable is not null && clip.LutStrength > 0.001:
                var mapped = new TableTransfer3DEffect { Source = source, Table = lutTable, AlphaMode = CanvasAlphaMode.Premultiplied };
                return clip.LutStrength >= 0.999 ? mapped : new ArithmeticCompositeEffect
                {
                    Source1 = source, Source2 = mapped, MultiplyAmount = 0,
                    Source1Amount = (float)(1 - clip.LutStrength), Source2Amount = (float)clip.LutStrength, ClampOutput = true
                };
            default: return source;
        }
    }

    private static ICanvasImage FramePresetEffects(ICanvasImage source, string? preset)
    {
        if (EditorLookCurves.Find(preset) is { } curves)
            return new TableTransferEffect
            {
                Source = source,
                RedTable = NaturalCubicCurve.BuildLookupTable(curves.Red),
                GreenTable = NaturalCubicCurve.BuildLookupTable(curves.Green),
                BlueTable = NaturalCubicCurve.BuildLookupTable(curves.Blue),
                AlphaDisable = true, ClampOutput = true
            };
        return preset switch
        {
            "boost" => FrameEq(source, 1.07, 0.008, 1.12),
            "warm" => FrameEq(FrameColorBalance(source, new(0.045f, 0.012f, -0.035f), new(0.025f, 0, -0.018f), Vector3.Zero), 1.015, 0, 1.04),
            "cool" => FrameEq(FrameColorBalance(source, new(-0.025f, 0.008f, 0.045f), new(-0.012f, 0, 0.022f), Vector3.Zero), 1.015, 0, 0.98),
            "mono" => FrameSaturation(source, 0),
            "cinematic" => FrameEq(FrameColorBalance(
                FrameCurve(source, [(0, 0.005), (0.2, 0.16), (0.5, 0.5), (0.8, 0.84), (1, 0.985)]),
                new(-0.018f, 0.008f, 0.025f), Vector3.Zero, new(0.022f, 0, -0.012f)), saturation: 0.9),
            "sharpen" => FrameUnsharp(source, 0.55),
            "blur" => FrameBlur(source, 2),
            "vintage" => FrameEq(FrameColorBalance(
                FrameCurve(source, [(0, 0.035), (0.25, 0.27), (0.75, 0.73), (1, 0.95)]),
                new(0.035f, 0.015f, -0.025f), Vector3.Zero, new(0.02f, 0, -0.025f)), saturation: 0.8),
            "vibrant" => FrameEq(source, 1.06, 0.006, 1.22),
            "noir" => FrameCurve(FrameSaturation(source, 0), [(0, 0), (0.2, 0.12), (0.5, 0.51), (0.8, 0.9), (1, 1)]),
            "dream" => FrameColorBalance(FrameEq(FrameBlur(source, 0.4), brightness: 0.018, saturation: 0.96, gamma: 1.025),
                new(0.018f, 0, 0.025f), Vector3.Zero, Vector3.Zero),
            "film" => FrameEq(FrameColorBalance(
                FrameCurve(source, [(0, 0.025), (0.2, 0.18), (0.5, 0.51), (0.8, 0.82), (1, 0.97)]),
                new(0.016f, 0, -0.012f), Vector3.Zero, new(0.02f, 0, -0.018f)), saturation: 0.94),
            "soft" => FrameUnsharp(FrameEq(
                FrameCurve(source, [(0, 0.015), (0.25, 0.27), (0.75, 0.74), (1, 0.985)]),
                saturation: 0.94, gamma: 1.025), -0.15),
            "tealorange" => FrameEq(FrameColorBalance(source,
                new(-0.035f, 0.018f, 0.05f), new(0.018f, 0, -0.018f), new(0.045f, 0.012f, -0.04f)), 1.04, 0, 1.06),
            "glitch" => FrameEq(FrameChromaShift(source), 1.18, 0, 1.25),
            "vignette" => FrameVignette(source, 0.6),
            _ => source
        };
    }

    private static ICanvasImage FrameOpaqueInput(ICanvasImage source) => new ColorMatrixEffect
    {
        Source = source, AlphaMode = CanvasAlphaMode.Premultiplied,
        ColorMatrix = new Matrix5x4 { M11 = 1, M22 = 1, M33 = 1, M54 = 1 }, ClampOutput = true
    };

    private static ICanvasImage FrameAffine(ICanvasImage source, Vector3 scale, Vector3 offset) => new ColorMatrixEffect
    {
        Source = source, AlphaMode = CanvasAlphaMode.Premultiplied,
        ColorMatrix = new Matrix5x4
        {
            M11 = scale.X, M22 = scale.Y, M33 = scale.Z, M44 = 1,
            M51 = offset.X, M52 = offset.Y, M53 = offset.Z
        }, ClampOutput = true
    };

    private static ICanvasImage FrameEq(ICanvasImage source, double contrast = 1, double brightness = 0,
        double saturation = 1, double gamma = 1)
    {
        var current = source;
        if (Math.Abs(contrast - 1) > 0.001 || Math.Abs(brightness) > 0.001)
            current = FrameAffine(current, new((float)contrast), new((float)((1 - contrast) / 2 + brightness)));
        if (Math.Abs(saturation - 1) > 0.001)
            current = FrameSaturation(current, saturation);
        if (Math.Abs(gamma - 1) > 0.001)
        {
            var exponent = (float)(1 / Math.Clamp(gamma, 0.25, 4));
            current = new GammaTransferEffect
            {
                Source = current, RedExponent = exponent, GreenExponent = exponent, BlueExponent = exponent,
                AlphaDisable = true, ClampOutput = true
            };
        }
        return current;
    }

    private static ICanvasImage FrameSaturation(ICanvasImage source, double saturation)
    {
        var s = (float)Math.Clamp(saturation, 0, 3);
        var r = 0.2126f * (1 - s);
        var g = 0.7152f * (1 - s);
        var b = 0.0722f * (1 - s);
        return new ColorMatrixEffect
        {
            Source = source, ClampOutput = true,
            ColorMatrix = new Matrix5x4
            {
                M11 = r + s, M12 = r, M13 = r, M21 = g, M22 = g + s, M23 = g,
                M31 = b, M32 = b, M33 = b + s, M44 = 1
            }
        };
    }

    private static ICanvasImage FrameCurve(ICanvasImage source, IReadOnlyList<(double X, double Y)> points)
    {
        var table = NaturalCubicCurve.BuildLookupTable(points.Select(point => new PiecewiseLinearCurvePoint(point.X, point.Y)));
        return new TableTransferEffect
        {
            Source = source, RedTable = table, GreenTable = table, BlueTable = table,
            AlphaDisable = true, ClampOutput = true
        };
    }

    private static ICanvasImage FrameColorBalance(ICanvasImage source, Vector3 shadows, Vector3 midtones, Vector3 highlights)
    {
        // The existing preview's smooth tonal weights remain stable when users
        // switch between live video, freeze frames, and image clips.
        ICanvasImage SignedAffine(ICanvasImage input, Vector3 scale, Vector3 offset,
            bool clamp = false, float alphaScale = 1) => new ColorMatrixEffect
        {
            Source = input, AlphaMode = CanvasAlphaMode.Straight, ClampOutput = clamp,
            ColorMatrix = new Matrix5x4
            {
                M11 = scale.X, M22 = scale.Y, M33 = scale.Z, M44 = alphaScale,
                M51 = offset.X, M52 = offset.Y, M53 = offset.Z
            }
        };
        var result = SignedAffine(source, Vector3.One - 2 * shadows + 4 * midtones, shadows);
        var quadratic = shadows - 4 * midtones + highlights;
        if (quadratic.LengthSquared() > 1e-12)
        {
            var square = new ArithmeticCompositeEffect
            {
                Source1 = source, Source2 = source, MultiplyAmount = 1,
                Source1Amount = 0, Source2Amount = 0, Offset = 0, ClampOutput = false
            };
            result = new ArithmeticCompositeEffect
            {
                Source1 = result, Source2 = SignedAffine(square, quadratic, Vector3.Zero, alphaScale: 0),
                Source1Amount = 1, Source2Amount = 1, MultiplyAmount = 0, Offset = 0, ClampOutput = false
            };
        }
        return SignedAffine(result, Vector3.One, Vector3.Zero, clamp: true);
    }

    private static ICanvasImage FrameHue(ICanvasImage source, double degrees) => new HueRotationEffect
    {
        Source = source, Angle = (float)(Math.Clamp(degrees, -180, 180) * Math.PI / 180)
    };

    private static ICanvasImage FrameBlur(ICanvasImage source, double amount) => new GaussianBlurEffect
    {
        Source = source, BlurAmount = (float)Math.Clamp(amount, 0, 10), BorderMode = EffectBorderMode.Hard
    };

    private static ICanvasImage FrameUnsharp(ICanvasImage source, double amount)
    {
        // FFmpeg's 5x5 unsharp uses a separable binomial blur. Match that kernel
        // rather than substituting a different native sharpening algorithm.
        float[] row = [1, 4, 6, 4, 1];
        var kernel = new float[25];
        for (var y = 0; y < 5; y++)
            for (var x = 0; x < 5; x++)
                kernel[y * 5 + x] = row[x] * row[y];
        var blurred = new ConvolveMatrixEffect
        {
            Source = source, KernelWidth = 5, KernelHeight = 5, KernelMatrix = kernel,
            Divisor = 256, PreserveAlpha = true, BorderMode = EffectBorderMode.Hard, ClampOutput = true
        };
        return new ArithmeticCompositeEffect
        {
            Source1 = source, Source2 = blurred, MultiplyAmount = 0,
            Source1Amount = (float)(1 + amount), Source2Amount = (float)-amount,
            Offset = 0, ClampOutput = true
        };
    }

    private static ICanvasImage FrameVignette(ICanvasImage source, double amount) => new VignetteEffect
    {
        Source = source, Color = Color.FromArgb(255, 0, 0, 0),
        Amount = (float)(Math.Clamp(amount, 0, 1) * 0.8), Curve = 0.5f
    };

    private static ICanvasImage FrameChromaShift(ICanvasImage source)
    {
        ICanvasImage Channel(Vector3 weights, float shift) => new Transform2DEffect
        {
            Source = new BorderEffect
            {
                Source = new ColorMatrixEffect
                {
                    Source = source, AlphaMode = CanvasAlphaMode.Straight,
                    BufferPrecision = CanvasBufferPrecision.Precision16Float, ClampOutput = false,
                    ColorMatrix = new Matrix5x4
                    {
                        M11 = weights.X, M12 = weights.X, M13 = weights.X,
                        M21 = weights.Y, M22 = weights.Y, M23 = weights.Y,
                        M31 = weights.Z, M32 = weights.Z, M33 = weights.Z, M44 = 1
                    }
                }, ExtendX = CanvasEdgeBehavior.Clamp, ExtendY = CanvasEdgeBehavior.Clamp
            }, TransformMatrix = Matrix3x2.CreateTranslation(shift, 0)
        };
        var y = Channel(new(0.299f, 0.587f, 0.114f), 0);
        var cb = Channel(new(-0.168736f, -0.331264f, 0.5f), 8);
        var cr = Channel(new(0.5f, -0.418688f, -0.081312f), -8);
        ICanvasImage Chroma(ICanvasImage channel, Vector3 weights) => new ColorMatrixEffect
        {
            Source = channel, AlphaMode = CanvasAlphaMode.Straight,
            BufferPrecision = CanvasBufferPrecision.Precision16Float, ClampOutput = false,
            ColorMatrix = new Matrix5x4 { M11 = weights.X, M12 = weights.Y, M13 = weights.Z }
        };
        ICanvasImage Sum(ICanvasImage left, ICanvasImage right, bool clamp) => new ArithmeticCompositeEffect
        {
            Source1 = left, Source2 = right, Source1Amount = 1, Source2Amount = 1,
            MultiplyAmount = 0, Offset = 0, ClampOutput = clamp,
            BufferPrecision = CanvasBufferPrecision.Precision16Float
        };
        return Sum(Sum(y, Chroma(cb, new(0, -0.344136f, 1.772f)), false), Chroma(cr, new(1.402f, -0.714136f, 0)), true);
    }
}
