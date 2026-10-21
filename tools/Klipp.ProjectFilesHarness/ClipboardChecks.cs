using System.Text.Json;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;

internal static class ClipboardChecks
{
    public static Task<int> RunAsync(string root)
    {
        var checks = 0;
        void Check(bool valid, string message)
        {
            if (!valid) throw new InvalidOperationException(message);
            checks++;
        }
        bool Near(double left, double right) => Math.Abs(left - right) < 0.000001;
        var tracks = new List<EditorTimelineTrack>
        {
            new() { Id = "clip-v1", Kind = EditorTimelineTrackKind.Video, Name = "Camera", Order = 0 },
            new() { Id = "clip-v2", Kind = EditorTimelineTrackKind.Video, Name = "B-roll", Order = 1, Height = 90, Muted = true, Solo = true },
            new() { Id = "clip-a1", Kind = EditorTimelineTrackKind.Audio, Name = "Dialogue", Order = 0 },
            new() { Id = "clip-a2", Kind = EditorTimelineTrackKind.Audio, Name = "Music", Order = 1 }
        };
        var video = new List<EditorProjectClip>
        {
            new()
            {
                Id = "copy-video", SourcePath = Path.Combine(root, "clipboard-video.mp4"), Name = "Take 1",
                Notes = "Keep this", ColorLabel = "blue", TrackId = tracks[0].Id, GroupId = "take-group",
                TimelineStartSeconds = 2, SourceDurationSeconds = 20, TrimStartSeconds = 3, TrimEndSeconds = 9,
                PlaybackSpeed = 1.5, SourceAudioSeparated = true, Volume = .7, Muted = true,
                NormalizeAudio = true, NormalizationGainDb = 2, IntegratedLoudnessLufs = -17,
                LoudnessAnalysisTrimStartSeconds = 3, LoudnessAnalysisTrimEndSeconds = 9,
                NoiseReductionEnabled = true, NoiseReductionStrength = .8, Scale = .7, OffsetX = .2,
                CropLeft = .1, MaskShape = "ellipse", MaskRoundness = .3, Filter = "warm", FilterStrength = .6,
                LutPath = Path.Combine(root, "clipboard.cube"), LutStrength = .4,
                TransitionInStyle = "dissolve", TransitionInDurationSeconds = .5,
                TransformKeyframes = [new() { TimeSeconds = 1, Scale = .8, Easing = "ease-out" }],
                VolumeKeyframes = [new() { TimeSeconds = 1, Volume = .5, Easing = "hold" }],
                FilterStrengthKeyframes = [new() { TimeSeconds = 1, Strength = .9 }],
                ToneCurve = [new() { X = 0, Y = 0 }, new() { X = .5, Y = .6 }, new() { X = 1, Y = 1 }],
                VideoProcessing = new() { OriginalSourcePath = "original.mp4", BakedSourcePath = "baked.mp4", IsApplied = true }
            },
            new() { Id = "copy-overlay", SourcePath = "overlay.mp4", TrackId = tracks[1].Id, Track = EditorClipTrack.Overlay,
                TimelineStartSeconds = 4, SourceDurationSeconds = 9, TrimStartSeconds = 1, TrimEndSeconds = 6 }
        };
        foreach (var clip in video) clip.Normalize();
        var audio = new List<EditorProjectAudioTrack>
        {
            new() { Id = "copy-audio", SourcePath = video[0].SourcePath, SourceClipId = video[0].Id, TrackId = tracks[2].Id,
                GroupId = "take-group", TimelineStartSeconds = 1.5, SourceDurationSeconds = 20, TrimStartSeconds = 3,
                TrimEndSeconds = 9, PlaybackSpeed = 1.5, Volume = .6, FadeInSeconds = .3, FadeOutSeconds = .4,
                VolumeKeyframes = [new() { TimeSeconds = 1, Volume = .2, Easing = "ease-in-out" }],
                DuckingEnabled = true, DuckingStrength = .8, NoiseReductionEnabled = true, NoiseReductionStrength = .2 },
            new() { Id = "copy-music", SourcePath = "music.wav", SourceClipId = "unselected-video", TrackId = tracks[3].Id,
                TimelineStartSeconds = 5, SourceDurationSeconds = 20, TrimEndSeconds = 10 }
        };
        foreach (var clip in audio) clip.Normalize();
        var fx = new List<EditorProjectEffectLayer>
        {
            new() { Id = "copy-fx", TargetClipId = video[0].Id, Filter = "mono", StartSeconds = 3, EndSeconds = 5,
                Amount = .6, RegionX = .25, RegionWidth = .5, RegionShape = "ellipse",
                Style = new() { Brightness = .2, LutPath = "copied-style.cube" } },
            new() { TargetClipId = "unselected-video", Filter = "warm", StartSeconds = 3, EndSeconds = 5 }
        };
        var original = new EditorProject { Clips = video, AudioTracks = audio, Tracks = tracks, EffectLayers = fx };
        var originalJson = JsonSerializer.Serialize(original);
        var clipboard = new EditorTimelineClipboardService();
        Check(!clipboard.HasClips && !clipboard.TryCreatePaste(tracks, 10, 30, out var emptyPlan, out _) && emptyPlan is null,
            "An empty clipboard produced a paste.");
        Check(clipboard.Capture(video, audio, tracks, fx) && clipboard.ClipCount == 4, "Mixed clip capture failed.");
        Check(JsonSerializer.Serialize(original) == originalJson, "Copy mutated the source selection.");
        Check(clipboard.TryCreatePaste(tracks, 10.014, 30, out var paste, out _) && paste is not null, "Paste planning failed.");
        var planned = paste!;
        Check(planned.AddedTracks.Count == 0 && planned.VideoClips.Count == 2 && planned.AudioClips.Count == 2,
            "Paste changed the clip count or unnecessarily added tracks.");
        Check(Near(planned.AudioClips[0].TimelineStartSeconds, 10) && Near(planned.VideoClips[0].TimelineStartSeconds, 10.5) &&
              Near(planned.VideoClips[1].TimelineStartSeconds, 12.5) && Near(planned.AudioClips[1].TimelineStartSeconds, 13.5),
            "Paste did not align the earliest clip to a frame or preserve relative offsets.");
        Check(planned.VideoClips[0].TrackId == tracks[0].Id && planned.VideoClips[1].TrackId == tracks[1].Id &&
              planned.AudioClips[1].TrackId == tracks[3].Id, "Same-project paste lost original tracks.");
        Check(planned.VideoClips[0].Id != video[0].Id && planned.AudioClips[0].Id != audio[0].Id &&
              planned.VideoClips[0].GroupId != "take-group" && planned.VideoClips[0].GroupId == planned.AudioClips[0].GroupId &&
              planned.VideoClips[1].GroupId == string.Empty, "Paste did not assign independent identities and groups.");
        Check(planned.AudioClips[0].SourceClipId == planned.VideoClips[0].Id && planned.AudioClips[1].SourceClipId == string.Empty,
            "Pasted audio was linked to a source clip outside the pasted selection.");
        Check(planned.EffectLayers.Count == 1 && planned.EffectLayers[0].TargetClipId == planned.VideoClips[0].Id &&
              planned.EffectLayers[0].Id != fx[0].Id && Near(planned.EffectLayers[0].StartSeconds, 11.5) &&
              Near(planned.EffectLayers[0].EndSeconds, 13.5), "Pasted FX lost their target, identity or relative time window.");
        Check(planned.EffectLayers[0].Filter == "mono" && planned.EffectLayers[0].RegionShape == "ellipse" &&
              Near(planned.EffectLayers[0].Amount, .6) && Near(planned.EffectLayers[0].RegionX, .25) &&
              Near(planned.EffectLayers[0].RegionWidth, .5) &&
              planned.EffectLayers[0].Style!.LutPath == "copied-style.cube", "Paste lost the FX region, amount or custom LUT style.");
        var expectedVideo = EditorProjectService.Clone(original).Clips[0];
        expectedVideo.Id = planned.VideoClips[0].Id;
        expectedVideo.GroupId = planned.VideoClips[0].GroupId;
        expectedVideo.TimelineStartSeconds = planned.VideoClips[0].TimelineStartSeconds;
        Check(JsonSerializer.Serialize(expectedVideo) == JsonSerializer.Serialize(planned.VideoClips[0]),
            "Video metadata, effects, processing or animation were lost during paste.");
        var expectedAudio = EditorProjectService.Clone(original).AudioTracks[0];
        expectedAudio.Id = planned.AudioClips[0].Id;
        expectedAudio.GroupId = planned.AudioClips[0].GroupId;
        expectedAudio.SourceClipId = planned.AudioClips[0].SourceClipId;
        expectedAudio.TimelineStartSeconds = planned.AudioClips[0].TimelineStartSeconds;
        Check(JsonSerializer.Serialize(expectedAudio) == JsonSerializer.Serialize(planned.AudioClips[0]),
            "Audio fades, automation or processing were lost during paste.");
        Check(JsonSerializer.Serialize(original) == originalJson, "Paste planning mutated source clips or destination tracks.");
        video[0].Filter = "mono";
        video[0].TransformKeyframes[0].Scale = 3;
        audio[0].VolumeKeyframes[0].Volume = 2;
        planned.VideoClips[0].Notes = "Changed pasted clip";
        planned.VideoClips[0].FilterStrengthKeyframes.Clear();
        fx[0].Amount = .1;
        fx[0].Style!.Brightness = .9;
        planned.EffectLayers[0].RegionWidth = .2;
        planned.EffectLayers[0].Style!.Brightness = .8;
        Check(clipboard.TryCreatePaste(tracks, 20, 30, out var repeat, out _) && repeat is not null &&
              repeat.VideoClips[0].Filter == "warm" && Near(repeat.VideoClips[0].TransformKeyframes[0].Scale, .8) &&
              repeat.VideoClips[0].Notes == "Keep this" && repeat.VideoClips[0].FilterStrengthKeyframes.Count == 1 &&
              Near(repeat.AudioClips[0].VolumeKeyframes[0].Volume, .2), "Clipboard shared mutable data with source or a previous paste.");
        Check(repeat!.VideoClips[0].Id != planned.VideoClips[0].Id && repeat.VideoClips[0].GroupId != planned.VideoClips[0].GroupId &&
              repeat.AudioClips[0].SourceClipId == repeat.VideoClips[0].Id, "Repeated pastes shared clip/group/link identities.");
        Check(Near(repeat.EffectLayers[0].Amount, .6) && Near(repeat.EffectLayers[0].RegionWidth, .5) &&
              Near(repeat.EffectLayers[0].Style!.Brightness, .2) &&
              repeat.EffectLayers[0].Id != planned.EffectLayers[0].Id &&
              repeat.EffectLayers[0].TargetClipId == repeat.VideoClips[0].Id,
            "Repeated FX pastes shared mutable data or retained another paste's target.");
        var lockedTracks = tracks.Select(track => track.Clone()).ToList();
        lockedTracks[1].Locked = true;
        var lockedBefore = JsonSerializer.Serialize(lockedTracks);
        Check(!clipboard.TryCreatePaste(lockedTracks, 4, 30, out var denied, out var lockedStatus) && denied is null &&
              lockedStatus.Contains("B-roll") && JsonSerializer.Serialize(lockedTracks) == lockedBefore,
            "A paste partly succeeded on a locked destination or changed that destination.");
        Check(clipboard.HasClips && clipboard.TryCreatePaste(tracks, 4, 30, out _, out _), "A rejected paste destroyed the clipboard.");
        var otherTracks = new List<EditorTimelineTrack>
        {
            new() { Id = "other-v", Kind = EditorTimelineTrackKind.Video, Name = "Other camera", Order = 0 },
            new() { Id = "other-a", Kind = EditorTimelineTrackKind.Audio, Name = "Other dialogue", Order = 0 }
        };
        var otherBefore = JsonSerializer.Serialize(otherTracks);
        Check(clipboard.TryCreatePaste(otherTracks, 3, 30, out var other, out _) && other is not null && other.AddedTracks.Count == 2,
            "Cross-project paste did not create exactly the missing video/audio rows.");
        Check(other!.VideoClips[0].TrackId == "other-v" && other.AudioClips[0].TrackId == "other-a" &&
              other.VideoClips[1].TrackId == other.AddedTracks.First(track => track.Kind == EditorTimelineTrackKind.Video).Id &&
              other.VideoClips[0].Track == EditorClipTrack.Primary && other.VideoClips[1].Track == EditorClipTrack.Overlay,
            "Cross-project paste lost track kinds or relative ordering.");
        Check(other.AddedTracks.All(track => !track.Locked && !track.Muted && !track.Solo) &&
              Near(other.AddedTracks.First(track => track.Kind == EditorTimelineTrackKind.Video).Height, 90) &&
              JsonSerializer.Serialize(otherTracks) == otherBefore, "New rows inherited source playback restrictions or planning mutated rows.");
        otherTracks[0].Locked = true;
        Check(!clipboard.TryCreatePaste(otherTracks, 3, 30, out var otherDenied, out _) && otherDenied is null,
            "Ordinal fallback ignored a locked destination.");
        Check(!clipboard.Capture([], [], tracks) && clipboard.ClipCount == 4, "An empty selection erased a useful clipboard.");
        var audioOnly = new EditorTimelineClipboardService();
        Check(audioOnly.Capture([], [audio[0]], tracks, fx) && audioOnly.TryCreatePaste([], -.25, double.NaN, out var audioPaste, out _) &&
              audioPaste is not null && audioPaste.VideoClips.Count == 0 && audioPaste.EffectLayers.Count == 0 && audioPaste.AddedTracks.Count == 1 &&
              audioPaste.AddedTracks[0].Kind == EditorTimelineTrackKind.Audio && audioPaste.AudioClips[0].SourceClipId == string.Empty &&
              Near(audioPaste.AudioClips[0].TimelineStartSeconds, 0), "Audio-only paste created video, retained original linkage or used an invalid position.");
        return Task.FromResult(checks);
    }
}
