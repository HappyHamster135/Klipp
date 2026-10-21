using System.Text.Json;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;

internal static class EffectsWorkflowChecks
{
    public static Task<int> RunAsync(string root)
    {
        var checks = 0;
        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
            checks++;
        }
        var original = new EditorProjectClip
        {
            Id = "effects-target", Name = "Interview", Notes = "Keep this take", ColorLabel = "blue",
            SourcePath = Path.Combine(root, "effects-source.mp4"), SourceDurationSeconds = 40,
            TrimStartSeconds = 3, TrimEndSeconds = 23, TimelineStartSeconds = 9, PlaybackSpeed = 1.25,
            Track = EditorClipTrack.Overlay, TrackId = "video-track-3", GroupId = "linked-interview", Order = 2,
            SourceAudioSeparated = true, Volume = 0.65, Muted = true, NoiseReductionEnabled = true,
            NoiseReductionStrength = 0.7, NormalizeAudio = true, NormalizationGainDb = 2.5,
            IntegratedLoudnessLufs = -18.5, LoudnessAnalysisTrimStartSeconds = 3, LoudnessAnalysisTrimEndSeconds = 23,
            Scale = 0.6, OffsetX = 0.3, OffsetY = -0.4, CropLeft = 0.1, MaskShape = "ellipse",
            TransformKeyframes = [new() { TimeSeconds = 1, Scale = 1.5, Easing = "ease-in-out" }],
            VolumeKeyframes = [new() { TimeSeconds = 2, Volume = 0.4, Easing = "ease-out" }],
            TransitionInStyle = "dissolve", TransitionInDurationSeconds = 0.6,
            Filter = "warm", FilterStrength = 0.7,
            FilterStrengthKeyframes = [new() { TimeSeconds = 0, Strength = 0.4 }, new() { TimeSeconds = 2, Strength = 0.8 }],
            Brightness = 0.2, Contrast = 1.2, Saturation = 1.3, Temperature = -0.1, Shadows = 0.2, Highlights = -0.3,
            Gamma = 1.2, HueDegrees = 20, Sharpen = 0.3, Blur = 0.2, Vignette = 0.4,
            LutPath = Path.Combine(root, "test.cube"), LutStrength = 0.7,
            ToneCurve = [new() { X = 0, Y = 0 }, new() { X = 0.5, Y = 0.6 }, new() { X = 1, Y = 1 }]
        };
        original.Normalize();
        original.Effects.Reverse();
        original.Effects.First(stage => stage.Kind == "look").Enabled = false;
        var before = JsonSerializer.Serialize(original);
        var originalNodeIds = original.Effects.ToDictionary(stage => stage.Kind, stage => stage.Id);
        var animation = JsonSerializer.Serialize(original.FilterStrengthKeyframes);
        Check(!EditorClipEffectWorkflowService.TryCreateMutation(original, _ => { }, out var unchanged) &&
              JsonSerializer.Serialize(unchanged) == before, "An unchanged effect edit was reported as a mutation.");
        Check(EditorClipEffectWorkflowService.TryCreateMutation(original,
                clip => EditorClipEffectWorkflowService.ApplyLook(clip, "cool"), out var recolored) &&
              recolored.Filter == "cool" && recolored.Effects.Single(stage => stage.Kind == "look").Enabled,
            "Applying a look did not re-enable the bypassed Look stage.");
        Check(JsonSerializer.Serialize(original) == before && recolored.Id == original.Id &&
              JsonSerializer.Serialize(recolored.FilterStrengthKeyframes) == animation,
            "Staging a look changed the live source, clip identity, or amount animation.");
        recolored.Effects[0].Enabled = !recolored.Effects[0].Enabled;
        recolored.ToneCurve[1].Y = 0.1;
        Check(JsonSerializer.Serialize(original) == before,
            "Staged effect lists or curve points shared mutable data with the live clip.");

        EditorClipEffectWorkflowService.TryCreateMutation(original,
            clip => EditorClipEffectWorkflowService.ApplyLook(clip, "none"), out var noLook);
        Check(noLook.Filter == "none" && noLook.Brightness == original.Brightness && noLook.LutPath == original.LutPath &&
              JsonSerializer.Serialize(noLook.FilterStrengthKeyframes) == animation &&
              !noLook.Effects.Single(stage => stage.Kind == "look").Enabled,
            "Removing the look also reset custom grading, LUT, bypass or animation settings.");

        Check(EditorClipEffectWorkflowService.TryCreateMutation(original,
                EditorClipEffectWorkflowService.ResetAllEffects, out var reset), "Resetting an adjusted clip did not report a change.");
        Check(reset.Filter == "none" && reset.FilterStrength == 1 && reset.FilterStrengthKeyframes.Count == 0 &&
              reset.Brightness == 0 && reset.Contrast == 1 && reset.Saturation == 1 && reset.Temperature == 0 &&
              reset.Shadows == 0 && reset.Highlights == 0 && reset.Gamma == 1 && reset.HueDegrees == 0 &&
              reset.Sharpen == 0 && reset.Blur == 0 && reset.Vignette == 0 && reset.ToneCurve.Count == 0 &&
              reset.LutPath.Length == 0 && reset.LutStrength == 1,
            "Reset all effects left behind grading, LUT, amount or animation data.");
        Check(reset.Effects.Select(stage => stage.Kind).SequenceEqual(EditorProjectClipEffect.DefaultOrder) &&
              reset.Effects.All(stage => stage.Enabled && stage.Id == originalNodeIds[stage.Kind]),
            "Reset all effects did not restore effect order/bypass while retaining node identities.");
        Check(UnaffectedState(original) == UnaffectedState(reset),
            "Reset all effects changed timing, motion, mask, track, source audio, analysis, transition or metadata.");
        Check(!EditorClipEffectWorkflowService.TryCreateMutation(reset,
                EditorClipEffectWorkflowService.ResetAllEffects, out _),
            "Resetting a neutral clip created another history entry.");

        var topKind = reset.Effects[0].Kind;
        Check(!EditorClipEffectWorkflowService.TryCreateMutation(reset,
                clip => EditorClipEffectWorkflowService.MoveStage(clip, topKind, -1), out _),
            "Moving the first effect above the stack was reported as a change.");
        Check(!EditorClipEffectWorkflowService.TryCreateMutation(reset,
                clip => EditorClipEffectWorkflowService.SetStageEnabled(clip, "removed-kind", false), out _),
            "A stale stack toggle modified the clip.");
        Check(EditorClipEffectWorkflowService.TryCreateMutation(reset,
                clip => EditorClipEffectWorkflowService.MoveStage(clip, topKind, 1), out var moved) &&
              moved.Effects[1].Kind == topKind && moved.Effects[1].Id == reset.Effects[0].Id,
            "Reordering effects lost the requested stage or its identity.");
        var rejected = false;
        try
        {
            EditorClipEffectWorkflowService.TryCreateMutation(original, clip =>
            {
                clip.Brightness = -0.5;
                throw new InvalidOperationException("Rejected staged edit");
            }, out _);
        }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && JsonSerializer.Serialize(original) == before,
            "A failed staged edit partially changed the live source clip.");
        return Task.FromResult(checks);
    }

    private static string UnaffectedState(EditorProjectClip clip) => JsonSerializer.Serialize(new
    {
        clip.Id, clip.Name, clip.Notes, clip.ColorLabel, clip.SourcePath, clip.SourceDurationSeconds,
        clip.TrimStartSeconds, clip.TrimEndSeconds, clip.TimelineStartSeconds, clip.PlaybackSpeed,
        clip.Track, clip.TrackId, clip.GroupId, clip.Order, clip.SourceAudioSeparated, clip.Volume, clip.Muted,
        clip.NoiseReductionEnabled, clip.NoiseReductionStrength, clip.NormalizeAudio, clip.NormalizationGainDb,
        clip.IntegratedLoudnessLufs, clip.LoudnessAnalysisTrimStartSeconds, clip.LoudnessAnalysisTrimEndSeconds,
        clip.Scale, clip.OffsetX, clip.OffsetY, clip.CropLeft, clip.CropTop, clip.CropRight, clip.CropBottom,
        clip.MaskShape, clip.MaskRoundness, clip.MaskInverted, clip.MaskPolygon, clip.TransformKeyframes,
        clip.VolumeKeyframes, clip.TransitionInStyle, clip.TransitionInDurationSeconds, clip.VideoProcessing
    });
}
