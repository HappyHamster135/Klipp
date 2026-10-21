using System;
using System.Diagnostics;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Microsoft.UI.Xaml;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private readonly EditorPreviewRefreshGate _timelineEditPreviewGate = new(1.0 / 30);
    private void AdvanceFreePreviewClock(EditorProjectClip? clip, double? sourceSeconds, bool decoderBlocked)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = _lastPreviewTick == 0 ? 0 : (now - _lastPreviewTick) / (double)Stopwatch.Frequency;
        EditorPreviewSourceTime? source = clip is not null && sourceSeconds is { } current
            ? new EditorPreviewSourceTime(current, clip.TimelineStartSeconds, clip.TrimStartSeconds, clip.PlaybackSpeed)
            : null;
        _previewPosition = TimeSpan.FromSeconds(EditorPreviewClockMath.Advance(
            _previewPosition.TotalSeconds, elapsed, source, decoderBlocked));
        _lastPreviewTick = decoderBlocked ? 0 : now;
    }

    private EditorProjectClip? ResolvePrimaryPreviewClipAt(TimeSpan position)
    {
        if (TryGetPreviewTransition(position, out var transition) &&
            (_project?.IsTrackActive(transition.OutgoingClip.TrackId) ?? true))
            return transition.OutgoingClip;
        var clip = FindPrimaryPreviewClipAt(position);
        return clip is not null && (_project?.IsTrackActive(clip.TrackId) ?? true) ? clip : null;
    }

    private EditorProjectClip? FindPrimaryPreviewClipAt(TimeSpan position)
    {
        var clips = LayerTimeline.GetPrimaryClipsForPreview();
        for (var index = clips.Count - 1; index >= 0; index--)
        {
            var clip = clips[index];
            if (position.TotalSeconds >= clip.TimelineStartSeconds - 0.001 &&
                position.TotalSeconds < clip.TimelineStartSeconds + clip.EffectiveDurationSeconds - 0.001)
                return clip;
        }
        return null;
    }

    private void SynchronizePrimaryPreview(TimeSpan position, bool seekExisting)
    {
        var clip = ResolvePrimaryPreviewClipAt(position);
        if (clip is null)
        {
            if (_activePreviewClipId is not null || _usingNativePreview || _isPreviewOpening ||
                PreviewImage.Source is not null || PreviewFallback.Visibility == Visibility.Visible)
            {
                // A thumbnail request from the previous clip must not fill a gap later.
                _thumbnailLoadVersion++;
                ClearPrimaryPreviewForGap();
                if (_primaryEffectPreviewSignature is not null)
                {
                    ClearClipEffectPreview(PrimaryEffectPreviewHost);
                    _primaryEffectPreviewSignature = null;
                }
                if (_isPreviewPlaying)
                    _lastPreviewTick = Stopwatch.GetTimestamp();
            }
            return;
        }

        if (!string.Equals(_activePreviewClipId, clip.Id, StringComparison.Ordinal) ||
            !string.Equals(_filePath, clip.SourcePath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_nativePreviewMediaPath, ResolvePreviewMediaPath(clip.SourcePath), StringComparison.OrdinalIgnoreCase))
        {
            LoadPreviewClipAt(clip, position, allowContinuousPlayback: _isPreviewPlaying && !seekExisting);
            return;
        }

        _pendingPreviewSourcePosition = GetSourcePosition(clip, position);
        if (!seekExisting || !_usingNativePreview)
            return;
        try
        {
            RequestPrimaryPreviewSeek(clip, position);
        }
        catch
        {
            // MediaOpened applies the latest pending position if the decoder is opening.
        }
    }

    private void RefreshPreviewAfterTimelineEdit()
    {
        if (!_timelineEditPreviewGate.TryEnter(PreviewNowSeconds, immediate: !LayerTimeline.IsDragging))
            return;
        // Editing can shorten content underneath the cursor. Re-render that time
        // without treating the edit as a seek or clamping the cursor to the new end.
        SynchronizePrimaryPreview(_previewPosition, seekExisting: true);
        UpdateTransitionPreview(_previewPosition, seekExisting: true);
        UpdateOverlayPreview(_previewPosition, seekExisting: true);
        UpdateAudioTrackPreview(_previewPosition, seekExisting: true);
        UpdatePlaybackUi();
    }
}
