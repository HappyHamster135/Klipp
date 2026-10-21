using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;

internal static class FreeTimelineChecks
{
    public static async Task RunAsync(EditorProjectExportService export, FFmpegLocator locator,
        string directory, string red, string blue, string gray)
    {
        var legacy = Project(Clip(red, 0, 2), Clip(blue, 0, 2));
        legacy.Version = 30;
        legacy.Clips[1].TransitionInStyle = "dissolve";
        legacy.Clips[1].TransitionInDurationSeconds = 0.4;
        legacy.Normalize();
        Check(legacy.Version == EditorProject.CurrentVersion && Near(legacy.Clips[1].TimelineStartSeconds, 1.6) && Near(legacy.DurationSeconds, 3.6),
            "Legacy transition placement changed during migration.");
        legacy.Clips[1].TimelineStartSeconds = 11;
        legacy.Normalize();
        Check(Near(legacy.Clips[1].TimelineStartSeconds, 11) && Near(legacy.DurationSeconds, 13),
            "Normalization repacked an explicit timeline after migration.");

        var nested = Project(Clip(red, 0, 2), Clip(gray, 0.2, 0.2), Clip(blue, 0.8, 1));
        nested.Clips[2].TransitionInStyle = "dissolve";
        nested.Clips[2].TransitionInDurationSeconds = 0.4;
        nested.Normalize();
        Check(nested.GetTransitionPredecessor(nested.Clips[2])?.Id == nested.Clips[0].Id &&
              Near(nested.GetTransitionOverlapSeconds(nested.Clips[0], nested.Clips[2]), 0.4),
            "Nested overlap did not select the visible outgoing clip.");

        var source = Clip(red, 0.25, 1);
        source.Volume = 0.4;
        source.PlaybackSpeed = 0.5;
        source.VolumeKeyframes = [new() { TimeSeconds = 0, Volume = 0.2 }, new() { TimeSeconds = 1, Volume = 0.7 }];
        var separation = Project(source);
        separation.Normalize();
        var audio = EditorSourceAudioService.SeparateClipAudio(separation, source, true);
        Check(audio is not null && source.SourceAudioSeparated && audio.SourceClipId == source.Id &&
              Near(audio.PlaybackSpeed, 0.5) && Near(audio.TimelineStartSeconds, 0.25) &&
              !ReferenceEquals(audio.VolumeKeyframes[0], source.VolumeKeyframes[0]),
            "Source audio did not preserve timing, rate, and independent envelopes.");
        Check(EditorSourceAudioService.SeparateClipAudio(separation, source, true) is null && separation.AudioTracks.Count == 1,
            "Source audio was generated more than once.");
        separation.AudioTracks.Clear();
        Check(EditorSourceAudioService.SeparateClipAudio(separation, source, true) is null && separation.AudioTracks.Count == 0,
            "Deleted independent audio was regenerated.");
        Check(source.CloneWithNewId().SourceAudioSeparated, "Duplicating a separated video reset its audio marker.");
        var roundTrip = JsonSerializer.Deserialize<EditorProject>(JsonSerializer.Serialize(separation))!;
        Check(roundTrip.Clips[0].SourceAudioSeparated, "The separated audio marker was not persisted.");

        var detachedVideo = Clip(red, 0.5, 1);
        detachedVideo.Muted = true;
        var detached = Project(detachedVideo);
        var existingAudio = new EditorProjectAudioTrack
        {
            SourcePath = red, SourceDurationSeconds = 2, TrimEndSeconds = 1,
            TimelineStartSeconds = 0.5, Volume = 0.73
        };
        detached.AudioTracks.Add(existingAudio);
        Check(ReferenceEquals(existingAudio, EditorSourceAudioService.SeparateClipAudio(detached, detachedVideo, true)) &&
              detached.AudioTracks.Count == 1 && existingAudio.SourceClipId == detachedVideo.Id && Near(existingAudio.Volume, 0.73),
            "Legacy detached audio was duplicated or its existing edit was replaced.");
        Console.WriteLine("PASS: v31 position migration, nested predecessors, and once-only persisted source audio");

        var sparse = Project(Clip(red, 0.5, 1), Clip(blue, 2.25, 0.75));
        sparse.Normalize();
        var sparsePath = Path.Combine(directory, "free-video-gaps.mp4");
        await export.ExportAsync(sparse, sparsePath);
        Check(Near(sparse.DurationSeconds, 3), "Sparse primary duration was packed instead of using its last end.");
        await AssertPixelAsync(sparsePath, 0.1, "black");
        await AssertPixelAsync(sparsePath, 0.8, "red");
        await AssertPixelAsync(sparsePath, 1.8, "black");
        await AssertPixelAsync(sparsePath, 2.5, "blue");

        var overlap = Project(Clip(red, 0, 2), Clip(blue, 0.5, 0.5));
        var overlapPath = Path.Combine(directory, "free-video-overlap.mp4");
        await export.ExportAsync(overlap, overlapPath);
        await AssertPixelAsync(overlapPath, 0.7, "blue");
        await AssertPixelAsync(overlapPath, 1.3, "red");

        var adjacent = Project(Clip(red, 0, 1), Clip(blue, 1, 1));
        adjacent.Clips[1].TransitionInStyle = "dissolve";
        var adjacentPath = Path.Combine(directory, "free-video-adjacent.mp4");
        await export.ExportAsync(adjacent, adjacentPath);
        Check(Near(adjacent.DurationSeconds, 2) && adjacent.GetTransitionOverlapSeconds(adjacent.Clips[0], adjacent.Clips[1]) == 0,
            "An adjacent transition moved the incoming clip.");
        await AssertPixelAsync(adjacentPath, 1.1, "blue");

        var nestedPath = Path.Combine(directory, "free-video-nested-transition.mp4");
        await export.ExportAsync(nested, nestedPath);
        var mixture = await PixelAsync(nestedPath, 1.0);
        Check(mixture[0] > 35 && mixture[2] > 35, "Nested transition did not blend the actual visible predecessor.");
        nested.Clips.Add(Clip(gray, 1.05, 0.2));
        var overridePath = Path.Combine(directory, "free-video-transition-overridden.mp4");
        await export.ExportAsync(nested, overridePath);
        var overriding = await PixelAsync(overridePath, 1.1);
        Check(Math.Abs(overriding[0] - overriding[2]) < 8 && overriding[1] > 80,
            "A previous transition painted over a later-start cut.");
        Console.WriteLine("PASS: black leading/internal gaps, overlap priority/restoration, adjacent cuts, nested transitions");

        var late = Project(Clip(red, 12, 2), Clip(blue, 13.6, 2));
        late.Clips[1].TransitionInStyle = "dissolve";
        late.Clips[1].TransitionInDurationSeconds = 0.4;
        var latePath = Path.Combine(directory, "free-video-late-transition.mp4");
        await export.ExportAsync(late, latePath);
        await AssertPixelAsync(latePath, 0.5, "black");
        await AssertPixelAsync(latePath, 12.5, "red");
        var lateMiddle = await PixelAsync(latePath, 13.8);
        Check(lateMiddle[0] > 35 && lateMiddle[2] > 35, "A transition after a long leading gap lost its absolute clock.");
        await AssertPixelAsync(latePath, 14.5, "blue");
        Console.WriteLine("PASS: late transitions retain their absolute clock across cached segments");

        var audible = Path.Combine(directory, "free-audible-source.mp4");
        await RunAsync(["-i", red, "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2",
            "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "aac", "-t", "2", audible]);
        var embedded = Project(Clip(audible, 0, 1));
        embedded.Clips[0].Volume = 0.5;
        var embeddedPath = Path.Combine(directory, "free-audio-embedded-reference.mp4");
        await export.ExportAsync(embedded, embeddedPath);
        var separated = Project(Clip(audible, 0, 1));
        separated.Clips[0].Volume = 0.5;
        EditorSourceAudioService.SeparateClipAudio(separated, separated.Clips[0], true);
        var separatedPath = Path.Combine(directory, "free-audio-no-duplicate.mp4");
        await export.ExportAsync(separated, separatedPath);
        var referenceRms = await RmsAsync(embeddedPath, 0.2, 0.5);
        var separatedRms = await RmsAsync(separatedPath, 0.2, 0.5);
        Check(separatedRms > 0.01 && Math.Abs(separatedRms / referenceRms - 1) < 0.12,
            "Separated video audio was doubled or lost in export.");
        separated.AudioTracks.Clear();
        var deletedAudioPath = Path.Combine(directory, "free-audio-deleted-stays-silent.mp4");
        await export.ExportAsync(separated, deletedAudioPath);
        Check(await RmsAsync(deletedAudioPath, 0.2, 0.5) < 0.0001,
            "Deleting separated audio re-enabled embedded video audio.");
        Console.WriteLine("PASS: source audio exported exactly once; deleting audio keeps video silent");

        var music = Path.Combine(directory, "free-duck-music.wav");
        await RunAsync(["-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000:duration=2", "-c:a", "pcm_s16le", music]);
        var ducking = Project(Clip(audible, 0, 2));
        var voice = EditorSourceAudioService.SeparateClipAudio(ducking, ducking.Clips[0], true)!;
        voice.TrimEndSeconds = 0.7;
        voice.TimelineStartSeconds = 0.5;
        var backgroundMusic = new EditorProjectAudioTrack
        {
            SourcePath = music, SourceDurationSeconds = 2, TrimEndSeconds = 2,
            DuckingStrength = 1
        };
        ducking.AudioTracks.Add(backgroundMusic);
        var musicReferencePath = Path.Combine(directory, "free-duck-reference.mp4");
        await export.ExportAsync(ducking, musicReferencePath);
        backgroundMusic.DuckingEnabled = true;
        var duckedPath = Path.Combine(directory, "free-duck-independent-source.mp4");
        await export.ExportAsync(ducking, duckedPath);
        var referenceMusic = await FrequencyAmplitudeAsync(musicReferencePath, 0.7, 0.3, 880);
        var duckedMusic = await FrequencyAmplitudeAsync(duckedPath, 0.7, 0.3, 880);
        Check(duckedMusic < referenceMusic * 0.65,
            $"Separated source audio did not duck music: {duckedMusic}/{referenceMusic}.");
        voice.Muted = true;
        var mutedControlPath = Path.Combine(directory, "free-duck-muted-source.mp4");
        await export.ExportAsync(ducking, mutedControlPath);
        Check(await FrequencyAmplitudeAsync(mutedControlPath, 0.7, 0.3, 880) > referenceMusic * 0.85,
            "Muted source audio still ducked the independent music track.");
        Console.WriteLine("PASS: independent source audio controls ducking only while audible");

        var audioOnly = Project();
        audioOnly.AudioTracks.Add(new EditorProjectAudioTrack
        {
            SourcePath = music, SourceDurationSeconds = 2, TrimEndSeconds = 1,
            TimelineStartSeconds = 0.25
        });
        var audioOnlyPath = Path.Combine(directory, "free-audio-only-black-video.mp4");
        await export.ExportAsync(audioOnly, audioOnlyPath);
        await AssertPixelAsync(audioOnlyPath, 0.6, "black");
        Check(await RmsAsync(audioOnlyPath, 0.5, 0.3) > 0.03,
            "An audio-only timeline did not export its independent audio.");
        var projectStore = new EditorProjectService(Path.Combine(directory, "saved-project-checks"));
        await projectStore.SaveAutosaveAsync(audioOnly, audible);
        var reopenedAudio = await projectStore.LoadOrCreateAsync(audible, TimeSpan.FromSeconds(2));
        Check(reopenedAudio.Clips.Count == 0 && reopenedAudio.AudioTracks.Count == 1,
            "Reopening an audio-only autosave restored the deleted video.");
        audioOnly.AudioTracks.Clear();
        await projectStore.SaveAutosaveAsync(audioOnly, audible);
        var reopenedEmpty = await projectStore.LoadOrCreateAsync(audible, TimeSpan.FromSeconds(2));
        Check(reopenedEmpty.Clips.Count == 0 && reopenedEmpty.AudioTracks.Count == 0,
            "Reopening an empty saved edit restored deleted media.");
        Console.WriteLine("PASS: audio-only export and saved audio-only/empty projects retain deleted media");

        async Task AssertPixelAsync(string path, double time, string expected)
        {
            var pixel = await PixelAsync(path, time);
            Check(expected switch
            {
                "red" => pixel[0] > 180 && pixel[2] < 35,
                "blue" => pixel[2] > 180 && pixel[0] < 35,
                _ => pixel.Take(3).All(value => value < 8)
            }, $"Expected {expected} at {time}s: {string.Join(',', pixel)} in {path}.");
        }
        Task<byte[]> PixelAsync(string path, double time) => RunAsync(
            ["-ss", F(time), "-i", path, "-frames:v", "1", "-vf", "crop=2:2:160:90", "-f", "rawvideo", "-pix_fmt", "rgb24", "pipe:1"]);
        async Task<double> RmsAsync(string path, double start, double duration)
        {
            var samples = await RunAsync(["-ss", F(start), "-i", path, "-t", F(duration), "-vn", "-ac", "1", "-f", "f32le", "pipe:1"]);
            var sum = 0d;
            for (var index = 0; index + 3 < samples.Length; index += 4)
            {
                var sample = BitConverter.ToSingle(samples, index);
                sum += sample * sample;
            }
            return Math.Sqrt(sum / Math.Max(1, samples.Length / 4));
        }
        async Task<double> FrequencyAmplitudeAsync(string path, double start, double duration, double frequency)
        {
            var samples = await RunAsync(["-ss", F(start), "-i", path, "-t", F(duration), "-vn", "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1"]);
            var real = 0d;
            var imaginary = 0d;
            var count = samples.Length / 4;
            for (var index = 0; index < count; index++)
            {
                var sample = BitConverter.ToSingle(samples, index * 4);
                var phase = 2 * Math.PI * frequency * index / 48000;
                real += sample * Math.Cos(phase);
                imaginary += sample * Math.Sin(phase);
            }
            return 2 * Math.Sqrt(real * real + imaginary * imaginary) / Math.Max(1, count);
        }
        async Task<byte[]> RunAsync(string[] arguments)
        {
            var info = new ProcessStartInfo(locator.FFmpegPath)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
            };
            foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-y" }.Concat(arguments))
                info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!;
            using var output = new MemoryStream();
            var read = process.StandardOutput.BaseStream.CopyToAsync(output);
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            await read;
            Check(process.ExitCode == 0, await error);
            return output.ToArray();
        }
    }

    private static EditorProject Project(params EditorProjectClip[] clips) => new()
    {
        CanvasWidth = 320, CanvasHeight = 180,
        Clips = clips.Select((clip, order) => { clip.Order = order; return clip; }).ToList(),
        Settings = new() { ExportEncoder = "software", ExportFrameRate = 30, ExportQuality = "draft" }
    };
    private static EditorProjectClip Clip(string path, double start, double duration) => new()
    {
        SourcePath = path, SourceDurationSeconds = 2, TrimEndSeconds = duration, TimelineStartSeconds = start,
        Track = EditorClipTrack.Primary
    };
    private static bool Near(double left, double right) => Math.Abs(left - right) < 0.00001;
    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new InvalidOperationException(message);
    }
}
