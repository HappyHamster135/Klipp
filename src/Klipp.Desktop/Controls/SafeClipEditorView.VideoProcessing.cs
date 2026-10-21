using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private readonly EditorVideoProcessingService _videoProcessingService = new();
    private CancellationTokenSource? _videoProcessingCts;
    private bool _syncingVideoProcessingControls;

    private void OnVideoProcessingPanelLoaded(object sender, RoutedEventArgs e) => SyncVideoProcessingControls();
    private void OnVideoProcessingPanelUnloaded(object sender, RoutedEventArgs e) => _videoProcessingCts?.Cancel();
    private void OnVideoProcessingOptionsChanged(object sender, RoutedEventArgs e) { if (!_syncingVideoProcessingControls) RefreshVideoProcessingEnabledState(); }
    private void OnVideoProcessingNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => RefreshVideoProcessingEnabledState();
    private void OnVideoProcessingStrengthChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => RefreshVideoProcessingEnabledState();

    private void SyncVideoProcessingControls()
    {
        if (VideoRampStartSpeedBox is null || _videoProcessingCts is not null) return;
        var clip = LayerTimeline.GetSelectedMediaClip();
        var state = clip?.VideoProcessing;
        _syncingVideoProcessingControls = true;
        try
        {
            VideoRampEnabledCheck.IsChecked = state?.RampEnabled ?? false;
            VideoRampStartSpeedBox.Value = state?.RampStartSpeed ?? clip?.PlaybackSpeed ?? 1;
            VideoRampMiddleSpeedBox.Value = state?.RampMiddleSpeed ?? clip?.PlaybackSpeed ?? 1;
            VideoRampEndSpeedBox.Value = state?.RampEndSpeed ?? clip?.PlaybackSpeed ?? 1;
            VideoStabilizationStrengthSlider.Value = (state?.StabilizationStrength ?? 0) * 100;
            VideoProcessingStatusText.Text = clip is null ? "Select a video clip" : state?.IsApplied == true
                ? "Processed video applied. Reset restores the original range and speed." : "Audio keeps its own timing.";
        }
        finally { _syncingVideoProcessingControls = false; }
        RefreshVideoProcessingEnabledState();
    }

    private void RefreshVideoProcessingEnabledState()
    {
        if (VideoRampStartSpeedBox is null || ApplyVideoProcessingButton is null) return;
        var clip = LayerTimeline.GetSelectedMediaClip();
        var busy = _videoProcessingCts is not null;
        var editable = clip is not null && _project?.GetTrack(clip.TrackId)?.Locked != true && !busy;
        VideoRampEnabledCheck.IsEnabled = editable;
        VideoRampStartSpeedBox.IsEnabled = VideoRampMiddleSpeedBox.IsEnabled = VideoRampEndSpeedBox.IsEnabled =
            editable && VideoRampEnabledCheck.IsChecked == true;
        VideoStabilizationStrengthSlider.IsEnabled = editable;
        ApplyVideoProcessingButton.IsEnabled = editable &&
            (VideoRampEnabledCheck.IsChecked == true || VideoStabilizationStrengthSlider.Value > 0);
        ResetVideoProcessingButton.IsEnabled = editable && clip?.VideoProcessing?.IsApplied == true;
        CancelVideoProcessingButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        VideoProcessingProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnApplyVideoProcessingClicked(object sender, RoutedEventArgs e)
    {
        if (_videoProcessingCts is not null || _project is not { } project || LayerTimeline.GetSelectedMediaClip() is not { } selected ||
            project.GetTrack(selected.TrackId)?.Locked == true || _isLoadingClip || _suppressProjectChanges) return;
        if (!selected.SourceAudioSeparated)
        {
            VideoProcessingStatusText.Text = "Separate the video's audio before processing.";
            return;
        }
        if (selected.VideoProcessing is { IsApplied: true } applied &&
            (selected.SourcePath != applied.BakedSourcePath || selected.TrimStartSeconds > 0.001 ||
             Math.Abs(selected.TrimEndSeconds - selected.SourceDurationSeconds) > 0.001 || Math.Abs(selected.PlaybackSpeed - 1) > 0.001))
        {
            VideoProcessingStatusText.Text = "Reset processing before changing a processed clip's range or speed.";
            return;
        }
        var options = new EditorVideoProcessingOptions(VideoRampEnabledCheck.IsChecked == true,
            VideoRampStartSpeedBox.Value, VideoRampMiddleSpeedBox.Value, VideoRampEndSpeedBox.Value,
            VideoStabilizationStrengthSlider.Value / 100, project.Settings.ExportFrameRate).Normalize();
        var source = selected.VideoProcessing is { IsApplied: true } previous ? previous.Clone() : new EditorProjectVideoProcessingState
        {
            OriginalSourcePath = selected.SourcePath, OriginalSourceDurationSeconds = selected.SourceDurationSeconds,
            OriginalTrimStartSeconds = selected.TrimStartSeconds, OriginalTrimEndSeconds = selected.TrimEndSeconds,
            OriginalPlaybackSpeed = selected.PlaybackSpeed
        };
        var generation = JsonSerializer.Serialize(selected);
        var loadVersion = _projectLoadVersion;
        CommitPendingProjectChange();
        PausePreview();
        using var operation = new CancellationTokenSource();
        _videoProcessingCts = operation;
        RefreshVideoProcessingEnabledState();
        VideoProcessingProgressBar.Value = 0;
        try
        {
            var progress = new Progress<EditorVideoProcessingProgress>(value =>
            {
                if (!ReferenceEquals(_videoProcessingCts, operation)) return;
                VideoProcessingProgressBar.Value = value.Fraction * 100;
                VideoProcessingStatusText.Text = $"{value.Stage} · {Math.Round(value.Fraction * 100):0}%";
            });
            var result = await _videoProcessingService.ProcessAsync(source.OriginalSourcePath,
                source.OriginalTrimStartSeconds, source.OriginalTrimEndSeconds, source.OriginalPlaybackSpeed,
                options, progress, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            var current = _project?.FindClip(selected.Id);
            if (loadVersion != _projectLoadVersion || !ReferenceEquals(project, _project) || LayerTimeline.GetSelectedMediaClip()?.Id != selected.Id || current is null ||
                project.GetTrack(current.TrackId)?.Locked == true || JsonSerializer.Serialize(current) != generation)
            {
                VideoProcessingStatusText.Text = "The clip changed during processing. Your current edit was kept.";
                return;
            }
            source.BakedSourcePath = result.SourcePath;
            source.IsApplied = true;
            source.RampEnabled = options.RampEnabled;
            source.RampStartSpeed = options.StartSpeed;
            source.RampMiddleSpeed = options.MiddleSpeed;
            source.RampEndSpeed = options.EndSpeed;
            source.StabilizationStrength = options.StabilizationStrength;
            if (ApplySelectedClipMutation(target =>
            {
                RetimeVideoProcessingKeyframes(target, result.DurationSeconds);
                target.VideoProcessing = source;
                target.SourcePath = result.SourcePath;
                target.SourceDurationSeconds = result.DurationSeconds;
                target.TrimStartSeconds = 0;
                target.TrimEndSeconds = result.DurationSeconds;
                target.PlaybackSpeed = 1;
                target.SourceAudioSeparated = true;
            }))
            {
                ProjectStatusText.Text = "Video processing applied — separate audio is unchanged";
                VideoProcessingStatusText.Text = "Processed video applied";
            }
        }
        catch (OperationCanceledException) { VideoProcessingStatusText.Text = "Processing cancelled. Your clip is unchanged."; }
        catch (Exception exception) { VideoProcessingStatusText.Text = exception.Message; }
        finally
        {
            if (ReferenceEquals(_videoProcessingCts, operation)) _videoProcessingCts = null;
            RefreshVideoProcessingEnabledState();
        }
    }

    private void OnCancelVideoProcessingClicked(object sender, RoutedEventArgs e) => _videoProcessingCts?.Cancel();

    private void OnResetVideoProcessingClicked(object sender, RoutedEventArgs e)
    {
        if (LayerTimeline.GetSelectedMediaClip()?.VideoProcessing is not { IsApplied: true } source) return;
        if (!File.Exists(source.OriginalSourcePath)) { VideoProcessingStatusText.Text = "Relink the original video before resetting processing."; return; }
        if (ApplySelectedClipMutation(target =>
        {
            var duration = Math.Max(0.1, source.OriginalTrimEndSeconds - source.OriginalTrimStartSeconds) /
                Math.Clamp(source.OriginalPlaybackSpeed, 0.25, 4);
            RetimeVideoProcessingKeyframes(target, duration);
            target.SourcePath = source.OriginalSourcePath;
            target.SourceDurationSeconds = source.OriginalSourceDurationSeconds;
            target.TrimStartSeconds = source.OriginalTrimStartSeconds;
            target.TrimEndSeconds = source.OriginalTrimEndSeconds;
            target.PlaybackSpeed = source.OriginalPlaybackSpeed;
            target.VideoProcessing = source.Clone();
            target.VideoProcessing.IsApplied = false;
        }))
        {
            SyncVideoProcessingControls();
            ProjectStatusText.Text = "Original video range and speed restored";
        }
    }

    private static void RetimeVideoProcessingKeyframes(EditorProjectClip clip, double duration)
    {
        var ratio = duration / Math.Max(0.00001, clip.EffectiveDurationSeconds);
        foreach (var keyframe in clip.TransformKeyframes) keyframe.TimeSeconds *= ratio;
        foreach (var keyframe in clip.FilterStrengthKeyframes) keyframe.TimeSeconds *= ratio;
        foreach (var keyframe in clip.VolumeKeyframes) keyframe.TimeSeconds *= ratio;
    }
}
