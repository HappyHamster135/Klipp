using System;
using System.Collections.Generic;
using System.Linq;
using Klipp.Desktop.Models;

namespace Klipp.Desktop.Services;

/// <summary>Creates independent source audio once, without extracting or copying the media file.</summary>
public static class EditorSourceAudioService
{
    public static EditorProjectAudioTrack? SeparateClipAudio(EditorProject project, EditorProjectClip clip, bool hasAudio)
        => SeparateClipAudioStreams(project, clip, hasAudio ? [new MediaAudioStreamInfo(0, string.Empty, string.Empty)] : []).FirstOrDefault();

    public static IReadOnlyList<MediaAudioStreamInfo> GetEditableStreams(IReadOnlyList<MediaAudioStreamInfo> streams)
    {
        // The first track is a compatibility mix. Editing that alongside its
        // stems would play each source twice, so prefer only the named stems.
        var stems = streams.Where(stream => stream.CaptureRole is "game audio" or "microphone" or "discord" or "music").ToList();
        return stems.Count > 0 ? stems : streams.Take(1).ToList();
    }

    public static IReadOnlyList<EditorProjectAudioTrack> SeparateClipAudioStreams(
        EditorProject project, EditorProjectClip clip, IReadOnlyList<MediaAudioStreamInfo> streams)
    {
        if (clip.SourceAudioSeparated)
            return [];

        // Older projects detached audio by muting the video. Adopt that existing
        // segment so opening the project cannot duplicate its soundtrack.
        var existing = project.AudioTracks.FirstOrDefault(track => track.SourceClipId == clip.Id) ??
            (clip.Muted ? project.AudioTracks.FirstOrDefault(track =>
                string.IsNullOrEmpty(track.SourceClipId) &&
                string.Equals(track.SourcePath, clip.SourcePath, StringComparison.OrdinalIgnoreCase) &&
                Math.Abs(track.TimelineStartSeconds - clip.TimelineStartSeconds) < 0.01 &&
                Math.Abs(track.TrimStartSeconds - clip.TrimStartSeconds) < 0.01 &&
                Math.Abs(track.TrimEndSeconds - clip.TrimEndSeconds) < 0.01 &&
                Math.Abs(track.PlaybackSpeed - clip.PlaybackSpeed) < 0.001) : null);
        clip.SourceAudioSeparated = true;
        if (existing is not null)
        {
            existing.SourceClipId = clip.Id;
            return [existing];
        }
        var created = new List<EditorProjectAudioTrack>();
        foreach (var stream in GetEditableStreams(streams))
        {
            var audio = CreateSourceAudio(project, clip);
            audio.SourceAudioStreamIndex = stream.Index;
            audio.SourceAudioRole = stream.CaptureRole switch { "microphone" => "microphone", "game audio" => "system", "discord" => "discord", "music" => "music", _ => string.Empty };
            var roleName = stream.CaptureRole switch { "game audio" => "Game audio", "microphone" => "Microphone", "discord" => "Discord", "music" => "Music", _ => null };
            if (roleName is not null) audio.Name = $"{roleName} · {clip.Name}";
            AssignAvailableAudioTrack(project, audio, roleName);
            project.AudioTracks.Add(audio);
            created.Add(audio);
        }
        return created;
    }

    private static EditorProjectAudioTrack CreateSourceAudio(EditorProject project, EditorProjectClip clip)
    {
        var audio = new EditorProjectAudioTrack
        {
            Name = clip.Name,
            SourceClipId = clip.Id,
            SourcePath = clip.SourcePath,
            SourceDurationSeconds = clip.SourceDurationSeconds,
            TrimStartSeconds = clip.TrimStartSeconds,
            TrimEndSeconds = clip.TrimEndSeconds,
            TimelineStartSeconds = clip.TimelineStartSeconds,
            PlaybackSpeed = clip.PlaybackSpeed,
            Muted = clip.Muted,
            Volume = clip.Volume,
            VolumeKeyframes = clip.VolumeKeyframes.Select(keyframe => keyframe.Clone()).ToList(),
            NormalizeAudio = clip.NormalizeAudio,
            NormalizationGainDb = clip.NormalizationGainDb,
            IntegratedLoudnessLufs = clip.IntegratedLoudnessLufs,
            LoudnessAnalysisTrimStartSeconds = clip.LoudnessAnalysisTrimStartSeconds,
            LoudnessAnalysisTrimEndSeconds = clip.LoudnessAnalysisTrimEndSeconds,
            NoiseReductionEnabled = clip.NoiseReductionEnabled,
            NoiseReductionStrength = clip.NoiseReductionStrength
        };
        if (clip.Track == EditorClipTrack.Primary)
        {
            if (project.GetTransitionPredecessor(clip) is { } previous)
                audio.FadeInSeconds = project.GetTransitionOverlapSeconds(previous, clip);
            audio.FadeOutSeconds = project.PrimaryClips
                .Where(incoming => project.GetTransitionPredecessor(incoming)?.Id == clip.Id)
                .Select(incoming => project.GetTransitionOverlapSeconds(clip, incoming))
                .DefaultIfEmpty(0).Max();
        }
        audio.Normalize();
        return audio;
    }

    public static void AssignAvailableAudioTrack(EditorProject project, EditorProjectAudioTrack audio, string? roleName = null)
    {
        var start = audio.TimelineStartSeconds;
        var end = start + audio.EffectiveDurationSeconds;
        var available = project.Tracks.Where(track => track.Kind == EditorTimelineTrackKind.Audio && !track.Locked &&
                (roleName is null || track.Name == roleName || !project.AudioTracks.Any(existing => existing.TrackId == track.Id)))
            .OrderBy(track => track.Order).FirstOrDefault(track => !project.AudioTracks.Any(existing =>
                existing.Id != audio.Id && existing.TrackId == track.Id &&
                existing.TimelineStartSeconds < end - 0.00001 &&
                existing.TimelineStartSeconds + existing.EffectiveDurationSeconds > start + 0.00001));
        if (available is null)
        {
            var order = project.Tracks.Where(track => track.Kind == EditorTimelineTrackKind.Audio)
                .Select(track => track.Order).DefaultIfEmpty(-1).Max() + 1;
            available = new EditorTimelineTrack
            {
                Id = Guid.NewGuid().ToString("N"), Name = $"A{order + 1}",
                Kind = EditorTimelineTrackKind.Audio, Order = order
            };
            project.Tracks.Add(available);
        }
        if (roleName is not null && !project.AudioTracks.Any(existing => existing.TrackId == available.Id))
            available.Name = roleName;
        audio.TrackId = available.Id;
    }
}
