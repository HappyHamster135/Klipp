using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private readonly Dictionary<string, EditorProjectAudioTrack> _audioPreviewModels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _audioPreviewSourceAvailability = new(StringComparer.OrdinalIgnoreCase);
    private readonly EditorPreviewRefreshGate _audioPreviewPoolGate = new(.1);
    private EditorProject? _audioPreviewIndexedProject;
    private List<EditorProjectAudioTrack>? _audioPreviewIndexedTracks;

    private void RebuildAudioPreviewModelIndex(IEnumerable<EditorProjectAudioTrack> tracks)
    {
        _audioPreviewModels.Clear();
        foreach (var track in tracks)
            _audioPreviewModels[track.Id] = track;
        _audioPreviewIndexedProject = _project;
        _audioPreviewIndexedTracks = _project?.AudioTracks;
        // Explicit edits/relink/reload should retry a source restored at the same path.
        _audioPreviewSourceAvailability.Clear();
    }

    private bool EnsureAudioPreviewModelIndex()
    {
        if (_project is null) return false;
        if (ReferenceEquals(_audioPreviewIndexedProject, _project) &&
            ReferenceEquals(_audioPreviewIndexedTracks, _project.AudioTracks))
            return false;
        RebuildAudioPreviewModelIndex(_project.AudioTracks);
        return true;
    }

    private void ReconcileAudioPreviewPool(TimeSpan position, bool immediate)
    {
        if (_project is null || !_audioPreviewPoolGate.TryEnter(PreviewNowSeconds, immediate))
            return;
        var plan = EditorAudioPreviewPlanner.Plan(_audioPreviewModels.Values.Select(track =>
            new EditorAudioPreviewSegment(track.Id, track.TimelineStartSeconds, track.EffectiveDurationSeconds,
                !track.Muted && _project.IsTrackActive(track.TrackId))), position.TotalSeconds);
        foreach (var state in _audioTrackPreviewPlayers.Values.ToArray())
        {
            if (plan.RetainedIds.Contains(state.TrackId) && state.LoadVersion == _projectLoadVersion &&
                _audioPreviewModels.TryGetValue(state.TrackId, out var track) &&
                track.SourceAudioStreamIndex == state.AudioStreamIndex &&
                string.Equals(track.SourcePath, state.SourcePath, StringComparison.OrdinalIgnoreCase))
                continue;
            _audioTrackPreviewPlayers.Remove(state.TrackId);
            state.Dispose();
        }
        foreach (var id in plan.RequiredIds)
        {
            if (_audioTrackPreviewPlayers.ContainsKey(id) || !_audioPreviewModels.TryGetValue(id, out var track))
                continue;
            if (!_audioPreviewSourceAvailability.TryGetValue(track.SourcePath, out var exists))
                _audioPreviewSourceAvailability[track.SourcePath] = exists = File.Exists(track.SourcePath);
            if (!exists) continue;
            CreateAudioPreviewState(track, position);
        }
    }

    private void CreateAudioPreviewState(EditorProjectAudioTrack track, TimeSpan position)
    {
        var state = new AudioTrackPreviewState(track.Id, track.SourcePath, _projectLoadVersion, track.SourceAudioStreamIndex)
        {
            PendingPosition = GetAudioPreviewSourcePosition(track, position)
        };
        state.OpenedHandler = (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsCurrentAudioPreviewState(state)) return;
            state.MediaOpened = true;
            CompleteAudioPreviewOpen(state);
        });
        state.FailedHandler = (_, args) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsCurrentAudioPreviewState(state)) return;
            state.Failed = true;
            state.PauseIfPlaying();
            Debug.WriteLine($"[Klipp] Audio track preview failed: {args.Error} {args.ErrorMessage}");
        });
        state.SeekCompletedHandler = (session, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsCurrentAudioPreviewState(state) || !ReferenceEquals(session, state.Player.PlaybackSession)) return;
            state.Seek.IsPending(session.Position.TotalSeconds, PreviewNowSeconds, seekCompleted: true);
            if (_audioPreviewModels.TryGetValue(state.TrackId, out var currentTrack))
                UpdateAudioPreviewState(state, currentTrack, _previewPosition, seekExisting: false,
                    ShouldPlayAudioPreview(), MuteAudioToggle?.IsOn ?? false);
        });
        state.Player.MediaOpened += state.OpenedHandler;
        state.Player.MediaFailed += state.FailedHandler;
        state.Player.PlaybackSession.SeekCompleted += state.SeekCompletedHandler;
        _audioTrackPreviewPlayers[track.Id] = state;
        try
        {
            state.Source = MediaSource.CreateFromUri(new Uri(track.SourcePath));
            state.Item = new MediaPlaybackItem(state.Source);
            state.TracksChangedHandler = (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (IsCurrentAudioPreviewState(state)) CompleteAudioPreviewOpen(state);
            });
            state.Item.AudioTracksChanged += state.TracksChangedHandler;
            state.Player.Source = state.Item;
        }
        catch { state.Failed = true; }
    }

    private void CompleteAudioPreviewOpen(AudioTrackPreviewState state)
    {
        if (!state.MediaOpened || state.IsOpened || state.Failed) return;
        EnsureAudioPreviewModelIndex();
        if (!_audioPreviewModels.TryGetValue(state.TrackId, out var track) ||
            track.SourceAudioStreamIndex != state.AudioStreamIndex ||
            !string.Equals(track.SourcePath, state.SourcePath, StringComparison.OrdinalIgnoreCase)) return;
        // Track enumeration may arrive after MediaOpened. Keep the voice paused
        // until its selected stem is ready rather than falling back to the mix.
        if (!SelectAudioPreviewStream(state)) return;
        state.IsOpened = true;
        UpdateAudioPreviewState(state, track, _previewPosition, seekExisting: true,
            ShouldPlayAudioPreview(), MuteAudioToggle?.IsOn ?? false);
    }

    private bool IsAudioPreviewDecoderBlocked(TimeSpan position)
    {
        ReconcileAudioPreviewPool(position, immediate: false);
        var blocked = false;
        foreach (var state in _audioTrackPreviewPlayers.Values)
        {
            if (state.Failed || !_audioPreviewModels.TryGetValue(state.TrackId, out var track) || track.Muted ||
                _project?.IsTrackActive(track.TrackId) != true || position.TotalSeconds < track.TimelineStartSeconds ||
                position.TotalSeconds >= track.TimelineStartSeconds + track.EffectiveDurationSeconds) continue;
            var session = state.Player.PlaybackSession;
            var pending = state.Seek.IsPending(session.Position.TotalSeconds, PreviewNowSeconds);
            if (state.Seek.TakeTimeout() || !state.IsOpened && PreviewNowSeconds - state.OpeningStartedAt >= 3)
            {
                state.Failed = true;
                state.PauseIfPlaying();
                ProjectStatusText.Text = $"Audio preview unavailable for {track.Name}. Other tracks will continue.";
                continue;
            }
            blocked |= !state.IsOpened || pending ||
                session.PlaybackState is MediaPlaybackState.Opening or MediaPlaybackState.Buffering;
        }
        return blocked;
    }

    private static void RequestAudioPreviewSeek(AudioTrackPreviewState state, TimeSpan position, bool force)
    {
        var session = state.Player.PlaybackSession;
        if (!state.Seek.Request(position.TotalSeconds, session.Position.TotalSeconds, PreviewNowSeconds, force)) return;
        // Do not let a running voice emit its old source range while the seek settles.
        state.PauseIfPlaying();
        try { session.Position = position; }
        catch { state.Seek.Reset(); throw; }
    }

    private static bool SelectAudioPreviewStream(AudioTrackPreviewState state)
    {
        try
        {
            if (state.Item is null || state.AudioStreamIndex >= state.Item.AudioTracks.Count) return false;
            state.Item.AudioTracks.SelectedIndex = state.AudioStreamIndex;
            return true;
        }
        catch { return false; }
    }

    private bool IsCurrentAudioPreviewState(AudioTrackPreviewState state) =>
        !state.IsDisposed && state.LoadVersion == _projectLoadVersion &&
        _audioTrackPreviewPlayers.TryGetValue(state.TrackId, out var current) && ReferenceEquals(current, state);

    private bool ShouldPlayAudioPreview() => _isPreviewPlaying && !_wasPreviewBuffering &&
        !IsPrimaryPreviewDecoderBlocked() && !IsOverlayPreviewDecoderBlocked() && !IsTransitionPreviewDecoderBlocked();

    private static TimeSpan GetAudioPreviewSourcePosition(EditorProjectAudioTrack track, TimeSpan position) =>
        TimeSpan.FromSeconds(Math.Clamp(track.TrimStartSeconds +
            Math.Max(0, position.TotalSeconds - track.TimelineStartSeconds) * track.PlaybackSpeed,
            track.TrimStartSeconds, track.TrimEndSeconds));

    private void UpdateAudioPreviewState(AudioTrackPreviewState state, EditorProjectAudioTrack track,
        TimeSpan position, bool seekExisting, bool shouldPlay, bool masterMuted)
    {
        if (state.IsDisposed || state.Failed || track.Muted || _project?.IsTrackActive(track.TrackId) != true)
        {
            state.PauseIfPlaying();
            return;
        }
        var mappingChanged = state.Timing.Update(track.TimelineStartSeconds, track.TrimStartSeconds, track.PlaybackSpeed);
        var localSeconds = position.TotalSeconds - track.TimelineStartSeconds;
        var isActive = localSeconds >= 0 && localSeconds < track.EffectiveDurationSeconds;
        if (!isActive)
        {
            state.PauseIfPlaying();
            if (localSeconds < 0)
            {
                state.PendingPosition = TimeSpan.FromSeconds(track.TrimStartSeconds);
                if (state.IsOpened)
                {
                    try
                    {
                        var session = state.Player.PlaybackSession;
                        session.PlaybackRate = track.PlaybackSpeed;
                        if (!state.Seek.IsPending(session.Position.TotalSeconds, PreviewNowSeconds) &&
                            Math.Abs((session.Position - state.PendingPosition).TotalSeconds) > .015)
                            RequestAudioPreviewSeek(state, state.PendingPosition, force: false);
                    }
                    catch { }
                }
            }
            return;
        }
        var sourcePosition = GetAudioPreviewSourcePosition(track, position);
        state.PendingPosition = sourcePosition;
        var fadeGain = 1d;
        if (track.FadeInSeconds > .001)
            fadeGain *= Math.Clamp(localSeconds / track.FadeInSeconds, 0, 1);
        if (track.FadeOutSeconds > .001)
            fadeGain *= Math.Clamp((track.EffectiveDurationSeconds - localSeconds) / track.FadeOutSeconds, 0, 1);
        var normalizationGain = track.NormalizeAudio ? DecibelsToLinear(track.NormalizationGainDb) : 1;
        var duckingGain = masterMuted ? 1 : GetPreviewAudioDuckingGain(track, position);
        try
        {
            PreviewAudioGainEffect.SetGain(state.Player, LivePreviewVolume * track.EvaluateVolume(localSeconds) *
                normalizationGain * fadeGain * duckingGain);
            state.Player.IsMuted = masterMuted;
            if (!state.IsOpened) return;
            var session = state.Player.PlaybackSession;
            session.PlaybackRate = track.PlaybackSpeed;
            var pending = state.Seek.IsPending(session.Position.TotalSeconds, PreviewNowSeconds);
            var drift = Math.Abs((session.Position - sourcePosition).TotalSeconds);
            if (seekExisting || mappingChanged || !pending && drift > .16)
                RequestAudioPreviewSeek(state, sourcePosition, force: seekExisting || mappingChanged);
            if (shouldPlay)
                state.PlayIfReady();
            else state.PauseIfPlaying();
        }
        catch { /* MediaOpened applies this voice's latest requested position. */ }
    }
}
