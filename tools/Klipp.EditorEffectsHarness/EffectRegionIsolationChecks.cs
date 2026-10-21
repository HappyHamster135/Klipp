using System.Diagnostics;
using System.Globalization;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;

internal static class EffectRegionIsolationChecks
{
    public static async Task RunAsync(EditorProjectExportService export, FFmpegLocator locator, string directory)
    {
        var source = Path.Combine(directory, "fx-isolation-pattern.mkv");
        await Run(["-f", "lavfi", "-i", "testsrc2=s=320x180:r=30:d=0.4,drawbox=x=8:y=8:w=50:h=40:color=black:t=fill,drawbox=x=260:y=8:w=50:h=40:color=gray:t=fill",
            "-c:v", "ffv1", "-pix_fmt", "bgra", source]);
        var clip = Clip(source);
        var project = Project(clip);
        var baseline = await ExportFrame(project, "baseline");
        var baselineEarly = await Frame(Path.Combine(directory, "baseline.mp4"), 0);
        var checks = 0;
        foreach (var shape in new[] { "rectangle", "ellipse" })
        foreach (var amount in new[] { 0d, .25, .65, 1d })
        {
            var fx = new EditorProjectEffectLayer
            {
                TargetClipId = clip.Id, Filter = "glitch", Amount = amount,
                StartSeconds = .1, EndSeconds = .3, RegionShape = shape,
                RegionX = .3, RegionY = .2, RegionWidth = .4, RegionHeight = .6
            };
            project.EffectLayers = [fx];
            var actual = await ExportFrame(project, $"{shape}-{F(amount)}");
            var stats = CompareOutside(baseline, actual, fx, x => x, y => y);
            Check(stats.Maximum <= 12 && stats.Mean <= 1.8,
                $"Outside FX changed for {shape} amount {amount}: max={stats.Maximum}, mean={stats.Mean:0.###}, gray={stats.GrayPixels}/{stats.Pixels}");
            var allUnchanged = amount == 0 ? CompareAll(baseline, actual) : default;
            if (amount == 0) Check(allUnchanged.Maximum <= 12 && allUnchanged.Mean <= 1.8, "Zero-amount FX changed the frame.");
            var outsideTime = await Frame(Path.Combine(directory, $"{shape}-{F(amount)}.mp4"), 0);
            var temporal = CompareAll(baselineEarly, outsideTime);
            Check(temporal.Maximum <= 12 && temporal.Mean <= 1.8,
                $"Inactive FX changed frame for {shape}: max={temporal.Maximum} mean={temporal.Mean:0.###}");
            Console.WriteLine($"PASS outside: {shape} amount {amount:0.##}, {stats.Pixels} pixels, max {stats.Maximum}, mean {stats.Mean:0.###}, newly gray {stats.GrayPixels}");
        }

        var blue = Path.Combine(directory, "fx-isolation-blue.mkv");
        await Run(["-f", "lavfi", "-i", "color=blue:s=320x180:r=30:d=0.4", "-c:v", "ffv1", blue]);
        var alpha = Path.Combine(directory, "fx-isolation-alpha.mkv");
        await Run(["-i", source, "-vf", "format=rgba,geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='255*(0.3+0.7*X/W)'", "-c:v", "ffv1", "-pix_fmt", "bgra", alpha]);
        var lower = Clip(blue);
        var upper = Clip(alpha);
        upper.TrackId = "video-2";
        upper.Track = EditorClipTrack.Overlay;
        upper.Scale = .6;
        upper.OffsetX = .4;
        upper.OffsetY = -.25;
        upper.CropLeft = upper.CropRight = .125;
        upper.CropTop = upper.CropBottom = .1;
        var overlay = Project(lower, upper);
        var overlayBaseline = await ExportFrame(overlay, "overlay-baseline");
        var overlayFx = new EditorProjectEffectLayer
        {
            TargetClipId = upper.Id, Filter = "glitch", Amount = .65,
            StartSeconds = 0, EndSeconds = .4, RegionShape = "ellipse",
            RegionX = .3, RegionY = .2, RegionWidth = .4, RegionHeight = .6
        };
        overlay.EffectLayers = [overlayFx];
        var overlayActual = await ExportFrame(overlay, "overlay-ellipse-alpha");
        // Source crop 240x144 fits to 300x180 plus a10px sidepad, then
        // scales to60% andpositions at(89.6,27). Undo that geometry.
        var overlayStats = CompareOutside(overlayBaseline, overlayActual, overlayFx,
            x => ((x - 89.6) / .6 - 10) / 1.25 + 40,
            y => ((y - 27) / .6) / 1.25 + 18);
        Check(overlayStats.Maximum <= 16 && overlayStats.Mean <= 2.1,
            $"Cropped alpha B-roll changed outside FX: max={overlayStats.Maximum}, mean={overlayStats.Mean:0.###}, gray={overlayStats.GrayPixels}");
        Console.WriteLine($"PASS outside: cropped positioned alpha B-roll, {overlayStats.Pixels} pixels, max {overlayStats.Maximum}, mean {overlayStats.Mean:0.###}");

        // Diagnostic for the legacy canvas-space grade path. This is separate
        // from the regional blend: compare with the first FX disabled entirely.
        overlay.EffectLayers.Clear();
        upper.Brightness = .12;
        upper.Vignette = .6;
        var legacyGradeBaseline = await ExportFrame(overlay, "legacy-grade-baseline");
        overlayFx.Enabled = false;
        overlay.EffectLayers = [overlayFx];
        var withDisabledFx = await ExportFrame(overlay, "legacy-grade-disabled-fx");
        var gradeDifference = CompareAll(legacyGradeBaseline, withDisabledFx);
        Console.WriteLine($"DIAGNOSTIC legacy canvas/source grade order: disabled FX max {gradeDifference.Maximum}, mean {gradeDifference.Mean:0.###}");

        Console.WriteLine($"PASS: {checks} full-frame region isolation checks");

        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        async Task<byte[]> ExportFrame(EditorProject current, string name)
        {
            var path = Path.Combine(directory, name + ".mp4");
            await export.ExportAsync(current, path);
            return await Frame(path, .2);
        }
        async Task<byte[]> Frame(string path, double seconds) => await Run(["-ss", F(seconds), "-i", path,
            "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]);
        async Task<byte[]> Run(string[] arguments)
        {
            var start = new ProcessStartInfo(locator.FFmpegPath)
            {
                RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(arguments)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            using var output = new MemoryStream();
            var reading = process.StandardOutput.BaseStream.CopyToAsync(output);
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); await reading;
            if (process.ExitCode != 0) throw new InvalidOperationException(await errors);
            return output.ToArray();
        }
    }

    private static (int Maximum, double Mean, int Pixels, int GrayPixels) CompareOutside(byte[] baseline, byte[] actual,
        EditorProjectEffectLayer fx, Func<double, double> sourceX, Func<double, double> sourceY)
    {
        var maximum = 0; long sum = 0; var pixels = 0; var grayPixels = 0;
        for (var y = 0; y < 180; y++) for (var x = 0; x < 320; x++)
        {
            var sx = sourceX(x + .5) / 320; var sy = sourceY(y + .5) / 180;
            var near = fx.RegionShape == "ellipse"
                ? Math.Pow((sx - fx.RegionX - fx.RegionWidth / 2) / (fx.RegionWidth / 2), 2) +
                  Math.Pow((sy - fx.RegionY - fx.RegionHeight / 2) / (fx.RegionHeight / 2), 2) < 1.25
                : sx >= fx.RegionX - .025 && sx <= fx.RegionX + fx.RegionWidth + .025 &&
                  sy >= fx.RegionY - .025 && sy <= fx.RegionY + fx.RegionHeight + .025;
            if (near) continue;
            var offset = (y * 320 + x) * 3;
            for (var channel = 0; channel < 3; channel++)
            {
                var difference = Math.Abs(baseline[offset + channel] - actual[offset + channel]);
                maximum = Math.Max(maximum, difference); sum += difference;
            }
            var wasColor = Math.Max(baseline[offset], Math.Max(baseline[offset + 1], baseline[offset + 2])) -
                Math.Min(baseline[offset], Math.Min(baseline[offset + 1], baseline[offset + 2])) > 30;
            var nowGray = Math.Max(actual[offset], Math.Max(actual[offset + 1], actual[offset + 2])) -
                Math.Min(actual[offset], Math.Min(actual[offset + 1], actual[offset + 2])) < 8;
            if (wasColor && nowGray) grayPixels++;
            pixels++;
        }
        return (maximum, (double)sum / Math.Max(1, pixels * 3), pixels, grayPixels);
    }

    private static (int Maximum, double Mean) CompareAll(byte[] baseline, byte[] actual)
    {
        var maximum = 0; long sum = 0;
        for (var index = 0; index < baseline.Length; index++)
        {
            var difference = Math.Abs(baseline[index] - actual[index]);
            maximum = Math.Max(maximum, difference); sum += difference;
        }
        return (maximum, (double)sum / baseline.Length);
    }

    private static EditorProject Project(params EditorProjectClip[] clips) => new()
    {
        CanvasWidth = 320, CanvasHeight = 180, Clips = clips.ToList(),
        Settings = new() { ExportEncoder = "software", ExportFrameRate = 30, ExportQuality = "draft" }
    };
    private static EditorProjectClip Clip(string source) => new()
    {
        SourcePath = source, SourceDurationSeconds = .4, TrimEndSeconds = .4, TrackId = "video-1", SourceAudioSeparated = true
    };
    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
