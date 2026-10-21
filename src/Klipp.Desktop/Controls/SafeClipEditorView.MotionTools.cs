using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private readonly EditorMotionTrackingService _motionTrackingService = new();
    private readonly List<Point> _motionMaskPoints = [];
    private CancellationTokenSource? _motionFrameCts;
    private CancellationTokenSource? _motionTrackingCts;
    private string? _motionSourceSignature;
    private string? _motionFrameClipId;
    private double _motionFrameLocalSeconds;
    private int _motionFrameWidth;
    private int _motionFrameHeight;
    private string _motionDrawMode = "polygon";
    private int _motionDragVertex = -1;
    private Point? _motionRegionStart;
    private EditorTrackingRegion? _motionTrackingRegion;
    private bool _motionPointerActive;

    private void OnMotionToolsLoaded(object sender, RoutedEventArgs e) => SyncMotionToolsControls();

    private void OnMotionToolsUnloaded(object sender, RoutedEventArgs e)
    {
        _motionFrameCts?.Cancel();
        _motionTrackingCts?.Cancel();
        _motionPointerActive = false;
    }

    private void SyncMotionToolsControls()
    {
        if (MotionToolsExpander is null)
            return;
        var clip = LayerTimeline.GetSelectedMediaClip();
        var signature = clip is null ? string.Empty : JsonSerializer.Serialize(new
        {
            clip.Id, clip.SourcePath, clip.TrimStartSeconds, clip.TrimEndSeconds, clip.PlaybackSpeed,
            clip.CropLeft, clip.CropTop, clip.CropRight, clip.CropBottom
        });
        if (signature != _motionSourceSignature)
        {
            _motionSourceSignature = signature;
            _motionFrameCts?.Cancel();
            _motionTrackingCts?.Cancel();
            _motionPointerActive = false;
            _motionDragVertex = -1;
            _motionRegionStart = null;
            MotionEditorCanvas.ReleasePointerCaptures();
            _motionFrameClipId = null;
            _motionTrackingRegion = null;
            _motionMaskPoints.Clear();
            MotionFrameEditor.Visibility = Visibility.Collapsed;
            MotionToolsStatusText.Text = clip is null ? "Select a video clip to draw a mask or track a subject."
                : "Load a source frame at the playhead to start.";
        }
        var enabled = clip is not null && _project?.GetTrack(clip.TrackId)?.Locked != true;
        LoadMotionFrameButton.IsEnabled = enabled && _motionTrackingCts is null;
        var loaded = enabled && _motionFrameClipId == clip?.Id;
        DrawPolygonMaskButton.IsEnabled = loaded && _motionTrackingCts is null;
        SelectTrackingRegionButton.IsEnabled = loaded && _motionTrackingCts is null;
        ApplyPolygonMaskButton.IsEnabled = loaded && _motionMaskPoints.Count >= 3 && _motionTrackingCts is null;
        UndoPolygonPointButton.IsEnabled = loaded && _motionMaskPoints.Count > 0 && _motionTrackingCts is null;
        RemovePolygonMaskButton.IsEnabled = enabled && clip?.MaskShape != "none" && _motionTrackingCts is null;
        StartMotionTrackingButton.IsEnabled = loaded && _motionTrackingRegion is not null && _motionTrackingCts is null;
        MotionEditorCanvas.IsHitTestVisible = _motionTrackingCts is null;
        MotionTrackingZoomBox.IsEnabled = MotionTrackingDurationBox.IsEnabled = _motionTrackingCts is null;
    }

    private async void OnLoadMotionSourceFrameClicked(object sender, RoutedEventArgs e)
    {
        var clip = LayerTimeline.GetSelectedMediaClip();
        if (clip is null || _project?.GetTrack(clip.TrackId)?.Locked == true)
            return;
        _motionFrameCts?.Cancel();
        var cancellation = new CancellationTokenSource();
        _motionFrameCts = cancellation;
        var project = _project;
        var loadVersion = _projectLoadVersion;
        var localSeconds = Math.Clamp(LayerTimeline.GetMediaClipLocalTime(clip.Id, CurrentTimelinePosition),
            0, Math.Max(0, clip.EffectiveDurationSeconds - 0.05));
        PausePreview();
        MotionToolsStatusText.Text = "Reading source frame…";
        LoadMotionFrameButton.IsEnabled = false;
        try
        {
            var frame = await _motionTrackingService.GetReferenceFrameAsync(clip.SourcePath,
                clip.TrimStartSeconds + localSeconds * clip.PlaybackSpeed, cancellation.Token);
            if (cancellation.IsCancellationRequested || loadVersion != _projectLoadVersion || !ReferenceEquals(project, _project) ||
                LayerTimeline.SelectedMediaClipId != clip.Id)
                return;
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(frame.Png))
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            if (cancellation.IsCancellationRequested || loadVersion != _projectLoadVersion || !ReferenceEquals(project, _project) ||
                LayerTimeline.SelectedMediaClipId != clip.Id)
                return;
            _motionFrameClipId = clip.Id;
            _motionFrameLocalSeconds = localSeconds;
            _motionFrameWidth = frame.Width;
            _motionFrameHeight = frame.Height;
            var fit = Math.Min(276.0 / frame.Width, 180.0 / frame.Height);
            MotionEditorCanvas.Width = MotionSourceFrameImage.Width = frame.Width * fit;
            MotionEditorCanvas.Height = MotionSourceFrameImage.Height = frame.Height * fit;
            MotionSourceFrameImage.Source = bitmap;
            MotionFrameEditor.Visibility = Visibility.Visible;
            _motionMaskPoints.Clear();
            _motionMaskPoints.AddRange(clip.MaskPolygon.Select(point => new Point(point.X, point.Y)));
            MotionMaskInvertCheckBox.IsChecked = clip.MaskInverted;
            _motionTrackingRegion = null;
            _motionDrawMode = "polygon";
            MotionToolsStatusText.Text = "Click to add mask points. Drag a point to adjust it; right-click a point to remove it.";
            RedrawMotionEditor();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { MotionToolsStatusText.Text = exception.Message; }
        finally
        {
            if (ReferenceEquals(_motionFrameCts, cancellation)) _motionFrameCts = null;
            cancellation.Dispose();
            SyncMotionToolsControls();
        }
    }

    private void OnDrawPolygonMaskClicked(object sender, RoutedEventArgs e)
    {
        _motionDrawMode = "polygon";
        MotionToolsStatusText.Text = "Click to add points, then Apply mask. Drag vertices to refine the outline.";
        RedrawMotionEditor();
    }

    private void OnSelectTrackingRegionClicked(object sender, RoutedEventArgs e)
    {
        _motionDrawMode = "region";
        MotionToolsStatusText.Text = "Drag a small rectangle around a textured subject. Tracking keeps it in place by panning the video.";
        RedrawMotionEditor();
    }

    private void OnMotionEditorPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_motionTrackingCts is not null || _motionFrameClipId is null)
            return;
        var current = e.GetCurrentPoint(MotionEditorCanvas);
        var point = NormalizeMotionPoint(current.Position);
        if (_motionDrawMode == "polygon")
        {
            _motionDragVertex = _motionMaskPoints.FindIndex(vertex =>
                Math.Pow((vertex.X - point.X) * MotionEditorCanvas.Width, 2) +
                Math.Pow((vertex.Y - point.Y) * MotionEditorCanvas.Height, 2) <= 100);
            if (current.Properties.IsRightButtonPressed)
            {
                if (_motionDragVertex >= 0) _motionMaskPoints.RemoveAt(_motionDragVertex);
                _motionDragVertex = -1;
                RedrawMotionEditor(); SyncMotionToolsControls(); e.Handled = true;
                return;
            }
            if (!current.Properties.IsLeftButtonPressed) return;
            if (_motionDragVertex < 0 && _motionMaskPoints.Count < 32)
            {
                _motionMaskPoints.Add(point);
                _motionDragVertex = _motionMaskPoints.Count - 1;
            }
        }
        else
        {
            if (!current.Properties.IsLeftButtonPressed) return;
            _motionRegionStart = point;
            _motionTrackingRegion = null;
        }
        _motionPointerActive = MotionEditorCanvas.CapturePointer(e.Pointer);
        RedrawMotionEditor(); SyncMotionToolsControls(); e.Handled = true;
    }

    private void OnMotionEditorPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_motionPointerActive) return;
        var point = NormalizeMotionPoint(e.GetCurrentPoint(MotionEditorCanvas).Position);
        if (_motionDrawMode == "polygon" && _motionDragVertex >= 0)
            _motionMaskPoints[_motionDragVertex] = point;
        else if (_motionRegionStart is { } start)
            _motionTrackingRegion = new(Math.Min(start.X, point.X), Math.Min(start.Y, point.Y),
                Math.Abs(point.X - start.X), Math.Abs(point.Y - start.Y));
        RedrawMotionEditor(); e.Handled = true;
    }

    private void OnMotionEditorPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_motionPointerActive) return;
        OnMotionEditorPointerMoved(sender, e);
        _motionPointerActive = false;
        _motionDragVertex = -1;
        _motionRegionStart = null;
        MotionEditorCanvas.ReleasePointerCapture(e.Pointer);
        if (_motionTrackingRegion is { } region && (region.Width * _motionFrameWidth < 8 || region.Height * _motionFrameHeight < 8))
        {
            _motionTrackingRegion = null;
            MotionToolsStatusText.Text = "Draw a larger tracking region with at least 8 pixels on each side.";
        }
        RedrawMotionEditor(); SyncMotionToolsControls(); e.Handled = true;
    }

    private void OnMotionEditorPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _motionPointerActive = false; _motionDragVertex = -1; _motionRegionStart = null;
    }

    private Point NormalizeMotionPoint(Point point) => new(
        Math.Clamp(point.X / Math.Max(1, MotionEditorCanvas.Width), 0, 1),
        Math.Clamp(point.Y / Math.Max(1, MotionEditorCanvas.Height), 0, 1));

    private void RedrawMotionEditor()
    {
        while (MotionEditorCanvas.Children.Count > 1) MotionEditorCanvas.Children.RemoveAt(1);
        var accent = new SolidColorBrush(Color.FromArgb(255, 113, 152, 199));
        if (_motionDrawMode == "polygon")
        {
            var outline = new Polygon { Stroke = accent, StrokeThickness = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(40, 113, 152, 199)), IsHitTestVisible = false };
            foreach (var point in _motionMaskPoints)
                outline.Points.Add(new(point.X * MotionEditorCanvas.Width, point.Y * MotionEditorCanvas.Height));
            MotionEditorCanvas.Children.Add(outline);
            foreach (var point in _motionMaskPoints)
            {
                var handle = new Ellipse { Width = 8, Height = 8, Fill = accent,
                    Stroke = new SolidColorBrush(Color.FromArgb(255, 230, 232, 235)), StrokeThickness = 1, IsHitTestVisible = false };
                Canvas.SetLeft(handle, point.X * MotionEditorCanvas.Width - 4);
                Canvas.SetTop(handle, point.Y * MotionEditorCanvas.Height - 4);
                MotionEditorCanvas.Children.Add(handle);
            }
        }
        else if (_motionTrackingRegion is { } region)
        {
            var rectangle = new Rectangle { Width = region.Width * MotionEditorCanvas.Width,
                Height = region.Height * MotionEditorCanvas.Height, Stroke = accent, StrokeThickness = 1.5,
                Fill = new SolidColorBrush(Color.FromArgb(35, 113, 152, 199)), IsHitTestVisible = false };
            Canvas.SetLeft(rectangle, region.X * MotionEditorCanvas.Width);
            Canvas.SetTop(rectangle, region.Y * MotionEditorCanvas.Height);
            MotionEditorCanvas.Children.Add(rectangle);
        }
    }

    private void OnUndoPolygonPointClicked(object sender, RoutedEventArgs e)
    {
        if (_motionMaskPoints.Count > 0) _motionMaskPoints.RemoveAt(_motionMaskPoints.Count - 1);
        RedrawMotionEditor(); SyncMotionToolsControls();
    }

    private void OnApplyPolygonMaskClicked(object sender, RoutedEventArgs e)
    {
        if (_motionMaskPoints.Count < 3 || LayerTimeline.SelectedMediaClipId != _motionFrameClipId) return;
        var points = _motionMaskPoints.Select(point => new EditorProjectCurvePoint { X = point.X, Y = point.Y }).ToList();
        if (ApplySelectedClipMutation(clip =>
            {
                clip.MaskPolygon = points; clip.MaskShape = "polygon";
                clip.MaskInverted = MotionMaskInvertCheckBox.IsChecked == true;
            }))
            MotionToolsStatusText.Text = $"Polygon mask applied · {points.Count} points. Undo restores the previous mask.";
    }

    private void OnRemovePolygonMaskClicked(object sender, RoutedEventArgs e)
    {
        if (ApplySelectedClipMutation(clip => { clip.MaskShape = "none"; clip.MaskPolygon.Clear(); clip.MaskInverted = false; }))
        {
            _motionMaskPoints.Clear(); RedrawMotionEditor(); SyncMotionToolsControls();
            MotionToolsStatusText.Text = "Mask removed.";
        }
    }

    private async void OnStartMotionTrackingClicked(object sender, RoutedEventArgs e)
    {
        var clip = LayerTimeline.GetSelectedMediaClip();
        if (clip is null || _project is null || _motionTrackingCts is not null || _motionTrackingRegion is not { } region ||
            clip.Id != _motionFrameClipId || _project.GetTrack(clip.TrackId)?.Locked == true) return;
        var project = _project;
        var loadVersion = _projectLoadVersion;
        var originalMotion = MotionSignature(clip);
        var cancellation = new CancellationTokenSource();
        _motionTrackingCts = cancellation;
        var sourceStart = clip.TrimStartSeconds + _motionFrameLocalSeconds * clip.PlaybackSpeed;
        var duration = Math.Min(clip.TrimEndSeconds - sourceStart,
            Math.Clamp(double.IsFinite(MotionTrackingDurationBox.Value) ? MotionTrackingDurationBox.Value : 15, 0.5, 120) * clip.PlaybackSpeed);
        var zoom = Math.Clamp(double.IsFinite(MotionTrackingZoomBox.Value) ? MotionTrackingZoomBox.Value / 100 : 1.5, 1.1, 4);
        PausePreview();
        MotionTrackingProgress.Value = 0;
        MotionTrackingProgress.Visibility = CancelMotionTrackingButton.Visibility = Visibility.Visible;
        MotionToolsStatusText.Text = "Tracking subject…";
        SyncMotionToolsControls();
        try
        {
            var progress = new Progress<EditorMotionTrackingProgress>(value =>
            {
                if (!ReferenceEquals(_motionTrackingCts, cancellation)) return;
                MotionTrackingProgress.Value = value.Fraction * 100;
                MotionToolsStatusText.Text = $"Tracking · {value.SourceOffsetSeconds / clip.PlaybackSpeed:0.0}s · {value.Confidence:P0} confidence";
            });
            var result = await _motionTrackingService.TrackAsync(clip.SourcePath, sourceStart, duration,
                _motionFrameWidth, _motionFrameHeight, region, progress, cancellation.Token);
            if (cancellation.IsCancellationRequested || loadVersion != _projectLoadVersion || !ReferenceEquals(project, _project)) return;
            var current = LayerTimeline.GetSelectedMediaClip();
            if (current?.Id != clip.Id || MotionSignature(current) != originalMotion)
            {
                MotionToolsStatusText.Text = "The clip changed while tracking. Load a new frame and track again.";
                return;
            }
            var first = result.Samples[0];
            var initial = clip.EvaluateTransform(_motionFrameLocalSeconds);
            var keys = new List<EditorProjectClipTransformKeyframe>();
            var panLimited = false;
            foreach (var sample in result.Samples)
            {
                var pan = EditorMotionTrackingMath.PanForSourceMotion(sample.CenterX - first.CenterX,
                    sample.CenterY - first.CenterY, zoom, _motionFrameWidth / (double)_motionFrameHeight,
                    project.CanvasWidth / (double)Math.Max(1, project.CanvasHeight),
                    clip.CropLeft, clip.CropTop, clip.CropRight, clip.CropBottom);
                var x = initial.OffsetX + pan.X;
                var y = initial.OffsetY + pan.Y;
                if (Math.Abs(x) > 1 || Math.Abs(y) > 1) { panLimited = true; break; }
                keys.Add(new() { TimeSeconds = _motionFrameLocalSeconds + sample.SourceOffsetSeconds / clip.PlaybackSpeed,
                    Scale = zoom, OffsetX = x, OffsetY = y });
            }
            if (keys.Count < 2)
            {
                MotionToolsStatusText.Text = "The subject reached the pan limit. Increase tracking zoom and try again.";
                return;
            }
            if (ApplySelectedClipMutation(target =>
                {
                    target.TransformKeyframes.RemoveAll(key => key.TimeSeconds >= keys[0].TimeSeconds - 0.004 &&
                        key.TimeSeconds <= keys[^1].TimeSeconds + 0.004);
                    target.TransformKeyframes.AddRange(keys);
                }))
            {
                var reason = panLimited ? " Stopped at the pan limit; increase zoom to follow farther."
                    : result.LostSubject ? " Stopped when the subject was lost." : string.Empty;
                MotionToolsStatusText.Text = $"Added {keys.Count} motion keyframes · {keys[^1].TimeSeconds - keys[0].TimeSeconds:0.0}s tracked.{reason}";
                ProjectStatusText.Text = "Subject tracking applied — Undo restores previous motion";
            }
        }
        catch (OperationCanceledException) { MotionToolsStatusText.Text = "Tracking cancelled. Existing motion is unchanged."; }
        catch (Exception exception) { MotionToolsStatusText.Text = exception.Message; }
        finally
        {
            if (ReferenceEquals(_motionTrackingCts, cancellation)) _motionTrackingCts = null;
            cancellation.Dispose();
            MotionTrackingProgress.Visibility = CancelMotionTrackingButton.Visibility = Visibility.Collapsed;
            SyncMotionToolsControls();
        }
    }

    private void OnCancelMotionTrackingClicked(object sender, RoutedEventArgs e) => _motionTrackingCts?.Cancel();

    private static string MotionSignature(EditorProjectClip clip) => JsonSerializer.Serialize(new
    {
        clip.SourcePath, clip.TrimStartSeconds, clip.TrimEndSeconds, clip.PlaybackSpeed,
        clip.Scale, clip.OffsetX, clip.OffsetY, clip.TransformKeyframes,
        clip.CropLeft, clip.CropTop, clip.CropRight, clip.CropBottom
    });
}
