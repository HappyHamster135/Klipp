using System.Diagnostics;
using System.Globalization;
using Klipp.Core.Models;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;

internal static class FilterParityChecks
{
    public static async Task RunAsync(EditorProjectExportService export, FFmpegLocator locator, string directory, bool curvesOnly)
    {
        var curves = new[]
        {
            new (double X, double Y)[] { (0, 0.025), (0.2, 0.18), (0.5, 0.51), (0.8, 0.82), (1, 0.97) },
            [(0, 0.015), (0.25, 0.27), (0.75, 0.74), (1, 0.985)],
            [(0, 0.005), (0.2, 0.16), (0.5, 0.5), (0.8, 0.84), (1, 0.985)],
            [(0, 0), (0.2, 0.12), (0.5, 0.51), (0.8, 0.9), (1, 1)],
            [(0, 0), (0.13, 0.17), (0.48, 0.65), (0.9, 0.8), (1, 1)]
        };
        foreach (var curve in curves)
        {
            var points = string.Join(' ', curve.Select(point => $"{F(point.X)}/{F(point.Y)}"));
            var bytes = await RunFfmpegAsync(locator,
                ["-f", "lavfi", "-i", "nullsrc=s=256x16:r=1:d=1,format=gbrp,geq=r='X':g='X':b='X'",
                 "-vf", $"curves=master='{points}'", "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
            var table = NaturalCubicCurve.BuildLookupTable(curve.Select(point => new PiecewiseLinearCurvePoint(point.X, point.Y)));
            Check(bytes.Length == 256 * 16 * 3, "FFmpeg did not return the complete reference ramp.");
            for (var value = 0; value < 256; value++)
                Check(Math.Abs(bytes[value * 3] - Math.Round(table[value] * 255)) <= 1,
                    $"Natural cubic curve differs from FFmpeg at input {value}.");
        }
        Console.WriteLine("PASS: natural cubic lookup agrees with actual FFmpeg RGB ramps within one channel value");
        await CheckSharedLookCurvesAsync(locator);
        var chart = Path.Combine(directory, "strength-chart.mp4");
        await RunFfmpegAsync(locator,
            ["-y", "-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", chart]);
        await CheckCloseCurveKnotsAsync(export, chart, directory);
        if (curvesOnly)
            return;
        var baselinePath = Path.Combine(directory, "strength-baseline.mp4");
        await export.ExportAsync(Project(Clip(chart)), baselinePath);
        var baseline = await FrameAsync(locator, baselinePath, 0.2);
        var styles = new[] { "boost", "warm", "cool", "mono", "cinematic", "sharpen", "blur", "vintage", "vibrant", "noir", "dream", "film", "soft", "tealorange", "glitch", "vignette", "natural", "matte", "goldenhour", "steel" };
        foreach (var style in styles)
        {
            var frames = new List<byte[]>();
            foreach (var strength in new[] { 0d, 0.5, 1d })
            {
                var clip = Clip(chart);
                clip.Filter = style;
                clip.FilterStrength = strength;
                var path = Path.Combine(directory, $"strength-{style}-{strength}.mp4");
                await export.ExportAsync(Project(clip), path);
                frames.Add(await FrameAsync(locator, path, 0.2));
            }
            var zeroError = frames[0].Zip(baseline, (actual, expected) => Math.Abs(actual - expected)).Average();
            Check(zeroError < 0.5, $"{style} changed the image at zero strength ({zeroError:0.###}).");
            var midpointError = frames[1].Select((actual, index) => Math.Abs(actual - (frames[0][index] + frames[2][index]) / 2d)).Average();
            Check(midpointError < 2, $"{style} did not blend linearly at half strength ({midpointError:0.###}).");
            Console.WriteLine($"PASS: {style} strengths 0/50/100% (half-strength error {midpointError:0.###})");
        }
        await CheckNewLookStacksAsync(export, locator, chart, directory, baseline);

        var edited = Clip(chart);
        edited.Filter = "film";
        edited.FilterStrength = 0.5;
        edited.Brightness = 0.08;
        edited.Contrast = 1.2;
        edited.Saturation = 0.8;
        edited.Gamma = 0.9;
        var neutral = Clip(chart);
        var mixedPath = Path.Combine(directory, "mixed-graded-neutral.mp4");
        await export.ExportAsync(Project(edited, neutral), mixedPath);
        var neutralFrame = await FrameAsync(locator, mixedPath, 0.7);
        var leakError = neutralFrame.Zip(baseline, (actual, expected) => Math.Abs(actual - expected)).Average();
        Check(leakError < 0.6, $"A clip grade affected the following neutral clip ({leakError:0.###}).");
        Console.WriteLine("PASS: edited-clip grades stay local in a mixed timeline");
    }

    private static readonly string[] NewLooks = ["natural", "matte", "goldenhour", "steel"];

    private static async Task CheckSharedLookCurvesAsync(FFmpegLocator locator)
    {
        foreach (var style in NewLooks)
        {
            var curves = EditorLookCurves.Find(style) ?? throw new InvalidOperationException($"Missing {style} channel curves.");
            var tables = new[] { curves.Red, curves.Green, curves.Blue }.Select(points => NaturalCubicCurve.BuildLookupTable(points)).ToArray();
            var bytes = await RunFfmpegAsync(locator,
                ["-f", "lavfi", "-i", "nullsrc=s=256x16:r=1:d=1,format=gbrp,geq=r='X':g='X':b='X'",
                 "-vf", string.Join(',', ClipEditService.GetVfxFilters(style)), "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
            Check(bytes.Length == 256 * 16 * 3, $"{style} did not return a complete RGB reference ramp.");
            for (var channel = 0; channel < 3; channel++)
            {
                var table = tables[channel];
                for (var value = 0; value < 256; value++)
                {
                    Check(Math.Abs(bytes[value * 3 + channel] - Math.Round(table[value] * 255)) <= 1,
                        $"{style} preview/export mapping differs at channel {channel}, input {value}.");
                    Check(value == 0 || table[value] >= table[value - 1], $"{style} reverses tonal detail.");
                }
                Check(table.Count(value => value <= 0 || value >= 1) <= 3,
                    $"{style} crushes shadows or clips highlights.");
            }
            var averageDifference = Enumerable.Range(0, 256).Average(value =>
                Enumerable.Range(0, 3).Average(channel => Math.Abs(bytes[value * 3 + channel] - value)));
            Check(averageDifference > 1, $"{style} is indistinguishable from the original ramp.");
            if (style == "matte")
                Check(tables.All(table => table[0] > 0.03 && table[255] < 0.98), "Matte must lift black and soften white.");
            if (style == "goldenhour")
                Check(tables[0][204] > tables[2][204] + 0.04 && tables.All(table => table[0] == 0),
                    "Golden hour must warm highlights while preserving black.");
            if (style == "steel")
                Check(tables[2][128] > tables[0][128] + 0.02, "Steel must have a restrained cool bias.");
            Console.WriteLine($"PASS: {style} RGB preview/export curves, restrained endpoints and tonal separation (mean change {averageDifference:0.###})");
        }
    }

    private static async Task CheckNewLookStacksAsync(EditorProjectExportService export, FFmpegLocator locator,
        string chart, string directory, byte[] baseline)
    {
        foreach (var style in NewLooks)
        {
            var disabled = Clip(chart);
            disabled.Filter = style;
            disabled.Effects.Single(effect => effect.Kind == "look").Enabled = false;
            var disabledPath = Path.Combine(directory, $"disabled-{style}.mp4");
            await export.ExportAsync(Project(disabled), disabledPath);
            var disabledFrame = await FrameAsync(locator, disabledPath, 0.2);
            Check(disabledFrame.Zip(baseline, (actual, expected) => Math.Abs(actual - expected)).Average() < 0.5,
                $"Disabled {style} look still changes the picture.");

            var frames = new List<byte[]>();
            foreach (var strength in new[] { 0d, 0.5, 1d })
            {
                var stack = Clip(chart);
                stack.Filter = style;
                stack.FilterStrength = strength;
                stack.Brightness = 0.06;
                stack.Contrast = 1.12;
                stack.ToneCurve = [new() { X = 0, Y = 0.03 }, new() { X = 0.45, Y = 0.55 }, new() { X = 1, Y = 0.98 }];
                var path = Path.Combine(directory, $"stack-{style}-{strength}.mp4");
                await export.ExportAsync(Project(stack, Clip(chart)), path);
                frames.Add(await FrameAsync(locator, path, 0.2));
                var following = await FrameAsync(locator, path, 0.7);
                Check(following.Zip(baseline, (actual, expected) => Math.Abs(actual - expected)).Average() < 0.6,
                    $"{style} stacked effects leaked into the following clip.");
            }
            Check(frames[0].Zip(baseline, (actual, expected) => Math.Abs(actual - expected)).Average() < 0.5,
                $"{style} effect stack is not bypassed at zero strength.");
            Check(frames[1].Select((actual, index) => Math.Abs(actual - (frames[0][index] + frames[2][index]) / 2d)).Average() < 2,
                $"{style} stack does not blend its complete pipeline at half strength.");
            Console.WriteLine($"PASS: {style} disabled look, complete-stack strengths 0/50/100% and neighboring clip isolation");
        }

        var orderFrames = new List<byte[]>();
        foreach (var lookFirst in new[] { true, false })
        {
            var clip = Clip(chart);
            clip.Filter = "goldenhour";
            clip.ToneCurve = [new() { X = 0, Y = 0.08 }, new() { X = 0.4, Y = 0.65 }, new() { X = 1, Y = 0.94 }];
            clip.Effects = clip.Effects.OrderBy(effect => effect.Kind == "look" ? (lookFirst ? -1 : 1) : 0).ToList();
            var path = Path.Combine(directory, $"look-stack-order-{lookFirst}.mp4");
            await export.ExportAsync(Project(clip), path);
            orderFrames.Add(await FrameAsync(locator, path, 0.2));
        }
        Check(orderFrames[0].Zip(orderFrames[1], (first, second) => Math.Abs(first - second)).Average() > 0.5,
            "Reordering a look and nonlinear curve did not change the pipeline order.");
        Console.WriteLine("PASS: look/curve effect-stack ordering is retained during export");
    }

    private static async Task CheckCloseCurveKnotsAsync(EditorProjectExportService export, string chart, string directory)
    {
        foreach (var (left, right, expectedCount) in new[] { (0.5, 0.5001, 3), (126d / 255, 127d / 255, 4) })
        {
            var clip = Clip(chart);
            clip.ToneCurve =
            [
                new() { X = 0, Y = 0 }, new() { X = left, Y = 0.2 },
                new() { X = right, Y = 0.8 }, new() { X = 1, Y = 1 }
            ];
            var project = Project(clip);
            await export.ExportAsync(project, Path.Combine(directory, $"close-curve-knots-{expectedCount}.mp4"));
            Check(project.Clips[0].ToneCurve.Count == expectedCount,
                "Tone-curve knots did not retain distinct 8-bit positions through normalization and export formatting.");
        }
        Console.WriteLine("PASS: close/adjacent curve knots normalize and retain valid positions during FFmpeg export");
    }

    private static EditorProject Project(params EditorProjectClip[] clips) => new()
    {
        Version = 30,
        CanvasWidth = 320, CanvasHeight = 180,
        Clips = clips.Select((clip, order) => { clip.Order = order; return clip; }).ToList(),
        Settings = new() { ExportEncoder = "software", ExportFrameRate = 30, ExportQuality = "draft" }
    };

    private static EditorProjectClip Clip(string path) => new()
    {
        SourcePath = path, SourceDurationSeconds = 1, TrimEndSeconds = 0.5, Track = EditorClipTrack.Primary
    };

    private static Task<byte[]> FrameAsync(FFmpegLocator locator, string path, double seconds) => RunFfmpegAsync(locator,
        ["-ss", F(seconds), "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);

    private static async Task<byte[]> RunFfmpegAsync(FFmpegLocator locator, string[] arguments)
    {
        var start = new ProcessStartInfo(locator.FFmpegPath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg did not start.");
        var errors = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        var reading = process.StandardOutput.BaseStream.CopyToAsync(output);
        await process.WaitForExitAsync();
        await reading;
        var detail = await errors;
        Check(process.ExitCode == 0, detail.Length > 2000 ? detail[..2000] : detail);
        return output.ToArray();
    }

    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
