using System;
using System.Collections.Generic;
using System.Linq;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private Border? _fxPreviewMoveArea;
    private Rectangle? _fxPreviewRectangle;
    private Ellipse? _fxPreviewEllipse;
    private Border? _fxPreviewCaption;
    private TextBlock? _fxPreviewCaptionText;
    private readonly Dictionary<string, Border> _fxPreviewCorners = new(StringComparer.Ordinal);
    private string? _fxPreviewDragId;
    private string _fxPreviewDragCorner = "move";
    private EditorEffectRegion _fxPreviewDragRegion;
    private EditorProjectEffectLayer? _fxPreviewDragEffect;
    private EditorEffectPoint _fxPreviewDragStart;
    private Point _fxPreviewDragScreenStart;
    private EditorEffectPoint? _fxPreviewPendingPoint;
    private bool _fxPreviewDragActivated;
    private bool _fxPreviewRendering;

    private void EnsureTimelineFxPreviewAdorner()
    {
        if (_fxPreviewMoveArea is not null) return;
        var accent = ResourceBrush("KlippAccentBrush");
        _fxPreviewMoveArea = new Border { Tag = "move", Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        AutomationProperties.SetName(_fxPreviewMoveArea, "Move the selected FX area");
        RegisterFxPreviewDragTarget(_fxPreviewMoveArea, "move");
        TimelineFxPreviewAdorner.Children.Add(_fxPreviewMoveArea);
        _fxPreviewRectangle = new Rectangle { Stroke = accent, StrokeThickness = 1.5, IsHitTestVisible = false };
        _fxPreviewEllipse = new Ellipse { Stroke = accent, StrokeThickness = 1.5, IsHitTestVisible = false };
        TimelineFxPreviewAdorner.Children.Add(_fxPreviewRectangle);
        TimelineFxPreviewAdorner.Children.Add(_fxPreviewEllipse);
        foreach (var corner in new[] { "nw", "ne", "sw", "se" })
        {
            var handle = new Border
            {
                Width = 24, Height = 24, Tag = corner, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Child = new Border
                {
                    Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Microsoft.UI.Colors.White),
                    BorderBrush = accent, BorderThickness = new Thickness(2),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false
                }
            };
            var label = corner switch { "nw" => "top left", "ne" => "top right", "sw" => "bottom left", _ => "bottom right" };
            AutomationProperties.SetName(handle, $"Resize FX {label} corner");
            ToolTipService.SetToolTip(handle, $"Drag the {label} corner to resize the FX area");
            RegisterFxPreviewDragTarget(handle, corner);
            _fxPreviewCorners[corner] = handle;
            TimelineFxPreviewAdorner.Children.Add(handle);
        }
        _fxPreviewCaptionText = new TextBlock { FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 230 };
        _fxPreviewCaption = new Border
        {
            Tag = "move", Background = ResourceBrush("KlippSurfaceElevatedBrush"), BorderBrush = accent,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(6, 3, 6, 3),
            Child = _fxPreviewCaptionText
        };
        RegisterFxPreviewDragTarget(_fxPreviewCaption, "move");
        TimelineFxPreviewAdorner.Children.Add(_fxPreviewCaption);
    }

    private void RegisterFxPreviewDragTarget(Border target, string corner)
    {
        target.PointerPressed += OnTimelineFxPreviewPressed;
        target.PointerEntered += (_, _) => SetFxPreviewCursor(corner);
        target.PointerExited += (_, _) => { if (_fxPreviewDragId is null) ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow); };
    }

    private void SetFxPreviewCursor(string corner) => ProtectedCursor = InputSystemCursor.Create(corner switch
    {
        "nw" or "se" => InputSystemCursorShape.SizeNorthwestSoutheast,
        "ne" or "sw" => InputSystemCursorShape.SizeNortheastSouthwest,
        _ => InputSystemCursorShape.SizeAll
    });

    private void UpdateTimelineFxPreviewAdorner()
    {
        if (TimelineFxPreviewAdorner is null) return;
        var effect = LayerTimeline?.GetSelectedEffectLayer();
        if (_fxPreviewDragId is not null && effect?.Id != _fxPreviewDragId)
            EndTimelineFxPreviewGesture(commit: false);
        var clip = effect is null ? null : LayerTimeline!.FindMediaClipById(effect.TargetClipId);
        var region = effect is null ? default : effect.EvaluateRegion(TimelineFxEditTime(effect));
        if (_activeTool != "vfx" || effect is null || clip is null || _project?.GetTrack(clip.TrackId)?.Locked == true ||
            !SourceNormalizedToPreview(effect, region.X, region.Y, out var first) ||
            !SourceNormalizedToPreview(effect, region.Right, region.Bottom, out var last))
        {
            TimelineFxPreviewAdorner.Visibility = Visibility.Collapsed;
            return;
        }
        EnsureTimelineFxPreviewAdorner();
        var toWorkspace = PreviewSurface.TransformToVisual(PreviewWorkspace);
        first = toWorkspace.TransformPoint(first);
        last = toWorkspace.TransformPoint(last);
        var left = Math.Min(first.X, last.X);
        var top = Math.Min(first.Y, last.Y);
        var width = Math.Abs(last.X - first.X);
        var height = Math.Abs(last.Y - first.Y);
        if (width < .01 || height < .01)
        {
            TimelineFxPreviewAdorner.Visibility = Visibility.Collapsed;
            return;
        }
        TimelineFxPreviewAdorner.Visibility = Visibility.Visible;
        void Place(FrameworkElement element, double x, double y, double w, double h)
        {
            Canvas.SetLeft(element, x); Canvas.SetTop(element, y); element.Width = w; element.Height = h;
        }
        Place(_fxPreviewMoveArea!, left, top, width, height);
        Place(_fxPreviewRectangle!, left, top, width, height);
        Place(_fxPreviewEllipse!, left, top, width, height);
        _fxPreviewRectangle!.Visibility = effect.RegionShape == "ellipse" ? Visibility.Collapsed : Visibility.Visible;
        _fxPreviewEllipse!.Visibility = effect.RegionShape == "ellipse" ? Visibility.Visible : Visibility.Collapsed;
        var workspaceWidth = Math.Max(24, PreviewWorkspace.ActualWidth);
        var workspaceHeight = Math.Max(24, PreviewWorkspace.ActualHeight);
        foreach (var (corner, handle) in _fxPreviewCorners)
        {
            var x = corner.EndsWith('w') ? left : left + width;
            var y = corner.StartsWith('n') ? top : top + height;
            Canvas.SetLeft(handle, Math.Clamp(x - 12, 0, workspaceWidth - 24));
            Canvas.SetTop(handle, Math.Clamp(y - 12, 0, workspaceHeight - 24));
        }
        _fxPreviewCaptionText!.Text = $"{effect.Name} · drag to move";
        Canvas.SetLeft(_fxPreviewCaption!, Math.Clamp(left, 0, Math.Max(0, workspaceWidth - 180)));
        Canvas.SetTop(_fxPreviewCaption!, top >= 28 ? top - 27 : Math.Min(workspaceHeight - 27, top + 12));
    }

    private void OnTimelineFxPreviewPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_fxPreviewDragId is not null || sender is not FrameworkElement { Tag: string corner } ||
            !e.GetCurrentPoint(PreviewWorkspace).Properties.IsLeftButtonPressed ||
            LayerTimeline.GetSelectedEffectLayer() is not { } effect ||
            LayerTimeline.FindMediaClipById(effect.TargetClipId) is not { } clip || _project?.GetTrack(clip.TrackId)?.Locked == true ||
            !PreviewToSourceNormalized(effect, e.GetCurrentPoint(PreviewSurface).Position, out var source)) return;
        CommitPendingProjectChange();
        PausePreview();
        Focus(FocusState.Pointer);
        _fxPreviewDragId = effect.Id;
        _fxPreviewDragCorner = corner;
        _fxPreviewDragRegion = effect.EvaluateRegion(TimelineFxEditTime(effect));
        _fxPreviewDragEffect = effect.Clone();
        _fxPreviewDragStart = source;
        _fxPreviewDragScreenStart = e.GetCurrentPoint(PreviewWorkspace).Position;
        _fxPreviewDragActivated = false;
        _fxPreviewPendingPoint = null;
        _projectChangeTimer.Stop();
        PreviewWorkspace.CapturePointer(e.Pointer);
        SetFxPreviewCursor(corner);
        _suppressNextPreviewTap = true;
        e.Handled = true;
    }

    private void OnTimelineFxPreviewMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_fxPreviewDragId is null) return;
        if (!e.GetCurrentPoint(PreviewWorkspace).Properties.IsLeftButtonPressed)
        {
            FinishTimelineFxPreviewPointer(e);
            return;
        }
        QueueTimelineFxPreviewPoint(e);
        e.Handled = true;
    }

    private void QueueTimelineFxPreviewPoint(PointerRoutedEventArgs e)
    {
        var effect = LayerTimeline.GetSelectedEffectLayer();
        if (effect is null || effect.Id != _fxPreviewDragId || !PreviewToSourceNormalized(effect, e.GetCurrentPoint(PreviewSurface).Position, out var point)) return;
        var screen = e.GetCurrentPoint(PreviewWorkspace).Position;
        var dx = screen.X - _fxPreviewDragScreenStart.X;
        var dy = screen.Y - _fxPreviewDragScreenStart.Y;
        if (!_fxPreviewDragActivated && dx * dx + dy * dy < 9) return;
        _fxPreviewDragActivated = true;
        _fxPreviewPendingPoint = point;
        if (_fxPreviewRendering) return;
        CompositionTarget.Rendering += OnTimelineFxPreviewRendering;
        _fxPreviewRendering = true;
    }

    private void OnTimelineFxPreviewRendering(object? sender, object e) => ApplyPendingTimelineFxPreviewPoint();

    private void ApplyPendingTimelineFxPreviewPoint()
    {
        if (_fxPreviewPendingPoint is not { } point || _fxPreviewDragId is null || LayerTimeline.SelectedEffectLayerId != _fxPreviewDragId) return;
        _fxPreviewPendingPoint = null;
        var delta = new EditorEffectPoint(point.X - _fxPreviewDragStart.X, point.Y - _fxPreviewDragStart.Y);
        var original = _fxPreviewDragRegion;
        var corner = new EditorEffectPoint(_fxPreviewDragCorner.EndsWith('w') ? original.X : original.Right,
            _fxPreviewDragCorner.StartsWith('n') ? original.Y : original.Bottom);
        var region = _fxPreviewDragCorner == "move"
            ? EditorEffectRegionMath.MoveWithinSource(original, delta)
            : EditorEffectRegionMath.ResizeCorner(original, new(corner.X + delta.X, corner.Y + delta.Y), _fxPreviewDragCorner);
        ApplyTimelineFxEdit(effect => SetTimelineFxRegion(effect, region), commit: false, syncControls: false);
        _projectChangeTimer.Stop();
        UpdateTimelineFxPreviewAdorner();
    }

    private void OnTimelineFxPreviewReleased(object sender, PointerRoutedEventArgs e) => FinishTimelineFxPreviewPointer(e);

    private void FinishTimelineFxPreviewPointer(PointerRoutedEventArgs e)
    {
        if (_fxPreviewDragId is null) return;
        QueueTimelineFxPreviewPoint(e);
        ApplyPendingTimelineFxPreviewPoint();
        EndTimelineFxPreviewGesture(commit: true);
        e.Handled = true;
    }

    private void OnTimelineFxPreviewCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_fxPreviewDragId is null) return;
        EndTimelineFxPreviewGesture(commit: false);
        e.Handled = true;
    }

    private void EndTimelineFxPreviewGesture(bool commit)
    {
        var id = _fxPreviewDragId;
        if (id is null) return;
        if (commit) ApplyPendingTimelineFxPreviewPoint();
        var original = _fxPreviewDragEffect;
        _fxPreviewDragId = null;
        _fxPreviewDragEffect = null;
        _fxPreviewPendingPoint = null;
        if (_fxPreviewRendering)
        {
            CompositionTarget.Rendering -= OnTimelineFxPreviewRendering;
            _fxPreviewRendering = false;
        }
        PreviewWorkspace.ReleasePointerCaptures();
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        if (!commit)
        {
            var selectedId = LayerTimeline.SelectedEffectLayerId;
            var layers = LayerTimeline.GetEffectLayers().ToList();
            if (original is not null && layers.FirstOrDefault(layer => layer.Id == id) is { } effect)
            {
                effect.RegionX = original.RegionX; effect.RegionY = original.RegionY;
                effect.RegionWidth = original.RegionWidth; effect.RegionHeight = original.RegionHeight;
                effect.RegionKeyframes = original.RegionKeyframes.Select(key => key.Clone()).ToList();
                LayerTimeline.SetEffectLayers(layers, selectedId);
                SyncTimelineEffectsToProject();
                UpdateVfxPreview();
                QueueProjectChange();
            }
        }
        CommitPendingProjectChange();
        QueueProjectSaveOnly();
        SyncTimelineFxInspector();
        UpdateProjectChrome();
        UpdateTimelineFxPreviewAdorner();
    }
}
