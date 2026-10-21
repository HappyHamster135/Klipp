using System.Text.Json;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;

internal static class LookPresetChecks
{
    public static async Task<int> RunAsync(string root)
    {
        var checks = 0;
        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
            checks++;
        }
        var source = new EditorProjectClip
        {
            SourcePath = Path.Combine(root, "source.mp4"), SourceDurationSeconds = 30,
            TrimStartSeconds = 1, TrimEndSeconds = 21, TimelineStartSeconds = 2,
            Filter = "cinematic", FilterStrength = 0.65, Brightness = 0.1, Contrast = 1.2,
            Saturation = 0.75, Gamma = 1.3, HueDegrees = 15, Temperature = 0.2,
            Shadows = 0.2, Highlights = -0.1, Sharpen = 0.4, Blur = 0.1, Vignette = 0.3,
            LutPath = Path.Combine(root, "grade.cube"), LutStrength = 0.7,
            ToneCurve = [new() { X = 0, Y = 0 }, new() { X = 0.5, Y = 0.6 }, new() { X = 1, Y = 1 }],
            Effects = EditorProjectClipEffect.CreateDefault().AsEnumerable().Reverse().ToList(),
            Scale = 1.5, OffsetX = 0.2, OffsetY = -0.3,
            CropLeft = 0.1, CropTop = 0.05, CropRight = 0.1, CropBottom = 0.05,
            MaskShape = "polygon", MaskRoundness = 0.3,
            MaskInverted = true, MaskPolygon = [new() { X = 0.1, Y = 0.1 }, new() { X = 0.9, Y = 0.1 }, new() { X = 0.5, Y = 0.8 }],
            TransformKeyframes = [new() { TimeSeconds = 0, Scale = 1.2 },
                new() { TimeSeconds = 2, Scale = 1.7, OffsetX = 0.3, Easing = "ease-in-out" }],
            FilterStrengthKeyframes = [new() { TimeSeconds = 2, Strength = 0.8 }]
        };
        source.Effects.First(node => node.Kind == "blur").Enabled = false;
        source.Normalize();
        var target = new EditorProjectClip
        {
            Id = "target-clip", SourcePath = Path.Combine(root, "target.mp4"), Name = "Target",
            SourceDurationSeconds = 40, TrimStartSeconds = 4, TrimEndSeconds = 24,
            TimelineStartSeconds = 17, PlaybackSpeed = 1.25, Track = EditorClipTrack.Overlay,
            TrackId = "video-track-3", GroupId = "group-9", Order = 12,
            SourceAudioSeparated = true, Volume = 0.3, Muted = true,
            NormalizeAudio = true, NormalizationGainDb = 3.25, IntegratedLoudnessLufs = -19.25,
            LoudnessAnalysisTrimStartSeconds = 4, LoudnessAnalysisTrimEndSeconds = 24,
            FilterStrengthKeyframes = [new() { TimeSeconds = 1, Strength = 0.4, Easing = "hold" }]
        };
        target.Normalize();
        var identity = ClipIdentity(target);
        var retainedAnimation = JsonSerializer.Serialize(target.FilterStrengthKeyframes);
        EditorClipAttributesService.CopyLook(source, target);
        Check(ClipIdentity(target) == identity, "Pasting a look changed target media, timing, track, group, or source-audio identity.");
        Check(target.LutPath == source.LutPath && target.LutStrength == source.LutStrength &&
              target.Filter == source.Filter && target.Gamma == source.Gamma && target.Saturation == source.Saturation,
            "Pasting a look did not copy its grade and LUT.");
        Check(EffectSignature(target) == EffectSignature(source), "Pasting a look changed effect order or bypass states.");
        Check(JsonSerializer.Serialize(target.FilterStrengthKeyframes) == retainedAnimation,
            "Pasting a static look replaced the target's amount animation.");
        source.ToneCurve[1].Y = 0.2;
        source.Effects[0].Enabled = !source.Effects[0].Enabled;
        Check(target.ToneCurve[1].Y == 0.6 && target.Effects[0].Enabled != source.Effects[0].Enabled,
            "Pasted curve or effect stack retained shared mutable objects.");

        EditorClipAttributesService.CopyTransform(source, target);
        Check(ClipIdentity(target) == identity, "Pasting motion changed target media, timing, audio analysis, track, or group.");
        Check(target.Scale == 1.5 && target.OffsetY == -0.3 && target.CropLeft == 0.1 &&
              target.MaskShape == source.MaskShape && target.TransformKeyframes[1].Easing == "ease-in-out",
            "Pasting transform did not include crop, mask, and motion keyframes.");
        Check(target.MaskInverted && target.MaskPolygon.Count == 3 && !ReferenceEquals(target.MaskPolygon[0], source.MaskPolygon[0]),
            "Pasting transform omitted polygon mask data or retained shared vertices.");
        source.TransformKeyframes[1].Scale = 3;
        Check(target.TransformKeyframes[1].Scale == 1.7 &&
              !ReferenceEquals(source.TransformKeyframes[1], target.TransformKeyframes[1]),
            "Pasted motion keyframes were not deep copies.");

        var audioSource = new EditorProjectAudioTrack
        {
            SourcePath = "other.wav", SourceDurationSeconds = 30, TrimEndSeconds = 20,
            Volume = 0.55, Muted = true, FadeInSeconds = 0.4, FadeOutSeconds = 0.7,
            NoiseReductionEnabled = true, NoiseReductionStrength = 0.6, DuckingEnabled = true, DuckingStrength = 0.8,
            NormalizeAudio = true, NormalizationGainDb = -5, IntegratedLoudnessLufs = -11,
            LoudnessAnalysisTrimEndSeconds = 20,
            VolumeKeyframes = [new() { TimeSeconds = 0, Volume = 0.2 },
                new() { TimeSeconds = 2, Volume = 0.8, Easing = "ease-out" }]
        };
        var audioTarget = new EditorProjectAudioTrack
        {
            Id = "target-audio", SourcePath = "target.wav", SourceClipId = "linked-clip",
            SourceDurationSeconds = 40, TrimStartSeconds = 4, TrimEndSeconds = 24,
            TimelineStartSeconds = 17, PlaybackSpeed = 1.25, TrackId = "audio-track-2", GroupId = "group-9",
            NormalizeAudio = true, NormalizationGainDb = 3.25, IntegratedLoudnessLufs = -19.25,
            LoudnessAnalysisTrimStartSeconds = 4, LoudnessAnalysisTrimEndSeconds = 24
        };
        audioSource.Normalize(); audioTarget.Normalize();
        var audioIdentity = AudioIdentity(audioTarget);
        EditorClipAttributesService.CopyAudio(audioSource, audioTarget);
        Check(AudioIdentity(audioTarget) == audioIdentity,
            "Pasting audio changed target media, link, range, track, group, or its own loudness measurement.");
        Check(audioTarget.Volume == 0.55 && audioTarget.Muted && audioTarget.NoiseReductionStrength == 0.6 &&
              audioTarget.DuckingStrength == 0.8 && audioTarget.FadeOutSeconds == 0.7 &&
              audioTarget.VolumeKeyframes[1].Easing == "ease-out", "Pasting audio omitted mix settings or envelope.");
        audioSource.VolumeKeyframes[1].Volume = 1.9;
        Check(audioTarget.VolumeKeyframes[1].Volume == 0.8 &&
              !ReferenceEquals(audioSource.VolumeKeyframes[1], audioTarget.VolumeKeyframes[1]),
            "Pasted volume envelope was not a deep copy.");

        var path = Path.Combine(root, "looks", "presets.json");
        var presets = new EditorLookPresetService(path);
        await presets.SaveAsync("Zulu", target);
        await presets.SaveAsync("alpha", source);
        var loaded = await new EditorLookPresetService(path).LoadAsync();
        Check(loaded.Select(item => item.Name).SequenceEqual(["alpha", "Zulu"]), "User looks were not persisted in display name order.");
        var saved = loaded.Single(item => item.Name == "Zulu");
        Check(EffectSignature(saved.Style) == EffectSignature(target) && saved.Style.LutPath == target.LutPath &&
              saved.Style.LutStrength == 0.7 && saved.Style.ToneCurve[1].Y == 0.6,
            "Saved look did not retain effect order, bypass, curve, and LUT configuration.");
        Check(saved.Style.SourcePath.Length == 0 && saved.Style.FilterStrengthKeyframes.Count == 0,
            "Saved static look retained source media or clip-local amount animation.");
        var savedId = saved.Id;
        target.Brightness = -0.25;
        await presets.SaveAsync(" zULu ", target);
        loaded = await presets.LoadAsync();
        Check(loaded.Count == 2 && loaded.Single(item => item.Id == savedId).Style.Brightness == -0.25,
            "Saving a look with the same name did not update its existing identity.");
        await presets.DeleteAsync(savedId);
        Check((await presets.LoadAsync()).Count == 1, "Deleting a user look did not persist.");

        foreach (var contents in new[] { "{ broken", "null", "[null]", "[{\"Name\":\"Bad\",\"Style\":null}]" })
        {
            await File.WriteAllTextAsync(path, contents);
            foreach (var operation in new Func<Task>[] {
                async () => { await presets.LoadAsync(); },
                () => presets.SaveAsync("New", target),
                () => presets.DeleteAsync("any") })
            {
                var rejected = false;
                try { await operation(); }
                catch (InvalidDataException) { rejected = true; }
                Check(rejected && await File.ReadAllTextAsync(path) == contents,
                    $"Malformed preset JSON was not rejected and preserved: {contents}");
            }
        }
        return checks;
    }

    private static string EffectSignature(EditorProjectClip clip) =>
        string.Join('|', clip.Effects.Select(node => $"{node.Id}:{node.Kind}:{node.Enabled}"));

    private static string ClipIdentity(EditorProjectClip clip) => JsonSerializer.Serialize(new
    {
        clip.Id, clip.SourcePath, clip.Name, clip.SourceDurationSeconds, clip.TrimStartSeconds, clip.TrimEndSeconds,
        clip.TimelineStartSeconds, clip.PlaybackSpeed, clip.Track, clip.TrackId, clip.GroupId, clip.Order,
        clip.Volume, clip.Muted, clip.SourceAudioSeparated, clip.NormalizeAudio, clip.NormalizationGainDb,
        clip.IntegratedLoudnessLufs, clip.LoudnessAnalysisTrimStartSeconds, clip.LoudnessAnalysisTrimEndSeconds
    });

    private static string AudioIdentity(EditorProjectAudioTrack track) => JsonSerializer.Serialize(new
    {
        track.Id, track.SourcePath, track.SourceClipId, track.SourceDurationSeconds, track.TrimStartSeconds,
        track.TrimEndSeconds, track.TimelineStartSeconds, track.PlaybackSpeed, track.TrackId, track.GroupId,
        track.NormalizeAudio, track.NormalizationGainDb, track.IntegratedLoudnessLufs,
        track.LoudnessAnalysisTrimStartSeconds, track.LoudnessAnalysisTrimEndSeconds
    });
}
