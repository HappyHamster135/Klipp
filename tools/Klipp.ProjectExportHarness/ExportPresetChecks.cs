using System.Diagnostics;
using System.Text.Json;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;

internal static class ExportPresetChecks
{
    internal static async Task RunAsync(string parent)
    {
        var root = Path.Combine(Path.GetFullPath(parent), "export-presets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "synthetic-source.mp4");
        var output = Path.Combine(root, "smaller-file.mp4");
        var locator = new FFmpegLocator();
        var ffmpeg = await locator.GetFFmpegPathAsync();
        await RunProcessAsync(ffmpeg,
        [
            "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30:duration=6,noise=alls=40:allf=t",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=6",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-pix_fmt", "yuv420p", "-c:a", "aac", source
        ]);
        var project = new EditorProject
        {
            Name = "Export preset check", CanvasWidth = 1920, CanvasHeight = 1080,
            Clips = [new EditorProjectClip { SourcePath = source, SourceDurationSeconds = 6, TrimEndSeconds = 6, Name = "Synthetic clip" }]
        };
        var small = EditorExportPresetMath.Presets.Single(preset => preset.Id == "small");
        var settings = EditorExportPresetMath.Resolve(small, project.CanvasWidth, project.CanvasHeight);
        project.Settings.ExportResolutionMode = settings.ResolutionMode;
        project.Settings.ExportWidth = settings.Width;
        project.Settings.ExportHeight = settings.Height;
        project.Settings.ExportFrameRate = settings.FrameRate;
        project.Settings.ExportQuality = settings.Quality;
        project.Settings.ExportBitrateMbps = settings.VideoBitrateMbps;
        project.Settings.ExportAudioBitrateKbps = settings.AudioBitrateKbps;
        project.Settings.ExportEncoder = "software";
        project.Normalize();
        var cloned = EditorProjectService.Clone(project);
        Require(cloned.Settings.ExportResolutionMode == "720p" && cloned.OutputWidth == 1280 && cloned.OutputHeight == 720,
            "Saving/normalizing a smaller-file preset discarded its output resolution.");
        var estimate = EditorExportPresetMath.EstimateBytes(project.DurationSeconds, settings.VideoBitrateMbps, settings.AudioBitrateKbps);
        var exporter = new EditorProjectExportService(locator,
            new EditorTimelineRenderCacheService(Path.Combine(root, "render-cache"), new MediaProbeService(locator)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await exporter.ExportAsync(cloned, output, timeout.Token);
        var info = await new MediaProbeService(locator).GetMediaInfoAsync(output, timeout.Token)
            ?? throw new InvalidOperationException("Exported preset output could not be read.");
        Require(info.Width == 1280 && info.Height == 720 && info.VideoCodec == "h264" && info.AudioCodecs.Contains("aac"),
            "Preset export did not preserve its chosen resolution and playback-compatible video/audio.");
        Require(Math.Abs(info.Duration.TotalSeconds - 6) < .15, "Preset export changed the edit duration.");
        var probe = await locator.GetFFprobePathAsync();
        var frameRateJson = await RunProcessAsync(probe,
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=r_frame_rate", "-of", "json", output]);
        using var document = JsonDocument.Parse(frameRateJson);
        Require(document.RootElement.GetProperty("streams")[0].GetProperty("r_frame_rate").GetString() == "30/1",
            "The smaller-file preset did not reach the encoder at 30 fps.");
        var bytes = new FileInfo(output).Length;
        var ratio = bytes / (double)estimate;
        // Short one-pass ABR clips vary with encoder startup and picture detail.
        // This fixture is deliberately busy; the estimate must remain useful,
        // while the UI explicitly avoids promising an exact upload-size limit.
        Require(ratio is > .35 and < 1.5, "The export-size estimate was not useful for the synthetic ABR fixture.");
        Console.WriteLine(FormattableString.Invariant($"PASS: smaller preset survives clone/normalize and exports 1280x720 H264/AAC at 30 fps; actual={bytes} estimated={estimate} ratio={ratio:0.00}"));
        Console.WriteLine(root);
    }

    private static async Task<string> RunProcessAsync(string file, string[] arguments)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start export check.");
        var error = process.StandardError.ReadToEndAsync();
        var text = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        var errorText = await error;
        if (process.ExitCode != 0) throw new InvalidOperationException(errorText);
        return await text;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
