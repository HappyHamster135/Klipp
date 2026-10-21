using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Windows.UI;

namespace Klipp.Desktop.Controls;

public sealed partial class ClipEditorView : UserControl
{
    private readonly DispatcherTimer _positionTimer;
    private readonly ThumbnailService _thumbnailService = new();
    private string? _currentFilePath;
    private TimeSpan _duration = TimeSpan.FromSeconds(1);
    private TimeSpan _previewPosition = TimeSpan.Zero;
    private double _playbackSpeed = 1.0;
    private string _activeTool = "general";
    private string _textStyle = "plain";
    private string _textPosition = "bottom";
    private string _videoFilter = "none";
    private string _elementOverlay = "none";
    private double _fadeInSeconds;
    private double _fadeOutSeconds;
    private double _freezeFrameSeconds;
    private bool _isLoadingClip;
    private bool _isPreviewPlaying;
    private long _lastPreviewTick;
    private int _thumbnailLoadVersion;

    public event EventHandler? BackRequested;
    public event EventHandler<ClipEditorExportEventArgs>? ExportRequested;

    public ClipEditorView()
    {
        InitializeComponent();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _positionTimer.Tick += OnPositionTimerTick;

        Unloaded += (_, _) => Stop();

        SetEditorTool("general");
        UpdateAllOptionButtons();
        UpdatePreviewOverlays();
    }

    public void LoadClip(string filePath, TimeSpan duration)
    {
        if (!File.Exists(filePath))
            return;

        _isLoadingClip = true;

        try
        {
            _currentFilePath = filePath;
            _duration = duration > TimeSpan.Zero ? duration : TimeSpan.FromSeconds(1);

            var file = new FileInfo(filePath);
            EditorTitleText.Text = file.Name.Replace(".mp4", "", StringComparison.OrdinalIgnoreCase)
                                       .Replace("klipp_", "", StringComparison.OrdinalIgnoreCase);

            LayerTimeline.Duration = _duration;
            LayerTimeline.Start = TimeSpan.Zero;
            LayerTimeline.End = _duration;
            LayerTimeline.Playhead = TimeSpan.Zero;
            _previewPosition = TimeSpan.Zero;
            ResetEditorAdjustments();

            PausePreview();
            SeekTo(TimeSpan.Zero);
            UpdateTrimUi();
            UpdatePlaybackUi();
            QueueThumbnailLoad(filePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Klipp] Editor kunde inte ladda klipp: {ex.Message}");
        }
        finally
        {
            _isLoadingClip = false;
        }
    }

    public void Stop()
    {
        _thumbnailLoadVersion++;
        PausePreview();

        try
        {
            PausePreview();
            if (EditorPreviewImage is not null)
                EditorPreviewImage.Source = null;
        }
        catch
        {
            // MediaPlayer kan redan vara stängd när vyn unloadas.
        }

        _currentFilePath = null;
    }

    private void QueueThumbnailLoad(string filePath)
    {
        var version = ++_thumbnailLoadVersion;
        EditorPreviewImage.Source = null;
        EditorPreviewImage.Opacity = 0;

        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(60);

            if (version != _thumbnailLoadVersion ||
                !string.Equals(_currentFilePath, filePath, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(filePath))
            {
                return;
            }

            try
            {
                var thumbnailPath = await _thumbnailService.GetOrCreateThumbnailAsync(filePath);
                if (version != _thumbnailLoadVersion ||
                    string.IsNullOrWhiteSpace(thumbnailPath) ||
                    !File.Exists(thumbnailPath))
                {
                    return;
                }

                EditorPreviewImage.Source = new BitmapImage(new Uri(thumbnailPath));
                EditorPreviewImage.Opacity = 1;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Klipp] Editor kunde inte ladda thumbnail-preview: {ex.Message}");
            }
        });
    }

    private void OnPositionTimerTick(object? sender, object e)
    {
        if (!_isPreviewPlaying)
            return;

        var now = Stopwatch.GetTimestamp();
        if (_lastPreviewTick == 0)
        {
            _lastPreviewTick = now;
            return;
        }

        var elapsed = (now - _lastPreviewTick) / (double)Stopwatch.Frequency;
        _lastPreviewTick = now;
        _previewPosition += TimeSpan.FromSeconds(elapsed * _playbackSpeed);

        UpdatePlaybackUi();
    }

    private void PausePreview()
    {
        _isPreviewPlaying = false;
        _lastPreviewTick = 0;
        _positionTimer.Stop();
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        Stop();
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnExportClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFilePath))
            return;

        ExportRequested?.Invoke(
            this,
            new ClipEditorExportEventArgs(
                _currentFilePath,
                LayerTimeline.Start,
                LayerTimeline.End,
                _playbackSpeed,
                ExportAudioVolume,
                MuteAudioToggle.IsOn,
                LayerTimeline.VfxEnabled ? _videoFilter : "none",
                TextOverlayBox.Text.Trim(),
                _textStyle,
                _textPosition,
                LayerTimeline.ElementEnabled ? _elementOverlay : "none",
                _fadeInSeconds,
                _fadeOutSeconds,
                _freezeFrameSeconds,
                SubtitleTextBox.Text.Trim(),
                LayerTimeline.TextEnabled,
                LayerTimeline.TextStart,
                LayerTimeline.TextEnd,
                LayerTimeline.VfxEnabled,
                LayerTimeline.VfxStart,
                LayerTimeline.VfxEnd,
                LayerTimeline.SubtitleEnabled,
                LayerTimeline.SubtitleStart,
                LayerTimeline.SubtitleEnd,
                LayerTimeline.ElementEnabled,
                LayerTimeline.ElementStart,
                LayerTimeline.ElementEnd));
    }

    private void OnPreviewTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        TogglePlayPause();
        e.Handled = true;
    }

    private void OnPlayPauseClicked(object sender, RoutedEventArgs e)
    {
        TogglePlayPause();
    }

    private void TogglePlayPause()
    {
        if (string.IsNullOrEmpty(_currentFilePath))
            return;

        if (IsPlaying())
        {
            PausePreview();
        }
        else
        {
            if (_previewPosition >= LayerTimeline.End - TimeSpan.FromMilliseconds(50))
                _previewPosition = LayerTimeline.Start;

            _isPreviewPlaying = true;
            _lastPreviewTick = 0;
            _positionTimer.Start();
        }

        UpdatePlaybackUi();
    }

    private void OnLayerTimelineChanged(object sender, EditorTimelineChangedEventArgs e)
    {
        if (_isLoadingClip)
        {
            UpdateTrimUi();
            return;
        }

        PausePreview();
        SeekTo(e.PreviewTime);
        UpdateTrimUi();
        UpdatePreviewOverlays();
    }

    private void OnSeekStartClicked(object sender, RoutedEventArgs e)
    {
        PausePreview();
        SeekTo(LayerTimeline.Start);
    }

    private void OnSeekEndClicked(object sender, RoutedEventArgs e)
    {
        PausePreview();
        SeekTo(LayerTimeline.End);
    }

    private void OnResetTrimClicked(object sender, RoutedEventArgs e)
    {
        PausePreview();
        LayerTimeline.Start = TimeSpan.Zero;
        LayerTimeline.End = _duration;
        SeekTo(TimeSpan.Zero);
        UpdateTrimUi();
    }

    private void OnGeneralToolClicked(object sender, RoutedEventArgs e) => SetEditorTool("general");
    private void OnTextToolClicked(object sender, RoutedEventArgs e) => SetEditorTool("text");
    private void OnElementsToolClicked(object sender, RoutedEventArgs e) => SetEditorTool("elements");
    private void OnVfxToolClicked(object sender, RoutedEventArgs e) => SetEditorTool("vfx");
    private void OnTransitionsToolClicked(object sender, RoutedEventArgs e) => SetEditorTool("transitions");
    private void OnAudioToolClicked(object sender, RoutedEventArgs e) => SetEditorTool("audio");
    private void OnSubtitlesToolClicked(object sender, RoutedEventArgs e) => SetEditorTool("subtitles");

    private void OnSpeed05Clicked(object sender, RoutedEventArgs e) => SetPlaybackSpeed(0.5);
    private void OnSpeed1Clicked(object sender, RoutedEventArgs e) => SetPlaybackSpeed(1.0);
    private void OnSpeed15Clicked(object sender, RoutedEventArgs e) => SetPlaybackSpeed(1.5);
    private void OnSpeed2Clicked(object sender, RoutedEventArgs e) => SetPlaybackSpeed(2.0);

    private void OnTextOverlayChanged(object sender, TextChangedEventArgs e)
    {
        LayerTimeline.SetLayerEnabled(
            EditorTimelineLayer.Text,
            !string.IsNullOrWhiteSpace(TextOverlayBox.Text),
            CurrentTimelinePosition,
            GetLayerLabel(TextOverlayBox.Text, _textStyle));
        UpdateTextOverlayPreview();
    }
    private void OnTextPlainClicked(object sender, RoutedEventArgs e) => SetTextStyle("plain");
    private void OnTextBoldClicked(object sender, RoutedEventArgs e) => SetTextStyle("bold");
    private void OnTextMemeClicked(object sender, RoutedEventArgs e) => SetTextStyle("meme");
    private void OnTextHighlightClicked(object sender, RoutedEventArgs e) => SetTextStyle("highlight");
    private void OnTextPixelClicked(object sender, RoutedEventArgs e) => SetTextStyle("pixel");
    private void OnTextPositionTopClicked(object sender, RoutedEventArgs e) => SetTextPosition("top");
    private void OnTextPositionCenterClicked(object sender, RoutedEventArgs e) => SetTextPosition("center");
    private void OnTextPositionBottomClicked(object sender, RoutedEventArgs e) => SetTextPosition("bottom");

    private void OnElementNoneClicked(object sender, RoutedEventArgs e) => SetElementOverlay("none");
    private void OnElementFocusClicked(object sender, RoutedEventArgs e) => SetElementOverlay("focus");
    private void OnElementLetterboxClicked(object sender, RoutedEventArgs e) => SetElementOverlay("letterbox");
    private void OnElementCenterClicked(object sender, RoutedEventArgs e) => SetElementOverlay("center");

    private void OnFilterNoneClicked(object sender, RoutedEventArgs e) => SetVideoFilter("none");
    private void OnFilterBoostClicked(object sender, RoutedEventArgs e) => SetVideoFilter("boost");
    private void OnFilterWarmClicked(object sender, RoutedEventArgs e) => SetVideoFilter("warm");
    private void OnFilterCoolClicked(object sender, RoutedEventArgs e) => SetVideoFilter("cool");
    private void OnFilterMonoClicked(object sender, RoutedEventArgs e) => SetVideoFilter("mono");
    private void OnFilterCinematicClicked(object sender, RoutedEventArgs e) => SetVideoFilter("cinematic");
    private void OnFilterSharpenClicked(object sender, RoutedEventArgs e) => SetVideoFilter("sharpen");
    private void OnFilterBlurClicked(object sender, RoutedEventArgs e) => SetVideoFilter("blur");

    private void OnFadeInNoneClicked(object sender, RoutedEventArgs e) => SetFadeIn(0);
    private void OnFadeIn05Clicked(object sender, RoutedEventArgs e) => SetFadeIn(0.5);
    private void OnFadeIn1Clicked(object sender, RoutedEventArgs e) => SetFadeIn(1.0);
    private void OnFadeOutNoneClicked(object sender, RoutedEventArgs e) => SetFadeOut(0);
    private void OnFadeOut05Clicked(object sender, RoutedEventArgs e) => SetFadeOut(0.5);
    private void OnFadeOut1Clicked(object sender, RoutedEventArgs e) => SetFadeOut(1.0);
    private void OnFreeze0Clicked(object sender, RoutedEventArgs e) => SetFreezeFrame(0);
    private void OnFreeze1Clicked(object sender, RoutedEventArgs e) => SetFreezeFrame(1);
    private void OnFreeze2Clicked(object sender, RoutedEventArgs e) => SetFreezeFrame(2);
    private void OnFreeze3Clicked(object sender, RoutedEventArgs e) => SetFreezeFrame(3);

    private void OnEditorVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (VolumeValueText is not null)
            VolumeValueText.Text = $"{Math.Round(e.NewValue):0}%";

        ApplyAudioPreviewSettings();
    }

    private void OnMuteAudioToggled(object sender, RoutedEventArgs e)
    {
        ApplyAudioPreviewSettings();
    }

    private void OnSubtitleTextChanged(object sender, TextChangedEventArgs e)
    {
        LayerTimeline.SetLayerEnabled(
            EditorTimelineLayer.Subtitle,
            !string.IsNullOrWhiteSpace(SubtitleTextBox.Text),
            CurrentTimelinePosition,
            GetLayerLabel(SubtitleTextBox.Text, "Subtitle"));
        UpdateSubtitlePreview();
    }

    private void SeekTo(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        if (position > _duration)
            position = _duration;

        _previewPosition = position;
        UpdatePlaybackUi();
    }

    private double ExportAudioVolume => EditorVolumeSlider is null
        ? 1.0
        : Math.Clamp(EditorVolumeSlider.Value / 100.0, 0, 2);

    private TimeSpan CurrentTimelinePosition => _previewPosition;

    private void ResetEditorAdjustments()
    {
        SetPlaybackSpeed(1.0);
        SetTextStyle("plain");
        SetTextPosition("bottom");
        SetVideoFilter("none");
        SetElementOverlay("none");
        SetFadeIn(0);
        SetFadeOut(0);
        SetFreezeFrame(0);
        SetEditorTool("general");
        LayerTimeline.ResetLayers();

        if (TextOverlayBox is not null)
            TextOverlayBox.Text = string.Empty;
        if (SubtitleTextBox is not null)
            SubtitleTextBox.Text = string.Empty;
        if (EditorVolumeSlider is not null)
            EditorVolumeSlider.Value = 100;
        if (MuteAudioToggle is not null)
            MuteAudioToggle.IsOn = false;

        ApplyAudioPreviewSettings();
        UpdatePreviewOverlays();
    }

    private void SetEditorTool(string tool)
    {
        _activeTool = tool;

        GeneralPanel.Visibility = tool == "general" ? Visibility.Visible : Visibility.Collapsed;
        TextPanel.Visibility = tool == "text" ? Visibility.Visible : Visibility.Collapsed;
        ElementsPanel.Visibility = tool == "elements" ? Visibility.Visible : Visibility.Collapsed;
        VfxPanel.Visibility = tool == "vfx" ? Visibility.Visible : Visibility.Collapsed;
        TransitionsPanel.Visibility = tool == "transitions" ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = tool == "audio" ? Visibility.Visible : Visibility.Collapsed;
        SubtitlesPanel.Visibility = tool == "subtitles" ? Visibility.Visible : Visibility.Collapsed;

        UpdateToolButtons();
    }

    private void SetPlaybackSpeed(double speed)
    {
        _playbackSpeed = Math.Clamp(speed, 0.5, 2.0);
        ApplyPlaybackSpeed();
        UpdateSpeedButtons();
    }

    private void ApplyPlaybackSpeed()
    {
        _lastPreviewTick = 0;
    }

    private void ApplyAudioPreviewSettings()
    {
        // Safe editor-preview använder en statisk thumbnail. Ljudinställningen används vid export.
    }

    private void SetTextStyle(string style)
    {
        _textStyle = style;
        UpdateTextButtons();
        UpdateTextOverlayPreview();
    }

    private void SetTextPosition(string position)
    {
        _textPosition = position;
        UpdateTextButtons();
        UpdateTextOverlayPreview();
    }

    private void SetVideoFilter(string filter)
    {
        _videoFilter = filter;
        LayerTimeline.SetLayerEnabled(
            EditorTimelineLayer.Vfx,
            !string.Equals(filter, "none", StringComparison.OrdinalIgnoreCase),
            CurrentTimelinePosition,
            GetVfxLabel(filter));
        UpdateFilterButtons();
        UpdateVfxPreview();
    }

    private void SetElementOverlay(string overlay)
    {
        _elementOverlay = overlay;
        LayerTimeline.SetLayerEnabled(
            EditorTimelineLayer.Element,
            !string.Equals(overlay, "none", StringComparison.OrdinalIgnoreCase),
            CurrentTimelinePosition,
            GetElementLabel(overlay));
        UpdateElementButtons();
        UpdateElementPreview();
    }

    private void SetFadeIn(double seconds)
    {
        _fadeInSeconds = seconds;
        UpdateTransitionButtons();
    }

    private void SetFadeOut(double seconds)
    {
        _fadeOutSeconds = seconds;
        UpdateTransitionButtons();
    }

    private void SetFreezeFrame(double seconds)
    {
        _freezeFrameSeconds = seconds;
        UpdateTransitionButtons();
    }

    private void UpdatePreviewOverlays()
    {
        UpdateTextOverlayPreview();
        UpdateSubtitlePreview();
        UpdateVfxPreview();
        UpdateElementPreview();
    }

    private void UpdateTextOverlayPreview()
    {
        if (TextOverlayBackground is null || TextOverlayPreview is null || TextOverlayBox is null)
            return;

        var position = CurrentTimelinePosition;
        var text = TextOverlayBox.Text.Trim();
        TextOverlayBackground.Visibility = string.IsNullOrWhiteSpace(text) || !LayerTimeline.IsTimeInLayer(EditorTimelineLayer.Text, position)
            ? Visibility.Collapsed
            : Visibility.Visible;

        TextOverlayPreview.Text = _textStyle == "meme"
            ? text.ToUpperInvariant()
            : text;

        TextOverlayPreview.FontFamily = _textStyle == "pixel"
            ? new FontFamily("Consolas")
            : new FontFamily("Segoe UI Variable");
        TextOverlayPreview.FontWeight = _textStyle is "bold" or "meme"
            ? FontWeights.Bold
            : FontWeights.SemiBold;
        TextOverlayPreview.FontSize = _textStyle == "meme" ? 40 : 34;
        TextOverlayPreview.Foreground = new SolidColorBrush(_textStyle == "highlight"
            ? Color.FromArgb(255, 0, 0, 0)
            : _textStyle == "pixel"
                ? Color.FromArgb(255, 176, 255, 80)
                : Color.FromArgb(255, 255, 255, 255));
        TextOverlayBackground.Background = new SolidColorBrush(_textStyle == "highlight"
            ? Color.FromArgb(230, 238, 255, 38)
            : Color.FromArgb(0, 0, 0, 0));

        TextOverlayBackground.VerticalAlignment = _textPosition switch
        {
            "top" => VerticalAlignment.Top,
            "center" => VerticalAlignment.Center,
            _ => VerticalAlignment.Bottom
        };
    }

    private void UpdateSubtitlePreview()
    {
        if (SubtitleOverlayBackground is null || SubtitleOverlayPreview is null || SubtitleTextBox is null)
            return;

        var text = SubtitleTextBox.Text.Trim();
        SubtitleOverlayBackground.Visibility = string.IsNullOrWhiteSpace(text) || !LayerTimeline.IsTimeInLayer(EditorTimelineLayer.Subtitle, CurrentTimelinePosition)
            ? Visibility.Collapsed
            : Visibility.Visible;
        SubtitleOverlayPreview.Text = text;
    }

    private void UpdateVfxPreview()
    {
        if (VfxTintOverlay is null)
            return;

        var isActive = LayerTimeline.IsTimeInLayer(EditorTimelineLayer.Vfx, CurrentTimelinePosition);
        (Color color, double opacity) = isActive ? _videoFilter switch
        {
            "warm" => (Color.FromArgb(255, 255, 136, 56), 0.16),
            "cool" => (Color.FromArgb(255, 70, 130, 255), 0.14),
            "cinematic" => (Color.FromArgb(255, 0, 0, 0), 0.15),
            "mono" => (Color.FromArgb(255, 160, 160, 160), 0.14),
            _ => (Color.FromArgb(0, 0, 0, 0), 0)
        } : (Color.FromArgb(0, 0, 0, 0), 0);

        VfxTintOverlay.Background = new SolidColorBrush(color);
        VfxTintOverlay.Opacity = opacity;
    }

    private void UpdateElementPreview()
    {
        var isActive = LayerTimeline.IsTimeInLayer(EditorTimelineLayer.Element, CurrentTimelinePosition);
        ElementFocusFrame.Visibility = isActive && _elementOverlay == "focus" ? Visibility.Visible : Visibility.Collapsed;
        ElementLetterboxOverlay.Visibility = isActive && _elementOverlay == "letterbox" ? Visibility.Visible : Visibility.Collapsed;
        ElementCenterMarker.Visibility = isActive && _elementOverlay == "center" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateAllOptionButtons()
    {
        UpdateToolButtons();
        UpdateSpeedButtons();
        UpdateTextButtons();
        UpdateFilterButtons();
        UpdateElementButtons();
        UpdateTransitionButtons();
    }

    private void UpdateToolButtons()
    {
        SetToolButtonState(GeneralToolButton, _activeTool == "general");
        SetToolButtonState(TextToolButton, _activeTool == "text");
        SetToolButtonState(ElementsToolButton, _activeTool == "elements");
        SetToolButtonState(VfxToolButton, _activeTool == "vfx");
        SetToolButtonState(TransitionsToolButton, _activeTool == "transitions");
        SetToolButtonState(AudioToolButton, _activeTool == "audio");
        SetToolButtonState(SubtitlesToolButton, _activeTool == "subtitles");
    }

    private void UpdateSpeedButtons()
    {
        SetOptionButtonState(Speed05Button, Math.Abs(_playbackSpeed - 0.5) < 0.001);
        SetOptionButtonState(Speed1Button, Math.Abs(_playbackSpeed - 1.0) < 0.001);
        SetOptionButtonState(Speed15Button, Math.Abs(_playbackSpeed - 1.5) < 0.001);
        SetOptionButtonState(Speed2Button, Math.Abs(_playbackSpeed - 2.0) < 0.001);
    }

    private void UpdateTextButtons()
    {
        SetOptionButtonState(TextPlainButton, _textStyle == "plain");
        SetOptionButtonState(TextBoldButton, _textStyle == "bold");
        SetOptionButtonState(TextMemeButton, _textStyle == "meme");
        SetOptionButtonState(TextHighlightButton, _textStyle == "highlight");
        SetOptionButtonState(TextPixelButton, _textStyle == "pixel");
        SetOptionButtonState(TextPositionTopButton, _textPosition == "top");
        SetOptionButtonState(TextPositionCenterButton, _textPosition == "center");
        SetOptionButtonState(TextPositionBottomButton, _textPosition == "bottom");
    }

    private void UpdateElementButtons()
    {
        SetOptionButtonState(ElementNoneButton, _elementOverlay == "none");
        SetOptionButtonState(ElementFocusButton, _elementOverlay == "focus");
        SetOptionButtonState(ElementLetterboxButton, _elementOverlay == "letterbox");
        SetOptionButtonState(ElementCenterButton, _elementOverlay == "center");
    }

    private void UpdateFilterButtons()
    {
        SetOptionButtonState(FilterNoneButton, _videoFilter == "none");
        SetOptionButtonState(FilterBoostButton, _videoFilter == "boost");
        SetOptionButtonState(FilterWarmButton, _videoFilter == "warm");
        SetOptionButtonState(FilterCoolButton, _videoFilter == "cool");
        SetOptionButtonState(FilterMonoButton, _videoFilter == "mono");
        SetOptionButtonState(FilterCinematicButton, _videoFilter == "cinematic");
        SetOptionButtonState(FilterSharpenButton, _videoFilter == "sharpen");
        SetOptionButtonState(FilterBlurButton, _videoFilter == "blur");
    }

    private void UpdateTransitionButtons()
    {
        SetOptionButtonState(FadeInNoneButton, Math.Abs(_fadeInSeconds) < 0.001);
        SetOptionButtonState(FadeIn05Button, Math.Abs(_fadeInSeconds - 0.5) < 0.001);
        SetOptionButtonState(FadeIn1Button, Math.Abs(_fadeInSeconds - 1.0) < 0.001);
        SetOptionButtonState(FadeOutNoneButton, Math.Abs(_fadeOutSeconds) < 0.001);
        SetOptionButtonState(FadeOut05Button, Math.Abs(_fadeOutSeconds - 0.5) < 0.001);
        SetOptionButtonState(FadeOut1Button, Math.Abs(_fadeOutSeconds - 1.0) < 0.001);
        SetOptionButtonState(Freeze0Button, Math.Abs(_freezeFrameSeconds) < 0.001);
        SetOptionButtonState(Freeze1Button, Math.Abs(_freezeFrameSeconds - 1.0) < 0.001);
        SetOptionButtonState(Freeze2Button, Math.Abs(_freezeFrameSeconds - 2.0) < 0.001);
        SetOptionButtonState(Freeze3Button, Math.Abs(_freezeFrameSeconds - 3.0) < 0.001);
    }

    private static void SetToolButtonState(Button? button, bool selected)
    {
        if (button is null)
            return;

        button.Background = ResourceBrush(selected ? "KlippSurfaceElevatedBrush" : "KlippSurfaceBrush");
        button.BorderBrush = ResourceBrush(selected ? "KlippAccentBrush" : "KlippBorderSubtleBrush");
        button.Foreground = ResourceBrush(selected ? "KlippTextPrimaryBrush" : "KlippTextSecondaryBrush");
        button.BorderThickness = new Thickness(selected ? 1 : 0);
    }

    private static void SetOptionButtonState(Button? button, bool selected)
    {
        if (button is null)
            return;

        button.Background = ResourceBrush(selected ? "KlippAccentBrush" : "KlippSurfaceElevatedBrush");
        button.BorderBrush = ResourceBrush(selected ? "KlippAccentBrush" : "KlippBorderBrush");
        button.Foreground = ResourceBrush(selected ? "KlippTextPrimaryBrush" : "KlippTextSecondaryBrush");
        button.BorderThickness = new Thickness(1);
    }

    private static Brush ResourceBrush(string key)
    {
        return (Brush)Application.Current.Resources[key];
    }

    private void UpdatePlaybackUi()
    {
        var position = _previewPosition;
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        if (position > _duration)
            position = _duration;

        _previewPosition = position;
        LayerTimeline.Playhead = position;
        if (position > LayerTimeline.End && IsPlaying())
        {
            _previewPosition = LayerTimeline.End;
            PausePreview();
            position = _previewPosition;
            LayerTimeline.Playhead = position;
        }

        var isPlaying = IsPlaying();
        var playIcon = isPlaying ? "\uE769" : "\uE768";
        PreviewPlayIcon.Text = playIcon;
        TimelinePlayIcon.Text = playIcon;
        PreviewPlayButton.Opacity = isPlaying ? 0 : 0.96;
        PreviewPlayButton.IsHitTestVisible = !isPlaying;
        PositionText.Text = $"{FormatTimestamp(position)} / {FormatTimestamp(_duration)}";
        UpdatePreviewOverlays();
    }

    private void UpdateTrimUi()
    {
        StartText.Text = FormatTimestamp(LayerTimeline.Start);
        EndText.Text = FormatTimestamp(LayerTimeline.End);
        SelectionText.Text = FormatTimestamp(LayerTimeline.End - LayerTimeline.Start);
    }

    private bool IsPlaying()
    {
        return _isPreviewPlaying;
    }

    private static string FormatTimestamp(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    private static string GetLayerLabel(string value, string fallback)
    {
        var text = value.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        return text.Length <= 18 ? text : $"{text[..16]}...";
    }

    private static string GetVfxLabel(string filter)
    {
        return filter switch
        {
            "boost" => "Boost",
            "warm" => "Warm",
            "cool" => "Cool",
            "mono" => "Mono",
            "cinematic" => "Cinematic",
            "sharpen" => "Sharpen",
            "blur" => "Soft blur",
            _ => "VFX"
        };
    }

    private static string GetElementLabel(string element)
    {
        return element switch
        {
            "focus" => "Focus frame",
            "letterbox" => "Cinematic bars",
            "center" => "Center mark",
            _ => "Element"
        };
    }
}

public sealed class ClipEditorExportEventArgs : EventArgs
{
    public string FilePath { get; }
    public TimeSpan Start { get; }
    public TimeSpan End { get; }
    public double PlaybackSpeed { get; }
    public double AudioVolume { get; }
    public bool MuteAudio { get; }
    public string VideoFilter { get; }
    public string TextOverlay { get; }
    public string TextStyle { get; }
    public string TextPosition { get; }
    public string ElementOverlay { get; }
    public double FadeInSeconds { get; }
    public double FadeOutSeconds { get; }
    public double FreezeFrameSeconds { get; }
    public string SubtitleText { get; }
    public bool TextEnabled { get; }
    public TimeSpan TextStart { get; }
    public TimeSpan TextEnd { get; }
    public bool VfxEnabled { get; }
    public TimeSpan VfxStart { get; }
    public TimeSpan VfxEnd { get; }
    public bool SubtitleEnabled { get; }
    public TimeSpan SubtitleStart { get; }
    public TimeSpan SubtitleEnd { get; }
    public bool ElementEnabled { get; }
    public TimeSpan ElementStart { get; }
    public TimeSpan ElementEnd { get; }
    public double TextAnchorX { get; }
    public double TextAnchorY { get; }
    public double TextSize { get; }
    public string SubtitleStyle { get; }
    public double SubtitleAnchorX { get; }
    public double SubtitleAnchorY { get; }
    public double SubtitleSize { get; }
    public double VideoScale { get; }
    public double VideoOffsetX { get; }
    public double VideoOffsetY { get; }
    public string TextAnimation { get; }
    public double TextBoxWidth { get; }
    public double SubtitleBoxWidth { get; }
    public string TextFontFamily { get; }
    public string TextColor { get; }
    public string SubtitleFontFamily { get; }
    public string SubtitleColor { get; }

    public ClipEditorExportEventArgs(
        string filePath,
        TimeSpan start,
        TimeSpan end,
        double playbackSpeed,
        double audioVolume,
        bool muteAudio,
        string videoFilter,
        string textOverlay,
        string textStyle,
        string textPosition,
        string elementOverlay,
        double fadeInSeconds,
        double fadeOutSeconds,
        double freezeFrameSeconds,
        string subtitleText,
        bool textEnabled,
        TimeSpan textStart,
        TimeSpan textEnd,
        bool vfxEnabled,
        TimeSpan vfxStart,
        TimeSpan vfxEnd,
        bool subtitleEnabled,
        TimeSpan subtitleStart,
        TimeSpan subtitleEnd,
        bool elementEnabled,
        TimeSpan elementStart,
        TimeSpan elementEnd,
        double textAnchorX = 0.5,
        double textAnchorY = 0.84,
        double textSize = 64,
        string subtitleStyle = "subtitle",
        double subtitleAnchorX = 0.5,
        double subtitleAnchorY = 0.88,
        double subtitleSize = 42,
        double videoScale = 1.0,
        double videoOffsetX = 0,
        double videoOffsetY = 0,
        string textAnimation = "none",
        double textBoxWidth = 420,
        double subtitleBoxWidth = 760,
        string textFontFamily = EditorTextTypography.Automatic,
        string textColor = EditorTextTypography.Automatic,
        string subtitleFontFamily = EditorTextTypography.Automatic,
        string subtitleColor = EditorTextTypography.Automatic)
    {
        FilePath = filePath;
        Start = start;
        End = end;
        PlaybackSpeed = playbackSpeed;
        AudioVolume = audioVolume;
        MuteAudio = muteAudio;
        VideoFilter = videoFilter;
        TextOverlay = textOverlay;
        TextStyle = textStyle;
        TextPosition = textPosition;
        ElementOverlay = elementOverlay;
        FadeInSeconds = fadeInSeconds;
        FadeOutSeconds = fadeOutSeconds;
        FreezeFrameSeconds = freezeFrameSeconds;
        SubtitleText = subtitleText;
        TextEnabled = textEnabled;
        TextStart = textStart;
        TextEnd = textEnd;
        VfxEnabled = vfxEnabled;
        VfxStart = vfxStart;
        VfxEnd = vfxEnd;
        SubtitleEnabled = subtitleEnabled;
        SubtitleStart = subtitleStart;
        SubtitleEnd = subtitleEnd;
        ElementEnabled = elementEnabled;
        ElementStart = elementStart;
        ElementEnd = elementEnd;
        TextAnchorX = Math.Clamp(textAnchorX, 0, 1);
        TextAnchorY = Math.Clamp(textAnchorY, 0, 1);
        TextSize = Math.Clamp(textSize, 12, 220);
        SubtitleStyle = string.IsNullOrWhiteSpace(subtitleStyle)
            ? "subtitle"
            : subtitleStyle.Trim().ToLowerInvariant();
        SubtitleAnchorX = Math.Clamp(subtitleAnchorX, 0, 1);
        SubtitleAnchorY = Math.Clamp(subtitleAnchorY, 0, 1);
        SubtitleSize = Math.Clamp(subtitleSize, 18, 160);
        VideoScale = Math.Clamp(videoScale, 0.25, 3.0);
        VideoOffsetX = Math.Clamp(videoOffsetX, -0.5, 0.5);
        VideoOffsetY = Math.Clamp(videoOffsetY, -0.5, 0.5);
        TextAnimation = string.IsNullOrWhiteSpace(textAnimation)
            ? "none"
            : textAnimation.Trim().ToLowerInvariant();
        TextBoxWidth = Math.Clamp(textBoxWidth, 96, 1280);
        SubtitleBoxWidth = Math.Clamp(subtitleBoxWidth, 220, 1280);
        TextFontFamily = EditorTextTypography.NormalizeFontFamily(textFontFamily);
        TextColor = EditorTextTypography.NormalizeColor(textColor);
        SubtitleFontFamily = EditorTextTypography.NormalizeFontFamily(subtitleFontFamily);
        SubtitleColor = EditorTextTypography.NormalizeColor(subtitleColor);
    }
}
