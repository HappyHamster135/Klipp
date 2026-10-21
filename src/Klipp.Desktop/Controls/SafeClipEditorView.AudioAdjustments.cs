using System;
using System.Globalization;
using System.Linq;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private EditorProjectAudioTrack[] _audioAdjustmentSelectorTracks = [];
    private string[] _audioAdjustmentSelectorNames = [];
    private static string FormatAudioLevel(double volume) => !double.IsFinite(volume) || volume <= 0
        ? "−∞ dB" : (20 * Math.Log10(volume)).ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + " dB";

    private void SyncAudioAdjustmentControls(EditorProjectAudioTrack? track, bool enabled, double volume)
    {
        var tracks = _project?.AudioTracks;
        var selectorChanged = (tracks?.Count ?? 0) != _audioAdjustmentSelectorTracks.Length;
        if (!selectorChanged && tracks is not null)
            for (var index = 0; index < tracks.Count; index++)
                if (!ReferenceEquals(tracks[index], _audioAdjustmentSelectorTracks[index]) ||
                    tracks[index].Name != _audioAdjustmentSelectorNames[index]) { selectorChanged = true; break; }
        if (selectorChanged)
        {
            _audioAdjustmentSelectorTracks = tracks?.ToArray() ?? [];
            _audioAdjustmentSelectorNames = _audioAdjustmentSelectorTracks.Select(clip => clip.Name).ToArray();
            AudioClipSelector.ItemsSource = _audioAdjustmentSelectorTracks;
        }
        AudioClipSelector.SelectedItem = track;
        ToolTipService.SetToolTip(AudioClipSelector, track?.Name ?? "Choose game audio, microphone, Discord, music or another audio clip to adjust.");
        AudioClipSelector.IsEnabled = tracks?.Count > 0;
        SelectedAudioTrackEditStatus.Visibility = track is not null && !enabled ? Visibility.Visible : Visibility.Collapsed;
        SelectedAudioTrackVolumeBox.IsEnabled = SelectedAudioTrackVolumeResetButton.IsEnabled = enabled;
        SelectedAudioTrackVolumeBox.Value = Math.Clamp(volume * 100, 0, 200);
        var normalizationGain = track?.NormalizeAudio == true ? Math.Pow(10, track.NormalizationGainDb / 20) : 1;
        SelectedAudioTrackPreviewGainHint.Visibility = PreviewAudioGainEffect.HasAttachmentFailure &&
            track is not null && LivePreviewVolume * volume * normalizationGain > 1.0001
            ? Visibility.Visible : Visibility.Collapsed;
        SelectedAudioTrackFadeInBox.IsEnabled = SelectedAudioTrackFadeOutBox.IsEnabled = enabled;
        SelectedAudioTrackFadeInBox.Maximum = SelectedAudioTrackFadeOutBox.Maximum = (track?.EffectiveDurationSeconds ?? 1) / 2;
        SelectedAudioTrackFadeInBox.Value = track?.FadeInSeconds ?? 0;
        SelectedAudioTrackFadeOutBox.Value = track?.FadeOutSeconds ?? 0;
    }

    private void OnAudioClipSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingAudioTrackControls || _isLoadingClip || AudioClipSelector.SelectedItem is not EditorProjectAudioTrack track)
            return;
        SelectAudioClipForAdjustment(track);
    }

    private void SelectAudioClipForAdjustment(EditorProjectAudioTrack track)
    {
        // Levels can be balanced while hearing game and microphone together.
        _selectedAudioTrackId = track.Id;
        LayerTimeline.SelectAudioTrack(track.Id);
        SetEditorTool("audio");
        SyncSelectedAudioTrackControls();
    }

    private void OnSelectedAudioTrackVolumeNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_syncingAudioTrackControls || !double.IsFinite(e.NewValue)) return;
        SetSelectedAudioTrackVolume(e.NewValue);
    }

    private void OnResetSelectedAudioTrackVolumeClicked(object sender, RoutedEventArgs e) => SetSelectedAudioTrackVolume(100);

    private void SetSelectedAudioTrackVolume(double percent)
    {
        if (!double.IsFinite(percent)) return;
        ApplySelectedAudioTrackChange(track =>
        {
            var target = Math.Clamp(percent / 100, 0, 2);
            var current = track.EvaluateVolume(Math.Max(0, CurrentTimelinePosition.TotalSeconds - track.TimelineStartSeconds));
            foreach (var keyframe in track.VolumeKeyframes)
                keyframe.Volume = Math.Clamp(current > 0.001 ? keyframe.Volume * target / current : keyframe.Volume + target, 0, 2);
            track.Volume = target;
        });
    }

    private void OnSelectedAudioTrackFadeChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_syncingAudioTrackControls || !double.IsFinite(e.NewValue)) return;
        ApplySelectedAudioTrackChange(track =>
        {
            if (ReferenceEquals(sender, SelectedAudioTrackFadeInBox)) track.FadeInSeconds = Math.Max(0, e.NewValue);
            else track.FadeOutSeconds = Math.Max(0, e.NewValue);
        });
    }
}
