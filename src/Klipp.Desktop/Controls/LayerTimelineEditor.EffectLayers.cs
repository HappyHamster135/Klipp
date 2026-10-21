using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace Klipp.Desktop.Controls;

public sealed partial class LayerTimelineEditor
{
    private readonly List<EditorProjectEffectLayer> _effectLayers = [];
    private readonly Dictionary<string, (Border Row, TextBlock Label, Border Block, TextBlock Name)> _effectLayerVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Border>> _effectRegionKeyframeMarkers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double Start, double TrimStart, double Speed)> _effectTargetPositions = new(StringComparer.Ordinal);
    private string? _selectedEffectLayerId;
    private string? _dragEffectLayerId;
    private double _dragEffectStart;
    private double _dragEffectEnd;

    public string? SelectedEffectLayerId => _selectedLayer == EditorTimelineLayer.Vfx ? _selectedEffectLayerId : null;
    public EditorProjectEffectLayer? GetSelectedEffectLayer() => _effectLayers.FirstOrDefault(layer => layer.Id == SelectedEffectLayerId) is { } layer
        ? CloneEffectLayer(layer) : null;
    public IReadOnlyList<EditorProjectEffectLayer> GetEffectLayers() => _effectLayers.Select(CloneEffectLayer).ToList();

    public void SetEffectLayers(IEnumerable<EditorProjectEffectLayer> layers, string? selectedId = null)
    {
        var incoming = layers.Select(CloneEffectLayer).ToList();
        var structureChanged = !_effectLayers.Select(layer => layer.Id)
            .SequenceEqual(incoming.Select(layer => layer.Id), StringComparer.Ordinal);
        _effectLayers.Clear();
        _effectLayers.AddRange(incoming);
        _selectedEffectLayerId = _effectLayers.Any(layer => layer.Id == selectedId) ? selectedId : null;
        RememberEffectTargetPositions();
        if (structureChanged)
            RebuildEffectLayerBlocks();
        UpdateVisuals(false, null, EditorTimelineLayer.None);
    }

    public bool SelectEffectLayerForEditing(string id)
    {
        var layer = _effectLayers.FirstOrDefault(item => item.Id == id);
        if (layer is null) return false;
        SelectOnlyMediaClip(layer.TargetClipId);
        _selectedEffectLayerId = id;
        _selectedLayer = EditorTimelineLayer.Vfx;
        ClearMarkerSelection();
        UpdateVisuals(true, _playhead, EditorTimelineLayer.Vfx, selectionOnly: true);
        return true;
    }

    public Rect? SelectedEffectLayerBounds => EffectLayerBounds(SelectedEffectLayerId);
    public Rect? EffectLayerBounds(string? id)
    {
        var index = _effectLayers.FindIndex(layer => layer.Id == id);
        return index < 0 ? null : new Rect(0, EffectLayersTop + index * (TrackHeight + TrackGap),
            Math.Max(240, TimelineSurface.ActualWidth), TrackHeight);
    }

    public string? AddEffectLayerToClip(string clipId, string filter, double? start = null)
    {
        var clip = _mediaClips.FirstOrDefault(item => item.Id == clipId);
        if (clip is null || IsClipLocked(clip) || !IsSupportedEffectFilter(filter)) return null;
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        var layer = new EditorProjectEffectLayer
        {
            TargetClipId = clip.Id, Name = EffectFilterName(filter), Filter = filter,
            RegionFeather = .15,
            StartSeconds = Math.Clamp(start ?? clip.TimelineStartSeconds, clip.TimelineStartSeconds,
                Math.Max(clip.TimelineStartSeconds, clip.TimelineStartSeconds + clip.EffectiveDurationSeconds - MinClipSeconds)),
            EndSeconds = clip.TimelineStartSeconds + clip.EffectiveDurationSeconds
        };
        _effectLayers.Add(layer);
        SelectOnlyMediaClip(clip.Id);
        _selectedEffectLayerId = layer.Id;
        _selectedLayer = EditorTimelineLayer.Vfx;
        RebuildEffectLayerBlocks();
        CommitUndoSnapshot(before);
        UpdateVisuals(true, _playhead, EditorTimelineLayer.Vfx);
        SetEditStatus($"{layer.Name} added to the FX timeline. Drag its edges to choose when it appears.");
        return layer.Id;
    }

    public bool UpdateSelectedEffectLayer(Action<EditorProjectEffectLayer> mutate)
    {
        var layer = _effectLayers.FirstOrDefault(item => item.Id == SelectedEffectLayerId);
        var target = _mediaClips.FirstOrDefault(clip => clip.Id == layer?.TargetClipId);
        if (layer is null || target is null || IsClipLocked(target)) return false;
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        mutate(layer);
        if (before.EffectLayers.FirstOrDefault(item => item.Id == layer.Id) is { } original &&
            (Math.Abs(original.StartSeconds - layer.StartSeconds) > .0000001 || Math.Abs(original.EndSeconds - layer.EndSeconds) > .0000001))
            EditorEffectRegionAnimationService.ApplyRange(layer, original, layer.StartSeconds, layer.EndSeconds,
                move: Math.Abs((layer.StartSeconds - original.StartSeconds) - (layer.EndSeconds - original.EndSeconds)) < .0000001);
        NormalizeEffectLayerRange(layer, target);
        CommitUndoSnapshot(before);
        UpdateVisuals(true, _playhead, EditorTimelineLayer.Vfx);
        return true;
    }

    public bool DeleteSelectedEffectLayer()
    {
        var layer = _effectLayers.FirstOrDefault(item => item.Id == SelectedEffectLayerId);
        if (layer is null || _mediaClips.FirstOrDefault(clip => clip.Id == layer.TargetClipId) is not { } target || IsClipLocked(target)) return false;
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        _effectLayers.Remove(layer);
        _selectedEffectLayerId = null;
        _selectedLayer = EditorTimelineLayer.None;
        RebuildEffectLayerBlocks();
        CommitUndoSnapshot(before);
        UpdateVisuals(true, _playhead, EditorTimelineLayer.Vfx);
        SetEditStatus("FX removed. The video is unchanged.");
        return true;
    }

    public bool DuplicateSelectedEffectLayer()
    {
        var layer = _effectLayers.FirstOrDefault(item => item.Id == SelectedEffectLayerId);
        if (layer is null || _mediaClips.FirstOrDefault(clip => clip.Id == layer.TargetClipId) is not { } target || IsClipLocked(target)) return false;
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        var copy = CloneEffectLayer(layer);
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name += " copy";
        _effectLayers.Insert(_effectLayers.IndexOf(layer) + 1, copy);
        _selectedEffectLayerId = copy.Id;
        RebuildEffectLayerBlocks();
        CommitUndoSnapshot(before);
        UpdateVisuals(true, _playhead, EditorTimelineLayer.Vfx);
        SetEditStatus("FX duplicated. Select either FX item to adjust its range or area.");
        return true;
    }

    private bool SplitSelectedEffectLayer(TimeSpan position)
    {
        var layer = _effectLayers.FirstOrDefault(item => item.Id == SelectedEffectLayerId);
        if (layer is null || _mediaClips.FirstOrDefault(clip => clip.Id == layer.TargetClipId) is not { } target || IsClipLocked(target)) return false;
        var time = SnapTime(position).TotalSeconds;
        if (time < layer.StartSeconds + MinClipSeconds || time > layer.EndSeconds - MinClipSeconds) return false;
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        var right = CloneEffectLayer(layer);
        var original = CloneEffectLayer(layer);
        right.Id = Guid.NewGuid().ToString("N");
        EditorEffectRegionAnimationService.ApplyRange(right, original, time, original.EndSeconds);
        EditorEffectRegionAnimationService.ApplyRange(layer, original, original.StartSeconds, time);
        _effectLayers.Insert(_effectLayers.IndexOf(layer) + 1, right);
        _selectedEffectLayerId = right.Id;
        RebuildEffectLayerBlocks();
        CommitUndoSnapshot(before);
        UpdateVisuals(true, _playhead, EditorTimelineLayer.Vfx);
        SetEditStatus($"FX split at {FrameTime(time)}.");
        return true;
    }

    private void OnEffectLayerPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsPrimaryPointer(e) || sender is not FrameworkElement { Tag: string id } element ||
            _effectLayers.FirstOrDefault(layer => layer.Id == id) is not { } layer ||
            _mediaClips.FirstOrDefault(clip => clip.Id == layer.TargetClipId) is not { } target || IsClipLocked(target)) return;
        Focus(FocusState.Pointer);
        SelectEffectLayerForEditing(id);
        var point = e.GetCurrentPoint(element).Position;
        var edge = EditorTimelineInteractionMath.TrimEdgeHitWidth(element.ActualWidth, 16);
        var mode = point.X <= edge && layer.StartSeconds >= VisibleStart.TotalSeconds ? DragMode.EffectStart
            : point.X >= element.ActualWidth - edge && layer.EndSeconds <= VisibleEnd.TotalSeconds ? DragMode.EffectEnd : DragMode.EffectMove;
        var surfacePoint = e.GetCurrentPoint(TimelineSurface).Position;
        if (_clipDoubleClick.Press($"fx:{id}", Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency,
                surfacePoint.X, surfacePoint.Y, 0, mode == DragMode.EffectMove))
        {
            SplitSelectedEffectLayer(TimeFromX(surfacePoint.X));
            e.Handled = true;
            return;
        }
        _dragMode = mode;
        _dragLayer = EditorTimelineLayer.Vfx;
        _dragEffectLayerId = id;
        _dragEffectStart = layer.StartSeconds;
        _dragEffectEnd = layer.EndSeconds;
        _dragStartTime = TimeFromPointer(e);
        _dragStartPlayhead = _playhead;
        _dragUndoSnapshot = TakeSnapshot();
        _dragEdgePointerOffset = mode == DragMode.EffectStart ? TimeSpan.FromSeconds(layer.StartSeconds) - _dragStartTime
            : mode == DragMode.EffectEnd ? TimeSpan.FromSeconds(layer.EndSeconds) - _dragStartTime : TimeSpan.Zero;
        UpdateHoverVisual(false);
        TimelineSurface.CapturePointer(e.Pointer);
        UpdateDrag(e, refreshSelection: true);
        e.Handled = true;
    }

    private TimeSpan UpdateEffectLayerDrag(TimeSpan time, out TimeSpan? guide)
    {
        guide = null;
        var layer = _effectLayers.FirstOrDefault(item => item.Id == _dragEffectLayerId);
        var target = _mediaClips.FirstOrDefault(clip => clip.Id == layer?.TargetClipId);
        if (!_pointerDragActivated || layer is null || target is null || IsClipLocked(target)) return _playhead;
        var first = target.TimelineStartSeconds;
        var last = first + target.EffectiveDurationSeconds;
        if (_dragMode == DragMode.EffectStart)
            layer.StartSeconds = SnapEdgeTime(time, EditorTimelineLayer.Vfx, TimeSpan.FromSeconds(first),
                TimeSpan.FromSeconds(_dragEffectEnd - MinClipSeconds), out guide).TotalSeconds;
        else if (_dragMode == DragMode.EffectEnd)
            layer.EndSeconds = SnapEdgeTime(time, EditorTimelineLayer.Vfx, TimeSpan.FromSeconds(_dragEffectStart + MinClipSeconds),
                TimeSpan.FromSeconds(last), out guide).TotalSeconds;
        else
        {
            var length = _dragEffectEnd - _dragEffectStart;
            var next = TimeSpan.FromSeconds(Math.Clamp(SnapTime(TimeSpan.FromSeconds(_dragEffectStart) + time - _dragStartTime).TotalSeconds,
                first, Math.Max(first, last - length)));
            guide = SnapMoveRange(EditorTimelineLayer.Vfx, TimeSpan.FromSeconds(length), ref next);
            layer.StartSeconds = Math.Clamp(next.TotalSeconds, first, Math.Max(first, last - length));
            layer.EndSeconds = layer.StartSeconds + length;
            if (Math.Abs(next.TotalSeconds - layer.StartSeconds) > .00001) guide = null;
        }
        if (_dragUndoSnapshot?.EffectLayers.FirstOrDefault(item => item.Id == layer.Id) is { } original)
            EditorEffectRegionAnimationService.ApplyRange(layer, original, layer.StartSeconds, layer.EndSeconds, move: _dragMode == DragMode.EffectMove);
        return _playhead;
    }

    private double EffectLayersTop => VfxRowTop + (ShowVfxTrack ? TrackHeight + TrackGap : 0);
    private double EffectLayersHeight => _effectLayers.Count * (TrackHeight + TrackGap);

    private void RebuildEffectLayerBlocks()
    {
        foreach (var visual in _effectLayerVisuals.Values) _clipTrimGrips.Remove(visual.Block);
        _effectLayerVisuals.Clear();
        _effectRegionKeyframeMarkers.Clear();
        EffectLayersCanvas.Children.Clear();
        for (var index = 0; index < _effectLayers.Count; index++)
        {
            var layer = _effectLayers[index];
            var row = new Border { Background = ResourceBrush("KlippSurfaceBrush"), BorderBrush = ResourceBrush("KlippBorderSubtleBrush"), BorderThickness = new Thickness(0, 0, 0, 1), IsHitTestVisible = false };
            var label = new TextBlock { Text = $"FX {index + 1}", FontSize = 11, Foreground = ResourceBrush("KlippTextMutedBrush"), IsHitTestVisible = false };
            var name = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0), IsHitTestVisible = false };
            var block = new Border { Tag = layer.Id, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 68, 59)), BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 120, 157, 136)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = name };
            AutomationProperties.SetName(block, $"{layer.Name} FX on timeline");
            block.PointerPressed += OnEffectLayerPointerPressed;
            var menu = new MenuFlyout();
            AddMenuAction(menu, "Split FX at playhead", () => { SelectEffectLayerForEditing(layer.Id); SplitSelectedEffectLayer(_playhead); });
            AddMenuAction(menu, "Delete FX", () => { SelectEffectLayerForEditing(layer.Id); DeleteSelectedEffectLayer(); });
            block.ContextFlyout = menu;
            ToolTipService.SetToolTip(block, $"{layer.Name} · Drag to move, drag edges to trim, double-click to split. Select to adjust its picture area.");
            AddTrimGrips(block);
            EffectLayersCanvas.Children.Add(row);
            EffectLayersCanvas.Children.Add(label);
            EffectLayersCanvas.Children.Add(block);
            _effectLayerVisuals[layer.Id] = (row, label, block, name);
        }
    }

    private void DrawEffectLayers(double railLeft, double railWidth)
    {
        EffectLayersCanvas.Width = TimelineCanvas.Width;
        EffectLayersCanvas.Height = TimelineContentHeight;
        for (var index = 0; index < _effectLayers.Count; index++)
        {
            var layer = _effectLayers[index];
            if (!_effectLayerVisuals.TryGetValue(layer.Id, out var visual)) continue;
            var top = EffectLayersTop + index * (TrackHeight + TrackGap);
            SetRow(visual.Row, top, TimelineCanvas.Width);
            SetLabel(visual.Label, top);
            if (visual.Name.Text != layer.Name)
            {
                visual.Name.Text = layer.Name;
                AutomationProperties.SetName(visual.Block, $"{layer.Name} FX on timeline");
                ToolTipService.SetToolTip(visual.Block, $"{layer.Name} · Drag to move, drag edges to trim, double-click to split. Select to adjust its picture area.");
            }
            var selected = layer.Id == SelectedEffectLayerId;
            visual.Row.Opacity = selected ? 1 : .7;
            visual.Label.Foreground = ResourceBrush(selected ? "KlippTextPrimaryBrush" : "KlippTextMutedBrush");
            if (!TryGetVisibleRect(TimeSpan.FromSeconds(layer.StartSeconds), TimeSpan.FromSeconds(layer.EndSeconds), railWidth, out var left, out var width))
            {
                visual.Block.Visibility = Visibility.Collapsed;
                if (_effectRegionKeyframeMarkers.TryGetValue(layer.Id, out var hidden))
                    foreach (var marker in hidden) marker.Visibility = Visibility.Collapsed;
                continue;
            }
            visual.Block.Visibility = Visibility.Visible;
            SetCanvasRect(visual.Block, railLeft + left, top + BlockInset, Math.Max(2, width), TrackHeight - 2 * BlockInset);
            visual.Block.BorderThickness = new Thickness(selected ? 2 : 1);
            visual.Block.BorderBrush = selected ? ResourceBrush("KlippTextPrimaryBrush") : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 120, 157, 136));
            visual.Block.Opacity = layer.Enabled ? 1 : .45;
            visual.Name.Visibility = width >= 44 ? Visibility.Visible : Visibility.Collapsed;
            UpdateTrimGrips(visual.Block, width, layer.StartSeconds >= VisibleStart.TotalSeconds, layer.EndSeconds <= VisibleEnd.TotalSeconds, selected);
            DrawEffectRegionKeyframes(layer, top, railLeft, railWidth, selected);
        }
    }

    private void DrawEffectRegionKeyframes(EditorProjectEffectLayer layer, double top, double railLeft, double railWidth, bool selected)
    {
        if (!_effectRegionKeyframeMarkers.TryGetValue(layer.Id, out var markers))
            _effectRegionKeyframeMarkers[layer.Id] = markers = [];
        var visible = new List<double>();
        var lastX = double.NegativeInfinity;
        if (selected)
        {
            foreach (var key in layer.RegionKeyframes)
            {
                var time = layer.StartSeconds + key.TimeSeconds;
                if (time < VisibleStart.TotalSeconds || time > VisibleEnd.TotalSeconds) continue;
                var x = railLeft + SecondsToX(time, railWidth);
                // Truncated easing curves can contain dense preservation samples.
                // Keep their markers legible rather than drawing a solid stripe.
                if (x - lastX < 8) continue;
                visible.Add(x);
                lastX = x;
                if (visible.Count >= 128) break;
            }
        }
        while (markers.Count < visible.Count)
        {
            var marker = new Border
            {
                Width = 6, Height = 6, Background = ResourceBrush("KlippAccentBrush"),
                BorderBrush = ResourceBrush("KlippTextPrimaryBrush"), BorderThickness = new Thickness(.5),
                IsHitTestVisible = false, RenderTransformOrigin = new Point(.5, .5),
                RenderTransform = new RotateTransform { Angle = 45 }
            };
            Canvas.SetZIndex(marker, 4);
            markers.Add(marker);
            EffectLayersCanvas.Children.Add(marker);
        }
        for (var index = 0; index < markers.Count; index++)
        {
            var marker = markers[index];
            marker.Visibility = index < visible.Count ? Visibility.Visible : Visibility.Collapsed;
            if (index < visible.Count)
                SetCanvasRect(marker, visible[index] - 3, top + TrackHeight - 11, 6, 6);
        }
    }

    private bool TryGetEffectLayerCursor(Point pointer, out InputSystemCursorShape cursor)
    {
        foreach (var layer in _effectLayers)
        {
            if (!_effectLayerVisuals.TryGetValue(layer.Id, out var visual) || visual.Block.Visibility != Visibility.Visible || !ContainsCanvasPoint(visual.Block, pointer)) continue;
            if (_mediaClips.FirstOrDefault(clip => clip.Id == layer.TargetClipId) is not { } target || IsClipLocked(target)) break;
            var edge = EditorTimelineInteractionMath.TrimEdgeHitWidth(visual.Block.Width, 16);
            var x = Canvas.GetLeft(visual.Block);
            cursor = layer.StartSeconds >= VisibleStart.TotalSeconds && pointer.X <= x + edge ||
                layer.EndSeconds <= VisibleEnd.TotalSeconds && pointer.X >= x + visual.Block.Width - edge
                ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeAll;
            return true;
        }
        cursor = InputSystemCursorShape.Arrow;
        return false;
    }

    private void RememberEffectTargetPositions()
    {
        _effectTargetPositions.Clear();
        foreach (var clip in _mediaClips) _effectTargetPositions[clip.Id] = (clip.TimelineStartSeconds, clip.TrimStartSeconds, clip.PlaybackSpeed);
    }

    private void SynchronizeEffectTargets()
    {
        var structureChanged = false;
        var originalLayers = _dragMode is DragMode.MediaMove or DragMode.AudioMove or DragMode.MediaTrimStart or DragMode.MediaTrimEnd or DragMode.MediaSlip
            ? _dragUndoSnapshot?.EffectLayers : null;
        // A temporary trim can hide an entire effect. Keep the original gesture
        // data available so extending that same trim restores the effect.
        if (originalLayers is not null)
        {
            foreach (var original in originalLayers.Where(original => !_effectLayers.Any(layer => layer.Id == original.Id) &&
                _mediaClips.Any(clip => clip.Id == original.TargetClipId)))
            {
                _effectLayers.Add(CloneEffectLayer(original));
                structureChanged = true;
            }
        }
        foreach (var layer in _effectLayers.ToArray())
        {
            var target = _mediaClips.FirstOrDefault(clip => clip.Id == layer.TargetClipId);
            if (target is null) { _effectLayers.Remove(layer); structureChanged = true; continue; }
            // Derive attached timing from the gesture's original data. Trimming
            // inward and back out must recover the effect's original interval.
            var original = originalLayers?.FirstOrDefault(item => item.Id == layer.Id);
            var originalTarget = original is null ? null : _dragUndoSnapshot?.MediaClips.FirstOrDefault(clip => clip.Id == target.Id);
            if (original is not null && originalTarget is not null)
            {
                var moved = Math.Abs(originalTarget.TrimStartSeconds - target.TrimStartSeconds) < .00001 &&
                    Math.Abs(originalTarget.PlaybackSpeed - target.PlaybackSpeed) < .00001;
                var delta = moved ? target.TimelineStartSeconds - originalTarget.TimelineStartSeconds : 0;
                EditorEffectRegionAnimationService.ApplyRange(layer, original,
                    original.StartSeconds + delta, original.EndSeconds + delta, move: true);
            }
            else if (_effectTargetPositions.TryGetValue(target.Id, out var previous) &&
                Math.Abs(previous.TrimStart - target.TrimStartSeconds) < .00001 && Math.Abs(previous.Speed - target.PlaybackSpeed) < .00001)
            {
                var delta = target.TimelineStartSeconds - previous.Start;
                layer.StartSeconds += delta;
                layer.EndSeconds += delta;
            }
            NormalizeEffectLayerRange(layer, target);
            if (layer.EndSeconds <= layer.StartSeconds + .0000001)
            {
                _effectLayers.Remove(layer);
                structureChanged = true;
            }
        }
        RememberEffectTargetPositions();
        if (structureChanged) RebuildEffectLayerBlocks();
        if (!_effectLayers.Any(layer => layer.Id == _selectedEffectLayerId)) _selectedEffectLayerId = null;
    }

    private void NormalizeEffectLayerRange(EditorProjectEffectLayer layer, EditorProjectClip target) =>
        layer.Normalize(target, Math.Max(_duration.TotalSeconds, target.TimelineStartSeconds + target.EffectiveDurationSeconds));

    private void SplitAttachedEffectLayers(string sourceClipId, string rightClipId, double split)
    {
        foreach (var layer in _effectLayers.Where(item => item.TargetClipId == sourceClipId).ToArray())
        {
            if (layer.EndSeconds <= split) continue;
            if (layer.StartSeconds >= split) { layer.TargetClipId = rightClipId; continue; }
            var right = CloneEffectLayer(layer);
            var original = CloneEffectLayer(layer);
            right.Id = Guid.NewGuid().ToString("N");
            right.TargetClipId = rightClipId;
            EditorEffectRegionAnimationService.ApplyRange(right, original, split, original.EndSeconds);
            EditorEffectRegionAnimationService.ApplyRange(layer, original, original.StartSeconds, split);
            _effectLayers.Insert(_effectLayers.IndexOf(layer) + 1, right);
        }
        RememberEffectTargetPositions();
        RebuildEffectLayerBlocks();
    }

    private static EditorProjectEffectLayer CloneEffectLayer(EditorProjectEffectLayer layer) => layer.Clone();

    private static bool EffectLayersEqual(IReadOnlyList<EditorProjectEffectLayer> a, IReadOnlyList<EditorProjectEffectLayer> b) =>
        a.Count == b.Count &&
        a.Select(layer => System.Text.Json.JsonSerializer.Serialize(layer))
            .SequenceEqual(b.Select(layer => System.Text.Json.JsonSerializer.Serialize(layer)), StringComparer.Ordinal);

    private static bool IsSupportedEffectFilter(string filter) => filter is "boost" or "film" or "cinematic" or "tealorange" or "warm" or "cool" or
        "soft" or "mono" or "noir" or "vintage" or "vibrant" or "dream" or "sharpen" or "blur" or "vignette" or "natural" or "matte" or "goldenhour" or "steel" or "glitch";
    private static string EffectFilterName(string filter) => filter switch
    {
        "natural" => "Natural", "film" => "Film", "matte" => "Matte", "goldenhour" => "Golden hour", "steel" => "Steel", "boost" => "Clean",
        "cinematic" => "Cinema", "tealorange" => "Teal & amber", "warm" => "Warm", "cool" => "Cool", "mono" => "Monochrome", "noir" => "Noir",
        "vintage" => "Faded film", "vibrant" => "Vivid", "soft" => "Soft contrast", "dream" => "Soft glow", "sharpen" => "Detail", "blur" => "Blur",
        "vignette" => "Vignette", "glitch" => "RGB split", _ => filter
    };
}
