using System.Linq;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private EditorProjectClip? _copiedClipLook;

    private void OnCopyClipLookClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingClip || _suppressProjectChanges)
            return;
        var clip = LayerTimeline.FindMediaClipById(LayerTimeline.SelectedMediaClipId);
        if (clip is null)
            return;

        _copiedClipLook = clip.CloneWithNewId();
        _copiedClipLook.FilterStrength = clip.EvaluateFilterStrength(LayerTimeline.GetMediaClipLocalTime(clip.Id, CurrentTimelinePosition));
        SyncClipLookClipboardControls();
        ProjectStatusText.Text = "Look copied — select another clip and paste";
    }

    private void OnPasteClipLookClicked(object sender, RoutedEventArgs e)
    {
        if (_isLoadingClip || _suppressProjectChanges)
            return;
        var look = _copiedClipLook;
        var clip = LayerTimeline.FindMediaClipById(LayerTimeline.SelectedMediaClipId);
        if (look is null || clip is null)
            return;
        if (_project?.GetTrack(clip.TrackId)?.Locked == true)
        {
            ProjectStatusText.Text = "Unlock the video track before pasting a look";
            return;
        }

        AddTimelineEffect(look.Filter == "none" ? "natural" : look.Filter, look, "Copied look");
    }

    private void SyncClipLookClipboardControls()
    {
        var clip = LayerTimeline.FindMediaClipById(LayerTimeline.SelectedMediaClipId);
        var selected = clip is not null;
        if (FindName("CopyClipLookButton") is Button copy)
            copy.IsEnabled = selected;
        if (FindName("PasteClipLookButton") is Button paste)
            paste.IsEnabled = selected && _copiedClipLook is not null && _project?.GetTrack(clip!.TrackId)?.Locked != true;
    }
}
