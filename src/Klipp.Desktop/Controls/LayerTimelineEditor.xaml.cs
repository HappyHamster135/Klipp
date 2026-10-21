using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Klipp.Core.Editing;
using Klipp.Desktop.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace Klipp.Desktop.Controls;

public sealed partial class LayerTimelineEditor : UserControl
{
    private const double HeaderWidth = 148;
    private const double RailPaddingRight = 14;
    private const double RulerHeight = 30;
    private const double TrackHeight = 38;
    private const double TrackGap = 4;
    private const double GroupHeaderHeight = 18;
    private const double BlockInset = 3;
    private const double MinClipSeconds = 0.1;
    private const double MinLayerSeconds = 0.25;
    private const double EdgeHitWidth = 12;
    private const double SnapMagnetPixels = 10;
    private const double MinVisibleSeconds = 2;
    private const int MaxHistorySnapshots = 40;
    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".ts", ".mts", ".m2ts"
    };
    public const string LibraryClipDataFormat = "Klipp.Editor.LibraryClip";
    public const string TransitionDataFormat = "Klipp.EditorTransition";
    public const string ClipLookDataFormat = "Klipp.EditorClipLook";

    private readonly Dictionary<EditorTimelineLayer, LayerState> _layers = new();
    private readonly List<EditorProjectClip> _mediaClips = [];
    private readonly Dictionary<string, double> _mediaTimelineStarts = new(StringComparer.Ordinal);
    private readonly List<EditorProjectClip> _primaryClipsInTimelineOrder = [];
    private bool _mediaTimelineStartsDirty = true;
    private readonly List<EditorProjectTextLayer> _subtitleSegments = [];
    private readonly List<EditorProjectTextKeyframe> _textKeyframes = [];
    private readonly List<Border> _textKeyframeMarkers = [];
    private readonly List<EditorProjectGraphicLayer> _graphicSegments = [];
    private readonly Dictionary<string, List<Border>> _graphicKeyframeMarkers = new(StringComparer.Ordinal);
    private readonly List<EditorProjectAudioTrack> _audioTrackSegments = [];
    private readonly List<EditorProjectMarker> _markers = [];
    private readonly Dictionary<string, Border> _mediaClipBlocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _linkedAudioBlocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Image> _linkedAudioWaveforms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource> _sourceAudioWaveforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Border>> _mediaTransformKeyframeMarkers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Border>> _mediaVolumeKeyframeMarkers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Border>> _mediaFilterKeyframeMarkers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _transitionHandleBlocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _subtitleSegmentBlocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _graphicSegmentBlocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Border> _audioTrackSegmentBlocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _audioTrackLanes = new(StringComparer.Ordinal);
    private int _audioTrackLaneCount = 1;
    private readonly Dictionary<string, MarkerVisual> _markerVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource?> _audioTrackWaveforms = new(StringComparer.Ordinal);
    private readonly Dictionary<Border, (Border Left, Border Right)> _clipTrimGrips = new();
    private readonly DispatcherTimer _autoPanTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private Point _lastDragPointer;
    private long _lastAutoPanTick;
    private long _lastExternalDragTick;
    private Point _dragInitialPointer;
    private bool _pointerDragActivated;
    private TimeSpan _dragOriginalDuration;
    private TimeSpan? _dragVisibleDuration;
    private bool _dragRangeEndFollowedDuration;
    private bool _snappingEnabled = true;
    private InputSystemCursorShape _cursorShape = InputSystemCursorShape.Arrow;
    private string? _transitionMoveTargetId;
    private string _dragTransitionStyle = "cut";
    private bool _transitionMoveActivated;
    private double _transitionResizeDirection = -1;
    private List<EditorProjectGraphicLayer>? _dragGraphicCancelSnapshot;
    private readonly HashSet<string> _selectedMediaClipIds = new(StringComparer.Ordinal);
    private readonly List<TimelineSnapshot> _undoHistory = new();
    private readonly List<TimelineSnapshot> _redoHistory = new();
    private DragMode _dragMode = DragMode.None;
    private EditorTimelineLayer _dragLayer = EditorTimelineLayer.None;
    private EditorTimelineLayer _selectedLayer = EditorTimelineLayer.None;
    private TimelineSnapshot? _dragUndoSnapshot;
    private TimeSpan _dragStartTime;
    private TimeSpan _dragEdgePointerOffset;
    private TimeSpan _dragStartLayerStart;
    private TimeSpan _dragStartLayerEnd;
    private TimeSpan _dragStartPlayhead;
    private string? _dragMediaClipId;
    private string? _selectedAudioTrackId;
    private string? _dragAudioTrackId;
    private double _dragAudioTrimStart;
    private double _dragAudioTrimEnd;
    private double _dragAudioTimelineStart;
    private List<EditorProjectClipVolumeKeyframe> _dragAudioVolumeKeyframes = [];
    private string? _dragGraphicSegmentId;
    private string? _selectedGraphicSegmentId;
    private string? _selectedMarkerId;
    private string? _dragMarkerId;
    private TimeSpan _dragMarkerPointerOffset;
    private double _dragMarkerOriginalTime;
    private double _dragGraphicStart;
    private double _dragGraphicEnd;
    private List<double> _dragGraphicKeyframeTimes = [];
    private double _dragMediaTrimStart;
    private double _dragMediaTrimEnd;
    private double _dragMediaTimelineStart;
    private List<EditorProjectClipTransformKeyframe> _dragMediaTransformKeyframes = [];
    private List<EditorProjectClipVolumeKeyframe> _dragMediaVolumeKeyframes = [];
    private List<EditorProjectClipFilterKeyframe> _dragMediaFilterKeyframes = [];
    private double _dragTransitionDuration;
    private double _dragTransitionPointerX;
    private double _dragTransitionSecondsPerPixel;
    private TimelineSnapshot? _curveEditSnapshot;
    private TimeSpan _duration = TimeSpan.FromSeconds(1);
    private TimeSpan _start = TimeSpan.Zero;
    private TimeSpan _end = TimeSpan.FromSeconds(1);
    private TimeSpan _playhead = TimeSpan.Zero;
    private TimeSpan _hoverTime = TimeSpan.Zero;
    private TimeSpan? _snapGuideTime;
    private double _zoomFactor = 1;
    private TimeSpan _viewportDuration = TimeSpan.FromSeconds(1);
    private double _zoomReferenceSeconds = 1;
    private bool _viewportInitialized;
    private bool _usesExplicitMediaTimeline;
    private double _frameRate = 60;
    private string _primaryTransitionStyle = "cut";
    private double _primaryTransitionSeconds;
    private TimeSpan _viewportStart = TimeSpan.Zero;
    private double _lastTickRailWidth = -1;
    private double _lastTickDurationSeconds = -1;
    private double _lastTickMajorSeconds = -1;
    private double _lastTickVisibleStartSeconds = -1;
    private double _lastTickVisibleDurationSeconds = -1;

    public event EventHandler<EditorTimelineChangedEventArgs>? TimelineChanged;
    public event EventHandler<EditorMediaClipsChangedEventArgs>? MediaClipsChanged;
    public event EventHandler<EditorAudioTracksChangedEventArgs>? AudioTracksChanged;
    public event EventHandler<EditorLibraryClipDropEventArgs>? LibraryClipDropped;
    public event EventHandler<EditorGraphicSegmentsChangedEventArgs>? GraphicSegmentsChanged;
    public event EventHandler<EditorGraphicSegmentSelectedEventArgs>? GraphicSegmentSelected;
    public event EventHandler<EditorMarkersChangedEventArgs>? MarkersChanged;
    public event EventHandler<EditorMarkerSelectedEventArgs>? MarkerSelected;
    public event EventHandler? HistoryChanged;
    public event EventHandler? DragCompleted;
    public event EventHandler? ViewportChanged;
    public event EventHandler? EditStarting;
    public event EventHandler<EditorTimelineEditStatusEventArgs>? EditStatusChanged;

    public string ZoomLabel => $"{_zoomFactor:0.#}x";
    public string SnapGridLabel => $"{_frameRate:0.##} fps · {(SnappingEnabled ? "snap on" : "snap off")}";
    public bool SnappingEnabled
    {
        get => _snappingEnabled;
        set
        {
            if (_snappingEnabled == value)
                return;
            _snappingEnabled = value;
            UpdateSnapVisual(null);
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public double FrameRate
    {
        get => _frameRate;
        set
        {
            _frameRate = double.IsFinite(value) ? Math.Clamp(value, 1, 240) : 60;
            ViewportChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public string ActiveLayerLabel => GetLayerDisplayName(_selectedLayer);
    public EditorTimelineLayer ActiveLayer => _selectedLayer;
    public TimeSpan TimelineStep => SnapStep;
    public bool CanUndo => _undoHistory.Count > 0;
    public bool CanRedo => _redoHistory.Count > 0;
    public bool IsDragging => _dragMode != DragMode.None;
    public bool IsScrubbing => _dragMode == DragMode.Playhead;
    public bool LastDragWasSeekOnly { get; private set; }
    public string LastEditStatus { get; private set; } = string.Empty;
    public string? SelectedMediaClipId => _selectedMediaClipIds.Count == 1
        ? _selectedMediaClipIds.First()
        : null;
    public IReadOnlyCollection<string> SelectedMediaClipIds => _selectedMediaClipIds;
    public string? SelectedGraphicSegmentId => _selectedGraphicSegmentId;
    public string? SelectedMarkerId => _selectedMarkerId;
    public string? SelectedAudioTrackId => _selectedAudioTrackId;

    public IReadOnlyList<EditorProjectAudioTrack> GetAudioTrackSegments() =>
        _audioTrackSegments.Select(CloneAudioTrack).ToList();

    public EditorProjectAudioTrack? GetSelectedAudioTrack()
    {
        var track = _audioTrackSegments.FirstOrDefault(item => item.Id == _selectedAudioTrackId);
        return track is null ? null : CloneAudioTrack(track);
    }

    public EditorProjectClip? GetSelectedMediaClip()
    {
        var clip = GetSingleSelectedMediaClip();
        return clip is null ? null : CloneClip(clip);
    }

    public LayerTimelineEditor()
    {
        InitializeComponent();
        EnsureTimelineTracks();
        _autoPanTimer.Tick += OnAutoPanTick;
        Unloaded += (_, _) => _autoPanTimer.Stop();

        _layers[EditorTimelineLayer.Vfx] = new LayerState("VFX", false, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        _layers[EditorTimelineLayer.Text] = new LayerState("Text", false, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        _layers[EditorTimelineLayer.Subtitle] = new LayerState("Subtitle", false, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        _layers[EditorTimelineLayer.Element] = new LayerState("Element", false, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        VfxBlock.Tag = EditorTimelineLayer.Vfx;
        TextBlockClip.Tag = EditorTimelineLayer.Text;
        SubtitleBlock.Tag = EditorTimelineLayer.Subtitle;
        ElementBlock.Tag = EditorTimelineLayer.Element;
        MediaClipsCanvas.Width = 1;
        MediaClipsCanvas.Height = TimelineContentHeight;
        SubtitleSegmentsCanvas.Width = 1;
        SubtitleSegmentsCanvas.Height = TimelineContentHeight;
        GraphicSegmentsCanvas.Width = 1;
        GraphicSegmentsCanvas.Height = TimelineContentHeight;
        AudioTrackSegmentsCanvas.Width = 1;
        AudioTrackSegmentsCanvas.Height = TimelineContentHeight;
        LinkedAudioClipsCanvas.Width = 1;
        LinkedAudioClipsCanvas.Height = TimelineContentHeight;
        TransitionHandlesCanvas.Width = 1;
        TransitionHandlesCanvas.Height = TimelineContentHeight;
        MarkerCanvas.Width = 1;
        MarkerCanvas.Height = TimelineContentHeight;

        Unloaded += (_, _) => StopDragUpdatePump();

        UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.None);
    }

    public IReadOnlyList<EditorProjectClip> GetMediaClips()
    {
        return _mediaClips.Select(CloneClip).ToList();
    }

    /// <summary>Returns the live clip for preview reads. Changes must use the timeline editing APIs.</summary>
    public EditorProjectClip? FindMediaClipById(string? clipId) =>
        clipId is null ? null : _mediaClips.FirstOrDefault(clip => clip.Id == clipId);

    /// <summary>Returns live clips in timeline order for preview reads, without cloning the project.</summary>
    public IReadOnlyList<EditorProjectClip> GetPrimaryClipsForPreview()
    {
        EnsureMediaTimelineStarts();
        return _primaryClipsInTimelineOrder;
    }

    public IReadOnlyList<EditorProjectMarker> GetMarkers()
    {
        return _markers.Select(CloneMarker).ToList();
    }

    public EditorProjectMarker? GetSelectedMarker()
    {
        var marker = _markers.FirstOrDefault(item => item.Id == _selectedMarkerId);
        return marker is null ? null : CloneMarker(marker);
    }

    public bool SelectMarker(string? markerId)
    {
        var marker = _markers.FirstOrDefault(item => item.Id == markerId);
        if (marker is null)
            return false;

        _selectedMarkerId = marker.Id;
        _selectedLayer = EditorTimelineLayer.Marker;
        _playhead = TimeSpan.FromSeconds(marker.TimeSeconds);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Marker);
        MarkerSelected?.Invoke(this, new EditorMarkerSelectedEventArgs(CloneMarker(marker)));
        return true;
    }

    public void SetMarkers(IEnumerable<EditorProjectMarker>? markers, string? selectedMarkerId = null)
    {
        _markers.Clear();
        foreach (var source in markers ?? [])
        {
            var marker = CloneMarker(source);
            marker.Normalize(_duration.TotalSeconds);
            _markers.Add(marker);
        }

        _markers.Sort((left, right) => left.TimeSeconds.CompareTo(right.TimeSeconds));
        _selectedMarkerId = _markers.Any(marker => marker.Id == selectedMarkerId)
            ? selectedMarkerId
            : null;
        RebuildMarkerVisuals();
        UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.None);
    }

    public EditorProjectMarker AddMarker(TimeSpan position)
    {
        var marker = new EditorProjectMarker
        {
            TimeSeconds = ClampTime(position, TimeSpan.Zero, _duration).TotalSeconds,
            Label = $"Marker {_markers.Count + 1}",
            ColorLabel = "yellow"
        };
        marker.Normalize(_duration.TotalSeconds);
        _markers.Add(marker);
        _markers.Sort((left, right) => left.TimeSeconds.CompareTo(right.TimeSeconds));
        _selectedMarkerId = marker.Id;
        _selectedLayer = EditorTimelineLayer.Marker;
        RebuildMarkerVisuals();
        UpdateVisuals(notify: true, previewTime: TimeSpan.FromSeconds(marker.TimeSeconds), changedLayer: EditorTimelineLayer.Marker);
        NotifyMarkersChanged(EditorMarkerChangeKind.Added, marker.Id);
        MarkerSelected?.Invoke(this, new EditorMarkerSelectedEventArgs(CloneMarker(marker)));
        return CloneMarker(marker);
    }

    public int ReplaceBeatMarkers(IEnumerable<double>? timelineSeconds, string? sourceName = null)
    {
        var normalizedTimes = (timelineSeconds ?? [])
            .Where(double.IsFinite)
            .Select(seconds => Math.Clamp(seconds, 0, _duration.TotalSeconds))
            .OrderBy(seconds => seconds)
            .GroupBy(seconds => Math.Round(seconds * 100) / 100)
            .Select(group => group.First())
            .ToList();

        _markers.RemoveAll(marker => string.Equals(marker.Kind, "beat", StringComparison.Ordinal));
        var labelPrefix = string.IsNullOrWhiteSpace(sourceName)
            ? "Beat"
            : $"Beat · {sourceName.Trim()}";
        foreach (var seconds in normalizedTimes)
        {
            var marker = new EditorProjectMarker
            {
                TimeSeconds = seconds,
                Label = labelPrefix,
                ColorLabel = "teal",
                Kind = "beat"
            };
            marker.Normalize(_duration.TotalSeconds);
            _markers.Add(marker);
        }

        _markers.Sort((left, right) => left.TimeSeconds.CompareTo(right.TimeSeconds));
        if (_selectedMarkerId is not null && _markers.All(marker => marker.Id != _selectedMarkerId))
        {
            _selectedMarkerId = null;
            MarkerSelected?.Invoke(this, new EditorMarkerSelectedEventArgs(null));
        }

        RebuildMarkerVisuals();
        UpdateVisuals(notify: false, previewTime: _playhead, changedLayer: EditorTimelineLayer.Marker);
        NotifyMarkersChanged(EditorMarkerChangeKind.Changed, null);
        return normalizedTimes.Count;
    }

    public bool SetSelectedMarkerMetadata(string? label, string? notes, string? colorLabel)
    {
        var marker = _markers.FirstOrDefault(item => item.Id == _selectedMarkerId);
        if (marker is null)
            return false;

        var updated = CloneMarker(marker);
        updated.Label = label ?? string.Empty;
        updated.Notes = notes ?? string.Empty;
        updated.ColorLabel = colorLabel ?? "yellow";
        updated.Normalize(_duration.TotalSeconds);
        if (marker.Label == updated.Label && marker.Notes == updated.Notes && marker.ColorLabel == updated.ColorLabel)
            return false;

        marker.Label = updated.Label;
        marker.Notes = updated.Notes;
        marker.ColorLabel = updated.ColorLabel;
        RebuildMarkerVisuals();
        UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.Marker);
        NotifyMarkersChanged(EditorMarkerChangeKind.Changed, marker.Id);
        return true;
    }

    public bool DeleteSelectedMarker()
    {
        if (string.IsNullOrWhiteSpace(_selectedMarkerId))
            return false;

        var removedId = _selectedMarkerId;
        if (_markers.RemoveAll(marker => marker.Id == removedId) == 0)
            return false;

        _selectedMarkerId = null;
        _selectedLayer = EditorTimelineLayer.Playhead;
        RebuildMarkerVisuals();
        UpdateVisuals(notify: false, previewTime: _playhead, changedLayer: EditorTimelineLayer.Marker);
        NotifyMarkersChanged(EditorMarkerChangeKind.Removed, removedId);
        MarkerSelected?.Invoke(this, new EditorMarkerSelectedEventArgs(null));
        return true;
    }

    public void SetPrimaryTransitionDuration(double seconds)
    {
        SetPrimaryTransition(seconds > 0.001 ? "dissolve" : "cut", seconds);
    }

    public void SetPrimaryTransition(string style, double seconds)
    {
        style = NormalizeTransitionStyle(style, allowInherit: false);
        seconds = Math.Clamp(seconds, 0, 2);
        if (string.Equals(_primaryTransitionStyle, style, StringComparison.Ordinal) &&
            Math.Abs(_primaryTransitionSeconds - seconds) < 0.001)
            return;

        _primaryTransitionStyle = style;
        _primaryTransitionSeconds = seconds;
        RecalculateDurationFromMedia(clearHistory: false);
        RebuildMediaClipBlocks();
        UpdateVisuals(notify: false, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media);
    }

    public void SetSubtitleSegments(IEnumerable<EditorProjectTextLayer> layers)
    {
        _subtitleSegments.Clear();
        _subtitleSegments.AddRange(layers.Select(CloneTextLayer));
        RebuildSubtitleSegmentBlocks();
        UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.Subtitle);
    }

    public void SetTextKeyframes(IEnumerable<EditorProjectTextKeyframe> keyframes)
    {
        _textKeyframes.Clear();
        _textKeyframes.AddRange(keyframes.Select(keyframe => keyframe.Clone()));
        _textKeyframes.Sort((left, right) => left.TimeSeconds.CompareTo(right.TimeSeconds));
        RebuildTextKeyframeMarkers();
        UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.Text);
    }

    public IReadOnlyList<EditorProjectTextKeyframe> GetTextKeyframes() =>
        _textKeyframes.Select(keyframe => keyframe.Clone()).ToList();

    public IReadOnlyList<EditorProjectGraphicLayer> GetGraphicSegments()
    {
        return _graphicSegments.Select(CloneGraphicLayer).ToList();
    }

    public void SetGraphicSegments(
        IEnumerable<EditorProjectGraphicLayer> layers,
        string? selectedSegmentId = null)
    {
        _graphicSegments.Clear();
        _graphicSegments.AddRange(layers.Select(CloneGraphicLayer));
        _selectedGraphicSegmentId = !string.IsNullOrWhiteSpace(selectedSegmentId) &&
                                    _graphicSegments.Any(layer => layer.Id == selectedSegmentId)
            ? selectedSegmentId
            : null;
        RebuildGraphicSegmentBlocks();
        UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.Element);
    }

    public void SelectGraphicSegment(string? segmentId)
    {
        _selectedGraphicSegmentId = !string.IsNullOrWhiteSpace(segmentId) &&
                                    _graphicSegments.Any(layer => layer.Id == segmentId)
            ? segmentId
            : null;
        if (_selectedGraphicSegmentId is not null)
            _selectedLayer = EditorTimelineLayer.Element;
        UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.None);
    }

    public void SetAudioTrackSegments(IEnumerable<EditorProjectAudioTrack> tracks)
    {
        var updatedTracks = tracks.Select(CloneAudioTrack).ToList();
        var timingChanged = _audioTrackSegments.Count != updatedTracks.Count ||
            _audioTrackSegments.Any(previous => !updatedTracks.Any(updated =>
                updated.Id == previous.Id &&
                updated.TimelineStartSeconds == previous.TimelineStartSeconds &&
                updated.TrimStartSeconds == previous.TrimStartSeconds &&
                updated.TrimEndSeconds == previous.TrimEndSeconds &&
                updated.PlaybackSpeed == previous.PlaybackSpeed));
        var previousStart = _start;
        var previousEnd = _end;
        var endFollowedDuration = Math.Abs((_end - _duration).TotalSeconds) < 0.00001;
        _audioTrackSegments.Clear();
        _audioTrackSegments.AddRange(updatedTracks);
        EnsureTimelineTracks();
        _selectedAudioTrackIds.RemoveWhere(id => !_audioTrackSegments.Any(track => track.Id == id));
        var activeIds = _audioTrackSegments.Select(track => track.Id).ToHashSet(StringComparer.Ordinal);
        if (_selectedAudioTrackId is not null && !activeIds.Contains(_selectedAudioTrackId))
            _selectedAudioTrackId = null;
        foreach (var staleId in _audioTrackWaveforms.Keys.Where(id => !activeIds.Contains(id)).ToList())
        {
            _audioTrackWaveforms.Remove(staleId);
            _audioWaveformSpans.Remove(staleId);
        }
        if (timingChanged)
        {
            RecalculateDurationFromMedia(clearHistory: false, allowEmpty: true);
            _start = ClampTime(previousStart, TimeSpan.Zero, _duration - MinimumTrim);
            _end = ClampTime(endFollowedDuration ? _duration : previousEnd, _start + MinimumTrim, _duration);
        }
        RebuildAudioTrackSegmentBlocks();
        UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.Media);
    }

    public void SetAudioTrackWaveform(string trackId, ImageSource? waveform, string sourcePath,
        double sourceStartSeconds, double sourceEndSeconds)
    {
        var track = _audioTrackSegments.FirstOrDefault(track => track.Id == trackId);
        if (track is null || !CanAcceptAudioWaveform(track, sourcePath, sourceStartSeconds, sourceEndSeconds))
            return;

        _audioTrackWaveforms[trackId] = waveform;
        _audioWaveformSpans[trackId] = new AudioWaveformSpan(sourcePath, sourceStartSeconds, sourceEndSeconds);
        // Updating one completed image must not rebuild clip blocks or fade handles
        // underneath an active pointer gesture.
        if (_audioTrackSegmentBlocks.TryGetValue(trackId, out var block))
            DrawIndependentAudioWaveform(track, block.Width, block.Height, GetRailWidth());
    }

    public bool TryGetSelectedMediaClipTransition(
        out string style,
        out double durationSeconds,
        out bool inherited)
    {
        style = _primaryTransitionStyle;
        durationSeconds = _primaryTransitionSeconds;
        inherited = true;
        var clip = GetSingleSelectedMediaClip();
        if (clip is null || GetTransitionPredecessor(clip, includeTouching: true) is null)
            return false;

        inherited = string.Equals(clip.TransitionInStyle, "inherit", StringComparison.OrdinalIgnoreCase);
        style = inherited ? _primaryTransitionStyle : clip.TransitionInStyle;
        durationSeconds = inherited ? _primaryTransitionSeconds : clip.TransitionInDurationSeconds;
        return true;
    }

    public bool SetSelectedMediaClipTransition(string style, double durationSeconds)
    {
        LastEditStatus = string.Empty;
        var clip = GetSingleSelectedMediaClip();
        if (clip is null || IsClipLocked(clip) || GetTransitionPredecessor(clip, includeTouching: true) is null)
            return false;

        style = NormalizeTransitionStyle(style, allowInherit: true);
        durationSeconds = Math.Clamp(durationSeconds, 0.1, 2);
        var predecessor = GetTransitionPredecessor(clip);
        if (string.Equals(clip.TransitionInStyle, style, StringComparison.Ordinal) &&
            (style == "cut" || Math.Abs(clip.TransitionInDurationSeconds - durationSeconds) < 0.001 &&
             predecessor is not null && Math.Abs(GetPrimaryTransitionOverlap(predecessor, clip) - durationSeconds) < 0.001))
        {
            return false;
        }

        var before = TakeSnapshot();
        if (!PrepareTransitionTarget(clip, style, durationSeconds))
            return false;
        clip.TransitionInStyle = style;
        clip.TransitionInDurationSeconds = durationSeconds;
        RecalculateDurationFromMedia(clearHistory: false);
        RebuildMediaClipBlocks();
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public void SetMediaClips(IEnumerable<EditorProjectClip> clips, string? selectedClipId = null)
    {
        _usesExplicitMediaTimeline = true;
        _mediaClips.Clear();
        _mediaClips.AddRange(clips.Select(CloneClip));
        NormalizeMediaClips();
        _selectedMediaClipIds.Clear();
        if (!string.IsNullOrWhiteSpace(selectedClipId) && _mediaClips.Any(clip => clip.Id == selectedClipId))
            _selectedMediaClipIds.Add(selectedClipId);
        else if (_mediaClips.Count > 0)
            _selectedMediaClipIds.Add(_mediaClips[0].Id);

        RecalculateDurationFromMedia(clearHistory: true);
        EnsureTimelineTracks();
        if (!_viewportInitialized)
        {
            _viewportInitialized = true;
            ResetZoom();
        }
        RebuildMediaClipBlocks();
        UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.Media);
    }

    public bool AddMediaClip(EditorProjectClip clip, EditorClipTrack track, TimeSpan timelinePosition)
    {
        if (string.IsNullOrWhiteSpace(clip.SourcePath) || !File.Exists(clip.SourcePath))
            return false;

        var before = TakeSnapshot();
        var added = CloneClip(clip);
        added.Id = Guid.NewGuid().ToString("N");
        added.Track = track;
        if (!_tracks.Any(item => item.Id == added.TrackId && item.Kind == EditorTimelineTrackKind.Video))
            added.TrackId = _tracks.FirstOrDefault(item => item.Id == _selectedTrackId && item.Kind == EditorTimelineTrackKind.Video && !item.Locked)?.Id
                ?? _tracks.Where(item => item.Kind == EditorTimelineTrackKind.Video && !item.Locked).OrderBy(item => item.Order)
                    .Skip(track == EditorClipTrack.Overlay ? 1 : 0).FirstOrDefault()?.Id ?? string.Empty;
        if (string.IsNullOrEmpty(added.TrackId) || IsTrackLocked(added.TrackId))
            return false;
        added.Normalize();

        added.TimelineStartSeconds = Math.Max(0, timelinePosition.TotalSeconds);
        _mediaClips.Add(added);
        EnsureTimelineTracks();

        SelectOnlyMediaClip(added.Id);
        RecalculateDurationFromMedia(clearHistory: false);
        RebuildMediaClipBlocks();
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Added, added.Id);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool DuplicateSelectedMediaClips() => SelectedEffectLayerId is not null ? DuplicateSelectedEffectLayer() : DuplicateMixedSelection();

    public bool SplitSelectedClip() => SplitSelectedClipAt(_playhead);

    private bool SplitSelectedClipAt(TimeSpan splitPosition)
    {
        LastEditStatus = "Place the playhead inside a video clip to split it.";
        if (_mediaClips.Any(item => item.Id == SelectedMediaClipId && IsClipLocked(item)))
        { LastEditStatus = "Unlock the video track before splitting its clips."; return false; }
        var clip = GetSingleSelectedMediaClip();
        if (clip is null || !EditorClipEditMath.Contains(splitPosition.TotalSeconds,
                GetClipTimelineStart(clip), clip.TrimStartSeconds, clip.TrimEndSeconds, clip.PlaybackSpeed))
            clip = FindEditableClipAt(splitPosition);
        if (clip is null)
            return false;

        var timelineStart = GetClipTimelineStart(clip);
        var localTimelineSeconds = splitPosition.TotalSeconds - timelineStart;
        if (!EditorClipEditMath.TrySplitSource(splitPosition.TotalSeconds, timelineStart,
                clip.TrimStartSeconds, clip.TrimEndSeconds, clip.PlaybackSpeed, out var splitSource))
        {
            return false;
        }

        var predecessor = GetTransitionPredecessor(clip);
        var leftPart = CloneClip(clip);
        leftPart.TrimEndSeconds = splitSource;
        var rightPart = CloneClip(clip);
        rightPart.TrimStartSeconds = splitSource;
        rightPart.TimelineStartSeconds = splitPosition.TotalSeconds;
        if ((predecessor is not null && GetPrimaryTransitionOverlap(predecessor, leftPart) + .0001 < GetPrimaryTransitionOverlap(predecessor, clip)) ||
            _mediaClips.Where(incoming => GetTransitionPredecessor(incoming)?.Id == clip.Id).Any(incoming =>
                GetPrimaryTransitionOverlap(rightPart, incoming) + .0001 < GetPrimaryTransitionOverlap(clip, incoming)))
        {
            LastEditStatus = "Move the cut outside the transition overlap or shorten the transition first.";
            return false;
        }

        var before = TakeSnapshot();
        var right = clip.CloneWithNewId();
        // A new edit point must remain a cut even when the project has default transitions.
        right.TransitionInStyle = "cut";
        SplitTransformKeyframes(clip, right, localTimelineSeconds);
        SplitVolumeKeyframes(clip, right, localTimelineSeconds);
        SplitFilterKeyframes(clip, right, localTimelineSeconds);
        right.TrimStartSeconds = splitSource;
        right.TimelineStartSeconds = splitPosition.TotalSeconds;
        clip.TrimEndSeconds = splitSource;
        InvalidateLoudnessAnalysis(clip);
        InvalidateLoudnessAnalysis(right);

        if (clip.Track == EditorClipTrack.Primary)
        {
            var primary = GetPrimaryClips();
            var index = primary.FindIndex(item => item.Id == clip.Id);
            primary.Insert(index + 1, right);
            ReplacePrimaryClips(primary);
        }
        else
        {
            right.TimelineStartSeconds = splitPosition.TotalSeconds;
            _mediaClips.Add(right);
        }

        SelectOnlyMediaClip(right.Id);
        SplitAttachedEffectLayers(clip.Id, right.Id, splitPosition.TotalSeconds);
        RecalculateDurationFromMedia(clearHistory: false);
        RebuildMediaClipBlocks();
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Split, right.Id);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media);
        LastEditStatus = string.Empty;
        return true;
    }

    public bool SplitAtPlayhead() => SplitSelectionAtPosition(_playhead);
    public bool SplitAtPosition(TimeSpan position) => SplitSelectionAtPosition(SnapTime(position));

    public bool DeleteSelection()
    {
        if (SelectedEffectLayerId is not null)
            return DeleteSelectedEffectLayer();
        if (_selectedMarkerId is not null)
            return DeleteSelectedMarker();
        if (SelectedClipCount > 0 && _selectedLayer is EditorTimelineLayer.Media or EditorTimelineLayer.Audio or EditorTimelineLayer.Playhead)
            return DeleteMixedSelection(ripple: false);
        if (_selectedAudioTrackId is not null &&
            _selectedLayer is EditorTimelineLayer.Audio or EditorTimelineLayer.Playhead)
            return DeleteSelectedAudioTrack();
        if (_selectedLayer is EditorTimelineLayer.Media or EditorTimelineLayer.Playhead)
            return DeleteSelectedMediaClips();
        return DeleteSelectedLayer();
    }

    public void SelectAudioTrack(string? trackId)
    {
        _selectedAudioTrackIds.Clear();
        _selectedAudioTrackId = _audioTrackSegments.Any(track => track.Id == trackId) ? trackId : null;
        _selectedTrackId = _audioTrackSegments.FirstOrDefault(track => track.Id == trackId)?.TrackId ?? _selectedTrackId;
        if (_selectedAudioTrackId is not null)
        {
            ClearMarkerSelection();
            _selectedMediaClipIds.Clear();
            _selectedAudioTrackIds.Add(_selectedAudioTrackId);
            _selectedGraphicSegmentId = null;
            _selectedLayer = EditorTimelineLayer.Audio;
        }
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Audio, selectionOnly: true);
    }

    public bool SplitSelectedAudioTrack() => SplitSelectedAudioTrackAt(_playhead);

    private bool SplitSelectedAudioTrackAt(TimeSpan splitPosition)
    {
        LastEditStatus = "Place the playhead inside the selected audio clip to split it.";
        var track = _audioTrackSegments.FirstOrDefault(item => item.Id == _selectedAudioTrackId);
        if (track is null || IsAudioLocked(track) || !EditorClipEditMath.TrySplitSource(splitPosition.TotalSeconds,
                track.TimelineStartSeconds, track.TrimStartSeconds, track.TrimEndSeconds,
                track.PlaybackSpeed, out var sourceSeconds))
            return false;

        var before = TakeSnapshot();
        BakeAudioFades(track);
        var right = CloneAudioTrack(track);
        var splitTime = splitPosition.TotalSeconds - track.TimelineStartSeconds;
        right.VolumeKeyframes = RetimeVolumeKeyframesForStartTrim(track.VolumeKeyframes, track.Volume, splitTime);
        track.VolumeKeyframes = RetimeVolumeKeyframesForEndTrim(track.VolumeKeyframes,
            track.Volume, splitTime, track.EffectiveDurationSeconds);
        right.Id = Guid.NewGuid().ToString("N");
        right.TrimStartSeconds = sourceSeconds;
        right.TimelineStartSeconds = splitPosition.TotalSeconds;
        right.FadeInSeconds = 0;
        track.TrimEndSeconds = sourceSeconds;
        track.FadeOutSeconds = 0;
        InvalidateAudioLoudnessAnalysis(track);
        InvalidateAudioLoudnessAnalysis(right);
        track.Normalize();
        right.Normalize();
        _audioTrackSegments.Insert(_audioTrackSegments.IndexOf(track) + 1, right);
        _selectedAudioTrackId = right.Id;
        _selectedAudioTrackIds.Clear();
        _selectedAudioTrackIds.Add(right.Id);
        _selectedLayer = EditorTimelineLayer.Audio;
        RebuildAudioTrackSegmentBlocks();
        CommitUndoSnapshot(before);
        NotifyAudioTracksChanged(EditorMediaChangeKind.Split);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Audio);
        LastEditStatus = string.Empty;
        return true;
    }

    public bool DeleteSelectedAudioTrack()
    {
        if (_selectedAudioTrackId is null || !_audioTrackSegments.Any(track => track.Id == _selectedAudioTrackId && !IsAudioLocked(track)))
            return false;
        var before = TakeSnapshot();
        _audioTrackSegments.RemoveAll(track => track.Id == _selectedAudioTrackId);
        _selectedAudioTrackIds.Remove(_selectedAudioTrackId);
        _selectedAudioTrackId = null;
        _selectedLayer = EditorTimelineLayer.None;
        RecalculateDurationFromMedia(clearHistory: false);
        RebuildAudioTrackSegmentBlocks();
        CommitUndoSnapshot(before);
        NotifyAudioTracksChanged(EditorMediaChangeKind.Removed);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Audio);
        return true;
    }

    public bool DetachSelectedClipAudio()
    {
        var clip = GetSingleSelectedMediaClip() ?? FindEditableClipAtPlayhead();
        if (clip is null || clip.Muted || clip.SourceAudioSeparated)
            return false;
        var before = TakeSnapshot();
        var track = new EditorProjectAudioTrack
        {
            Name = clip.Name,
            SourcePath = clip.SourcePath,
            SourceClipId = clip.Id,
            SourceDurationSeconds = clip.SourceDurationSeconds,
            TrimStartSeconds = clip.TrimStartSeconds,
            TrimEndSeconds = clip.TrimEndSeconds,
            TimelineStartSeconds = GetClipTimelineStart(clip),
            PlaybackSpeed = clip.PlaybackSpeed,
            Volume = clip.Volume,
            VolumeKeyframes = clip.VolumeKeyframes.Select(keyframe => keyframe.Clone()).ToList(),
            NormalizeAudio = clip.NormalizeAudio,
            NormalizationGainDb = clip.NormalizationGainDb,
            IntegratedLoudnessLufs = clip.IntegratedLoudnessLufs,
            LoudnessAnalysisTrimStartSeconds = clip.LoudnessAnalysisTrimStartSeconds,
            LoudnessAnalysisTrimEndSeconds = clip.LoudnessAnalysisTrimEndSeconds,
            NoiseReductionEnabled = clip.NoiseReductionEnabled,
            NoiseReductionStrength = clip.NoiseReductionStrength
        };
        if (clip.Track == EditorClipTrack.Primary)
        {
            var primary = GetPrimaryClips();
            var index = primary.FindIndex(item => item.Id == clip.Id);
            if (index > 0)
                track.FadeInSeconds = GetPrimaryTransitionOverlap(primary[index - 1], clip);
            if (index >= 0 && index + 1 < primary.Count)
                track.FadeOutSeconds = GetPrimaryTransitionOverlap(clip, primary[index + 1]);
        }
        track.Normalize();
        clip.Muted = true;
        clip.SourceAudioSeparated = true;
        _audioTrackSegments.Add(track);
        _selectedMediaClipIds.Clear();
        _selectedAudioTrackId = track.Id;
        _selectedLayer = EditorTimelineLayer.Audio;
        RebuildMediaClipBlocks();
        RebuildAudioTrackSegmentBlocks();
        CommitUndoSnapshot(before);
        NotifyAudioTracksChanged(EditorMediaChangeKind.Added);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Audio);
        return true;
    }

    private void NotifyAudioTracksChanged(EditorMediaChangeKind kind)
    {
        if (_editBatchDepth == 0)
            AudioTracksChanged?.Invoke(this, new EditorAudioTracksChangedEventArgs(GetAudioTrackSegments(), kind, _selectedAudioTrackId, _playhead));
    }

    private static void InvalidateAudioLoudnessAnalysis(EditorProjectAudioTrack track)
    {
        track.NormalizeAudio = false;
        track.NormalizationGainDb = 0;
        track.IntegratedLoudnessLufs = null;
    }

    private static void BakeAudioFades(EditorProjectAudioTrack track)
    {
        if (track.FadeInSeconds <= 0 && track.FadeOutSeconds <= 0)
            return;
        var duration = track.EffectiveDurationSeconds;
        var original = track.VolumeKeyframes.Select(keyframe => keyframe.Clone()).ToList();
        var boundaries = new SortedSet<double>(original.Select(keyframe => keyframe.TimeSeconds)) { 0, duration };
        if (track.FadeInSeconds > 0)
            boundaries.Add(track.FadeInSeconds);
        if (track.FadeOutSeconds > 0)
            boundaries.Add(duration - track.FadeOutSeconds);
        var ordered = boundaries.ToList();
        var result = new List<EditorProjectClipVolumeKeyframe>();
        double GainAt(double time)
        {
            var gain = EvaluateVolumeKeyframes(original, track.Volume, time);
            if (track.FadeInSeconds > 0)
                gain *= Math.Clamp(time / track.FadeInSeconds, 0, 1);
            if (track.FadeOutSeconds > 0)
                gain *= Math.Clamp((duration - time) / track.FadeOutSeconds, 0, 1);
            return gain;
        }
        for (var index = 1; index < ordered.Count; index++)
        {
            var left = ordered[index - 1];
            var right = ordered[index];
            var samples = Math.Clamp((int)Math.Ceiling((right - left) * 120), 1, 256);
            for (var sample = index == 1 ? 0 : 1; sample <= samples; sample++)
            {
                var time = left + (right - left) * sample / samples;
                result.Add(new EditorProjectClipVolumeKeyframe { TimeSeconds = time, Volume = GainAt(time) });
            }
        }
        var originalTimes = original.Select(key => Math.Round(key.TimeSeconds, 8)).ToHashSet();
        var protectedIndices = result.Select((key, index) => (key, index))
            .Where(item => originalTimes.Contains(Math.Round(item.key.TimeSeconds, 8)))
            .Select(item => item.index).ToHashSet();
        track.VolumeKeyframes = EditorAdvancedEditMath.SimplifyLinearSamples(
            result.Select(key => (key.TimeSeconds, key.Volume)).ToList(), protectedIndices)
            .Select(index => result[index]).ToList();
        track.FadeInSeconds = 0;
        track.FadeOutSeconds = 0;
    }

    private static void SplitTransformKeyframes(
        EditorProjectClip left,
        EditorProjectClip right,
        double splitTimeSeconds)
    {
        if (left.TransformKeyframes.Count == 0)
            return;

        var original = left.TransformKeyframes
            .OrderBy(keyframe => keyframe.TimeSeconds)
            .Select(keyframe => keyframe.Clone())
            .ToList();
        var fallback = new EditorProjectClipTransformState(left.Scale, left.OffsetX, left.OffsetY);
        right.TransformKeyframes = RetimeTransformKeyframesForStartTrim(original, fallback, splitTimeSeconds);
        left.TransformKeyframes = RetimeTransformKeyframesForEndTrim(original, fallback, splitTimeSeconds, left.EffectiveDurationSeconds);
    }

    private static void SplitVolumeKeyframes(
        EditorProjectClip left,
        EditorProjectClip right,
        double splitTimeSeconds)
    {
        if (left.VolumeKeyframes.Count == 0)
            return;

        var original = left.VolumeKeyframes
            .OrderBy(keyframe => keyframe.TimeSeconds)
            .Select(keyframe => keyframe.Clone())
            .ToList();
        right.VolumeKeyframes = RetimeVolumeKeyframesForStartTrim(original, left.Volume, splitTimeSeconds);
        left.VolumeKeyframes = RetimeVolumeKeyframesForEndTrim(original, left.Volume,
            splitTimeSeconds, left.EffectiveDurationSeconds);
    }

    private static void SplitFilterKeyframes(
        EditorProjectClip left,
        EditorProjectClip right,
        double splitTimeSeconds)
    {
        if (left.FilterStrengthKeyframes.Count == 0)
            return;

        var original = left.FilterStrengthKeyframes
            .OrderBy(keyframe => keyframe.TimeSeconds)
            .Select(keyframe => keyframe.Clone())
            .ToList();
        right.FilterStrengthKeyframes = RetimeFilterKeyframesForStartTrim(original, left.FilterStrength, splitTimeSeconds);
        left.FilterStrengthKeyframes = RetimeFilterKeyframesForEndTrim(original, left.FilterStrength, splitTimeSeconds, left.EffectiveDurationSeconds);
    }

    public bool DeleteSelectedMediaClips() => DeleteMixedSelection(ripple: false);

    public bool DeleteSelectedLayer() => TryRemoveSelectedLayer();

    public bool SetSelectedMediaClipPlaybackSpeed(double speed)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        speed = Math.Clamp(speed, 0.5, 2);
        if (Math.Abs(clip.PlaybackSpeed - speed) < 0.001)
            return false;

        var before = TakeSnapshot();
        clip.PlaybackSpeed = speed;
        RecalculateDurationFromMedia(clearHistory: false);
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool SetSelectedMediaClipAudio(double volume, bool muted)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        volume = Math.Clamp(volume, 0, 2);
        if (Math.Abs(clip.Volume - volume) < 0.001 && clip.Muted == muted)
            return false;

        var before = TakeSnapshot();
        clip.Volume = volume;
        clip.Muted = muted;
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool SetSelectedMediaClipNormalization(bool enabled, double gainDb, double? integratedLoudnessLufs)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        gainDb = Math.Clamp(double.IsFinite(gainDb) ? gainDb : 0, -24, 12);
        integratedLoudnessLufs = integratedLoudnessLufs is { } lufs && double.IsFinite(lufs)
            ? lufs
            : null;
        if (clip.NormalizeAudio == enabled &&
            Math.Abs(clip.NormalizationGainDb - gainDb) < 0.001 &&
            Nullable.Equals(clip.IntegratedLoudnessLufs, integratedLoudnessLufs))
        {
            return false;
        }

        var before = TakeSnapshot();
        clip.NormalizeAudio = enabled;
        clip.NormalizationGainDb = gainDb;
        clip.IntegratedLoudnessLufs = integratedLoudnessLufs;
        if (integratedLoudnessLufs is not null)
        {
            clip.LoudnessAnalysisTrimStartSeconds = clip.TrimStartSeconds;
            clip.LoudnessAnalysisTrimEndSeconds = clip.TrimEndSeconds;
        }
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool UpsertSelectedMediaClipVolumeKeyframe(
        TimeSpan projectPosition,
        double volume,
        string? easing = null)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        var localTime = GetMediaClipLocalTime(clip.Id, projectPosition);
        volume = Math.Clamp(volume, 0, 2);
        var before = TakeSnapshot();
        if (clip.VolumeKeyframes.Count == 0 && localTime > 0.001)
        {
            clip.VolumeKeyframes.Add(new EditorProjectClipVolumeKeyframe
            {
                TimeSeconds = 0,
                Volume = clip.Volume,
                Easing = "linear"
            });
        }

        var keyframe = FindVolumeKeyframe(clip, localTime);
        if (keyframe is null)
        {
            keyframe = new EditorProjectClipVolumeKeyframe { TimeSeconds = localTime };
            clip.VolumeKeyframes.Add(keyframe);
        }

        keyframe.Volume = volume;
        if (!string.IsNullOrWhiteSpace(easing))
            keyframe.Easing = easing;
        clip.Normalize();
        RebuildMediaClipBlocks();
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: false, previewTime: projectPosition, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool RemoveSelectedMediaClipVolumeKeyframe(TimeSpan projectPosition)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null || clip.VolumeKeyframes.Count == 0)
            return false;

        var keyframe = FindVolumeKeyframe(
            clip,
            GetMediaClipLocalTime(clip.Id, projectPosition));
        if (keyframe is null)
            return false;

        var before = TakeSnapshot();
        var removedVolume = keyframe.Volume;
        clip.VolumeKeyframes.Remove(keyframe);
        if (clip.VolumeKeyframes.Count == 0)
            clip.Volume = removedVolume;
        clip.Normalize();
        RebuildMediaClipBlocks();
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: false, previewTime: projectPosition, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool ClearSelectedMediaClipVolumeKeyframes(
        TimeSpan projectPosition,
        bool preserveCurrentValue)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null || clip.VolumeKeyframes.Count == 0)
            return false;

        var before = TakeSnapshot();
        if (preserveCurrentValue)
        {
            clip.Volume = clip.EvaluateVolume(
                GetMediaClipLocalTime(clip.Id, projectPosition));
        }
        clip.VolumeKeyframes.Clear();
        RebuildMediaClipBlocks();
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: false, previewTime: projectPosition, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool SetSelectedMediaClipVolumeKeyframeEasing(
        TimeSpan projectPosition,
        string easing)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        var keyframe = FindVolumeKeyframe(
            clip,
            GetMediaClipLocalTime(clip.Id, projectPosition));
        if (keyframe is null)
            return false;

        var normalized = new EditorProjectClipVolumeKeyframe { Easing = easing };
        normalized.Normalize(clip.EffectiveDurationSeconds);
        if (keyframe.Easing == normalized.Easing)
            return false;

        var before = TakeSnapshot();
        keyframe.Easing = normalized.Easing;
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        return true;
    }

    public bool TryGetSelectedMediaClipVolumeKeyframePosition(
        TimeSpan projectPosition,
        bool next,
        out TimeSpan target)
    {
        target = projectPosition;
        var clip = GetSingleSelectedMediaClip();
        if (clip is null || clip.VolumeKeyframes.Count == 0)
            return false;

        var localTime = GetMediaClipLocalTime(clip.Id, projectPosition);
        var ordered = clip.VolumeKeyframes.OrderBy(keyframe => keyframe.TimeSeconds).ToList();
        var keyframe = next
            ? ordered.FirstOrDefault(candidate => candidate.TimeSeconds > localTime + 0.001)
            : ordered.LastOrDefault(candidate => candidate.TimeSeconds < localTime - 0.001);
        keyframe ??= next ? ordered[^1] : ordered[0];
        target = TimeSpan.FromSeconds(GetClipTimelineStart(clip) + keyframe.TimeSeconds);
        return true;
    }

    private static EditorProjectClipVolumeKeyframe? FindVolumeKeyframe(
        EditorProjectClip clip,
        double localTime)
    {
        return clip.VolumeKeyframes
            .OrderBy(keyframe => Math.Abs(keyframe.TimeSeconds - localTime))
            .FirstOrDefault(keyframe => Math.Abs(keyframe.TimeSeconds - localTime) <= 1.0 / 60.0);
    }

    public bool SetSelectedMediaClipFilterStrength(double strength)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        strength = Math.Clamp(strength, 0, 1);
        if (Math.Abs(clip.FilterStrength - strength) < 0.001)
            return false;

        var before = TakeSnapshot();
        clip.FilterStrength = strength;
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool UpsertSelectedMediaClipFilterKeyframe(
        TimeSpan projectPosition,
        double strength,
        string? easing = null)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        var localTime = GetMediaClipLocalTime(clip.Id, projectPosition);
        strength = Math.Clamp(strength, 0, 1);
        var before = TakeSnapshot();
        if (clip.FilterStrengthKeyframes.Count == 0 && localTime > 0.001)
        {
            clip.FilterStrengthKeyframes.Add(new EditorProjectClipFilterKeyframe
            {
                TimeSeconds = 0,
                Strength = clip.FilterStrength,
                Easing = "linear"
            });
        }

        var keyframe = FindFilterKeyframe(clip, localTime);
        if (keyframe is null)
        {
            keyframe = new EditorProjectClipFilterKeyframe { TimeSeconds = localTime };
            clip.FilterStrengthKeyframes.Add(keyframe);
        }

        keyframe.Strength = strength;
        if (!string.IsNullOrWhiteSpace(easing))
            keyframe.Easing = easing;
        clip.Normalize();
        RebuildMediaClipBlocks();
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: false, previewTime: projectPosition, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool RemoveSelectedMediaClipFilterKeyframe(TimeSpan projectPosition)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null || clip.FilterStrengthKeyframes.Count == 0)
            return false;

        var keyframe = FindFilterKeyframe(
            clip,
            GetMediaClipLocalTime(clip.Id, projectPosition));
        if (keyframe is null)
            return false;

        var before = TakeSnapshot();
        var removedStrength = keyframe.Strength;
        clip.FilterStrengthKeyframes.Remove(keyframe);
        if (clip.FilterStrengthKeyframes.Count == 0)
            clip.FilterStrength = removedStrength;
        clip.Normalize();
        RebuildMediaClipBlocks();
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: false, previewTime: projectPosition, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool ClearSelectedMediaClipFilterKeyframes(
        TimeSpan projectPosition,
        bool preserveCurrentValue)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null || clip.FilterStrengthKeyframes.Count == 0)
            return false;

        var before = TakeSnapshot();
        if (preserveCurrentValue)
        {
            clip.FilterStrength = clip.EvaluateFilterStrength(
                GetMediaClipLocalTime(clip.Id, projectPosition));
        }
        clip.FilterStrengthKeyframes.Clear();
        RebuildMediaClipBlocks();
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: false, previewTime: projectPosition, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool SetSelectedMediaClipFilterKeyframeEasing(
        TimeSpan projectPosition,
        string easing)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        var keyframe = FindFilterKeyframe(
            clip,
            GetMediaClipLocalTime(clip.Id, projectPosition));
        if (keyframe is null)
            return false;

        var normalized = new EditorProjectClipFilterKeyframe { Easing = easing };
        normalized.Normalize(clip.EffectiveDurationSeconds);
        if (keyframe.Easing == normalized.Easing)
            return false;

        var before = TakeSnapshot();
        keyframe.Easing = normalized.Easing;
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        return true;
    }

    public bool TryGetSelectedMediaClipFilterKeyframePosition(
        TimeSpan projectPosition,
        bool next,
        out TimeSpan target)
    {
        target = projectPosition;
        var clip = GetSingleSelectedMediaClip();
        if (clip is null || clip.FilterStrengthKeyframes.Count == 0)
            return false;

        var localTime = GetMediaClipLocalTime(clip.Id, projectPosition);
        var ordered = clip.FilterStrengthKeyframes.OrderBy(keyframe => keyframe.TimeSeconds).ToList();
        var keyframe = next
            ? ordered.FirstOrDefault(candidate => candidate.TimeSeconds > localTime + 0.001)
            : ordered.LastOrDefault(candidate => candidate.TimeSeconds < localTime - 0.001);
        keyframe ??= next ? ordered[^1] : ordered[0];
        target = TimeSpan.FromSeconds(GetClipTimelineStart(clip) + keyframe.TimeSeconds);
        return true;
    }

    private static EditorProjectClipFilterKeyframe? FindFilterKeyframe(
        EditorProjectClip clip,
        double localTime)
    {
        return clip.FilterStrengthKeyframes
            .OrderBy(keyframe => Math.Abs(keyframe.TimeSeconds - localTime))
            .FirstOrDefault(keyframe => Math.Abs(keyframe.TimeSeconds - localTime) <= 1.0 / 60.0);
    }

    public bool SetSelectedMediaClipMetadata(string? notes, string? colorLabel)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        var normalizedNotes = notes?.Trim() ?? string.Empty;
        if (normalizedNotes.Length > 4000)
            normalizedNotes = normalizedNotes[..4000];
        var normalizedColor = EditorProjectColorLabels.Normalize(colorLabel);
        if (clip.Notes == normalizedNotes && clip.ColorLabel == normalizedColor)
            return false;

        clip.Notes = normalizedNotes;
        clip.ColorLabel = normalizedColor;
        RebuildMediaClipBlocks();
        UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.Media);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        return true;
    }

    public bool SetSelectedMediaClipTransform(double scale, double offsetX, double offsetY)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        scale = Math.Clamp(scale, 0.1, 4);
        offsetX = Math.Clamp(offsetX, -1, 1);
        offsetY = Math.Clamp(offsetY, -1, 1);
        if (Math.Abs(clip.Scale - scale) < 0.001 &&
            Math.Abs(clip.OffsetX - offsetX) < 0.001 &&
            Math.Abs(clip.OffsetY - offsetY) < 0.001)
        {
            return false;
        }

        var before = TakeSnapshot();
        clip.Scale = scale;
        clip.OffsetX = offsetX;
        clip.OffsetY = offsetY;
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public double GetMediaClipLocalTime(string clipId, TimeSpan projectPosition)
    {
        var clip = _mediaClips.FirstOrDefault(item => item.Id == clipId);
        if (clip is null)
            return 0;

        return Math.Clamp(
            projectPosition.TotalSeconds - GetClipTimelineStart(clip),
            0,
            clip.EffectiveDurationSeconds);
    }

    public bool UpsertSelectedMediaClipTransformKeyframe(
        TimeSpan projectPosition,
        double scale,
        double offsetX,
        double offsetY,
        string? easing = null)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        var localTime = GetMediaClipLocalTime(clip.Id, projectPosition);
        scale = Math.Clamp(scale, 0.1, 4);
        offsetX = Math.Clamp(offsetX, -1, 1);
        offsetY = Math.Clamp(offsetY, -1, 1);
        var before = TakeSnapshot();
        if (clip.TransformKeyframes.Count == 0 && localTime > 0.001)
        {
            clip.TransformKeyframes.Add(new EditorProjectClipTransformKeyframe
            {
                TimeSeconds = 0,
                Scale = clip.Scale,
                OffsetX = clip.OffsetX,
                OffsetY = clip.OffsetY,
                Easing = "linear"
            });
        }

        var keyframe = FindTransformKeyframe(clip, localTime);
        if (keyframe is null)
        {
            keyframe = new EditorProjectClipTransformKeyframe { TimeSeconds = localTime };
            clip.TransformKeyframes.Add(keyframe);
        }

        keyframe.Scale = scale;
        keyframe.OffsetX = offsetX;
        keyframe.OffsetY = offsetY;
        if (!string.IsNullOrWhiteSpace(easing))
            keyframe.Easing = easing;
        clip.Normalize();
        RebuildMediaClipBlocks();
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: false, previewTime: projectPosition, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool RemoveSelectedMediaClipTransformKeyframe(TimeSpan projectPosition)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null || clip.TransformKeyframes.Count == 0)
            return false;

        var localTime = GetMediaClipLocalTime(clip.Id, projectPosition);
        var keyframe = FindTransformKeyframe(clip, localTime);
        if (keyframe is null)
            return false;

        var before = TakeSnapshot();
        var removedState = keyframe.ToState();
        clip.TransformKeyframes.Remove(keyframe);
        if (clip.TransformKeyframes.Count == 0)
        {
            clip.Scale = removedState.Scale;
            clip.OffsetX = removedState.OffsetX;
            clip.OffsetY = removedState.OffsetY;
        }
        clip.Normalize();
        RebuildMediaClipBlocks();
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: false, previewTime: projectPosition, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool ClearSelectedMediaClipTransformKeyframes(TimeSpan projectPosition, bool preserveCurrentState)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null || clip.TransformKeyframes.Count == 0)
            return false;

        var before = TakeSnapshot();
        if (preserveCurrentState)
        {
            var state = clip.EvaluateTransform(GetMediaClipLocalTime(clip.Id, projectPosition));
            clip.Scale = state.Scale;
            clip.OffsetX = state.OffsetX;
            clip.OffsetY = state.OffsetY;
        }
        clip.TransformKeyframes.Clear();
        RebuildMediaClipBlocks();
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: false, previewTime: projectPosition, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool SetSelectedMediaClipTransformKeyframeEasing(TimeSpan projectPosition, string easing)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        var keyframe = FindTransformKeyframe(clip, GetMediaClipLocalTime(clip.Id, projectPosition));
        if (keyframe is null)
            return false;

        var normalized = new EditorProjectClipTransformKeyframe { Easing = easing };
        normalized.Normalize(clip.EffectiveDurationSeconds);
        if (keyframe.Easing == normalized.Easing)
            return false;

        var before = TakeSnapshot();
        keyframe.Easing = normalized.Easing;
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        return true;
    }

    public bool TryGetSelectedMediaClipTransformKeyframePosition(
        TimeSpan projectPosition,
        bool next,
        out TimeSpan target)
    {
        target = projectPosition;
        var clip = GetSingleSelectedMediaClip();
        if (clip is null || clip.TransformKeyframes.Count == 0)
            return false;

        var localTime = GetMediaClipLocalTime(clip.Id, projectPosition);
        var ordered = clip.TransformKeyframes.OrderBy(keyframe => keyframe.TimeSeconds).ToList();
        var keyframe = next
            ? ordered.FirstOrDefault(candidate => candidate.TimeSeconds > localTime + 0.001)
            : ordered.LastOrDefault(candidate => candidate.TimeSeconds < localTime - 0.001);
        keyframe ??= next ? ordered[^1] : ordered[0];
        target = TimeSpan.FromSeconds(GetClipTimelineStart(clip) + keyframe.TimeSeconds);
        return true;
    }

    private static EditorProjectClipTransformKeyframe? FindTransformKeyframe(
        EditorProjectClip clip,
        double localTime)
    {
        return clip.TransformKeyframes
            .OrderBy(keyframe => Math.Abs(keyframe.TimeSeconds - localTime))
            .FirstOrDefault(keyframe => Math.Abs(keyframe.TimeSeconds - localTime) <= 1.0 / 60.0);
    }

    public bool SetSelectedMediaClipCrop(double left, double top, double right, double bottom)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        left = Math.Clamp(left, 0, 0.9);
        top = Math.Clamp(top, 0, 0.9);
        right = Math.Clamp(right, 0, 0.9 - left);
        bottom = Math.Clamp(bottom, 0, 0.9 - top);
        if (Math.Abs(clip.CropLeft - left) < 0.001 &&
            Math.Abs(clip.CropTop - top) < 0.001 &&
            Math.Abs(clip.CropRight - right) < 0.001 &&
            Math.Abs(clip.CropBottom - bottom) < 0.001)
        {
            return false;
        }

        var before = TakeSnapshot();
        clip.CropLeft = left;
        clip.CropTop = top;
        clip.CropRight = right;
        clip.CropBottom = bottom;
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool SetSelectedMediaClipMask(string? shape, double roundness)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        var normalizedShape = shape?.Trim().ToLowerInvariant() switch
        {
            "rounded" => "rounded",
            "ellipse" => "ellipse",
            _ => "none"
        };
        roundness = Math.Clamp(roundness, 0, 0.5);
        if (string.Equals(clip.MaskShape, normalizedShape, StringComparison.Ordinal) &&
            Math.Abs(clip.MaskRoundness - roundness) < 0.001)
        {
            return false;
        }

        var before = TakeSnapshot();
        clip.MaskShape = normalizedShape;
        clip.MaskRoundness = roundness;
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool SetSelectedMediaClipNoiseReduction(bool enabled, double strength)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        strength = Math.Clamp(strength, 0, 1);
        if (clip.NoiseReductionEnabled == enabled &&
            Math.Abs(clip.NoiseReductionStrength - strength) < 0.001)
        {
            return false;
        }

        var before = TakeSnapshot();
        clip.NoiseReductionEnabled = enabled;
        clip.NoiseReductionStrength = strength;
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        return true;
    }

    public bool SetSelectedMediaClipFilter(string filter)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        filter = string.IsNullOrWhiteSpace(filter) ? "none" : filter;
        var lookStage = clip.Effects.FirstOrDefault(stage => stage.Kind == "look");
        if (string.Equals(clip.Filter, filter, StringComparison.OrdinalIgnoreCase) &&
            (filter == "none" || lookStage?.Enabled != false))
            return false;

        var before = TakeSnapshot();
        clip.Filter = filter;
        if (filter != "none" && lookStage is not null) lookStage.Enabled = true;
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool SetSelectedMediaClipFilterAdjustments(
        double brightness,
        double contrast,
        double saturation,
        double temperature,
        double shadows,
        double highlights,
        double gamma,
        double hueDegrees,
        double sharpen,
        double blur,
        double vignette)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        brightness = Math.Clamp(brightness, -1, 1);
        contrast = Math.Clamp(contrast, 0.5, 2);
        saturation = Math.Clamp(saturation, 0, 3);
        temperature = Math.Clamp(temperature, -1, 1);
        shadows = Math.Clamp(shadows, -1, 1);
        highlights = Math.Clamp(highlights, -1, 1);
        gamma = Math.Clamp(gamma, 0.25, 4);
        hueDegrees = Math.Clamp(hueDegrees, -180, 180);
        sharpen = Math.Clamp(sharpen, 0, 2);
        blur = Math.Clamp(blur, 0, 10);
        vignette = Math.Clamp(vignette, 0, 1);
        if (Math.Abs(clip.Brightness - brightness) < 0.001 &&
            Math.Abs(clip.Contrast - contrast) < 0.001 &&
            Math.Abs(clip.Saturation - saturation) < 0.001 &&
            Math.Abs(clip.Temperature - temperature) < 0.001 &&
            Math.Abs(clip.Shadows - shadows) < 0.001 &&
            Math.Abs(clip.Highlights - highlights) < 0.001 &&
            Math.Abs(clip.Gamma - gamma) < 0.001 &&
            Math.Abs(clip.HueDegrees - hueDegrees) < 0.001 &&
            Math.Abs(clip.Sharpen - sharpen) < 0.001 &&
            Math.Abs(clip.Blur - blur) < 0.001 &&
            Math.Abs(clip.Vignette - vignette) < 0.001)
        {
            return false;
        }

        var before = TakeSnapshot();
        clip.Brightness = brightness;
        clip.Contrast = contrast;
        clip.Saturation = saturation;
        clip.Temperature = temperature;
        clip.Shadows = shadows;
        clip.Highlights = highlights;
        clip.Gamma = gamma;
        clip.HueDegrees = hueDegrees;
        clip.Sharpen = sharpen;
        clip.Blur = blur;
        clip.Vignette = vignette;
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public void BeginSelectedMediaClipCurveEdit()
    {
        if (_curveEditSnapshot is null && GetSingleSelectedMediaClip() is not null)
            _curveEditSnapshot = TakeSnapshot();
    }

    public bool PreviewSelectedMediaClipToneCurve(IEnumerable<EditorProjectCurvePoint> points)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        var normalized = CloneCurvePoints(points);
        if (CurvePointsEqual(clip.ToneCurve, normalized))
            return false;

        clip.ToneCurve = normalized;
        clip.Normalize();
        UpdateVisuals(notify: false, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool CommitSelectedMediaClipToneCurve(IEnumerable<EditorProjectCurvePoint> points)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
        {
            _curveEditSnapshot = null;
            return false;
        }

        var before = _curveEditSnapshot ?? TakeSnapshot();
        _curveEditSnapshot = null;
        clip.ToneCurve = CloneCurvePoints(points);
        clip.Normalize();
        var previous = before.MediaClips.FirstOrDefault(candidate => candidate.Id == clip.Id);
        if (previous is not null && CurvePointsEqual(previous.ToneCurve, clip.ToneCurve))
            return false;

        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool SetSelectedMediaClipToneCurve(IEnumerable<EditorProjectCurvePoint> points)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        var normalized = CloneCurvePoints(points);
        if (CurvePointsEqual(clip.ToneCurve, normalized))
            return false;

        var before = TakeSnapshot();
        clip.ToneCurve = normalized;
        clip.Normalize();
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public bool SetSelectedMediaClipLut(string? lutPath, double strength)
    {
        var clip = GetSingleSelectedMediaClip();
        if (clip is null)
            return false;

        lutPath = lutPath?.Trim() ?? string.Empty;
        strength = Math.Clamp(strength, 0, 1);
        if (string.Equals(clip.LutPath, lutPath, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs(clip.LutStrength - strength) < 0.001)
        {
            return false;
        }

        var before = TakeSnapshot();
        clip.LutPath = lutPath;
        clip.LutStrength = strength;
        CommitUndoSnapshot(before);
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, clip.Id);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media);
        return true;
    }

    public TimeSpan Duration
    {
        get => _duration;
        set
        {
            var duration = value > TimeSpan.Zero ? value : TimeSpan.FromSeconds(1);
            if (Math.Abs((duration - _duration).TotalSeconds) < 0.00001)
                return;
            _duration = duration;
            _start = ClampTime(_start, TimeSpan.Zero, _duration - MinimumTrim);
            _end = ClampTime(_end, _start + MinimumTrim, _duration);

            foreach (var layer in _layers.Values)
                ClampLayer(layer);
            foreach (var marker in _markers)
                marker.Normalize(_duration.TotalSeconds);

            InvalidateTicks();
            UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.None);
            ClearHistory();
        }
    }

    public TimeSpan Start
    {
        get => _start;
        set
        {
            _start = ClampTime(value, TimeSpan.Zero, _end - MinimumTrim);
            UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Trim);
        }
    }

    public TimeSpan End
    {
        get => _end;
        set
        {
            _end = ClampTime(value, _start + MinimumTrim, _duration);
            UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Trim);
        }
    }

    public TimeSpan Playhead
    {
        get => _playhead;
        set
        {
            _playhead = value < TimeSpan.Zero ? TimeSpan.Zero : value;
            UpdatePlayheadVisual();
        }
    }

    public bool TextEnabled => IsLayerEnabled(EditorTimelineLayer.Text);
    public bool VfxEnabled => IsLayerEnabled(EditorTimelineLayer.Vfx);
    public bool SubtitleEnabled => IsLayerEnabled(EditorTimelineLayer.Subtitle);
    public bool ElementEnabled => IsLayerEnabled(EditorTimelineLayer.Element);

    public TimeSpan TextStart => GetLayer(EditorTimelineLayer.Text).Start;
    public TimeSpan TextEnd => GetLayer(EditorTimelineLayer.Text).End;
    public TimeSpan VfxStart => GetLayer(EditorTimelineLayer.Vfx).Start;
    public TimeSpan VfxEnd => GetLayer(EditorTimelineLayer.Vfx).End;
    public TimeSpan SubtitleStart => GetLayer(EditorTimelineLayer.Subtitle).Start;
    public TimeSpan SubtitleEnd => GetLayer(EditorTimelineLayer.Subtitle).End;
    public TimeSpan ElementStart => GetLayer(EditorTimelineLayer.Element).Start;
    public TimeSpan ElementEnd => GetLayer(EditorTimelineLayer.Element).End;

    public void ZoomIn()
    {
        SetZoom(_zoomFactor * 1.5);
    }

    public void ZoomOut()
    {
        SetZoom(_zoomFactor / 1.5);
    }

    public void ResetZoom()
    {
        _clipDoubleClick.Reset();
        _viewportDuration = TimeSpan.FromSeconds(Math.Max(0.1, _duration.TotalSeconds));
        _zoomReferenceSeconds = _viewportDuration.TotalSeconds;
        _zoomFactor = 1;
        _viewportStart = TimeSpan.Zero;
        InvalidateTicks();
        UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.None);
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Undo()
    {
        if (_undoHistory.Count == 0)
            return false;

        var previous = _undoHistory[^1];
        _undoHistory.RemoveAt(_undoHistory.Count - 1);
        _redoHistory.Add(TakeSnapshot());
        RestoreSnapshot(previous);
        NotifyHistoryChanged();
        return true;
    }

    public bool Redo()
    {
        if (_redoHistory.Count == 0)
            return false;

        var next = _redoHistory[^1];
        _redoHistory.RemoveAt(_redoHistory.Count - 1);
        _undoHistory.Add(TakeSnapshot());
        RestoreSnapshot(next);
        NotifyHistoryChanged();
        return true;
    }

    public void ClearHistory()
    {
        _undoHistory.Clear();
        _redoHistory.Clear();
        _dragUndoSnapshot = null;
        NotifyHistoryChanged();
    }

    /// <summary>
    /// Sätter (eller nollställer) waveform-bilden som visas bakom audio-blocket.
    /// Bilden stretchas över blockets aktuella rektangel — inte zoom-exakt, men ger
    /// en tydlig visuell indikation på ljudinnehåll (Premiere/Medal-känsla).
    /// </summary>
    public void SetAudioWaveform(BitmapImage? bitmap, string? sourcePath = null)
    {
        AudioWaveform.Source = bitmap;
        if (bitmap is not null && !string.IsNullOrWhiteSpace(sourcePath))
            _sourceAudioWaveforms[sourcePath] = bitmap;
        UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.None);
    }

    public void ResetTrimRange()
    {
        var before = TakeSnapshot();
        _start = TimeSpan.Zero;
        _end = _duration;
        _selectedLayer = EditorTimelineLayer.Trim;
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Trim);
        CommitUndoSnapshot(before);
    }

    private void SetZoom(double zoomFactor, double? pointerX = null)
    {
        _clipDoubleClick.Reset();
        var oldVisibleDuration = VisibleDuration;
        var anchor = pointerX is double x ? TimeFromX(x) : _playhead;
        var ratio = pointerX is double pointer
            ? Math.Clamp((pointer - HeaderWidth) / GetRailWidth(), 0, 1)
            : Math.Clamp((anchor - VisibleStart).TotalSeconds / Math.Max(0.001, oldVisibleDuration.TotalSeconds), 0, 1);
        var maxZoom = Math.Max(1, _zoomReferenceSeconds / MinVisibleSeconds);
        var newZoom = Math.Clamp(zoomFactor, 1, maxZoom);
        if (Math.Abs(newZoom - _zoomFactor) < 0.00001)
            return;
        _zoomFactor = newZoom;
        _viewportDuration = TimeSpan.FromSeconds(_zoomReferenceSeconds / newZoom);
        _viewportStart = TimeSpan.FromSeconds(Math.Max(0, anchor.TotalSeconds - ratio * _viewportDuration.TotalSeconds));
        ClampViewport();
        InvalidateTicks();
        UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.None);
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Pan(TimeSpan delta)
    {
        _clipDoubleClick.Reset();
        var previousStart = _viewportStart;
        _viewportStart += delta;
        ClampViewport();
        if (_viewportStart == previousStart)
            return;
        InvalidateTicks();
        UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.None);
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ResetLayers()
    {
        _selectedLayer = EditorTimelineLayer.None;
        _selectedEffectLayerId = null;
        _selectedAudioTrackId = null;

        foreach (var pair in _layers)
        {
            pair.Value.Enabled = false;
            pair.Value.Start = TimeSpan.Zero;
            pair.Value.End = DefaultLayerEnd(TimeSpan.Zero);
            pair.Value.Label = pair.Key switch
            {
                EditorTimelineLayer.Vfx => "VFX",
                EditorTimelineLayer.Text => "Text",
                EditorTimelineLayer.Subtitle => "Subtitle",
                EditorTimelineLayer.Element => "Element",
                _ => "Layer"
            };
        }

        UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.None);
        ClearHistory();
    }

    public void SetLayerEnabled(EditorTimelineLayer layer, bool enabled, TimeSpan anchor, string label)
    {
        if (layer == EditorTimelineLayer.Vfx)
            _selectedEffectLayerId = null;
        var state = GetLayer(layer);
        state.Label = label;

        if (enabled && !state.Enabled)
        {
            var start = ClampTime(anchor, TimeSpan.Zero, _duration - TimeSpan.FromSeconds(MinLayerSeconds));
            state.Start = start;
            state.End = DefaultLayerEnd(start);
        }

        state.Enabled = enabled;
        _selectedLayer = enabled ? layer : EditorTimelineLayer.None;
        ClampLayer(state);
        UpdateVisuals(notify: true, previewTime: enabled ? state.Start : (TimeSpan?)null, changedLayer: layer);
    }

    public void SetLayerRange(
        EditorTimelineLayer layer,
        bool enabled,
        TimeSpan start,
        TimeSpan end,
        string label,
        bool notify = false)
    {
        if (!_layers.TryGetValue(layer, out var state))
            return;

        state.Label = label;
        state.Enabled = enabled;
        state.Start = start;
        state.End = end;
        ClampLayer(state);
        UpdateVisuals(
            notify,
            previewTime: enabled ? state.Start : null,
            changedLayer: notify ? layer : EditorTimelineLayer.None);
    }

    public bool IsLayerEnabled(EditorTimelineLayer layer)
    {
        return _layers.TryGetValue(layer, out var state) && state.Enabled;
    }

    public bool IsTimeInLayer(EditorTimelineLayer layer, TimeSpan time)
    {
        var state = GetLayer(layer);
        return state.Enabled && time >= state.Start && time <= state.End;
    }

    public bool TryGetSelectedRange(out EditorTimelineLayer layer, out TimeSpan start, out TimeSpan end)
    {
        layer = _selectedLayer;

        if (GetSelectedEffectLayer() is { } effect)
        {
            start = TimeSpan.FromSeconds(effect.StartSeconds);
            end = TimeSpan.FromSeconds(effect.EndSeconds);
            return true;
        }

        if (TryGetPrecisionClipRange(out start, out end))
            return true;

        if (layer == EditorTimelineLayer.Trim)
        {
            start = _start;
            end = _end;
            return true;
        }

        if (_layers.TryGetValue(layer, out var state) && state.Enabled)
        {
            start = state.Start;
            end = state.End;
            return true;
        }

        start = TimeSpan.Zero;
        end = TimeSpan.Zero;
        return false;
    }

    public bool MoveSelectedRange(TimeSpan delta)
    {
        if (GetSelectedEffectLayer() is { } effect)
            return UpdateSelectedEffectLayer(layer =>
            {
                var target = _mediaClips.First(clip => clip.Id == layer.TargetClipId);
                var length = layer.EndSeconds - layer.StartSeconds;
                layer.StartSeconds = Math.Clamp(SnapTime(TimeSpan.FromSeconds(layer.StartSeconds) + delta).TotalSeconds,
                    target.TimelineStartSeconds, Math.Max(target.TimelineStartSeconds, target.TimelineStartSeconds + target.EffectiveDurationSeconds - length));
                layer.EndSeconds = layer.StartSeconds + length;
            });
        if (HasPrecisionClipSelection)
            return MovePrecisionClipSelection(delta);
        var before = TakeSnapshot();
        if (TryNudgeSelectedLayer(delta))
        {
            CommitUndoSnapshot(before);
            return true;
        }

        if (_selectedLayer != EditorTimelineLayer.Trim)
            return false;

        NudgeTrimRange(delta);
        CommitUndoSnapshot(before);
        return true;
    }

    public bool TrimSelectedStart(TimeSpan delta)
    {
        if (SelectedEffectLayerId is not null)
            return UpdateSelectedEffectLayer(layer =>
            {
                var target = _mediaClips.First(clip => clip.Id == layer.TargetClipId);
                layer.StartSeconds = Math.Clamp(SnapTime(TimeSpan.FromSeconds(layer.StartSeconds) + delta).TotalSeconds,
                    target.TimelineStartSeconds, Math.Max(target.TimelineStartSeconds, layer.EndSeconds - Math.Min(MinClipSeconds, target.EffectiveDurationSeconds)));
            });
        if (HasPrecisionClipSelection)
            return TrimPrecisionClipSelection(delta, trimStart: true);
        var before = TakeSnapshot();
        if (_selectedLayer == EditorTimelineLayer.Trim)
        {
            _start = ClampTime(SnapTime(_start + delta), TimeSpan.Zero, _end - MinimumTrim);
            UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Trim);
            CommitUndoSnapshot(before);
            return true;
        }

        if (!_layers.TryGetValue(_selectedLayer, out var state) || !state.Enabled)
            return false;

        state.Start = ClampTime(SnapTime(state.Start + delta), TimeSpan.Zero, state.End - TimeSpan.FromSeconds(MinLayerSeconds));
        ClampLayer(state);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: _selectedLayer);
        CommitUndoSnapshot(before);
        return true;
    }

    public bool TrimSelectedEnd(TimeSpan delta)
    {
        if (SelectedEffectLayerId is not null)
            return UpdateSelectedEffectLayer(layer =>
            {
                var target = _mediaClips.First(clip => clip.Id == layer.TargetClipId);
                var last = target.TimelineStartSeconds + target.EffectiveDurationSeconds;
                layer.EndSeconds = Math.Clamp(SnapTime(TimeSpan.FromSeconds(layer.EndSeconds) + delta).TotalSeconds,
                    Math.Min(last, layer.StartSeconds + Math.Min(MinClipSeconds, target.EffectiveDurationSeconds)), last);
            });
        if (HasPrecisionClipSelection)
            return TrimPrecisionClipSelection(delta, trimStart: false);
        var before = TakeSnapshot();
        if (_selectedLayer == EditorTimelineLayer.Trim)
        {
            _end = ClampTime(SnapTime(_end + delta), _start + MinimumTrim, _duration);
            UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Trim);
            CommitUndoSnapshot(before);
            return true;
        }

        if (!_layers.TryGetValue(_selectedLayer, out var state) || !state.Enabled)
            return false;

        state.End = ClampTime(SnapTime(state.End + delta), state.Start + TimeSpan.FromSeconds(MinLayerSeconds), _duration);
        ClampLayer(state);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: _selectedLayer);
        CommitUndoSnapshot(before);
        return true;
    }

    private TimeSpan MinimumTrim => TimeSpan.FromSeconds(Math.Min(MinClipSeconds, Math.Max(0.01, _duration.TotalSeconds / 2)));

    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e)
    {
        InvalidateTicks();
        UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.None);
    }

    private void OnSurfacePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode != DragMode.None || !IsPrimaryPointer(e))
        {
            _clipDoubleClick.Reset();
            return;
        }
        if (e.GetCurrentPoint(TimelineSurface).Position.X < HeaderWidth)
            return;
        if (e.GetCurrentPoint(TimelineSurface).Position.Y >= RulerHeight)
        {
            BeginMarquee(e);
            return;
        }

        if (_selectedMarkerId is not null)
        {
            _selectedMarkerId = null;
            MarkerSelected?.Invoke(this, new EditorMarkerSelectedEventArgs(null));
        }

        BeginDrag(DragMode.Playhead, EditorTimelineLayer.Playhead, e);
    }

    private void OnPlayheadPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode != DragMode.None || !IsPrimaryPointer(e))
            return;

        // The stem follows the pointer through every track, but an aligned cut
        // must remain trimmable without first moving the playhead out of the way.
        if (ReferenceEquals(sender, PlayheadStemHitArea) && TryBeginTrimBehindPlayhead(e))
            return;

        BeginDrag(DragMode.Playhead, EditorTimelineLayer.Playhead, e);
    }

    private bool TryBeginTrimBehindPlayhead(PointerRoutedEventArgs e)
    {
        static bool IsTrimEdge(FrameworkElement block, PointerRoutedEventArgs args, double maximum,
            bool canTrimStart = true, bool canTrimEnd = true)
        {
            if (block.Visibility != Visibility.Visible || block.ActualWidth <= 0 || block.ActualHeight <= 0)
                return false;
            var point = args.GetCurrentPoint(block).Position;
            if (point.X < 0 || point.X > block.ActualWidth || point.Y < 0 || point.Y > block.ActualHeight)
                return false;
            var edge = EditorTimelineInteractionMath.TrimEdgeHitWidth(block.ActualWidth, maximum);
            return canTrimStart && point.X <= edge || canTrimEnd && point.X >= block.ActualWidth - edge;
        }

        foreach (var block in _transitionHandleBlocks.Values)
        {
            if (block.Tag is not string id || FindMediaClipById(id) is not { } clip || IsClipLocked(clip)) continue;
            if (!IsTrimEdge(block, e, 10)) continue;
            OnTransitionHandlePointerPressed(block, e);
            e.Handled = true;
            return true;
        }
        foreach (var visual in _effectLayerVisuals.Values)
        {
            if (visual.Block.Tag is not string id || _effectLayers.FirstOrDefault(layer => layer.Id == id) is not { } effect ||
                _mediaClips.FirstOrDefault(clip => clip.Id == effect.TargetClipId) is not { } target || IsClipLocked(target)) continue;
            if (!IsTrimEdge(visual.Block, e, 16, effect.StartSeconds >= VisibleStart.TotalSeconds, effect.EndSeconds <= VisibleEnd.TotalSeconds)) continue;
            OnEffectLayerPointerPressed(visual.Block, e);
            e.Handled = true;
            return true;
        }
        foreach (var block in _mediaClipBlocks.Values.Concat(_linkedAudioBlocks.Values))
        {
            if (block.Tag is not string id || FindMediaClipById(id) is not { } clip || IsClipLocked(clip)) continue;
            if (!IsTrimEdge(block, e, 16, clip.TimelineStartSeconds >= VisibleStart.TotalSeconds,
                    clip.TimelineStartSeconds + clip.EffectiveDurationSeconds <= VisibleEnd.TotalSeconds)) continue;
            OnMediaClipPointerPressed(block, e);
            e.Handled = true;
            return true;
        }
        foreach (var block in _audioTrackSegmentBlocks.Values)
        {
            if (block.Tag is not string id || _audioTrackSegments.FirstOrDefault(item => item.Id == id) is not { } clip || IsAudioLocked(clip)) continue;
            if (!IsTrimEdge(block, e, 16, clip.TimelineStartSeconds >= VisibleStart.TotalSeconds,
                    clip.TimelineStartSeconds + clip.EffectiveDurationSeconds <= VisibleEnd.TotalSeconds)) continue;
            OnAudioTrackPointerPressed(block, e);
            e.Handled = true;
            return true;
        }
        return false;
    }

    private void OnMarkerPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _clipDoubleClick.Reset();
        if (!IsPrimaryPointer(e))
            return;
        if (sender is not FrameworkElement { Tag: string markerId })
            return;

        var marker = _markers.FirstOrDefault(item => item.Id == markerId);
        if (marker is null)
            return;

        EditStarting?.Invoke(this, EventArgs.Empty);
        Focus(FocusState.Pointer);
        UpdateHoverVisual(show: false);
        UpdateSnapVisual(null);
        _selectedMarkerId = marker.Id;
        _selectedLayer = EditorTimelineLayer.Marker;
        _dragMode = DragMode.MarkerMove;
        _dragLayer = EditorTimelineLayer.Marker;
        _dragMarkerId = marker.Id;
        _dragMarkerOriginalTime = marker.TimeSeconds;
        _dragStartTime = TimeFromPointer(e);
        _dragStartPlayhead = TimeSpan.FromSeconds(marker.TimeSeconds);
        _dragMarkerPointerOffset = TimeSpan.FromSeconds(marker.TimeSeconds) - _dragStartTime;
        _playhead = TimeSpan.FromSeconds(marker.TimeSeconds);
        MarkerSelected?.Invoke(this, new EditorMarkerSelectedEventArgs(CloneMarker(marker)));
        TimelineSurface.CapturePointer(e.Pointer);
        UpdateDrag(e);
        e.Handled = true;
    }

    private void OnTrimStartThumbPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        BeginDrag(DragMode.TrimStart, EditorTimelineLayer.Trim, e);
    }

    private void OnTrimEndThumbPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        BeginDrag(DragMode.TrimEnd, EditorTimelineLayer.Trim, e);
    }

    private void OnTrimBlockPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsPrimaryPointer(e))
            return;
        if (sender is not FrameworkElement element)
            return;

        var point = e.GetCurrentPoint(element).Position;
        var width = Math.Max(1, element.ActualWidth);
        var edgeWidth = EditorTimelineInteractionMath.TrimEdgeHitWidth(width);
        var mode = point.X <= edgeWidth && _start >= VisibleStart
            ? DragMode.TrimStart
            : point.X >= width - edgeWidth && _end <= VisibleEnd
                ? DragMode.TrimEnd
                : DragMode.TrimMove;

        BeginDrag(mode, EditorTimelineLayer.Trim, e);
    }

    private void OnLayerBlockPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsPrimaryPointer(e))
            return;
        if (sender is not FrameworkElement element || element.Tag is not EditorTimelineLayer layer)
            return;

        if (layer == EditorTimelineLayer.Vfx)
            _selectedEffectLayerId = null;

        var point = e.GetCurrentPoint(element).Position;
        var width = element.ActualWidth;
        var edgeWidth = EditorTimelineInteractionMath.TrimEdgeHitWidth(width);
        var state = GetLayer(layer);
        var mode = point.X <= edgeWidth && state.Start >= VisibleStart
            ? DragMode.LayerStart
            : point.X >= width - edgeWidth && state.End <= VisibleEnd
                ? DragMode.LayerEnd
                : DragMode.LayerMove;

        BeginDrag(mode, layer, e);
    }

    private void OnGraphicSegmentPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsPrimaryPointer(e))
            return;
        if (sender is not FrameworkElement { Tag: string segmentId } element)
            return;

        var segment = _graphicSegments.FirstOrDefault(layer => layer.Id == segmentId);
        if (segment is null)
            return;

        Focus(FocusState.Pointer);
        ClearMarkerSelection();
        _selectedGraphicSegmentId = segment.Id;
        _selectedLayer = EditorTimelineLayer.Element;
        GraphicSegmentSelected?.Invoke(
            this,
            new EditorGraphicSegmentSelectedEventArgs(segment.Id, TimeSpan.FromSeconds(segment.StartSeconds)));

        var point = e.GetCurrentPoint(element).Position;
        var width = Math.Max(1, element.ActualWidth);
        var edgeWidth = EditorTimelineInteractionMath.TrimEdgeHitWidth(width);
        var mode = point.X <= edgeWidth && segment.StartSeconds >= VisibleStart.TotalSeconds
            ? DragMode.GraphicStart
            : point.X >= width - edgeWidth && segment.EndSeconds <= VisibleEnd.TotalSeconds
                ? DragMode.GraphicEnd
                : DragMode.GraphicMove;
        BeginGraphicSegmentDrag(mode, segment, e);
    }

    private void OnMediaClipPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsPrimaryPointer(e))
            return;
        if (sender is not FrameworkElement element || element.Tag is not string clipId)
            return;

        var clip = _mediaClips.FirstOrDefault(item => item.Id == clipId);
        if (clip is null || IsClipLocked(clip))
            return;

        ClearMarkerSelection();

        if (IsKeyDown(VirtualKey.Control))
        {
            _clipDoubleClick.Reset();
            SelectForPointer(clipId, audio: false, toggle: true);
            UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media, selectionOnly: true);
            e.Handled = true;
            return;
        }

        var point = e.GetCurrentPoint(element).Position;
        var width = Math.Max(1, element.ActualWidth);
        var edgeWidth = EditorTimelineInteractionMath.TrimEdgeHitWidth(width);
        var clipStart = GetClipTimelineStart(clip);
        var mode = point.X <= edgeWidth && clipStart >= VisibleStart.TotalSeconds
            ? DragMode.MediaTrimStart
            : point.X >= width - edgeWidth && clipStart + clip.EffectiveDurationSeconds <= VisibleEnd.TotalSeconds
                ? DragMode.MediaTrimEnd
                : EditTool == EditorTimelineEditTool.Slip ? DragMode.MediaSlip : DragMode.MediaMove;
        if (TryHandleClipDoublePress(clipId, audio: false, mode, e))
            return;
        SelectForPointer(clipId, audio: false, toggle: false);
        BeginMediaDrag(mode, clip, e);
    }

    private void OnAudioTrackPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsPrimaryPointer(e) || sender is not FrameworkElement { Tag: string trackId } element)
            return;
        var track = _audioTrackSegments.FirstOrDefault(item => item.Id == trackId);
        if (track is null || IsAudioLocked(track))
            return;
        if (IsKeyDown(VirtualKey.Control))
        {
            _clipDoubleClick.Reset();
            SelectForPointer(trackId, audio: true, toggle: true);
            UpdateVisuals(true, _playhead, EditorTimelineLayer.Audio, selectionOnly: true);
            e.Handled = true;
            return;
        }
        var point = e.GetCurrentPoint(element).Position;
        var edgeWidth = EditorTimelineInteractionMath.TrimEdgeHitWidth(element.ActualWidth);
        var mode = point.X <= edgeWidth && track.TimelineStartSeconds >= VisibleStart.TotalSeconds
            ? DragMode.AudioTrimStart
            : point.X >= element.ActualWidth - edgeWidth &&
              track.TimelineStartSeconds + track.EffectiveDurationSeconds <= VisibleEnd.TotalSeconds
                ? DragMode.AudioTrimEnd
                : EditTool == EditorTimelineEditTool.Slip ? DragMode.AudioSlip : DragMode.AudioMove;
        if (TryHandleClipDoublePress(trackId, audio: true, mode, e))
            return;
        Focus(FocusState.Pointer);
        SelectForPointer(trackId, audio: true, toggle: false);
        _selectedAudioTrackId = trackId;
        _dragMode = mode;
        _dragLayer = EditorTimelineLayer.Audio;
        _dragAudioTrackId = track.Id;
        _dragAudioTrimStart = track.TrimStartSeconds;
        _dragAudioTrimEnd = track.TrimEndSeconds;
        _dragAudioTimelineStart = track.TimelineStartSeconds;
        _dragAudioVolumeKeyframes = track.VolumeKeyframes.Select(keyframe => keyframe.Clone()).ToList();
        _dragStartTime = TimeFromPointer(e);
        _dragStartPlayhead = _playhead;
        _dragEdgePointerOffset = mode switch
        {
            DragMode.AudioTrimStart => TimeSpan.FromSeconds(track.TimelineStartSeconds) - _dragStartTime,
            DragMode.AudioTrimEnd => TimeSpan.FromSeconds(track.TimelineStartSeconds + track.EffectiveDurationSeconds) - _dragStartTime,
            _ => TimeSpan.Zero
        };
        _dragUndoSnapshot = TakeSnapshot();
        UpdateHoverVisual(show: false);
        TimelineSurface.CapturePointer(e.Pointer);
        UpdateDrag(e);
        e.Handled = true;
    }

    private TimeSpan UpdateAudioDrag(TimeSpan time, out TimeSpan? snapGuideTime)
    {
        snapGuideTime = null;
        var track = _audioTrackSegments.FirstOrDefault(item => item.Id == _dragAudioTrackId);
        if (track is null)
            return _playhead;
        if (!_pointerDragActivated)
            return time;
        var speed = track.PlaybackSpeed;
        var minimumTimelineSeconds = MinClipSeconds / speed;
        var originalEnd = _dragAudioTimelineStart + (_dragAudioTrimEnd - _dragAudioTrimStart) / speed;
        if (_dragMode == DragMode.AudioTrimStart)
        {
            var minStart = EditorClipEditMath.EarliestTrimStart(_dragAudioTimelineStart, _dragAudioTrimStart, speed);
            var start = SnapEdgeTime(time, EditorTimelineLayer.Audio, TimeSpan.FromSeconds(minStart),
                TimeSpan.FromSeconds(originalEnd - minimumTimelineSeconds), out snapGuideTime);
            track.TrimStartSeconds = _dragAudioTrimStart + (start.TotalSeconds - _dragAudioTimelineStart) * speed;
            track.TimelineStartSeconds = start.TotalSeconds;
            track.VolumeKeyframes = RetimeVolumeKeyframesForStartTrim(_dragAudioVolumeKeyframes,
                track.Volume, start.TotalSeconds - _dragAudioTimelineStart);
            InvalidateAudioLoudnessAnalysis(track);
            time = start;
        }
        else if (_dragMode == DragMode.AudioTrimEnd)
        {
            var maxEnd = _dragAudioTimelineStart + (track.SourceDurationSeconds - _dragAudioTrimStart) / speed;
            var end = SnapEdgeTime(time, EditorTimelineLayer.Audio,
                TimeSpan.FromSeconds(track.TimelineStartSeconds + minimumTimelineSeconds),
                TimeSpan.FromSeconds(maxEnd), out snapGuideTime);
            track.TrimEndSeconds = _dragAudioTrimStart + (end.TotalSeconds - _dragAudioTimelineStart) * speed;
            track.VolumeKeyframes = RetimeVolumeKeyframesForEndTrim(_dragAudioVolumeKeyframes,
                track.Volume, end.TotalSeconds - _dragAudioTimelineStart,
                (_dragAudioTrimEnd - _dragAudioTrimStart) / speed);
            InvalidateAudioLoudnessAnalysis(track);
            time = end;
        }
        else
        {
            if (!_pointerDragActivated)
                return time;
            var start = TimeSpan.FromSeconds(EditorClipEditMath.MoveTimelineStart(
                _dragAudioTimelineStart, _dragStartTime.TotalSeconds, time.TotalSeconds, _frameRate));
            var length = TimeSpan.FromSeconds((_dragAudioTrimEnd - _dragAudioTrimStart) / speed);
            start = TimeSpan.FromSeconds(EditorTimelineMath.SnapToFrame(Math.Max(0, start.TotalSeconds), _frameRate));
            snapGuideTime = SnapMoveRange(EditorTimelineLayer.Audio, length, ref start, allowExtension: true);
            track.TimelineStartSeconds = start.TotalSeconds;
        }
        if (_dragUndoSnapshot?.AudioTracks.FirstOrDefault(item => item.Id == track.Id) is { } original)
        {
            track.FadeInSeconds = original.FadeInSeconds;
            track.FadeOutSeconds = original.FadeOutSeconds;
        }
        track.Normalize();
        EnsureDragDuration(track.TimelineStartSeconds + track.EffectiveDurationSeconds);
        return time;
    }

    private void OnTransitionHandlePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsPrimaryPointer(e))
            return;
        if (sender is not FrameworkElement { Tag: string clipId })
            return;

        var clip = _mediaClips.FirstOrDefault(item => item.Id == clipId);
        if (clip is null || IsClipLocked(clip) || GetTransitionPredecessor(clip) is null)
            return;

        var style = string.Equals(clip.TransitionInStyle, "inherit", StringComparison.OrdinalIgnoreCase)
            ? _primaryTransitionStyle
            : clip.TransitionInStyle;
        if (string.Equals(style, "cut", StringComparison.OrdinalIgnoreCase))
            return;

        Focus(FocusState.Pointer);
        SelectOnlyMediaClip(clip.Id);
        _selectedLayer = EditorTimelineLayer.Media;
        _clipDoubleClick.Reset();
        var pointer = e.GetCurrentPoint((FrameworkElement)sender).Position;
        var handleWidth = ((FrameworkElement)sender).ActualWidth;
        var edgeWidth = EditorTimelineInteractionMath.TrimEdgeHitWidth(handleWidth, 10, 22);
        _dragMode = pointer.X <= edgeWidth || pointer.X >= handleWidth - edgeWidth
            ? DragMode.TransitionDuration : DragMode.TransitionMove;
        _transitionResizeDirection = pointer.X >= handleWidth - edgeWidth ? 1 : -1;
        _dragTransitionStyle = style;
        _transitionMoveTargetId = null;
        _transitionMoveActivated = false;
        _dragLayer = EditorTimelineLayer.Media;
        _dragMediaClipId = clip.Id;
        _dragStartTime = TimeFromPointer(e);
        _dragStartPlayhead = _playhead;
        _dragTransitionDuration = string.Equals(clip.TransitionInStyle, "inherit", StringComparison.OrdinalIgnoreCase)
            ? _primaryTransitionSeconds
            : clip.TransitionInDurationSeconds;
        var predecessor = GetTransitionPredecessor(clip);
        if (predecessor is not null)
            _dragTransitionDuration = GetPrimaryTransitionOverlap(predecessor, clip);
        _dragTransitionPointerX = e.GetCurrentPoint(TimelineSurface).Position.X;
        _dragTransitionSecondsPerPixel = VisibleDuration.TotalSeconds / GetRailWidth();
        _dragUndoSnapshot = TakeSnapshot();
        TimelineSurface.CapturePointer(e.Pointer);
        UpdateDrag(e);
        e.Handled = true;
    }

    private void BeginMediaDrag(DragMode mode, EditorProjectClip clip, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        UpdateHoverVisual(show: false);
        UpdateSnapVisual(null);
        _dragMode = mode;
        _dragLayer = EditorTimelineLayer.Media;
        _selectedLayer = EditorTimelineLayer.Media;
        _dragMediaClipId = clip.Id;
        _dragMediaTrimStart = clip.TrimStartSeconds;
        _dragMediaTrimEnd = clip.TrimEndSeconds;
        _dragMediaTimelineStart = GetClipTimelineStart(clip);
        _dragMediaTransformKeyframes = clip.TransformKeyframes
            .Select(keyframe => keyframe.Clone())
            .ToList();
        _dragMediaVolumeKeyframes = clip.VolumeKeyframes
            .Select(keyframe => keyframe.Clone())
            .ToList();
        _dragMediaFilterKeyframes = clip.FilterStrengthKeyframes
            .Select(keyframe => keyframe.Clone())
            .ToList();
        _dragStartTime = TimeFromPointer(e);
        _dragEdgePointerOffset = mode switch
        {
            DragMode.MediaTrimStart => TimeSpan.FromSeconds(_dragMediaTimelineStart) - _dragStartTime,
            DragMode.MediaTrimEnd => TimeSpan.FromSeconds(_dragMediaTimelineStart + clip.EffectiveDurationSeconds) - _dragStartTime,
            _ => TimeSpan.Zero
        };
        _dragStartPlayhead = _playhead;
        _dragUndoSnapshot = TakeSnapshot();
        TimelineSurface.CapturePointer(e.Pointer);
        UpdateDrag(e);
        e.Handled = true;
    }

    private void BeginGraphicSegmentDrag(
        DragMode mode,
        EditorProjectGraphicLayer segment,
        PointerRoutedEventArgs e)
    {
        _clipDoubleClick.Reset();
        EditStarting?.Invoke(this, EventArgs.Empty);
        _dragGraphicCancelSnapshot = _graphicSegments.Select(CloneGraphicLayer).ToList();
        UpdateHoverVisual(show: false);
        UpdateSnapVisual(null);
        _dragMode = mode;
        _dragLayer = EditorTimelineLayer.Element;
        _dragGraphicSegmentId = segment.Id;
        _dragGraphicStart = segment.StartSeconds;
        _dragGraphicEnd = segment.EndSeconds;
        _dragGraphicKeyframeTimes = segment.Keyframes
            .Select(keyframe => keyframe.TimeSeconds)
            .ToList();
        _dragStartTime = TimeFromPointer(e);
        _dragEdgePointerOffset = mode switch
        {
            DragMode.GraphicStart => TimeSpan.FromSeconds(segment.StartSeconds) - _dragStartTime,
            DragMode.GraphicEnd => TimeSpan.FromSeconds(segment.EndSeconds) - _dragStartTime,
            _ => TimeSpan.Zero
        };
        _dragStartPlayhead = _playhead;
        _dragUndoSnapshot = null;
        TimelineSurface.CapturePointer(e.Pointer);
        UpdateDrag(e);
        e.Handled = true;
    }

    private void OnSurfacePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode == DragMode.None)
        {
            UpdatePointerCursor(e.GetCurrentPoint(TimelineSurface).Position);
            var hoverX = e.GetCurrentPoint(TimelineSurface).Position.X;
            if (hoverX < HeaderWidth || hoverX > HeaderWidth + GetRailWidth())
            {
                UpdateHoverVisual(show: false);
                return;
            }
            _hoverTime = TimeFromPointer(e);
            UpdateHoverVisual(show: true);
            return;
        }

        var point = e.GetCurrentPoint(TimelineSurface);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed)
        {
            UpdateDrag(e);
            FinishDrag();
            return;
        }

        QueueDragPoint(point.Position);
        e.Handled = true;
    }

    private void OnSurfacePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode == DragMode.None)
            return;

        UpdateDrag(e);
        FinishDrag();
        e.Handled = true;
    }

    private void OnSurfacePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        CancelDrag();
    }

    private void OnSurfacePointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode == DragMode.None)
        {
            UpdateHoverVisual(show: false);
            SetTimelineCursor(InputSystemCursorShape.Arrow);
        }
    }

    private void OnSurfacePointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        CancelDrag();
    }

    private void BeginDrag(DragMode mode, EditorTimelineLayer layer, PointerRoutedEventArgs e)
    {
        _clipDoubleClick.Reset();
        if (!IsPrimaryPointer(e))
            return;
        if (mode != DragMode.Playhead)
            EditStarting?.Invoke(this, EventArgs.Empty);
        Focus(FocusState.Pointer);
        if (layer != EditorTimelineLayer.Marker)
            ClearMarkerSelection();
        UpdateHoverVisual(show: false);
        UpdateSnapVisual(null);

        _dragMode = mode;
        _dragLayer = layer;
        if (layer != EditorTimelineLayer.Playhead || SelectedEffectLayerId is null)
            _selectedLayer = layer;
        _dragStartTime = TimeFromPointer(e);
        _dragStartPlayhead = _playhead;
        _dragUndoSnapshot = mode == DragMode.Playhead ? null : TakeSnapshot();

        if (_layers.TryGetValue(layer, out var state))
        {
            _dragStartLayerStart = state.Start;
            _dragStartLayerEnd = state.End;
        }
        else
        {
            _dragStartLayerStart = _start;
            _dragStartLayerEnd = _end;
        }

        _dragEdgePointerOffset = mode switch
        {
            DragMode.TrimStart or DragMode.LayerStart => _dragStartLayerStart - _dragStartTime,
            DragMode.TrimEnd or DragMode.LayerEnd => _dragStartLayerEnd - _dragStartTime,
            _ => TimeSpan.Zero
        };

        TimelineSurface.CapturePointer(e.Pointer);
        UpdateDrag(e, refreshSelection: true);
        e.Handled = true;
    }

    private void UpdateDrag(PointerRoutedEventArgs e, bool refreshSelection = false)
    {
        // Press and release apply synchronously. Pointer moves are coalesced to
        // the next rendered frame, and can never overwrite the final release.
        _pendingDragPoint = null;
        UpdateDragPoint(e.GetCurrentPoint(TimelineSurface).Position, refreshSelection);
    }

    private void UpdateDragPoint(Point point, bool refreshSelection = false)
    {
        _lastDragPointer = point;
        if (_dragVisibleDuration is null)
        {
            _dragEditStartingRaised = !DefersEditUntilDrag(_dragMode);
            _dragLinkedTrimChanged = false;
            _dragVisibleDuration = VisibleDuration;
            _dragOriginalDuration = _duration;
            _dragInitialPointer = _lastDragPointer;
            _pointerDragActivated = false;
            _dragRangeEndFollowedDuration = Math.Abs((_end - _duration).TotalSeconds) < 0.00001;
            _lastAutoPanTick = Stopwatch.GetTimestamp();
            _autoPanTimer.Start();
        }
        else if (Math.Abs(_lastDragPointer.X - _dragInitialPointer.X) >= 4 ||
                 Math.Abs(_lastDragPointer.Y - _dragInitialPointer.Y) >= 4)
            _pointerDragActivated = true;
        if (_pointerDragActivated && !_dragEditStartingRaised)
        {
            EditStarting?.Invoke(this, EventArgs.Empty);
            _dragStartPlayhead = _playhead;
            _dragEditStartingRaised = true;
            _clipDoubleClick.Reset();
        }
        UpdateDragAt(_lastDragPointer, refreshSelection);
    }

    private void UpdateDragAt(Point pointer, bool refreshSelection = false)
    {
        var time = TimeFromX(pointer.X);
        if (_dragMode is DragMode.TrimStart or DragMode.TrimEnd or DragMode.LayerStart or DragMode.LayerEnd
            or DragMode.GraphicStart or DragMode.GraphicEnd or DragMode.MediaTrimStart or DragMode.MediaTrimEnd
            or DragMode.AudioTrimStart or DragMode.AudioTrimEnd or DragMode.EffectStart or DragMode.EffectEnd)
            time += _dragEdgePointerOffset;
        var changedLayer = _dragLayer;
        TimeSpan? snapGuideTime = null;
        TimeSpan previewTime;

        switch (_dragMode)
        {
            case DragMode.EffectStart:
            case DragMode.EffectEnd:
            case DragMode.EffectMove:
                previewTime = UpdateEffectLayerDrag(time, out snapGuideTime);
                break;
            case DragMode.Marquee:
                UpdateMarquee(pointer);
                previewTime = _dragStartPlayhead;
                break;
            case DragMode.MediaSlip:
            case DragMode.AudioSlip:
                previewTime = SlipSelectedClip(time);
                break;
            case DragMode.AudioAutomation:
                UpdateAudioAutomation(pointer);
                previewTime = _dragStartPlayhead;
                break;
            case DragMode.AudioTrimStart:
            case DragMode.AudioTrimEnd:
            case DragMode.AudioMove:
                previewTime = _dragMode == DragMode.AudioMove
                    ? MoveSelectedClips(time, pointer, out snapGuideTime)
                    : UpdateAudioDrag(time, out snapGuideTime);
                break;
            case DragMode.MarkerMove:
                previewTime = MoveMarker(time + _dragMarkerPointerOffset, out snapGuideTime);
                break;
            case DragMode.GraphicStart:
                previewTime = ResizeGraphicSegmentStart(time, out snapGuideTime);
                break;
            case DragMode.GraphicEnd:
                previewTime = ResizeGraphicSegmentEnd(time, out snapGuideTime);
                break;
            case DragMode.GraphicMove:
                previewTime = MoveGraphicSegment(time, out snapGuideTime);
                break;
            case DragMode.MediaTrimStart:
                previewTime = ResizeMediaClipStart(time, out snapGuideTime);
                break;
            case DragMode.MediaTrimEnd:
                previewTime = ResizeMediaClipEnd(time, out snapGuideTime);
                break;
            case DragMode.MediaMove:
                previewTime = MoveSelectedClips(time, pointer, out snapGuideTime);
                break;
            case DragMode.TransitionMove:
                if (Math.Abs(pointer.X - _dragTransitionPointerX) >= 4)
                    _transitionMoveActivated = true;
                var transitionTarget = _transitionMoveActivated ? FindTransitionDropTarget(pointer) : null;
                _transitionMoveTargetId = transitionTarget?.Id;
                ShowTransitionDropTarget(transitionTarget);
                previewTime = _dragStartPlayhead;
                break;
            case DragMode.TransitionDuration:
                previewTime = ResizeTransitionDuration(pointer.X, out snapGuideTime);
                break;
            case DragMode.TrimStart:
                _start = SnapEdgeTime(time, EditorTimelineLayer.Trim, TimeSpan.Zero, _end - MinimumTrim, out snapGuideTime);
                previewTime = _start;
                break;
            case DragMode.TrimEnd:
                _end = SnapEdgeTime(time, EditorTimelineLayer.Trim, _start + MinimumTrim, _duration, out snapGuideTime);
                previewTime = _end;
                break;
            case DragMode.TrimMove:
                snapGuideTime = MoveTrimRange(time - _dragStartTime);
                previewTime = _start;
                break;
            case DragMode.LayerStart:
                snapGuideTime = ResizeLayerStart(_dragLayer, time);
                previewTime = GetLayer(_dragLayer).Start;
                break;
            case DragMode.LayerEnd:
                snapGuideTime = ResizeLayerEnd(_dragLayer, time);
                previewTime = GetLayer(_dragLayer).End;
                break;
            case DragMode.LayerMove:
                snapGuideTime = MoveLayer(_dragLayer, time - _dragStartTime);
                previewTime = GetLayer(_dragLayer).Start;
                break;
            case DragMode.Playhead:
                previewTime = time;
                break;
            default:
                return;
        }

        if (_dragMode is DragMode.MediaTrimStart or DragMode.MediaTrimEnd or DragMode.AudioTrimStart or DragMode.AudioTrimEnd)
            ApplyLinkedSelectionTrim();
        if (_dragMode is DragMode.MediaTrimStart or DragMode.MediaTrimEnd or DragMode.AudioTrimStart or DragMode.AudioTrimEnd)
            ApplyTrimTool(previewTime);
        if ((_dragLinkedTrimChanged || EditTool is EditorTimelineEditTool.RollingTrim or EditorTimelineEditTool.RippleTrim) && snapGuideTime.HasValue &&
            _dragMode is DragMode.MediaTrimStart or DragMode.MediaTrimEnd or DragMode.AudioTrimStart or DragMode.AudioTrimEnd)
        {
            var editedVideo = GetDraggedMediaClip();
            var editedAudio = _audioTrackSegments.FirstOrDefault(item => item.Id == _dragAudioTrackId);
            var start = editedVideo?.TimelineStartSeconds ?? editedAudio?.TimelineStartSeconds ?? 0;
            var duration = editedVideo?.EffectiveDurationSeconds ?? editedAudio?.EffectiveDurationSeconds ?? 0;
            var edge = _dragMode is DragMode.MediaTrimStart or DragMode.AudioTrimStart ? start : start + duration;
            if (Math.Abs(edge - snapGuideTime.Value.TotalSeconds) > .00001)
                snapGuideTime = null;
        }
        if (_dragMode is DragMode.Playhead or DragMode.MarkerMove)
            _playhead = previewTime < TimeSpan.Zero ? TimeSpan.Zero : previewTime;
        else if (_dragMode != DragMode.Marquee && (_pointerDragActivated || !DefersEditUntilDrag(_dragMode)))
            _playhead = _dragStartPlayhead;
        SetTimelineCursor(_dragMode is DragMode.MediaTrimStart or DragMode.MediaTrimEnd or DragMode.AudioTrimStart or
            DragMode.AudioTrimEnd or DragMode.EffectStart or DragMode.EffectEnd or DragMode.TransitionDuration || IsDraggingAudioFade ? InputSystemCursorShape.SizeWestEast :
            _dragMode == DragMode.Playhead ? InputSystemCursorShape.Cross : InputSystemCursorShape.SizeAll);
        if (_dragMode == DragMode.Playhead && !refreshSelection)
        {
            UpdatePlayheadVisual();
            TimelineChanged?.Invoke(this, CreateEventArgs(_playhead, EditorTimelineLayer.Playhead));
            return;
        }
        if (_dragMode is DragMode.GraphicStart or DragMode.GraphicEnd or DragMode.GraphicMove)
            NotifyGraphicSegmentsChanged();
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: changedLayer,
            selectionOnly: _dragMode == DragMode.Marquee || (!_pointerDragActivated && DefersEditUntilDrag(_dragMode)));
        UpdateSnapVisual(snapGuideTime);
        UpdateDragFeedback(pointer);
    }

    private void FinishDrag()
    {
        if (_dragMode == DragMode.None)
            return;

        var completedMode = _dragMode;
        var rippleTrim = EditTool == EditorTimelineEditTool.RippleTrim &&
            completedMode is DragMode.MediaTrimStart or DragMode.MediaTrimEnd or DragMode.AudioTrimStart or DragMode.AudioTrimEnd;
        var linkedTrim = _dragLinkedTrimChanged;
        CompleteClipClick(completedMode);
        LastDragWasSelectionOnly = completedMode == DragMode.Marquee || (!_pointerDragActivated && DefersEditUntilDrag(completedMode));
        LastDragWasSeekOnly = completedMode == DragMode.Playhead;
        var undoSnapshot = _dragUndoSnapshot;
        _dragMode = DragMode.None;
        _dragLayer = EditorTimelineLayer.None;
        _dragUndoSnapshot = null;
        _autoPanTimer.Stop();
        UpdateSnapVisual(null);
        DropTargetHighlight.Visibility = InsertionGuide.Visibility = Visibility.Collapsed;
        MarqueeBox.Visibility = Visibility.Collapsed;
        TimelineSurface.ReleasePointerCaptures();

        if (completedMode == DragMode.TransitionMove && _transitionMoveActivated)
            ApplyTransitionMove();
        if (!LastDragWasSelectionOnly && completedMode is not DragMode.Playhead and not DragMode.Marquee && undoSnapshot is not null)
        {
            if (rippleTrim || linkedTrim || completedMode is DragMode.AudioTrimStart or DragMode.AudioTrimEnd or DragMode.AudioMove or DragMode.MediaMove or DragMode.AudioAutomation)
            {
                RecalculateDurationFromMedia(clearHistory: false);
                if (rippleTrim || linkedTrim || completedMode is DragMode.MediaMove or DragMode.AudioMove && _selectedMediaClipIds.Count > 0)
                    RebuildMediaClipBlocks();
                RebuildAudioTrackSegmentBlocks();
                UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.None);
            }
            CommitUndoSnapshot(undoSnapshot);
            if (rippleTrim || linkedTrim || completedMode is DragMode.MediaTrimStart or DragMode.MediaTrimEnd or DragMode.MediaMove or DragMode.MediaSlip or DragMode.TransitionDuration)
                NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, _dragMediaClipId);
            if (rippleTrim || linkedTrim || completedMode is DragMode.AudioTrimStart or DragMode.AudioTrimEnd or DragMode.AudioMove or DragMode.MediaMove or DragMode.AudioSlip or DragMode.AudioAutomation)
                NotifyAudioTracksChanged(EditorMediaChangeKind.Changed);
            if (completedMode == DragMode.AudioMove && _selectedMediaClipIds.Count > 0)
                NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, SelectedMediaClipId);
        }

        if (completedMode is DragMode.GraphicStart or DragMode.GraphicEnd or DragMode.GraphicMove)
            NotifyGraphicSegmentsChanged();

        if (completedMode == DragMode.MarkerMove)
        {
            _markers.Sort((left, right) => left.TimeSeconds.CompareTo(right.TimeSeconds));
            NotifyMarkersChanged(EditorMarkerChangeKind.Changed, _dragMarkerId);
            var marker = _markers.FirstOrDefault(item => item.Id == _dragMarkerId);
            MarkerSelected?.Invoke(this, new EditorMarkerSelectedEventArgs(marker is null ? null : CloneMarker(marker)));
        }

        _dragMediaClipId = null;
        _dragEffectLayerId = null;
        _dragAudioTrackId = null;
        _dragAudioAutomation = null;
        if (!LastDragWasSelectionOnly)
            RebuildAudioAutomation();
        _dragAudioVolumeKeyframes = [];
        _dragGraphicSegmentId = null;
        _dragMarkerId = null;
        _dragGraphicKeyframeTimes = [];
        _dragMediaTransformKeyframes = [];
        _dragMediaVolumeKeyframes = [];
        _dragMediaFilterKeyframes = [];
        _dragGraphicCancelSnapshot = null;
        StopDragTracking();
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyTransitionMove()
    {
        var source = _mediaClips.FirstOrDefault(clip => clip.Id == _dragMediaClipId);
        var target = _mediaClips.FirstOrDefault(clip => clip.Id == _transitionMoveTargetId);
        if (source is null || target is null)
        {
            SetEditStatus("Drop the transition near the join between two video clips.");
            return;
        }
        if (source.Id == target.Id)
        {
            SetEditStatus("Transition kept at this join.");
            return;
        }
        if (!PrepareTransitionTarget(target, _dragTransitionStyle, _dragTransitionDuration))
            return;
        var copy = IsKeyDown(VirtualKey.Control);
        if (!copy)
            source.TransitionInStyle = "cut";
        target.TransitionInStyle = _dragTransitionStyle;
        target.TransitionInDurationSeconds = _dragTransitionDuration;
        SelectOnlyMediaClip(target.Id);
        RecalculateDurationFromMedia(clearHistory: false);
        RebuildMediaClipBlocks();
        NotifyMediaClipsChanged(EditorMediaChangeKind.Changed, target.Id);
        SetEditStatus(copy ? "Transition copied to this join." : "Transition moved to this join.");
    }

    private void EnsureDragDuration(double endSeconds)
    {
        if (endSeconds <= _duration.TotalSeconds)
            return;
        _duration = TimeSpan.FromSeconds(endSeconds);
        if (_dragRangeEndFollowedDuration)
            _end = _duration;
        InvalidateTicks();
    }

    private void OnAutoPanTick(object? sender, object e)
    {
        if (_dragMode == DragMode.None)
        {
            _autoPanTimer.Stop();
            return;
        }
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _lastAutoPanTick) / (double)Stopwatch.Frequency;
        _lastAutoPanTick = now;
        if (!_pointerDragActivated)
            return;
        var delta = EditorTimelineInteractionMath.AutoPanDeltaSeconds(_lastDragPointer.X,
            HeaderWidth, GetRailWidth(), VisibleDuration.TotalSeconds, elapsed);
        if (Math.Abs(delta) < 0.000001)
            return;
        var canExtend = _dragMode is DragMode.AudioMove or DragMode.MediaMove or DragMode.AudioTrimEnd or DragMode.MediaTrimEnd;
        if (delta > 0 && canExtend)
        {
            var maximumDuration = double.MaxValue;
            if (_dragMode == DragMode.AudioTrimEnd &&
                _audioTrackSegments.FirstOrDefault(track => track.Id == _dragAudioTrackId) is { } audio)
                maximumDuration = Math.Max(_dragOriginalDuration.TotalSeconds,
                    _dragAudioTimelineStart + (audio.SourceDurationSeconds - _dragAudioTrimStart) / audio.PlaybackSpeed);
            else if (_dragMode == DragMode.MediaTrimEnd && GetDraggedMediaClip() is { } media)
                maximumDuration = Math.Max(_dragOriginalDuration.TotalSeconds,
                    _dragMediaTimelineStart + (media.SourceDurationSeconds - _dragMediaTrimStart) / media.PlaybackSpeed);
            EnsureDragDuration(Math.Min(maximumDuration, _viewportStart.TotalSeconds + VisibleDuration.TotalSeconds + delta));
        }
        var previous = _viewportStart;
        _viewportStart += TimeSpan.FromSeconds(delta);
        ClampViewport();
        if (_viewportStart == previous)
            return;
        InvalidateTicks();
        UpdateDragAt(_lastDragPointer);
        ViewportChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StopDragTracking()
    {
        StopDragUpdatePump();
        HideDragFeedback();
        _autoPanTimer.Stop();
        var visibleDuration = _dragVisibleDuration;
        _dragVisibleDuration = null;
        if (visibleDuration is { TotalSeconds: > 0 })
            _viewportDuration = visibleDuration.Value;
        SetTimelineCursor(InputSystemCursorShape.Arrow);
        UpdateVisuals(notify: false, previewTime: null, changedLayer: EditorTimelineLayer.None);
    }

    private void CancelDrag()
    {
        StopDragUpdatePump();
        if (_dragMode == DragMode.None)
            return;
        MarqueeBox.Visibility = Visibility.Collapsed;
        _dragAudioAutomation = null;
        _clipDoubleClick.Reset();
        var canceledMode = _dragMode;
        LastDragWasSelectionOnly = canceledMode == DragMode.Marquee || (!_pointerDragActivated && DefersEditUntilDrag(canceledMode));
        LastDragWasSeekOnly = canceledMode == DragMode.Playhead;
        var snapshot = _dragUndoSnapshot;
        var graphics = _dragGraphicCancelSnapshot;
        _dragUndoSnapshot = null;
        _dragGraphicCancelSnapshot = null;
        _dragMode = DragMode.None;
        _dragLayer = EditorTimelineLayer.None;
        LastDragWasSeekOnly = true;
        _autoPanTimer.Stop();
        DropTargetHighlight.Visibility = InsertionGuide.Visibility = Visibility.Collapsed;
        UpdateSnapVisual(null);
        TimelineSurface.ReleasePointerCaptures();
        if (canceledMode == DragMode.MarkerMove && _markers.FirstOrDefault(marker => marker.Id == _dragMarkerId) is { } marker)
        {
            marker.TimeSeconds = _dragMarkerOriginalTime;
            _playhead = _dragStartPlayhead;
            RebuildMarkerVisuals();
            NotifyMarkersChanged(EditorMarkerChangeKind.Changed, marker.Id);
            MarkerSelected?.Invoke(this, new EditorMarkerSelectedEventArgs(CloneMarker(marker)));
            UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Marker);
        }
        else if (snapshot is not null && LastDragWasSelectionOnly)
        {
            RestoreClipSelection(snapshot.SelectedMediaClipIds, snapshot.SelectedAudioTrackIds,
                snapshot.SelectedMediaClipIds.Count == 1 ? snapshot.SelectedMediaClipIds[0] : null, snapshot.SelectedAudioTrackId);
            UpdateVisuals(true, _playhead, EditorTimelineLayer.Media, selectionOnly: true);
        }
        else if (snapshot is not null)
            RestoreSnapshot(snapshot);
        else if (graphics is not null)
        {
            _graphicSegments.Clear();
            _graphicSegments.AddRange(graphics);
            _playhead = _dragStartPlayhead;
            RebuildGraphicSegmentBlocks();
            NotifyGraphicSegmentsChanged();
            UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Element);
        }
        else
        {
            _playhead = _dragStartPlayhead;
            UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Playhead);
        }
        _dragMediaClipId = _dragAudioTrackId = _dragGraphicSegmentId = null;
        _dragEffectLayerId = null;
        StopDragTracking();
        SetEditStatus("Edit canceled.");
        DragCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void SetTimelineCursor(InputSystemCursorShape shape)
    {
        if (_cursorShape == shape)
            return;
        _cursorShape = shape;
        ProtectedCursor = InputSystemCursor.Create(shape);
    }

    private void AddTrimGrips(Border block)
    {
        var content = block.Child;
        block.Child = null;
        var grid = new Grid();
        if (content is not null)
            grid.Children.Add(content);
        Border Grip(HorizontalAlignment alignment) => new()
        {
            Width = 2,
            Height = 12,
            CornerRadius = new CornerRadius(1),
            Margin = new Thickness(4, 0, 4, 0),
            HorizontalAlignment = alignment,
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 221, 232, 236)),
            IsHitTestVisible = false,
            Opacity = 0.55
        };
        var left = Grip(HorizontalAlignment.Left);
        var right = Grip(HorizontalAlignment.Right);
        grid.Children.Add(left);
        grid.Children.Add(right);
        block.Child = grid;
        _clipTrimGrips[block] = (left, right);
    }

    private void UpdateTrimGrips(Border block, double width, bool leftVisible, bool rightVisible, bool selected)
    {
        if (!_clipTrimGrips.TryGetValue(block, out var grips))
            return;
        grips.Left.Visibility = width >= 24 && leftVisible ? Visibility.Visible : Visibility.Collapsed;
        grips.Right.Visibility = width >= 24 && rightVisible ? Visibility.Visible : Visibility.Collapsed;
        grips.Left.Opacity = grips.Right.Opacity = selected ? 1 : 0.55;
    }

    private void UpdatePointerCursor(Point pointer)
    {
        if (IsPointInHoverTimeBadge(pointer))
        {
            SetTimelineCursor(InputSystemCursorShape.SizeWestEast);
            return;
        }
        var playheadX = HeaderWidth + SecondsToX(_playhead.TotalSeconds, GetRailWidth()) + 1;
        var playheadHitWidth = pointer.Y < RulerHeight ? PlayheadCapHitArea.Width : PlayheadStemHitArea.Width;
        var overPlayhead = _playhead >= VisibleStart && _playhead <= VisibleEnd &&
            Math.Abs(pointer.X - playheadX) <= playheadHitWidth / 2;
        if (overPlayhead)
        {
            SetTimelineCursor(InputSystemCursorShape.SizeWestEast);
            return;
        }
        if (TryGetAudioAutomationCursor(pointer, out var automationCursor))
        {
            SetTimelineCursor(automationCursor);
            return;
        }
        if (TryGetEffectLayerCursor(pointer, out var effectCursor))
        {
            SetTimelineCursor(effectCursor);
            return;
        }
        foreach (var block in _transitionHandleBlocks.Values.Concat(_mediaClipBlocks.Values)
                     .Concat(_linkedAudioBlocks.Values).Concat(_audioTrackSegmentBlocks.Values))
        {
            if (block.Visibility != Visibility.Visible)
                continue;
            var x = Canvas.GetLeft(block);
            var y = Canvas.GetTop(block);
            if (pointer.X < x || pointer.X > x + block.Width || pointer.Y < y || pointer.Y > y + block.Height)
                continue;
            var transition = block.Tag is string id && _transitionHandleBlocks.TryGetValue(id, out var handle) && ReferenceEquals(handle, block);
            var audio = block.Tag is string audioId && _audioTrackSegmentBlocks.TryGetValue(audioId, out var audioBlock) && ReferenceEquals(audioBlock, block)
                ? _audioTrackSegments.FirstOrDefault(clip => clip.Id == audioId) : null;
            var video = audio is null && block.Tag is string videoId ? _mediaClips.FirstOrDefault(clip => clip.Id == videoId) : null;
            if (audio is not null && IsAudioLocked(audio) || video is not null && IsClipLocked(video))
            {
                SetTimelineCursor(InputSystemCursorShape.Arrow);
                return;
            }
            var start = video?.TimelineStartSeconds ?? audio?.TimelineStartSeconds;
            var duration = video?.EffectiveDurationSeconds ?? audio?.EffectiveDurationSeconds;
            var leftVisible = transition || start.HasValue && start.Value >= VisibleStart.TotalSeconds;
            var rightVisible = transition || start.HasValue && duration.HasValue && start.Value + duration.Value <= VisibleEnd.TotalSeconds;
            var edge = EditorTimelineInteractionMath.TrimEdgeHitWidth(block.Width, transition ? 10 : 16);
            SetTimelineCursor(leftVisible && pointer.X <= x + edge || rightVisible && pointer.X >= x + block.Width - edge
                ? InputSystemCursorShape.SizeWestEast : InputSystemCursorShape.SizeAll);
            return;
        }
        SetTimelineCursor(InputSystemCursorShape.Arrow);
    }

    private void OnTimelineKeyDown(object sender, KeyRoutedEventArgs e)
    {
        _clipDoubleClick.Reset();
        if (_dragMode != DragMode.None)
        {
            if (e.Key == VirtualKey.Escape)
            {
                CancelDrag();
                e.Handled = true;
            }
            return;
        }

        if (IsKeyDown(VirtualKey.Control))
        {
            if (!IsKeyDown(VirtualKey.Shift) && !IsKeyDown(VirtualKey.Menu) && e.Key == VirtualKey.A)
            {
                SelectAllClips();
                e.Handled = true;
            }
            return;
        }

        // Modified commands belong to the configurable editor shortcuts. Only
        // Shift+Left/Right is a timeline gesture (a ten-frame nudge).
        if (IsKeyDown(VirtualKey.Menu) ||
            IsKeyDown(VirtualKey.Shift) && e.Key is not (VirtualKey.Left or VirtualKey.Right))
            return;

        var step = GetKeyboardStep();
        switch (e.Key)
        {
            case VirtualKey.Left:
                NudgeSelected(TimeSpan.FromTicks(-step.Ticks));
                e.Handled = true;
                break;
            case VirtualKey.Right:
                NudgeSelected(step);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                SeekToAdjacentClipEdit(next: false);
                e.Handled = true;
                break;
            case VirtualKey.Down:
                SeekToAdjacentClipEdit(next: true);
                e.Handled = true;
                break;
            case VirtualKey.Home:
                SeekWithKeyboard(_start);
                e.Handled = true;
                break;
            case VirtualKey.End:
                SeekWithKeyboard(_end);
                e.Handled = true;
                break;
            case VirtualKey.I:
                MarkTrimStartAtPlayhead();
                e.Handled = true;
                break;
            case VirtualKey.O:
                MarkTrimEndAtPlayhead();
                e.Handled = true;
                break;
        }
    }

    private void OnTimelinePointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(TimelineSurface).Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        if (IsKeyDown(VirtualKey.Control))
        {
            var pointerX = e.GetCurrentPoint(TimelineSurface).Position.X;
            SetZoom(_zoomFactor * Math.Pow(1.5, delta / 120d), pointerX);
        }
        else
        {
            Pan(TimeSpan.FromSeconds(-VisibleDuration.TotalSeconds * 0.12 * delta / 120d));
        }

        e.Handled = true;
    }

    public bool PreviewPresetDrop(string format, Point relativeTimeline) =>
        PreviewPresetDropAtSurface(format, TransformToVisual(TimelineSurface).TransformPoint(relativeTimeline));

    public bool ApplyPresetDrop(string format, string value, Point relativeTimeline, double? transitionDurationSeconds = null) =>
        ApplyPresetDropAtSurface(format, value, TransformToVisual(TimelineSurface).TransformPoint(relativeTimeline), transitionDurationSeconds);

    public void ClearPresetDropPreview()
    {
        _lastExternalDragTick = 0;
        DropTargetHighlight.Visibility = Visibility.Collapsed;
    }

    private bool PreviewPresetDropAtSurface(string format, Point point)
    {
        _clipDoubleClick.Reset();
        var transition = format == TransitionDataFormat;
        var supported = transition || format == ClipLookDataFormat;
        var overRow = TrackAt(point.Y, EditorTimelineTrackKind.Video) is not null;
        if (!supported || !overRow || point.X < HeaderWidth || point.X > HeaderWidth + GetRailWidth())
        {
            ClearPresetDropPreview();
            return false;
        }
        AutoPanExternalDrag(point);
        var clip = transition ? FindTransitionDropTarget(point) : FindLookDropTarget(point);
        if (transition)
            ShowTransitionDropTarget(clip);
        else
            ShowLookDropTarget(clip);
        return clip is not null;
    }

    private bool ApplyPresetDropAtSurface(string format, string rawValue, Point point, double? transitionDurationSeconds = null)
    {
        ClearPresetDropPreview();
        var transition = format == TransitionDataFormat;
        if (!transition && format != ClipLookDataFormat)
            return false;
        var target = transition ? FindTransitionDropTarget(point) : FindLookDropTarget(point);
        if (target is null)
        {
            SetEditStatus(transition ? "Drop near the join between two video clips." : "Drop the look on a video clip.");
            return false;
        }
        var value = rawValue?.Trim().ToLowerInvariant() ?? string.Empty;
        var valid = transition
            ? value == NormalizeTransitionStyle(value, allowInherit: false)
            : value is "none" or "boost" or "film" or "cinematic" or "tealorange" or "warm" or "cool" or
                "soft" or "mono" or "noir" or "vintage" or "vibrant" or "dream" or "sharpen" or "blur" or "vignette" or
                "natural" or "matte" or "goldenhour" or "steel" or "glitch";
        if (!valid)
        {
            SetEditStatus("That preset is not supported.");
            return false;
        }
        EditStarting?.Invoke(this, EventArgs.Empty);
        SelectMediaClipForEditing(target.Id);
        LastEditStatus = string.Empty;
        var changed = transition
            ? SetSelectedMediaClipTransition(value, Math.Clamp(transitionDurationSeconds ?? (_primaryTransitionSeconds > 0 ? _primaryTransitionSeconds : 0.5), 0.1, 2))
            : AddEffectLayerToClip(target.Id, value) is not null;
        if (!changed)
        {
            if (string.IsNullOrEmpty(LastEditStatus))
                SetEditStatus("This preset is already applied to the clip.");
            return false;
        }
        SetEditStatus(transition ? $"Transition applied between clips · {GetTransitionDisplayName(value)}" : $"FX added to the timeline · {target.Name}");
        return true;
    }

    private void AutoPanExternalDrag(Point point)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastExternalDragTick != 0)
        {
            var delta = EditorTimelineInteractionMath.AutoPanDeltaSeconds(point.X, HeaderWidth, GetRailWidth(),
                VisibleDuration.TotalSeconds, (now - _lastExternalDragTick) / (double)Stopwatch.Frequency);
            if (Math.Abs(delta) > 0.00001)
                Pan(TimeSpan.FromSeconds(delta));
        }
        _lastExternalDragTick = now;
    }

    private void OnTimelineDragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(TransitionDataFormat) && !e.DataView.Contains(ClipLookDataFormat) &&
            !e.DataView.Contains(LibraryClipDataFormat) && !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            DropTargetHighlight.Visibility = Visibility.Collapsed;
            return;
        }
        if (e.DataView.Contains(TransitionDataFormat) || e.DataView.Contains(ClipLookDataFormat))
        {
            var point = e.GetPosition(TimelineSurface);
            var transition = e.DataView.Contains(TransitionDataFormat);
            PreviewPresetDropAtSurface(transition ? TransitionDataFormat : ClipLookDataFormat, point);
            var clip = transition ? FindTransitionDropTarget(point) : FindLookDropTarget(point);
            e.AcceptedOperation = clip is null ? DataPackageOperation.None : DataPackageOperation.Copy;
            e.DragUIOverride.Caption = clip is null
                ? transition ? "Drop near the join between two video clips" : "Drop directly on a video clip"
                : transition ? $"Apply transition · {clip.Name}" : $"Apply look · {clip.Name}";
            e.DragUIOverride.IsCaptionVisible = true;
            e.Handled = true;
            return;
        }
        DropTargetHighlight.Visibility = Visibility.Collapsed;
        if (e.DataView.Contains(LibraryClipDataFormat) || e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            AutoPanExternalDrag(e.GetPosition(TimelineSurface));
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Add to project";
            e.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private async void OnTimelineDrop(object sender, DragEventArgs e)
    {
        _lastExternalDragTick = 0;
        DropTargetHighlight.Visibility = Visibility.Collapsed;
        var dropPoint = e.GetPosition(TimelineSurface);
        var transitionDrop = e.DataView.Contains(TransitionDataFormat);
        if (transitionDrop || e.DataView.Contains(ClipLookDataFormat))
        {
            e.Handled = true;
            try
            {
                var format = transitionDrop ? TransitionDataFormat : ClipLookDataFormat;
                if (await e.DataView.GetDataAsync(format) is not string rawValue)
                    return;
                ApplyPresetDropAtSurface(format, rawValue, dropPoint);
            }
            catch (Exception)
            {
                SetEditStatus("The preset could not be dropped. Try dragging it again.");
            }
            return;
        }
        var filePaths = new List<string>();
        if (e.DataView.Contains(LibraryClipDataFormat))
        {
            if (await e.DataView.GetDataAsync(LibraryClipDataFormat) is string filePath)
                filePaths.Add(filePath);
        }
        else if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            filePaths.AddRange((await e.DataView.GetStorageItemsAsync())
                .OfType<Windows.Storage.StorageFile>()
                .Select(file => file.Path));
        }

        filePaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path) &&
                           File.Exists(path) &&
                           SupportedVideoExtensions.Contains(Path.GetExtension(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (filePaths.Count == 0)
            return;

        var point = e.GetPosition(TimelineSurface);
        var timelinePosition = TimeFromX(point.X);
        var row = TrackAt(point.Y, EditorTimelineTrackKind.Video);
        if (row is null || row.Locked)
        { SetEditStatus("Drop video on an unlocked video track."); return; }
        _selectedTrackId = row.Id;
        var firstVideo = _tracks.Where(track => track.Kind == EditorTimelineTrackKind.Video).MinBy(track => track.Order);
        var track = row.Id == firstVideo?.Id ? EditorClipTrack.Primary : EditorClipTrack.Overlay;
        LibraryClipDropped?.Invoke(this, new EditorLibraryClipDropEventArgs(filePaths, track, timelinePosition, row.Id));
    }

    private void OnTimelineDragLeave(object sender, DragEventArgs e)
    {
        ClearPresetDropPreview();
    }

    private EditorProjectClip? FindTransitionDropTarget(Point point)
    {
        var row = TrackAt(point.Y, EditorTimelineTrackKind.Video);
        if (row is null || row.Locked)
            return null;
        var primary = _mediaClips.Where(clip => clip.TrackId == row.Id).OrderBy(clip => clip.TimelineStartSeconds).ThenBy(clip => clip.Order).ToList();
        var incoming = primary.Where(clip => GetTransitionPredecessor(clip, includeTouching: true) is not null).ToList();
        var boundaries = incoming.Select(clip => HeaderWidth + SecondsToX(
            GetClipTimelineStart(clip) + GetPrimaryTransitionOverlap(GetTransitionPredecessor(clip, includeTouching: true)!, clip) / 2,
            GetRailWidth())).ToList();
        var index = EditorTimelineInteractionMath.FindNearestBoundaryIndex(point.X, boundaries, HeaderWidth, GetRailWidth());
        return index < 0 ? null : incoming[index];
    }

    private EditorProjectClip? FindLookDropTarget(Point point)
    {
        if (point.X < HeaderWidth || point.X > HeaderWidth + GetRailWidth())
            return null;
        var track = TrackAt(point.Y, EditorTimelineTrackKind.Video);
        if (track is null || track.Locked)
            return null;
        var time = TimeFromX(point.X).TotalSeconds;
        return _mediaClips.Where(clip => clip.TrackId == track.Id && time >= clip.TimelineStartSeconds &&
            time < clip.TimelineStartSeconds + clip.EffectiveDurationSeconds).OrderByDescending(clip => clip.TimelineStartSeconds).ThenByDescending(clip => clip.Order).FirstOrDefault();
    }

    private void ShowTransitionDropTarget(EditorProjectClip? clip)
    {
        DropTargetHighlight.Visibility = clip is null ? Visibility.Collapsed : Visibility.Visible;
        if (clip is null)
            return;
        var primary = GetPrimaryClips();
        var predecessor = GetTransitionPredecessor(clip, includeTouching: true);
        var center = GetClipTimelineStart(clip) + (predecessor is not null ? GetPrimaryTransitionOverlap(predecessor, clip) / 2 : 0);
        SetCanvasRect(DropTargetHighlight, HeaderWidth + SecondsToX(center, GetRailWidth()) - 12,
            MediaRowTop(clip) - 2, 24, MediaRowHeight(clip) + 4);
    }

    private void ShowLookDropTarget(EditorProjectClip? clip)
    {
        DropTargetHighlight.Visibility = Visibility.Collapsed;
        if (clip is null)
            return;
        var start = TimeSpan.FromSeconds(GetClipTimelineStart(clip));
        if (!TryGetVisibleRect(start, start + TimeSpan.FromSeconds(clip.EffectiveDurationSeconds), GetRailWidth(), out var left, out var width))
            return;
        DropTargetHighlight.Visibility = Visibility.Visible;
        SetCanvasRect(DropTargetHighlight, HeaderWidth + left, MediaRowTop(clip) + 1,
            Math.Max(2, width), MediaRowHeight(clip) - 2);
    }

    private void SetEditStatus(string status)
    {
        LastEditStatus = status;
        EditStatusChanged?.Invoke(this, new EditorTimelineEditStatusEventArgs(status));
    }

    private void NudgeSelected(TimeSpan delta)
    {
        if (HasPrecisionClipSelection)
        {
            MovePrecisionClipSelection(delta);
            return;
        }
        var before = TakeSnapshot();
        if (TryNudgeSelectedLayer(delta))
        {
            CommitUndoSnapshot(before);
            return;
        }

        if (_selectedLayer == EditorTimelineLayer.Trim)
        {
            NudgeTrimRange(delta);
            CommitUndoSnapshot(before);
            return;
        }

        SeekWithKeyboard(_playhead + delta);
    }

    private bool TryNudgeSelectedLayer(TimeSpan delta)
    {
        if (_effectLayers.FirstOrDefault(item => item.Id == SelectedEffectLayerId) is { } effect &&
            _mediaClips.FirstOrDefault(clip => clip.Id == effect.TargetClipId) is { } target)
        {
            if (IsClipLocked(target)) return false;
            var effectLength = effect.EndSeconds - effect.StartSeconds;
            effect.StartSeconds = Math.Clamp(SnapTime(TimeSpan.FromSeconds(effect.StartSeconds) + delta).TotalSeconds,
                target.TimelineStartSeconds, Math.Max(target.TimelineStartSeconds, target.TimelineStartSeconds + target.EffectiveDurationSeconds - effectLength));
            effect.EndSeconds = effect.StartSeconds + effectLength;
            UpdateVisuals(true, _playhead, EditorTimelineLayer.Vfx);
            return true;
        }
        if (!_layers.TryGetValue(_selectedLayer, out var state) || !state.Enabled)
            return false;

        var length = state.End - state.Start;
        var maxStart = _duration - length;
        var start = ClampTime(SnapTime(state.Start + delta), TimeSpan.Zero, maxStart);

        state.Start = start;
        state.End = start + length;
        ClampLayer(state);

        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: _selectedLayer);
        return true;
    }

    private void NudgeTrimRange(TimeSpan delta)
    {
        var length = _end - _start;
        var start = ClampTime(SnapTime(_start + delta), TimeSpan.Zero, _duration - length);
        _start = start;
        _end = start + length;
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Trim);
    }

    private TimeSpan? MoveTrimRange(TimeSpan delta)
    {
        var length = _dragStartLayerEnd - _dragStartLayerStart;
        var start = ClampTime(SnapTime(_dragStartLayerStart + delta), TimeSpan.Zero, _duration - length);
        var snapGuideTime = SnapMoveRange(EditorTimelineLayer.Trim, length, ref start);
        _start = start;
        _end = start + length;
        return snapGuideTime;
    }

    private bool TryRemoveSelectedLayer()
    {
        if (SelectedEffectLayerId is not null)
            return DeleteSelectedEffectLayer();
        if (!_layers.TryGetValue(_selectedLayer, out var state) || !state.Enabled)
            return false;

        var before = TakeSnapshot();
        var removedLayer = _selectedLayer;
        state.Enabled = false;
        _selectedLayer = EditorTimelineLayer.None;
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: removedLayer);
        CommitUndoSnapshot(before);
        return true;
    }

    private void SeekWithKeyboard(TimeSpan position)
    {
        var keepEffectSelection = SelectedEffectLayerId is not null;
        _playhead = SnapTime(position);
        if (!keepEffectSelection)
            _selectedLayer = EditorTimelineLayer.Playhead;
        if (_playhead < VisibleStart || _playhead > VisibleEnd)
        {
            _viewportStart = TimeSpan.FromSeconds(EditorTimelineMath.ZoomViewportStart(
                _playhead.TotalSeconds, 0.25, VisibleDuration.TotalSeconds, _duration.TotalSeconds));
            InvalidateTicks();
        }
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Playhead);
    }

    private void MarkTrimStartAtPlayhead()
    {
        var before = TakeSnapshot();
        _start = ClampTime(_playhead, TimeSpan.Zero, _end - MinimumTrim);
        _selectedLayer = EditorTimelineLayer.Trim;
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Trim);
        CommitUndoSnapshot(before);
    }

    private void MarkTrimEndAtPlayhead()
    {
        var before = TakeSnapshot();
        _end = ClampTime(_playhead, _start + MinimumTrim, _duration);
        _selectedLayer = EditorTimelineLayer.Trim;
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Trim);
        CommitUndoSnapshot(before);
    }

    private TimeSpan? ResizeLayerStart(EditorTimelineLayer layer, TimeSpan time)
    {
        var state = GetLayer(layer);
        state.Start = SnapEdgeTime(time, layer, TimeSpan.Zero, state.End - TimeSpan.FromSeconds(MinLayerSeconds), out var snapGuideTime);
        ClampLayer(state);
        return snapGuideTime;
    }

    private TimeSpan? ResizeLayerEnd(EditorTimelineLayer layer, TimeSpan time)
    {
        var state = GetLayer(layer);
        state.End = SnapEdgeTime(time, layer, state.Start + TimeSpan.FromSeconds(MinLayerSeconds), _duration, out var snapGuideTime);
        ClampLayer(state);
        return snapGuideTime;
    }

    private TimeSpan? MoveLayer(EditorTimelineLayer layer, TimeSpan delta)
    {
        var state = GetLayer(layer);
        var length = _dragStartLayerEnd - _dragStartLayerStart;
        var start = _dragStartLayerStart + delta;
        start = ClampTime(SnapTime(start), TimeSpan.Zero, _duration - length);
        var snapGuideTime = SnapMoveRange(layer, length, ref start);
        state.Start = start;
        state.End = ClampTime(start + length, start + TimeSpan.FromSeconds(MinLayerSeconds), _duration);
        return snapGuideTime;
    }

    private TimeSpan ResizeGraphicSegmentStart(TimeSpan time, out TimeSpan? snapGuideTime)
    {
        snapGuideTime = null;
        var segment = GetDraggedGraphicSegment();
        if (segment is null)
            return _playhead;

        var maximum = TimeSpan.FromSeconds(segment.EndSeconds - MinLayerSeconds);
        var start = SnapEdgeTime(
            time,
            EditorTimelineLayer.Element,
            TimeSpan.Zero,
            maximum,
            out snapGuideTime);
        segment.StartSeconds = start.TotalSeconds;
        return start;
    }

    private TimeSpan ResizeGraphicSegmentEnd(TimeSpan time, out TimeSpan? snapGuideTime)
    {
        snapGuideTime = null;
        var segment = GetDraggedGraphicSegment();
        if (segment is null)
            return _playhead;

        var minimum = TimeSpan.FromSeconds(segment.StartSeconds + MinLayerSeconds);
        var end = SnapEdgeTime(
            time,
            EditorTimelineLayer.Element,
            minimum,
            _duration,
            out snapGuideTime);
        segment.EndSeconds = end.TotalSeconds;
        return end;
    }

    private TimeSpan MoveGraphicSegment(TimeSpan time, out TimeSpan? snapGuideTime)
    {
        snapGuideTime = null;
        var segment = GetDraggedGraphicSegment();
        if (segment is null)
            return _playhead;

        var length = TimeSpan.FromSeconds(Math.Max(MinLayerSeconds, _dragGraphicEnd - _dragGraphicStart));
        var start = TimeSpan.FromSeconds(_dragGraphicStart) + (time - _dragStartTime);
        start = ClampTime(start, TimeSpan.Zero, _duration - length);
        snapGuideTime = SnapMoveRange(EditorTimelineLayer.Element, length, ref start);
        segment.StartSeconds = start.TotalSeconds;
        segment.EndSeconds = Math.Min(_duration.TotalSeconds, start.TotalSeconds + length.TotalSeconds);
        var delta = segment.StartSeconds - _dragGraphicStart;
        for (var index = 0; index < segment.Keyframes.Count && index < _dragGraphicKeyframeTimes.Count; index++)
        {
            segment.Keyframes[index].TimeSeconds = Math.Clamp(
                _dragGraphicKeyframeTimes[index] + delta,
                0,
                _duration.TotalSeconds);
        }
        return start;
    }

    private EditorProjectGraphicLayer? GetDraggedGraphicSegment()
    {
        return string.IsNullOrWhiteSpace(_dragGraphicSegmentId)
            ? null
            : _graphicSegments.FirstOrDefault(layer => layer.Id == _dragGraphicSegmentId);
    }

    private void NotifyGraphicSegmentsChanged()
    {
        GraphicSegmentsChanged?.Invoke(
            this,
            new EditorGraphicSegmentsChangedEventArgs(
                GetGraphicSegments(),
                _selectedGraphicSegmentId,
                _playhead));
    }

    private TimeSpan ResizeMediaClipStart(TimeSpan time, out TimeSpan? snapGuideTime)
    {
        snapGuideTime = null;
        var clip = GetDraggedMediaClip();
        if (clip is null)
            return _playhead;
        if (!_pointerDragActivated)
            return time;

        var clipStart = TimeSpan.FromSeconds(_dragMediaTimelineStart);
        var clipEnd = clipStart + TimeSpan.FromSeconds(
            (_dragMediaTrimEnd - _dragMediaTrimStart) / clip.PlaybackSpeed);
        var minimumStart = TimeSpan.FromSeconds(EditorClipEditMath.EarliestTrimStart(
            _dragMediaTimelineStart, _dragMediaTrimStart, clip.PlaybackSpeed));
        var snappedTime = SnapEdgeTime(
            time,
            EditorTimelineLayer.Media,
            minimumStart,
            clipEnd - TimeSpan.FromSeconds(MinClipSeconds / clip.PlaybackSpeed),
            out snapGuideTime);
        var timelineDelta = (snappedTime - clipStart).TotalSeconds;
        clip.TrimStartSeconds = Math.Clamp(
            _dragMediaTrimStart + timelineDelta * clip.PlaybackSpeed,
            0,
            clip.TrimEndSeconds - MinClipSeconds);
        InvalidateLoudnessAnalysis(clip);
        clip.TransformKeyframes = RetimeTransformKeyframesForStartTrim(
            _dragMediaTransformKeyframes,
            new EditorProjectClipTransformState(clip.Scale, clip.OffsetX, clip.OffsetY),
            timelineDelta);
        clip.VolumeKeyframes = RetimeVolumeKeyframesForStartTrim(
            _dragMediaVolumeKeyframes,
            clip.Volume,
            timelineDelta);
        clip.FilterStrengthKeyframes = RetimeFilterKeyframesForStartTrim(
            _dragMediaFilterKeyframes,
            clip.FilterStrength,
            timelineDelta);

        clip.TimelineStartSeconds = snappedTime.TotalSeconds;

        RecalculateDurationFromMedia(clearHistory: false);
        return snappedTime;
    }

    private TimeSpan ResizeMediaClipEnd(TimeSpan time, out TimeSpan? snapGuideTime)
    {
        snapGuideTime = null;
        var clip = GetDraggedMediaClip();
        if (clip is null)
            return _playhead;
        if (!_pointerDragActivated)
            return time;

        var clipStart = TimeSpan.FromSeconds(GetClipTimelineStart(clip));
        var maxTimelineEnd = clipStart + TimeSpan.FromSeconds(
            (clip.SourceDurationSeconds - clip.TrimStartSeconds) / clip.PlaybackSpeed);
        var snappedTime = SnapEdgeTime(
            time,
            EditorTimelineLayer.Media,
            clipStart + TimeSpan.FromSeconds(MinClipSeconds / clip.PlaybackSpeed),
            maxTimelineEnd,
            out snapGuideTime);
        var timelineLength = Math.Max(MinClipSeconds / clip.PlaybackSpeed, (snappedTime - clipStart).TotalSeconds);
        clip.TrimEndSeconds = Math.Clamp(
            clip.TrimStartSeconds + timelineLength * clip.PlaybackSpeed,
            clip.TrimStartSeconds + MinClipSeconds,
            clip.SourceDurationSeconds);
        InvalidateLoudnessAnalysis(clip);
        var originalDuration = (_dragMediaTrimEnd - _dragMediaTrimStart) / clip.PlaybackSpeed;
        clip.TransformKeyframes = RetimeTransformKeyframesForEndTrim(
            _dragMediaTransformKeyframes,
            new EditorProjectClipTransformState(clip.Scale, clip.OffsetX, clip.OffsetY),
            timelineLength,
            originalDuration);
        clip.VolumeKeyframes = RetimeVolumeKeyframesForEndTrim(
            _dragMediaVolumeKeyframes,
            clip.Volume,
            timelineLength,
            originalDuration);
        clip.FilterStrengthKeyframes = RetimeFilterKeyframesForEndTrim(
            _dragMediaFilterKeyframes,
            clip.FilterStrength,
            timelineLength,
            originalDuration);
        RecalculateDurationFromMedia(clearHistory: false);
        return snappedTime;
    }

    private static List<EditorProjectClipTransformKeyframe> RetimeTransformKeyframesForStartTrim(
        IReadOnlyList<EditorProjectClipTransformKeyframe> source,
        EditorProjectClipTransformState fallback,
        double removedSeconds)
    {
        if (source.Count == 0)
            return [];
        source = SamplePartialTransformCurve(source, fallback, removedSeconds, keepAfter: true);
        var stateAtStart = EvaluateTransformKeyframes(source, fallback, Math.Max(0, removedSeconds));
        var result = source
            .Where(keyframe => keyframe.TimeSeconds >= removedSeconds - 0.0001)
            .Select(keyframe =>
            {
                var shifted = keyframe.Clone();
                shifted.TimeSeconds = Math.Max(0, shifted.TimeSeconds - removedSeconds);
                return shifted;
            })
            .ToList();
        if (result.All(keyframe => keyframe.TimeSeconds > 0.0001))
        {
            result.Insert(0, new EditorProjectClipTransformKeyframe
            {
                TimeSeconds = 0,
                Scale = stateAtStart.Scale,
                OffsetX = stateAtStart.OffsetX,
                OffsetY = stateAtStart.OffsetY,
                Easing = "linear"
            });
        }

        return result;
    }

    private static List<EditorProjectClipTransformKeyframe> RetimeTransformKeyframesForEndTrim(
        IReadOnlyList<EditorProjectClipTransformKeyframe> source,
        EditorProjectClipTransformState fallback,
        double newDuration,
        double originalDuration)
    {
        if (source.Count == 0)
            return [];
        source = SamplePartialTransformCurve(source, fallback, newDuration, keepAfter: false);
        var result = source
            .Where(keyframe => keyframe.TimeSeconds <= newDuration + 0.0001)
            .Select(keyframe => keyframe.Clone())
            .ToList();
        if (newDuration < originalDuration - 0.0001 &&
            result.All(keyframe => Math.Abs(keyframe.TimeSeconds - newDuration) > 0.0001))
        {
            var state = EvaluateTransformKeyframes(source, fallback, newDuration);
            result.Add(new EditorProjectClipTransformKeyframe
            {
                TimeSeconds = newDuration,
                Scale = state.Scale,
                OffsetX = state.OffsetX,
                OffsetY = state.OffsetY,
                Easing = source.FirstOrDefault(keyframe => keyframe.TimeSeconds > newDuration)?.Easing ?? "linear"
            });
        }

        return result;
    }

    private static EditorProjectClipTransformState EvaluateTransformKeyframes(
        IReadOnlyList<EditorProjectClipTransformKeyframe> source,
        EditorProjectClipTransformState fallback,
        double timeSeconds)
    {
        var evaluator = new EditorProjectClip
        {
            Scale = fallback.Scale,
            OffsetX = fallback.OffsetX,
            OffsetY = fallback.OffsetY,
            TransformKeyframes = source.Select(keyframe => keyframe.Clone()).ToList()
        };
        return evaluator.EvaluateTransform(timeSeconds);
    }

    private static List<EditorProjectClipVolumeKeyframe> RetimeVolumeKeyframesForStartTrim(
        IReadOnlyList<EditorProjectClipVolumeKeyframe> source,
        double fallback,
        double removedSeconds)
    {
        if (source.Count == 0)
            return [];

        source = SamplePartialVolumeCurve(source, fallback, removedSeconds, keepAfterBoundary: true);
        var volumeAtStart = EvaluateVolumeKeyframes(source, fallback, Math.Max(0, removedSeconds));
        var result = source
            .Where(keyframe => keyframe.TimeSeconds >= removedSeconds - 0.0001)
            .Select(keyframe =>
            {
                var shifted = keyframe.Clone();
                shifted.TimeSeconds = Math.Max(0, shifted.TimeSeconds - removedSeconds);
                return shifted;
            })
            .ToList();
        if (result.All(keyframe => keyframe.TimeSeconds > 0.0001))
        {
            result.Insert(0, new EditorProjectClipVolumeKeyframe
            {
                TimeSeconds = 0,
                Volume = volumeAtStart,
                Easing = "linear"
            });
        }

        return result;
    }

    private static List<EditorProjectClipVolumeKeyframe> RetimeVolumeKeyframesForEndTrim(
        IReadOnlyList<EditorProjectClipVolumeKeyframe> source,
        double fallback,
        double newDuration,
        double originalDuration)
    {
        if (source.Count == 0)
            return [];

        source = SamplePartialVolumeCurve(source, fallback, newDuration, keepAfterBoundary: false);
        var result = source
            .Where(keyframe => keyframe.TimeSeconds <= newDuration + 0.0001)
            .Select(keyframe => keyframe.Clone())
            .ToList();
        if (newDuration < originalDuration - 0.0001 &&
            result.All(keyframe => Math.Abs(keyframe.TimeSeconds - newDuration) > 0.0001))
        {
            result.Add(new EditorProjectClipVolumeKeyframe
            {
                TimeSeconds = newDuration,
                Volume = EvaluateVolumeKeyframes(source, fallback, newDuration),
                Easing = source.FirstOrDefault(keyframe => keyframe.TimeSeconds > newDuration)?.Easing ?? "linear"
            });
        }

        return result;
    }

    private static IReadOnlyList<EditorProjectClipVolumeKeyframe> SamplePartialVolumeCurve(
        IReadOnlyList<EditorProjectClipVolumeKeyframe> source, double fallback,
        double boundary, bool keepAfterBoundary)
    {
        return EditorKeyframeTrimMath.SampleTruncatedCurve(source, boundary, keepAfterBoundary,
            key => key.TimeSeconds, key => key.Easing, time => new EditorProjectClipVolumeKeyframe
            { TimeSeconds = time, Volume = EvaluateVolumeKeyframes(source, fallback, time), Easing = "linear" });
    }

    private static double EvaluateVolumeKeyframes(
        IReadOnlyList<EditorProjectClipVolumeKeyframe> source,
        double fallback,
        double timeSeconds)
    {
        var evaluator = new EditorProjectClip
        {
            Volume = fallback,
            VolumeKeyframes = source.Select(keyframe => keyframe.Clone()).ToList()
        };
        return evaluator.EvaluateVolume(timeSeconds);
    }

    private static List<EditorProjectClipFilterKeyframe> RetimeFilterKeyframesForStartTrim(
        IReadOnlyList<EditorProjectClipFilterKeyframe> source,
        double fallback,
        double removedSeconds)
    {
        if (source.Count == 0)
            return [];
        source = SamplePartialFilterCurve(source, fallback, removedSeconds, keepAfter: true);
        var strengthAtStart = EvaluateFilterKeyframes(source, fallback, Math.Max(0, removedSeconds));
        var result = source
            .Where(keyframe => keyframe.TimeSeconds >= removedSeconds - 0.0001)
            .Select(keyframe =>
            {
                var shifted = keyframe.Clone();
                shifted.TimeSeconds = Math.Max(0, shifted.TimeSeconds - removedSeconds);
                return shifted;
            })
            .ToList();
        if (result.All(keyframe => keyframe.TimeSeconds > 0.0001))
        {
            result.Insert(0, new EditorProjectClipFilterKeyframe
            {
                TimeSeconds = 0,
                Strength = strengthAtStart,
                Easing = "linear"
            });
        }

        return result;
    }

    private static List<EditorProjectClipFilterKeyframe> RetimeFilterKeyframesForEndTrim(
        IReadOnlyList<EditorProjectClipFilterKeyframe> source,
        double fallback,
        double newDuration,
        double originalDuration)
    {
        if (source.Count == 0)
            return [];
        source = SamplePartialFilterCurve(source, fallback, newDuration, keepAfter: false);
        var result = source
            .Where(keyframe => keyframe.TimeSeconds <= newDuration + 0.0001)
            .Select(keyframe => keyframe.Clone())
            .ToList();
        if (newDuration < originalDuration - 0.0001 &&
            result.All(keyframe => Math.Abs(keyframe.TimeSeconds - newDuration) > 0.0001))
        {
            result.Add(new EditorProjectClipFilterKeyframe
            {
                TimeSeconds = newDuration,
                Strength = EvaluateFilterKeyframes(source, fallback, newDuration),
                Easing = source.FirstOrDefault(keyframe => keyframe.TimeSeconds > newDuration)?.Easing ?? "linear"
            });
        }

        return result;
    }

    private static double EvaluateFilterKeyframes(
        IReadOnlyList<EditorProjectClipFilterKeyframe> source,
        double fallback,
        double timeSeconds)
    {
        var evaluator = new EditorProjectClip
        {
            FilterStrength = fallback,
            FilterStrengthKeyframes = source.Select(keyframe => keyframe.Clone()).ToList()
        };
        return evaluator.EvaluateFilterStrength(timeSeconds);
    }

    private TimeSpan ResizeTransitionDuration(double pointerX, out TimeSpan? snapGuideTime)
    {
        snapGuideTime = null;
        var clip = GetDraggedMediaClip();
        if (clip is null || IsClipLocked(clip))
            return _playhead;
        if (!_pointerDragActivated)
            return _dragStartPlayhead;

        if (_dragUndoSnapshot?.MediaClips.FirstOrDefault(item => item.Id == clip.Id) is { } original)
        {
            var oldPredecessor = _dragUndoSnapshot.MediaClips.Where(item => item.TrackId == original.TrackId)
                .OrderBy(item => item.TimelineStartSeconds).ThenBy(item => item.Order).TakeWhile(item => item.Id != original.Id)
                .LastOrDefault(item => item.TimelineStartSeconds + item.EffectiveDurationSeconds >= original.TimelineStartSeconds - .001);
            _mediaClips[_mediaClips.FindIndex(item => item.Id == original.Id)] = CloneClip(original);
            if (oldPredecessor is not null)
                _mediaClips[_mediaClips.FindIndex(item => item.Id == oldPredecessor.Id)] = CloneClip(oldPredecessor);
            clip = GetDraggedMediaClip()!;
            _mediaTimelineStartsDirty = true;
        }
        var predecessor = GetTransitionPredecessor(clip, includeTouching: true);
        if (predecessor is null)
            return _playhead;

        var rawDuration = _dragTransitionDuration + _transitionResizeDirection *
                          (pointerX - _dragTransitionPointerX) * _dragTransitionSecondsPerPixel;
        var step = Math.Min(0.25, Math.Max(0.05, SnapStep.TotalSeconds));
        var snappedDuration = Math.Round(rawDuration / step, MidpointRounding.AwayFromZero) * step;
        var maxDuration = Math.Min(2, Math.Min(
            predecessor.EffectiveDurationSeconds / 2,
            clip.EffectiveDurationSeconds / 2));
        var duration = Math.Clamp(snappedDuration, Math.Min(0.1, maxDuration), maxDuration);

        var style = string.Equals(clip.TransitionInStyle, "inherit", StringComparison.OrdinalIgnoreCase)
            ? _primaryTransitionStyle : clip.TransitionInStyle;
        if (!PrepareTransitionTarget(clip, style, duration))
        {
            RecalculateDurationFromMedia(false);
            return _dragStartPlayhead;
        }
        clip.TransitionInStyle = style;
        clip.TransitionInDurationSeconds = duration;
        RecalculateDurationFromMedia(clearHistory: false);
        var transitionStart = TimeSpan.FromSeconds(GetClipTimelineStart(clip));
        snapGuideTime = transitionStart;
        return transitionStart;
    }

    private TimeSpan MoveMediaClip(TimeSpan time, out TimeSpan? snapGuideTime)
    {
        snapGuideTime = null;
        var clip = GetDraggedMediaClip();
        if (clip is null)
            return _playhead;
        if (!_pointerDragActivated)
            return time;

        var proposed = TimeSpan.FromSeconds(EditorClipEditMath.MoveTimelineStart(
            _dragMediaTimelineStart, _dragStartTime.TotalSeconds, time.TotalSeconds, _frameRate));
        var length = TimeSpan.FromSeconds(clip.EffectiveDurationSeconds);
        var start = TimeSpan.FromSeconds(EditorTimelineMath.SnapToFrame(Math.Max(0, proposed.TotalSeconds), _frameRate));
        snapGuideTime = SnapMoveRange(EditorTimelineLayer.Media, length, ref start, allowExtension: true);
        clip.TimelineStartSeconds = Math.Max(0, start.TotalSeconds);
        _mediaTimelineStartsDirty = true;
        EnsureDragDuration(clip.TimelineStartSeconds + clip.EffectiveDurationSeconds);
        return start;
    }

    private EditorProjectClip? GetDraggedMediaClip()
    {
        return string.IsNullOrWhiteSpace(_dragMediaClipId)
            ? null
            : _mediaClips.FirstOrDefault(clip => clip.Id == _dragMediaClipId);
    }

    private TimeSpan MoveMarker(TimeSpan time, out TimeSpan? snapGuideTime)
    {
        var marker = _markers.FirstOrDefault(item => item.Id == _dragMarkerId);
        if (marker is null)
        {
            snapGuideTime = null;
            return _playhead;
        }

        var snapped = SnapEdgeTime(
            time,
            EditorTimelineLayer.Marker,
            TimeSpan.Zero,
            _duration,
            out snapGuideTime);
        marker.TimeSeconds = snapped.TotalSeconds;
        return snapped;
    }

    private TimeSpan SnapEdgeTime(
        TimeSpan time,
        EditorTimelineLayer editedLayer,
        TimeSpan min,
        TimeSpan max,
        out TimeSpan? snapGuideTime)
    {
        time = ClampTime(SnapTime(time), min, max);
        if (TryFindSnapCandidate(time, editedLayer, min, max, out var snapped))
        {
            snapGuideTime = snapped;
            return snapped;
        }

        snapGuideTime = null;
        return time;
    }

    private TimeSpan? SnapMoveRange(EditorTimelineLayer editedLayer, TimeSpan length, ref TimeSpan start, bool allowExtension = false)
    {
        var maxStart = allowExtension ? TimeSpan.MaxValue - length : _duration - length;
        var proposedStart = ClampTime(start, TimeSpan.Zero, maxStart);
        if (!SnappingEnabled || IsKeyDown(VirtualKey.Menu))
        {
            start = proposedStart;
            return null;
        }
        var proposedEnd = proposedStart + length;
        var threshold = SnapMagnetThreshold;
        var bestDistance = TimeSpan.MaxValue;
        var bestDelta = TimeSpan.Zero;
        TimeSpan? bestGuideTime = null;

        void TrySnap(TimeSpan edge, TimeSpan candidate)
        {
            var delta = candidate - edge;
            var snappedStart = proposedStart + delta;
            if (snappedStart < TimeSpan.Zero || snappedStart > maxStart)
                return;

            var distance = Abs(delta);
            if (distance <= threshold && distance < bestDistance)
            {
                bestDistance = distance;
                bestDelta = delta;
                bestGuideTime = candidate;
            }
        }

        ForEachSnapCandidate(editedLayer, candidate =>
        {
            TrySnap(proposedStart, candidate);
            TrySnap(proposedEnd, candidate);
        });

        if (bestGuideTime is null)
        {
            start = proposedStart;
            return null;
        }

        start = ClampTime(proposedStart + bestDelta, TimeSpan.Zero, maxStart);
        return bestGuideTime;
    }

    private bool TryFindSnapCandidate(
        TimeSpan time,
        EditorTimelineLayer editedLayer,
        TimeSpan min,
        TimeSpan max,
        out TimeSpan snapped)
    {
        if (!SnappingEnabled || IsKeyDown(VirtualKey.Menu))
        {
            snapped = time;
            return false;
        }
        var threshold = SnapMagnetThreshold;
        var bestDistance = TimeSpan.MaxValue;
        var bestCandidate = time;
        var found = false;

        ForEachSnapCandidate(editedLayer, candidate =>
        {
            if (candidate < min || candidate > max)
                return;

            var distance = Abs(candidate - time);
            if (distance <= threshold && distance < bestDistance)
            {
                bestDistance = distance;
                bestCandidate = candidate;
                found = true;
            }
        });

        snapped = bestCandidate;
        return found;
    }

    private void ForEachSnapCandidate(EditorTimelineLayer editedLayer, Action<TimeSpan> action)
    {
        action(TimeSpan.Zero);
        // A drag can extend the project. Its previous frame's moving end is
        // not a stationary target, or slow drags keep snapping back to themselves.
        action(_dragVisibleDuration is not null ? _dragOriginalDuration : _duration);
        action(_dragMode == DragMode.None ? _playhead : _dragStartPlayhead);

        if (editedLayer != EditorTimelineLayer.Trim)
        {
            action(_start);
            action(_dragRangeEndFollowedDuration && _dragVisibleDuration is not null ? _dragOriginalDuration : _end);
        }

        foreach (var pair in _layers)
        {
            if (pair.Key == editedLayer || !pair.Value.Enabled)
                continue;

            action(pair.Value.Start);
            action(pair.Value.End);
        }

        foreach (var clip in _mediaClips)
        {
            if (clip.Id == _dragMediaClipId || (_dragMode is DragMode.MediaMove or DragMode.AudioMove && _selectedMediaClipIds.Contains(clip.Id)))
                continue;
            var start = TimeSpan.FromSeconds(GetClipTimelineStart(clip));
            action(start);
            action(start + TimeSpan.FromSeconds(clip.EffectiveDurationSeconds));
            foreach (var keyframe in clip.TransformKeyframes)
                action(start + TimeSpan.FromSeconds(keyframe.TimeSeconds));
            foreach (var keyframe in clip.VolumeKeyframes)
                action(start + TimeSpan.FromSeconds(keyframe.TimeSeconds));
            foreach (var keyframe in clip.FilterStrengthKeyframes)
                action(start + TimeSpan.FromSeconds(keyframe.TimeSeconds));
        }

        foreach (var segment in _graphicSegments)
        {
            if (segment.Id == _dragGraphicSegmentId)
                continue;
            action(TimeSpan.FromSeconds(segment.StartSeconds));
            action(TimeSpan.FromSeconds(segment.EndSeconds));
        }

        foreach (var effect in _effectLayers)
        {
            if (effect.Id == _dragEffectLayerId) continue;
            if (_dragMode is DragMode.MediaMove or DragMode.AudioMove &&
                _selectedMediaClipIds.Contains(effect.TargetClipId)) continue;
            action(TimeSpan.FromSeconds(effect.StartSeconds));
            action(TimeSpan.FromSeconds(effect.EndSeconds));
        }

        foreach (var track in _audioTrackSegments)
        {
            if (track.Id == _dragAudioTrackId || (_dragMode is DragMode.MediaMove or DragMode.AudioMove && _selectedAudioTrackIds.Contains(track.Id)))
                continue;
            action(TimeSpan.FromSeconds(track.TimelineStartSeconds));
            action(TimeSpan.FromSeconds(track.TimelineStartSeconds + track.EffectiveDurationSeconds));
        }

        foreach (var marker in _markers)
        {
            if (marker.Id == _dragMarkerId)
                continue;
            action(TimeSpan.FromSeconds(marker.TimeSeconds));
        }

        if (editedLayer != EditorTimelineLayer.Text)
        {
            foreach (var keyframe in _textKeyframes)
                action(TimeSpan.FromSeconds(keyframe.TimeSeconds));
        }
    }

    private TimelineSnapshot TakeSnapshot()
    {
        SynchronizeEffectTargets();
        var layers = new Dictionary<EditorTimelineLayer, LayerSnapshot>();
        foreach (var pair in _layers)
        {
            layers[pair.Key] = new LayerSnapshot(
                pair.Value.Label,
                pair.Value.Enabled,
                pair.Value.Start,
                pair.Value.End);
        }

        return new TimelineSnapshot(
            _start,
            _end,
            _playhead,
            _selectedLayer,
            layers,
            _mediaClips.Select(CloneClip).ToList(),
            _selectedMediaClipIds.ToList(),
            _audioTrackSegments.Select(CloneAudioTrack).ToList(),
            _selectedAudioTrackId)
        {
            Tracks = GetTracks().ToList(),
            Markers = _markers.Select(CloneMarker).ToList(),
            EffectLayers = _effectLayers.Select(CloneEffectLayer).ToList(),
            SelectedEffectLayerId = _selectedEffectLayerId,
            SelectedAudioTrackIds = _selectedAudioTrackIds.ToList()
        };
    }

    private void RestoreSnapshot(TimelineSnapshot snapshot)
    {
        _start = snapshot.Start;
        _end = snapshot.End;
        _selectedLayer = snapshot.SelectedLayer;

        foreach (var pair in _layers)
        {
            if (!snapshot.Layers.TryGetValue(pair.Key, out var layer))
                continue;

            pair.Value.Label = layer.Label;
            pair.Value.Enabled = layer.Enabled;
            pair.Value.Start = layer.Start;
            pair.Value.End = layer.End;
        }

        _mediaClips.Clear();
        _mediaClips.AddRange(snapshot.MediaClips.Select(CloneClip));
        _audioTrackSegments.Clear();
        _audioTrackSegments.AddRange(snapshot.AudioTracks.Select(CloneAudioTrack));
        _effectLayers.Clear();
        _effectLayers.AddRange(snapshot.EffectLayers.Select(CloneEffectLayer));
        _selectedEffectLayerId = snapshot.SelectedEffectLayerId;
        RememberEffectTargetPositions();
        RebuildEffectLayerBlocks();
        _selectedAudioTrackId = snapshot.SelectedAudioTrackId;
        _selectedAudioTrackIds.Clear();
        _selectedAudioTrackIds.UnionWith(snapshot.SelectedAudioTrackIds);
        _tracks.Clear();
        _tracks.AddRange(snapshot.Tracks.Select(track => track.Clone()));
        EnsureTimelineTracks();
        RebuildTrackRows();
        _selectedMediaClipIds.Clear();
        foreach (var id in snapshot.SelectedMediaClipIds)
            _selectedMediaClipIds.Add(id);
        NormalizeMediaClips();
        RecalculateDurationFromMedia(clearHistory: false);
        _markers.Clear();
        _markers.AddRange(snapshot.Markers.Select(CloneMarker));
        RebuildMarkerVisuals();
        _start = ClampTime(snapshot.Start, TimeSpan.Zero, _duration - MinimumTrim);
        _end = ClampTime(snapshot.End, _start + MinimumTrim, _duration);
        RebuildMediaClipBlocks();
        RebuildAudioTrackSegmentBlocks();

        NotifyAudioTracksChanged(EditorMediaChangeKind.History);
        NotifyTracksChanged();
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.None);
        NotifyMediaClipsChanged(EditorMediaChangeKind.History, SelectedMediaClipId);
    }

    private void CommitUndoSnapshot(TimelineSnapshot before)
    {
        if (_editBatchDepth > 0)
            return;
        if (TimelineSnapshotEquals(before, TakeSnapshot()))
            return;
        _clipDoubleClick.Reset();

        _undoHistory.Add(before);
        if (_undoHistory.Count > MaxHistorySnapshots)
            _undoHistory.RemoveAt(0);

        _redoHistory.Clear();
        NotifyHistoryChanged();
    }

    private void NotifyHistoryChanged()
    {
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool TimelineSnapshotEquals(TimelineSnapshot left, TimelineSnapshot right)
    {
        if (left.Start != right.Start ||
            !EffectLayersEqual(left.EffectLayers, right.EffectLayers) ||
            left.SelectedEffectLayerId != right.SelectedEffectLayerId ||
            !TracksEqual(left.Tracks, right.Tracks) ||
            !left.Markers.Select(marker => (marker.Id, marker.TimeSeconds, marker.Label, marker.Notes, marker.ColorLabel, marker.Kind))
                .SequenceEqual(right.Markers.Select(marker => (marker.Id, marker.TimeSeconds, marker.Label, marker.Notes, marker.ColorLabel, marker.Kind))) ||
            !left.SelectedAudioTrackIds.ToHashSet(StringComparer.Ordinal).SetEquals(right.SelectedAudioTrackIds) ||
            left.End != right.End ||
            left.Playhead != right.Playhead ||
            left.SelectedLayer != right.SelectedLayer ||
            left.Layers.Count != right.Layers.Count ||
            left.MediaClips.Count != right.MediaClips.Count ||
            left.AudioTracks.Count != right.AudioTracks.Count ||
            left.SelectedAudioTrackId != right.SelectedAudioTrackId ||
            !left.SelectedMediaClipIds.ToHashSet(StringComparer.Ordinal)
                .SetEquals(right.SelectedMediaClipIds))
        {
            return false;
        }

        foreach (var pair in left.Layers)
        {
            if (!right.Layers.TryGetValue(pair.Key, out var other))
                return false;

            if (pair.Value.Label != other.Label ||
                pair.Value.Enabled != other.Enabled ||
                pair.Value.Start != other.Start ||
                pair.Value.End != other.End)
            {
                return false;
            }
        }

        for (var index = 0; index < left.AudioTracks.Count; index++)
        {
            var a = left.AudioTracks[index];
            var b = right.AudioTracks[index];
            if (a.Id != b.Id || a.TrackId != b.TrackId || a.GroupId != b.GroupId || a.SourceClipId != b.SourceClipId || a.SourcePath != b.SourcePath || a.Name != b.Name ||
                a.SourceAudioStreamIndex != b.SourceAudioStreamIndex ||
                a.SourceAudioRole != b.SourceAudioRole ||
                a.TrimStartSeconds != b.TrimStartSeconds || a.TrimEndSeconds != b.TrimEndSeconds ||
                a.TimelineStartSeconds != b.TimelineStartSeconds || a.PlaybackSpeed != b.PlaybackSpeed ||
                a.Volume != b.Volume || a.Muted != b.Muted || a.NormalizeAudio != b.NormalizeAudio ||
                a.NormalizationGainDb != b.NormalizationGainDb || a.IntegratedLoudnessLufs != b.IntegratedLoudnessLufs ||
                a.DuckingEnabled != b.DuckingEnabled || a.DuckingStrength != b.DuckingStrength ||
                a.NoiseReductionEnabled != b.NoiseReductionEnabled || a.NoiseReductionStrength != b.NoiseReductionStrength ||
                a.FadeInSeconds != b.FadeInSeconds || a.FadeOutSeconds != b.FadeOutSeconds ||
                !VolumeKeyframesEqual(a.VolumeKeyframes, b.VolumeKeyframes))
                return false;
        }

        for (var index = 0; index < left.MediaClips.Count; index++)
        {
            var a = left.MediaClips[index];
            var b = right.MediaClips[index];
            if (a.Id != b.Id ||
                a.TrackId != b.TrackId || a.GroupId != b.GroupId ||
                !a.Effects.Select(effect => (effect.Id, effect.Kind, effect.Enabled))
                    .SequenceEqual(b.Effects.Select(effect => (effect.Id, effect.Kind, effect.Enabled))) ||
                a.Track != b.Track ||
                a.Order != b.Order ||
                a.SourcePath != b.SourcePath ||
                a.SourceAudioSeparated != b.SourceAudioSeparated ||
                a.MaskShape != b.MaskShape || a.MaskRoundness != b.MaskRoundness || a.MaskInverted != b.MaskInverted ||
                !CurvePointsEqual(a.MaskPolygon, b.MaskPolygon) || !VideoProcessingEqual(a.VideoProcessing, b.VideoProcessing) ||
                a.Name != b.Name ||
                a.Notes != b.Notes ||
                a.ColorLabel != b.ColorLabel ||
                Math.Abs(a.TrimStartSeconds - b.TrimStartSeconds) > 0.0001 ||
                Math.Abs(a.TrimEndSeconds - b.TrimEndSeconds) > 0.0001 ||
                Math.Abs(a.TimelineStartSeconds - b.TimelineStartSeconds) > 0.0001 ||
                Math.Abs(a.PlaybackSpeed - b.PlaybackSpeed) > 0.0001 ||
                Math.Abs(a.Volume - b.Volume) > 0.0001 ||
                a.Muted != b.Muted ||
                a.NormalizeAudio != b.NormalizeAudio ||
                Math.Abs(a.NormalizationGainDb - b.NormalizationGainDb) > 0.0001 ||
                !Nullable.Equals(a.IntegratedLoudnessLufs, b.IntegratedLoudnessLufs) ||
                Math.Abs(a.LoudnessAnalysisTrimStartSeconds - b.LoudnessAnalysisTrimStartSeconds) > 0.0001 ||
                Math.Abs(a.LoudnessAnalysisTrimEndSeconds - b.LoudnessAnalysisTrimEndSeconds) > 0.0001 ||
                !VolumeKeyframesEqual(a.VolumeKeyframes, b.VolumeKeyframes) ||
                Math.Abs(a.Scale - b.Scale) > 0.0001 ||
                Math.Abs(a.OffsetX - b.OffsetX) > 0.0001 ||
                Math.Abs(a.OffsetY - b.OffsetY) > 0.0001 ||
                !TransformKeyframesEqual(a.TransformKeyframes, b.TransformKeyframes) ||
                Math.Abs(a.CropLeft - b.CropLeft) > 0.0001 ||
                Math.Abs(a.CropTop - b.CropTop) > 0.0001 ||
                Math.Abs(a.CropRight - b.CropRight) > 0.0001 ||
                Math.Abs(a.CropBottom - b.CropBottom) > 0.0001 ||
                a.Filter != b.Filter ||
                Math.Abs(a.FilterStrength - b.FilterStrength) > 0.0001 ||
                !FilterKeyframesEqual(a.FilterStrengthKeyframes, b.FilterStrengthKeyframes) ||
                Math.Abs(a.Brightness - b.Brightness) > 0.0001 ||
                Math.Abs(a.Contrast - b.Contrast) > 0.0001 ||
                Math.Abs(a.Saturation - b.Saturation) > 0.0001 ||
                Math.Abs(a.Temperature - b.Temperature) > 0.0001 ||
                Math.Abs(a.Shadows - b.Shadows) > 0.0001 ||
                Math.Abs(a.Highlights - b.Highlights) > 0.0001 ||
                Math.Abs(a.Gamma - b.Gamma) > 0.0001 ||
                Math.Abs(a.HueDegrees - b.HueDegrees) > 0.0001 ||
                !CurvePointsEqual(a.ToneCurve, b.ToneCurve) ||
                Math.Abs(a.Sharpen - b.Sharpen) > 0.0001 ||
                Math.Abs(a.Blur - b.Blur) > 0.0001 ||
                Math.Abs(a.Vignette - b.Vignette) > 0.0001 ||
                a.LutPath != b.LutPath ||
                Math.Abs(a.LutStrength - b.LutStrength) > 0.0001 ||
                a.TransitionInStyle != b.TransitionInStyle ||
                Math.Abs(a.TransitionInDurationSeconds - b.TransitionInDurationSeconds) > 0.0001)
            {
                return false;
            }
        }

        return true;
    }

    private void UpdateVisuals(bool notify, TimeSpan? previewTime, EditorTimelineLayer changedLayer, bool selectionOnly = false)
    {
        SynchronizeEffectTargets();
        var totalWidth = Math.Max(240, TimelineSurface.ActualWidth);
        var totalHeight = TimelineContentHeight;
        var railWidth = GetRailWidth();
        var railLeft = HeaderWidth;

        TimelineCanvas.Width = totalWidth;
        TimelineCanvas.Height = totalHeight;

        DrawRows(totalWidth, railLeft);
        DrawTicksIfNeeded(railLeft, railWidth);
        DrawVideoBlock(railLeft, railWidth);
        DrawAudioBlock(railLeft, railWidth);
        DrawMediaClipBlocks(railLeft, railWidth);
        DrawLinkedAudioBlocks(railLeft, railWidth);
        DrawTransitionHandles(railLeft, railWidth);
        DrawSubtitleSegments(railLeft, railWidth);
        DrawGraphicSegments(railLeft, railWidth);
        DrawEffectLayers(railLeft, railWidth);
        DrawAudioTrackSegments(railLeft, railWidth);
        DrawAudioAutomation(railLeft, railWidth);
        DrawMarkers(railLeft, railWidth);
        DrawLayerBlock(EditorTimelineLayer.Vfx, VfxBlock, VfxBlockText, VfxRowTop, railLeft, railWidth);
        DrawLayerBlock(EditorTimelineLayer.Text, TextBlockClip, TextBlockText, TextRowTop, railLeft, railWidth);
        DrawTextKeyframes(railLeft, railWidth);
        DrawLayerBlock(EditorTimelineLayer.Subtitle, SubtitleBlock, SubtitleBlockText, SubtitleRowTop, railLeft, railWidth);
        DrawLayerBlock(EditorTimelineLayer.Element, ElementBlock, ElementBlockText, ElementRowTop, railLeft, railWidth);
        DrawPlayhead(railLeft, railWidth, totalHeight);
        if (HoverGuide.Visibility == Visibility.Visible)
            UpdateHoverVisual(show: true);
        if (_snapGuideTime is not null)
            UpdateSnapVisual(_snapGuideTime);

        if (notify && _editBatchDepth == 0)
            TimelineChanged?.Invoke(this, CreateEventArgs(
                changedLayer is EditorTimelineLayer.Playhead or EditorTimelineLayer.Marker
                    ? previewTime ?? _playhead : _playhead, changedLayer, selectionOnly));
    }

    private void UpdatePlayheadVisual()
    {
        if (TimelineSurface.ActualWidth <= 0)
            return;

        DrawPlayhead(HeaderWidth, GetRailWidth(), TimelineContentHeight);
    }

    private void DrawRows(double totalWidth, double railLeft)
    {
        EffectsGroupHeader.Visibility = EffectsGroupLabel.Visibility =
            HasEffectTracks ? Visibility.Visible : Visibility.Collapsed;
        VfxRow.Visibility = VfxLabel.Visibility = ShowVfxTrack ? Visibility.Visible : Visibility.Collapsed;
        TextRow.Visibility = TextLabel.Visibility = ShowTextTrack ? Visibility.Visible : Visibility.Collapsed;
        SubtitleRow.Visibility = SubtitleLabel.Visibility = ShowSubtitleTrack ? Visibility.Visible : Visibility.Collapsed;
        ElementRow.Visibility = ElementLabel.Visibility = ShowElementTrack ? Visibility.Visible : Visibility.Collapsed;
        SetGroupHeader(EffectsGroupHeader, EffectsGroupLabel, EffectsGroupHeaderTop, totalWidth);
        SetGroupHeader(MediaGroupHeader, MediaGroupLabel, MediaGroupHeaderTop, totalWidth);

        SetRow(VfxRow, VfxRowTop, totalWidth);
        SetRow(TextRow, TextRowTop, totalWidth);
        SetRow(SubtitleRow, SubtitleRowTop, totalWidth);
        SetRow(ElementRow, ElementRowTop, totalWidth);
        SetRow(OverlayRow, OverlayRowTop, totalWidth);
        SetRow(VideoRow, VideoRowTop, totalWidth);
        SetRow(AudioRow, AudioRowTop, totalWidth);
        AudioRow.Visibility = AudioLabel.Visibility = ShowLinkedAudioTrack ? Visibility.Visible : Visibility.Collapsed;
        AudioTracksRow.Visibility = AudioTracksLabel.Visibility =
            _audioTrackSegments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SetRow(AudioTracksRow, AudioTracksRowTop, totalWidth);
        AudioTracksRow.Height = _audioTrackLaneCount * (TrackHeight + TrackGap) - TrackGap;

        SetLabel(VfxLabel, VfxRowTop);
        SetLabel(TextLabel, TextRowTop);
        SetLabel(SubtitleLabel, SubtitleRowTop);
        SetLabel(ElementLabel, ElementRowTop);
        SetLabel(OverlayLabel, OverlayRowTop);
        SetLabel(VideoLabel, VideoRowTop);
        SetLabel(AudioLabel, AudioRowTop);
        SetLabel(AudioTracksLabel, AudioTracksRowTop);

        SetRowSelection(VfxRow, VfxLabel, EditorTimelineLayer.Vfx);
        SetRowSelection(TextRow, TextLabel, EditorTimelineLayer.Text);
        SetRowSelection(SubtitleRow, SubtitleLabel, EditorTimelineLayer.Subtitle);
        SetRowSelection(ElementRow, ElementLabel, EditorTimelineLayer.Element);
        SetRowSelection(OverlayRow, OverlayLabel, EditorTimelineLayer.Media);
        SetRowSelection(VideoRow, VideoLabel, EditorTimelineLayer.Media);
        SetRowSelection(AudioRow, AudioLabel, EditorTimelineLayer.Media);
        SetRowSelection(AudioTracksRow, AudioTracksLabel, EditorTimelineLayer.Audio);
        OverlayRow.Visibility = VideoRow.Visibility = AudioTracksRow.Visibility = Visibility.Collapsed;
        OverlayLabel.Visibility = VideoLabel.Visibility = AudioTracksLabel.Visibility = Visibility.Collapsed;
        DrawPersistentTracks(totalWidth);
    }

    private void DrawTicksIfNeeded(double railLeft, double railWidth)
    {
        var visibleStart = VisibleStart;
        var visibleDuration = VisibleDuration;
        var majorEvery = GetMajorTickSeconds();
        if (Math.Abs(_lastTickRailWidth - railWidth) < 0.5
            && Math.Abs(_lastTickDurationSeconds - _duration.TotalSeconds) < 0.001
            && Math.Abs(_lastTickMajorSeconds - majorEvery) < 0.001
            && Math.Abs(_lastTickVisibleStartSeconds - visibleStart.TotalSeconds) < 0.001
            && Math.Abs(_lastTickVisibleDurationSeconds - visibleDuration.TotalSeconds) < 0.001)
        {
            return;
        }

        _lastTickRailWidth = railWidth;
        _lastTickDurationSeconds = _duration.TotalSeconds;
        _lastTickMajorSeconds = majorEvery;
        _lastTickVisibleStartSeconds = visibleStart.TotalSeconds;
        _lastTickVisibleDurationSeconds = visibleDuration.TotalSeconds;
        DrawTicks(railLeft, railWidth, majorEvery);
    }

    private void DrawTicks(double railLeft, double railWidth, double majorEvery)
    {
        TicksCanvas.Children.Clear();
        TicksCanvas.Width = Math.Max(TimelineSurface.ActualWidth, railLeft + railWidth + RailPaddingRight);
        TicksCanvas.Height = RulerHeight;

        var visibleStart = VisibleStart.TotalSeconds;
        var visibleEnd = VisibleEnd.TotalSeconds;
        var minorEvery = majorEvery / 5;
        var firstTick = (long)Math.Ceiling(visibleStart / minorEvery - 0.000001);
        var lastTick = (long)Math.Floor(visibleEnd / minorEvery + 0.000001);

        for (var index = firstTick; index <= lastTick; index++)
        {
            var second = index * minorEvery;
            if (second < -0.001 || second > _duration.TotalSeconds + 0.001)
                continue;

            var isMajor = index % 5 == 0;
            var x = railLeft + SecondsToX(second, railWidth);

            var tick = new Border
            {
                Width = 1,
                Height = isMajor ? 13 : 6,
                Background = ResourceBrush(isMajor ? "KlippTextMutedBrush" : "KlippBorderBrush"),
                Opacity = isMajor ? 0.8 : 0.7
            };
            Canvas.SetLeft(tick, x);
            Canvas.SetTop(tick, isMajor ? 19 : 26);
            TicksCanvas.Children.Add(tick);

            if (isMajor)
            {
                var label = new TextBlock
                {
                    Text = majorEvery < 1
                        ? TimeSpan.FromSeconds(second).ToString(@"m\:ss\.f")
                        : FormatTimestamp(TimeSpan.FromSeconds(second)),
                    Foreground = ResourceBrush("KlippTextMutedBrush"),
                    FontSize = 10
                };
                Canvas.SetLeft(label, Math.Max(railLeft, x - 11));
                Canvas.SetTop(label, 2);
                TicksCanvas.Children.Add(label);
            }
        }
    }

    private void DrawVideoBlock(double railLeft, double railWidth)
    {
        if (_usesExplicitMediaTimeline || _mediaClips.Count > 0)
        {
            VideoBlock.Visibility = Visibility.Collapsed;
            TrimStartThumb.Visibility = Visibility.Collapsed;
            TrimEndThumb.Visibility = Visibility.Collapsed;
            return;
        }

        if (!TryGetVisibleRect(_start, _end, railWidth, out var left, out var width))
        {
            VideoBlock.Visibility = Visibility.Collapsed;
            AudioBlock.Visibility = Visibility.Collapsed;
            TrimStartThumb.Visibility = Visibility.Collapsed;
            TrimEndThumb.Visibility = Visibility.Collapsed;
            return;
        }

        VideoBlock.Visibility = Visibility.Visible;
        SetBlockSelection(VideoBlock, EditorTimelineLayer.Trim);
        SetCanvasRect(VideoBlock, railLeft + left, VideoRowTop + BlockInset, Math.Max(24, width), TrackHeight - BlockInset * 2);

        SetThumbVisibilityAndRect(TrimStartThumb, _start, railLeft, railWidth);
        SetThumbVisibilityAndRect(TrimEndThumb, _end, railLeft, railWidth);
    }

    private void DrawMediaClipBlocks(double railLeft, double railWidth)
    {
        MediaClipsCanvas.Width = TimelineCanvas.Width;
        MediaClipsCanvas.Height = TimelineContentHeight;

        var drawOrder = 0;
        foreach (var clip in _mediaClips.OrderBy(clip => clip.TimelineStartSeconds).ThenBy(clip => clip.Order))
        {
            if (!_mediaClipBlocks.TryGetValue(clip.Id, out var block))
                continue;

            var start = TimeSpan.FromSeconds(GetClipTimelineStart(clip));
            var end = start + TimeSpan.FromSeconds(clip.EffectiveDurationSeconds);
            if (!TryGetVisibleRect(start, end, railWidth, out var left, out var width))
            {
                block.Visibility = Visibility.Collapsed;
                HideMediaKeyframeMarkers(clip.Id);
                continue;
            }

            block.Visibility = Visibility.Visible;
            Canvas.SetZIndex(block, _dragMode == DragMode.MediaMove && clip.Id == _dragMediaClipId ? 10000 : ++drawOrder);
            var rowTop = MediaRowTop(clip);
            var rowHeight = MediaRowHeight(clip);
            SetCanvasRect(block, railLeft + left, rowTop + BlockInset, Math.Max(2, width), rowHeight - BlockInset * 2);
            if (block.Child is UIElement content)
                content.Visibility = width >= 38 ? Visibility.Visible : Visibility.Collapsed;
            var selected = _selectedMediaClipIds.Contains(clip.Id);
            UpdateTrimGrips(block, width, start >= VisibleStart, end <= VisibleEnd, selected);
            block.BorderThickness = new Thickness(selected ? 2 : 1);
            block.BorderBrush = new SolidColorBrush(selected
                ? Windows.UI.Color.FromArgb(255, 224, 230, 238)
                : GetColorLabelBrushColor(
                    clip.ColorLabel,
                    clip.Track == EditorClipTrack.Overlay
                        ? Windows.UI.Color.FromArgb(255, 133, 122, 106)
                        : Windows.UI.Color.FromArgb(255, 107, 132, 157),
                    dark: false));
            block.Opacity = selected ? 1 : 0.9;

            if (_mediaTransformKeyframeMarkers.TryGetValue(clip.Id, out var markers))
            {
                var lastMarkerX = double.NegativeInfinity;
                for (var index = 0; index < markers.Count && index < clip.TransformKeyframes.Count; index++)
                {
                    var marker = markers[index];
                    var markerTime = TimeSpan.FromSeconds(
                        GetClipTimelineStart(clip) + clip.TransformKeyframes[index].TimeSeconds);
                    if (markerTime < VisibleStart || markerTime > VisibleEnd)
                    {
                        marker.Visibility = Visibility.Collapsed;
                        continue;
                    }

                    marker.Visibility = Visibility.Visible;
                    var markerX = railLeft + SecondsToX(markerTime.TotalSeconds, railWidth);
                    if (markerX - lastMarkerX < 6)
                    { marker.Visibility = Visibility.Collapsed; continue; }
                    lastMarkerX = markerX;
                    SetCanvasRect(marker, markerX - 4, rowTop + TrackHeight / 2 - 4, 8, 8);
                    marker.Opacity = selected ? 1 : 0.68;
                    Canvas.SetZIndex(marker, 6);
                }
            }

            if (_mediaVolumeKeyframeMarkers.TryGetValue(clip.Id, out var volumeMarkers))
            {
                var lastMarkerX = double.NegativeInfinity;
                for (var index = 0; index < volumeMarkers.Count && index < clip.VolumeKeyframes.Count; index++)
                {
                    var marker = volumeMarkers[index];
                    var markerTime = TimeSpan.FromSeconds(
                        GetClipTimelineStart(clip) + clip.VolumeKeyframes[index].TimeSeconds);
                    if (markerTime < VisibleStart || markerTime > VisibleEnd)
                    {
                        marker.Visibility = Visibility.Collapsed;
                        continue;
                    }

                    marker.Visibility = Visibility.Visible;
                    var markerX = railLeft + SecondsToX(markerTime.TotalSeconds, railWidth);
                    if (markerX - lastMarkerX < 6)
                    { marker.Visibility = Visibility.Collapsed; continue; }
                    lastMarkerX = markerX;
                    SetCanvasRect(marker, markerX - 4, rowTop + TrackHeight - 10, 8, 8);
                    marker.Opacity = selected ? 1 : 0.68;
                    Canvas.SetZIndex(marker, 7);
                }
            }

            if (_mediaFilterKeyframeMarkers.TryGetValue(clip.Id, out var filterMarkers))
            {
                var lastMarkerX = double.NegativeInfinity;
                for (var index = 0; index < filterMarkers.Count && index < clip.FilterStrengthKeyframes.Count; index++)
                {
                    var marker = filterMarkers[index];
                    var markerTime = TimeSpan.FromSeconds(
                        GetClipTimelineStart(clip) + clip.FilterStrengthKeyframes[index].TimeSeconds);
                    if (markerTime < VisibleStart || markerTime > VisibleEnd)
                    {
                        marker.Visibility = Visibility.Collapsed;
                        continue;
                    }

                    marker.Visibility = Visibility.Visible;
                    var markerX = railLeft + SecondsToX(markerTime.TotalSeconds, railWidth);
                    if (markerX - lastMarkerX < 6)
                    { marker.Visibility = Visibility.Collapsed; continue; }
                    lastMarkerX = markerX;
                    SetCanvasRect(marker, markerX - 4, rowTop + 2, 8, 8);
                    marker.Opacity = selected ? 1 : 0.68;
                    Canvas.SetZIndex(marker, 8);
                }
            }
        }
    }

    private void HideMediaKeyframeMarkers(string clipId)
    {
        if (_mediaTransformKeyframeMarkers.TryGetValue(clipId, out var transforms))
            foreach (var marker in transforms)
                marker.Visibility = Visibility.Collapsed;
        if (_mediaVolumeKeyframeMarkers.TryGetValue(clipId, out var volumes))
            foreach (var marker in volumes)
                marker.Visibility = Visibility.Collapsed;
        if (_mediaFilterKeyframeMarkers.TryGetValue(clipId, out var filters))
            foreach (var marker in filters)
                marker.Visibility = Visibility.Collapsed;
    }

    private void DrawMarkers(double railLeft, double railWidth)
    {
        MarkerCanvas.Width = TimelineCanvas.Width;
        MarkerCanvas.Height = TimelineContentHeight;

        foreach (var marker in _markers)
        {
            if (!_markerVisuals.TryGetValue(marker.Id, out var visual))
                continue;

            var time = TimeSpan.FromSeconds(marker.TimeSeconds);
            if (time < VisibleStart || time > VisibleEnd)
            {
                visual.Line.Visibility = Visibility.Collapsed;
                visual.Cap.Visibility = Visibility.Collapsed;
                continue;
            }

            var x = railLeft + SecondsToX(marker.TimeSeconds, railWidth);
            var selected = marker.Id == _selectedMarkerId;
            var isBeat = string.Equals(marker.Kind, "beat", StringComparison.Ordinal);
            var capSize = isBeat ? 8 : 12;
            visual.Line.Visibility = Visibility.Visible;
            visual.Cap.Visibility = Visibility.Visible;
            visual.Line.Background = new SolidColorBrush(GetColorLabelBrushColor(
                marker.ColorLabel,
                Windows.UI.Color.FromArgb(255, 250, 204, 21),
                dark: false));
            visual.Line.Opacity = selected ? 0.8 : isBeat ? 0.24 : 0.38;
            visual.Cap.Background = visual.Line.Background;
            visual.Cap.BorderBrush = new SolidColorBrush(selected
                ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
                : Windows.UI.Color.FromArgb(255, 24, 24, 27));
            visual.Cap.BorderThickness = new Thickness(selected ? 2 : 1);
            SetCanvasRect(visual.Line, x, RulerHeight - 9, 1, TimelineContentHeight - RulerHeight + 9);
            SetCanvasRect(visual.Cap, x - capSize / 2, 8 - capSize / 2, capSize, capSize);
        }
    }

    private void DrawTransitionHandles(double railLeft, double railWidth)
    {
        TransitionHandlesCanvas.Width = TimelineCanvas.Width;
        TransitionHandlesCanvas.Height = TimelineContentHeight;
        foreach (var handle in _transitionHandleBlocks.Values)
            handle.Visibility = Visibility.Collapsed;
        var primary = _mediaClips.OrderBy(clip => clip.TimelineStartSeconds).ThenBy(clip => clip.Order).ToList();
        for (var index = 0; index < primary.Count; index++)
        {
            var clip = primary[index];
            if (!_transitionHandleBlocks.TryGetValue(clip.Id, out var handle))
                continue;

            var predecessor = GetTransitionPredecessor(clip);
            var overlap = predecessor is null ? 0 : GetPrimaryTransitionOverlap(predecessor, clip);
            if (overlap <= 0.001)
            {
                handle.Visibility = Visibility.Collapsed;
                continue;
            }

            var start = TimeSpan.FromSeconds(GetClipTimelineStart(clip));
            var end = start + TimeSpan.FromSeconds(overlap);
            if (!TryGetVisibleRect(start, end, railWidth, out var left, out var width))
            {
                handle.Visibility = Visibility.Collapsed;
                continue;
            }

            handle.Visibility = Visibility.Visible;
            var label = (handle.Child as Grid)?.Children.OfType<TextBlock>().FirstOrDefault();
            if (label is not null)
                label.Text = $"{overlap:0.##}s";
            var handleWidth = Math.Max(46, width);
            SetCanvasRect(
                handle,
                Math.Clamp(railLeft + left + (width - handleWidth) / 2, railLeft,
                    Math.Max(railLeft, railLeft + railWidth - handleWidth)),
                MediaRowTop(clip) - 3,
                handleWidth,
                17);
            UpdateTrimGrips(handle, handleWidth, true, true, _selectedMediaClipIds.Contains(clip.Id));
            handle.Opacity = _dragMode == DragMode.TransitionMove && _transitionMoveActivated && _dragMediaClipId == clip.Id ? 0.55 : 1;
        }
    }

    private void DrawSubtitleSegments(double railLeft, double railWidth)
    {
        SubtitleSegmentsCanvas.Width = TimelineCanvas.Width;
        SubtitleSegmentsCanvas.Height = TimelineContentHeight;

        foreach (var segment in _subtitleSegments)
        {
            if (!_subtitleSegmentBlocks.TryGetValue(segment.Id, out var block))
                continue;

            var start = TimeSpan.FromSeconds(segment.StartSeconds);
            var end = TimeSpan.FromSeconds(segment.EndSeconds);
            if (!TryGetVisibleRect(start, end, railWidth, out var left, out var width))
            {
                block.Visibility = Visibility.Collapsed;
                continue;
            }

            block.Visibility = Visibility.Visible;
            SetCanvasRect(
                block,
                railLeft + left,
                SubtitleRowTop + BlockInset,
                Math.Max(26, width),
                TrackHeight - BlockInset * 2);
        }
    }

    private void DrawGraphicSegments(double railLeft, double railWidth)
    {
        GraphicSegmentsCanvas.Width = TimelineCanvas.Width;
        GraphicSegmentsCanvas.Height = TimelineContentHeight;

        foreach (var segment in _graphicSegments)
        {
            if (!_graphicSegmentBlocks.TryGetValue(segment.Id, out var block))
                continue;

            var start = TimeSpan.FromSeconds(segment.StartSeconds);
            var end = TimeSpan.FromSeconds(segment.EndSeconds);
            if (!TryGetVisibleRect(start, end, railWidth, out var left, out var width))
            {
                block.Visibility = Visibility.Collapsed;
                if (_graphicKeyframeMarkers.TryGetValue(segment.Id, out var hiddenMarkers))
                    foreach (var marker in hiddenMarkers)
                        marker.Visibility = Visibility.Collapsed;
                continue;
            }

            block.Visibility = Visibility.Visible;
            SetCanvasRect(
                block,
                railLeft + left,
                ElementRowTop + BlockInset,
                Math.Max(26, width),
                TrackHeight - BlockInset * 2);
            var selected = string.Equals(segment.Id, _selectedGraphicSegmentId, StringComparison.Ordinal);
            Canvas.SetZIndex(block, selected ? 2 : 1);
            block.BorderThickness = new Thickness(selected ? 2 : 1);
            block.BorderBrush = new SolidColorBrush(selected
                ? Windows.UI.Color.FromArgb(255, 255, 255, 255)
                : Windows.UI.Color.FromArgb(255, 251, 146, 60));
            block.Opacity = selected ? 1 : 0.88;

            if (!_graphicKeyframeMarkers.TryGetValue(segment.Id, out var markers))
                continue;

            for (var index = 0; index < markers.Count && index < segment.Keyframes.Count; index++)
            {
                var marker = markers[index];
                var keyframeTime = segment.Keyframes[index].TimeSeconds;
                if (keyframeTime < segment.StartSeconds || keyframeTime > segment.EndSeconds ||
                    keyframeTime < VisibleStart.TotalSeconds || keyframeTime > VisibleEnd.TotalSeconds)
                {
                    marker.Visibility = Visibility.Collapsed;
                    continue;
                }

                marker.Visibility = Visibility.Visible;
                var markerX = railLeft + SecondsToX(keyframeTime, railWidth);
                SetCanvasRect(marker, markerX - 5, ElementRowTop + TrackHeight / 2 - 5, 10, 10);
                Canvas.SetZIndex(marker, selected ? 5 : 4);
            }
        }
    }

    private void DrawLinkedAudioBlocks(double railLeft, double railWidth)
    {
        LinkedAudioClipsCanvas.Width = TimelineCanvas.Width;
        LinkedAudioClipsCanvas.Height = TimelineContentHeight;
        foreach (var clip in _mediaClips.Where(clip => clip.Track == EditorClipTrack.Primary))
        {
            if (!_linkedAudioBlocks.TryGetValue(clip.Id, out var block))
                continue;
            var start = TimeSpan.FromSeconds(GetClipTimelineStart(clip));
            var end = start + TimeSpan.FromSeconds(clip.EffectiveDurationSeconds);
            if (clip.Muted || clip.SourceAudioSeparated || !TryGetVisibleRect(start, end, railWidth, out var left, out var width))
            {
                block.Visibility = Visibility.Collapsed;
                continue;
            }
            block.Visibility = Visibility.Visible;
            SetCanvasRect(block, railLeft + left, AudioRowTop + BlockInset,
                Math.Max(2, width), TrackHeight - BlockInset * 2);
            if (block.Child is UIElement content)
                content.Visibility = width >= 38 ? Visibility.Visible : Visibility.Collapsed;
            if (block.Child is Grid grid)
                grid.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, TrackHeight - BlockInset * 2) };
            if (_linkedAudioWaveforms.TryGetValue(clip.Id, out var waveform))
            {
                waveform.Source = _sourceAudioWaveforms.GetValueOrDefault(clip.SourcePath);
                waveform.Width = clip.SourceDurationSeconds / clip.PlaybackSpeed /
                    Math.Max(0.001, VisibleDuration.TotalSeconds) * railWidth;
                waveform.Height = TrackHeight - BlockInset * 2;
                var clippedTimelineSeconds = Math.Max(0, VisibleStart.TotalSeconds - start.TotalSeconds);
                Canvas.SetLeft(waveform, -(clip.TrimStartSeconds / clip.PlaybackSpeed + clippedTimelineSeconds) /
                    Math.Max(0.001, VisibleDuration.TotalSeconds) * railWidth);
            }
            var selected = _selectedMediaClipIds.Contains(clip.Id);
            UpdateTrimGrips(block, width, start >= VisibleStart, end <= VisibleEnd, selected);
            block.BorderThickness = new Thickness(selected ? 2 : 1);
            block.BorderBrush = new SolidColorBrush(selected
                ? Windows.UI.Color.FromArgb(255, 224, 230, 238)
                : Windows.UI.Color.FromArgb(255, 83, 128, 114));
            block.Opacity = clip.Muted ? 0.35 : selected ? 1 : 0.85;
        }
    }

    private void DrawAudioTrackSegments(double railLeft, double railWidth)
    {
        AudioTrackSegmentsCanvas.Width = TimelineCanvas.Width;
        AudioTrackSegmentsCanvas.Height = TimelineContentHeight;

        foreach (var track in _audioTrackSegments)
        {
            if (!_audioTrackSegmentBlocks.TryGetValue(track.Id, out var block))
                continue;

            var start = TimeSpan.FromSeconds(track.TimelineStartSeconds);
            var end = start + TimeSpan.FromSeconds(track.EffectiveDurationSeconds);
            if (!TryGetVisibleRect(start, end, railWidth, out var left, out var width))
            {
                block.Visibility = Visibility.Collapsed;
                continue;
            }

            block.Visibility = Visibility.Visible;
            SetCanvasRect(
                block,
                railLeft + left,
                AudioRowTopFor(track) + BlockInset,
                Math.Max(2, width),
                TrackHeightFor(track.TrackId) - BlockInset * 2);
            var selected = _selectedAudioTrackIds.Contains(track.Id);
            UpdateTrimGrips(block, width, start >= VisibleStart, end <= VisibleEnd, selected);
            block.BorderThickness = new Thickness(selected ? 2 : 1);
            block.BorderBrush = new SolidColorBrush(selected
                ? Windows.UI.Color.FromArgb(255, 224, 230, 238)
                : Windows.UI.Color.FromArgb(255, 83, 128, 114));
            if (block.Child is UIElement content)
                content.Visibility = width >= 38 ? Visibility.Visible : Visibility.Collapsed;
            DrawIndependentAudioWaveform(track, width, TrackHeightFor(track.TrackId) - BlockInset * 2, railWidth);
        }
    }

    private void DrawAudioBlock(double railLeft, double railWidth)
    {
        if (_usesExplicitMediaTimeline || _mediaClips.Count > 0)
        {
            AudioBlock.Visibility = Visibility.Collapsed;
            AudioWaveform.Visibility = Visibility.Collapsed;
            return;
        }
        if (!TryGetVisibleRect(_start, _end, railWidth, out var left, out var width))
        {
            AudioBlock.Visibility = Visibility.Collapsed;
            AudioWaveform.Visibility = Visibility.Collapsed;
            return;
        }

        AudioBlock.Visibility = Visibility.Visible;
        SetBlockSelection(AudioBlock, EditorTimelineLayer.Trim);
        SetCanvasRect(AudioBlock, railLeft + left, AudioRowTop + BlockInset, Math.Max(24, width), TrackHeight - BlockInset * 2);

        if (AudioWaveform.Source is not null)
        {
            AudioWaveform.Visibility = Visibility.Visible;
            SetCanvasRect(AudioWaveform, railLeft + left, AudioRowTop + BlockInset, Math.Max(24, width), TrackHeight - BlockInset * 2);
        }
        else
        {
            AudioWaveform.Visibility = Visibility.Collapsed;
        }
    }

    private void DrawLayerBlock(
        EditorTimelineLayer layer,
        Border block,
        TextBlock label,
        double rowTop,
        double railLeft,
        double railWidth)
    {
        var state = GetLayer(layer);
        block.Visibility = state.Enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!state.Enabled)
            return;

        if (!TryGetVisibleRect(state.Start, state.End, railWidth, out var left, out var width))
        {
            block.Visibility = Visibility.Collapsed;
            return;
        }

        label.Text = state.Label;
        SetBlockSelection(block, layer);

        SetCanvasRect(block, railLeft + left, rowTop + BlockInset, Math.Max(26, width), TrackHeight - BlockInset * 2);
    }

    private void DrawTextKeyframes(double railLeft, double railWidth)
    {
        var state = GetLayer(EditorTimelineLayer.Text);
        for (var index = 0; index < _textKeyframeMarkers.Count && index < _textKeyframes.Count; index++)
        {
            var marker = _textKeyframeMarkers[index];
            var time = TimeSpan.FromSeconds(_textKeyframes[index].TimeSeconds);
            if (!state.Enabled || time < state.Start || time > state.End || time < VisibleStart || time > VisibleEnd)
            {
                marker.Visibility = Visibility.Collapsed;
                continue;
            }

            marker.Visibility = Visibility.Visible;
            var markerX = railLeft + SecondsToX(time.TotalSeconds, railWidth);
            SetCanvasRect(marker, markerX - 4, TextRowTop + TrackHeight / 2 - 4, 8, 8);
            Canvas.SetZIndex(marker, 8);
        }
    }

    private void DrawPlayhead(double railLeft, double railWidth, double totalHeight)
    {
        var visible = _playhead >= VisibleStart && _playhead <= VisibleEnd;
        PlayheadLine.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PlayheadCap.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PlayheadStemHitArea.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PlayheadCapHitArea.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
            return;
        var x = railLeft + SecondsToX(_playhead.TotalSeconds, railWidth);
        SetCanvasRect(PlayheadLine, x, 0, 2, totalHeight);
        SetCanvasRect(PlayheadCap, x - PlayheadCap.Width / 2 + 1, 0, PlayheadCap.Width, PlayheadCap.Height);
        SetCanvasRect(PlayheadStemHitArea, x - PlayheadStemHitArea.Width / 2 + 1, RulerHeight,
            PlayheadStemHitArea.Width, Math.Max(0, totalHeight - RulerHeight));
        SetCanvasRect(PlayheadCapHitArea, x - PlayheadCapHitArea.Width / 2 + 1, 0,
            PlayheadCapHitArea.Width, RulerHeight);
    }

    private void UpdateHoverVisual(bool show)
    {
        if (!show)
        {
            HoverGuide.Visibility = Visibility.Collapsed;
            HoverTimeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var railWidth = GetRailWidth();
        var x = HeaderWidth + SecondsToX(_hoverTime.TotalSeconds, railWidth);
        HoverTimeText.Text = FrameTime(_hoverTime.TotalSeconds);
        SetCanvasRect(HoverGuide, x, 0, 1, TimelineContentHeight);
        SetCanvasRect(
            HoverTimeBadge,
            Math.Clamp(x - HoverTimeBadge.Width / 2, HeaderWidth, Math.Max(HeaderWidth, HeaderWidth + railWidth - HoverTimeBadge.Width)),
            3,
            HoverTimeBadge.Width,
            HoverTimeBadge.Height);
        HoverGuide.Visibility = Visibility.Visible;
        HoverTimeBadge.Visibility = Visibility.Visible;
    }

    private void UpdateSnapVisual(TimeSpan? snapTime)
    {
        _snapGuideTime = snapTime;
        if (snapTime is not TimeSpan time || time < VisibleStart || time > VisibleEnd)
        {
            SnapGuide.Visibility = Visibility.Collapsed;
            SnapTimeBadge.Visibility = Visibility.Collapsed;
            return;
        }

        var railWidth = GetRailWidth();
        var x = HeaderWidth + SecondsToX(time.TotalSeconds, railWidth);
        SnapTimeText.Text = $"Snap {FrameTime(time.TotalSeconds)}";
        SetCanvasRect(SnapGuide, x, 0, SnapGuide.Width, TimelineContentHeight);
        SetCanvasRect(
            SnapTimeBadge,
            Math.Clamp(x - SnapTimeBadge.Width / 2, HeaderWidth, Math.Max(HeaderWidth, HeaderWidth + railWidth - SnapTimeBadge.Width)),
            3,
            SnapTimeBadge.Width,
            SnapTimeBadge.Height);
        SnapGuide.Visibility = Visibility.Visible;
        SnapTimeBadge.Visibility = Visibility.Visible;
    }

    private void SetThumbVisibilityAndRect(FrameworkElement thumb, TimeSpan time, double railLeft, double railWidth)
    {
        if (time < VisibleStart || time > VisibleEnd)
        {
            thumb.Visibility = Visibility.Collapsed;
            return;
        }

        thumb.Visibility = Visibility.Visible;
        var x = SecondsToX(time.TotalSeconds, railWidth);
        SetCanvasRect(thumb, railLeft + x - thumb.Width / 2, VideoRowTop + 2, thumb.Width, TrackHeight - 4);
    }

    private bool TryGetVisibleRect(TimeSpan start, TimeSpan end, double railWidth, out double left, out double width)
    {
        var visibleStart = VisibleStart;
        var visibleEnd = VisibleEnd;

        if (end < visibleStart || start > visibleEnd)
        {
            left = 0;
            width = 0;
            return false;
        }

        var clippedStart = start < visibleStart ? visibleStart : start;
        var clippedEnd = end > visibleEnd ? visibleEnd : end;
        left = SecondsToX(clippedStart.TotalSeconds, railWidth);
        width = SecondsToX(clippedEnd.TotalSeconds, railWidth) - left;
        return width > 0.5;
    }

    private EditorTimelineChangedEventArgs CreateEventArgs(TimeSpan previewTime, EditorTimelineLayer changedLayer, bool selectionOnly = false)
    {
        return new EditorTimelineChangedEventArgs(
            _start,
            _end,
            previewTime < TimeSpan.Zero ? TimeSpan.Zero : previewTime,
            changedLayer,
            IsLayerEnabled(EditorTimelineLayer.Text),
            TextStart,
            TextEnd,
            IsLayerEnabled(EditorTimelineLayer.Vfx),
            VfxStart,
            VfxEnd,
            IsLayerEnabled(EditorTimelineLayer.Subtitle),
            SubtitleStart,
            SubtitleEnd,
            IsLayerEnabled(EditorTimelineLayer.Element),
            ElementStart,
            ElementEnd, selectionOnly);
    }

    private void SetRow(FrameworkElement row, double top, double width)
    {
        row.Width = width;
        row.Height = TrackHeight;
        Canvas.SetLeft(row, 0);
        Canvas.SetTop(row, top);
    }

    private static void SetGroupHeader(FrameworkElement border, FrameworkElement label, double top, double width)
    {
        border.Width = width;
        border.Height = GroupHeaderHeight;
        Canvas.SetLeft(border, 0);
        Canvas.SetTop(border, top);

        Canvas.SetLeft(label, 14);
        Canvas.SetTop(label, top + 3);
    }

    private static void SetLabel(FrameworkElement label, double rowTop)
    {
        Canvas.SetLeft(label, 14);
        Canvas.SetTop(label, rowTop + 10);
    }

    private static void SetCanvasRect(FrameworkElement element, double left, double top, double width, double height)
    {
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
    }

    private void SetRowSelection(Border row, TextBlock label, EditorTimelineLayer layer)
    {
        var selected = _selectedLayer == layer ||
                       (layer == EditorTimelineLayer.Trim && _selectedLayer == EditorTimelineLayer.Trim);
        row.Opacity = selected ? 1 : 0.78;
        row.BorderBrush = selected ? ResourceBrush("KlippAccentBrush") : ResourceBrush("KlippBorderSubtleBrush");
        label.Foreground = selected ? ResourceBrush("KlippTextPrimaryBrush") : ResourceBrush("KlippTextMutedBrush");
    }

    private void SetBlockSelection(Border block, EditorTimelineLayer layer)
    {
        var selected = _selectedLayer == layer;
        block.BorderThickness = new Thickness(selected ? 2 : 1);
        block.Opacity = selected ? 1 : 0.92;
    }

    private TimeSpan TimeFromPointer(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(TimelineSurface).Position;
        return TimeFromX(point.X);
    }

    private TimeSpan TimeFromX(double pointerX)
    {
        return TimeSpan.FromSeconds(EditorTimelineMath.PointerTime(pointerX, HeaderWidth,
            GetRailWidth(), VisibleStart.TotalSeconds, VisibleDuration.TotalSeconds));
    }

    private double GetRailWidth()
    {
        return Math.Max(1, TimelineSurface.ActualWidth - HeaderWidth - RailPaddingRight);
    }

    private double SecondsToX(double seconds, double railWidth)
    {
        var ratio = VisibleDuration.TotalSeconds <= 0
            ? 0
            : (seconds - VisibleStart.TotalSeconds) / VisibleDuration.TotalSeconds;
        return ratio * railWidth;
    }

    private double GetMajorTickSeconds()
    {
        return EditorTimelineMath.MajorTickSeconds(VisibleDuration.TotalSeconds, GetRailWidth());
    }

    private TimeSpan VisibleDuration
    {
        get
        {
            if (_dragVisibleDuration is { } dragDuration)
                return dragDuration;
            return _viewportDuration;
        }
    }

    private TimeSpan VisibleStart
    {
        get
        {
            return _viewportStart < TimeSpan.Zero ? TimeSpan.Zero : _viewportStart;
        }
    }

    private TimeSpan VisibleEnd => VisibleStart + VisibleDuration;

    private TimeSpan SnapStep => TimeSpan.FromSeconds(1 / _frameRate);

    private TimeSpan SnapMagnetThreshold
    {
        get
        {
            var secondsPerPixel = VisibleDuration.TotalSeconds / GetRailWidth();
            return TimeSpan.FromSeconds(secondsPerPixel * SnapMagnetPixels);
        }
    }

    private TimeSpan GetKeyboardStep()
    {
        var step = SnapStep;
        if (!IsKeyDown(VirtualKey.Shift))
            return step;

        return TimeSpan.FromTicks(step.Ticks * 10);
    }

    private TimeSpan SnapTime(TimeSpan value)
    {
        var snapped = EditorTimelineMath.SnapToFrame(value.TotalSeconds, _frameRate);
        return TimeSpan.FromSeconds(Math.Max(0, snapped));
    }

    private void InvalidateTicks()
    {
        _lastTickRailWidth = -1;
        _lastTickDurationSeconds = -1;
        _lastTickMajorSeconds = -1;
        _lastTickVisibleStartSeconds = -1;
        _lastTickVisibleDurationSeconds = -1;
    }

    private void ClampViewport()
    {
        _viewportStart = ClampTime(_viewportStart, TimeSpan.Zero,
            TimeSpan.FromSeconds(Math.Max(_duration.TotalSeconds, _playhead.TotalSeconds)));
    }

    private void RebuildMediaClipBlocks()
    {
        foreach (var block in _mediaClipBlocks.Values.Concat(_linkedAudioBlocks.Values))
            _clipTrimGrips.Remove(block);
        MediaClipsCanvas.Children.Clear();
        LinkedAudioClipsCanvas.Children.Clear();
        _mediaClipBlocks.Clear();
        _linkedAudioBlocks.Clear();
        _linkedAudioWaveforms.Clear();
        _mediaTransformKeyframeMarkers.Clear();
        _mediaVolumeKeyframeMarkers.Clear();
        _mediaFilterKeyframeMarkers.Clear();

        foreach (var clip in _mediaClips)
        {
            var effectiveTransition = string.Equals(clip.TransitionInStyle, "inherit", StringComparison.OrdinalIgnoreCase)
                ? _primaryTransitionStyle
                : clip.TransitionInStyle;
            var predecessor = GetTransitionPredecessor(clip);
            var transitionLabel = predecessor is not null && GetPrimaryTransitionOverlap(predecessor, clip) > 0 && effectiveTransition != "cut"
                ? GetTransitionDisplayName(effectiveTransition)
                : null;
            var label = new TextBlock
            {
                Text = transitionLabel is null ? clip.Name : $"{transitionLabel} · {clip.Name}",
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var icon = new TextBlock
            {
                Text = clip.Track == EditorClipTrack.Overlay ? "\uE7C3" : "\uE714",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            var content = new Grid
            {
                ColumnSpacing = 6,
                Padding = new Thickness(8, 0, 8, 0)
            };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(label, 1);
            content.Children.Add(icon);
            content.Children.Add(label);

            var block = new Border
            {
                Tag = clip.Id,
                Background = new SolidColorBrush(GetColorLabelBrushColor(
                    clip.ColorLabel,
                    clip.Track == EditorClipTrack.Overlay
                        ? Windows.UI.Color.FromArgb(255, 65, 58, 49)
                        : Windows.UI.Color.FromArgb(255, 43, 57, 72),
                    dark: true)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = content
            };
            block.PointerPressed += OnMediaClipPointerPressed;
            AddTrimGrips(block);
            block.RightTapped += (_, _) => SelectClipForContextMenu(clip.Id, audio: false);
            block.ContextFlyout = CreateMediaClipMenu(clip.Id);
            ToolTipService.SetToolTip(block, $"{clip.Name} · Double-click to split here · Drag edges to trim · Right click for actions");
            MediaClipsCanvas.Children.Add(block);
            _mediaClipBlocks[clip.Id] = block;
            if (clip.Track == EditorClipTrack.Primary)
            {
                var waveform = new Image { Stretch = Stretch.Fill, Opacity = 0.36, IsHitTestVisible = false };
                var waveformCanvas = new Canvas { IsHitTestVisible = false };
                waveformCanvas.Children.Add(waveform);
                var audioContent = new Grid();
                audioContent.Children.Add(waveformCanvas);
                audioContent.Children.Add(new TextBlock
                {
                    Text = clip.Name,
                    FontSize = 11,
                    Foreground = ResourceBrush("KlippTextPrimaryBrush"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 8, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                var linkedAudio = new Border
                {
                    Tag = clip.Id,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 35, 54, 49)),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 83, 128, 114)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    ContextFlyout = CreateMediaClipMenu(clip.Id),
                    Child = audioContent
                };
                linkedAudio.PointerPressed += OnMediaClipPointerPressed;
                AddTrimGrips(linkedAudio);
                linkedAudio.RightTapped += (_, _) => SelectClipForContextMenu(clip.Id, audio: false);
                ToolTipService.SetToolTip(linkedAudio, "Linked clip audio · Split trims video and audio together · Right click to detach audio");
                LinkedAudioClipsCanvas.Children.Add(linkedAudio);
                _linkedAudioBlocks[clip.Id] = linkedAudio;
                _linkedAudioWaveforms[clip.Id] = waveform;
            }
        }

        foreach (var clip in _mediaClips)
        {
            var markers = new List<Border>();
            foreach (var keyframe in clip.TransformKeyframes.OrderBy(item => item.TimeSeconds))
            {
                var marker = new Border
                {
                    Width = 8,
                    Height = 8,
                    IsHitTestVisible = false,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 253, 224, 71)),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(1),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform { Angle = 45 }
                };
                ToolTipService.SetToolTip(marker, $"Transform keyframe {keyframe.TimeSeconds:0.00}s");
                MediaClipsCanvas.Children.Add(marker);
                markers.Add(marker);
            }
            _mediaTransformKeyframeMarkers[clip.Id] = markers;

            var volumeMarkers = new List<Border>();
            foreach (var keyframe in clip.VolumeKeyframes.OrderBy(item => item.TimeSeconds))
            {
                var marker = new Border
                {
                    Width = 8,
                    Height = 8,
                    IsHitTestVisible = false,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 45, 212, 191)),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(1),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform { Angle = 45 }
                };
                ToolTipService.SetToolTip(marker, $"Volume keyframe {keyframe.TimeSeconds:0.00}s");
                MediaClipsCanvas.Children.Add(marker);
                volumeMarkers.Add(marker);
            }
            _mediaVolumeKeyframeMarkers[clip.Id] = volumeMarkers;

            var filterMarkers = new List<Border>();
            foreach (var keyframe in clip.FilterStrengthKeyframes.OrderBy(item => item.TimeSeconds))
            {
                var marker = new Border
                {
                    Width = 8,
                    Height = 8,
                    IsHitTestVisible = false,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 244, 114, 182)),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(1),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform { Angle = 45 }
                };
                ToolTipService.SetToolTip(marker, $"Filter keyframe {keyframe.TimeSeconds:0.00}s");
                MediaClipsCanvas.Children.Add(marker);
                filterMarkers.Add(marker);
            }
            _mediaFilterKeyframeMarkers[clip.Id] = filterMarkers;
        }

        RebuildTransitionHandleBlocks();
    }

    private void RebuildTextKeyframeMarkers()
    {
        foreach (var marker in _textKeyframeMarkers)
            TimelineCanvas.Children.Remove(marker);
        _textKeyframeMarkers.Clear();

        foreach (var keyframe in _textKeyframes.OrderBy(item => item.TimeSeconds))
        {
            var marker = new Border
            {
                Width = 8,
                Height = 8,
                IsHitTestVisible = false,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 253, 224, 71)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(1),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform { Angle = 45 }
            };
            ToolTipService.SetToolTip(marker, $"Text keyframe {keyframe.TimeSeconds:0.00}s");
            TimelineCanvas.Children.Add(marker);
            _textKeyframeMarkers.Add(marker);
        }
    }

    private void RebuildMarkerVisuals()
    {
        MarkerCanvas.Children.Clear();
        _markerVisuals.Clear();

        foreach (var marker in _markers)
        {
            var isBeat = string.Equals(marker.Kind, "beat", StringComparison.Ordinal);
            var line = new Border
            {
                IsHitTestVisible = false
            };
            var cap = new Border
            {
                Tag = marker.Id,
                Width = isBeat ? 8 : 12,
                Height = isBeat ? 8 : 12,
                CornerRadius = new CornerRadius(isBeat ? 4 : 2),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform { Angle = isBeat ? 0 : 45 }
            };
            ToolTipService.SetToolTip(cap, $"{marker.Label}  {marker.TimeLabel}\nDrag to move");
            cap.PointerPressed += OnMarkerPointerPressed;
            MarkerCanvas.Children.Add(line);
            MarkerCanvas.Children.Add(cap);
            _markerVisuals[marker.Id] = new MarkerVisual(line, cap);
        }
    }

    private static Windows.UI.Color GetColorLabelBrushColor(
        string? colorLabel,
        Windows.UI.Color fallback,
        bool dark)
    {
        return EditorProjectColorLabels.Normalize(colorLabel) switch
        {
            "red" => dark
                ? Windows.UI.Color.FromArgb(255, 127, 29, 29)
                : Windows.UI.Color.FromArgb(255, 248, 113, 113),
            "orange" => dark
                ? Windows.UI.Color.FromArgb(255, 124, 45, 18)
                : Windows.UI.Color.FromArgb(255, 251, 146, 60),
            "yellow" => dark
                ? Windows.UI.Color.FromArgb(255, 113, 63, 18)
                : Windows.UI.Color.FromArgb(255, 250, 204, 21),
            "green" => dark
                ? Windows.UI.Color.FromArgb(255, 20, 83, 45)
                : Windows.UI.Color.FromArgb(255, 74, 222, 128),
            "teal" => dark
                ? Windows.UI.Color.FromArgb(255, 17, 94, 89)
                : Windows.UI.Color.FromArgb(255, 45, 212, 191),
            "blue" => dark
                ? Windows.UI.Color.FromArgb(255, 30, 64, 175)
                : Windows.UI.Color.FromArgb(255, 96, 165, 250),
            "purple" => dark
                ? Windows.UI.Color.FromArgb(255, 88, 28, 135)
                : Windows.UI.Color.FromArgb(255, 192, 132, 252),
            _ => fallback
        };
    }

    private void RebuildTransitionHandleBlocks()
    {
        foreach (var block in _transitionHandleBlocks.Values)
            _clipTrimGrips.Remove(block);
        TransitionHandlesCanvas.Children.Clear();
        _transitionHandleBlocks.Clear();
        foreach (var clip in _mediaClips.Where(clip => GetTransitionPredecessor(clip, includeTouching: true) is not null))
        {
            var style = string.Equals(clip.TransitionInStyle, "inherit", StringComparison.OrdinalIgnoreCase)
                ? _primaryTransitionStyle
                : clip.TransitionInStyle;
            if (string.Equals(style, "cut", StringComparison.OrdinalIgnoreCase))
                continue;

            var label = new TextBlock
            {
                Text = "0.5s",
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                FontSize = 9,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var handle = new Border
            {
                Tag = clip.Id,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 54, 98, 112)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = label
            };
            AddTrimGrips(handle);
            ToolTipService.SetToolTip(handle, "Drag center to move transition · Ctrl to copy · Drag either edge to change duration");
            handle.PointerPressed += OnTransitionHandlePointerPressed;
            TransitionHandlesCanvas.Children.Add(handle);
            _transitionHandleBlocks[clip.Id] = handle;
        }
    }

    private void RebuildSubtitleSegmentBlocks()
    {
        SubtitleSegmentsCanvas.Children.Clear();
        _subtitleSegmentBlocks.Clear();

        foreach (var segment in _subtitleSegments)
        {
            var label = new TextBlock
            {
                Text = segment.Text,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var block = new Border
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 88, 28, 135)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 192, 132, 252)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 0, 8, 0),
                Child = label
            };
            SubtitleSegmentsCanvas.Children.Add(block);
            _subtitleSegmentBlocks[segment.Id] = block;
        }
    }

    private void RebuildGraphicSegmentBlocks()
    {
        GraphicSegmentsCanvas.Children.Clear();
        _graphicSegmentBlocks.Clear();
        _graphicKeyframeMarkers.Clear();

        foreach (var segment in _graphicSegments)
        {
            var icon = new TextBlock
            {
                Text = "\uE8B9",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            var label = new TextBlock
            {
                Text = segment.Name,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Padding = new Thickness(8, 0, 8, 0)
            };
            content.Children.Add(icon);
            content.Children.Add(label);

            var block = new Border
            {
                Tag = segment.Id,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 124, 45, 18)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 251, 146, 60)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = content
            };
            ToolTipService.SetToolTip(block, "Drag to move. Drag either edge to trim.");
            block.PointerPressed += OnGraphicSegmentPointerPressed;
            GraphicSegmentsCanvas.Children.Add(block);
            _graphicSegmentBlocks[segment.Id] = block;

            var markers = new List<Border>();
            foreach (var keyframe in segment.Keyframes.OrderBy(item => item.TimeSeconds))
            {
                var marker = new Border
                {
                    Tag = new GraphicKeyframeMarkerTag(segment.Id, keyframe.TimeSeconds),
                    Width = 10,
                    Height = 10,
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 253, 224, 71)),
                    BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(1),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new RotateTransform { Angle = 45 }
                };
                ToolTipService.SetToolTip(marker, $"Keyframe {keyframe.TimeSeconds:0.00}s");
                marker.PointerPressed += OnGraphicKeyframePointerPressed;
                GraphicSegmentsCanvas.Children.Add(marker);
                markers.Add(marker);
            }
            _graphicKeyframeMarkers[segment.Id] = markers;
        }
    }

    private void OnGraphicKeyframePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: GraphicKeyframeMarkerTag tag })
            return;

        Focus(FocusState.Pointer);
        _selectedGraphicSegmentId = tag.SegmentId;
        _selectedLayer = EditorTimelineLayer.Element;
        _playhead = ClampTime(TimeSpan.FromSeconds(tag.TimeSeconds), TimeSpan.Zero, _duration);
        GraphicSegmentSelected?.Invoke(
            this,
            new EditorGraphicSegmentSelectedEventArgs(tag.SegmentId, _playhead));
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Playhead);
        e.Handled = true;
    }

    private MenuFlyout CreateMediaClipMenu(string clipId)
    {
        var menu = new MenuFlyout();
        AddMenuAction(menu, "Split at playhead", () =>
        {
            SelectMediaClipForEditing(clipId);
            SplitSelectedClip();
        });
        AddMenuAction(menu, "Detach audio", () =>
        {
            SelectMediaClipForEditing(clipId);
            DetachSelectedClipAudio();
        });
        AddMenuAction(menu, "Duplicate", () =>
        {
            SelectMediaClipForEditing(clipId);
            DuplicateSelectedMediaClips();
        });
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenuAction(menu, "Delete clip", () =>
        {
            SelectMediaClipForEditing(clipId);
            DeleteSelectedMediaClips();
        });
        AppendClipboardMenuActions(menu);
        return menu;
    }

    private static void AddMenuAction(MenuFlyout menu, string label, Action action)
    {
        var item = new MenuFlyoutItem { Text = label };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private void RebuildAudioTrackSegmentBlocks()
    {
        foreach (var block in _audioTrackSegmentBlocks.Values)
            _clipTrimGrips.Remove(block);
        AudioTrackSegmentsCanvas.Children.Clear();
        _audioTrackSegmentBlocks.Clear();
        _audioWaveformVisuals.Clear();
        _audioTrackLanes.Clear();
        var laneEnds = new List<double>();
        foreach (var track in _audioTrackSegments.OrderBy(track => track.TimelineStartSeconds))
        {
            var lane = laneEnds.FindIndex(end => end <= track.TimelineStartSeconds + 0.0001);
            if (lane < 0)
            {
                lane = laneEnds.Count;
                laneEnds.Add(0);
            }
            laneEnds[lane] = track.TimelineStartSeconds + track.EffectiveDurationSeconds;
            _audioTrackLanes[track.Id] = lane;
        }
        _audioTrackLaneCount = Math.Max(1, laneEnds.Count);

        foreach (var track in _audioTrackSegments)
        {
            var content = new Grid();
            var waveformImage = new Image { Stretch = Stretch.Fill, Opacity = .52, IsHitTestVisible = false };
            var waveformViewport = new Canvas { IsHitTestVisible = false };
            waveformViewport.Children.Add(waveformImage);
            content.Children.Add(waveformViewport);
            _audioWaveformVisuals[track.Id] = new AudioWaveformVisual(waveformViewport, waveformImage);

            var label = new TextBlock
            {
                Text = track.Name,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255)),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 8, 0)
            };
            content.Children.Add(label);
            var block = new Border
            {
                Tag = track.Id,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 35, 54, 49)),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 83, 128, 114)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Opacity = track.Muted ? 0.5 : 0.95,
                Child = content
            };
            var menu = new MenuFlyout();
            AddMenuAction(menu, "Split audio at playhead", () =>
            {
                SelectAudioTrack(track.Id);
                SplitSelectedAudioTrack();
            });
            AddMenuAction(menu, "Delete audio clip", () =>
            {
                SelectAudioTrack(track.Id);
                DeleteSelectedAudioTrack();
            });
            AddMenuAction(menu, "Add volume point at playhead", () => AddAudioVolumePoint(track.Id,
                new Point(HeaderWidth + SecondsToX(_playhead.TotalSeconds, GetRailWidth()), AudioRowTopFor(track) + TrackHeightFor(track.TrackId) / 2)));
            AppendClipboardMenuActions(menu);
            block.ContextFlyout = menu;
            AddTrimGrips(block);
            block.PointerPressed += OnAudioTrackPointerPressed;
            block.RightTapped += (_, _) => SelectClipForContextMenu(track.Id, audio: true);
            ToolTipService.SetToolTip(block, $"{track.Name} · Double-click to split here · Alt+double-click for a volume point · Drag edges to trim");
            AudioTrackSegmentsCanvas.Children.Add(block);
            _audioTrackSegmentBlocks[track.Id] = block;
        }
        RebuildAudioAutomation();
    }

    private void NormalizeMediaClips()
    {
        _mediaTimelineStartsDirty = true;
        EnsureTimelineTracks();
        foreach (var clip in _mediaClips)
            clip.Normalize();
        foreach (var track in _mediaClips.GroupBy(clip => clip.TrackId))
        {
            var order = 0;
            foreach (var clip in track.OrderBy(item => item.TimelineStartSeconds).ThenBy(item => item.Order))
                clip.Order = order++;
        }
    }

    private void RecalculateDurationFromMedia(bool clearHistory, bool allowEmpty = false)
    {
        var previousStart = _start;
        var previousEnd = _end;
        var endFollowedDuration = Math.Abs((_end - _duration).TotalSeconds) < 0.00001;
        _mediaTimelineStartsDirty = true;
        if (_mediaClips.Count == 0 && _audioTrackSegments.Count == 0 && !allowEmpty)
            return;

        NormalizeMediaClips();
        var primary = GetPrimaryClips();
        var primaryDuration = primary.Select(clip => clip.TimelineStartSeconds + clip.EffectiveDurationSeconds)
            .DefaultIfEmpty(0).Max();
        var overlayEnd = _mediaClips
            .Where(clip => clip.Track == EditorClipTrack.Overlay)
            .Select(clip => clip.TimelineStartSeconds + clip.EffectiveDurationSeconds)
            .DefaultIfEmpty(0)
            .Max();
        var audioEnd = _audioTrackSegments.Select(track => track.TimelineStartSeconds + track.EffectiveDurationSeconds)
            .DefaultIfEmpty(0).Max();
        _duration = TimeSpan.FromSeconds(Math.Max(0.1, Math.Max(audioEnd, Math.Max(primaryDuration, overlayEnd))));
        _start = ClampTime(previousStart, TimeSpan.Zero, _duration - MinimumTrim);
        _end = ClampTime(endFollowedDuration ? _duration : previousEnd, _start + MinimumTrim, _duration);
        foreach (var layer in _layers.Values)
            ClampLayer(layer);
        foreach (var marker in _markers)
            marker.Normalize(_duration.TotalSeconds);
        if (clearHistory)
            ClearHistory();
    }

    private List<EditorProjectClip> GetPrimaryClips()
    {
        return _mediaClips
            .Where(clip => clip.Track == EditorClipTrack.Primary)
            .OrderBy(clip => clip.TimelineStartSeconds).ThenBy(clip => clip.Order)
            .ToList();
    }

    private void ReplacePrimaryClips(IReadOnlyList<EditorProjectClip> primary)
    {
        _mediaTimelineStartsDirty = true;
        _mediaClips.RemoveAll(clip => clip.Track == EditorClipTrack.Primary);
        for (var index = 0; index < primary.Count; index++)
        {
            primary[index].Track = EditorClipTrack.Primary;
            primary[index].Order = index;
            _mediaClips.Add(primary[index]);
        }
    }

    private int GetPrimaryInsertIndex(TimeSpan position)
    {
        var primary = GetPrimaryClips();
        for (var index = 0; index < primary.Count; index++)
        {
            var midpoint = GetClipTimelineStart(primary[index]) + primary[index].EffectiveDurationSeconds / 2;
            if (position.TotalSeconds < midpoint)
                return index;
        }
        return primary.Count;
    }

    private double GetClipTimelineStart(EditorProjectClip clip)
    {
        return Math.Max(0, clip.TimelineStartSeconds);
    }

    private void EnsureMediaTimelineStarts()
    {
        if (!_mediaTimelineStartsDirty)
            return;
        _mediaTimelineStarts.Clear();
        _primaryClipsInTimelineOrder.Clear();
        _primaryClipsInTimelineOrder.AddRange(GetPrimaryClips());
        for (var index = 0; index < _primaryClipsInTimelineOrder.Count; index++)
        {
            var primary = _primaryClipsInTimelineOrder[index];
            _mediaTimelineStarts[primary.Id] = primary.TimelineStartSeconds;
        }
        _mediaTimelineStartsDirty = false;
    }

    private double GetPrimaryTransitionOverlap(EditorProjectClip left, EditorProjectClip right)
    {
        var style = string.Equals(right.TransitionInStyle, "inherit", StringComparison.OrdinalIgnoreCase)
            ? _primaryTransitionStyle
            : right.TransitionInStyle;
        if (string.Equals(style, "cut", StringComparison.OrdinalIgnoreCase))
            return 0;

        var availableOverlap = left.TimelineStartSeconds + left.EffectiveDurationSeconds - right.TimelineStartSeconds;
        if (right.TimelineStartSeconds < left.TimelineStartSeconds || availableOverlap <= 0.00001)
            return 0;

        var duration = string.Equals(right.TransitionInStyle, "inherit", StringComparison.OrdinalIgnoreCase)
            ? _primaryTransitionSeconds
            : right.TransitionInDurationSeconds;
        return Math.Min(availableOverlap, Math.Clamp(
            duration,
            0,
            Math.Min(left.EffectiveDurationSeconds / 2, right.EffectiveDurationSeconds / 2)));
    }

    private EditorProjectClip? GetTransitionPredecessor(EditorProjectClip incoming, bool includeTouching = false)
    {
        return _mediaClips.Where(clip => clip.TrackId == incoming.TrackId).OrderBy(clip => clip.TimelineStartSeconds).ThenBy(clip => clip.Order)
            .TakeWhile(clip => clip.Id != incoming.Id).LastOrDefault(clip =>
            clip.TimelineStartSeconds + clip.EffectiveDurationSeconds > incoming.TimelineStartSeconds + 0.00001 ||
            (includeTouching && Math.Abs(clip.TimelineStartSeconds + clip.EffectiveDurationSeconds - incoming.TimelineStartSeconds) < 0.001));
    }

    private bool PrepareTransitionTarget(EditorProjectClip incoming, string style, double durationSeconds)
    {
        if (style == "cut" || (style == "inherit" && _primaryTransitionStyle == "cut"))
            return true;
        var predecessor = GetTransitionPredecessor(incoming, includeTouching: true);
        if (predecessor is null)
        {
            SetEditStatus("Overlap the video clips first, or place them directly beside each other.");
            return false;
        }
        if (IsClipLocked(predecessor) || IsClipLocked(incoming))
        { SetEditStatus("Unlock the video track before editing its transition."); return false; }
        var availableOverlap = Math.Max(0, predecessor.TimelineStartSeconds + predecessor.EffectiveDurationSeconds - incoming.TimelineStartSeconds);
        var capacity = Math.Min(predecessor.EffectiveDurationSeconds / 2, incoming.EffectiveDurationSeconds / 2);
        var handles = EditorAdvancedEditMath.PlanAudioCrossfadeHandles(durationSeconds,
            Math.Max(0, predecessor.SourceDurationSeconds - predecessor.TrimEndSeconds) / predecessor.PlaybackSpeed,
            Math.Min(incoming.TrimStartSeconds / incoming.PlaybackSpeed, incoming.TimelineStartSeconds), availableOverlap);
        if (durationSeconds > capacity + .00001 || handles.Overlap + .00001 < durationSeconds)
        {
            SetEditStatus("Trim clip edges to provide source handles, or shorten the transition. Existing audio timing is preserved.");
            return false;
        }
        if (handles.LeftExtension > 0)
        {
            predecessor.TrimEndSeconds += handles.LeftExtension * predecessor.PlaybackSpeed;
            InvalidateLoudnessAnalysis(predecessor);
        }
        if (handles.RightExtension > 0)
        {
            incoming.TrimStartSeconds -= handles.RightExtension * incoming.PlaybackSpeed;
            incoming.TimelineStartSeconds -= handles.RightExtension;
            incoming.TransformKeyframes = RetimeTransformKeyframesForStartTrim(incoming.TransformKeyframes,
                new(incoming.Scale, incoming.OffsetX, incoming.OffsetY), -handles.RightExtension);
            incoming.VolumeKeyframes = RetimeVolumeKeyframesForStartTrim(incoming.VolumeKeyframes, incoming.Volume, -handles.RightExtension);
            incoming.FilterStrengthKeyframes = RetimeFilterKeyframesForStartTrim(incoming.FilterStrengthKeyframes, incoming.FilterStrength, -handles.RightExtension);
            InvalidateLoudnessAnalysis(incoming);
        }
        predecessor.Normalize();
        incoming.Normalize();
        _mediaTimelineStartsDirty = true;
        return true;
    }

    private static string GetTransitionDisplayName(string style) => style switch
    {
        "cut" => "No transition", "dissolve" => "Cross dissolve",
        "fadeblack" => "Dip to black", "fadewhite" => "Dip to white",
        "wipeleft" => "Wipe · Left", "wiperight" => "Wipe · Right",
        "wipeup" => "Wipe · Up", "wipedown" => "Wipe · Down",
        "slideleft" => "Slide · Left", "slideright" => "Slide · Right",
        "slideup" => "Slide · Up", "slidedown" => "Slide · Down",
        "circleopen" => "Iris", "rectcrop" => "Box reveal",
        "horzopen" => "Barn doors · Horizontal", "vertopen" => "Barn doors · Vertical",
        "diagtl" => "Diagonal", _ => "Cross dissolve"
    };

    private static string NormalizeTransitionStyle(string? style, bool allowInherit)
    {
        return style switch
        {
            "dissolve" or "fadeblack" or "fadewhite" or "wipeleft" or "wiperight" or "wipeup" or "wipedown" or
                "slideleft" or "slideright" or "slideup" or "slidedown" or "cut" or
                "circleopen" or "rectcrop" or "horzopen" or "vertopen" or "diagtl" => style,
            "inherit" when allowInherit => style,
            _ => allowInherit ? "inherit" : "cut"
        };
    }

    public EditorProjectClip? FindMediaClipAt(TimeSpan position, EditorClipTrack track = EditorClipTrack.Primary)
    {
        var matches = _mediaClips.Where(clip => clip.Track == track);
        foreach (var clip in matches.OrderByDescending(clip => clip.TimelineStartSeconds).ThenByDescending(clip => clip.Order))
        {
            var start = GetClipTimelineStart(clip);
            if (position.TotalSeconds >= start - 0.001 &&
                position.TotalSeconds < start + clip.EffectiveDurationSeconds - 0.001)
            {
                return CloneClip(clip);
            }
        }
        return null;
    }

    private EditorProjectClip? FindEditableClipAtPlayhead() => FindEditableClipAt(_playhead);

    private EditorProjectClip? FindEditableClipAt(TimeSpan position)
    {
        var found = FindMediaClipAt(position);
        return found is null ? null : _mediaClips.FirstOrDefault(clip => clip.Id == found.Id && !IsClipLocked(clip));
    }

    public TimeSpan GetMediaClipTimelineStart(string clipId)
    {
        var clip = _mediaClips.FirstOrDefault(item => item.Id == clipId);
        return TimeSpan.FromSeconds(clip is null ? 0 : GetClipTimelineStart(clip));
    }

    private EditorProjectClip? GetSingleSelectedMediaClip()
    {
        var id = SelectedMediaClipId;
        return id is null ? null : _mediaClips.FirstOrDefault(clip => clip.Id == id && !IsClipLocked(clip));
    }

    public bool SelectMediaClipForEditing(string clipId)
    {
        if (!_mediaClips.Any(clip => clip.Id == clipId))
            return false;
        SelectOnlyMediaClip(clipId);
        UpdateVisuals(notify: true, previewTime: _playhead, changedLayer: EditorTimelineLayer.Media, selectionOnly: true);
        return true;
    }

    private void SelectOnlyMediaClip(string clipId)
    {
        ClearMarkerSelection();
        _selectedMediaClipIds.Clear();
        _selectedAudioTrackIds.Clear();
        _selectedAudioTrackId = null;
        _selectedMediaClipIds.Add(clipId);
        _selectedTrackId = _mediaClips.FirstOrDefault(clip => clip.Id == clipId)?.TrackId ?? _selectedTrackId;
        _selectedLayer = EditorTimelineLayer.Media;
    }

    private void ClearMarkerSelection()
    {
        if (_selectedMarkerId is null)
            return;

        _selectedMarkerId = null;
        MarkerSelected?.Invoke(this, new EditorMarkerSelectedEventArgs(null));
    }

    private void NotifyMediaClipsChanged(EditorMediaChangeKind kind, string? clipId)
    {
        if (_editBatchDepth > 0)
            return;
        MediaClipsChanged?.Invoke(
            this,
            new EditorMediaClipsChangedEventArgs(GetMediaClips(), kind, clipId, _playhead));
    }

    private void NotifyMarkersChanged(EditorMarkerChangeKind kind, string? markerId)
    {
        MarkersChanged?.Invoke(
            this,
            new EditorMarkersChangedEventArgs(GetMarkers(), kind, markerId));
    }

    private static EditorProjectClip CloneClip(EditorProjectClip source)
    {
        return new EditorProjectClip
        {
            Id = source.Id,
            SourcePath = source.SourcePath,
            SourceAudioSeparated = source.SourceAudioSeparated,
            TrackId = source.TrackId,
            GroupId = source.GroupId,
            Effects = source.Effects.Select(effect => effect.Clone()).ToList(),
            Name = source.Name,
            Notes = source.Notes,
            ColorLabel = source.ColorLabel,
            Track = source.Track,
            Order = source.Order,
            SourceDurationSeconds = source.SourceDurationSeconds,
            TrimStartSeconds = source.TrimStartSeconds,
            TrimEndSeconds = source.TrimEndSeconds,
            TimelineStartSeconds = source.TimelineStartSeconds,
            PlaybackSpeed = source.PlaybackSpeed,
            Volume = source.Volume,
            Muted = source.Muted,
            NormalizeAudio = source.NormalizeAudio,
            NormalizationGainDb = source.NormalizationGainDb,
            IntegratedLoudnessLufs = source.IntegratedLoudnessLufs,
            LoudnessAnalysisTrimStartSeconds = source.LoudnessAnalysisTrimStartSeconds,
            LoudnessAnalysisTrimEndSeconds = source.LoudnessAnalysisTrimEndSeconds,
            NoiseReductionEnabled = source.NoiseReductionEnabled,
            NoiseReductionStrength = source.NoiseReductionStrength,
            VolumeKeyframes = source.VolumeKeyframes?
                .Select(keyframe => keyframe.Clone())
                .ToList() ?? [],
            Scale = source.Scale,
            OffsetX = source.OffsetX,
            OffsetY = source.OffsetY,
            TransformKeyframes = source.TransformKeyframes?
                .Select(keyframe => keyframe.Clone())
                .ToList() ?? [],
            CropLeft = source.CropLeft,
            CropTop = source.CropTop,
            CropRight = source.CropRight,
            CropBottom = source.CropBottom,
            MaskShape = source.MaskShape,
            MaskRoundness = source.MaskRoundness,
            MaskPolygon = source.MaskPolygon.Select(point => new EditorProjectCurvePoint { X = point.X, Y = point.Y }).ToList(),
            MaskInverted = source.MaskInverted,
            VideoProcessing = source.VideoProcessing?.Clone(),
            Filter = source.Filter,
            FilterStrength = source.FilterStrength,
            FilterStrengthKeyframes = source.FilterStrengthKeyframes?
                .Select(keyframe => keyframe.Clone())
                .ToList() ?? [],
            Brightness = source.Brightness,
            Contrast = source.Contrast,
            Saturation = source.Saturation,
            Temperature = source.Temperature,
            Shadows = source.Shadows,
            Highlights = source.Highlights,
            Gamma = source.Gamma,
            HueDegrees = source.HueDegrees,
            ToneCurve = CloneCurvePoints(source.ToneCurve),
            Sharpen = source.Sharpen,
            Blur = source.Blur,
            Vignette = source.Vignette,
            LutPath = source.LutPath,
            LutStrength = source.LutStrength,
            TransitionInStyle = source.TransitionInStyle,
            TransitionInDurationSeconds = source.TransitionInDurationSeconds
        };
    }

    private static void InvalidateLoudnessAnalysis(EditorProjectClip clip)
    {
        if (clip.IntegratedLoudnessLufs is null)
            return;
        if (Math.Abs(clip.LoudnessAnalysisTrimStartSeconds - clip.TrimStartSeconds) <= 0.001 &&
            Math.Abs(clip.LoudnessAnalysisTrimEndSeconds - clip.TrimEndSeconds) <= 0.001)
        {
            return;
        }

        clip.NormalizeAudio = false;
        clip.NormalizationGainDb = 0;
        clip.IntegratedLoudnessLufs = null;
    }

    private static EditorProjectMarker CloneMarker(EditorProjectMarker source)
    {
        return new EditorProjectMarker
        {
            Id = source.Id,
            TimeSeconds = source.TimeSeconds,
            Label = source.Label,
            Notes = source.Notes,
            ColorLabel = source.ColorLabel,
            Kind = source.Kind
        };
    }

    private static List<EditorProjectCurvePoint> CloneCurvePoints(
        IEnumerable<EditorProjectCurvePoint>? points) =>
        points?.Select(point => new EditorProjectCurvePoint
        {
            X = point.X,
            Y = point.Y
        }).ToList() ?? [];

    private static bool CurvePointsEqual(
        IReadOnlyList<EditorProjectCurvePoint>? left,
        IReadOnlyList<EditorProjectCurvePoint>? right)
    {
        left ??= [];
        right ??= [];
        if (left.Count != right.Count)
            return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (Math.Abs(left[index].X - right[index].X) > 0.0001 ||
                Math.Abs(left[index].Y - right[index].Y) > 0.0001)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TransformKeyframesEqual(
        IReadOnlyList<EditorProjectClipTransformKeyframe>? left,
        IReadOnlyList<EditorProjectClipTransformKeyframe>? right)
    {
        left ??= [];
        right ??= [];
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            var a = left[index];
            var b = right[index];
            if (Math.Abs(a.TimeSeconds - b.TimeSeconds) > 0.0001 ||
                Math.Abs(a.Scale - b.Scale) > 0.0001 ||
                Math.Abs(a.OffsetX - b.OffsetX) > 0.0001 ||
                Math.Abs(a.OffsetY - b.OffsetY) > 0.0001 ||
                a.Easing != b.Easing)
            {
                return false;
            }
        }

        return true;
    }

    private static bool VolumeKeyframesEqual(
        IReadOnlyList<EditorProjectClipVolumeKeyframe>? left,
        IReadOnlyList<EditorProjectClipVolumeKeyframe>? right)
    {
        left ??= [];
        right ??= [];
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            var a = left[index];
            var b = right[index];
            if (Math.Abs(a.TimeSeconds - b.TimeSeconds) > 0.0001 ||
                Math.Abs(a.Volume - b.Volume) > 0.0001 ||
                a.Easing != b.Easing)
            {
                return false;
            }
        }

        return true;
    }

    private static bool FilterKeyframesEqual(
        IReadOnlyList<EditorProjectClipFilterKeyframe>? left,
        IReadOnlyList<EditorProjectClipFilterKeyframe>? right)
    {
        left ??= [];
        right ??= [];
        if (left.Count != right.Count)
            return false;

        for (var index = 0; index < left.Count; index++)
        {
            var a = left[index];
            var b = right[index];
            if (Math.Abs(a.TimeSeconds - b.TimeSeconds) > 0.0001 ||
                Math.Abs(a.Strength - b.Strength) > 0.0001 ||
                a.Easing != b.Easing)
            {
                return false;
            }
        }

        return true;
    }

    private static EditorProjectTextLayer CloneTextLayer(EditorProjectTextLayer source)
    {
        return new EditorProjectTextLayer
        {
            Id = source.Id,
            Name = source.Name,
            Text = source.Text,
            Style = source.Style,
            Animation = source.Animation,
            Origin = source.Origin,
            SourceClipId = source.SourceClipId,
            FontFamily = source.FontFamily,
            Color = source.Color,
            AnchorX = source.AnchorX,
            AnchorY = source.AnchorY,
            Size = source.Size,
            BoxWidth = source.BoxWidth,
            Opacity = source.Opacity,
            StartSeconds = source.StartSeconds,
            EndSeconds = source.EndSeconds,
            Keyframes = source.Keyframes.Select(keyframe => keyframe.Clone()).ToList()
        };
    }

    private static EditorProjectAudioTrack CloneAudioTrack(EditorProjectAudioTrack source)
    {
        return new EditorProjectAudioTrack
        {
            TrackId = source.TrackId,
            GroupId = source.GroupId,
            Id = source.Id,
            Name = source.Name,
            SourcePath = source.SourcePath,
            SourceClipId = source.SourceClipId,
            SourceAudioStreamIndex = source.SourceAudioStreamIndex,
            SourceAudioRole = source.SourceAudioRole,
            SourceDurationSeconds = source.SourceDurationSeconds,
            PlaybackSpeed = source.PlaybackSpeed,
            TrimStartSeconds = source.TrimStartSeconds,
            TrimEndSeconds = source.TrimEndSeconds,
            TimelineStartSeconds = source.TimelineStartSeconds,
            Volume = source.Volume,
            VolumeKeyframes = source.VolumeKeyframes.Select(keyframe => keyframe.Clone()).ToList(),
            Muted = source.Muted,
            NormalizeAudio = source.NormalizeAudio,
            NormalizationGainDb = source.NormalizationGainDb,
            IntegratedLoudnessLufs = source.IntegratedLoudnessLufs,
            LoudnessAnalysisTrimStartSeconds = source.LoudnessAnalysisTrimStartSeconds,
            LoudnessAnalysisTrimEndSeconds = source.LoudnessAnalysisTrimEndSeconds,
            DuckingEnabled = source.DuckingEnabled,
            DuckingStrength = source.DuckingStrength,
            NoiseReductionEnabled = source.NoiseReductionEnabled,
            NoiseReductionStrength = source.NoiseReductionStrength,
            FadeInSeconds = source.FadeInSeconds,
            FadeOutSeconds = source.FadeOutSeconds
        };
    }

    private static EditorProjectGraphicLayer CloneGraphicLayer(EditorProjectGraphicLayer source)
    {
        return new EditorProjectGraphicLayer
        {
            Id = source.Id,
            Name = source.Name,
            SourcePath = source.SourcePath,
            AnchorX = source.AnchorX,
            AnchorY = source.AnchorY,
            Width = source.Width,
            Height = source.Height,
            Opacity = source.Opacity,
            BlendMode = source.BlendMode,
            MaskShape = source.MaskShape,
            MaskRoundness = source.MaskRoundness,
            StartSeconds = source.StartSeconds,
            EndSeconds = source.EndSeconds,
            Keyframes = source.Keyframes.Select(CloneGraphicKeyframe).ToList()
        };
    }

    private static EditorProjectGraphicKeyframe CloneGraphicKeyframe(EditorProjectGraphicKeyframe source)
    {
        return new EditorProjectGraphicKeyframe
        {
            TimeSeconds = source.TimeSeconds,
            AnchorX = source.AnchorX,
            AnchorY = source.AnchorY,
            Width = source.Width,
            Height = source.Height,
            Opacity = source.Opacity,
            Easing = source.Easing
        };
    }

    private LayerState GetLayer(EditorTimelineLayer layer)
    {
        return _layers[layer];
    }

    private void ClampLayer(LayerState state)
    {
        state.Start = ClampTime(state.Start, TimeSpan.Zero, _duration - TimeSpan.FromSeconds(MinLayerSeconds));
        state.End = ClampTime(state.End, state.Start + TimeSpan.FromSeconds(MinLayerSeconds), _duration);
    }

    private TimeSpan DefaultLayerEnd(TimeSpan start)
    {
        return ClampTime(start + TimeSpan.FromSeconds(Math.Min(5, Math.Max(1, _duration.TotalSeconds / 3))), start + TimeSpan.FromSeconds(MinLayerSeconds), _duration);
    }

    private static TimeSpan ClampTime(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (max < min)
            max = min;
        if (value < min)
            return min;
        if (value > max)
            return max;
        return value;
    }

    private static TimeSpan Abs(TimeSpan value)
    {
        return value < TimeSpan.Zero ? TimeSpan.FromTicks(-value.Ticks) : value;
    }

    private static string FormatTimestamp(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    private static Brush ResourceBrush(string key)
    {
        return (Brush)Application.Current.Resources[key];
    }

    private static bool IsKeyDown(VirtualKey key)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private bool IsPrimaryPointer(PointerRoutedEventArgs e) =>
        e.Pointer.PointerDeviceType != PointerDeviceType.Mouse ||
        e.GetCurrentPoint(TimelineSurface).Properties.IsLeftButtonPressed;

    private string GetLayerDisplayName(EditorTimelineLayer layer)
    {
        return layer switch
        {
            EditorTimelineLayer.Playhead => "Playhead",
            EditorTimelineLayer.Trim => "Trim",
            EditorTimelineLayer.Vfx => "VFX",
            EditorTimelineLayer.Text => "Text",
            EditorTimelineLayer.Subtitle => "Subtitles",
            EditorTimelineLayer.Element => "Elements",
            EditorTimelineLayer.Marker => "Marker",
            EditorTimelineLayer.Media => _selectedMediaClipIds.Count > 1 ? $"{_selectedMediaClipIds.Count} clips" : "Clip",
            EditorTimelineLayer.Audio => "Audio clip",
            _ => "Timeline"
        };
    }

    private bool ShowVfxTrack => VfxEnabled;
    private bool ShowTextTrack => TextEnabled;
    private bool ShowSubtitleTrack => SubtitleEnabled || _subtitleSegments.Count > 0;
    private bool ShowElementTrack => ElementEnabled || _graphicSegments.Count > 0;
    private bool HasEffectTracks => _effectLayers.Count > 0 || ShowVfxTrack || ShowTextTrack || ShowSubtitleTrack || ShowElementTrack;
    private double EffectsGroupHeaderTop => RulerHeight;
    private double VfxRowTop => EffectsGroupHeaderTop + (HasEffectTracks ? GroupHeaderHeight : 0);
    private double TextRowTop => EffectLayersTop + EffectLayersHeight;
    private double SubtitleRowTop => TextRowTop + (ShowTextTrack ? TrackHeight + TrackGap : 0);
    private double ElementRowTop => SubtitleRowTop + (ShowSubtitleTrack ? TrackHeight + TrackGap : 0);
    private double MediaGroupHeaderTop => ElementRowTop + (ShowElementTrack ? TrackHeight + TrackGap : 0);
    private double OverlayRowTop => PersistentTrackTop(_tracks.Where(track => track.Kind == EditorTimelineTrackKind.Video).OrderBy(track => track.Order).Skip(1).FirstOrDefault()?.Id);
    private double VideoRowTop => PersistentTrackTop(_tracks.Where(track => track.Kind == EditorTimelineTrackKind.Video).OrderBy(track => track.Order).FirstOrDefault()?.Id);
    private double AudioRowTop => VideoRowTop + TrackHeightFor(_tracks.Where(track => track.Kind == EditorTimelineTrackKind.Video).OrderBy(track => track.Order).FirstOrDefault()?.Id) + TrackGap;
    private bool ShowLinkedAudioTrack => !_usesExplicitMediaTimeline || _mediaClips.Any(clip =>
        clip.Track == EditorClipTrack.Primary && !clip.Muted && !clip.SourceAudioSeparated);
    private double AudioTracksRowTop => ShowLinkedAudioTrack ? AudioRowTop + TrackHeight + TrackGap : AudioRowTop;
    private double TimelineContentHeight => PersistentTracksBottom;

    private sealed class LayerState
    {
        public LayerState(string label, bool enabled, TimeSpan start, TimeSpan end)
        {
            Label = label;
            Enabled = enabled;
            Start = start;
            End = end;
        }

        public string Label { get; set; }
        public bool Enabled { get; set; }
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
    }

    private sealed class TimelineSnapshot
    {
        public TimelineSnapshot(
            TimeSpan start,
            TimeSpan end,
            TimeSpan playhead,
            EditorTimelineLayer selectedLayer,
            Dictionary<EditorTimelineLayer, LayerSnapshot> layers,
            List<EditorProjectClip> mediaClips,
            List<string> selectedMediaClipIds,
            List<EditorProjectAudioTrack> audioTracks,
            string? selectedAudioTrackId)
        {
            Start = start;
            End = end;
            Playhead = playhead;
            SelectedLayer = selectedLayer;
            Layers = layers;
            MediaClips = mediaClips;
            SelectedMediaClipIds = selectedMediaClipIds;
            AudioTracks = audioTracks;
            SelectedAudioTrackId = selectedAudioTrackId;
        }

        public TimeSpan Start { get; }
        public TimeSpan End { get; }
        public TimeSpan Playhead { get; }
        public EditorTimelineLayer SelectedLayer { get; }
        public Dictionary<EditorTimelineLayer, LayerSnapshot> Layers { get; }
        public List<EditorProjectClip> MediaClips { get; }
        public List<string> SelectedMediaClipIds { get; }
        public List<EditorProjectAudioTrack> AudioTracks { get; }
        public string? SelectedAudioTrackId { get; }
        public List<EditorTimelineTrack> Tracks { get; init; } = [];
        public List<EditorProjectMarker> Markers { get; init; } = [];
        public List<string> SelectedAudioTrackIds { get; init; } = [];
        public List<EditorProjectEffectLayer> EffectLayers { get; init; } = [];
        public string? SelectedEffectLayerId { get; init; }
    }

    private sealed class LayerSnapshot
    {
        public LayerSnapshot(string label, bool enabled, TimeSpan start, TimeSpan end)
        {
            Label = label;
            Enabled = enabled;
            Start = start;
            End = end;
        }

        public string Label { get; }
        public bool Enabled { get; }
        public TimeSpan Start { get; }
        public TimeSpan End { get; }
    }

    private enum DragMode
    {
        None,
        Marquee,
        Playhead,
        TrimStart,
        TrimEnd,
        TrimMove,
        LayerStart,
        LayerEnd,
        LayerMove,
        EffectStart,
        EffectEnd,
        EffectMove,
        GraphicStart,
        GraphicEnd,
        GraphicMove,
        MarkerMove,
        MediaTrimStart,
        MediaTrimEnd,
        MediaMove,
        MediaSlip,
        AudioTrimStart,
        AudioTrimEnd,
        AudioMove,
        AudioSlip,
        AudioAutomation,
        TransitionDuration,
        TransitionMove
    }

    private sealed record GraphicKeyframeMarkerTag(string SegmentId, double TimeSeconds);
    private sealed record MarkerVisual(Border Line, Border Cap);
}

public enum EditorTimelineLayer
{
    None,
    Playhead,
    Trim,
    Vfx,
    Text,
    Subtitle,
    Element,
    Marker,
    Media,
    Audio
}

public enum EditorMarkerChangeKind
{
    Added,
    Changed,
    Removed
}

public sealed class EditorMarkersChangedEventArgs : EventArgs
{
    public EditorMarkersChangedEventArgs(
        IReadOnlyList<EditorProjectMarker> markers,
        EditorMarkerChangeKind changeKind,
        string? markerId)
    {
        Markers = markers;
        ChangeKind = changeKind;
        MarkerId = markerId;
    }

    public IReadOnlyList<EditorProjectMarker> Markers { get; }
    public EditorMarkerChangeKind ChangeKind { get; }
    public string? MarkerId { get; }
}

public sealed class EditorMarkerSelectedEventArgs : EventArgs
{
    public EditorMarkerSelectedEventArgs(EditorProjectMarker? marker)
    {
        Marker = marker;
    }

    public EditorProjectMarker? Marker { get; }
}

public enum EditorMediaChangeKind
{
    Added,
    Changed,
    Removed,
    Split,
    History
}

public sealed class EditorMediaClipsChangedEventArgs : EventArgs
{
    public EditorMediaClipsChangedEventArgs(
        IReadOnlyList<EditorProjectClip> clips,
        EditorMediaChangeKind changeKind,
        string? selectedClipId,
        TimeSpan previewTime)
    {
        Clips = clips;
        ChangeKind = changeKind;
        SelectedClipId = selectedClipId;
        PreviewTime = previewTime;
    }

    public IReadOnlyList<EditorProjectClip> Clips { get; }
    public EditorMediaChangeKind ChangeKind { get; }
    public string? SelectedClipId { get; }
    public TimeSpan PreviewTime { get; }
}

public sealed class EditorTimelineEditStatusEventArgs : EventArgs
{
    public EditorTimelineEditStatusEventArgs(string status) => Status = status;
    public string Status { get; }
}

public sealed class EditorAudioTracksChangedEventArgs : EventArgs
{
    public EditorAudioTracksChangedEventArgs(IReadOnlyList<EditorProjectAudioTrack> tracks,
        EditorMediaChangeKind changeKind, string? selectedTrackId, TimeSpan previewTime)
    {
        Tracks = tracks;
        ChangeKind = changeKind;
        SelectedTrackId = selectedTrackId;
        PreviewTime = previewTime;
    }

    public IReadOnlyList<EditorProjectAudioTrack> Tracks { get; }
    public EditorMediaChangeKind ChangeKind { get; }
    public string? SelectedTrackId { get; }
    public TimeSpan PreviewTime { get; }
}

public sealed class EditorLibraryClipDropEventArgs : EventArgs
{
    public EditorLibraryClipDropEventArgs(
        IReadOnlyList<string> filePaths,
        EditorClipTrack track,
        TimeSpan timelinePosition,
        string? trackId = null)
    {
        FilePaths = filePaths;
        Track = track;
        TimelinePosition = timelinePosition;
        TrackId = trackId;
    }

    public IReadOnlyList<string> FilePaths { get; }
    public EditorClipTrack Track { get; }
    public TimeSpan TimelinePosition { get; }
    public string? TrackId { get; }
}

public sealed class EditorGraphicSegmentsChangedEventArgs : EventArgs
{
    public EditorGraphicSegmentsChangedEventArgs(
        IReadOnlyList<EditorProjectGraphicLayer> segments,
        string? selectedSegmentId,
        TimeSpan previewTime)
    {
        Segments = segments;
        SelectedSegmentId = selectedSegmentId;
        PreviewTime = previewTime;
    }

    public IReadOnlyList<EditorProjectGraphicLayer> Segments { get; }
    public string? SelectedSegmentId { get; }
    public TimeSpan PreviewTime { get; }
}

public sealed class EditorGraphicSegmentSelectedEventArgs : EventArgs
{
    public EditorGraphicSegmentSelectedEventArgs(string segmentId, TimeSpan start)
    {
        SegmentId = segmentId;
        Start = start;
    }

    public string SegmentId { get; }
    public TimeSpan Start { get; }
}

public sealed class EditorTimelineChangedEventArgs : EventArgs
{
    public EditorTimelineChangedEventArgs(
        TimeSpan start,
        TimeSpan end,
        TimeSpan previewTime,
        EditorTimelineLayer changedLayer,
        bool textEnabled,
        TimeSpan textStart,
        TimeSpan textEnd,
        bool vfxEnabled,
        TimeSpan vfxStart,
        TimeSpan vfxEnd,
        bool subtitleEnabled,
        TimeSpan subtitleStart,
        TimeSpan subtitleEnd,
        bool elementEnabled,
        TimeSpan elementStart,
        TimeSpan elementEnd,
        bool isSelectionOnly = false)
    {
        Start = start;
        End = end;
        PreviewTime = previewTime;
        ChangedLayer = changedLayer;
        TextEnabled = textEnabled;
        TextStart = textStart;
        TextEnd = textEnd;
        VfxEnabled = vfxEnabled;
        VfxStart = vfxStart;
        VfxEnd = vfxEnd;
        SubtitleEnabled = subtitleEnabled;
        SubtitleStart = subtitleStart;
        SubtitleEnd = subtitleEnd;
        ElementEnabled = elementEnabled;
        ElementStart = elementStart;
        ElementEnd = elementEnd;
        IsSelectionOnly = isSelectionOnly;
    }

    public TimeSpan Start { get; }
    public TimeSpan End { get; }
    public TimeSpan PreviewTime { get; }
    public EditorTimelineLayer ChangedLayer { get; }
    public bool IsSeekOnly => ChangedLayer == EditorTimelineLayer.Playhead;
    public bool IsSelectionOnly { get; }
    public bool TextEnabled { get; }
    public TimeSpan TextStart { get; }
    public TimeSpan TextEnd { get; }
    public bool VfxEnabled { get; }
    public TimeSpan VfxStart { get; }
    public TimeSpan VfxEnd { get; }
    public bool SubtitleEnabled { get; }
    public TimeSpan SubtitleStart { get; }
    public TimeSpan SubtitleEnd { get; }
    public bool ElementEnabled { get; }
    public TimeSpan ElementStart { get; }
    public TimeSpan ElementEnd { get; }
}
