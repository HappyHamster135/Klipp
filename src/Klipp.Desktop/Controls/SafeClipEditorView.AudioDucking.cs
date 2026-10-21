using System;
using System.Collections.Generic;
using System.Linq;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private sealed class DuckingPreviewState
    {
        public EditorAudioDuckingEnvelope Envelope { get; } = new();
        public double Position { get; set; } = double.NaN;
    }
    private readonly Dictionary<string, DuckingPreviewState> _duckingPreviewStates = new(StringComparer.Ordinal);

    private void PruneAudioDuckingStates(EditorProject project)
    {
        var ids = project.AudioTracks.Where(track => track.DuckingEnabled).Select(track => track.Id).ToHashSet();
        foreach (var id in _duckingPreviewStates.Keys.Where(id => !ids.Contains(id)).ToArray()) _duckingPreviewStates.Remove(id);
    }

    private double GetPreviewAudioDuckingGain(EditorProjectAudioTrack target, TimeSpan position)
    {
        if (_project is null || !target.DuckingEnabled || target.DuckingStrength <= .001) return 1;
        var microphonePresent = _project.AudioTracks.Any(track => track.SourceAudioRole == "microphone");
        var level = 0d;
        foreach (var source in _project.AudioTracks)
        {
            var local = position.TotalSeconds - source.TimelineStartSeconds;
            if (source.Id == target.Id || source.Muted || !_project.IsTrackActive(source.TrackId) ||
                !EditorAudioDuckingMath.IsControlSource(source.SourceAudioRole, microphonePresent, source.SourceClipId.Length > 0) ||
                local < 0 || local >= source.EffectiveDurationSeconds ||
                !TryGetAudioSignalFrame(source.SourcePath, source.TrimStartSeconds + local * source.PlaybackSpeed,
                    out var frame, source.SourceAudioStreamIndex)) continue;
            var fade = source.FadeInSeconds > .001 ? Math.Clamp(local / source.FadeInSeconds, 0, 1) : 1;
            if (source.FadeOutSeconds > .001) fade *= Math.Clamp((source.EffectiveDurationSeconds - local) / source.FadeOutSeconds, 0, 1);
            var gain = source.EvaluateVolume(local) * fade * (source.NormalizeAudio ? DecibelsToLinear(source.NormalizationGainDb) : 1);
            level += (frame.RmsLeft + frame.RmsRight) * .5 * gain;
        }
        if (!microphonePresent)
        {
            void AddLegacy(EditorProjectClip clip, double weight)
            {
                if (clip.Muted || clip.SourceAudioSeparated || !_project.IsTrackActive(clip.TrackId)) return;
                var seconds = clip.TrimStartSeconds + LayerTimeline.GetMediaClipLocalTime(clip.Id, position) * clip.PlaybackSpeed;
                if (TryGetAudioSignalFrame(clip.SourcePath, seconds, out var frame))
                    level += (frame.RmsLeft + frame.RmsRight) * .5 * EvaluatePreviewClipVolume(clip, position) * weight;
            }
            if (TryGetPreviewTransition(position, out var transition))
            { AddLegacy(transition.OutgoingClip, 1 - transition.Progress); AddLegacy(transition.IncomingClip, transition.Progress); }
            else if (LayerTimeline.FindMediaClipAt(position, EditorClipTrack.Primary) is { } clip) AddLegacy(clip, 1);
        }
        if (!_duckingPreviewStates.TryGetValue(target.Id, out var state)) _duckingPreviewStates[target.Id] = state = new();
        var elapsed = position.TotalSeconds - state.Position;
        var reset = !_isPreviewPlaying || !double.IsFinite(elapsed) || elapsed < 0 || elapsed > .5;
        var envelope = state.Envelope.Update(level, elapsed, reset);
        state.Position = position.TotalSeconds;
        return EditorAudioDuckingMath.Gain(envelope, target.DuckingStrength);
    }
}
