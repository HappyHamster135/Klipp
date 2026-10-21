using System;
using System.Collections.Generic;
using System.Linq;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;

namespace Klipp.Desktop.Controls;

public sealed partial class LayerTimelineEditor
{
    private bool HasPrecisionClipSelection =>
        _selectedLayer is EditorTimelineLayer.Media or EditorTimelineLayer.Audio && SelectedClipCount > 0;

    private (List<EditorProjectClip> Video, List<EditorProjectAudioTrack> Audio) PrecisionClipSelection() =>
        (_mediaClips.Where(clip => _selectedMediaClipIds.Contains(clip.Id)).ToList(),
            _audioTrackSegments.Where(clip => _selectedAudioTrackIds.Contains(clip.Id)).ToList());

    private bool PrecisionSelectionIsLocked(List<EditorProjectClip> video, List<EditorProjectAudioTrack> audio)
    {
        var groups = video.Select(clip => clip.GroupId).Concat(audio.Select(clip => clip.GroupId))
            .Where(group => !string.IsNullOrEmpty(group)).ToHashSet(StringComparer.Ordinal);
        var locked = video.Any(IsClipLocked) || audio.Any(IsAudioLocked) ||
            _mediaClips.Any(clip => groups.Contains(clip.GroupId) && IsClipLocked(clip)) ||
            _audioTrackSegments.Any(clip => groups.Contains(clip.GroupId) && IsAudioLocked(clip));
        if (locked)
            SetEditStatus("Unlock the selected clips' tracks before moving or trimming the selection.");
        return locked;
    }

    private bool TryGetPrecisionClipRange(out TimeSpan start, out TimeSpan end)
    {
        start = end = TimeSpan.Zero;
        if (!HasPrecisionClipSelection)
            return false;
        var (video, audio) = PrecisionClipSelection();
        var spans = video.Select(clip => (Start: clip.TimelineStartSeconds, End: clip.TimelineStartSeconds + clip.EffectiveDurationSeconds))
            .Concat(audio.Select(clip => (Start: clip.TimelineStartSeconds, End: clip.TimelineStartSeconds + clip.EffectiveDurationSeconds))).ToList();
        if (spans.Count == 0)
            return false;
        start = TimeSpan.FromSeconds(spans.Min(span => span.Start));
        end = TimeSpan.FromSeconds(spans.Max(span => span.End));
        return true;
    }

    private bool MovePrecisionClipSelection(TimeSpan requestedDelta)
    {
        var (video, audio) = PrecisionClipSelection();
        if (video.Count + audio.Count == 0 || PrecisionSelectionIsLocked(video, audio))
            return false;
        var delta = EditorAdvancedEditMath.ClampGroupMove(
            EditorTimelineMath.SnapToFrame(requestedDelta.TotalSeconds, _frameRate),
            video.Select(clip => clip.TimelineStartSeconds).Concat(audio.Select(clip => clip.TimelineStartSeconds)));
        if (Math.Abs(delta) < .00000001)
            return false;
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        foreach (var clip in video)
            clip.TimelineStartSeconds = Math.Max(0, clip.TimelineStartSeconds + delta);
        foreach (var clip in audio)
            clip.TimelineStartSeconds = Math.Max(0, clip.TimelineStartSeconds + delta);
        CompleteToolEdit(before, $"{video.Count + audio.Count} {(video.Count + audio.Count == 1 ? "clip" : "clips")} moved.");
        return true;
    }

    private bool TrimPrecisionClipSelection(TimeSpan requestedDelta, bool trimStart)
    {
        var (video, audio) = PrecisionClipSelection();
        if (video.Count + audio.Count == 0 || PrecisionSelectionIsLocked(video, audio))
            return false;
        var ranges = video.Select(clip => new EditorSelectionTrimRange(clip.TimelineStartSeconds,
                clip.TrimStartSeconds, clip.TrimEndSeconds, clip.SourceDurationSeconds, clip.PlaybackSpeed))
            .Concat(audio.Select(clip => new EditorSelectionTrimRange(clip.TimelineStartSeconds,
                clip.TrimStartSeconds, clip.TrimEndSeconds, clip.SourceDurationSeconds, clip.PlaybackSpeed)));
        var delta = EditorSelectionEditMath.ClampTrimDelta(
            EditorTimelineMath.SnapToFrame(requestedDelta.TotalSeconds, _frameRate), trimStart, ranges, MinClipSeconds);
        if (Math.Abs(delta) < .00000001)
            return false;
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        foreach (var clip in video)
        {
            var oldDuration = clip.EffectiveDurationSeconds;
            var fallback = new EditorProjectClipTransformState(clip.Scale, clip.OffsetX, clip.OffsetY);
            if (trimStart)
            {
                clip.TrimStartSeconds += delta * clip.PlaybackSpeed;
                clip.TimelineStartSeconds = Math.Max(0, clip.TimelineStartSeconds + delta);
                clip.TransformKeyframes = RetimeTransformKeyframesForStartTrim(clip.TransformKeyframes, fallback, delta);
                clip.VolumeKeyframes = RetimeVolumeKeyframesForStartTrim(clip.VolumeKeyframes, clip.Volume, delta);
                clip.FilterStrengthKeyframes = RetimeFilterKeyframesForStartTrim(clip.FilterStrengthKeyframes, clip.FilterStrength, delta);
            }
            else
            {
                clip.TrimEndSeconds += delta * clip.PlaybackSpeed;
                clip.TransformKeyframes = RetimeTransformKeyframesForEndTrim(clip.TransformKeyframes, fallback, clip.EffectiveDurationSeconds, oldDuration);
                clip.VolumeKeyframes = RetimeVolumeKeyframesForEndTrim(clip.VolumeKeyframes, clip.Volume, clip.EffectiveDurationSeconds, oldDuration);
                clip.FilterStrengthKeyframes = RetimeFilterKeyframesForEndTrim(clip.FilterStrengthKeyframes, clip.FilterStrength, clip.EffectiveDurationSeconds, oldDuration);
            }
            InvalidateLoudnessAnalysis(clip);
        }
        foreach (var clip in audio)
        {
            var oldDuration = clip.EffectiveDurationSeconds;
            if (trimStart)
            {
                clip.TrimStartSeconds += delta * clip.PlaybackSpeed;
                clip.TimelineStartSeconds = Math.Max(0, clip.TimelineStartSeconds + delta);
                clip.VolumeKeyframes = RetimeVolumeKeyframesForStartTrim(clip.VolumeKeyframes, clip.Volume, delta);
            }
            else
            {
                clip.TrimEndSeconds += delta * clip.PlaybackSpeed;
                clip.VolumeKeyframes = RetimeVolumeKeyframesForEndTrim(clip.VolumeKeyframes, clip.Volume, clip.EffectiveDurationSeconds, oldDuration);
            }
            InvalidateAudioLoudnessAnalysis(clip);
            clip.Normalize();
        }
        CompleteToolEdit(before, $"Selected {(trimStart ? "starts" : "ends")} trimmed.");
        return true;
    }
}
