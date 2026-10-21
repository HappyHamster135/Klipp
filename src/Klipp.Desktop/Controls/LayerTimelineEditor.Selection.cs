using System;
using System.Collections.Generic;
using System.Linq;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace Klipp.Desktop.Controls;

public sealed partial class LayerTimelineEditor
{
    private readonly HashSet<string> _selectedAudioTrackIds = new(StringComparer.Ordinal);
    private int _editBatchDepth;
    private HashSet<string> _marqueeMediaBase = new(StringComparer.Ordinal);
    private HashSet<string> _marqueeAudioBase = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> SelectedAudioTrackIds => _selectedAudioTrackIds;
    public int SelectedClipCount => _selectedMediaClipIds.Count + _selectedAudioTrackIds.Count;

    private bool SplitSelectionAtPosition(TimeSpan splitPosition)
    {
        if (SelectedEffectLayerId is not null)
            return SplitSelectedEffectLayer(splitPosition);
        if (SelectedClipCount <= 1)
            return _selectedAudioTrackId is not null && _selectedLayer is EditorTimelineLayer.Audio or EditorTimelineLayer.Playhead
                ? SplitSelectedAudioTrackAt(splitPosition) : SplitSelectedClipAt(splitPosition);
        var video = _mediaClips.Where(clip => _selectedMediaClipIds.Contains(clip.Id) && !IsClipLocked(clip) &&
            EditorClipEditMath.TrySplitSource(splitPosition.TotalSeconds, clip.TimelineStartSeconds,
                clip.TrimStartSeconds, clip.TrimEndSeconds, clip.PlaybackSpeed, out _)).ToList();
        var audio = _audioTrackSegments.Where(clip => _selectedAudioTrackIds.Contains(clip.Id) && !IsAudioLocked(clip) &&
            EditorClipEditMath.TrySplitSource(splitPosition.TotalSeconds, clip.TimelineStartSeconds,
                clip.TrimStartSeconds, clip.TrimEndSeconds, clip.PlaybackSpeed, out _)).ToList();
        if (video.Count + audio.Count == 0)
        { SetEditStatus("Place the playhead inside the selected clips to split them."); return false; }
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        var wasAudio = _selectedLayer == EditorTimelineLayer.Audio;
        var videoIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var audioIds = new List<string>();
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        string RightGroup(string source)
        {
            if (string.IsNullOrEmpty(source)) return string.Empty;
            if (!groups.TryGetValue(source, out var id)) groups[source] = id = Guid.NewGuid().ToString("N");
            return id;
        }
        string? failure = null;
        _editBatchDepth++;
        try
        {
            foreach (var source in video)
            {
                SelectOnlyMediaClip(source.Id);
                if (!SplitSelectedClipAt(splitPosition) || SelectedMediaClipId is not { } rightId)
                { failure = LastEditStatus; break; }
                var right = _mediaClips.First(clip => clip.Id == rightId);
                right.GroupId = RightGroup(source.GroupId);
                videoIds[source.Id] = rightId;
            }
            if (failure is null)
            {
                foreach (var source in audio)
                {
                    SelectAudioTrack(source.Id);
                    if (!SplitSelectedAudioTrackAt(splitPosition) || _selectedAudioTrackId is not { } rightId)
                    { failure = LastEditStatus; break; }
                    var right = _audioTrackSegments.First(clip => clip.Id == rightId);
                    right.GroupId = RightGroup(source.GroupId);
                    right.SourceClipId = videoIds.GetValueOrDefault(source.SourceClipId, source.SourceClipId);
                    audioIds.Add(rightId);
                }
            }
            if (failure is not null)
                RestoreSnapshot(before);
        }
        finally { _editBatchDepth--; }
        if (failure is not null)
        { SetEditStatus(failure); return false; }
        _selectedMediaClipIds.Clear();
        _selectedMediaClipIds.UnionWith(videoIds.Values);
        _selectedAudioTrackIds.Clear();
        _selectedAudioTrackIds.UnionWith(audioIds);
        _selectedAudioTrackId = audioIds.FirstOrDefault();
        _selectedLayer = wasAudio ? EditorTimelineLayer.Audio : EditorTimelineLayer.Media;
        CompleteToolEdit(before, $"{videoIds.Count + audioIds.Count} clips split at {splitPosition.TotalSeconds:0.###}s.");
        return true;
    }

    public void RestoreClipSelection(IEnumerable<string> videoIds, IEnumerable<string> audioIds,
        string? activeVideoId = null, string? activeAudioId = null)
    {
        _selectedMediaClipIds.Clear();
        _selectedMediaClipIds.UnionWith(videoIds.Where(id => _mediaClips.Any(clip => clip.Id == id)));
        _selectedAudioTrackIds.Clear();
        _selectedAudioTrackIds.UnionWith(audioIds.Where(id => _audioTrackSegments.Any(clip => clip.Id == id)));
        _selectedAudioTrackId = activeAudioId is not null && _selectedAudioTrackIds.Contains(activeAudioId)
            ? activeAudioId : _selectedAudioTrackIds.FirstOrDefault();
        _selectedLayer = activeAudioId is not null && _selectedAudioTrackIds.Contains(activeAudioId)
            ? EditorTimelineLayer.Audio : _selectedMediaClipIds.Count > 0 ? EditorTimelineLayer.Media
            : _selectedAudioTrackIds.Count > 0 ? EditorTimelineLayer.Audio : EditorTimelineLayer.None;
        _selectedTrackId = _selectedLayer == EditorTimelineLayer.Audio
            ? _audioTrackSegments.FirstOrDefault(clip => clip.Id == _selectedAudioTrackId)?.TrackId
            : _mediaClips.FirstOrDefault(clip => clip.Id == activeVideoId || (_selectedMediaClipIds.Count == 1 && _selectedMediaClipIds.Contains(clip.Id)))?.TrackId;
        UpdateVisuals(false, null, EditorTimelineLayer.None);
    }

    public void SelectAllClips()
    {
        _selectedMediaClipIds.Clear();
        _selectedAudioTrackIds.Clear();
        foreach (var clip in _mediaClips.Where(clip => !IsClipLocked(clip)))
            _selectedMediaClipIds.Add(clip.Id);
        foreach (var clip in _audioTrackSegments.Where(clip => !IsAudioLocked(clip)))
            _selectedAudioTrackIds.Add(clip.Id);
        _selectedAudioTrackId = _selectedAudioTrackIds.FirstOrDefault();
        _selectedLayer = EditorTimelineLayer.Media;
        UpdateVisuals(true, _playhead, EditorTimelineLayer.Media, selectionOnly: true);
    }

    public bool GroupSelection()
    {
        if (SelectedClipCount < 2)
            return false;
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        var group = Guid.NewGuid().ToString("N");
        foreach (var clip in _mediaClips.Where(clip => _selectedMediaClipIds.Contains(clip.Id) && !IsClipLocked(clip)))
            clip.GroupId = group;
        foreach (var clip in _audioTrackSegments.Where(clip => _selectedAudioTrackIds.Contains(clip.Id) && !IsAudioLocked(clip)))
            clip.GroupId = group;
        CompleteSelectionEdit(before, "Clips grouped. Drag any member to move the group.");
        return true;
    }

    public bool UngroupSelection()
    {
        var groups = _mediaClips.Where(clip => _selectedMediaClipIds.Contains(clip.Id)).Select(clip => clip.GroupId)
            .Concat(_audioTrackSegments.Where(clip => _selectedAudioTrackIds.Contains(clip.Id)).Select(clip => clip.GroupId))
            .Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
        if (groups.Count == 0)
            return false;
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        foreach (var clip in _mediaClips.Where(clip => groups.Contains(clip.GroupId) && !IsClipLocked(clip)))
            clip.GroupId = string.Empty;
        foreach (var clip in _audioTrackSegments.Where(clip => groups.Contains(clip.GroupId) && !IsAudioLocked(clip)))
            clip.GroupId = string.Empty;
        CompleteSelectionEdit(before, "Clips ungrouped.");
        return true;
    }

    private void CompleteSelectionEdit(TimelineSnapshot before, string status)
    {
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, SelectedMediaClipId);
        NotifyAudioTracksChanged(EditorMediaChangeKind.Changed);
        UpdateVisuals(true, _playhead, EditorTimelineLayer.Media);
        SetEditStatus(status);
    }

    private void SelectForPointer(string id, bool audio, bool toggle)
    {
        ClearMarkerSelection();
        var alreadySelected = !toggle && (audio ? _selectedAudioTrackIds.Contains(id) : _selectedMediaClipIds.Contains(id));
        // Initial/programmatic selection may contain only the pressed clip. Keep
        // an existing multi-selection, but still expand this anchor's group and
        // optional A/V links before a drag starts.
        if (!toggle && !alreadySelected)
        {
            _selectedMediaClipIds.Clear();
            _selectedAudioTrackIds.Clear();
        }
        var set = audio ? _selectedAudioTrackIds : _selectedMediaClipIds;
        var deselect = toggle && set.Contains(id);
        if (deselect)
            set.Remove(id);
        else
            set.Add(id);
        var clip = audio ? null : _mediaClips.FirstOrDefault(item => item.Id == id);
        var audioClip = audio ? _audioTrackSegments.FirstOrDefault(item => item.Id == id) : null;
        _selectedTrackId = clip?.TrackId ?? audioClip?.TrackId;
        var group = clip?.GroupId ?? audioClip?.GroupId;
        if (!string.IsNullOrEmpty(group))
        {
            foreach (var item in _mediaClips.Where(item => item.GroupId == group && !IsClipLocked(item)))
                if (deselect) _selectedMediaClipIds.Remove(item.Id); else _selectedMediaClipIds.Add(item.Id);
            foreach (var item in _audioTrackSegments.Where(item => item.GroupId == group && !IsAudioLocked(item)))
                if (deselect) _selectedAudioTrackIds.Remove(item.Id); else _selectedAudioTrackIds.Add(item.Id);
        }
        if (LinkedSelectionEnabled)
        {
            var source = clip?.Id ?? audioClip?.SourceClipId;
            if (!string.IsNullOrEmpty(source))
            {
                foreach (var item in _mediaClips.Where(item => item.Id == source && !IsClipLocked(item)))
                    if (deselect) _selectedMediaClipIds.Remove(item.Id); else _selectedMediaClipIds.Add(item.Id);
                foreach (var item in _audioTrackSegments.Where(item => item.SourceClipId == source && !IsAudioLocked(item)))
                    if (deselect) _selectedAudioTrackIds.Remove(item.Id); else _selectedAudioTrackIds.Add(item.Id);
            }
        }
        _selectedAudioTrackId = _selectedAudioTrackIds.FirstOrDefault();
        _selectedLayer = audio ? EditorTimelineLayer.Audio : EditorTimelineLayer.Media;
    }

    private void BeginMarquee(PointerRoutedEventArgs e)
    {
        _clipDoubleClick.Reset();
        Focus(FocusState.Pointer);
        ClearMarkerSelection();
        _dragStartPlayhead = _playhead;
        _dragStartTime = TimeFromPointer(e);
        _dragMode = DragMode.Marquee;
        _dragUndoSnapshot = TakeSnapshot();
        _dragLayer = EditorTimelineLayer.Media;
        _marqueeMediaBase = IsKeyDown(VirtualKey.Control) ? new(_selectedMediaClipIds, StringComparer.Ordinal) : new(StringComparer.Ordinal);
        _marqueeAudioBase = IsKeyDown(VirtualKey.Control) ? new(_selectedAudioTrackIds, StringComparer.Ordinal) : new(StringComparer.Ordinal);
        TimelineSurface.CapturePointer(e.Pointer);
        UpdateDrag(e);
        e.Handled = true;
    }

    private void UpdateMarquee(Point pointer)
    {
        var rect = new Rect(Math.Min(pointer.X, _dragInitialPointer.X), Math.Min(pointer.Y, _dragInitialPointer.Y),
            Math.Abs(pointer.X - _dragInitialPointer.X), Math.Abs(pointer.Y - _dragInitialPointer.Y));
        MarqueeBox.Visibility = _pointerDragActivated ? Visibility.Visible : Visibility.Collapsed;
        SetCanvasRect(MarqueeBox, rect.X, rect.Y, rect.Width, rect.Height);
        Microsoft.UI.Xaml.Controls.Canvas.SetZIndex(MarqueeBox, 50000);
        _selectedMediaClipIds.Clear();
        _selectedMediaClipIds.UnionWith(_marqueeMediaBase);
        _selectedAudioTrackIds.Clear();
        _selectedAudioTrackIds.UnionWith(_marqueeAudioBase);
        if (_pointerDragActivated)
        {
            bool Intersects(double start, double duration, double rowTop, double rowHeight)
            {
                var left = HeaderWidth + SecondsToX(start, GetRailWidth());
                var width = duration / Math.Max(.001, VisibleDuration.TotalSeconds) * GetRailWidth();
                return left < rect.Right && left + width > rect.Left && rowTop < rect.Bottom && rowTop + rowHeight > rect.Top;
            }
            foreach (var clip in _mediaClips.Where(clip => !IsClipLocked(clip) &&
                         Intersects(clip.TimelineStartSeconds, clip.EffectiveDurationSeconds, MediaRowTop(clip), MediaRowHeight(clip))))
                _selectedMediaClipIds.Add(clip.Id);
            foreach (var clip in _audioTrackSegments.Where(clip => !IsAudioLocked(clip) &&
                         Intersects(clip.TimelineStartSeconds, clip.EffectiveDurationSeconds, AudioRowTopFor(clip), TrackHeightFor(clip.TrackId))))
                _selectedAudioTrackIds.Add(clip.Id);
        }
        _selectedAudioTrackId = _selectedAudioTrackIds.FirstOrDefault();
        _selectedLayer = EditorTimelineLayer.Media;
    }

    private TimeSpan MoveSelectedClips(TimeSpan time, Point pointer, out TimeSpan? guide)
    {
        guide = null;
        if (!_pointerDragActivated || _dragUndoSnapshot is null)
            return _playhead;
        var originalVideo = _dragUndoSnapshot.MediaClips.Where(clip => _selectedMediaClipIds.Contains(clip.Id)).ToList();
        var originalAudio = _dragUndoSnapshot.AudioTracks.Where(clip => _selectedAudioTrackIds.Contains(clip.Id)).ToList();
        if (PrecisionSelectionIsLocked(originalVideo, originalAudio))
            return _playhead;
        var allStarts = originalVideo.Select(clip => clip.TimelineStartSeconds).Concat(originalAudio.Select(clip => clip.TimelineStartSeconds)).ToList();
        if (allStarts.Count == 0)
            return _playhead;
        var delta = EditorTimelineMath.SnapToFrame(EditorAdvancedEditMath.ClampGroupMove(
            time.TotalSeconds - _dragStartTime.TotalSeconds, allStarts), _frameRate);
        var anchorVideo = originalVideo.FirstOrDefault(clip => clip.Id == _dragMediaClipId);
        var anchorAudio = originalAudio.FirstOrDefault(clip => clip.Id == _dragAudioTrackId);
        if (SnappingEnabled && !IsKeyDown(VirtualKey.Menu))
        {
            var candidates = new List<double>();
            ForEachSnapCandidate(anchorVideo is null ? EditorTimelineLayer.Audio : EditorTimelineLayer.Media,
                candidate => candidates.Add(candidate.TotalSeconds));
            var ranges = originalVideo.Select(clip => (Start: clip.TimelineStartSeconds, End: clip.TimelineStartSeconds + clip.EffectiveDurationSeconds))
                .Concat(originalAudio.Select(clip => (Start: clip.TimelineStartSeconds, End: clip.TimelineStartSeconds + clip.EffectiveDurationSeconds)));
            var snapped = EditorSelectionEditMath.SnapGroupMove(delta, ranges, candidates, SnapMagnetThreshold.TotalSeconds);
            delta = snapped.Delta;
            guide = snapped.Guide.HasValue ? TimeSpan.FromSeconds(snapped.Guide.Value) : null;
        }
        var kind = anchorVideo is null ? EditorTimelineTrackKind.Audio : EditorTimelineTrackKind.Video;
        var anchorTrackId = anchorVideo?.TrackId ?? anchorAudio?.TrackId;
        var peers = VisualTracks.Where(track => track.Kind == kind).ToList();
        var targetTrack = TrackAt(pointer.Y, kind);
        var trackDelta = targetTrack is null || targetTrack.Locked ? 0 : peers.FindIndex(track => track.Id == targetTrack.Id) - peers.FindIndex(track => track.Id == anchorTrackId);
        var indices = (kind == EditorTimelineTrackKind.Video ? originalVideo.Select(clip => clip.TrackId) : originalAudio.Select(clip => clip.TrackId))
            .Select(id => peers.FindIndex(track => track.Id == id)).Where(index => index >= 0).ToList();
        trackDelta = EditorAdvancedEditMath.ClampGroupTrackMove(trackDelta, indices, peers.Select(track => track.Locked).ToList());
        string MovedTrack(string id, EditorTimelineTrackKind clipKind)
        {
            if (clipKind != kind || trackDelta == 0)
                return id;
            var index = peers.FindIndex(track => track.Id == id);
            var target = index + trackDelta;
            return target >= 0 && target < peers.Count && !peers[target].Locked ? peers[target].Id : id;
        }
        foreach (var original in originalVideo)
        {
            var clip = _mediaClips.First(item => item.Id == original.Id);
            clip.TimelineStartSeconds = Math.Max(0, original.TimelineStartSeconds + delta);
            clip.TrackId = MovedTrack(original.TrackId, EditorTimelineTrackKind.Video);
            EnsureDragDuration(clip.TimelineStartSeconds + clip.EffectiveDurationSeconds);
        }
        foreach (var original in originalAudio)
        {
            var clip = _audioTrackSegments.First(item => item.Id == original.Id);
            clip.TimelineStartSeconds = Math.Max(0, original.TimelineStartSeconds + delta);
            clip.TrackId = MovedTrack(original.TrackId, EditorTimelineTrackKind.Audio);
            EnsureDragDuration(clip.TimelineStartSeconds + clip.EffectiveDurationSeconds);
        }
        EnsureTimelineTracks();
        _mediaTimelineStartsDirty = true;
        return _playhead;
    }

    private bool DeleteMixedSelection(bool ripple)
    {
        var video = _mediaClips.Where(clip => _selectedMediaClipIds.Contains(clip.Id) && !IsClipLocked(clip)).ToList();
        var audio = _audioTrackSegments.Where(clip => _selectedAudioTrackIds.Contains(clip.Id) && !IsAudioLocked(clip)).ToList();
        if (video.Count + audio.Count == 0)
            return false;
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        var intervals = video.Select(clip => (clip.TrackId, clip.TimelineStartSeconds, clip.TimelineStartSeconds + clip.EffectiveDurationSeconds))
            .Concat(audio.Select(clip => (clip.TrackId, clip.TimelineStartSeconds, clip.TimelineStartSeconds + clip.EffectiveDurationSeconds))).ToList();
        _mediaClips.RemoveAll(clip => video.Any(removed => removed.Id == clip.Id));
        _audioTrackSegments.RemoveAll(clip => audio.Any(removed => removed.Id == clip.Id));
        var rippleSeconds = ripple ? RippleRemovedIntervals(intervals) : 0;
        _selectedMediaClipIds.Clear();
        _selectedAudioTrackIds.Clear();
        _selectedAudioTrackId = null;
        NormalizeMediaClips();
        RecalculateDurationFromMedia(false, allowEmpty: true);
        RebuildMediaClipBlocks();
        RebuildAudioTrackSegmentBlocks();
        var intendedRippleSeconds = ripple ? EditorAdvancedEditMath.MergeIntervals(intervals.Select(interval =>
            (interval.Item2, interval.Item3))).Sum(interval => interval.End - interval.Start) : 0;
        CompleteSelectionEdit(before, !ripple ? "Selection deleted." : rippleSeconds > .00001
            ? rippleSeconds < intendedRippleSeconds - 1 / _frameRate
                ? "Selection deleted; only shared empty space was closed. Split media on sync-locked tracks or turn off their Sync lock to close the remaining gap."
                : "Selection ripple deleted across participating tracks."
            : "Selection deleted. Media on a sync-locked track prevented closing the gap; split that media or turn off its Sync lock to close it.");
        return true;
    }

    private bool DuplicateMixedSelection()
    {
        var video = _mediaClips.Where(clip => _selectedMediaClipIds.Contains(clip.Id) && !IsClipLocked(clip)).ToList();
        var audio = _audioTrackSegments.Where(clip => _selectedAudioTrackIds.Contains(clip.Id) && !IsAudioLocked(clip)).ToList();
        if (video.Count + audio.Count == 0)
            return false;
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        var starts = video.Select(clip => clip.TimelineStartSeconds).Concat(audio.Select(clip => clip.TimelineStartSeconds));
        var ends = video.Select(clip => clip.TimelineStartSeconds + clip.EffectiveDurationSeconds)
            .Concat(audio.Select(clip => clip.TimelineStartSeconds + clip.EffectiveDurationSeconds));
        var offset = ends.Max() - starts.Min();
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        string NewGroup(string original)
        {
            if (string.IsNullOrEmpty(original)) return string.Empty;
            if (!groups.TryGetValue(original, out var id)) groups[original] = id = Guid.NewGuid().ToString("N");
            return id;
        }
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        _selectedMediaClipIds.Clear();
        _selectedAudioTrackIds.Clear();
        foreach (var source in video)
        {
            var copy = CloneClip(source);
            copy.Id = Guid.NewGuid().ToString("N");
            idMap[source.Id] = copy.Id;
            copy.Name += " copy";
            copy.TimelineStartSeconds += offset;
            copy.GroupId = NewGroup(source.GroupId);
            _mediaClips.Add(copy);
            _selectedMediaClipIds.Add(copy.Id);
            foreach (var sourceEffect in _effectLayers.Where(layer => layer.TargetClipId == source.Id).ToArray())
            {
                var effect = CloneEffectLayer(sourceEffect);
                effect.Id = Guid.NewGuid().ToString("N");
                effect.TargetClipId = copy.Id;
                effect.StartSeconds += offset;
                effect.EndSeconds += offset;
                _effectLayers.Add(effect);
            }
        }
        foreach (var source in audio)
        {
            var copy = CloneAudioTrack(source);
            copy.Id = Guid.NewGuid().ToString("N");
            copy.Name += " copy";
            copy.TimelineStartSeconds += offset;
            copy.GroupId = NewGroup(source.GroupId);
            copy.SourceClipId = idMap.GetValueOrDefault(source.SourceClipId, source.SourceClipId);
            _audioTrackSegments.Add(copy);
            _selectedAudioTrackIds.Add(copy.Id);
        }
        _selectedAudioTrackId = _selectedAudioTrackIds.FirstOrDefault();
        RebuildEffectLayerBlocks();
        CompleteToolEdit(before, "Selection duplicated.");
        return true;
    }
}
