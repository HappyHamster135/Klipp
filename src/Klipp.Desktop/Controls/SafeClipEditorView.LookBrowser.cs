using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Performance;
using Klipp.Desktop.Services;
using Klipp.Desktop.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private sealed record ClipLookOption(string Id, string Name, string Description, string Category);

    private static readonly ClipLookOption[] ClipLookOptions =
    [
        new("natural", "Natural", "Balanced color", "color"),
        new("film", "Film", "Soft film contrast", "color"),
        new("matte", "Matte", "Lifted blacks", "color"),
        new("goldenhour", "Golden hour", "Warm highlights", "color"),
        new("steel", "Steel", "Cool contrast", "color"),
        new("boost", "Clean", "Crisp and bright", "color"),
        new("cinematic", "Cinema", "Deep contrast", "color"),
        new("tealorange", "Teal & amber", "Split color tone", "color"),
        new("warm", "Warm", "Warmer color", "color"),
        new("cool", "Cool", "Cooler color", "color"),
        new("mono", "Monochrome", "Black and white", "color"),
        new("noir", "Noir", "Bold monochrome", "color"),
        new("vintage", "Faded film", "Muted warm color", "color"),
        new("vibrant", "Vivid", "Richer color", "color"),
        new("soft", "Soft contrast", "Gentle tones", "finish"),
        new("dream", "Soft glow", "Diffused detail", "finish"),
        new("sharpen", "Detail", "Sharper edges", "finish"),
        new("blur", "Blur", "Soften the frame", "finish"),
        new("vignette", "Vignette", "Darken the edges", "finish"),
        new("glitch", "RGB split", "Separated color", "finish")
    ];

    private readonly List<Button> _lookPresetButtons = [];
    private readonly Dictionary<string, Image> _lookThumbnailImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lookThumbnailPaths = new(StringComparer.Ordinal);
    private readonly EditorLookThumbnailService _lookThumbnailService = new();
    private CancellationTokenSource? _lookThumbnailCancellation;
    private string? _lookThumbnailSourceSignature;
    private int _lookThumbnailVersion;
    private double _lastLookContextUpdate;
    private bool _syncingQuickColorControls;

    private void OnLookSearchChanged(object sender, TextChangedEventArgs e) => RefreshLookBrowser();

    private void RefreshLookBrowser()
    {
        if (LookPresetGrid is null) return;
        var query = LookSearchBox?.Text?.Trim() ?? string.Empty;
        var matches = ClipLookOptions.Where(option =>
            (_activeLookPaletteCategory == "all" || option.Category == _activeLookPaletteCategory) &&
            (query.Length == 0 || $"{option.Name} {option.Description}".Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        LookPresetGrid.Children.Clear();
        LookPresetGrid.RowDefinitions.Clear();
        _lookPresetButtons.Clear();
        _lookThumbnailImages.Clear();
        for (var index = 0; index < matches.Count; index++)
        {
            if (index % 2 == 0) LookPresetGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var option = matches[index];
            var text = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            var thumbnail = new Image { Height = 52, Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
            if (_lookThumbnailPaths.TryGetValue(option.Id, out var path)) thumbnail.Source = new BitmapImage(new Uri(path));
            _lookThumbnailImages[option.Id] = thumbnail;
            text.Children.Add(new Border
            {
                Height = 52, Background = ResourceBrush("KlippSurfaceBrush"), Child = thumbnail,
                CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 0, 3), IsHitTestVisible = false
            });
            text.Children.Add(new TextBlock { Text = option.Name, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Text = option.Description, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["KlippTextMutedBrush"]
            });
            var button = new Button
            {
                Tag = option.Id, Content = text, Height = 96, Padding = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetName(button, option.Name);
            AutomationProperties.SetHelpText(button, $"{option.Description}. Click to add an FX timeline item, or drag onto a video clip.");
            ToolTipService.SetToolTip(button, $"{option.Description}. Add to the FX timeline, then trim its timing and choose an area.");
            button.Click += OnClipLookPaletteClicked;
            button.Loaded += OnClipLookPaletteItemLoaded;
            Grid.SetRow(button, index / 2);
            Grid.SetColumn(button, index % 2);
            LookPresetGrid.Children.Add(button);
            _lookPresetButtons.Add(button);
        }
        LookSearchEmptyText.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LookBrowserCountText.Text = $"{matches.Count} look{(matches.Count == 1 ? string.Empty : "s")} · full-strength previews";
        ToolTipService.SetToolTip(LookBrowserCountText, "Pictures preview each look at 100% using a frame from the selected video. Your manual adjustments and amount are applied separately.");
        SyncLookBrowserSelection();
    }

    private void SyncLookBrowserSelection()
    {
        if (LookSelectionHintText is null || LayerTimeline is null) return;
        var clip = LayerTimeline.GetSelectedMediaClip();
        var locked = clip is not null && _project?.GetTrack(clip.TrackId)?.Locked == true;
        foreach (var button in _lookPresetButtons)
        {
            // An unselected palette can still be dragged onto any unlocked clip.
            var selectedEffect = LayerTimeline.GetSelectedEffectLayer();
            SetOptionButtonState(button, selectedEffect?.Style is null &&
                (string?)button.Tag == (selectedEffect?.Filter ?? clip?.Filter));
        }
        UpdateLookPreviewContext(force: true);
        if (ResetAllClipEffectsButton is not null) ResetAllClipEffectsButton.IsEnabled = clip is not null && !locked;
        SyncQuickColorControls();
        EnsureLookThumbnails();
    }

    private void UpdateLookPreviewContext(bool force = false)
    {
        if (LookSelectionHintText is null || _activeTool != "vfx") return;
        if (!force && _isPreviewPlaying && PreviewNowSeconds - _lastLookContextUpdate < 0.1) return;
        _lastLookContextUpdate = PreviewNowSeconds;
        var clip = LayerTimeline.FindMediaClipById(LayerTimeline.SelectedMediaClipId);
        var position = CurrentTimelinePosition.TotalSeconds;
        var over = clip is not null && position >= clip.TimelineStartSeconds &&
            position < clip.TimelineStartSeconds + clip.EffectiveDurationSeconds;
        var active = clip is not null && _project?.IsTrackActive(clip.TrackId) == true;
        var covered = over && IsLookClipCovered(clip!, position);
        var hint = clip is null ? "Select a video clip, or drag a look onto one."
            : !active ? $"{clip.Name} · track is muted or excluded by solo"
            : _project?.GetTrack(clip.TrackId)?.Locked == true ? "Unlock this track to edit its effects."
            : !over ? $"Editing {clip.Name} · outside the playhead"
            : covered ? $"Editing {clip.Name} · covered by another clip"
            : $"Editing {clip.Name}";
        if (LookSelectionHintText.Text != hint) LookSelectionHintText.Text = hint;
        ShowSelectedEffectClipButton.Visibility = clip is not null && active && (!over || covered)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool IsLookClipCovered(EditorProjectClip clip, double position)
    {
        if (_project is null) return false;
        var winner = clip;
        foreach (var other in _project.Clips)
        {
            if (other.TrackId != clip.TrackId || position < other.TimelineStartSeconds ||
                position >= other.TimelineStartSeconds + other.EffectiveDurationSeconds) continue;
            if (other.TimelineStartSeconds > winner.TimelineStartSeconds ||
                other.TimelineStartSeconds == winner.TimelineStartSeconds && other.Order > winner.Order) winner = other;
        }
        if (winner.Id == clip.Id) return false;
        return !(_project.GetTransitionStyle(winner) != "cut" &&
            _project.GetTransitionPredecessor(winner) is { } outgoing && outgoing.Id == clip.Id &&
            position < winner.TimelineStartSeconds + _project.GetTransitionOverlapSeconds(outgoing, winner));
    }

    private void EnsureLookThumbnails()
    {
        if (_activeTool != "vfx" || _isLoadingClip || LayerTimeline.IsDragging) return;
        var clip = LayerTimeline.GetSelectedMediaClip() ?? GetActivePreviewClip();
        if (clip is null) return;
        var sourceSeconds = clip.TrimStartSeconds + Math.Min(0.5, Math.Max(0, (clip.TrimEndSeconds - clip.TrimStartSeconds) / 2));
        var signature = $"{_projectLoadVersion}|{clip.SourcePath}|{sourceSeconds.ToString("0.######", CultureInfo.InvariantCulture)}";
        if (_lookThumbnailSourceSignature == signature) return;
        CancelLookThumbnailLoading();
        _lookThumbnailSourceSignature = signature;
        _lookThumbnailPaths.Clear();
        foreach (var image in _lookThumbnailImages.Values) image.Source = null;
        _lookThumbnailCancellation = new CancellationTokenSource();
        var token = _lookThumbnailCancellation.Token;
        var version = _lookThumbnailVersion;
        var first = _lookThumbnailImages.Keys.Take(4).ToHashSet(StringComparer.Ordinal);
        _ = Task.WhenAll(ClipLookOptions.Select(option => LoadLookThumbnailAsync(
            clip.SourcePath, sourceSeconds, option.Id, first.Contains(option.Id) ? BackgroundWorkPriority.Visible : BackgroundWorkPriority.Background,
            version, token)));
    }

    private async Task LoadLookThumbnailAsync(string sourcePath, double sourceSeconds, string look,
        BackgroundWorkPriority priority, int version, CancellationToken token)
    {
        try
        {
            var path = await _lookThumbnailService.GetOrCreateAsync(sourcePath, sourceSeconds, look, priority, token).ConfigureAwait(false);
            if (path is null || token.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (token.IsCancellationRequested || version != _lookThumbnailVersion) return;
                _lookThumbnailPaths[look] = path;
                if (_lookThumbnailImages.TryGetValue(look, out var image)) image.Source = new BitmapImage(new Uri(path));
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Look thumbnail unavailable: {exception.Message}"); }
    }

    private void CancelLookThumbnailLoading()
    {
        _lookThumbnailCancellation?.Cancel();
        _lookThumbnailCancellation?.Dispose();
        _lookThumbnailCancellation = null;
        _lookThumbnailSourceSignature = null;
        _lookThumbnailVersion++;
    }

    private void SyncQuickColorControls()
    {
        if (QuickBrightnessSlider is null || LayerTimeline is null) return;
        var clip = LayerTimeline.GetSelectedMediaClip();
        var enabled = clip is not null && _project?.GetTrack(clip.TrackId)?.Locked != true;
        _syncingQuickColorControls = true;
        try
        {
            QuickBrightnessSlider.IsEnabled = QuickContrastSlider.IsEnabled =
                QuickSaturationSlider.IsEnabled = QuickTemperatureSlider.IsEnabled = enabled;
            QuickBrightnessSlider.Value = (clip?.Brightness ?? 0) * 100;
            QuickContrastSlider.Value = (clip?.Contrast ?? 1) * 100;
            QuickSaturationSlider.Value = (clip?.Saturation ?? 1) * 100;
            QuickTemperatureSlider.Value = (clip?.Temperature ?? 0) * 100;
            QuickBrightnessValueText.Text = $"{Math.Round((clip?.Brightness ?? 0) * 100):0}";
            QuickContrastValueText.Text = $"{Math.Round((clip?.Contrast ?? 1) * 100):0}%";
            QuickSaturationValueText.Text = $"{Math.Round((clip?.Saturation ?? 1) * 100):0}%";
            QuickTemperatureValueText.Text = $"{Math.Round((clip?.Temperature ?? 0) * 100):0}";
        }
        finally { _syncingQuickColorControls = false; }
    }

    private void OnQuickColorAdjustmentChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingQuickColorControls || _syncingSelectedClipVisualControls || _isLoadingClip || _suppressProjectChanges ||
            QuickTemperatureSlider is null || ClipTemperatureSlider is null || LayerTimeline is null) return;
        var target = ReferenceEquals(sender, QuickBrightnessSlider) ? ClipBrightnessSlider
            : ReferenceEquals(sender, QuickContrastSlider) ? ClipContrastSlider
            : ReferenceEquals(sender, QuickSaturationSlider) ? ClipSaturationSlider : ClipTemperatureSlider;
        target.Value = e.NewValue;
    }

    private void OnOpenAdvancedColorClicked(object sender, RoutedEventArgs e)
    {
        _generalInspectorSection = "clip";
        SetEditorTool("general");
        ClipColorEffectsExpander.IsExpanded = true;
        DispatcherQueue.TryEnqueue(() => ClipColorEffectsExpander.StartBringIntoView());
    }

    private void OnShowSelectedEffectClipClicked(object sender, RoutedEventArgs e)
    {
        var clip = LayerTimeline.GetSelectedMediaClip();
        if (clip is null) return;
        var start = Math.Max(LayerTimeline.Start.TotalSeconds, clip.TimelineStartSeconds);
        var end = Math.Min(LayerTimeline.End.TotalSeconds, clip.TimelineStartSeconds + clip.EffectiveDurationSeconds);
        if (_project?.IsTrackActive(clip.TrackId) != true)
        {
            ProjectStatusText.Text = "Enable this video track or adjust solo to preview its effects.";
            return;
        }
        if (end <= start)
        {
            ProjectStatusText.Text = "This clip is outside the playback range. Expand the range to preview it.";
            return;
        }
        var edges = _project.Clips.Where(item => item.TrackId == clip.TrackId)
            .SelectMany(item => new[] { item.TimelineStartSeconds, item.TimelineStartSeconds + item.EffectiveDurationSeconds })
            .Append(start).Append(end).Where(time => time >= start && time <= end).Distinct().Order().ToArray();
        for (var index = 0; index + 1 < edges.Length; index++)
        {
            var time = edges[index] + Math.Min(0.25, (edges[index + 1] - edges[index]) / 2);
            if (IsLookClipCovered(clip, time)) continue;
            PausePreview();
            SeekTo(TimeSpan.FromSeconds(time));
            UpdateLookPreviewContext(force: true);
            return;
        }
        ProjectStatusText.Text = "Another clip covers this selection throughout the playback range.";
    }
}
