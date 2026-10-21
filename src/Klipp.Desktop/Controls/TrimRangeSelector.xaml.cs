using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Klipp.Desktop.Controls;

public sealed partial class TrimRangeSelector : UserControl
{
    private const double ThumbWidth = 18;
    private const double MinSelectionSeconds = 0.1;

    private DragTarget _dragTarget = DragTarget.None;
    private TimeSpan _duration = TimeSpan.FromSeconds(1);
    private TimeSpan _start = TimeSpan.Zero;
    private TimeSpan _end = TimeSpan.FromSeconds(1);
    private bool _syncingNumberBoxes;

    public event EventHandler<TrimRangeChangedEventArgs>? RangeChanged;

    public TrimRangeSelector()
    {
        InitializeComponent();
        UpdateVisuals(notifyPreview: false, previewTime: null);
    }

    public TimeSpan Duration
    {
        get => _duration;
        set
        {
            _duration = value > TimeSpan.Zero ? value : TimeSpan.FromSeconds(1);
            _start = ClampTime(_start, TimeSpan.Zero, _duration - MinimumSelection);
            _end = ClampTime(_end, _start + MinimumSelection, _duration);
            UpdateVisuals(notifyPreview: false, previewTime: null);
        }
    }

    public TimeSpan Start
    {
        get => _start;
        set => SetStart(value, notifyPreview: true);
    }

    public TimeSpan End
    {
        get => _end;
        set => SetEnd(value, notifyPreview: true);
    }

    private TimeSpan MinimumSelection => TimeSpan.FromSeconds(Math.Min(MinSelectionSeconds, Math.Max(0.01, _duration.TotalSeconds / 2)));

    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateVisuals(notifyPreview: false, previewTime: null);
    }

    private void OnStartNumberBoxValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingNumberBoxes || !double.IsFinite(args.NewValue))
            return;

        SetStart(TimeSpan.FromSeconds(args.NewValue), notifyPreview: true);
    }

    private void OnEndNumberBoxValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingNumberBoxes || !double.IsFinite(args.NewValue))
            return;

        SetEnd(TimeSpan.FromSeconds(args.NewValue), notifyPreview: true);
    }

    private void OnTimelinePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, StartThumb) || ReferenceEquals(e.OriginalSource, EndThumb))
            return;

        var time = TimeFromPointer(e);
        var startDistance = Math.Abs((time - _start).TotalSeconds);
        var endDistance = Math.Abs((time - _end).TotalSeconds);
        BeginDrag(startDistance <= endDistance ? DragTarget.Start : DragTarget.End, e);
    }

    private void OnStartThumbPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        BeginDrag(DragTarget.Start, e);
    }

    private void OnEndThumbPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        BeginDrag(DragTarget.End, e);
    }

    private void OnTimelinePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragTarget == DragTarget.None)
            return;

        var point = e.GetCurrentPoint(TimelineSurface);
        if (!point.Properties.IsLeftButtonPressed)
        {
            FinishDrag();
            return;
        }

        UpdateDraggedTime(e);
        e.Handled = true;
    }

    private void OnTimelinePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        UpdateDraggedTime(e);
        FinishDrag();
        e.Handled = true;
    }

    private void OnTimelinePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        FinishDrag();
    }

    private void OnTimelinePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        FinishDrag();
    }

    private void BeginDrag(DragTarget target, PointerRoutedEventArgs e)
    {
        _dragTarget = target;
        TimelineSurface.CapturePointer(e.Pointer);
        UpdateDraggedTime(e);
        e.Handled = true;
    }

    private void FinishDrag()
    {
        if (_dragTarget == DragTarget.None)
            return;

        _dragTarget = DragTarget.None;
        TimelineSurface.ReleasePointerCaptures();
    }

    private void UpdateDraggedTime(PointerRoutedEventArgs e)
    {
        var time = TimeFromPointer(e);
        if (_dragTarget == DragTarget.Start)
            SetStart(time, notifyPreview: true);
        else if (_dragTarget == DragTarget.End)
            SetEnd(time, notifyPreview: true);
    }

    private TimeSpan TimeFromPointer(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(TimelineSurface).Position;
        var railWidth = GetRailWidth();
        var x = Math.Clamp(point.X - ThumbWidth / 2, 0, railWidth);
        var ratio = railWidth <= 0 ? 0 : x / railWidth;
        return TimeSpan.FromSeconds(ratio * _duration.TotalSeconds);
    }

    private void SetStart(TimeSpan value, bool notifyPreview)
    {
        _start = ClampTime(value, TimeSpan.Zero, _end - MinimumSelection);
        UpdateVisuals(notifyPreview, _start);
    }

    private void SetEnd(TimeSpan value, bool notifyPreview)
    {
        _end = ClampTime(value, _start + MinimumSelection, _duration);
        UpdateVisuals(notifyPreview, _end);
    }

    private void UpdateVisuals(bool notifyPreview, TimeSpan? previewTime)
    {
        var railWidth = GetRailWidth();
        var startX = SecondsToX(_start.TotalSeconds, railWidth);
        var endX = SecondsToX(_end.TotalSeconds, railWidth);
        var railLeft = ThumbWidth / 2;

        TimelineCanvas.Width = Math.Max(0, TimelineSurface.ActualWidth);
        TrackRail.Width = railWidth;
        SelectionRail.Width = Math.Max(0, endX - startX);

        Canvas.SetLeft(TrackRail, railLeft);
        Canvas.SetLeft(SelectionRail, railLeft + startX);
        Canvas.SetLeft(StartThumb, startX);
        Canvas.SetLeft(EndThumb, endX);

        StartValueText.Text = FormatTimestamp(_start);
        EndValueText.Text = FormatTimestamp(_end);
        SelectionValueText.Text = FormatTimestamp(_end - _start);
        SyncNumberBoxes();

        if (notifyPreview && previewTime is not null)
            RangeChanged?.Invoke(this, new TrimRangeChangedEventArgs(_start, _end, previewTime.Value));
    }

    private void SyncNumberBoxes()
    {
        _syncingNumberBoxes = true;
        try
        {
            StartNumberBox.Minimum = 0;
            StartNumberBox.Maximum = Math.Max(0, (_end - MinimumSelection).TotalSeconds);
            EndNumberBox.Minimum = Math.Min(_duration.TotalSeconds, (_start + MinimumSelection).TotalSeconds);
            EndNumberBox.Maximum = _duration.TotalSeconds;
            StartNumberBox.Value = Math.Round(_start.TotalSeconds, 3);
            EndNumberBox.Value = Math.Round(_end.TotalSeconds, 3);
        }
        finally
        {
            _syncingNumberBoxes = false;
        }
    }

    private double GetRailWidth()
    {
        return Math.Max(1, TimelineSurface.ActualWidth - ThumbWidth);
    }

    private double SecondsToX(double seconds, double railWidth)
    {
        var ratio = _duration.TotalSeconds <= 0 ? 0 : seconds / _duration.TotalSeconds;
        return Math.Clamp(ratio, 0, 1) * railWidth;
    }

    private static TimeSpan ClampTime(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (max < min)
            max = min;
        if (value < min)
            return min;
        if (value > max)
            return max;
        return value;
    }

    private static string FormatTimestamp(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    private enum DragTarget
    {
        None,
        Start,
        End
    }
}

public sealed class TrimRangeChangedEventArgs : EventArgs
{
    public TimeSpan Start { get; }
    public TimeSpan End { get; }
    public TimeSpan PreviewTime { get; }

    public TrimRangeChangedEventArgs(TimeSpan start, TimeSpan end, TimeSpan previewTime)
    {
        Start = start;
        End = end;
        PreviewTime = previewTime;
    }
}
