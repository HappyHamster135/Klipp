using System;
using System.Linq;
using Klipp.Core.Editing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private bool _syncingExportPresetPicker;

    private void OnExportPresetSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_syncingExportPresetPicker || _syncingExportControls || _suppressProjectChanges ||
            _isLoadingClip || _project is null || ExportPresetComboBox.SelectedItem is not ComboBoxItem { Tag: string id })
            return;
        var preset = EditorExportPresetMath.Presets.FirstOrDefault(choice => choice.Id == id);
        if (preset is null) return;
        var settings = EditorExportPresetMath.Resolve(preset, _project.CanvasWidth, _project.CanvasHeight);
        CommitPendingProjectChange();
        _exportResolutionMode = settings.ResolutionMode;
        _exportWidth = settings.Width;
        _exportHeight = settings.Height;
        _exportQuality = settings.Quality;
        _exportFrameRate = settings.FrameRate;
        _exportBitrateMbps = settings.VideoBitrateMbps;
        _exportAudioBitrateKbps = settings.AudioBitrateKbps;
        _exportAspectLocked = true;
        _exportLockedAspectRatio = settings.Width / (double)settings.Height;
        LayerTimeline.FrameRate = _exportFrameRate;
        UpdateCanvasPresetButtons();
        QueueProjectChange();
    }

    private void UpdateExportPresetSummary()
    {
        if (ExportPresetComboBox is null || ExportEstimateValueText is null) return;
        var settings = new EditorExportPresetSettings(_exportResolutionMode, _exportWidth, _exportHeight,
            _exportQuality, _exportFrameRate, _exportBitrateMbps, _exportAudioBitrateKbps);
        var selectedId = EditorExportPresetMath.Match(settings, _project?.CanvasWidth ?? 1280, _project?.CanvasHeight ?? 720);
        _syncingExportPresetPicker = true;
        try
        {
            if (ExportPresetComboBox.Items.Count == 0)
            {
                foreach (var preset in EditorExportPresetMath.Presets)
                    ExportPresetComboBox.Items.Add(new ComboBoxItem { Content = preset.Name, Tag = preset.Id });
                ExportPresetComboBox.Items.Add(new ComboBoxItem { Content = "Custom settings", Tag = "custom", IsEnabled = false });
            }
            ExportPresetComboBox.SelectedItem = ExportPresetComboBox.Items.OfType<ComboBoxItem>()
                .First(item => string.Equals(item.Tag as string, selectedId, StringComparison.Ordinal));
        }
        finally { _syncingExportPresetPicker = false; }
        ExportPresetDescriptionText.Text = EditorExportPresetMath.Presets.FirstOrDefault(preset => preset.Id == selectedId)?.Description
            ?? "Your individual export settings. Presets keep your current framing.";
        UpdateExportSizeEstimate();
    }

    private void UpdateExportSizeEstimate()
    {
        if (ExportEstimateValueText is null || ExportEstimateDetailText is null) return;
        if (_project is null || (_project.Clips.Count == 0 && _project.AudioTracks.Count == 0 && _project.GraphicLayers.Count == 0))
        {
            ExportEstimateValueText.Text = "—";
            ExportEstimateDetailText.Text = "Add media to estimate file size.";
            return;
        }
        // Project export renders the whole timeline, including gaps and later
        // audio. The ruler's preview range does not limit that exported project.
        var duration = _project.DurationSeconds;
        duration += _freezeFrameSeconds;
        var bytes = EditorExportPresetMath.EstimateBytes(duration, _exportBitrateMbps, _exportAudioBitrateKbps,
            includeAudio: MuteAudioToggle?.IsOn != true);
        ExportEstimateValueText.Text = "Approx. " + FormatProxyBytes(bytes);
        ExportEstimateDetailText.Text = $"Whole timeline · {FormatTimestamp(TimeSpan.FromSeconds(duration))} · {_exportWidth} × {_exportHeight} · {_exportFrameRate} fps";
    }
}
