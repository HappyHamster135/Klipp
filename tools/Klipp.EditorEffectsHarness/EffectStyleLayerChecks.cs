using System.Diagnostics;
using System.Globalization;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;

internal static class EffectStyleLayerChecks
{
    public static async Task RunAsync(EditorProjectExportService export, FFmpegLocator locator, string directory)
    {
        var checks = 0;
        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
            checks++;
        }
        var source = Path.Combine(directory, "fx-style-red.mp4");
        await Run(["-f", "lavfi", "-i", "color=c=red:s=320x180:r=30:d=1", "-c:v", "libx264", "-pix_fmt", "yuv420p", source]);
        var clip = new EditorProjectClip
        {
            SourcePath = source, SourceDurationSeconds = 1, TrimEndSeconds = 1,
            TrackId = "video-1", SourceAudioSeparated = true
        };
        var style = new EditorProjectClip
        {
            Filter = "none", Saturation = 0, Brightness = .2, FilterStrength = .4,
            FilterStrengthKeyframes = [new() { TimeSeconds = .5, Strength = .1 }],
            ToneCurve = [new() { X = 0, Y = 0 }, new() { X = 1, Y = 1 }]
        };
        var fx = new EditorProjectEffectLayer
        {
            TargetClipId = clip.Id, Name = "Saved grade", Filter = "none", Style = style,
            Amount = 1, StartSeconds = .25, EndSeconds = .75, RegionWidth = .5
        };
        var project = new EditorProject
        {
            Name = "Custom FX style", CanvasWidth = 320, CanvasHeight = 180,
            Clips = [clip], EffectLayers = [fx],
            Settings = new() { ExportEncoder = "software", ExportFrameRate = 30, ExportQuality = "draft" }
        };
        project.Normalize();
        Check(project.EffectLayers.Count == 1 && fx.IsActiveAt(.5) && style.FilterStrength == 1 &&
              style.FilterStrengthKeyframes.Count == 0, "Custom saved grades did not normalize into static active FX.");
        var copy = fx.Clone();
        Check(copy.Style!.Id == style.Id && !ReferenceEquals(copy.Style, style) &&
              !ReferenceEquals(copy.Style.Effects[0], style.Effects[0]) && !ReferenceEquals(copy.Style.ToneCurve[0], style.ToneCurve[0]),
            "FX style clone changed identity or shared mutable grade data.");
        var graded = Path.Combine(directory, "fx-style-grade.mp4");
        await export.ExportAsync(project, graded);
        Check(IsGray(await Pixel(graded, .5, 80)) && IsRed(await Pixel(graded, .5, 240)) &&
              IsRed(await Pixel(graded, .1, 80)), "Saved custom grade did not honor region/time while preserving unaffected video.");

        var lut = Path.Combine(directory, "fx-style-invert.cube");
        var rows = new List<string> { "LUT_3D_SIZE 2" };
        for (var b = 0; b < 2; b++) for (var g = 0; g < 2; g++) for (var r = 0; r < 2; r++)
            rows.Add($"{1-r} {1-g} {1-b}");
        await File.WriteAllLinesAsync(lut, rows);
        style.Saturation = 1;
        style.Brightness = 0;
        style.LutPath = lut;
        var mapped = Path.Combine(directory, "fx-style-lut.mp4");
        await export.ExportAsync(project, mapped);
        var cyan = await Pixel(mapped, .5, 80);
        Check(cyan[0] < 40 && cyan[1] > 180 && cyan[2] > 180 && IsRed(await Pixel(mapped, .5, 240)),
            "Custom FX LUT was not blended inside its region.");

        var projects = Path.Combine(directory, "style-projects");
        var files = new EditorProjectService(projects);
        var saved = Path.Combine(projects, "custom.klippproject");
        await files.SaveAsync(project, saved);
        var reopened = await files.TryLoadAsync(saved);
        Check(reopened!.EffectLayers[0].Style!.LutPath == lut && reopened.EffectLayers[0].Style!.FilterStrength == 1,
            "Saved project did not restore its custom FX LUT snapshot.");
        Check((await files.GetReferencedMediaPathsAsync()).Contains(lut), "Custom FX LUT was omitted from referenced media protection.");
        var collector = new EditorProjectFilesService();
        var collected = await collector.CollectAsync(project, directory);
        var portable = await files.TryLoadAsync(collected.ProjectPath);
        var portableLut = portable!.EffectLayers[0].Style!.LutPath;
        Check(collected.FileCount == 2 && portableLut != lut && File.Exists(portableLut) &&
              await File.ReadAllTextAsync(portableLut) == await File.ReadAllTextAsync(lut),
            "Portable collection did not copy and resolve a custom FX LUT.");

        var cache = new EditorTimelineRenderCacheService(Path.Combine(directory, "style-fingerprints"));
        var before = cache.BuildSegments(project, "libx264")[0].CacheKey;
        await File.AppendAllTextAsync(lut, "\n# LUT edited in place\n");
        Check(cache.BuildSegments(project, "libx264")[0].CacheKey != before, "Editing a custom FX LUT file did not invalidate rendered cache.");
        style.LutPath = Path.Combine(directory, "not-there.cube");
        var missing = false;
        try { await export.ExportAsync(project, Path.Combine(directory, "fx-style-missing.mp4")); }
        catch (InvalidOperationException exception) { missing = exception.Message.Contains("Saved grade LUT", StringComparison.Ordinal); }
        Check(missing, "Export did not identify a missing custom FX LUT.");
        var collectMissing = false;
        try { await collector.CollectAsync(project, directory); }
        catch (FileNotFoundException) { collectMissing = true; }
        Check(collectMissing, "Portable collection did not reject a missing custom FX LUT.");
        Console.WriteLine($"PASS: {checks} custom FX style/LUT checks (static grade, identity/deep clone, regional export, project save, protected media, portable collection, cache and missing files)");

        async Task<byte[]> Pixel(string path, double seconds, int x) => await Run([
            "-ss", seconds.ToString(CultureInfo.InvariantCulture), "-i", path, "-frames:v", "1", "-vf", $"format=rgb24,crop=1:1:{x}:90",
            "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]);
        async Task<byte[]> Run(string[] arguments)
        {
            var info = new ProcessStartInfo(locator.FFmpegPath)
            {
                RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(arguments)) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            using var output = new MemoryStream();
            var reading = process.StandardOutput.BaseStream.CopyToAsync(output);
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); await reading;
            if (process.ExitCode != 0) throw new InvalidOperationException(await errors);
            return output.ToArray();
        }
    }

    private static bool IsRed(byte[] pixel) => pixel.Length == 3 && pixel[0] > 180 && pixel[1] < 40 && pixel[2] < 40;
    private static bool IsGray(byte[] pixel) => pixel.Length == 3 && pixel[0] > 30 &&
        Math.Abs(pixel[0] - pixel[1]) < 8 && Math.Abs(pixel[1] - pixel[2]) < 8;
}
