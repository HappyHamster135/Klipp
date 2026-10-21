using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;

internal static class VideoProcessingChecks
{
    public static async Task RunAsync(EditorProjectExportService export, FFmpegLocator locator, string directory)
    {
        var derived = Path.Combine(directory, "derived");
        var service = new EditorVideoProcessingService(locator, derived + Path.DirectorySeparatorChar);
        var probe = new MediaProbeService(locator);
        var ramp = new EditorVideoProcessingOptions(true, 1, 2, 1, 0, 30);
        var plan = EditorVideoProcessingService.BuildRampPlan(4, 1, ramp);
        var exact = 4 * Math.Log(2);
        Check(Math.Abs(plan.Sum(part => part.OutputFrames) / 30d - exact) <= 1d / 30,
            "The three-anchor ramp did not integrate rates in source time.");
        Check(Math.Abs(plan.Sum(part => part.SourceDurationSeconds) - 4) < 1e-8 &&
            plan.Zip(plan.Skip(1)).All(pair => Math.Abs(pair.First.SourceOffsetSeconds + pair.First.SourceDurationSeconds - pair.Second.SourceOffsetSeconds) < 1e-8),
            "The ramp skipped or repeated a source interval.");

        var source = Path.Combine(directory, "ramp-source.mp4");
        await Run(["-f", "lavfi", "-i", "color=c=red:s=320x180:r=60:d=5,drawbox=color=blue:t=fill:enable='gte(t,2.5)'",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=5", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", source]);
        var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var shortRejected = false;
        try { await service.ProcessAsync(source, 0, 0.1, 1, ramp with { StartSpeed = 4, MiddleSpeed = 4, EndSpeed = 4 }); }
        catch (ArgumentException) { shortRejected = true; }
        Check(shortRejected, "A sub-minimum 1x bake could silently extend the chosen cut.");
        var processed = await service.ProcessAsync(source, 0.5, 4.5, 1, ramp);
        var info = await probe.GetMediaInfoAsync(processed.SourcePath);
        Check(info is not null && info.AudioCodecs.Count == 0 && info.VideoCodec == "h264" &&
            Math.Abs(info.Duration.TotalSeconds - exact) < 0.04, "The ramp output has the wrong duration or contains audio.");
        var before = await Pixel(processed.SourcePath, 1.25, 160, 90);
        var after = await Pixel(processed.SourcePath, 1.55, 160, 90);
        Check(before[0] > 180 && before[2] < 35 && after[2] > 180 && after[0] < 35,
            "Speed ramp frames do not map to the selected original source range.");
        var cached = await service.ProcessAsync(source, 0.5, 4.5, 1, ramp);
        var afterHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
        Check(cached.CacheHit && cached.SourcePath == processed.SourcePath &&
            originalHash.SequenceEqual(afterHash),
            "Processing did not reuse immutable media or changed the original.");
        using (var cancellation = new CancellationTokenSource())
        {
            var cancelled = false;
            var progress = new InlineProgress<EditorVideoProcessingProgress>(_ => cancellation.Cancel());
            try { await service.ProcessAsync(source, 0, 5, 1, ramp with { StartSpeed = 0.5 }, progress, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && !Directory.EnumerateDirectories(derived, ".processing-*").Any(),
                "Cancelling processing left a job running or temporary media behind.");
        }
        Console.WriteLine("PASS: three-anchor speed ramp duration/source frames, silent immutable cache and cancellation cleanup");

        var still = Path.Combine(directory, "stabilization-pattern.ppm");
        var texture = new byte[352 * 212 * 3];
        for (var y = 0; y < 212; y++) for (var x = 0; x < 352; x++)
        {
            var shade = (byte)(45 + unchecked((uint)(x * 73856093 ^ y * 19349663)) % 150);
            for (var channel = 0; channel < 3; channel++) texture[(y * 352 + x) * 3 + channel] = shade;
        }
        await using (var image = File.Create(still))
        {
            await image.WriteAsync(System.Text.Encoding.ASCII.GetBytes("P6\n352 212\n255\n"));
            await image.WriteAsync(texture);
        }
        var shaky = Path.Combine(directory, "shaky-source.mp4");
        await Run(["-loop", "1", "-framerate", "30", "-i", still, "-t", "2", "-vf",
            "drawbox=x=150:y=80:w=25:h=25:color=0x00ff00:t=fill,crop=320:180:x='16+8*sin(n*1.7)':y='16+6*cos(n*1.3)'",
            "-an", "-c:v", "libx264", "-pix_fmt", "yuv420p", shaky]);
        var stabilized = await service.ProcessAsync(shaky, 0, 2, 1,
            new(false, 1, 1, 1, 1, 30));
        var rawBefore = await Frames(shaky);
        var rawAfter = await Frames(stabilized.SourcePath);
        var motionBefore = GreenMotion(rawBefore);
        var motionAfter = GreenMotion(rawAfter);
        Check(!rawBefore.SequenceEqual(rawAfter) && motionAfter < motionBefore * 0.8,
            $"Stabilization did not reduce the measured camera jitter ({motionBefore:0.00} -> {motionAfter:0.00}).");
        Console.WriteLine($"PASS: real deshake stabilization reduces tracked jitter ({motionBefore:0.00} -> {motionAfter:0.00} pixels/frame)");

        var red = Path.Combine(directory, "polygon-red.mp4");
        var blue = Path.Combine(directory, "polygon-blue-portrait.mp4");
        await Run(["-f", "lavfi", "-i", "color=c=red:s=320x180:r=30:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", red]);
        await Run(["-f", "lavfi", "-i", "color=c=blue:s=180x320:r=30:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", blue]);
        var mask = new EditorProjectClip { SourcePath = blue, SourceDurationSeconds = 1, TrimEndSeconds = 1,
            TrackId = "video-2", SourceAudioSeparated = true, MaskShape = "polygon",
            MaskPolygon = [new() { X = 0.1, Y = 0.1 }, new() { X = 0.9, Y = 0.1 }, new() { X = 0.5, Y = 0.9 }],
            VideoProcessing = new() { OriginalSourcePath = source, BakedSourcePath = processed.SourcePath, IsApplied = true } };
        var project = new EditorProject { CanvasWidth = 320, CanvasHeight = 180,
            Clips = [new() { SourcePath = red, SourceDurationSeconds = 1, TrimEndSeconds = 1, TrackId = "video-1", SourceAudioSeparated = true }, mask],
            Settings = new() { ExportEncoder = "software", ExportFrameRate = 30, ExportQuality = "draft" } };
        foreach (var inverted in new[] { false, true })
        {
            mask.MaskInverted = inverted;
            var path = Path.Combine(directory, $"polygon-portrait-{inverted}.mp4");
            await export.ExportAsync(project, path);
            var inside = await Pixel(path, 0.4, 150, 40);
            var outside = await Pixel(path, 0.4, 120, 140);
            Check(inverted ? inside[0] > 180 && outside[2] > 180 : inside[2] > 180 && outside[0] > 180,
                "Source-space polygon or inversion changed when the portrait video was fitted to canvas.");
        }
        var clone = mask.CloneWithNewId();
        clone.MaskPolygon[0].X = 0.3;
        clone.VideoProcessing!.OriginalTrimStartSeconds = 2;
        Check(mask.MaskPolygon[0].X == 0.1 && mask.VideoProcessing.OriginalTrimStartSeconds == 0,
            "Processing metadata or polygon points were not deep cloned.");
        var saved = JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(project))!;
        saved.Normalize();
        Check(saved.Clips[1].VideoProcessing?.BakedSourcePath == processed.SourcePath && saved.Clips[1].MaskInverted,
            "Processing source references or polygon inversion did not persist.");
        Console.WriteLine("PASS: source-space portrait polygon/inversion, alpha, processing metadata persistence and deep clones");

        async Task<byte[]> Pixel(string path, double time, int x, int y) => await Run(["-ss", F(time), "-i", path,
            "-frames:v", "1", "-vf", $"crop=2:2:{x}:{y}", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]);
        async Task<byte[]> Frames(string path) => await Run(["-i", path, "-an", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]);
        async Task<byte[]> Run(string[] arguments)
        {
            var start = new ProcessStartInfo(locator.FFmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(arguments)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            using var data = new MemoryStream();
            var reading = process.StandardOutput.BaseStream.CopyToAsync(data);
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); await reading;
            Check(process.ExitCode == 0, await error);
            return data.ToArray();
        }
    }

    private static double GreenMotion(byte[] bytes)
    {
        const int frameBytes = 320 * 180 * 3;
        var positions = new List<(double X, double Y)>();
        for (var offset = 0; offset + frameBytes <= bytes.Length; offset += frameBytes)
        {
            var sumX = 0d; var sumY = 0d; var count = 0;
            // A green marker over static grayscale texture isolates camera
            // motion from compression and mirrored edge pixels.
            for (var y = 20; y < 160; y++) for (var x = 20; x < 300; x++)
            {
                var pixel = offset + (y * 320 + x) * 3;
                if (bytes[pixel + 1] > 170 && bytes[pixel] < 50 && bytes[pixel + 2] < 50)
                { sumX += x; sumY += y; count++; }
            }
            if (count > 50) positions.Add((sumX / count, sumY / count));
        }
        Check(positions.Count >= 55, $"The stabilization marker could not be tracked ({positions.Count}/{bytes.Length / frameBytes} frames).");
        return positions.Zip(positions.Skip(1)).Skip(4).Average(pair =>
            Math.Sqrt(Math.Pow(pair.First.X - pair.Second.X, 2) + Math.Pow(pair.First.Y - pair.Second.Y, 2)));
    }
    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T> { public void Report(T value) => callback(value); }
    private static string F(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
