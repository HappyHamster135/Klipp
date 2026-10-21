using System;
using System.Collections.Generic;
using System.Linq;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;

namespace Klipp.Desktop.Services;

/// <summary>An in-app clipboard of clip data. Media files and live project objects are never changed.</summary>
public sealed class EditorTimelineClipboardService
{
    private EditorProject? _contents;

    public int ClipCount => (_contents?.Clips.Count ?? 0) + (_contents?.AudioTracks.Count ?? 0);
    public bool HasClips => ClipCount > 0;

    public bool Capture(IEnumerable<EditorProjectClip> video, IEnumerable<EditorProjectAudioTrack> audio,
        IEnumerable<EditorTimelineTrack> tracks, IEnumerable<EditorProjectEffectLayer>? effectLayers = null)
    {
        var payload = new EditorProject { Clips = video.ToList(), AudioTracks = audio.ToList(), Tracks = tracks.ToList() };
        var targets = payload.Clips.Select(clip => clip.Id).ToHashSet(StringComparer.Ordinal);
        payload.EffectLayers = effectLayers?.Where(layer => targets.Contains(layer.TargetClipId)).ToList() ?? [];
        if (payload.Clips.Count + payload.AudioTracks.Count == 0)
            return false;
        // Use the project serializer so new clip attributes are included automatically.
        _contents = EditorProjectService.Clone(payload);
        return true;
    }

    public bool TryCreatePaste(IReadOnlyList<EditorTimelineTrack> destinationTracks, double playheadSeconds,
        double frameRate, out EditorTimelinePastePlan? plan, out string status)
    {
        plan = null;
        if (!HasClips)
        {
            status = "Copy video or audio clips before pasting.";
            return false;
        }
        var contents = _contents!;
        var usedTracks = contents.Clips.Select(clip => (clip.TrackId, Kind: EditorTimelineTrackKind.Video))
            .Concat(contents.AudioTracks.Select(clip => (clip.TrackId, Kind: EditorTimelineTrackKind.Audio))).Distinct().ToList();
        var mappedTracks = new Dictionary<(string, EditorTimelineTrackKind), EditorTimelineTrack>();
        var addedTracks = new List<EditorTimelineTrack>();
        var claimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var kind in new[] { EditorTimelineTrackKind.Video, EditorTimelineTrackKind.Audio })
        {
            var source = contents.Tracks.Where(track => track.Kind == kind).OrderBy(track => track.Order).ToList();
            var destination = destinationTracks.Where(track => track.Kind == kind).OrderBy(track => track.Order).ToList();
            var used = usedTracks.Where(item => item.Kind == kind)
                .OrderBy(item => source.FindIndex(track => track.Id == item.TrackId)).ToList();
            // Exact track IDs take priority over ordinal fallbacks, including IDs captured in another project.
            foreach (var item in used)
            {
                var exact = destination.FirstOrDefault(track => track.Id == item.TrackId);
                if (exact is null) continue;
                mappedTracks[item] = exact;
                claimed.Add(exact.Id);
            }
            foreach (var item in used.Where(item => !mappedTracks.ContainsKey(item)))
            {
                var sourceIndex = source.FindIndex(track => track.Id == item.TrackId);
                var candidate = sourceIndex >= 0 && sourceIndex < destination.Count && !claimed.Contains(destination[sourceIndex].Id)
                    ? destination[sourceIndex] : null;
                // Reuse another existing row before adding a row that is not needed.
                candidate ??= destination.FirstOrDefault(track => !claimed.Contains(track.Id));
                if (candidate is null)
                {
                    var order = destinationTracks.Concat(addedTracks).Where(track => track.Kind == kind)
                        .Select(track => track.Order).DefaultIfEmpty(-1).Max() + 1;
                    var original = source.FirstOrDefault(track => track.Id == item.TrackId);
                    candidate = new EditorTimelineTrack
                    {
                        Id = Guid.NewGuid().ToString("N"), Kind = kind, Order = order,
                        Name = $"{(kind == EditorTimelineTrackKind.Video ? "V" : "A")}{order + 1}",
                        Height = original?.Height ?? 44,
                        SyncLocked = original?.SyncLocked ?? true
                    };
                    addedTracks.Add(candidate);
                }
                mappedTracks[item] = candidate;
                claimed.Add(candidate.Id);
            }
        }
        var locked = mappedTracks.Values.FirstOrDefault(track => track.Locked);
        if (locked is not null)
        {
            status = $"Unlock {locked.Name} before pasting. No clips were pasted.";
            return false;
        }

        var pasted = EditorProjectService.Clone(contents);
        var earliest = pasted.Clips.Select(clip => clip.TimelineStartSeconds)
            .Concat(pasted.AudioTracks.Select(clip => clip.TimelineStartSeconds)).Min();
        var rate = double.IsFinite(frameRate) ? Math.Clamp(frameRate, 1, 240) : 30;
        var anchor = EditorTimelineMath.SnapToFrame(Math.Max(0, double.IsFinite(playheadSeconds) ? playheadSeconds : 0), rate);
        var delta = anchor - earliest;
        var identities = new Dictionary<string, string>(StringComparer.Ordinal);
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        string NewGroup(string original)
        {
            if (string.IsNullOrEmpty(original)) return string.Empty;
            if (!groups.TryGetValue(original, out var id)) groups[original] = id = Guid.NewGuid().ToString("N");
            return id;
        }
        foreach (var clip in pasted.Clips)
        {
            var newId = Guid.NewGuid().ToString("N");
            identities[clip.Id] = newId;
            clip.Id = newId;
            clip.GroupId = NewGroup(clip.GroupId);
            clip.TimelineStartSeconds = Math.Max(0, clip.TimelineStartSeconds + delta);
            clip.TrackId = mappedTracks[(clip.TrackId, EditorTimelineTrackKind.Video)].Id;
        }
        foreach (var clip in pasted.AudioTracks)
        {
            clip.Id = Guid.NewGuid().ToString("N");
            clip.GroupId = NewGroup(clip.GroupId);
            // Audio pasted on its own must not become linked to an original clip left behind.
            clip.SourceClipId = identities.GetValueOrDefault(clip.SourceClipId, string.Empty);
            clip.TimelineStartSeconds = Math.Max(0, clip.TimelineStartSeconds + delta);
            clip.TrackId = mappedTracks[(clip.TrackId, EditorTimelineTrackKind.Audio)].Id;
        }
        foreach (var effect in pasted.EffectLayers.ToArray())
        {
            if (!identities.TryGetValue(effect.TargetClipId, out var targetId))
            {
                pasted.EffectLayers.Remove(effect);
                continue;
            }
            effect.Id = Guid.NewGuid().ToString("N");
            effect.TargetClipId = targetId;
            effect.StartSeconds = Math.Max(0, effect.StartSeconds + delta);
            effect.EndSeconds = Math.Max(effect.StartSeconds, effect.EndSeconds + delta);
        }
        var primaryId = destinationTracks.Concat(addedTracks).Where(track => track.Kind == EditorTimelineTrackKind.Video)
            .OrderBy(track => track.Order).FirstOrDefault()?.Id;
        foreach (var clip in pasted.Clips)
            clip.Track = clip.TrackId == primaryId ? EditorClipTrack.Primary : EditorClipTrack.Overlay;
        plan = new EditorTimelinePastePlan(pasted.Clips, pasted.AudioTracks, addedTracks)
        {
            EffectLayers = pasted.EffectLayers
        };
        status = $"{ClipCount} {(ClipCount == 1 ? "clip" : "clips")} pasted at the playhead.";
        return true;
    }
}

public sealed record EditorTimelinePastePlan(IReadOnlyList<EditorProjectClip> VideoClips,
    IReadOnlyList<EditorProjectAudioTrack> AudioClips, IReadOnlyList<EditorTimelineTrack> AddedTracks)
{
    public IReadOnlyList<EditorProjectEffectLayer> EffectLayers { get; init; } = [];
}
