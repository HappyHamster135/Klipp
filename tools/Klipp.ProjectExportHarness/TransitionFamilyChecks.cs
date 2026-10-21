using System.Diagnostics;
using System.Text.Json;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;

internal static class TransitionFamilyChecks
{
    public static async Task RunAsync(string parent)
    {
        var root = Path.Combine(parent, $"transition-families-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var ffmpeg = await new FFmpegLocator().GetFFmpegPathAsync();
        var red = Path.Combine(root, "red.mp4");
        var blue = Path.Combine(root, "blue.mp4");
        foreach (var (color, path) in new[] { ("red", red), ("blue", blue) })
            await RunAsync(ffmpeg, ["-y", "-v", "error", "-f", "lavfi", "-i", $"color={color}:s=320x180:r=30:d=2",
                "-c:v", "libx264", "-preset", "ultrafast", "-crf", "12", "-pix_fmt", "yuv420p", path]);
        var reports = new List<object>();
        foreach (var style in new[] { "circleopen", "rectcrop", "horzopen", "vertopen", "diagtl" })
        {
            var project = new EditorProject
            {
                Name = $"Transition {style}", CanvasWidth = 320, CanvasHeight = 180,
                Settings = new EditorProjectSettings
                {
                    TransitionStyle = style, ExportEncoder = "software", ExportFrameRate = 30,
                    ExportQuality = "draft", ExportResolutionMode = "canvas", ExportWidth = 320,
                    ExportHeight = 180, ExportBitrateMbps = 4
                },
                Clips =
                [
                    new EditorProjectClip { SourcePath = red, Name = "Outgoing", SourceDurationSeconds = 2,
                        TrimEndSeconds = 2, TimelineStartSeconds = 0, TrackId = EditorProject.DefaultPrimaryVideoTrackId,
                        SourceAudioSeparated = true },
                    new EditorProjectClip { SourcePath = blue, Name = "Incoming", SourceDurationSeconds = 2,
                        TrimEndSeconds = 2, TimelineStartSeconds = 1, TrackId = EditorProject.DefaultPrimaryVideoTrackId,
                        TransitionInStyle = style, TransitionInDurationSeconds = 1, SourceAudioSeparated = true }
                ]
            };
            project.Normalize();
            var roundtrip = JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(project))!;
            roundtrip.Normalize();
            if (roundtrip.Settings.TransitionStyle != style || roundtrip.Clips[1].TransitionInStyle != style)
                throw new InvalidOperationException($"{style} was lost during project roundtrip.");
            var output = Path.Combine(root, $"{style}.mp4");
            var cache = new EditorTimelineRenderCacheService(Path.Combine(root, "cache"));
            await new EditorProjectExportService(renderCacheService: cache).ExportAsync(roundtrip, output);
            var raw = await RunAsync(ffmpeg, ["-v", "error", "-i", output, "-an", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"]);
            const int frameBytes = 320 * 180 * 3;
            var count = raw.Length / frameBytes;
            if (count is < 88 or > 92 || raw.Length % frameBytes != 0)
                throw new InvalidOperationException($"{style} rendered an unexpected frame count: {count}.");
            var checkedPixels = 0;
            foreach (var index in new[] { 0, 38, 45, 52, count - 1 })
            {
                var p = Math.Clamp(index / 30d - 1, 0, 1);
                // Frame rounding and 4:2:0 edges may move a frontier by one frame.
                // Check only pixels whose expected owner is stable across that band.
                var low = EditorTransitionAuditionMath.Evaluate(style, Math.Max(0, p - 1d / 30));
                var high = EditorTransitionAuditionMath.Evaluate(style, Math.Min(1, p + 1d / 30));
                for (var y = 2; y < 178; y += 3)
                for (var x = 2; x < 318; x += 3)
                {
                    var before = EditorTransitionAuditionMath.IsIncomingVisible(low, x + .5, y + .5, 320, 180);
                    var after = EditorTransitionAuditionMath.IsIncomingVisible(high, x + .5, y + .5, 320, 180);
                    if (before != after) continue;
                    var offset = index * frameBytes + (y * 320 + x) * 3;
                    var isBlue = raw[offset + 2] > raw[offset] + 40;
                    var isRed = raw[offset] > raw[offset + 2] + 40;
                    if (!(before ? isBlue : isRed))
                        throw new InvalidOperationException($"{style} wrong owner at frame {index}, ({x},{y}), p={p:0.000}: RGB {raw[offset]},{raw[offset + 1]},{raw[offset + 2]}.");
                    checkedPixels++;
                }
            }
            reports.Add(new { style, frames = count, checkedPixels, output });
            Console.WriteLine($"PASS {style}: roundtrip, actual export, {count} frames, {checkedPixels} stable mask pixels");
        }
        var report = Path.Combine(root, "report.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(report);
    }

    private static async Task<byte[]> RunAsync(string ffmpeg, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        using var memory = new MemoryStream();
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(memory);
        var stderr = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdout, process.WaitForExitAsync());
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return memory.ToArray();
    }
}
