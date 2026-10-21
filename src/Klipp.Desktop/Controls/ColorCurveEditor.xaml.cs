using System;
using System.Collections.Generic;
using System.Linq;
using Klipp.Desktop.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Klipp.Desktop.Controls;

public sealed partial class ColorCurveEditor : UserControl
{
    private const double HandleSize = 13;
    private const double MinimumPointGap = 0.025;
    private readonly List<EditorProjectCurvePoint> _points = [];
    private readonly List<Border> _handles = [];
    private int _activePointIndex = -1;
    private Border? _capturedHandle;

    public event EventHandler? InteractionStarted;
    public event EventHandler<ColorCurveChangedEventArgs>? CurvePreviewChanged;
    public event EventHandler<ColorCurveChangedEventArgs>? CurveCommitted;

    public ColorCurveEditor()
    {
        InitializeComponent();
        SetPoints([]);
    }

    public IReadOnlyList<EditorProjectCurvePoint> Points => ClonePoints(_points);

    public void SetPoints(IEnumerable<EditorProjectCurvePoint>? points)
    {
        _points.Clear();
        _points.AddRange(NormalizePoints(points));
        RebuildHandles();
        UpdateGeometry();
    }

    public void ResetToNeutral()
    {
        SetPoints([]);
    }

    private void OnCurveSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        DrawGrid();
        UpdateGeometry();
    }

    private void DrawGrid()
    {
        GridCanvas.Children.Clear();
        var width = Math.Max(1, CurveSurface.ActualWidth);
        var height = Math.Max(1, CurveSurface.ActualHeight);
        for (var index = 1; index < 4; index++)
        {
            var fraction = index / 4.0;
            GridCanvas.Children.Add(new Line
            {
                X1 = width * fraction,
                X2 = width * fraction,
                Y1 = 0,
                Y2 = height,
                Stroke = new SolidColorBrush(Color.FromArgb(80, 92, 92, 108)),
                StrokeThickness = 1
            });
            GridCanvas.Children.Add(new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = height * fraction,
                Y2 = height * fraction,
                Stroke = new SolidColorBrush(Color.FromArgb(80, 92, 92, 108)),
                StrokeThickness = 1
            });
        }

        GridCanvas.Children.Add(new Line
        {
            X1 = 0,
            Y1 = height,
            X2 = width,
            Y2 = 0,
            Stroke = new SolidColorBrush(Color.FromArgb(120, 160, 160, 174)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 3, 4 }
        });
    }

    private void RebuildHandles()
    {
        PointsCanvas.Children.Clear();
        _handles.Clear();
        for (var index = 0; index < _points.Count; index++)
        {
            var handle = new Border
            {
                Width = HandleSize,
                Height = HandleSize,
                CornerRadius = new CornerRadius(HandleSize / 2),
                Background = new SolidColorBrush(Color.FromArgb(255, 240, 240, 248)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 169, 112, 255)),
                BorderThickness = new Thickness(2),
                Tag = index
            };
            ToolTipService.SetToolTip(handle, $"Curve point {index + 1}");
            handle.PointerPressed += OnPointPointerPressed;
            handle.PointerMoved += OnPointPointerMoved;
            handle.PointerReleased += OnPointPointerReleased;
            handle.PointerCanceled += OnPointPointerReleased;
            handle.PointerCaptureLost += OnPointPointerCaptureLost;
            PointsCanvas.Children.Add(handle);
            _handles.Add(handle);
        }
    }

    private void UpdateGeometry()
    {
        var width = Math.Max(1, CurveSurface.ActualWidth);
        var height = Math.Max(1, CurveSurface.ActualHeight);
        var linePoints = new PointCollection();
        for (var index = 0; index < _points.Count; index++)
        {
            var x = _points[index].X * width;
            var y = (1 - _points[index].Y) * height;
            linePoints.Add(new Point(x, y));
            if (index >= _handles.Count)
                continue;
            Canvas.SetLeft(_handles[index], x - HandleSize / 2);
            Canvas.SetTop(_handles[index], y - HandleSize / 2);
        }

        CurveLine.Points = linePoints;
    }

    private void OnPointPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsEnabled || sender is not Border { Tag: int index } handle)
            return;

        _activePointIndex = index;
        _capturedHandle = handle;
        handle.CapturePointer(e.Pointer);
        InteractionStarted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnPointPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_activePointIndex < 0 || _capturedHandle is null)
            return;

        var position = e.GetCurrentPoint(CurveSurface).Position;
        var width = Math.Max(1, CurveSurface.ActualWidth);
        var height = Math.Max(1, CurveSurface.ActualHeight);
        var point = _points[_activePointIndex];
        var x = Math.Clamp(position.X / width, 0, 1);
        var y = Math.Clamp(1 - position.Y / height, 0, 1);
        if (_activePointIndex == 0)
            x = 0;
        else if (_activePointIndex == _points.Count - 1)
            x = 1;
        else
            x = Math.Clamp(
                x,
                _points[_activePointIndex - 1].X + MinimumPointGap,
                _points[_activePointIndex + 1].X - MinimumPointGap);
        if (Math.Abs(y - x) < 0.018)
            y = x;

        point.X = x;
        point.Y = y;
        UpdateGeometry();
        CurvePreviewChanged?.Invoke(this, new ColorCurveChangedEventArgs(Points));
        e.Handled = true;
    }

    private void OnPointPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_activePointIndex < 0)
            return;

        var handle = _capturedHandle;
        EndInteraction();
        handle?.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
    }

    private void OnPointPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndInteraction();
    }

    private void OnCurveSurfacePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndInteraction();
    }

    private void OnCurveSurfacePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        EndInteraction();
    }

    private void OnCurveSurfacePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        EndInteraction();
    }

    private void EndInteraction()
    {
        if (_activePointIndex < 0)
            return;

        _capturedHandle = null;
        _activePointIndex = -1;
        CurveCommitted?.Invoke(this, new ColorCurveChangedEventArgs(Points));
    }

    private static IReadOnlyList<EditorProjectCurvePoint> NormalizePoints(
        IEnumerable<EditorProjectCurvePoint>? points)
    {
        var normalized = points?
            .Select(point => new EditorProjectCurvePoint
            {
                X = Math.Clamp(point.X, 0, 1),
                Y = Math.Clamp(point.Y, 0, 1)
            })
            .OrderBy(point => point.X)
            .ToList() ?? [];
        if (normalized.Count < 2)
        {
            return
            [
                new EditorProjectCurvePoint { X = 0, Y = 0 },
                new EditorProjectCurvePoint { X = 0.25, Y = 0.25 },
                new EditorProjectCurvePoint { X = 0.5, Y = 0.5 },
                new EditorProjectCurvePoint { X = 0.75, Y = 0.75 },
                new EditorProjectCurvePoint { X = 1, Y = 1 }
            ];
        }

        normalized[0].X = 0;
        normalized[^1].X = 1;
        return normalized;
    }

    private static IReadOnlyList<EditorProjectCurvePoint> ClonePoints(
        IEnumerable<EditorProjectCurvePoint> points) =>
        points.Select(point => new EditorProjectCurvePoint
        {
            X = point.X,
            Y = point.Y
        }).ToList();
}

public sealed class ColorCurveChangedEventArgs : EventArgs
{
    public ColorCurveChangedEventArgs(IReadOnlyList<EditorProjectCurvePoint> points)
    {
        Points = points;
    }

    public IReadOnlyList<EditorProjectCurvePoint> Points { get; }
}
