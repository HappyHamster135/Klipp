using System;
using System.Linq;
using Klipp.Core.Editing;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Klipp.Desktop.Controls;

public sealed partial class LayerTimelineEditor
{
    private bool IsDraggingAudioFade => _dragMode == DragMode.AudioAutomation &&
        _dragAudioAutomation?.Kind is AudioAutomationKind.FadeIn or AudioAutomationKind.FadeOut;

    private string FrameTime(double seconds) => EditorTimecodeMath.Format(seconds, (int)Math.Round(_frameRate));

    private string FrameDelta(double seconds)
    {
        var frames = (long)Math.Round(seconds * _frameRate, MidpointRounding.AwayFromZero);
        return frames > 0 ? $"+{frames}f" : frames < 0 ? $"−{Math.Abs(frames)}f" : "0f";
    }

    private void HideDragFeedback() => DragFeedbackBadge.Visibility = Visibility.Collapsed;

    private void UpdateDragFeedback(Point pointer)
    {
        if (!_pointerDragActivated || _dragUndoSnapshot is null)
        { HideDragFeedback(); return; }

        string primary;
        string detail;
        if (_dragMode == DragMode.AudioAutomation && _dragAudioAutomation is { } automation)
        {
            var audio = _audioTrackSegments.FirstOrDefault(clip => clip.Id == automation.TrackId);
            var original = _dragUndoSnapshot.AudioTracks.FirstOrDefault(clip => clip.Id == automation.TrackId);
            if (audio is null || original is null)
            { HideDragFeedback(); return; }
            if (automation.Kind == AudioAutomationKind.Volume)
            {
                var point = audio.VolumeKeyframes.FirstOrDefault(key => Math.Abs(key.TimeSeconds - _dragAudioKeyTime) < .00001);
                if (point is null) { HideDragFeedback(); return; }
                primary = $"Volume point {FrameTime(point.TimeSeconds)} · {FrameDelta(point.TimeSeconds - automation.Time)}";
                detail = $"Level {point.Volume * 100:0}%";
            }
            else
            {
                var fadeIn = automation.Kind == AudioAutomationKind.FadeIn;
                var fade = fadeIn ? audio.FadeInSeconds : audio.FadeOutSeconds;
                var previousFade = fadeIn ? original.FadeInSeconds : original.FadeOutSeconds;
                primary = $"Fade {(fadeIn ? "in" : "out")} {FrameTime(fade)} · {FrameDelta(fade - previousFade)}";
                detail = $"Length {FrameTime(audio.EffectiveDurationSeconds)}";
            }
        }
        else if (_effectLayers.FirstOrDefault(layer => layer.Id == _dragEffectLayerId) is { } effect &&
            _dragUndoSnapshot.EffectLayers.FirstOrDefault(layer => layer.Id == effect.Id) is { } originalEffect)
        {
            var start = _dragMode == DragMode.EffectStart;
            var end = _dragMode == DragMode.EffectEnd;
            var time = end ? effect.EndSeconds : effect.StartSeconds;
            var originalTime = end ? originalEffect.EndSeconds : originalEffect.StartSeconds;
            primary = $"FX {(start ? "start" : end ? "end" : "move")} {FrameTime(time)} · {FrameDelta(time - originalTime)}";
            detail = $"Length {FrameTime(effect.EndSeconds - effect.StartSeconds)}";
        }
        else
        {
            var video = GetDraggedMediaClip();
            var audio = _audioTrackSegments.FirstOrDefault(clip => clip.Id == _dragAudioTrackId);
            var originalVideo = _dragUndoSnapshot.MediaClips.FirstOrDefault(clip => clip.Id == _dragMediaClipId);
            var originalAudio = _dragUndoSnapshot.AudioTracks.FirstOrDefault(clip => clip.Id == _dragAudioTrackId);
            if (video is null && audio is null || originalVideo is null && originalAudio is null)
            { HideDragFeedback(); return; }
            var start = video?.TimelineStartSeconds ?? audio!.TimelineStartSeconds;
            var sourceStart = video?.TrimStartSeconds ?? audio!.TrimStartSeconds;
            var sourceEnd = video?.TrimEndSeconds ?? audio!.TrimEndSeconds;
            var sourceDuration = video?.SourceDurationSeconds ?? audio!.SourceDurationSeconds;
            var speed = video?.PlaybackSpeed ?? audio!.PlaybackSpeed;
            var duration = video?.EffectiveDurationSeconds ?? audio!.EffectiveDurationSeconds;
            var originalStart = originalVideo?.TimelineStartSeconds ?? originalAudio!.TimelineStartSeconds;
            var originalSourceStart = originalVideo?.TrimStartSeconds ?? originalAudio!.TrimStartSeconds;
            var originalDuration = originalVideo?.EffectiveDurationSeconds ?? originalAudio!.EffectiveDurationSeconds;
            var prefix = EditTool == EditorTimelineEditTool.RollingTrim ? "Roll "
                : EditTool == EditorTimelineEditTool.RippleTrim ? "Ripple " : string.Empty;
            var limit = string.Empty;
            switch (_dragMode)
            {
                case DragMode.MediaTrimStart:
                case DragMode.AudioTrimStart:
                    primary = $"{prefix}Start {FrameTime(start)} · {FrameDelta(start - originalStart)}";
                    limit = start <= .000001 ? "Timeline start"
                        : sourceStart <= .000001 ? "Source start"
                        : sourceEnd - sourceStart <= MinClipSeconds + .000001 ? "Minimum length" : string.Empty;
                    break;
                case DragMode.MediaTrimEnd:
                case DragMode.AudioTrimEnd:
                    primary = $"{prefix}End {FrameTime(start + duration)} · {FrameDelta(start + duration - originalStart - originalDuration)}";
                    limit = sourceEnd >= sourceDuration - .000001 ? "Source end"
                        : sourceEnd - sourceStart <= MinClipSeconds + .000001 ? "Minimum length" : string.Empty;
                    break;
                case DragMode.MediaMove:
                case DragMode.AudioMove:
                    primary = $"Move {FrameTime(start)} · {FrameDelta(start - originalStart)}";
                    limit = start <= .000001 ? "Timeline start" : string.Empty;
                    break;
                case DragMode.MediaSlip:
                case DragMode.AudioSlip:
                    primary = $"Source in {FrameTime(sourceStart)} · {FrameDelta((sourceStart - originalSourceStart) / speed)}";
                    limit = sourceStart <= .000001 ? "Source start"
                        : sourceEnd >= sourceDuration - .000001 ? "Source end" : string.Empty;
                    break;
                default:
                    HideDragFeedback();
                    return;
            }
            detail = $"Length {FrameTime(duration)}";
            if (_dragMode is DragMode.MediaMove or DragMode.AudioMove && SelectedClipCount > 1)
                detail = $"{SelectedClipCount} clips · {detail}";
            if (!string.IsNullOrEmpty(limit)) detail += $" · {limit}";
        }

        if (DragFeedbackPrimaryText.Text != primary) DragFeedbackPrimaryText.Text = primary;
        if (DragFeedbackDetailText.Text != detail) DragFeedbackDetailText.Text = detail;
        var width = Math.Min(360, Math.Max(160, GetRailWidth() - 8));
        var x = Math.Clamp(pointer.X + 14, HeaderWidth, Math.Max(HeaderWidth, HeaderWidth + GetRailWidth() - width));
        var y = Math.Clamp(pointer.Y - 58, RulerHeight + 2, Math.Max(RulerHeight + 2, TimelineContentHeight - 50));
        SetCanvasRect(DragFeedbackBadge, x, y, width, 50);
        Canvas.SetZIndex(DragFeedbackBadge, 60000);
        DragFeedbackBadge.Visibility = Visibility.Visible;
    }

    private bool TryGetAudioAutomationCursor(Point pointer, out InputSystemCursorShape shape)
    {
        foreach (var visual in _audioAutomation.Values)
        {
            foreach (var handle in visual.Keys.AsEnumerable().Reverse().Append(visual.OutHandle).Append(visual.InHandle))
            {
                if (handle.Visibility != Visibility.Visible || !ContainsCanvasPoint(handle, pointer)) continue;
                shape = handle.Tag is AudioAutomationTag { Kind: AudioAutomationKind.Volume }
                    ? InputSystemCursorShape.SizeAll : InputSystemCursorShape.SizeWestEast;
                return true;
            }
        }
        shape = InputSystemCursorShape.Arrow;
        return false;
    }

    private static bool ContainsCanvasPoint(FrameworkElement element, Point point)
    {
        var x = Canvas.GetLeft(element);
        var y = Canvas.GetTop(element);
        return point.X >= x && point.X <= x + element.Width && point.Y >= y && point.Y <= y + element.Height;
    }
}
