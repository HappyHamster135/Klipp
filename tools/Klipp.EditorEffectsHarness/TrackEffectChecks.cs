using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;

internal static class TrackEffectChecks
{
    public static async Task RunAsync(EditorProjectExportService export, FFmpegLocator locator, string directory,
        string red, string blue, string gray)
    {
        var legacy = Project(Clip(red, "", 0, 2), Clip(blue, "", 0, 2), Clip(gray, "", 0.2, 0.3),
            Clip(blue, "", 0.3, 1.2), Clip(gray, "", 0.8, 1));
        legacy.Version = 31;
        foreach (var clip in legacy.Clips.Skip(1)) clip.Track = EditorClipTrack.Overlay;
        legacy.AudioTracks = [Audio(red, "", 0, 1), Audio(red, "", 0.2, 1), Audio(red, "", 1.2, 0.5)];
        legacy.Normalize();
        Check(legacy.Version == EditorProject.CurrentVersion && legacy.Clips[0].TrackId == EditorProject.DefaultPrimaryVideoTrackId,
            "Legacy primary video did not migrate to V1.");
        Check(legacy.GetTrack(legacy.Clips[4].TrackId)!.Order > legacy.GetTrack(legacy.Clips[3].TrackId)!.Order,
            "Greedy migration changed legacy overlay stacking order.");
        Check(legacy.AudioTracks[0].TrackId != legacy.AudioTracks[1].TrackId &&
              legacy.AudioTracks[0].TrackId == legacy.AudioTracks[2].TrackId,
            "Legacy audio overlap did not migrate to stable reusable rows.");
        var ids = legacy.Clips.Select(clip => clip.TrackId).ToArray();
        legacy.Normalize();
        Check(ids.SequenceEqual(legacy.Clips.Select(clip => clip.TrackId)), "Normalization repacked persistent video rows.");

        var project = Project(Clip(red, "video-1"), Clip(blue, "video-2"), Clip(gray, "video-3"));
        project.Clips[1].Scale = 0.5;
        project.Clips[2].Scale = 0.25;
        project.Normalize();
        var top = project.GetTrack("video-3")!;
        var middle = project.GetTrack("video-2")!;
        var bottom = project.GetTrack("video-1")!;
        var stackedPath = Path.Combine(directory, "tracks-order.mp4");
        await export.ExportAsync(project, stackedPath);
        var center = await Pixel(stackedPath, 0.5);
        Check(Math.Abs(center[0] - center[2]) < 8 && center[1] > 80, "Top video row did not cover lower rows.");
        var corner = await Pixel(stackedPath, 0.5, 8, 8);
        Check(corner[0] > 180 && corner[2] < 35, "Persistent overlay rows lost their transparent canvas.");

        top.Muted = true;
        var mutedPath = Path.Combine(directory, "tracks-mute.mp4");
        await export.ExportAsync(project, mutedPath);
        Check((await Pixel(mutedPath, 0.5))[2] > 180, "Muted video row remained visible.");
        bottom.Solo = true;
        var soloPath = Path.Combine(directory, "tracks-video-solo.mp4");
        await export.ExportAsync(project, soloPath);
        Check((await Pixel(soloPath, 0.5))[0] > 180, "Video solo did not isolate its row.");
        bottom.Muted = true;
        var silentSoloPath = Path.Combine(directory, "tracks-muted-solo.mp4");
        await export.ExportAsync(project, silentSoloPath);
        Check((await Pixel(silentSoloPath, 0.5)).All(value => value < 8), "Muted solo video did not produce black.");
        bottom.Muted = false;
        bottom.Solo = false;
        top.Muted = false;
        middle.Order = 4;
        project.Normalize();
        var reorderedPath = Path.Combine(directory, "tracks-reordered.mp4");
        await export.ExportAsync(project, reorderedPath);
        Check((await Pixel(reorderedPath, 0.5))[2] > 180, "Changing video track order did not change stacking.");
        var cache = new EditorTimelineRenderCacheService(Path.Combine(directory, "track-key-checks"));
        var key = cache.BuildSegments(project, "libx264")[0].CacheKey;
        middle.Locked = true;
        middle.Height = 100;
        middle.Name = "Renamed row";
        Check(cache.BuildSegments(project, "libx264")[0].CacheKey == key,
            "Editing-only row settings invalidated the render cache.");
        var lockedPath = Path.Combine(directory, "tracks-locked-renders.mp4");
        await export.ExportAsync(project, lockedPath);
        Check((await Pixel(lockedPath, 0.5))[2] > 180, "Locking a row changed its rendered picture.");
        Console.WriteLine("PASS: persistent video rows migrate, reorder, preserve alpha, mute/solo, and render while locked");

        var upperTransition = Project(Clip(red, "video-1", 0, 2), Clip(blue, "video-2", 0, 1), Clip(gray, "video-2", 0.6, 1));
        upperTransition.Clips[1].Scale = upperTransition.Clips[2].Scale = 0.5;
        upperTransition.Clips[2].TransitionInStyle = "dissolve";
        upperTransition.Clips[2].TransitionInDurationSeconds = 0.4;
        var upperPath = Path.Combine(directory, "tracks-upper-row-transition.mp4");
        await export.ExportAsync(upperTransition, upperPath);
        var upperCenter = await Pixel(upperPath, 0.8);
        var upperCorner = await Pixel(upperPath, 0.8, 8, 8);
        Check(upperCenter[2] > upperCenter[0] + 25 && upperCenter[0] > 25 && upperCorner[0] > 180,
            "An upper-row transition lost its blend or covered the lower row outside its matte.");

        var tone440 = Path.Combine(directory, "track-tone-440.wav");
        var tone880 = Path.Combine(directory, "track-tone-880.wav");
        await Run(["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2", tone440]);
        await Run(["-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=2", tone880]);
        var audioProject = Project(Clip(red, "video-1"));
        audioProject.Clips[0].SourceAudioSeparated = true;
        audioProject.AudioTracks = [Audio(tone440, "audio-1"), Audio(tone880, "audio-2")];
        audioProject.Normalize();
        var secondAudio = audioProject.GetTrack("audio-2")!;
        secondAudio.Solo = true;
        secondAudio.Locked = true;
        var audioSolo = Path.Combine(directory, "tracks-audio-solo.mp4");
        await export.ExportAsync(audioProject, audioSolo);
        Check(await Amplitude(audioSolo, 880) > 0.06 && await Amplitude(audioSolo, 440) < 0.002,
            "Audio track solo did not isolate the chosen row.");
        secondAudio.Muted = true;
        var audioMute = Path.Combine(directory, "tracks-audio-muted-solo.mp4");
        await export.ExportAsync(audioProject, audioMute);
        Check(await Amplitude(audioMute, 880) < 0.001 && await Amplitude(audioMute, 440) < 0.001,
            "Muted solo audio did not silence the mix.");
        Console.WriteLine("PASS: upper-row transitions and independent audio row solo/mute/lock");

        var effects = Project(Clip(gray, "video-1"));
        var graded = effects.Clips[0];
        graded.Brightness = 0.2;
        graded.ToneCurve = [new() { X = 0, Y = 0 }, new() { X = 0.5, Y = 0.25 }, new() { X = 1, Y = 1 }];
        var curveFirstPath = Path.Combine(directory, "stack-curve-first.mp4");
        await export.ExportAsync(effects, curveFirstPath);
        graded.Effects = graded.Effects.OrderBy(effect => effect.Kind == "color" ? 0 : effect.Kind == "curve" ? 1 : 2).ToList();
        var colorFirstPath = Path.Combine(directory, "stack-color-first.mp4");
        await export.ExportAsync(effects, colorFirstPath);
        Check(Math.Abs((await Pixel(curveFirstPath, 0.5))[0] - (await Pixel(colorFirstPath, 0.5))[0]) > 12,
            "Reordering curve and color did not change the exported grade.");
        foreach (var effect in graded.Effects) effect.Enabled = false;
        var bypassPath = Path.Combine(directory, "stack-bypass.mp4");
        await export.ExportAsync(effects, bypassPath);
        Check(Math.Abs((await Pixel(bypassPath, 0.5))[0] - 128) < 8, "Bypassed effects still changed the image.");

        var lut = Path.Combine(directory, "stack-invert.cube");
        var rows = new List<string> { "LUT_3D_SIZE 2" };
        for (var b = 0; b < 2; b++) for (var g = 0; g < 2; g++) for (var r = 0; r < 2; r++)
            rows.Add($"{1-r} {1-g} {1-b}");
        await File.WriteAllLinesAsync(lut, rows);
        var lutProject = Project(Clip(red, "video-1"));
        lutProject.Clips[0].LutPath = lut;
        foreach (var strength in new[] { 0d, 0.5, 1d })
        {
            lutProject.Clips[0].FilterStrength = strength;
            var path = Path.Combine(directory, $"stack-lut-strength-{strength}.mp4");
            await export.ExportAsync(lutProject, path);
            var pixel = await Pixel(path, 0.5);
            Check(strength switch
            {
                0 => pixel[0] > 180 && pixel[2] < 35,
                1 => pixel[0] < 35 && pixel[2] > 180,
                _ => Math.Abs(pixel[0] - pixel[2]) < 12 && pixel[0] is > 100 and < 155
            }, $"Global stack amount did not include LUT at {strength}: {string.Join(',', pixel)}.");
        }
        graded.GroupId = "shared-group";
        var clone = graded.CloneWithNewId();
        Check(clone.GroupId == graded.GroupId && clone.TrackId == graded.TrackId &&
              clone.Effects[0].Kind == graded.Effects[0].Kind && !ReferenceEquals(clone.Effects[0], graded.Effects[0]),
            "Track/group/effect nodes did not deep-copy with their clip.");
        var saved = JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(effects))!;
        Check(saved.Clips[0].Effects.All(effect => !effect.Enabled) && saved.Clips[0].GroupId == "shared-group",
            "Effect bypass/order/group metadata did not persist.");
        Console.WriteLine("PASS: effect order, per-node bypass, deep metadata copies, and whole-stack LUT strength");

        async Task<byte[]> Pixel(string path, double seconds, int x = 160, int y = 90) => await Run(
            ["-ss", F(seconds), "-i", path, "-frames:v", "1", "-vf", $"crop=2:2:{x}:{y}", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"]);
        async Task<double> Amplitude(string path, double frequency)
        {
            var data = await Run(["-ss", "0.2", "-i", path, "-t", "0.5", "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1"]);
            var real = 0d; var imaginary = 0d; var count = data.Length / 4;
            for (var index = 0; index < count; index++)
            {
                var phase = 2 * Math.PI * frequency * index / 48000;
                real += BitConverter.ToSingle(data, index * 4) * Math.Cos(phase);
                imaginary += BitConverter.ToSingle(data, index * 4) * Math.Sin(phase);
            }
            return 2 * Math.Sqrt(real * real + imaginary * imaginary) / Math.Max(1, count);
        }
        async Task<byte[]> Run(string[] arguments)
        {
            var info = new ProcessStartInfo(locator.FFmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(arguments)) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            using var output = new MemoryStream();
            var reading = process.StandardOutput.BaseStream.CopyToAsync(output);
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(); await reading;
            Check(process.ExitCode == 0, await error);
            return output.ToArray();
        }
    }
    private static EditorProject Project(params EditorProjectClip[] clips) => new()
    {
        CanvasWidth = 320, CanvasHeight = 180, Clips = clips.ToList(),
        Settings = new() { ExportEncoder = "software", ExportFrameRate = 30, ExportQuality = "draft" }
    };
    private static EditorProjectClip Clip(string path, string trackId, double start = 0, double duration = 1) => new()
    {
        SourcePath = path, SourceDurationSeconds = 2, TrimEndSeconds = duration,
        TimelineStartSeconds = start, TrackId = trackId, SourceAudioSeparated = true
    };
    private static EditorProjectAudioTrack Audio(string path, string trackId, double start = 0, double duration = 1) => new()
    {
        SourcePath = path, SourceDurationSeconds = 2, TrimEndSeconds = duration, TimelineStartSeconds = start, TrackId = trackId
    };
    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
}
