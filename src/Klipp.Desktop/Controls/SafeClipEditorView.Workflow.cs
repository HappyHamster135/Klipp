using System;
using System.Collections.Generic;
using System.Linq;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private bool _compareOriginalPreview;
    private string? _compareOriginalClipId;

    private bool IsShowingOriginal(EditorProjectClip? clip) =>
        _compareOriginalPreview && clip is not null && clip.Id == _compareOriginalClipId;

    private void ResetOriginalComparison()
    {
        _compareOriginalPreview = false;
        _compareOriginalClipId = null;
        if (CompareOriginalButton?.IsChecked == true)
            CompareOriginalButton.IsChecked = false;
    }

    private void SyncOriginalComparisonSelection()
    {
        var selectedId = LayerTimeline.SelectedMediaClipId;
        if (_compareOriginalPreview && selectedId != _compareOriginalClipId)
            ResetOriginalComparison();
        if (CompareOriginalButton is not null)
        {
            CompareOriginalButton.IsEnabled = selectedId is not null;
            CompareOriginalButton.Content = _compareOriginalPreview ? "Show edited" : "Compare original";
            AutomationProperties.SetName(CompareOriginalButton, _compareOriginalPreview
                ? "Show the selected clip with its effects" : "Compare the selected clip with its original colors");
        }
    }

    private void OnTimelineEditStarting(object? sender, EventArgs e)
    {
        CommitPendingProjectChange();
        PausePreview();
    }

    private void OnTimelineEditStatusChanged(object? sender, EditorTimelineEditStatusEventArgs e)
    {
        ProjectStatusText.Text = e.Status;
    }

    private void OnToggleTimelineSnappingClicked(object sender, RoutedEventArgs e)
    {
        LayerTimeline.SnappingEnabled = !LayerTimeline.SnappingEnabled;
        UpdateTimelineChrome();
    }

    private void OnFitTimelineClicked(object sender, RoutedEventArgs e) =>
        OnTimelineZoomResetClicked(sender, e);

    private void SyncTimelineSnappingControl()
    {
        if (TimelineSnapButton is null)
            return;
        SetOptionButtonState(TimelineSnapButton, LayerTimeline.SnappingEnabled);
        AutomationProperties.SetName(TimelineSnapButton,
            LayerTimeline.SnappingEnabled ? "Timeline snapping on" : "Timeline snapping off");
        ToolTipService.SetToolTip(TimelineSnapButton,
            LayerTimeline.SnappingEnabled ? "Snapping on · hold Alt to bypass" : "Snapping off");
    }

    private void SeekToAdjacentEdit(bool next)
    {
        var target = EditorTimelineNavigationMath.AdjacentBoundary(LayerTimeline.GetEditBoundariesForSelectedTrack(),
            CurrentTimelinePosition.TotalSeconds, LayerTimeline.Duration.TotalSeconds, _exportFrameRate, next);
        PausePreview();
        SeekTo(TimeSpan.FromSeconds(target));
    }

    private void SyncQuickClipEffectControls(bool enabled, double strength)
    {
        if (QuickClipFilterStrengthSlider is null)
            return;
        SyncOriginalComparisonSelection();
        QuickClipFilterStrengthSlider.IsEnabled = enabled;
        QuickClipFilterStrengthSlider.Value = strength * 100;
        QuickClipFilterStrengthValueText.Text = $"{Math.Round(strength * 100):0}%";
        SyncQuickColorControls();
    }

    private void OnQuickClipFilterStrengthChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingSelectedClipVisualControls || _isLoadingClip || _suppressProjectChanges ||
            LayerTimeline is null || ClipFilterStrengthSlider is null || ClipFilterStrengthValueText is null)
            return;
        OnClipFilterStrengthChanged(sender, e);
    }

    private void OnResetClipLookClicked(object sender, RoutedEventArgs e)
    {
        if (ApplySelectedClipMutation(clip => EditorClipEffectWorkflowService.ApplyLook(clip, "none")))
        {
            UpdatePresetPickers();
            ProjectStatusText.Text = "Look removed — color adjustments and animation retained";
        }
    }

    private void OnResetAllClipEffectsClicked(object sender, RoutedEventArgs e)
    {
        if (ApplySelectedClipMutation(EditorClipEffectWorkflowService.ResetAllEffects))
        {
            UpdatePresetPickers();
            ProjectStatusText.Text = "Effects reset — motion, audio and clip timing retained";
        }
    }

    private void OnOriginalPreviewToggled(object sender, RoutedEventArgs e)
    {
        _compareOriginalPreview = sender is ToggleButton { IsChecked: true };
        _compareOriginalClipId = _compareOriginalPreview ? LayerTimeline?.SelectedMediaClipId : null;
        if (_compareOriginalPreview && _compareOriginalClipId is null)
            ResetOriginalComparison();
        if (LayerTimeline is not null)
            SyncOriginalComparisonSelection();
        if (VfxTintOverlay is not null && PrimaryEffectPreviewHost is not null)
            UpdateVfxPreview();
    }

    private void UpdateQuickEffectPreviewStatus()
    {
        if (ClipEffectPreviewStatusText is null)
            return;
        var clip = LayerTimeline.FindMediaClipById(LayerTimeline.SelectedMediaClipId);
        var overClip = clip is not null && (CurrentTimelinePosition.TotalSeconds >= clip.TimelineStartSeconds &&
            CurrentTimelinePosition.TotalSeconds < clip.TimelineStartSeconds + clip.EffectiveDurationSeconds);
        var localTime = clip is null ? 0 : LayerTimeline.GetMediaClipLocalTime(clip.Id, CurrentTimelinePosition);
        var signature = clip?.Id == GetActiveOverlayPreviewClip()?.Id ? _overlayEffectPreviewSignature
            : clip?.Id == GetActiveTransitionPreviewClip()?.Id ? _transitionEffectPreviewSignature
            : clip?.Id == GetActivePreviewClip()?.Id ? _primaryEffectPreviewSignature : null;
        var message = clip is null ? "Select one video clip to edit its effects."
            : !overClip ? "Place the playhead over this clip to preview its effects."
            : IsShowingOriginal(clip) ? "Showing this clip's original colors. Effects are kept for export."
            : _project?.GetTrack(clip.TrackId)?.Locked == true ? "Unlock this video track to change its effects."
            : clip.EvaluateFilterStrength(localTime) <= 0.001 ? "Amount is 0%. Increase it to see this clip's effects."
            : signature?.StartsWith("fallback|", StringComparison.Ordinal) == true
                ? "Live preview is limited for this look on this device."
                : !string.IsNullOrWhiteSpace(clip?.LutPath) && !System.IO.File.Exists(clip.LutPath)
                    ? "The custom LUT is missing. Relink it to restore its colors."
                    : clip is not null && clip.Filter != "none" && !clip.Effects.Any(node => node.Kind == "look" && node.Enabled)
                        ? "The look is bypassed. Enable Look in the effect stack to preview it."
                    : string.Empty;
        ClipEffectPreviewStatusText.Text = message;
        ClipEffectPreviewStatusText.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
