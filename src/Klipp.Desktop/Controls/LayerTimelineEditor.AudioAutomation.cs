using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Klipp.Desktop.Controls;

public sealed partial class LayerTimelineEditor
{
    private double _dragAudioAutomationTimeOffset;
    private double _dragAudioAutomationGainOffset;
    private readonly Dictionary<string, AudioAutomationVisual> _audioAutomation = new(StringComparer.Ordinal);
    private AudioAutomationTag? _dragAudioAutomation;
    private double _dragAudioKeyTime;

    public bool SetSelectedAudioVolumeKeyframeEasing(string easing)
    {
        var track = _audioTrackSegments.FirstOrDefault(track => track.Id == _selectedAudioTrackId && !IsAudioLocked(track));
        if (track is null)
            return false;
        var local = _playhead.TotalSeconds - track.TimelineStartSeconds;
        var key = track.VolumeKeyframes.FirstOrDefault(key => Math.Abs(key.TimeSeconds - local) <= TimelineStep.TotalSeconds / 2);
        if (key is null)
            return false;
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        key.Easing = easing is "ease-in" or "ease-out" or "ease-in-out" or "hold" ? easing : "linear";
        CompleteSelectionEdit(before, "Audio keyframe easing changed.");
        return true;
    }

    public bool ApplyAudioCrossfadeAtPlayhead()
    {
        var selected = _audioTrackSegments.FirstOrDefault(track => track.Id == _selectedAudioTrackId && !IsAudioLocked(track));
        var trackId = selected?.TrackId ?? _selectedTrackId;
        var peers = _audioTrackSegments.Where(track => trackId is null || track.TrackId == trackId).OrderBy(track => track.TimelineStartSeconds).ToList();
        var candidates = peers.SelectMany(left => peers.Where(right => right.Id != left.Id && right.TrackId == left.TrackId &&
                right.TimelineStartSeconds > left.TimelineStartSeconds && !IsAudioLocked(left) && !IsAudioLocked(right))
            .Select(right => (Left: left, Right: right, Distance: Math.Abs(right.TimelineStartSeconds - _playhead.TotalSeconds))))
            .Where(pair => pair.Right.TimelineStartSeconds <= pair.Left.TimelineStartSeconds + pair.Left.EffectiveDurationSeconds + .001)
            .OrderBy(pair => selected is not null && (pair.Left.Id == selected.Id || pair.Right.Id == selected.Id) ? 0 : 1)
            .ThenBy(pair => pair.Distance).FirstOrDefault();
        if (candidates.Left is null || candidates.Right is null)
        { SetEditStatus("Select two touching or overlapping audio clips on the same track."); return false; }
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        var left = candidates.Left;
        var right = candidates.Right;
        var requested = Math.Min(.5, Math.Min(left.EffectiveDurationSeconds / 2, right.EffectiveDurationSeconds / 2));
        var existingOverlap = Math.Max(0, left.TimelineStartSeconds + left.EffectiveDurationSeconds - right.TimelineStartSeconds);
        var handles = EditorAdvancedEditMath.PlanAudioCrossfadeHandles(requested,
            Math.Max(0, left.SourceDurationSeconds - left.TrimEndSeconds) / left.PlaybackSpeed,
            Math.Min(right.TrimStartSeconds / right.PlaybackSpeed, right.TimelineStartSeconds), existingOverlap);
        if (handles.LeftExtension > 0)
        {
            left.TrimEndSeconds += handles.LeftExtension * left.PlaybackSpeed;
            InvalidateAudioLoudnessAnalysis(left);
        }
        if (handles.RightExtension > 0)
        {
            right.TrimStartSeconds -= handles.RightExtension * right.PlaybackSpeed;
            right.TimelineStartSeconds -= handles.RightExtension;
            right.VolumeKeyframes = RetimeVolumeKeyframesForStartTrim(right.VolumeKeyframes, right.Volume, -handles.RightExtension);
            InvalidateAudioLoudnessAnalysis(right);
        }
        var overlap = Math.Max(0, left.TimelineStartSeconds + left.EffectiveDurationSeconds - right.TimelineStartSeconds);
        var fade = Math.Min(overlap > .00001 ? overlap : requested,
            Math.Min(left.EffectiveDurationSeconds / 2, right.EffectiveDurationSeconds / 2));
        left.FadeOutSeconds = fade;
        right.FadeInSeconds = fade;
        left.Normalize();
        right.Normalize();
        _selectedAudioTrackIds.Clear();
        _selectedAudioTrackIds.Add(left.Id);
        _selectedAudioTrackIds.Add(right.Id);
        _selectedAudioTrackId = right.Id;
        CompleteToolEdit(before, overlap > .00001
            ? $"Audio crossfade added · {fade:0.##}s. Existing audio stays in sync."
            : "No source handles available. Fade out/in added at the join; audio timing is unchanged.");
        return true;
    }

    private void RebuildAudioAutomation()
    {
        AudioAutomationCanvas.Children.Clear();
        _audioAutomation.Clear();
        foreach (var track in _audioTrackSegments)
        {
            var envelope = new Polyline { Stroke = ResourceBrush("KlippAccentBrush"), StrokeThickness = 1.5, IsHitTestVisible = false };
            var fadeIn = new Line { Stroke = ResourceBrush("KlippTextPrimaryBrush"), StrokeThickness = 1.2, IsHitTestVisible = false, Opacity = .7 };
            var fadeOut = new Line { Stroke = ResourceBrush("KlippTextPrimaryBrush"), StrokeThickness = 1.2, IsHitTestVisible = false, Opacity = .7 };
            Border Handle(AudioAutomationTag tag, string tip) => new()
            {
                Tag = tag,
                Width = tag.Kind == AudioAutomationKind.Volume ? 16 : 20,
                Height = tag.Kind == AudioAutomationKind.Volume ? 16 : 20,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                Child = new Border
                {
                    Width = 10, Height = 10,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    CornerRadius = new CornerRadius(tag.Kind == AudioAutomationKind.Volume ? 5 : 2),
                    Background = ResourceBrush(tag.Kind == AudioAutomationKind.Volume ? "KlippAccentBrush" : "KlippTextPrimaryBrush"),
                    BorderBrush = ResourceBrush("KlippBackgroundBrush"), BorderThickness = new Thickness(1),
                    IsHitTestVisible = false
                }
            };
            var startHandle = Handle(new(track.Id, AudioAutomationKind.FadeIn, 0), "Drag to fade in");
            var endHandle = Handle(new(track.Id, AudioAutomationKind.FadeOut, 0), "Drag to fade out");
            ToolTipService.SetToolTip(startHandle, "Drag to adjust fade in");
            ToolTipService.SetToolTip(endHandle, "Drag to adjust fade out");
            startHandle.PointerPressed += OnAudioAutomationPressed;
            endHandle.PointerPressed += OnAudioAutomationPressed;
            AudioAutomationCanvas.Children.Add(envelope);
            AudioAutomationCanvas.Children.Add(fadeIn);
            AudioAutomationCanvas.Children.Add(fadeOut);
            AudioAutomationCanvas.Children.Add(startHandle);
            AudioAutomationCanvas.Children.Add(endHandle);
            var keys = new List<Border>();
            foreach (var key in track.VolumeKeyframes)
            {
                var point = Handle(new(track.Id, AudioAutomationKind.Volume, key.TimeSeconds), "Drag volume point");
                point.PointerPressed += OnAudioAutomationPressed;
                var menu = new MenuFlyout();
                AddMenuAction(menu, "Delete volume point", () => RemoveAudioVolumePoint(track.Id, key.TimeSeconds));
                foreach (var easing in new[] { "linear", "ease-in", "ease-out", "ease-in-out", "hold" })
                    AddMenuAction(menu, $"Interpolation: {easing}", () => SetAudioPointEasing(track.Id, key.TimeSeconds, easing));
                point.ContextFlyout = menu;
                ToolTipService.SetToolTip(point, $"Volume · {key.Volume * 100:0}% · {key.TimeSeconds:0.##}s");
                keys.Add(point);
                AudioAutomationCanvas.Children.Add(point);
            }
            _audioAutomation[track.Id] = new(envelope, fadeIn, fadeOut, startHandle, endHandle, keys);
        }
    }

    private void DrawAudioAutomation(double railLeft, double railWidth)
    {
        AudioAutomationCanvas.Width = TimelineCanvas.Width;
        AudioAutomationCanvas.Height = TimelineContentHeight;
        foreach (var track in _audioTrackSegments)
        {
            if (!_audioAutomation.TryGetValue(track.Id, out var visual))
                continue;
            var visible = _selectedAudioTrackIds.Contains(track.Id) && !IsAudioLocked(track) &&
                TryGetVisibleRect(TimeSpan.FromSeconds(track.TimelineStartSeconds), TimeSpan.FromSeconds(track.TimelineStartSeconds + track.EffectiveDurationSeconds), railWidth, out _, out _);
            foreach (var element in visual.Elements)
                element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible)
                continue;
            var top = AudioRowTopFor(track) + BlockInset + 5;
            var height = TrackHeightFor(track.TrackId) - BlockInset * 2 - 10;
            double X(double local) => railLeft + SecondsToX(track.TimelineStartSeconds + local, railWidth);
            double Y(double gain) => top + height * (1 - Math.Clamp(gain / 2, 0, 1));
            var from = Math.Max(0, VisibleStart.TotalSeconds - track.TimelineStartSeconds);
            var to = Math.Min(track.EffectiveDurationSeconds, VisibleEnd.TotalSeconds - track.TimelineStartSeconds);
            var points = new PointCollection();
            var count = Math.Max(2, Math.Min(240, (int)((to - from) / Math.Max(.001, VisibleDuration.TotalSeconds) * railWidth / 5)));
            for (var index = 0; index <= count; index++)
            {
                var time = from + (to - from) * index / count;
                points.Add(new Point(X(time), Y(track.EvaluateVolume(time))));
            }
            visual.Envelope.Points = points;
            var fadeInEnd = Math.Min(track.FadeInSeconds, track.EffectiveDurationSeconds);
            var fadeOutStart = Math.Max(0, track.EffectiveDurationSeconds - track.FadeOutSeconds);
            void PlaceFade(Line line, Border handle, double start, double end, bool inward)
            {
                line.X1 = X(start); line.X2 = X(end); line.Y1 = inward ? top + height : top; line.Y2 = inward ? top : top + height;
                var local = inward ? fadeInEnd : fadeOutStart;
                var x = X(local);
                handle.Visibility = x >= railLeft && x <= railLeft + railWidth ? Visibility.Visible : Visibility.Collapsed;
                SetCanvasRect(handle, Math.Clamp(x - 10, railLeft - 5, Math.Max(railLeft - 5, railLeft + railWidth - 15)), top - 7, 20, 20);
                line.Clip = new RectangleGeometry { Rect = new Rect(railLeft, top, railWidth, height + 1) };
            }
            PlaceFade(visual.FadeIn, visual.InHandle, 0, fadeInEnd, true);
            PlaceFade(visual.FadeOut, visual.OutHandle, fadeOutStart, track.EffectiveDurationSeconds, false);
            var lastPointX = double.NegativeInfinity;
            var activeKey = _dragAudioAutomation is { Kind: AudioAutomationKind.Volume } active && active.TrackId == track.Id
                ? track.VolumeKeyframes.FirstOrDefault(key => Math.Abs(key.TimeSeconds - _dragAudioKeyTime) < .00001) : null;
            var activeX = activeKey is null ? double.NaN : X(activeKey.TimeSeconds);
            foreach (var point in visual.Keys)
            {
                var tag = (AudioAutomationTag)point.Tag;
                var key = track.VolumeKeyframes.FirstOrDefault(key => Math.Abs(key.TimeSeconds - tag.Time) < .00001);
                if (_dragAudioAutomation is { Kind: AudioAutomationKind.Volume } dragging && dragging.TrackId == track.Id && Math.Abs(dragging.Time - tag.Time) < .00001)
                    key = track.VolumeKeyframes.FirstOrDefault(key => Math.Abs(key.TimeSeconds - _dragAudioKeyTime) < .00001);
                if (key is null || X(key.TimeSeconds) < railLeft || X(key.TimeSeconds) > railLeft + railWidth)
                { point.Visibility = Visibility.Collapsed; continue; }
                var x = X(key.TimeSeconds);
                var isActive = activeKey is not null && ReferenceEquals(key, activeKey);
                if (!isActive && (x - lastPointX < 6 || Math.Abs(x - activeX) < 6))
                { point.Visibility = Visibility.Collapsed; continue; }
                if (!isActive) lastPointX = x;
                SetCanvasRect(point, x - 8, Y(key.Volume) - 8, 16, 16);
            }
        }
    }

    private void OnAudioAutomationPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsPrimaryPointer(e) || sender is not Border { Tag: AudioAutomationTag tag })
            return;
        var track = _audioTrackSegments.FirstOrDefault(track => track.Id == tag.TrackId);
        if (track is null || IsAudioLocked(track)) return;
        Focus(FocusState.Pointer);
        _clipDoubleClick.Reset();
        _dragAudioAutomation = tag;
        _dragAudioKeyTime = tag.Time;
        _dragAudioTrackId = tag.TrackId;
        _dragMode = DragMode.AudioAutomation;
        _dragLayer = EditorTimelineLayer.Audio;
        _dragStartPlayhead = _playhead;
        _dragStartTime = TimeFromPointer(e);
        var pointer = e.GetCurrentPoint(TimelineSurface).Position;
        var handleTime = tag.Kind == AudioAutomationKind.FadeIn ? track.FadeInSeconds
            : tag.Kind == AudioAutomationKind.FadeOut ? track.EffectiveDurationSeconds - track.FadeOutSeconds : tag.Time;
        _dragAudioAutomationTimeOffset = handleTime - (_dragStartTime.TotalSeconds - track.TimelineStartSeconds);
        var top = AudioRowTopFor(track) + BlockInset + 5;
        var height = TrackHeightFor(track.TrackId) - BlockInset * 2 - 10;
        var key = track.VolumeKeyframes.FirstOrDefault(key => Math.Abs(key.TimeSeconds - tag.Time) < .00001);
        _dragAudioAutomationGainOffset = tag.Kind == AudioAutomationKind.Volume && key is not null
            ? key.Volume - (1 - (pointer.Y - top) / height) * 2 : 0;
        _dragUndoSnapshot = TakeSnapshot();
        TimelineSurface.CapturePointer(e.Pointer);
        UpdateDrag(e);
        e.Handled = true;
    }

    private void UpdateAudioAutomation(Point pointer)
    {
        if (!_pointerDragActivated || _dragAudioAutomation is not { } tag)
            return;
        var track = _audioTrackSegments.First(item => item.Id == tag.TrackId);
        var local = Math.Clamp(TimeFromX(pointer.X).TotalSeconds - track.TimelineStartSeconds + _dragAudioAutomationTimeOffset,
            0, track.EffectiveDurationSeconds);
        if (tag.Kind == AudioAutomationKind.FadeIn)
            track.FadeInSeconds = local;
        else if (tag.Kind == AudioAutomationKind.FadeOut)
            track.FadeOutSeconds = track.EffectiveDurationSeconds - local;
        else
        {
            var key = track.VolumeKeyframes.FirstOrDefault(key => Math.Abs(key.TimeSeconds - _dragAudioKeyTime) < .00001);
            if (key is null) return;
            var top = AudioRowTopFor(track) + BlockInset + 5;
            var height = TrackHeightFor(track.TrackId) - BlockInset * 2 - 10;
            var time = Math.Clamp(EditorTimelineMath.SnapToFrame(local, _frameRate), 0, track.EffectiveDurationSeconds);
            track.VolumeKeyframes.RemoveAll(other => !ReferenceEquals(other, key) && Math.Abs(other.TimeSeconds - time) <= .00001);
            key.TimeSeconds = time;
            key.Volume = Math.Clamp((1 - (pointer.Y - top) / height) * 2 + _dragAudioAutomationGainOffset, 0, 2);
            _dragAudioKeyTime = time;
            track.VolumeKeyframes.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        }
    }

    private void AddAudioVolumePoint(string id, Point pointer)
    {
        var track = _audioTrackSegments.FirstOrDefault(track => track.Id == id && !IsAudioLocked(track));
        if (track is null) return;
        if (_dragMode is DragMode.AudioMove or DragMode.AudioSlip && !_pointerDragActivated)
        {
            _dragMode = DragMode.None;
            _dragUndoSnapshot = null;
            _dragAudioTrackId = null;
            TimelineSurface.ReleasePointerCaptures();
            StopDragTracking();
        }
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        var time = Math.Clamp(EditorTimelineMath.SnapToFrame(TimeFromX(pointer.X).TotalSeconds - track.TimelineStartSeconds, _frameRate), 0, track.EffectiveDurationSeconds);
        var gain = track.EvaluateVolume(time);
        track.VolumeKeyframes.RemoveAll(key => Math.Abs(key.TimeSeconds - time) <= .00001);
        track.VolumeKeyframes.Add(new EditorProjectClipVolumeKeyframe { TimeSeconds = time, Volume = gain });
        track.VolumeKeyframes.Sort((a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
        RebuildAudioAutomation();
        CompleteSelectionEdit(before, "Volume point added. Drag it to adjust volume and timing.");
    }

    private void RemoveAudioVolumePoint(string id, double time)
    {
        var track = _audioTrackSegments.FirstOrDefault(track => track.Id == id && !IsAudioLocked(track));
        if (track is null) return;
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        track.VolumeKeyframes.RemoveAll(key => Math.Abs(key.TimeSeconds - time) < .00001);
        RebuildAudioAutomation();
        CompleteSelectionEdit(before, "Volume point removed.");
    }

    private void SetAudioPointEasing(string id, double time, string easing)
    {
        var track = _audioTrackSegments.FirstOrDefault(track => track.Id == id && !IsAudioLocked(track));
        var key = track?.VolumeKeyframes.FirstOrDefault(key => Math.Abs(key.TimeSeconds - time) < .00001);
        if (key is null) return;
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        key.Easing = easing;
        CompleteSelectionEdit(before, "Volume point interpolation changed.");
    }

    private enum AudioAutomationKind { FadeIn, FadeOut, Volume }
    private sealed record AudioAutomationTag(string TrackId, AudioAutomationKind Kind, double Time);
    private sealed record AudioAutomationVisual(Polyline Envelope, Line FadeIn, Line FadeOut, Border InHandle, Border OutHandle, List<Border> Keys)
    {
        public IEnumerable<FrameworkElement> Elements => new FrameworkElement[] { Envelope, FadeIn, FadeOut, InHandle, OutHandle }.Concat(Keys);
    }
}
