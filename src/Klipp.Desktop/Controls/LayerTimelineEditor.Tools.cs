using System;
using System.Collections.Generic;
using System.Linq;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;

namespace Klipp.Desktop.Controls;

public sealed partial class LayerTimelineEditor
{
    public bool RippleDeleteSelection() => DeleteMixedSelection(ripple: true);

    public bool CloseGapAtPlayhead()
    {
        var trackId = _selectedTrackId ?? GetSingleSelectedMediaClip()?.TrackId ?? GetSelectedAudioTrack()?.TrackId;
        trackId ??= _tracks.Where(track => track.Kind == EditorTimelineTrackKind.Video).OrderBy(track => track.Order).First().Id;
        if (IsTrackLocked(trackId))
            return false;
        var spans = _mediaClips.Where(clip => clip.TrackId == trackId)
            .Select(clip => (Start: clip.TimelineStartSeconds, End: clip.TimelineStartSeconds + clip.EffectiveDurationSeconds))
            .Concat(_audioTrackSegments.Where(clip => clip.TrackId == trackId)
                .Select(clip => (Start: clip.TimelineStartSeconds, End: clip.TimelineStartSeconds + clip.EffectiveDurationSeconds))).ToList();
        var head = _playhead.TotalSeconds;
        if (spans.Any(span => head >= span.Start && head < span.End))
        { SetEditStatus("Place the playhead inside a gap on the selected track."); return false; }
        var previousEnd = spans.Where(span => span.End <= head).Select(span => span.End).DefaultIfEmpty(0).Max();
        var nextStart = spans.Where(span => span.Start >= head).Select(span => span.Start).DefaultIfEmpty(double.NaN).Min();
        if (!double.IsFinite(nextStart) || nextStart - previousEnd < .00001)
            return false;
        var plan = CreateRippleRemovalPlan([(trackId, previousEnd, nextStart)]);
        if (plan.Intervals.Count == 0)
        {
            SetEditStatus("A sync-locked track has media in this gap. Split or move that media, or turn off its Sync lock, before closing the gap.");
            return false;
        }
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        ApplyRippleRemovalPlan(plan);
        CompleteToolEdit(before, plan.RemovedSeconds < nextStart - previousEnd - 1 / _frameRate
            ? "Shared empty space closed. Media on sync-locked tracks was kept intact."
            : "Gap closed across participating tracks.");
        return true;
    }

    private IEnumerable<EditorRippleTrackState> RippleTrackStates => _tracks.Select(track =>
        new EditorRippleTrackState(track.Id, track.SyncLocked, track.Locked));

    private EditorRippleRemovalPlan CreateRippleRemovalPlan(IEnumerable<(string TrackId, double Start, double End)> removed) =>
        EditorRippleSyncMath.PlanRemoval(RippleTrackStates,
            removed.Select(interval => new EditorRippleSpan(interval.TrackId, interval.Start, interval.End)),
            _mediaClips.Select(clip => new EditorRippleSpan(clip.TrackId, clip.TimelineStartSeconds,
                    clip.TimelineStartSeconds + clip.EffectiveDurationSeconds))
                .Concat(_audioTrackSegments.Select(clip => new EditorRippleSpan(clip.TrackId, clip.TimelineStartSeconds,
                    clip.TimelineStartSeconds + clip.EffectiveDurationSeconds))), _frameRate);

    private double RippleRemovedIntervals(IEnumerable<(string TrackId, double Start, double End)> removed)
    {
        var plan = CreateRippleRemovalPlan(removed);
        ApplyRippleRemovalPlan(plan);
        return plan.RemovedSeconds;
    }

    private void ApplyRippleRemovalPlan(EditorRippleRemovalPlan plan)
    {
        foreach (var clip in _mediaClips.Where(clip => plan.TrackIds.Contains(clip.TrackId)))
            clip.TimelineStartSeconds = EditorAdvancedEditMath.RippleStart(clip.TimelineStartSeconds, plan.Intervals);
        foreach (var clip in _audioTrackSegments.Where(clip => plan.TrackIds.Contains(clip.TrackId)))
            clip.TimelineStartSeconds = EditorAdvancedEditMath.RippleStart(clip.TimelineStartSeconds, plan.Intervals);
        _mediaTimelineStartsDirty = true;
    }

    private void CompleteToolEdit(TimelineSnapshot before, string status)
    {
        NormalizeMediaClips();
        RecalculateDurationFromMedia(false, allowEmpty: true);
        RebuildMediaClipBlocks();
        RebuildAudioTrackSegmentBlocks();
        CompleteSelectionEdit(before, status);
    }

    private void ApplyTrimTool(TimeSpan pointerTime)
    {
        if (_dragUndoSnapshot is null || !_pointerDragActivated || EditTool == EditorTimelineEditTool.Selection)
            return;
        if (EditTool == EditorTimelineEditTool.RollingTrim)
        { ApplyRollingTrim(pointerTime); return; }
        if (EditTool != EditorTimelineEditTool.RippleTrim)
            return;
        var originalVideo = _dragUndoSnapshot.MediaClips.FirstOrDefault(clip => clip.Id == _dragMediaClipId);
        var originalAudio = _dragUndoSnapshot.AudioTracks.FirstOrDefault(clip => clip.Id == _dragAudioTrackId);
        var originalStart = originalVideo?.TimelineStartSeconds ?? originalAudio?.TimelineStartSeconds;
        var originalDuration = originalVideo?.EffectiveDurationSeconds ?? originalAudio?.EffectiveDurationSeconds;
        if (!originalStart.HasValue || !originalDuration.HasValue)
            return;
        var video = originalVideo is null ? null : _mediaClips.First(clip => clip.Id == originalVideo.Id);
        var audio = originalAudio is null ? null : _audioTrackSegments.First(clip => clip.Id == originalAudio.Id);
        var duration = video?.EffectiveDurationSeconds ?? audio!.EffectiveDurationSeconds;
        var requestedDelta = duration - originalDuration.Value;
        var originalEnd = originalStart.Value + originalDuration.Value;
        var leading = _dragMode is DragMode.MediaTrimStart or DragMode.AudioTrimStart;
        var plan = EditorRippleSyncMath.PlanSourceTrim(RippleTrackStates,
            originalVideo is not null ? "v:" + originalVideo.Id : "a:" + originalAudio!.Id, requestedDelta, leading,
            _dragUndoSnapshot.MediaClips.Select(clip => new EditorRippleSourceSpan("v:" + clip.Id, clip.TrackId, "", true,
                    clip.TimelineStartSeconds, clip.EffectiveDurationSeconds, clip.TrimStartSeconds, clip.TrimEndSeconds, clip.SourceDurationSeconds, clip.PlaybackSpeed))
                .Concat(_dragUndoSnapshot.AudioTracks.Select(clip => new EditorRippleSourceSpan("a:" + clip.Id, clip.TrackId, "v:" + clip.SourceClipId, false,
                    clip.TimelineStartSeconds, clip.EffectiveDurationSeconds, clip.TrimStartSeconds, clip.TrimEndSeconds, clip.SourceDurationSeconds, clip.PlaybackSpeed))), _frameRate);
        var delta = plan.Delta;
        foreach (var previous in _dragUndoSnapshot.MediaClips.Where(clip => plan.TrimmedIds.Contains("v:" + clip.Id)))
        {
            var index = _mediaClips.FindIndex(clip => clip.Id == previous.Id);
            if (Math.Abs(delta) < .00000001) _mediaClips[index] = CloneClip(previous);
            else ApplySyncedVideoTrim(_mediaClips[index], previous, delta, leading);
        }
        foreach (var previous in _dragUndoSnapshot.AudioTracks.Where(clip => plan.TrimmedIds.Contains("a:" + clip.Id)))
        {
            var index = _audioTrackSegments.FindIndex(clip => clip.Id == previous.Id);
            if (Math.Abs(delta) < .00000001) _audioTrackSegments[index] = CloneAudioTrack(previous);
            else ApplySyncedAudioTrim(_audioTrackSegments[index], previous, delta, leading);
        }
        // Always derive follower positions from the drag snapshot: reversing a drag
        // must restore earlier positions instead of accumulating previous deltas.
        foreach (var previous in _dragUndoSnapshot.MediaClips.Where(clip => plan.TrackIds.Contains(clip.TrackId) && !plan.TrimmedIds.Contains("v:" + clip.Id)))
            _mediaClips.First(clip => clip.Id == previous.Id).TimelineStartSeconds = EditorAdvancedEditMath.RippleTrimFollowerStart(previous.TimelineStartSeconds, originalEnd, delta);
        foreach (var previous in _dragUndoSnapshot.AudioTracks.Where(clip => plan.TrackIds.Contains(clip.TrackId) && !plan.TrimmedIds.Contains("a:" + clip.Id)))
            _audioTrackSegments.First(clip => clip.Id == previous.Id).TimelineStartSeconds = EditorAdvancedEditMath.RippleTrimFollowerStart(previous.TimelineStartSeconds, originalEnd, delta);
        if (Math.Abs(delta - requestedDelta) > .00001)
            SetEditStatus("Ripple trim stopped at media on a sync-locked track. Split or move that media, or turn off its Sync lock, to trim further.");
        _mediaTimelineStartsDirty = true;
        RecalculateDurationFromMedia(false);
    }

    private static void ApplySyncedVideoTrim(EditorProjectClip current, EditorProjectClip original, double delta, bool leading)
    {
        current.TimelineStartSeconds = original.TimelineStartSeconds;
        current.TrimStartSeconds = original.TrimStartSeconds - (leading ? delta * original.PlaybackSpeed : 0);
        current.TrimEndSeconds = original.TrimEndSeconds + (leading ? 0 : delta * original.PlaybackSpeed);
        var fallback = new EditorProjectClipTransformState(original.Scale, original.OffsetX, original.OffsetY);
        current.TransformKeyframes = leading
            ? RetimeTransformKeyframesForStartTrim(original.TransformKeyframes, fallback, -delta)
            : RetimeTransformKeyframesForEndTrim(original.TransformKeyframes, fallback, current.EffectiveDurationSeconds, original.EffectiveDurationSeconds);
        current.VolumeKeyframes = leading
            ? RetimeVolumeKeyframesForStartTrim(original.VolumeKeyframes, original.Volume, -delta)
            : RetimeVolumeKeyframesForEndTrim(original.VolumeKeyframes, original.Volume, current.EffectiveDurationSeconds, original.EffectiveDurationSeconds);
        current.FilterStrengthKeyframes = leading
            ? RetimeFilterKeyframesForStartTrim(original.FilterStrengthKeyframes, original.FilterStrength, -delta)
            : RetimeFilterKeyframesForEndTrim(original.FilterStrengthKeyframes, original.FilterStrength, current.EffectiveDurationSeconds, original.EffectiveDurationSeconds);
        InvalidateLoudnessAnalysis(current);
    }

    private static void ApplySyncedAudioTrim(EditorProjectAudioTrack current, EditorProjectAudioTrack original, double delta, bool leading)
    {
        current.TimelineStartSeconds = original.TimelineStartSeconds;
        current.TrimStartSeconds = original.TrimStartSeconds - (leading ? delta * original.PlaybackSpeed : 0);
        current.TrimEndSeconds = original.TrimEndSeconds + (leading ? 0 : delta * original.PlaybackSpeed);
        current.VolumeKeyframes = leading
            ? RetimeVolumeKeyframesForStartTrim(original.VolumeKeyframes, original.Volume, -delta)
            : RetimeVolumeKeyframesForEndTrim(original.VolumeKeyframes, original.Volume, current.EffectiveDurationSeconds, original.EffectiveDurationSeconds);
        InvalidateAudioLoudnessAnalysis(current);
    }

    private void ApplyRollingTrim(TimeSpan pointerTime)
    {
        if (_dragUndoSnapshot is null)
            return;
        var leading = _dragMode is DragMode.MediaTrimStart or DragMode.AudioTrimStart;
        var original = _dragUndoSnapshot.MediaClips.FirstOrDefault(clip => clip.Id == _dragMediaClipId);
        if (original is not null)
        {
            var peers = _dragUndoSnapshot.MediaClips.Where(clip => clip.TrackId == original.TrackId).OrderBy(clip => clip.TimelineStartSeconds).ToList();
            var index = peers.FindIndex(clip => clip.Id == original.Id);
            var left = leading ? peers.ElementAtOrDefault(index - 1) : original;
            var right = leading ? original : peers.ElementAtOrDefault(index + 1);
            if (left is null || right is null || IsClipLocked(left) || IsClipLocked(right) ||
                Math.Abs(left.TimelineStartSeconds + left.EffectiveDurationSeconds - right.TimelineStartSeconds) > .001)
            { RestoreRollingOriginal(original); SetEditStatus("Rolling trim needs two touching, unlocked clips on the same track."); return; }
            var delta = EditorAdvancedEditMath.ClampRollingDelta(pointerTime.TotalSeconds - right.TimelineStartSeconds,
                left.TrimStartSeconds, left.TrimEndSeconds, left.SourceDurationSeconds, left.PlaybackSpeed,
                right.TrimStartSeconds, right.TrimEndSeconds, right.PlaybackSpeed);
            var currentLeft = _mediaClips.First(clip => clip.Id == left.Id);
            var currentRight = _mediaClips.First(clip => clip.Id == right.Id);
            currentLeft.TrimEndSeconds = left.TrimEndSeconds + delta * left.PlaybackSpeed;
            currentRight.TrimStartSeconds = right.TrimStartSeconds + delta * right.PlaybackSpeed;
            currentRight.TimelineStartSeconds = right.TimelineStartSeconds + delta;
            currentLeft.TransformKeyframes = RetimeTransformKeyframesForEndTrim(left.TransformKeyframes,
                new(left.Scale, left.OffsetX, left.OffsetY), currentLeft.EffectiveDurationSeconds, left.EffectiveDurationSeconds);
            currentLeft.VolumeKeyframes = RetimeVolumeKeyframesForEndTrim(left.VolumeKeyframes, left.Volume, currentLeft.EffectiveDurationSeconds, left.EffectiveDurationSeconds);
            currentLeft.FilterStrengthKeyframes = RetimeFilterKeyframesForEndTrim(left.FilterStrengthKeyframes, left.FilterStrength, currentLeft.EffectiveDurationSeconds, left.EffectiveDurationSeconds);
            currentRight.TransformKeyframes = RetimeTransformKeyframesForStartTrim(right.TransformKeyframes, new(right.Scale, right.OffsetX, right.OffsetY), delta);
            currentRight.VolumeKeyframes = RetimeVolumeKeyframesForStartTrim(right.VolumeKeyframes, right.Volume, delta);
            currentRight.FilterStrengthKeyframes = RetimeFilterKeyframesForStartTrim(right.FilterStrengthKeyframes, right.FilterStrength, delta);
            InvalidateLoudnessAnalysis(currentLeft);
            InvalidateLoudnessAnalysis(currentRight);
        }
        else
        {
            var source = _dragUndoSnapshot.AudioTracks.FirstOrDefault(clip => clip.Id == _dragAudioTrackId);
            if (source is null) return;
            var peers = _dragUndoSnapshot.AudioTracks.Where(clip => clip.TrackId == source.TrackId).OrderBy(clip => clip.TimelineStartSeconds).ToList();
            var index = peers.FindIndex(clip => clip.Id == source.Id);
            var left = leading ? peers.ElementAtOrDefault(index - 1) : source;
            var right = leading ? source : peers.ElementAtOrDefault(index + 1);
            if (left is null || right is null || IsAudioLocked(left) || IsAudioLocked(right) ||
                Math.Abs(left.TimelineStartSeconds + left.EffectiveDurationSeconds - right.TimelineStartSeconds) > .001)
            {
                var unchanged = _audioTrackSegments.FindIndex(clip => clip.Id == source.Id);
                _audioTrackSegments[unchanged] = CloneAudioTrack(source);
                RecalculateDurationFromMedia(false);
                SetEditStatus("Rolling trim needs two touching, unlocked clips on the same track.");
                return;
            }
            var delta = EditorAdvancedEditMath.ClampRollingDelta(pointerTime.TotalSeconds - right.TimelineStartSeconds,
                left.TrimStartSeconds, left.TrimEndSeconds, left.SourceDurationSeconds, left.PlaybackSpeed,
                right.TrimStartSeconds, right.TrimEndSeconds, right.PlaybackSpeed);
            var currentLeft = _audioTrackSegments.First(clip => clip.Id == left.Id);
            var currentRight = _audioTrackSegments.First(clip => clip.Id == right.Id);
            currentLeft.TrimEndSeconds = left.TrimEndSeconds + delta * left.PlaybackSpeed;
            currentRight.TrimStartSeconds = right.TrimStartSeconds + delta * right.PlaybackSpeed;
            currentRight.TimelineStartSeconds = right.TimelineStartSeconds + delta;
            currentLeft.VolumeKeyframes = RetimeVolumeKeyframesForEndTrim(left.VolumeKeyframes, left.Volume, currentLeft.EffectiveDurationSeconds, left.EffectiveDurationSeconds);
            currentRight.VolumeKeyframes = RetimeVolumeKeyframesForStartTrim(right.VolumeKeyframes, right.Volume, delta);
            InvalidateAudioLoudnessAnalysis(currentLeft);
            InvalidateAudioLoudnessAnalysis(currentRight);
        }
        _mediaTimelineStartsDirty = true;
        RecalculateDurationFromMedia(false);
    }

    private void RestoreRollingOriginal(EditorProjectClip original)
    {
        _mediaClips[_mediaClips.FindIndex(clip => clip.Id == original.Id)] = CloneClip(original);
        _mediaTimelineStartsDirty = true;
        RecalculateDurationFromMedia(false);
    }

    private TimeSpan SlipSelectedClip(TimeSpan time)
    {
        if (!_pointerDragActivated || _dragUndoSnapshot is null)
            return _playhead;
        var delta = time.TotalSeconds - _dragStartTime.TotalSeconds;
        var original = _dragUndoSnapshot.MediaClips.FirstOrDefault(clip => clip.Id == _dragMediaClipId);
        if (original is not null)
        {
            var clip = _mediaClips.First(item => item.Id == original.Id);
            var slipped = EditorAdvancedEditMath.SlipSourceRange(original.TrimStartSeconds, original.TrimEndSeconds,
                original.SourceDurationSeconds, original.PlaybackSpeed, delta);
            clip.TrimStartSeconds = slipped.Start;
            clip.TrimEndSeconds = slipped.End;
            InvalidateLoudnessAnalysis(clip);
        }
        else if (_dragUndoSnapshot.AudioTracks.FirstOrDefault(clip => clip.Id == _dragAudioTrackId) is { } source)
        {
            var clip = _audioTrackSegments.First(item => item.Id == source.Id);
            var slipped = EditorAdvancedEditMath.SlipSourceRange(source.TrimStartSeconds, source.TrimEndSeconds,
                source.SourceDurationSeconds, source.PlaybackSpeed, delta);
            clip.TrimStartSeconds = slipped.Start;
            clip.TrimEndSeconds = slipped.End;
            InvalidateAudioLoudnessAnalysis(clip);
        }
        return _playhead;
    }
}
