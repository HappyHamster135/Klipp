using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private readonly EditorLookPresetService _lookPresetService = new();
    private string? _effectStackUiSignature;
    private string? _effectStackClipId;
    private bool _syncingEffectStackControls;
    private int _effectStackLayoutVersion;
    private readonly Dictionary<string, EffectStackRowControls> _effectStackRowsByKind = new(StringComparer.Ordinal);
    private int _userLooksRefreshVersion;
    private EditorProjectClip? _copiedTransform;
    private EditorProjectAudioTrack? _copiedAudioAttributes;

    private sealed record EffectStackRowControls(Grid Row, CheckBox Enabled, Button MoveUp, Button MoveDown);

    private async void OnEffectsWorkflowLoaded(object sender, RoutedEventArgs e)
    {
        SyncEffectStackControls();
        await RefreshUserLooksAsync();
    }

    private async Task RefreshUserLooksAsync(string? selectedName = null)
    {
        var version = ++_userLooksRefreshVersion;
        selectedName ??= (UserLookComboBox.SelectedItem as EditorLookPreset)?.Name;
        try
        {
            var presets = await _lookPresetService.LoadAsync();
            if (version != _userLooksRefreshVersion)
                return;
            UserLookComboBox.ItemsSource = presets;
            UserLookComboBox.SelectedItem = presets.FirstOrDefault(item => string.Equals(item.Name, selectedName, StringComparison.OrdinalIgnoreCase)) ?? presets.FirstOrDefault();
            ApplyUserLookButton.IsEnabled = presets.Count > 0 && LayerTimeline.GetSelectedMediaClip() is not null;
            DeleteUserLookButton.IsEnabled = presets.Count > 0;
        }
        catch (Exception exception) { if (version == _userLooksRefreshVersion) ProjectStatusText.Text = exception.Message; }
    }

    private static string EffectStageName(string kind) => kind switch
    {
        "look" => "Look", "shadows" => "Shadows / highlights", "curve" => "Tone curve", "color" => "Color",
        "temperature" => "Temperature", "hue" => "Hue", "sharpen" => "Sharpen", "blur" => "Blur",
        "vignette" => "Vignette", "lut" => "Custom LUT", _ => kind
    };

    private void SyncEffectStackControls()
    {
        if (EffectStackRows is null)
            return;
        var clip = LayerTimeline.FindMediaClipById(LayerTimeline.SelectedMediaClipId);
        var editable = clip is not null && _project?.GetTrack(clip.TrackId)?.Locked != true;
        if (ApplyUserLookButton is not null)
            ApplyUserLookButton.IsEnabled = editable && UserLookComboBox.SelectedItem is EditorLookPreset;
        var signature = clip is null ? string.Empty : clip.Id + editable + string.Join('|', clip.Effects.Select(node => $"{node.Id}:{node.Kind}:{node.Enabled}"));
        if (_effectStackUiSignature == signature)
            return;
        _effectStackUiSignature = signature;
        var sameClip = clip is not null && _effectStackClipId == clip.Id;
        var reorder = sameClip && !EffectStackRows.Children.OfType<Grid>().Select(row => row.Tag as string)
            .SequenceEqual(clip!.Effects.Select(node => node.Kind));
        var scrollOffset = InspectorScrollViewer.VerticalOffset;
        var focused = reorder && XamlRoot is not null ? FocusManager.GetFocusedElement(XamlRoot) as Control : null;
        var focusedRow = focused is null ? null : _effectStackRowsByKind.Values
            .FirstOrDefault(row => IsEffectStackDescendant(focused, row.Row));
        var focusState = focused?.FocusState ?? FocusState.Unfocused;
        var layoutVersion = ++_effectStackLayoutVersion;
        _syncingEffectStackControls = true;
        try
        {
            if (!sameClip)
            {
                EffectStackRows.Children.Clear();
                _effectStackRowsByKind.Clear();
            }
            _effectStackClipId = clip?.Id;
            if (clip is null)
            {
                EffectStackRows.Children.Add(new TextBlock { Text = "Select a video clip to edit its effects.", TextWrapping = TextWrapping.Wrap });
                return;
            }
            foreach (var kind in _effectStackRowsByKind.Keys.Where(kind => clip.Effects.All(node => node.Kind != kind)).ToArray())
            {
                EffectStackRows.Children.Remove(_effectStackRowsByKind[kind].Row);
                _effectStackRowsByKind.Remove(kind);
            }
            for (var index = 0; index < clip.Effects.Count; index++)
            {
                var node = clip.Effects[index];
                if (!_effectStackRowsByKind.TryGetValue(node.Kind, out var controls))
                {
                    controls = CreateEffectStackRow(clip.Id, node.Kind);
                    _effectStackRowsByKind[node.Kind] = controls;
                }
                // Bypass and lock changes update the existing controls. Keeping their
                // place in the tree preserves focus and the inspector's scroll anchor.
                controls.Enabled.IsChecked = node.Enabled;
                controls.Enabled.IsEnabled = editable;
                controls.MoveUp.IsEnabled = editable && index > 0;
                controls.MoveDown.IsEnabled = editable && index + 1 < clip.Effects.Count;
                var currentIndex = EffectStackRows.Children.IndexOf(controls.Row);
                if (currentIndex == index)
                    continue;
                if (currentIndex >= 0)
                    EffectStackRows.Children.RemoveAt(currentIndex);
                EffectStackRows.Children.Insert(index, controls.Row);
            }
        }
        finally { _syncingEffectStackControls = false; }

        if (reorder)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (layoutVersion != _effectStackLayoutVersion || _effectStackClipId != clip!.Id || _activeTool != "vfx")
                    return;
                EffectStackRows.UpdateLayout();
                if (focusedRow is not null && EffectStackRows.Children.Contains(focusedRow.Row))
                {
                    var target = focused?.IsEnabled == true ? focused! : focusedRow.Enabled;
                    target.Focus(focusState == FocusState.Unfocused ? FocusState.Programmatic : focusState);
                }
                InspectorScrollViewer.ChangeView(null, scrollOffset, null, disableAnimation: true);
            });
        }
    }

    private static bool IsEffectStackDescendant(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor))
                return true;
        return false;
    }

    private EffectStackRowControls CreateEffectStackRow(string clipId, string kind)
    {
        var row = new Grid { Tag = kind, ColumnSpacing = 4, MinHeight = 32 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var enabled = new CheckBox { Content = EffectStageName(kind), MinHeight = 28, FontSize = 12 };
        AutomationProperties.SetName(enabled, $"Enable {EffectStageName(kind)} effect");
        enabled.Checked += (_, _) => SetEffectStageEnabled(true);
        enabled.Unchecked += (_, _) => SetEffectStageEnabled(false);
        void SetEffectStageEnabled(bool value)
        {
            if (!_syncingEffectStackControls)
                ApplySelectedClipMutation(target => EditorClipEffectWorkflowService.SetStageEnabled(target, kind, value), clipId);
        }
        row.Children.Add(enabled);
        Button MoveButton(int delta)
        {
            var button = new Button { Content = delta < 0 ? "↑" : "↓", Width = 28, Height = 28, Padding = new Thickness(0) };
            AutomationProperties.SetName(button, $"Move {EffectStageName(kind)} effect {(delta < 0 ? "up" : "down")}");
            button.Click += (_, _) => ApplySelectedClipMutation(target => EditorClipEffectWorkflowService.MoveStage(target, kind, delta), clipId);
            Grid.SetColumn(button, delta < 0 ? 1 : 2);
            row.Children.Add(button);
            return button;
        }
        return new EffectStackRowControls(row, enabled, MoveButton(-1), MoveButton(1));
    }

    private bool ApplySelectedClipMutation(Action<EditorProjectClip> mutate, string? expectedClipId = null)
    {
        if (_project is null || _suppressProjectChanges || _isLoadingClip)
            return false;
        var selected = LayerTimeline.FindMediaClipById(LayerTimeline.SelectedMediaClipId);
        if (selected is null || (expectedClipId is not null && selected.Id != expectedClipId))
        {
            ProjectStatusText.Text = "Select one video clip to edit its effects";
            return false;
        }
        if (_project.GetTrack(selected.TrackId)?.Locked == true)
        {
            ProjectStatusText.Text = "Unlock the video track before changing its effects";
            return false;
        }
        if (!EditorClipEffectWorkflowService.TryCreateMutation(selected, mutate, out var updated))
            return false;

        var selectedVideoIds = LayerTimeline.SelectedMediaClipIds.ToArray();
        var selectedAudioIds = LayerTimeline.SelectedAudioTrackIds.ToArray();
        var activeAudioId = LayerTimeline.ActiveLayer == EditorTimelineLayer.Audio ? LayerTimeline.SelectedAudioTrackId : null;
        // Committing captures the timeline and replaces the project's clip instances.
        // Resolve the target afterwards so a pending slider gesture cannot detach this edit.
        CommitPendingProjectChange();
        var position = _project.Clips.FindIndex(item => item.Id == selected.Id);
        if (position < 0)
            return false;
        PausePreview();
        ResetOriginalComparison();
        _project.Clips[position] = updated;
        LayerTimeline.SetMediaClips(_project.Clips, updated.Id);
        LayerTimeline.RestoreClipSelection(selectedVideoIds, selectedAudioIds, selected.Id, activeAudioId);
        _selectedAudioTrackId = LayerTimeline.SelectedAudioTrackId;
        RefreshPreviewAfterTimelineEdit();
        UpdateProjectChrome();
        QueueProjectChange();
        CommitPendingProjectChange();
        SyncEffectStackControls();
        return true;
    }

    private async void OnSaveUserLookClicked(object sender, RoutedEventArgs e)
    {
        var clip = LayerTimeline.GetSelectedMediaClip();
        if (clip is null) { ProjectStatusText.Text = "Select a video clip to save its look"; return; }
        clip.FilterStrength = clip.EvaluateFilterStrength(LayerTimeline.GetMediaClipLocalTime(clip.Id, CurrentTimelinePosition));
        var name = new TextBox { Header = "Preset name", MaxLength = 64, PlaceholderText = "My look" };
        var dialog = AccessibleDialogFactory.Create(XamlRoot, "Save look", name, "Save", "Cancel", ContentDialogButton.Primary);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(name.Text)) return;
        try
        {
            await _lookPresetService.SaveAsync(name.Text, clip);
            await RefreshUserLooksAsync(name.Text.Trim());
            ProjectStatusText.Text = "Look saved — available in every project";
        }
        catch (Exception exception) { ProjectStatusText.Text = exception.Message; }
    }

    private void OnApplyUserLookClicked(object sender, RoutedEventArgs e)
    {
        if (UserLookComboBox.SelectedItem is not EditorLookPreset preset)
            return;
        AddTimelineEffect(preset.Style.Filter == "none" ? "natural" : preset.Style.Filter, preset.Style, preset.Name);
    }

    private async void OnDeleteUserLookClicked(object sender, RoutedEventArgs e)
    {
        if (UserLookComboBox.SelectedItem is not EditorLookPreset preset) return;
        try { await _lookPresetService.DeleteAsync(preset.Id); await RefreshUserLooksAsync(); }
        catch (Exception exception) { ProjectStatusText.Text = exception.Message; }
    }

    private void OnCopyTransformClicked(object sender, RoutedEventArgs e)
    {
        _copiedTransform = LayerTimeline.GetSelectedMediaClip();
        ProjectStatusText.Text = _copiedTransform is null ? "Select a video clip" : "Transform and motion copied";
    }

    private void OnPasteTransformClicked(object sender, RoutedEventArgs e)
    {
        if (_copiedTransform is not null && ApplySelectedClipMutation(target => EditorClipAttributesService.CopyTransform(_copiedTransform, target)))
            ProjectStatusText.Text = "Transform and motion pasted";
    }

    private void OnCopyAudioAttributesClicked(object sender, RoutedEventArgs e)
    {
        var track = GetSelectedAudioTrack() ?? GetSelectedVideoSourceAudio();
        _copiedAudioAttributes = track is null ? null : JsonSerializer.Deserialize<EditorProjectAudioTrack>(JsonSerializer.Serialize(track));
        ProjectStatusText.Text = track is null ? "Select an audio clip" : "Audio settings copied";
    }

    private void OnPasteAudioAttributesClicked(object sender, RoutedEventArgs e)
    {
        var target = GetSelectedAudioTrack() ?? GetSelectedVideoSourceAudio();
        if (target is null || _copiedAudioAttributes is null || _project?.GetTrack(target.TrackId)?.Locked == true) return;
        CommitPendingProjectChange(); PausePreview();
        EditorClipAttributesService.CopyAudio(_copiedAudioAttributes, target);
        SyncAudioTracks(_project!); RefreshPreviewAfterTimelineEdit();
        QueueProjectChange(); CommitPendingProjectChange();
        ProjectStatusText.Text = "Audio settings pasted";
    }
}
