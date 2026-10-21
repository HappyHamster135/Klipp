using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Klipp.Desktop.Controls;

public sealed partial class LayerTimelineEditor
{
    private Point? _pendingDragPoint;
    private bool _dragRenderingSubscribed;

    private bool IsPointInHoverTimeBadge(Point point) => HoverTimeBadge.Visibility == Visibility.Visible &&
        point.X >= Canvas.GetLeft(HoverTimeBadge) && point.X <= Canvas.GetLeft(HoverTimeBadge) + HoverTimeBadge.Width &&
        point.Y >= Canvas.GetTop(HoverTimeBadge) && point.Y <= Canvas.GetTop(HoverTimeBadge) + HoverTimeBadge.Height;

    private void OnHoverTimeBadgePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode != DragMode.None || !IsPrimaryPointer(e))
            return;
        // The badge is a complete scrub target, including its border and lower
        // half. Capture on the timeline before hiding the hover visual.
        BeginDrag(DragMode.Playhead, EditorTimelineLayer.Playhead, e);
    }

    private void QueueDragPoint(Point point)
    {
        _lastDragPointer = point;
        _pendingDragPoint = point;
        if (_dragRenderingSubscribed)
            return;
        CompositionTarget.Rendering += OnDragRendering;
        _dragRenderingSubscribed = true;
    }

    private void OnDragRendering(object? sender, object e)
    {
        if (_dragMode == DragMode.None)
        {
            StopDragUpdatePump();
            return;
        }
        if (_pendingDragPoint is not { } point)
            return;
        _pendingDragPoint = null;
        UpdateDragPoint(point);
    }

    private void StopDragUpdatePump()
    {
        _pendingDragPoint = null;
        if (!_dragRenderingSubscribed)
            return;
        CompositionTarget.Rendering -= OnDragRendering;
        _dragRenderingSubscribed = false;
    }
}
