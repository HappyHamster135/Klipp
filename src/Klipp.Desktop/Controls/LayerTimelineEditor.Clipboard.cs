using System;
using System.Linq;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml.Controls;

namespace Klipp.Desktop.Controls;

public sealed partial class LayerTimelineEditor
{
    // Shared across editor instances so clips can be pasted into another open project.
    private static readonly EditorTimelineClipboardService ClipClipboard = new();
    private string _copyClipGesture = "Ctrl + C";
    private string _cutClipGesture = "Ctrl + X";
    private string _pasteClipGesture = "Ctrl + V";

    public void ConfigureClipboardGestures(string copyGesture, string cutGesture, string pasteGesture)
    {
        _copyClipGesture = copyGesture ?? string.Empty;
        _cutClipGesture = cutGesture ?? string.Empty;
        _pasteClipGesture = pasteGesture ?? string.Empty;
    }

    public bool HasCopiedClips => ClipClipboard.HasClips;
    public bool CanCopySelectedClips => (_selectedLayer is EditorTimelineLayer.Media or EditorTimelineLayer.Audio or EditorTimelineLayer.Playhead) &&
        (_mediaClips.Any(clip => _selectedMediaClipIds.Contains(clip.Id)) ||
         _audioTrackSegments.Any(clip => _selectedAudioTrackIds.Contains(clip.Id)));
    public bool CanCutSelectedClips
    {
        get
        {
            if (!CanCopySelectedClips) return false;
            var groups = _mediaClips.Where(clip => _selectedMediaClipIds.Contains(clip.Id)).Select(clip => clip.GroupId)
                .Concat(_audioTrackSegments.Where(clip => _selectedAudioTrackIds.Contains(clip.Id)).Select(clip => clip.GroupId))
                .Where(group => !string.IsNullOrEmpty(group)).ToHashSet(StringComparer.Ordinal);
            return !_mediaClips.Any(clip => (_selectedMediaClipIds.Contains(clip.Id) || groups.Contains(clip.GroupId)) && IsClipLocked(clip)) &&
                !_audioTrackSegments.Any(clip => (_selectedAudioTrackIds.Contains(clip.Id) || groups.Contains(clip.GroupId)) && IsAudioLocked(clip));
        }
    }

    public bool CopySelectedClips()
    {
        if (!CanCopySelectedClips)
        {
            SetEditStatus("Select video or audio clips to copy.");
            return false;
        }
        ClipClipboard.Capture(_mediaClips.Where(clip => _selectedMediaClipIds.Contains(clip.Id)),
            _audioTrackSegments.Where(clip => _selectedAudioTrackIds.Contains(clip.Id)), _tracks,
            _effectLayers.Where(layer => _selectedMediaClipIds.Contains(layer.TargetClipId)));
        SetEditStatus($"{ClipClipboard.ClipCount} {(ClipClipboard.ClipCount == 1 ? "clip" : "clips")} copied. Paste at the playhead.");
        return true;
    }

    public bool CutSelectedClips()
    {
        if (!CanCopySelectedClips)
        {
            SetEditStatus("Select video or audio clips to cut.");
            return false;
        }
        if (!CanCutSelectedClips)
        {
            SetEditStatus("Unlock the selected clips' tracks and any locked group members before cutting. No clips were cut.");
            return false;
        }
        if (!CopySelectedClips() || !DeleteMixedSelection(ripple: false)) return false;
        SetEditStatus($"{ClipClipboard.ClipCount} {(ClipClipboard.ClipCount == 1 ? "clip" : "clips")} cut. Paste at the playhead.");
        return true;
    }

    public bool PasteClipsAtPlayhead()
    {
        if (!ClipClipboard.TryCreatePaste(_tracks, _playhead.TotalSeconds, _frameRate, out var plan, out var status))
        {
            SetEditStatus(status);
            return false;
        }
        var pasted = plan!;
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        ClearMarkerSelection();
        _tracks.AddRange(pasted.AddedTracks);
        _mediaClips.AddRange(pasted.VideoClips);
        _audioTrackSegments.AddRange(pasted.AudioClips);
        _effectLayers.AddRange(pasted.EffectLayers.Select(CloneEffectLayer));
        RebuildEffectLayerBlocks();
        _selectedMediaClipIds.Clear();
        _selectedMediaClipIds.UnionWith(pasted.VideoClips.Select(clip => clip.Id));
        _selectedAudioTrackIds.Clear();
        _selectedAudioTrackIds.UnionWith(pasted.AudioClips.Select(clip => clip.Id));
        _selectedAudioTrackId = pasted.AudioClips.FirstOrDefault()?.Id;
        _selectedLayer = pasted.VideoClips.Count > 0 ? EditorTimelineLayer.Media : EditorTimelineLayer.Audio;
        _selectedTrackId = pasted.VideoClips.FirstOrDefault()?.TrackId ?? pasted.AudioClips.FirstOrDefault()?.TrackId;
        if (pasted.AddedTracks.Count == 0)
            CompleteToolEdit(before, status);
        else
        {
            NormalizeMediaClips();
            RecalculateDurationFromMedia(false);
            CompleteTrackEdit(before);
            SetEditStatus(status);
        }
        return true;
    }

    private void SelectClipForContextMenu(string clipId, bool audio)
    {
        if (audio ? _selectedAudioTrackIds.Contains(clipId) : _selectedMediaClipIds.Contains(clipId))
        {
            ClearMarkerSelection();
            _selectedLayer = audio ? EditorTimelineLayer.Audio : EditorTimelineLayer.Media;
            _selectedTrackId = audio ? _audioTrackSegments.FirstOrDefault(clip => clip.Id == clipId)?.TrackId
                : _mediaClips.FirstOrDefault(clip => clip.Id == clipId)?.TrackId;
            if (audio) _selectedAudioTrackId = clipId;
            UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: _selectedLayer, selectionOnly: true);
            return;
        }
        if (audio) SelectAudioTrack(clipId); else SelectMediaClipForEditing(clipId);
    }

    private void AppendClipboardMenuActions(MenuFlyout menu)
    {
        menu.Items.Add(new MenuFlyoutSeparator());
        var copy = new MenuFlyoutItem { Text = "Copy clips" };
        var cut = new MenuFlyoutItem { Text = "Cut clips" };
        var paste = new MenuFlyoutItem { Text = "Paste at playhead" };
        copy.Click += (_, _) => CopySelectedClips();
        cut.Click += (_, _) => CutSelectedClips();
        paste.Click += (_, _) => PasteClipsAtPlayhead();
        menu.Items.Add(copy);
        menu.Items.Add(cut);
        menu.Items.Add(paste);
        menu.Opening += (_, _) =>
        {
            copy.KeyboardAcceleratorTextOverride = _copyClipGesture;
            cut.KeyboardAcceleratorTextOverride = _cutClipGesture;
            paste.KeyboardAcceleratorTextOverride = _pasteClipGesture;
            copy.IsEnabled = CanCopySelectedClips;
            cut.IsEnabled = CanCutSelectedClips;
            paste.IsEnabled = HasCopiedClips;
        };
    }
}
