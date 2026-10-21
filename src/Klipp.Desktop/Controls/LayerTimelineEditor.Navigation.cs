using System;
using System.Collections.Generic;
using System.Linq;
using Klipp.Core.Editing;

namespace Klipp.Desktop.Controls;

public sealed partial class LayerTimelineEditor
{
    private void SeekToAdjacentClipEdit(bool next)
    {
        var target = EditorTimelineNavigationMath.AdjacentBoundary(GetEditBoundariesForSelectedTrack(),
            _playhead.TotalSeconds, _duration.TotalSeconds, _frameRate, next);
        // Notify as an explicit seek before the enclosing ScrollViewer gets the
        // arrow key. The editor pauses playback and synchronizes its decoders.
        SeekWithKeyboard(TimeSpan.FromSeconds(target));
    }

    public IReadOnlyList<double> GetEditBoundariesForSelectedTrack()
    {
        var trackId = _tracks.Any(track => track.Id == _selectedTrackId) ? _selectedTrackId : null;
        var boundaries = new List<double>();
        foreach (var clip in _mediaClips.Where(clip => trackId is null || clip.TrackId == trackId))
        {
            boundaries.Add(clip.TimelineStartSeconds);
            boundaries.Add(clip.TimelineStartSeconds + clip.EffectiveDurationSeconds);
        }
        foreach (var clip in _audioTrackSegments.Where(clip => trackId is null || clip.TrackId == trackId))
        {
            boundaries.Add(clip.TimelineStartSeconds);
            boundaries.Add(clip.TimelineStartSeconds + clip.EffectiveDurationSeconds);
        }
        return boundaries;
    }
}
