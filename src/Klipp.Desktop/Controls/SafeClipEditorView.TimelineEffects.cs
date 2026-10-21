using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Globalization.NumberFormatting;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private static string? ResolveHistoryEffectSelection(EditorProjectEffectLayer? selected,
        System.Collections.Generic.IReadOnlyList<EditorProjectEffectLayer> restored, string? restoredTargetId)
    {
        if (selected is null) return null;
        if (restored.Any(item => item.Id == selected.Id)) return selected.Id;
        return restored.Where(item => item.TargetClipId == restoredTargetId && item.Filter == selected.Filter &&
                JsonSerializer.Serialize(item.Style) == JsonSerializer.Serialize(selected.Style) &&
                item.RegionShape == selected.RegionShape && Math.Abs(item.RegionX - selected.RegionX) < .0001 &&
                Math.Abs(item.RegionY - selected.RegionY) < .0001 && Math.Abs(item.RegionWidth - selected.RegionWidth) < .0001 &&
                Math.Abs(item.RegionHeight - selected.RegionHeight) < .0001 &&
                item.StartSeconds <= selected.StartSeconds + .001 && item.EndSeconds >= selected.EndSeconds - .001)
            .OrderBy(item => item.EndSeconds - item.StartSeconds).Select(item => item.Id).FirstOrDefault();
    }

    private bool _syncingTimelineFx;
    private bool _timelineFxAmountGesture;
    private CancellationTokenSource? _fxAreaFrameCts;
    private string? _fxAreaFrameSignature;
    private string? _fxAreaDragId;
    private Point? _fxAreaDrawStart;
    private Rect? _fxAreaDrawRect;

    private void ConfigureTimelineFxControls()
    {
        var seconds = new DecimalFormatter(["en-US"], "US")
        {
            IntegerDigits = 1, FractionDigits = 3, IsGrouped = false,
            NumberRounder = new IncrementNumberRounder { Increment = .001 }
        };
        TimelineFxStartBox.NumberFormatter = TimelineFxEndBox.NumberFormatter = seconds;
        var percent = new DecimalFormatter(["en-US"], "US")
        {
            IntegerDigits = 1, FractionDigits = 1, IsGrouped = false,
            NumberRounder = new IncrementNumberRounder { Increment = .1 }
        };
        foreach (var box in new[] { TimelineFxXBox, TimelineFxYBox, TimelineFxWidthBox, TimelineFxHeightBox })
            box.NumberFormatter = percent;
        TimelineFxFeatherSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnTimelineFxAmountPressed), true);
        TimelineFxFeatherSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnTimelineFxAmountReleased), true);
        TimelineFxFeatherSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnTimelineFxAmountReleased), true);
    }

    private EditorProjectClip? GetTimelineFxTarget() =>
        LayerTimeline.GetSelectedEffectLayer() is { } effect
            ? LayerTimeline.FindMediaClipById(effect.TargetClipId)
            : LayerTimeline.GetSelectedMediaClip() ?? GetActivePreviewClip();

    private void AddTimelineEffect(string filter, EditorProjectClip? style = null, string? name = null)
    {
        if (_project is null || _isLoadingClip || _suppressProjectChanges || filter == "none") return;
        var clip = GetTimelineFxTarget();
        if (clip is null)
        {
            ProjectStatusText.Text = "Select a video clip to add an FX item.";
            return;
        }
        if (_project.GetTrack(clip.TrackId)?.Locked == true)
        {
            ProjectStatusText.Text = "Unlock the video track before adding FX.";
            return;
        }
        CommitPendingProjectChange();
        PausePreview();
        ResetOriginalComparison();
        var id = LayerTimeline.AddEffectLayerToClip(clip.Id, filter);
        if (id is null) return;
        if (style is not null)
        {
            var effects = LayerTimeline.GetEffectLayers().ToList();
            var effect = effects.First(item => item.Id == id);
            effect.Style = new EditorProjectClip();
            EditorClipAttributesService.CopyLook(style, effect.Style);
            effect.Amount = Math.Clamp(style.FilterStrength, 0, 1);
            effect.Style.FilterStrength = 1;
            effect.Name = name ?? "Custom look";
            LayerTimeline.SetEffectLayers(effects, id);
        }
        SyncTimelineEffectsToProject();
        UpdateVfxPreview();
        SetEditorTool("vfx");
        UpdatePresetPickers();
        QueueProjectChange();
        CommitPendingProjectChange();
        RevealSelectedTimelineEffect();
        var effectName = name ?? ClipLookOptions.FirstOrDefault(option => option.Id == filter)?.Name ?? filter;
        ProjectStatusText.Text = $"{effectName} added to the FX timeline. Trim its edges or choose an affected area.";
    }

    private void SyncTimelineEffectsToProject()
    {
        if (_project is not null)
            _project.EffectLayers = LayerTimeline.GetEffectLayers().ToList();
    }

    private void RevealSelectedTimelineEffect()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (LayerTimeline.SelectedEffectLayerBounds is { } bounds)
            {
                var top = TimelineScrollViewer.VerticalOffset;
                var height = TimelineScrollViewer.ViewportHeight;
                if (bounds.Y < top)
                    TimelineScrollViewer.ChangeView(null, Math.Max(0, bounds.Y - 12), null, disableAnimation: true);
                else if (bounds.Bottom > top + height)
                    TimelineScrollViewer.ChangeView(null, Math.Max(0, bounds.Bottom - height + 12), null, disableAnimation: true);
            }
            InspectorScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
        });
    }

    private void SyncTimelineFxInspector()
    {
        if (TimelineFxInspector is null || LayerTimeline is null) return;
        var effect = LayerTimeline.GetSelectedEffectLayer();
        var clip = effect is null ? null : LayerTimeline.FindMediaClipById(effect.TargetClipId);
        var editable = clip is not null && _project?.GetTrack(clip.TrackId)?.Locked != true;
        TimelineFxInspector.Visibility = effect is null ? Visibility.Collapsed : Visibility.Visible;
        ClipEffectsControls.Visibility = effect is null ? Visibility.Visible : Visibility.Collapsed;
        _syncingTimelineFx = true;
        try
        {
            foreach (var control in new Control[] { TimelineFxEnabledCheckBox, TimelineFxAmountSlider,
                TimelineFxStartBox, TimelineFxEndBox, TimelineFxShapeBox, TimelineFxAreaPresetBox, TimelineFxFeatherSlider,
                TimelineFxXBox, TimelineFxYBox, TimelineFxWidthBox, TimelineFxHeightBox, TimelineFxRemoveButton })
                control.IsEnabled = editable;
            TimelineFxPreviewButton.IsEnabled = clip is not null && effect is not null;
            TimelineFxAreaCanvas.IsHitTestVisible = editable;
            TimelineFxNameText.Text = effect?.Name ?? "Timeline FX";
            TimelineFxTargetText.Text = clip?.Name ?? string.Empty;
            TimelineFxEnabledCheckBox.IsChecked = effect?.Enabled == true;
            TimelineFxAmountSlider.Value = (effect?.Amount ?? 1) * 100;
            TimelineFxAmountText.Text = $"{Math.Round((effect?.Amount ?? 1) * 100):0}%";
            TimelineFxFeatherSlider.Value = (effect?.RegionFeather ?? 0) * 100;
            TimelineFxFeatherText.Text = $"{Math.Round((effect?.RegionFeather ?? 0) * 100):0}%";
            TimelineFxStartBox.Minimum = clip?.TimelineStartSeconds ?? 0;
            TimelineFxStartBox.Maximum = clip is null ? 1 : clip.TimelineStartSeconds + clip.EffectiveDurationSeconds;
            TimelineFxEndBox.Minimum = TimelineFxStartBox.Minimum;
            TimelineFxEndBox.Maximum = TimelineFxStartBox.Maximum;
            TimelineFxStartBox.Value = effect?.StartSeconds ?? 0;
            TimelineFxEndBox.Value = effect?.EndSeconds ?? 1;
            TimelineFxShapeBox.SelectedIndex = effect?.RegionShape == "ellipse" ? 1 : 0;
            TimelineFxAreaPresetBox.SelectedItem = TimelineFxAreaPresetBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => (string?)item.Tag == GetTimelineFxAreaPreset(effect));
            TimelineFxXBox.Value = (effect?.RegionX ?? 0) * 100;
            TimelineFxYBox.Value = (effect?.RegionY ?? 0) * 100;
            TimelineFxWidthBox.Value = (effect?.RegionWidth ?? 1) * 100;
            TimelineFxHeightBox.Value = (effect?.RegionHeight ?? 1) * 100;
        }
        finally { _syncingTimelineFx = false; }
        SyncTimelineFxMotionControls(effect);
        DrawTimelineFxArea(effect);
        if (effect is not null && clip is not null && _activeTool == "vfx")
            _ = LoadTimelineFxAreaFrameAsync(effect, clip);
        UpdateTimelineFxPreviewAdorner();
    }

    private string GetTimelineFxAreaPreset(EditorProjectEffectLayer? effect)
    {
        if (effect is null) return "full";
        var region = effect.EvaluateRegion(TimelineFxEditTime(effect));
        bool Is(double x, double y, double w, double h) =>
            Math.Abs(region.X - x) < .001 && Math.Abs(region.Y - y) < .001 &&
            Math.Abs(region.Width - w) < .001 && Math.Abs(region.Height - h) < .001;
        return Is(0, 0, 1, 1) ? "full" : Is(.25, .25, .5, .5) ? "center"
            : Is(0, 0, .5, 1) ? "left" : Is(.5, 0, .5, 1) ? "right"
            : Is(0, 0, 1, .5) ? "top" : Is(0, .5, 1, .5) ? "bottom" : "custom";
    }

    private void ApplyTimelineFxEdit(Action<EditorProjectEffectLayer> mutate, bool commit = true, bool syncControls = true)
    {
        if (_syncingTimelineFx || _isLoadingClip || _suppressProjectChanges || _project is null) return;
        var selected = LayerTimeline.GetSelectedEffectLayer();
        var clip = selected is null ? null : LayerTimeline.FindMediaClipById(selected.TargetClipId);
        if (selected is null || clip is null || _project.GetTrack(clip.TrackId)?.Locked == true) return;
        if (commit) CommitPendingProjectChange();
        var effects = LayerTimeline.GetEffectLayers().ToList();
        var effect = effects.First(item => item.Id == selected.Id);
        var before = JsonSerializer.Serialize(effect);
        mutate(effect);
        effect.Normalize(clip, LayerTimeline.Duration.TotalSeconds);
        if (before == JsonSerializer.Serialize(effect)) return;
        if (_fxPreviewDragId is null && !_timelineFxAmountGesture) PausePreview();
        LayerTimeline.SetEffectLayers(effects, effect.Id);
        SyncTimelineEffectsToProject();
        UpdateVfxPreview();
        if (syncControls) SyncTimelineFxInspector();
        QueueProjectChange();
        if (_timelineFxAmountGesture) _projectChangeTimer.Stop();
        if (commit) CommitPendingProjectChange();
    }

    private void OnTimelineFxEnabledChanged(object sender, RoutedEventArgs e) =>
        ApplyTimelineFxEdit(effect => effect.Enabled = TimelineFxEnabledCheckBox.IsChecked == true);

    private void OnShowTimelineFxPreviewClicked(object sender, RoutedEventArgs e)
    {
        var effect = LayerTimeline.GetSelectedEffectLayer();
        if (effect is null) return;
        PausePreview();
        var time = Math.Clamp(CurrentTimelinePosition.TotalSeconds, effect.StartSeconds,
            Math.Max(effect.StartSeconds, effect.EndSeconds - 1d / Math.Max(1, _exportFrameRate)));
        SeekTo(TimeSpan.FromSeconds(time));
        UpdateTimelineFxPreviewAdorner();
    }

    private void OnTimelineFxAmountChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        ApplyTimelineFxEdit(effect => effect.Amount = e.NewValue / 100, commit: !_timelineFxAmountGesture);

    private void OnTimelineFxAmountPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(TimelineFxAmountSlider).Properties.IsLeftButtonPressed) return;
        CommitPendingProjectChange();
        PausePreview();
        _projectChangeTimer.Stop();
        _timelineFxAmountGesture = true;
    }

    private void OnTimelineFxAmountReleased(object sender, PointerRoutedEventArgs e)
    {
        _timelineFxAmountGesture = false;
        CommitPendingProjectChange();
        QueueProjectSaveOnly();
    }

    private void OnTimelineFxTimeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsFinite(args.NewValue)) return;
        ApplyTimelineFxEdit(effect =>
        {
            var time = EditorTimelineMath.SnapToFrame(args.NewValue, _exportFrameRate);
            var start = ReferenceEquals(sender, TimelineFxStartBox) ? Math.Min(time, effect.EndSeconds - .1) : effect.StartSeconds;
            var end = ReferenceEquals(sender, TimelineFxEndBox) ? Math.Max(time, effect.StartSeconds + .1) : effect.EndSeconds;
            EditorEffectRegionAnimationService.ApplyRange(effect, effect.Clone(), start, end, move: false);
        });
    }

    private void OnTimelineFxAreaPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TimelineFxAreaPresetBox.SelectedItem is not ComboBoxItem { Tag: string preset } || preset == "custom") return;
        ApplyTimelineFxRegionEdit(_ => preset switch
            {
                "center" => new(.25, .25, .5, .5), "left" => new(0, 0, .5, 1), "right" => new(.5, 0, .5, 1),
                "top" => new(0, 0, 1, .5), "bottom" => new(0, .5, 1, .5), _ => new(0, 0, 1, 1)
            });
    }

    private void OnTimelineFxShapeChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyTimelineFxEdit(effect => effect.RegionShape = TimelineFxShapeBox.SelectedIndex == 1 ? "ellipse" : "rectangle");

    private void OnTimelineFxAreaNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsFinite(args.NewValue)) return;
        ApplyTimelineFxRegionEdit(region =>
        {
            var value = args.NewValue / 100;
            if (ReferenceEquals(sender, TimelineFxXBox)) return region with { X = Math.Min(value, 1 - region.Width) };
            if (ReferenceEquals(sender, TimelineFxYBox)) return region with { Y = Math.Min(value, 1 - region.Height) };
            if (ReferenceEquals(sender, TimelineFxWidthBox)) return region with { Width = Math.Min(value, 1 - region.X) };
            return region with { Height = Math.Min(value, 1 - region.Y) };
        });
    }

    private void OnRemoveTimelineFxClicked(object sender, RoutedEventArgs e)
    {
        CommitPendingProjectChange();
        if (LayerTimeline.DeleteSelectedEffectLayer())
        {
            SyncTimelineEffectsToProject();
            QueueProjectChange();
            CommitPendingProjectChange();
            SyncTimelineFxInspector();
        }
    }

    private async Task LoadTimelineFxAreaFrameAsync(EditorProjectEffectLayer effect, EditorProjectClip clip)
    {
        var localSeconds = Math.Clamp(LayerTimeline.GetMediaClipLocalTime(clip.Id, CurrentTimelinePosition),
            0, Math.Max(0, clip.EffectiveDurationSeconds - .05));
        var sourceSeconds = Path.GetExtension(clip.SourcePath).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp"
            ? 0 : clip.TrimStartSeconds + localSeconds * clip.PlaybackSpeed;
        var signature = $"{_projectLoadVersion}|{clip.Id}|{clip.SourcePath}|{sourceSeconds:R}";
        if (_fxAreaFrameSignature == signature) return;
        _fxAreaFrameSignature = signature;
        _fxAreaFrameCts?.Cancel();
        var cancellation = new CancellationTokenSource();
        _fxAreaFrameCts = cancellation;
        TimelineFxSourceImage.Source = null;
        try
        {
            if (!new FFmpegLocator().IsInstalled) return;
            var frame = await _motionTrackingService.GetReferenceFrameAsync(clip.SourcePath, sourceSeconds, cancellation.Token);
            if (cancellation.IsCancellationRequested || _fxAreaFrameSignature != signature) return;
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(frame.Png))
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            if (cancellation.IsCancellationRequested || _fxAreaFrameSignature != signature) return;
            var fit = Math.Min(276d / frame.Width, 172d / frame.Height);
            TimelineFxAreaCanvas.Width = TimelineFxSourceImage.Width = frame.Width * fit;
            TimelineFxAreaCanvas.Height = TimelineFxSourceImage.Height = frame.Height * fit;
            TimelineFxSourceImage.Source = bitmap;
            DrawTimelineFxArea(LayerTimeline.GetSelectedEffectLayer());
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"FX area frame: {exception.Message}"); }
        finally
        {
            if (ReferenceEquals(_fxAreaFrameCts, cancellation)) _fxAreaFrameCts = null;
            cancellation.Dispose();
        }
    }

    private void OnRefreshTimelineFxFrameClicked(object sender, RoutedEventArgs e)
    {
        var effect = LayerTimeline.GetSelectedEffectLayer();
        var clip = effect is null ? null : LayerTimeline.FindMediaClipById(effect.TargetClipId);
        if (effect is null || clip is null) return;
        _fxAreaFrameSignature = null;
        _ = LoadTimelineFxAreaFrameAsync(effect, clip);
    }

    private void DrawTimelineFxArea(EditorProjectEffectLayer? effect)
    {
        if (TimelineFxAreaRectangle is null || effect is null) return;
        var region = effect.EvaluateRegion(TimelineFxEditTime(effect));
        var rect = _fxAreaDrawRect ?? new Rect(region.X, region.Y, region.Width, region.Height);
        var shape = effect.RegionShape == "ellipse" ? (FrameworkElement)TimelineFxAreaEllipse : TimelineFxAreaRectangle;
        TimelineFxAreaRectangle.Visibility = effect.RegionShape == "ellipse" ? Visibility.Collapsed : Visibility.Visible;
        TimelineFxAreaEllipse.Visibility = effect.RegionShape == "ellipse" ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(shape, rect.X * TimelineFxAreaCanvas.Width);
        Canvas.SetTop(shape, rect.Y * TimelineFxAreaCanvas.Height);
        shape.Width = rect.Width * TimelineFxAreaCanvas.Width;
        shape.Height = rect.Height * TimelineFxAreaCanvas.Height;
    }

    private Point TimelineFxAreaPoint(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(TimelineFxAreaCanvas).Position;
        return new(Math.Clamp(point.X / TimelineFxAreaCanvas.Width, 0, 1), Math.Clamp(point.Y / TimelineFxAreaCanvas.Height, 0, 1));
    }

    private void OnTimelineFxAreaPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(TimelineFxAreaCanvas).Properties.IsLeftButtonPressed ||
            !TimelineFxAreaCanvas.IsHitTestVisible || LayerTimeline.GetSelectedEffectLayer() is not { } effect) return;
        _fxAreaDragId = effect.Id;
        _fxAreaDrawStart = TimelineFxAreaPoint(e);
        TimelineFxAreaCanvas.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnTimelineFxAreaMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_fxAreaDrawStart is not { } start) return;
        var point = TimelineFxAreaPoint(e);
        _fxAreaDrawRect = new Rect(Math.Min(start.X, point.X), Math.Min(start.Y, point.Y),
            Math.Abs(start.X - point.X), Math.Abs(start.Y - point.Y));
        DrawTimelineFxArea(LayerTimeline.GetSelectedEffectLayer());
        e.Handled = true;
    }

    private void OnTimelineFxAreaReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_fxAreaDrawStart is not { } start) return;
        var point = TimelineFxAreaPoint(e);
        var id = _fxAreaDragId;
        _fxAreaDrawStart = null;
        _fxAreaDrawRect = null;
        _fxAreaDragId = null;
        TimelineFxAreaCanvas.ReleasePointerCapture(e.Pointer);
        if (LayerTimeline.SelectedEffectLayerId == id &&
            Math.Abs(point.X - start.X) * TimelineFxAreaCanvas.Width >= 4 &&
            Math.Abs(point.Y - start.Y) * TimelineFxAreaCanvas.Height >= 4)
            ApplyTimelineFxRegionEdit(_ => new(Math.Min(start.X, point.X), Math.Min(start.Y, point.Y),
                Math.Abs(start.X - point.X), Math.Abs(start.Y - point.Y)));
        DrawTimelineFxArea(LayerTimeline.GetSelectedEffectLayer());
        e.Handled = true;
    }

    private void OnTimelineFxAreaCanceled(object sender, PointerRoutedEventArgs e)
    {
        _fxAreaDrawStart = null;
        _fxAreaDrawRect = null;
        _fxAreaDragId = null;
        DrawTimelineFxArea(LayerTimeline.GetSelectedEffectLayer());
    }

    private void OnTimelineFxInspectorUnloaded(object sender, RoutedEventArgs e)
    {
        _fxAreaFrameCts?.Cancel();
        _fxAreaFrameSignature = null;
        _fxAreaDrawStart = null;
        _fxAreaDrawRect = null;
        _fxAreaDragId = null;
        _timelineFxAmountGesture = false;
    }
}
