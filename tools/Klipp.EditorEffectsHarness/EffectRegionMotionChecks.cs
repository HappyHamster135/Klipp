using System.Diagnostics;
using System.Globalization;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;

internal static class EffectRegionMotionChecks
{
    public static async Task RunAsync(EditorProjectExportService export, FFmpegLocator locator, string directory)
    {
        var checks = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); checks++; }
        var source = Path.Combine(directory, "fx-motion-red.mp4");
        await Run(["-f", "lavfi", "-i", "color=c=red:s=320x180:r=30:d=8", "-c:v", "libx264", "-pix_fmt", "yuv420p", source]);
        var clip = new EditorProjectClip
        {
            SourcePath = source, SourceDurationSeconds = 8, TrimStartSeconds = 2, TrimEndSeconds = 6,
            PlaybackSpeed = 2, TimelineStartSeconds = .5, TrackId = "video-1", SourceAudioSeparated = true
        };
        var project = new EditorProject
        {
            CanvasWidth = 320, CanvasHeight = 180, Clips = [clip],
            Settings = new() { ExportEncoder = "software", ExportFrameRate = 30, ExportQuality = "draft" }
        };
        var baselinePath = Path.Combine(directory, "fx-motion-baseline.mp4");
        await export.ExportAsync(project, baselinePath);
        var baseline = await Frame(baselinePath, 1);
        var full = new EditorProjectEffectLayer { TargetClipId = clip.Id, Filter = "mono", StartSeconds = .5, EndSeconds = 2.5 };
        project.EffectLayers = [full];
        var fullPath = Path.Combine(directory, "fx-motion-full.mp4");
        await export.ExportAsync(project, fullPath);
        var filtered = await Frame(fullPath, 1);
        foreach (var shape in new[] { "rectangle", "ellipse" })
        foreach (var feather in new[] { 0d, .25, .6, 1d })
        {
            var effect = new EditorProjectEffectLayer
            {
                TargetClipId = clip.Id, Filter = "mono", StartSeconds = .7, EndSeconds = 2.3,
                RegionShape = shape, RegionFeather = feather, RegionX = .2, RegionY = .2, RegionWidth = .6, RegionHeight = .6
            };
            project.EffectLayers = [effect];
            var path = Path.Combine(directory, $"fx-feather-{shape}-{F(feather)}.mp4");
            await export.ExportAsync(project, path);
            AssertCoverage(effect, 1, await Frame(path, 1), baseline, filtered);
        }
        foreach (var easing in new[] { "linear", "ease-in", "ease-out", "ease-in-out", "hold" })
        {
            var effect = new EditorProjectEffectLayer
            {
                TargetClipId = clip.Id, Filter = "mono", StartSeconds = .7, EndSeconds = 2.3,
                RegionShape = "ellipse", RegionFeather = .6, Amount = .7,
                RegionKeyframes =
                [
                    new() { TimeSeconds = 0, RegionX = .05, RegionY = .1, RegionWidth = .3, RegionHeight = .4 },
                    new() { TimeSeconds = .8, RegionX = .3, RegionY = .2, RegionWidth = .4, RegionHeight = .6, Easing = easing },
                    new() { TimeSeconds = 1.6, RegionX = .65, RegionY = .4, RegionWidth = .3, RegionHeight = .5, Easing = easing }
                ]
            };
            project.EffectLayers = [effect];
            var path = Path.Combine(directory, $"fx-motion-{easing}.mp4");
            await export.ExportAsync(project, path);
            foreach (var time in new[] { .8, 1.1, 1.5, 2.2 })
                AssertCoverage(effect, time, await Frame(path, time), baseline, filtered);
            var before = await Frame(path, .6);
            var after = await Frame(path, 2.4);
            Check(MaximumDifference(before, baseline) <= 6 && MaximumDifference(after, baseline) <= 6,
                $"Animated {easing} FX changed frames outside its time window.");
        }
        var animated = project.EffectLayers[0];
        var dense = animated.Clone();
        foreach (var key in dense.RegionKeyframes) key.Easing = "ease-in-out";
        dense.TrimRegionAnimation(.85, 2.15);
        Check(dense.RegionKeyframes.Count > 100, "The partial eased trim did not exercise dense sampled motion.");
        project.EffectLayers = [dense];
        var densePath = Path.Combine(directory, "fx-motion-trimmed-dense.mp4");
        await export.ExportAsync(project, densePath);
        AssertCoverage(dense, 1.1, await Frame(densePath, 1.1), baseline, filtered);
        AssertCoverage(dense, 2, await Frame(densePath, 2), baseline, filtered);
        project.EffectLayers = [animated];
        var projectService = new EditorProjectService(Path.Combine(directory, "motion-projects"));
        var savedPath = Path.Combine(directory, "motion-projects", "animated.klippproject");
        await projectService.SaveAsync(project, savedPath);
        var reopened = await projectService.TryLoadAsync(savedPath);
        Check(reopened!.EffectLayers[0].RegionKeyframes.Count == 3 && reopened.EffectLayers[0].RegionFeather == .6 &&
              reopened.EffectLayers[0].EvaluateRegion(1.1) == animated.EvaluateRegion(1.1),
            "Region motion or feather was lost during save/reopen.");
        var copy = animated.Clone();
        copy.RegionKeyframes[0].RegionX = .8;
        Check(animated.RegionKeyframes[0].RegionX == .05, "Cloning an FX shared mutable region motion.");
        Console.WriteLine($"PASS: {checks} feather and animated region export checks, including every easing, retimed source, source-pixel coverage and saved motion");

        void AssertCoverage(EditorProjectEffectLayer effect, double time, byte[] actual, byte[] original, byte[] grade)
        {
            var region = effect.EvaluateRegion(time);
            var max = 0; var stableMax = 0; var exteriorMax = 0; long sum = 0;
            for (var y = 0; y < 180; y++) for (var x = 0; x < 320; x++)
            {
                var coverage = EditorEffectRegionMath.Coverage(region, effect.RegionShape, effect.RegionFeather,
                    320, 180, x + .5, y + .5) * effect.Amount;
                var offset = (y * 320 + x) * 3;
                var stable = effect.RegionFeather > 0 || new[] { (-2, 0), (2, 0), (0, -2), (0, 2) }
                    .All(delta => EditorEffectRegionMath.Coverage(region, effect.RegionShape, 0,
                        320, 180, x + .5 + delta.Item1, y + .5 + delta.Item2) * effect.Amount == coverage);
                for (var channel = 0; channel < 3; channel++)
                {
                    var expected = original[offset + channel] + (grade[offset + channel] - original[offset + channel]) * coverage;
                    var difference = (int)Math.Round(Math.Abs(actual[offset + channel] - expected));
                    max = Math.Max(max, difference); sum += difference;
                    if (stable) stableMax = Math.Max(stableMax, difference);
                    if (coverage == 0) exteriorMax = Math.Max(exteriorMax, difference);
                }
            }
            var mean = sum / (320d * 180 * 3);
            // Final H.264 chroma sampling may cross a hard region edge. Feathered
            // ramps should match the shared math much more closely everywhere.
            Check(mean <= 2.1 && stableMax <= 18,
                $"{effect.RegionShape} feather {effect.RegionFeather} at {time}: pixel coverage mismatch stable max {stableMax}, edge max {max}, mean {mean:0.###}.");
            Console.WriteLine($"PASS coverage: {effect.RegionShape} feather {effect.RegionFeather:0.##} at {time:0.###}, max {max}, mean {mean:0.###}, exterior max {exteriorMax}");
        }
        async Task<byte[]> Frame(string path, double seconds) => await Run(["-ss", F(seconds), "-i", path,
            "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]);
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
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); await reading;
            if (process.ExitCode != 0) throw new InvalidOperationException(await error);
            return output.ToArray();
        }
    }

    private static int MaximumDifference(byte[] first, byte[] second) => first.Zip(second, (a, b) => Math.Abs(a - b)).Max();
    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
