using System;
using System.Linq;
using Klipp.Core.Editing;

namespace Klipp.Desktop.Controls;

public sealed partial class LayerTimelineEditor
{
    private bool _dragLinkedTrimChanged;

    private void ApplyLinkedSelectionTrim()
    {
        if (!_pointerDragActivated || !LinkedSelectionEnabled || _dragUndoSnapshot is null ||
            EditTool != EditorTimelineEditTool.Selection) return;
        var originalVideo = _dragUndoSnapshot.MediaClips.FirstOrDefault(clip => clip.Id == _dragMediaClipId);
        var originalAudio = _dragUndoSnapshot.AudioTracks.FirstOrDefault(clip => clip.Id == _dragAudioTrackId);
        if (originalVideo is null && originalAudio is null) return;
        var leading = _dragMode is DragMode.MediaTrimStart or DragMode.AudioTrimStart;
        var video = originalVideo is null ? null : GetDraggedMediaClip();
        var audio = originalAudio is null ? null : _audioTrackSegments.FirstOrDefault(clip => clip.Id == originalAudio.Id);
        var originalStart = originalVideo?.TimelineStartSeconds ?? originalAudio!.TimelineStartSeconds;
        var requested = leading
            ? (video?.TimelineStartSeconds ?? audio!.TimelineStartSeconds) - originalStart
            : (video?.EffectiveDurationSeconds ?? audio!.EffectiveDurationSeconds) -
                (originalVideo?.EffectiveDurationSeconds ?? originalAudio!.EffectiveDurationSeconds);
        var spans = _dragUndoSnapshot.MediaClips.Where(clip => _selectedMediaClipIds.Contains(clip.Id) && !IsClipLocked(clip))
            .Select(clip => new EditorRippleSourceSpan("v:" + clip.Id, clip.TrackId, "", true,
                clip.TimelineStartSeconds, clip.EffectiveDurationSeconds, clip.TrimStartSeconds, clip.TrimEndSeconds,
                clip.SourceDurationSeconds, clip.PlaybackSpeed))
            .Concat(_dragUndoSnapshot.AudioTracks.Where(clip => _selectedAudioTrackIds.Contains(clip.Id) && !IsAudioLocked(clip))
                .Select(clip => new EditorRippleSourceSpan("a:" + clip.Id, clip.TrackId,
                    string.IsNullOrEmpty(clip.SourceClipId) ? "" : "v:" + clip.SourceClipId, false,
                    clip.TimelineStartSeconds, clip.EffectiveDurationSeconds, clip.TrimStartSeconds, clip.TrimEndSeconds,
                    clip.SourceDurationSeconds, clip.PlaybackSpeed)));
        var plan = EditorLinkedTrimMath.Plan(originalVideo is not null ? "v:" + originalVideo.Id : "a:" + originalAudio!.Id,
            requested, leading, spans);
        if (plan.ClipIds.Count < 2) return;
        _dragLinkedTrimChanged = true;
        var durationDelta = leading ? -plan.EdgeDelta : plan.EdgeDelta;
        foreach (var original in _dragUndoSnapshot.MediaClips.Where(clip => plan.ClipIds.Contains("v:" + clip.Id)))
        {
            if (Math.Abs(plan.EdgeDelta) < .00000001)
            {
                _mediaClips[_mediaClips.FindIndex(clip => clip.Id == original.Id)] = CloneClip(original);
                continue;
            }
            var current = _mediaClips.First(clip => clip.Id == original.Id);
            ApplySyncedVideoTrim(current, original, durationDelta, leading);
            current.TimelineStartSeconds = original.TimelineStartSeconds + (leading ? plan.EdgeDelta : 0);
        }
        foreach (var original in _dragUndoSnapshot.AudioTracks.Where(clip => plan.ClipIds.Contains("a:" + clip.Id)))
        {
            if (Math.Abs(plan.EdgeDelta) < .00000001)
            {
                _audioTrackSegments[_audioTrackSegments.FindIndex(clip => clip.Id == original.Id)] = CloneAudioTrack(original);
                continue;
            }
            var current = _audioTrackSegments.First(clip => clip.Id == original.Id);
            ApplySyncedAudioTrim(current, original, durationDelta, leading);
            current.TimelineStartSeconds = original.TimelineStartSeconds + (leading ? plan.EdgeDelta : 0);
            current.FadeInSeconds = original.FadeInSeconds;
            current.FadeOutSeconds = original.FadeOutSeconds;
            current.Normalize();
        }
        _mediaTimelineStartsDirty = true;
        RecalculateDurationFromMedia(false);
    }
}
