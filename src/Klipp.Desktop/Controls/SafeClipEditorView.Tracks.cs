using System;
using System.Linq;
using Klipp.Desktop.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView
{
    private double _preferredTimelineHeight = 220;
    private bool _timelineViewportWatchingSize;
    private bool _resizingTimelineHeight;
    private double _timelineResizePointerY;
    private double _timelineResizeStartHeight;
    private double _timelineResizePreviousPreference;

    private void OnTimelineViewportLoaded(object sender, RoutedEventArgs e)
    {
        if (!_timelineViewportWatchingSize)
        {
            SizeChanged += OnEditorSizeChangedForTimeline;
            _timelineViewportWatchingSize = true;
        }
        ApplyTimelineViewportHeight();
    }

    private void OnTimelineViewportUnloaded(object sender, RoutedEventArgs e)
    {
        SizeChanged -= OnEditorSizeChangedForTimeline;
        _timelineViewportWatchingSize = false;
    }

    private void OnEditorSizeChangedForTimeline(object sender, SizeChangedEventArgs e) => ApplyTimelineViewportHeight();

    private void ApplyTimelineViewportHeight()
    {
        if (TimelineScrollViewer is null || ActualHeight <= 0)
            return;
        var topBar = Content is Grid grid && grid.RowDefinitions.Count > 0 ? grid.RowDefinitions[0].ActualHeight : 48;
        var timelineChrome = Math.Max(96, TimelinePanel.ActualHeight - TimelineScrollViewer.ActualHeight);
        var maximum = Math.Max(96, Math.Min(420, ActualHeight - topBar - timelineChrome - 260));
        TimelineScrollViewer.Height = Math.Clamp(_preferredTimelineHeight, 96, maximum);
    }

    private void OnTimelineHeightGripPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse &&
            !e.GetCurrentPoint(TimelineHeightGrip).Properties.IsLeftButtonPressed)
            return;
        _resizingTimelineHeight = true;
        _timelineResizePointerY = e.GetCurrentPoint(this).Position.Y;
        _timelineResizeStartHeight = TimelineScrollViewer.ActualHeight;
        _timelineResizePreviousPreference = _preferredTimelineHeight;
        TimelineHeightGrip.CapturePointer(e.Pointer);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
        e.Handled = true;
    }

    private void OnTimelineHeightGripMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizingTimelineHeight)
            return;
        _preferredTimelineHeight = Math.Clamp(_timelineResizeStartHeight + _timelineResizePointerY -
            e.GetCurrentPoint(this).Position.Y, 96, 420);
        ApplyTimelineViewportHeight();
        e.Handled = true;
    }

    private void OnTimelineHeightGripReleased(object sender, PointerRoutedEventArgs e)
    {
        _resizingTimelineHeight = false;
        TimelineHeightGrip.ReleasePointerCaptures();
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        e.Handled = true;
    }

    private void OnTimelineHeightGripCanceled(object sender, PointerRoutedEventArgs e)
    {
        _preferredTimelineHeight = _timelineResizePreviousPreference;
        ApplyTimelineViewportHeight();
        OnTimelineHeightGripReleased(sender, e);
    }

    private void OnTimelineHeightGripCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _resizingTimelineHeight = false;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    private void OnTimelineHeightGripEntered(object sender, PointerRoutedEventArgs e) =>
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);

    private void OnTimelineHeightGripExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizingTimelineHeight)
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
    }

    private void OnTimelineTracksChanged(object? sender, EditorTracksChangedEventArgs e)
    {
        if (_project is null || _suppressProjectChanges || _isLoadingClip)
            return;
        _project.Tracks = e.Tracks.Select(track => track.Clone()).ToList();
        _project.Clips = LayerTimeline.GetMediaClips().ToList();
        _project.AudioTracks = LayerTimeline.GetAudioTrackSegments().ToList();
        _project.Normalize();
        PausePreview();
        RefreshPreviewAfterTimelineEdit();
        UpdateProjectChrome();
        QueueProjectChange();
    }

    private void OnAddVideoTrackClicked(object sender, RoutedEventArgs e) => AddEditorTrack(EditorTimelineTrackKind.Video);
    private void OnAddAudioTrackClicked(object sender, RoutedEventArgs e) => AddEditorTrack(EditorTimelineTrackKind.Audio);

    private void AddEditorTrack(EditorTimelineTrackKind kind)
    {
        if (_project is null)
            return;
        CommitPendingProjectChange();
        PausePreview();
        if (LayerTimeline.AddTrack(kind))
        {
            QueueProjectChange();
            CommitPendingProjectChange();
            ProjectStatusText.Text = $"{(kind == EditorTimelineTrackKind.Video ? "Video" : "Audio")} track added — drag a clip onto it";
        }
    }

    private void OnTimelineEditToolChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LayerTimeline is null || TimelineEditToolComboBox.SelectedItem is not ComboBoxItem { Tag: string tool })
            return;
        LayerTimeline.EditTool = Enum.TryParse<EditorTimelineEditTool>(tool, out var selected)
            ? selected : EditorTimelineEditTool.Selection;
        ProjectStatusText.Text = selected switch
        {
            EditorTimelineEditTool.RippleTrim => "Ripple trim — drag an edge to trim and shift later clips on sync-locked tracks",
            EditorTimelineEditTool.RollingTrim => "Rolling trim — drag a shared edge to adjust both clips",
            EditorTimelineEditTool.Slip => "Slip — drag a clip to change its source footage without moving it",
            _ => "Select — drag clips to move, edges to trim, or empty track space to select a group"
        };
    }

    private void OnLinkedSelectionToggled(object sender, RoutedEventArgs e)
    {
        if (LayerTimeline is not null)
            LayerTimeline.LinkedSelectionEnabled = LinkedSelectionToggle.IsChecked == true;
    }

    private void RunTimelineSelectionCommand(Func<bool> action)
    {
        CommitPendingProjectChange();
        PausePreview();
        if (!action())
        {
            ProjectStatusText.Text = LayerTimeline.LastEditStatus;
            return;
        }
        QueueProjectChange();
        CommitPendingProjectChange();
        RefreshPreviewAfterTimelineEdit();
    }

    private void OnGroupTimelineSelectionClicked(object sender, RoutedEventArgs e) => RunTimelineSelectionCommand(LayerTimeline.GroupSelection);
    private void OnUngroupTimelineSelectionClicked(object sender, RoutedEventArgs e) => RunTimelineSelectionCommand(LayerTimeline.UngroupSelection);
    private void OnRippleDeleteClicked(object sender, RoutedEventArgs e) => RunTimelineSelectionCommand(LayerTimeline.RippleDeleteSelection);
    private void OnCloseTimelineGapClicked(object sender, RoutedEventArgs e) => RunTimelineSelectionCommand(LayerTimeline.CloseGapAtPlayhead);
    private void OnAudioCrossfadeClicked(object sender, RoutedEventArgs e) => RunTimelineSelectionCommand(LayerTimeline.ApplyAudioCrossfadeAtPlayhead);
}
