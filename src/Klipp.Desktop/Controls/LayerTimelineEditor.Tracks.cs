using System;
using System.Collections.Generic;
using System.Linq;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Klipp.Desktop.Controls;

public sealed partial class LayerTimelineEditor
{
    private readonly List<EditorTimelineTrack> _tracks = [];
    private readonly Dictionary<string, (Border Row, Grid Header)> _trackRows = new(StringComparer.Ordinal);
    private string? _selectedTrackId;
    public event EventHandler<EditorTracksChangedEventArgs>? TracksChanged;
    public string? SelectedTrackId => _selectedTrackId;
    public bool LinkedSelectionEnabled { get; set; }
    public EditorTimelineEditTool EditTool { get; set; }

    public IReadOnlyList<EditorTimelineTrack> GetTracks() => _tracks.Select(track => track.Clone()).ToList();

    public void SetTracks(IEnumerable<EditorTimelineTrack> tracks)
    {
        _tracks.Clear();
        _tracks.AddRange(tracks.Select(track => track.Clone()));
        EnsureTimelineTracks();
        RebuildTrackRows();
        UpdateVisuals(false, null, EditorTimelineLayer.None);
    }

    public bool AddTrack(EditorTimelineTrackKind kind)
    {
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        var order = _tracks.Where(track => track.Kind == kind).Select(track => track.Order).DefaultIfEmpty(-1).Max() + 1;
        var track = new EditorTimelineTrack
        {
            Id = Guid.NewGuid().ToString("N"), Kind = kind, Order = order,
            Name = $"{(kind == EditorTimelineTrackKind.Video ? "V" : "A")}{order + 1}", Height = 44
        };
        _tracks.Add(track);
        _selectedTrackId = track.Id;
        CompleteTrackEdit(before);
        return true;
    }

    public bool UpdateTrack(EditorTimelineTrack track)
    {
        var existing = _tracks.FindIndex(item => item.Id == track.Id);
        if (existing < 0)
            return false;
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        _tracks[existing] = track.Clone();
        _selectedTrackId = track.Id;
        _tracks[existing].Normalize();
        CompleteTrackEdit(before);
        return true;
    }

    public bool RemoveSelectedTrack()
    {
        var track = _tracks.FirstOrDefault(item => item.Id == _selectedTrackId);
        if (track is null || _tracks.Count(item => item.Kind == track.Kind) <= (track.Kind == EditorTimelineTrackKind.Video ? 2 : 1))
            return false;
        if (_mediaClips.Any(clip => clip.TrackId == track.Id) || _audioTrackSegments.Any(clip => clip.TrackId == track.Id))
        {
            SetEditStatus("Move or delete the clips on this track before removing it.");
            return false;
        }
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        _tracks.Remove(track);
        _selectedTrackId = null;
        CompleteTrackEdit(before);
        return true;
    }

    private void EnsureTimelineTracks()
    {
        if (!_tracks.Any(track => track.Kind == EditorTimelineTrackKind.Video))
            _tracks.Add(new EditorTimelineTrack { Id = "video-1", Name = "V1", Kind = EditorTimelineTrackKind.Video, Height = 44 });
        if (!_tracks.Any(track => track.Kind == EditorTimelineTrackKind.Audio))
            _tracks.Add(new EditorTimelineTrack { Id = "audio-1", Name = "A1", Kind = EditorTimelineTrackKind.Audio, Height = 44 });
        if (_tracks.Count(track => track.Kind == EditorTimelineTrackKind.Video) == 1)
            _tracks.Add(new EditorTimelineTrack { Id = "video-2", Name = "V2", Kind = EditorTimelineTrackKind.Video, Order = 1, Height = 44 });
        var video = _tracks.Where(track => track.Kind == EditorTimelineTrackKind.Video).OrderBy(track => track.Order).ToList();
        var audio = _tracks.Where(track => track.Kind == EditorTimelineTrackKind.Audio).OrderBy(track => track.Order).ToList();
        foreach (var clip in _mediaClips)
        {
            if (!video.Any(track => track.Id == clip.TrackId))
            {
                if (clip.Track == EditorClipTrack.Overlay && video.Count == 1)
                {
                    var overlay = new EditorTimelineTrack { Id = "video-2", Name = "V2", Kind = EditorTimelineTrackKind.Video, Order = 1, Height = 44 };
                    _tracks.Add(overlay);
                    video.Add(overlay);
                }
                clip.TrackId = video[clip.Track == EditorClipTrack.Overlay && video.Count > 1 ? 1 : 0].Id;
            }
            clip.Track = clip.TrackId == video[0].Id ? EditorClipTrack.Primary : EditorClipTrack.Overlay;
        }
        foreach (var clip in _audioTrackSegments)
            if (!audio.Any(track => track.Id == clip.TrackId))
                clip.TrackId = audio[0].Id;
        foreach (var track in _tracks)
            track.Normalize();
    }

    private IEnumerable<EditorTimelineTrack> VisualTracks =>
        _tracks.Where(track => track.Kind == EditorTimelineTrackKind.Video).OrderByDescending(track => track.Order)
            .Concat(_tracks.Where(track => track.Kind == EditorTimelineTrackKind.Audio).OrderBy(track => track.Order));

    private double PersistentTrackTop(string? id)
    {
        var top = MediaGroupHeaderTop + GroupHeaderHeight;
        foreach (var track in VisualTracks)
        {
            if (track.Id == id)
                return top;
            top += Math.Clamp(track.Height, 38, 160) + TrackGap;
            if (track.Kind == EditorTimelineTrackKind.Video && ShowLinkedAudioTrack &&
                !_tracks.Any(item => item.Kind == EditorTimelineTrackKind.Video && item.Order < track.Order))
                top += TrackHeight + TrackGap;
        }
        return top;
    }

    private double PersistentTracksBottom => VisualTracks.LastOrDefault() is { } track
        ? PersistentTrackTop(track.Id) + Math.Clamp(track.Height, 38, 160)
        : MediaGroupHeaderTop + GroupHeaderHeight;
    private double MediaRowTop(EditorProjectClip clip) => PersistentTrackTop(clip.TrackId);
    private double AudioRowTopFor(EditorProjectAudioTrack clip) => PersistentTrackTop(clip.TrackId);
    private double MediaRowHeight(EditorProjectClip clip) => TrackHeightFor(clip.TrackId);
    private double TrackHeightFor(string? id) => Math.Clamp(_tracks.FirstOrDefault(track => track.Id == id)?.Height ?? TrackHeight, 38, 160);
    public bool IsTrackLocked(string? id) => _tracks.FirstOrDefault(track => track.Id == id)?.Locked == true;
    private bool IsClipLocked(EditorProjectClip clip) => IsTrackLocked(clip.TrackId);
    private bool IsAudioLocked(EditorProjectAudioTrack clip) => IsTrackLocked(clip.TrackId);
    private EditorTimelineTrack? TrackAt(double y, EditorTimelineTrackKind? kind = null) => VisualTracks.FirstOrDefault(track =>
        (!kind.HasValue || track.Kind == kind.Value) && y >= PersistentTrackTop(track.Id) && y < PersistentTrackTop(track.Id) + TrackHeightFor(track.Id));

    private void RebuildTrackRows()
    {
        TrackRowsCanvas.Children.Clear();
        _trackRows.Clear();
        foreach (var track in VisualTracks)
        {
            var id = track.Id;
            var row = new Border
            {
                Background = ResourceBrush(track.Kind == EditorTimelineTrackKind.Video ? "KlippSurfaceBrush" : "KlippBackgroundBrush"),
                BorderBrush = ResourceBrush("KlippBorderSubtleBrush"), BorderThickness = new Thickness(0, 0, 0, 1),
                IsHitTestVisible = false
            };
            var header = new Grid
            {
                Background = ResourceBrush("KlippBackgroundBrush"), ColumnSpacing = 2,
                Padding = new Thickness(7, 1, 5, 1)
            };
            header.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            header.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            for (var index = 0; index < 4; index++)
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var label = new TextBlock
            {
                Text = track.Name, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ResourceBrush("KlippTextPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumnSpan(label, 4);
            header.Children.Add(label);
            void AddControl(int column, string text, string help, bool active, Action<EditorTimelineTrack> update, bool icon = false)
            {
                var button = new Button
                {
                    Content = icon ? new FontIcon { Glyph = text, FontSize = 11 } : text,
                    Width = 28, Height = 22, MinWidth = 0, MinHeight = 0,
                    FontSize = 10, Padding = new Thickness(0), CornerRadius = new CornerRadius(3),
                    Background = active ? ResourceBrush("KlippAccentBrush") : ResourceBrush("KlippSurfaceElevatedBrush"),
                    Foreground = ResourceBrush("KlippTextPrimaryBrush")
                };
                ToolTipService.SetToolTip(button, help);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"{help}: {track.Name}");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(button,
                    column == 4 ? "When enabled, ripple edits on other tracks shift this track too. An editing lock always keeps the track fixed." : help);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(button, active ? "On" : "Off");
                button.Click += (_, _) =>
                {
                    var changed = _tracks.First(item => item.Id == id).Clone();
                    update(changed);
                    UpdateTrack(changed);
                };
                Grid.SetRow(button, 1);
                Grid.SetColumn(button, column - 1);
                header.Children.Add(button);
            }
            AddControl(1, "M", "Mute track", track.Muted, item => item.Muted = !item.Muted);
            AddControl(2, "S", "Solo track", track.Solo, item => item.Solo = !item.Solo);
            AddControl(3, "L", "Lock track", track.Locked, item => item.Locked = !item.Locked);
            AddControl(4, "\uE71B", "Sync lock: follow ripple edits", track.SyncLocked, item => item.SyncLocked = !item.SyncLocked, icon: true);
            header.PointerPressed += (_, e) => { _clipDoubleClick.Reset(); _selectedTrackId = id; e.Handled = true; };
            var menu = new MenuFlyout();
            AddMenuAction(menu, "Rename track…", async () =>
            {
                var name = new TextBox { Header = "Track name", Text = track.Name, MaxLength = 40, MinWidth = 250 };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(name, "Track name");
                var dialog = AccessibleDialogFactory.Create(XamlRoot, "Rename track", name,
                    primaryButtonText: "Save", closeButtonText: "Cancel", defaultButton: ContentDialogButton.Primary);
                dialog.Opened += (_, _) => { name.Focus(FocusState.Programmatic); name.SelectAll(); };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(name.Text))
                { var renamed = track.Clone(); renamed.Name = name.Text.Trim(); UpdateTrack(renamed); }
            });
            AddMenuAction(menu, "Move track up", () => MoveTrack(id, -1));
            AddMenuAction(menu, "Move track down", () => MoveTrack(id, 1));
            foreach (var height in new[] { 44d, 72d, 110d })
                AddMenuAction(menu, $"Track height: {height:0}", () => { var resized = track.Clone(); resized.Height = height; UpdateTrack(resized); });
            AddMenuAction(menu, "Remove empty track", () => { _selectedTrackId = id; RemoveSelectedTrack(); });
            header.ContextFlyout = menu;
            TrackRowsCanvas.Children.Add(row);
            TrackRowsCanvas.Children.Add(header);
            _trackRows[id] = (row, header);
        }
    }

    private void DrawPersistentTracks(double totalWidth)
    {
        if (_trackRows.Count != _tracks.Count)
            RebuildTrackRows();
        foreach (var track in VisualTracks)
        {
            var visual = _trackRows[track.Id];
            SetCanvasRect(visual.Row, 0, PersistentTrackTop(track.Id), totalWidth, TrackHeightFor(track.Id));
            SetCanvasRect(visual.Header, 0, PersistentTrackTop(track.Id), HeaderWidth - 3, TrackHeightFor(track.Id));
            visual.Row.Opacity = track.Locked ? 0.6 : 1;
        }
    }

    private void MoveTrack(string id, int visualDelta)
    {
        var track = _tracks.First(item => item.Id == id);
        var peers = VisualTracks.Where(item => item.Kind == track.Kind).ToList();
        var index = peers.FindIndex(item => item.Id == id);
        var targetIndex = index + visualDelta;
        if (targetIndex < 0 || targetIndex >= peers.Count)
            return;
        EditStarting?.Invoke(this, EventArgs.Empty);
        var before = TakeSnapshot();
        (track.Order, peers[targetIndex].Order) = (peers[targetIndex].Order, track.Order);
        CompleteTrackEdit(before);
    }

    private void CompleteTrackEdit(TimelineSnapshot before)
    {
        EnsureTimelineTracks();
        _mediaTimelineStartsDirty = true;
        RebuildTrackRows();
        RebuildMediaClipBlocks();
        RebuildAudioTrackSegmentBlocks();
        UpdateVisuals(true, _playhead, EditorTimelineLayer.None);
        CommitUndoSnapshot(before);
        NotifyTracksChanged();
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, SelectedMediaClipId);
        NotifyAudioTracksChanged(EditorMediaChangeKind.Changed);
    }

    private void NotifyTracksChanged()
    {
        if (_editBatchDepth == 0)
            TracksChanged?.Invoke(this, new EditorTracksChangedEventArgs(GetTracks()));
    }

    private static bool TracksEqual(IReadOnlyList<EditorTimelineTrack> left, IReadOnlyList<EditorTimelineTrack> right) =>
        left.Count == right.Count && left.All(track => right.Any(other => track.Id == other.Id && track.Name == other.Name &&
            track.Kind == other.Kind && track.Order == other.Order && track.Height == other.Height &&
            track.Locked == other.Locked && track.SyncLocked == other.SyncLocked && track.Muted == other.Muted && track.Solo == other.Solo));

    private static bool VideoProcessingEqual(EditorProjectVideoProcessingState? left, EditorProjectVideoProcessingState? right) =>
        ReferenceEquals(left, right) || (left is not null && right is not null &&
            left.OriginalSourcePath == right.OriginalSourcePath && left.BakedSourcePath == right.BakedSourcePath &&
            left.OriginalSourceDurationSeconds == right.OriginalSourceDurationSeconds &&
            left.OriginalTrimStartSeconds == right.OriginalTrimStartSeconds && left.OriginalTrimEndSeconds == right.OriginalTrimEndSeconds &&
            left.OriginalPlaybackSpeed == right.OriginalPlaybackSpeed && left.IsApplied == right.IsApplied &&
            left.RampEnabled == right.RampEnabled && left.RampStartSpeed == right.RampStartSpeed &&
            left.RampMiddleSpeed == right.RampMiddleSpeed && left.RampEndSpeed == right.RampEndSpeed &&
            left.StabilizationStrength == right.StabilizationStrength);
}

public enum EditorTimelineEditTool { Selection, RippleTrim, RollingTrim, Slip }

public sealed class EditorTracksChangedEventArgs(IReadOnlyList<EditorTimelineTrack> tracks) : EventArgs
{
    public IReadOnlyList<EditorTimelineTrack> Tracks { get; } = tracks;
}
