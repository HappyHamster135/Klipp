using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;

internal static class EffectLayerChecks
{
    public static async Task RunAsync(EditorProjectExportService export, FFmpegLocator locator, string directory)
    {
        var checks = 0;
        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
            checks++;
        }
        var red = Path.Combine(directory, "fx-region-red.mp4");
        var blue = Path.Combine(directory, "fx-region-blue.mp4");
        await Source(red, "red");
        await Source(blue, "blue");
        var clip = Clip(red, "video-1", 1, 2);
        clip.TrimStartSeconds = 2;
        clip.TrimEndSeconds = 6;
        clip.PlaybackSpeed = 2;
        var project = Project(clip);
        var layer = new EditorProjectEffectLayer
        {
            TargetClipId = clip.Id, Name = "Left monochrome", Filter = "mono",
            StartSeconds = 1.5, EndSeconds = 2.5, RegionWidth = 0.5
        };
        project.EffectLayers.Add(layer);
        project.Normalize();
        Check(!layer.IsActiveAt(1.49) && layer.IsActiveAt(1.5) && !layer.IsActiveAt(2.5),
            "FX windows must include their start and exclude their end.");
        var clone = EditorProjectService.Clone(project);
        Check(clone.EffectLayers.Count == 1 && clone.EffectLayers[0].TargetClipId == clip.Id &&
              clone.EffectLayers[0].RegionWidth == 0.5 && !ReferenceEquals(layer, clone.EffectLayers[0]),
            "Timed FX did not deep-copy through project serialization.");
        var savePath = Path.Combine(directory, "fx-region-project.klippproject");
        var files = new EditorProjectService(Path.Combine(directory, "projects"));
        await files.SaveAsync(project, savePath);
        var restored = await files.TryLoadAsync(savePath);
        Check(restored!.EffectLayers[0].Filter == "mono" && restored.EffectLayers[0].StartSeconds == 1.5 &&
              restored.EffectLayers[0].EndSeconds == 2.5, "Saved FX time/region did not survive reopen.");
        var path = Path.Combine(directory, "fx-region-timed.mp4");
        await export.ExportAsync(project, path);
        Check(IsRed(await Pixel(path, 1.25, 80, 90)), "FX appeared before its start on a moved, retimed clip.");
        Check(IsGray(await Pixel(path, 1.75, 80, 90)), "Active left-side FX was not applied.");
        Check(IsRed(await Pixel(path, 1.75, 240, 90)), "FX changed pixels outside its selected region.");
        Check(IsRed(await Pixel(path, 2.5, 80, 90)), "FX remained active at its exclusive out-point.");

        layer.RegionShape = "ellipse";
        layer.RegionX = 0.25;
        layer.RegionY = 0.1;
        layer.RegionWidth = 0.5;
        layer.RegionHeight = 0.8;
        var ellipse = Path.Combine(directory, "fx-region-ellipse.mp4");
        await export.ExportAsync(project, ellipse);
        Check(IsGray(await Pixel(ellipse, 2, 160, 90)), "Ellipse FX did not affect its center.");
        Check(IsRed(await Pixel(ellipse, 2, 88, 26)), "Ellipse FX changed its bounding-box corner.");

        layer.RegionShape = "rectangle";
        layer.RegionX = layer.RegionY = 0;
        layer.RegionWidth = layer.RegionHeight = 1;
        var amounts = new List<byte[]>();
        foreach (var amount in new[] { 0d, 0.5, 1d })
        {
            layer.Amount = amount;
            var amountPath = Path.Combine(directory, $"fx-region-amount-{F(amount)}.mp4");
            await export.ExportAsync(project, amountPath);
            amounts.Add(await Pixel(amountPath, 2, 160, 90));
        }
        Check(IsRed(amounts[0]) && IsGray(amounts[2]) && amounts[1][0] < amounts[0][0] - 30 &&
              amounts[1][0] > amounts[2][0] + 30 && amounts[1][1] > amounts[0][1] + 10,
            "FX amount did not interpolate original and filtered RGB pixels at 0/50/100%.");
        layer.Amount = 1;
        clip.FilterStrength = 0;
        var independent = Path.Combine(directory, "fx-region-independent-amount.mp4");
        await export.ExportAsync(project, independent);
        Check(IsGray(await Pixel(independent, 2, 160, 90)), "Clip grade amount incorrectly bypassed independent timeline FX.");
        clip.FilterStrength = 1;
        layer.Enabled = false;
        var disabled = Path.Combine(directory, "fx-region-disabled.mp4");
        await export.ExportAsync(project, disabled);
        Check(IsRed(await Pixel(disabled, 2, 160, 90)), "Disabled FX still changed the picture.");
        layer.Enabled = true;

        var lower = Clip(blue, "video-1", 0, 3);
        var upper = Clip(red, "video-2", 0, 3);
        upper.Scale = 0.5;
        upper.OffsetX = 0.5;
        upper.CropLeft = 0.25;
        var overlayProject = Project(lower, upper);
        overlayProject.EffectLayers = [new()
        {
            TargetClipId = upper.Id, Filter = "mono", StartSeconds = 0, EndSeconds = 3, RegionWidth = 0.5
        }];
        var overlay = Path.Combine(directory, "fx-region-transformed-overlay.mp4");
        await export.ExportAsync(overlayProject, overlay);
        Check(IsGray(await Pixel(overlay, 1, 160, 90)) && IsRed(await Pixel(overlay, 1, 230, 90)),
            "FX region did not follow the cropped and positioned source frame.");
        Check(IsBlue(await Pixel(overlay, 1, 40, 90)), "FX destroyed upper-row alpha outside the picture-in-picture.");

        var ordered = Project(Clip(red, "video-1", 0, 1));
        ordered.EffectLayers = [new()
        {
            TargetClipId = ordered.Clips[0].Id, Filter = "warm", StartSeconds = 0, EndSeconds = 1
        }, new()
        {
            TargetClipId = ordered.Clips[0].Id, Filter = "mono", StartSeconds = 0, EndSeconds = 1
        }];
        var orderedPath = Path.Combine(directory, "fx-region-ordered.mp4");
        await export.ExportAsync(ordered, orderedPath);
        Check(IsGray(await Pixel(orderedPath, 0.5, 160, 90)), "Sequential FX did not preserve the layer order.");
        ordered.EffectLayers.Reverse();
        var reversePath = Path.Combine(directory, "fx-region-reversed.mp4");
        await export.ExportAsync(ordered, reversePath);
        var reversed = await Pixel(reversePath, 0.5, 160, 90);
        Check(reversed[0] > reversed[2] + 4, $"Reversing FX order did not alter its output: {string.Join(',', reversed)}.");

        var longClip = Clip(red, "video-1", 0, 10);
        var longProject = Project(longClip);
        var late = new EditorProjectEffectLayer
        {
            TargetClipId = longClip.Id, Filter = "mono", StartSeconds = 8.2, EndSeconds = 8.8, RegionWidth = 0.5
        };
        var cache = new EditorTimelineRenderCacheService(Path.Combine(directory, "fx-cache-fingerprints"));
        var noFxKeys = cache.BuildSegments(longProject, "libx264");
        longProject.EffectLayers.Add(late);
        var keys = cache.BuildSegments(longProject, "libx264");
        Check(noFxKeys[0].CacheKey != keys[0].CacheKey && noFxKeys[1].CacheKey != keys[1].CacheKey,
            "Adding the first source-space FX retained cache rendered with a different grade order.");
        late.RegionWidth = 0.6;
        var editedKeys = cache.BuildSegments(longProject, "libx264");
        Check(keys[0].CacheKey == editedKeys[0].CacheKey && keys[1].CacheKey != editedKeys[1].CacheKey,
            "A regional FX edit did not invalidate only its intersecting cache segment.");
        var latePath = Path.Combine(directory, "fx-region-late-segment.mp4");
        await export.ExportAsync(longProject, latePath);
        Check(IsRed(await Pixel(latePath, 8.1, 80, 90)) && IsGray(await Pixel(latePath, 8.4, 80, 90)) &&
              IsRed(await Pixel(latePath, 8.9, 80, 90)), "Cached segment offset changed the FX window.");

        var invalid = new EditorProjectEffectLayer
        {
            TargetClipId = clip.Id, Filter = "mono", Amount = double.NaN, StartSeconds = -50,
            EndSeconds = double.PositiveInfinity, RegionX = 2, RegionY = -1,
            RegionWidth = 0, RegionHeight = double.NaN, RegionShape = "unknown"
        };
        invalid.Normalize(clip, project.DurationSeconds);
        Check(invalid.StartSeconds == 1 && invalid.EndSeconds == 3 && invalid.Amount == 1 &&
              invalid.RegionWidth == 0.02 && invalid.RegionX == 0.98 && invalid.RegionY == 0 &&
              invalid.RegionHeight == 1 && invalid.RegionShape == "rectangle", "Invalid FX values were not safely normalized.");
        var fractionalTarget = Clip(red, "video-1", 0, 0.3);
        var fractionalEffect = new EditorProjectEffectLayer { Filter = "mono", StartSeconds = 0.3, EndSeconds = 0.3 };
        fractionalEffect.Normalize(fractionalTarget, 0.3);
        Check(fractionalEffect.EndSeconds == 0.3 && fractionalEffect.StartSeconds == 0.3,
            "FX wholly outside a source trim was stretched onto previously unaffected frames.");
        fractionalEffect.StartSeconds = 0.28;
        fractionalEffect.Normalize(fractionalTarget, 0.3);
        Check(fractionalEffect.StartSeconds == 0.28 && fractionalEffect.EndSeconds == 0.3,
            "A tiny valid FX intersection was stretched beyond its original window.");
        project.EffectLayers.Add(new() { TargetClipId = "deleted", Filter = "mono" });
        project.EffectLayers.Add(new() { TargetClipId = clip.Id, Filter = "invalid-filter" });
        project.EffectLayers.Add(new() { TargetClipId = clip.Id, Filter = "mono", StartSeconds = 4, EndSeconds = 5 });
        project.Normalize();
        Check(project.EffectLayers.Count == 1, "Deleted-target or unknown FX survived normalization.");
        foreach (var look in new[]
        {
            "natural", "film", "matte", "goldenhour", "steel", "boost", "cinematic", "tealorange",
            "warm", "cool", "mono", "noir", "vintage", "vibrant", "soft", "dream", "sharpen",
            "blur", "vignette", "glitch"
        })
        {
            var presetProject = Project(Clip(red, "video-1", 0, 0.4));
            presetProject.EffectLayers = [new()
            {
                TargetClipId = presetProject.Clips[0].Id, Filter = look, StartSeconds = 0,
                EndSeconds = 0.4, RegionWidth = 0.5, Amount = 0.65
            }];
            var presetPath = Path.Combine(directory, $"fx-region-preset-{look}.mp4");
            await export.ExportAsync(presetProject, presetPath);
            Check(IsRed(await Pixel(presetPath, 0.2, 240, 90)), $"{look} failed regional export or changed pixels outside its region.");
        }
        Console.WriteLine($"PASS: {checks} timed and regional FX checks (rectangle/ellipse, timing, retiming, RGB amount, alpha, order, saved projects and segmented cache)");

        async Task Source(string sourcePath, string color) => await Run([
            "-f", "lavfi", "-i", $"color=c={color}:s=320x180:r=30:d=12", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", sourcePath]);
        async Task<byte[]> Pixel(string videoPath, double seconds, int x, int y) => await Run([
            "-ss", F(seconds), "-i", videoPath, "-frames:v", "1", "-vf", $"format=rgb24,crop=1:1:{x}:{y}", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]);
        async Task<byte[]> Run(string[] arguments)
        {
            var start = new ProcessStartInfo(locator.FFmpegPath)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(arguments)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            using var output = new MemoryStream();
            var reading = process.StandardOutput.BaseStream.CopyToAsync(output);
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await reading;
            if (process.ExitCode != 0) throw new InvalidOperationException(await errors);
            return output.ToArray();
        }
    }

    private static bool IsRed(byte[] pixel) => pixel.Length == 3 && pixel[0] > 180 && pixel[1] < 40 && pixel[2] < 40;
    private static bool IsBlue(byte[] pixel) => pixel.Length == 3 && pixel[2] > 180 && pixel[0] < 40 && pixel[1] < 40;
    private static bool IsGray(byte[] pixel) => pixel.Length == 3 && pixel[0] > 30 &&
        Math.Abs(pixel[0] - pixel[1]) < 8 && Math.Abs(pixel[1] - pixel[2]) < 8;
    private static EditorProject Project(params EditorProjectClip[] clips) => new()
    {
        CanvasWidth = 320, CanvasHeight = 180, Clips = clips.ToList(),
        Settings = new() { ExportEncoder = "software", ExportFrameRate = 30, ExportQuality = "draft" }
    };
    private static EditorProjectClip Clip(string path, string trackId, double start, double duration) => new()
    {
        SourcePath = path, SourceDurationSeconds = 12, TrimEndSeconds = duration,
        TimelineStartSeconds = start, TrackId = trackId, SourceAudioSeparated = true
    };
    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
