using System;
using System.Linq;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Windows.Media.Playback;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private bool _isTransitionPreviewOpening;
    private long _transitionPreviewSourceVersion;
    private readonly EditorPreviewSeekState _transitionDecoderSeek = new();
    private bool _transitionSeekEventsInitialized;
    private double _transitionPreviewBlockedSince;
    private string? _failedTransitionPreviewClipId;
    private string? _failedTransitionPreviewSourcePath;

    private void EnsureTransitionSeekEvents()
    {
        if (_transitionSeekEventsInitialized) return;
        _transitionSeekEventsInitialized = true;
        TransitionPreviewPlayer.MediaPlayer.PlaybackSession.SeekCompleted += (session, _) =>
        {
            var version = _transitionPreviewSourceVersion;
            var loadVersion = _projectLoadVersion;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (version != _transitionPreviewSourceVersion || loadVersion != _projectLoadVersion ||
                    _activeTransitionPreviewClipId is null) return;
                _transitionDecoderSeek.IsPending(session.Position.TotalSeconds, PreviewNowSeconds, seekCompleted: true);
                _lastPreviewTick = 0;
            });
        };
    }

    private bool IsTransitionPreviewDecoderBlocked()
    {
        if (_activeTransitionPreviewClipId is null) return false;
        var session = TransitionPreviewPlayer.MediaPlayer.PlaybackSession;
        var now = PreviewNowSeconds;
        var pending = _transitionDecoderSeek.IsPending(session.Position.TotalSeconds, now);
        var blocked = _isTransitionPreviewOpening || session.PlaybackState is MediaPlaybackState.Opening or MediaPlaybackState.Buffering || pending;
        if (blocked && _transitionPreviewBlockedSince == 0) _transitionPreviewBlockedSince = now;
        if (_transitionDecoderSeek.TakeTimeout() || blocked && now - _transitionPreviewBlockedSince >= 3)
        {
            SkipFailedTransitionPreview("A transition frame could not be previewed. Seek again or choose a lower preview quality.");
            return false;
        }
        if (!blocked) _transitionPreviewBlockedSince = 0;
        return blocked;
    }

    private void ResetTransitionPreviewFailure()
    {
        _failedTransitionPreviewClipId = _failedTransitionPreviewSourcePath = null;
    }

    private void SkipFailedTransitionPreview(string status)
    {
        _failedTransitionPreviewClipId = _activeTransitionPreviewClipId;
        _failedTransitionPreviewSourcePath = _project?.FindClip(_activeTransitionPreviewClipId)?.SourcePath;
        ClearTransitionPreview();
        ProjectStatusText.Text = status;
    }

    private bool TryGetSelectedTransitionContext(out EditorProjectClip incoming,
        out EditorProjectClip outgoing, out double overlap)
    {
        incoming = outgoing = null!;
        overlap = 0;
        if (_project is null || LayerTimeline.GetSelectedMediaClip() is not { } selected ||
            _project.GetTransitionPredecessor(selected, includeTouching: true) is not { } previous)
            return false;
        incoming = selected;
        outgoing = previous;
        overlap = _project.GetTransitionOverlapSeconds(previous, selected);
        return true;
    }

    private bool CanPreviewSelectedTransition()
    {
        return TryGetSelectedTransitionContext(out var incoming, out _, out var overlap) &&
            overlap > 0.001 && (_project?.IsTrackActive(incoming.TrackId) ?? false) &&
            incoming.TimelineStartSeconds < LayerTimeline.End.TotalSeconds &&
            incoming.TimelineStartSeconds + overlap > LayerTimeline.Start.TotalSeconds;
    }

    private string GetTransitionSelectionPrompt() =>
        LayerTimeline.GetSelectedMediaClip() is { } selected && _project?.GetTrack(selected.TrackId)?.Locked == true
            ? "Unlock the video track to edit its transition"
            : "Select a video clip at an overlapping or adjacent join";

    private void RefreshTransitionEditPreview()
    {
        // Adding source handles can change the decoder's source in-point while
        // the project cursor stays fixed. Refresh all players at that same time.
        ResetTransitionPreviewFailure();
        RefreshPreviewAfterTimelineEdit();
        UpdateTransitionButtons();
        UpdateTrimUi();
    }
}
