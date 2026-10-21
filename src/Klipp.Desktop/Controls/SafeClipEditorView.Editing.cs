using System;
using System.Linq;
using Klipp.Desktop.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private string _generalInspectorSection = "clip";
    private bool _syncingPresetPickers;
    private TimeSpan? _transitionPreviewStopPosition;

    private void ApplyGeneralInspectorSection()
    {
        if (ClipInspectorGroup is null)
            return;
        ClipInspectorGroup.Visibility = _generalInspectorSection == "clip" ? Visibility.Visible : Visibility.Collapsed;
        ProjectInspectorGroup.Visibility = _generalInspectorSection == "project" ? Visibility.Visible : Visibility.Collapsed;
        OutputInspectorGroup.Visibility = _generalInspectorSection == "output" ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in GeneralInspectorNavigation.Children.OfType<Button>())
            SetOptionButtonState(button, button.Tag as string == _generalInspectorSection);
    }

    private void OnTimelineAudioTracksChanged(object? sender, EditorAudioTracksChangedEventArgs e)
    {
        if (_project is null || _suppressProjectChanges)
            return;
        _project.AudioTracks = e.Tracks.ToList();
        _selectedAudioTrackId = e.SelectedTrackId;
        _duration = TimeSpan.FromSeconds(_project.DurationSeconds);
        if (LayerTimeline.IsDragging)
            return;
        SyncAudioTracks(_project);
        UpdateProjectChrome();
        UpdatePlaybackUi();
        QueueProjectChange();
    }

    private void OnDetachAudioClicked(object sender, RoutedEventArgs e)
    {
        SelectVideoSourceAudio();
    }

    private void UpdatePresetPickers()
    {
        if (ClipLookComboBox is null)
            return;
        _syncingPresetPickers = true;
        try
        {
            var clip = LayerTimeline.GetSelectedMediaClip();
            SelectedClipAudioCard.Visibility = _selectedAudioTrackId is null && clip is { SourceAudioSeparated: false }
                ? Visibility.Visible : Visibility.Collapsed;
            ClipLookComboBox.IsEnabled = clip is not null && _project?.GetTrack(clip.TrackId)?.Locked != true;
            ClipLookComboBox.SelectedItem = ClipLookComboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => (item.Tag as string) == (clip?.Filter ?? "none"));
            QuickClipLookNameText.Text = (ClipLookComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "None";
            SyncClipLookClipboardControls();
            SyncLookBrowserSelection();
            SyncTimelineFxInspector();
            var available = LayerTimeline.TryGetSelectedMediaClipTransition(out var style, out _, out _);
            TransitionStyleComboBox.IsEnabled = available;
            PreviewTransitionButton.IsEnabled = available && CanPreviewSelectedTransition();
            TransitionStyleComboBox.SelectedItem = TransitionStyleComboBox.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => (item.Tag as string) == GetTransitionFamily(style));
            SyncTransitionDirectionPicker(TransitionDirectionComboBox, style, available);
            SyncTransitionAuditionControls();
        }
        finally { _syncingPresetPickers = false; }
    }

    private void OnClipLookChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPresetPickers || _isLoadingClip || _suppressProjectChanges ||
            ClipLookComboBox.SelectedItem is not ComboBoxItem { Tag: string preset })
            return;
        var clip = LayerTimeline.GetSelectedMediaClip();
        if (clip is null || clip.Filter == preset)
            return;
        CommitPendingProjectChange();
        SetSelectedClipFilter(preset);
    }

    private void OnTransitionStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingPresetPickers || _isLoadingClip || _suppressProjectChanges ||
            TransitionStyleComboBox.SelectedItem is not ComboBoxItem { Tag: string style })
            return;
        style = ResolveTransitionFamilyStyle(style);
        if (!LayerTimeline.TryGetSelectedMediaClipTransition(out var current, out _, out _) || current == style)
            return;
        CommitPendingProjectChange();
        PausePreview();
        SetClipTransition(style);
        UpdatePresetPickers();
    }

    private void SelectTransitionNearPlayhead()
    {
        if (LayerTimeline.TryGetSelectedMediaClipTransition(out _, out _, out _))
            return;
        if (_project is null) return;
        var trackId = LayerTimeline.GetSelectedMediaClip()?.TrackId ?? LayerTimeline.SelectedTrackId;
        var candidates = _project.Clips.Where(clip => _project.GetTrack(clip.TrackId)?.Locked != true &&
            _project.GetTransitionPredecessor(clip, includeTouching: true) is not null).ToList();
        // A selected row is intentional: never apply a preset to another row
        // simply because its cut is closer to the cursor.
        if (_project.GetTrack(trackId ?? string.Empty)?.Kind == EditorTimelineTrackKind.Video)
            candidates = candidates.Where(clip => clip.TrackId == trackId).ToList();
        var incoming = candidates.OrderBy(clip => Math.Abs(clip.TimelineStartSeconds - _previewPosition.TotalSeconds))
            .ThenBy(clip => _project.GetTrack(clip.TrackId)?.Order ?? int.MaxValue).FirstOrDefault();
        if (incoming is not null)
            LayerTimeline.SelectMediaClipForEditing(incoming.Id);
    }

    private void OnPreviewTransitionClicked(object sender, RoutedEventArgs e)
    {
        DismissTransitionAudition();
        SelectTransitionNearPlayhead();
        if (!CanPreviewSelectedTransition() || !TryGetSelectedTransitionContext(out var clip, out _, out var duration))
            return;
        PausePreview();
        var start = LayerTimeline.GetMediaClipTimelineStart(clip.Id);
        SeekTo(TimeSpan.FromSeconds(Math.Max(LayerTimeline.Start.TotalSeconds, start.TotalSeconds - 0.25)));
        _transitionPreviewStopPosition = TimeSpan.FromSeconds(Math.Min(LayerTimeline.End.TotalSeconds, start.TotalSeconds + duration + 0.25));
        TogglePlayPause();
    }

    private void ClearPrimaryPreviewForGap()
    {
        _usingNativePreview = false;
        _isPreviewOpening = false;
        _activePreviewClipId = null;
        _nativePreviewMediaPath = null;
        _lastPreviewTick = 0;
        NativePreviewPlayer.MediaPlayer.Source = null;
        PreviewImage.Source = null;
        PreviewImage.Opacity = 0;
        PreviewFallback.Visibility = Visibility.Collapsed;
    }

    private bool TryContinuePreviewWithoutVideo()
    {
        var clip = GetActivePreviewClip();
        if (clip is null)
            return false;
        var end = LayerTimeline.GetMediaClipTimelineStart(clip.Id) + TimeSpan.FromSeconds(clip.EffectiveDurationSeconds);
        if (end >= LayerTimeline.End - TimeSpan.FromMilliseconds(20))
            return false;
        _previewPosition = end;
        ClearPrimaryPreviewForGap();
        UpdateTransitionPreview(_previewPosition, seekExisting: false);
        UpdateOverlayPreview(_previewPosition, seekExisting: true);
        UpdatePlaybackUi();
        return true;
    }
}
