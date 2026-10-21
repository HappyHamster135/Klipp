using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Branding;
using Klipp.Core.Editing;
using Klipp.Core.Input;
using Klipp.Core.Models;
using Klipp.Core.Performance;
using Klipp.Core.Subtitles;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Graphics.Effects;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI;

namespace Klipp.Desktop.Controls;

public sealed partial class SafeClipEditorView : UserControl
{
    private static readonly SemaphoreSlim EditorLibraryThumbnailDecodeGate = new(initialCount: 2, maxCount: 2);

    private enum ResizeTarget
    {
        None,
        Video,
        Text,
        Subtitle
    }

    private sealed record PreviewTransition(
        EditorProjectClip OutgoingClip,
        EditorProjectClip IncomingClip,
        string Style,
        double Progress);

    private sealed class AudioTrackPreviewState : IDisposable
    {
        public AudioTrackPreviewState(string trackId, string sourcePath, int loadVersion, int audioStreamIndex)
        {
            TrackId = trackId;
            SourcePath = sourcePath;
            AudioStreamIndex = audioStreamIndex;
            LoadVersion = loadVersion;
            Player = new MediaPlayer { AutoPlay = false, Volume = 0 };
            PreviewAudioGainEffect.SetGain(Player, 0);
            Player.CommandManager.IsEnabled = false;
            OpeningStartedAt = PreviewNowSeconds;
        }

        public string TrackId { get; }
        public string SourcePath { get; }
        public int AudioStreamIndex { get; }
        public MediaSource? Source { get; set; }
        public MediaPlaybackItem? Item { get; set; }
        public int LoadVersion { get; }
        public MediaPlayer Player { get; }
        public EditorPreviewSeekState Seek { get; } = new();
        public EditorAudioPreviewTimingState Timing { get; } = new();
        public double OpeningStartedAt { get; }
        public bool MediaOpened { get; set; }
        public TimeSpan PendingPosition { get; set; }
        public bool IsOpened { get; set; }
        public bool Failed { get; set; }
        public bool IsDisposed { get; private set; }
        private bool _playRequested;
        public TypedEventHandler<MediaPlayer, object>? OpenedHandler { get; set; }
        public TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>? FailedHandler { get; set; }
        public TypedEventHandler<MediaPlaybackItem, Windows.Foundation.Collections.IVectorChangedEventArgs>? TracksChangedHandler { get; set; }
        public TypedEventHandler<MediaPlaybackSession, object>? SeekCompletedHandler { get; set; }

        public void PauseIfPlaying()
        {
            if (IsDisposed) return;
            try
            {
                if (_playRequested || Player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                    Player.Pause();
            }
            catch { }
            _playRequested = false;
        }

        public void PlayIfReady()
        {
            if (IsDisposed || !IsOpened || Failed ||
                Seek.IsPending(Player.PlaybackSession.Position.TotalSeconds, PreviewNowSeconds)) return;
            var playbackState = Player.PlaybackSession.PlaybackState;
            if (playbackState == MediaPlaybackState.Playing)
            {
                _playRequested = true;
                return;
            }
            if (_playRequested && playbackState is MediaPlaybackState.Opening or MediaPlaybackState.Buffering)
                return;
            Player.Play();
            _playRequested = true;
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;
            if (OpenedHandler is not null) Player.MediaOpened -= OpenedHandler;
            if (FailedHandler is not null) Player.MediaFailed -= FailedHandler;
            if (SeekCompletedHandler is not null) Player.PlaybackSession.SeekCompleted -= SeekCompletedHandler;
            if (Item is not null && TracksChangedHandler is not null) Item.AudioTracksChanged -= TracksChangedHandler;
            try { Player.Pause(); } catch { }
            try { Player.Source = null; } catch { }
            PreviewAudioGainEffect.Release(Player);
            Player.Dispose();
            Source?.Dispose();
            Source = null;
            Item = null;
        }
    }

    private readonly DispatcherTimer _positionTimer;
    private readonly ThumbnailService _thumbnailService = new();
    private readonly WaveformService _waveformService = new();
    private readonly MediaProbeService _mediaProbeService = new();
    private readonly PreviewProxyService _previewProxyService = new();
    private readonly CubeLutService _cubeLutService = new();
    private readonly EditorProjectService _projectService = new();
    private readonly EditorTextTemplateService _textTemplateService = new();
    private readonly EditorBrandKitService _brandKitService = new();
    private readonly AudioLoudnessAnalysisService _audioLoudnessAnalysisService = new();
    private readonly AudioSignalAnalysisService _audioSignalAnalysisService = new();
    private readonly WhisperTranscriptionService _whisperTranscriptionService = new();
    private readonly FFmpegEncoderService _ffmpegEncoderService = new();
    private readonly MissingMediaSearchService _missingMediaSearchService;
    private readonly DispatcherTimer _projectChangeTimer;
    private readonly List<EditorProject> _projectUndoHistory = [];
    private readonly List<EditorProject> _projectRedoHistory = [];
    private readonly Dictionary<string, AudioTrackPreviewState> _audioTrackPreviewPlayers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioSignalAnalysis> _audioSignalAnalyses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _audioSignalAnalysisInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TimeSpan> _libraryDurationCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _addClipGate = new(initialCount: 1, maxCount: 1);
    private List<EditorShortcutBinding> _shortcuts = EditorShortcutCatalog.CreateDefaults();
    private CancellationTokenSource? _missingMediaSearchCts;
    private CancellationTokenSource? _audioTrackWaveformCts;
    private CancellationTokenSource? _audioSignalAnalysisCts;
    private CancellationTokenSource? _previewProxyCts;
    private CancellationTokenSource? _transcriptionCts;
    private const int MaxProjectHistory = 60;
    private const double CanvasWidth = 1280;
    private const double CanvasHeight = 720;
    private const double DefaultTextAnchorX = 0.5;
    private const double DefaultTextAnchorY = 0.84;
    private const double DefaultSubtitleAnchorX = 0.5;
    private const double DefaultSubtitleAnchorY = 0.88;
    private const double DefaultTextSizePixels = 64;
    private const double DefaultSubtitleSizePixels = 42;
    private const double DefaultTextBoxWidthPixels = 420;
    private const double DefaultSubtitleBoxWidthPixels = 760;
    private const double CanvasSnapPixels = 8;
    private string? _filePath;
    private string? _nativePreviewMediaPath;
    private string? _projectSourcePath;
    private string? _activePreviewClipId;
    private string? _activeTransitionPreviewClipId;
    private string? _transitionPreviewSourcePath;
    private string? _activeOverlayPreviewClipId;
    private string? _overlayPreviewSourcePath;
    private string? _primaryEffectPreviewSignature;
    private string? _transitionEffectPreviewSignature;
    private string? _overlayEffectPreviewSignature;
    private string? _editingExtraTextLayerId;
    private string? _editingSubtitleLayerId;
    private string? _selectedAudioTrackId;
    private string? _selectedGraphicLayerId;
    private TimeSpan _pendingPreviewSourcePosition;
    private TimeSpan _pendingTransitionSourcePosition;
    private TimeSpan _pendingOverlaySourcePosition;
    private EditorProject? _project;
    private EditorProject? _projectHistoryBaseline;
    private bool _suppressProjectChanges;
    private bool _projectChangePending;
    private bool _projectSaveInProgress;
    private bool _projectSaveQueued;
    private int _projectLoadVersion;
    private TimeSpan _duration = TimeSpan.FromSeconds(1);
    private TimeSpan _previewPosition = TimeSpan.Zero;
    private bool _usingNativePreview;
    private bool _isPreviewOpening;
    private bool _wasPreviewBuffering;
    private bool _loopPreview;
    private long _lastMeterUpdateTicks;
    private TimeSpan _lastTimelinePreviewTime = TimeSpan.Zero;
    private long _lastLiveSeekTicks;
    private double _playbackSpeed = 1.0;
    private string _activeTool = "general";
    private string _textStyle = "plain";
    private string _textFontFamily = EditorTextTypography.Automatic;
    private string _textFontFilePath = string.Empty;
    private string _textFontFamilyName = string.Empty;
    private string _textColor = EditorTextTypography.Automatic;
    private string _textPosition = "custom";
    private string _textAnimation = "none";
    private double _textAnchorX = DefaultTextAnchorX;
    private double _textAnchorY = DefaultTextAnchorY;
    private double _textSize = DefaultTextSizePixels;
    private double _textBoxWidth = DefaultTextBoxWidthPixels;
    private double _textOpacity = 1;
    private List<EditorProjectTextKeyframe> _textKeyframes = [];
    private TimeSpan _textTimelineRangeStart = TimeSpan.Zero;
    private TimeSpan _textTimelineRangeEnd = TimeSpan.FromSeconds(5);
    private string _subtitleStyle = "subtitle";
    private string _subtitleFontFamily = EditorTextTypography.Automatic;
    private string _subtitleFontFilePath = string.Empty;
    private string _subtitleFontFamilyName = string.Empty;
    private string _subtitleColor = EditorTextTypography.Automatic;
    private string _subtitlePosition = "bottom";
    private double _subtitleAnchorX = DefaultSubtitleAnchorX;
    private double _subtitleAnchorY = DefaultSubtitleAnchorY;
    private double _subtitleSize = DefaultSubtitleSizePixels;
    private double _subtitleBoxWidth = DefaultSubtitleBoxWidthPixels;
    private string _videoFilter = "none";
    private string _elementOverlay = "none";
    private double _fadeInSeconds;
    private double _fadeOutSeconds;
    private double _freezeFrameSeconds;
    private string _clipTransitionStyle = "cut";
    private double _clipTransitionSeconds = 0.5;
    private string _exportQuality = "high";
    private string _previewQuality = "original";
    private int _exportFrameRate = 60;
    private string _exportEncoder = "auto";
    private int _exportBitrateMbps = 16;
    private string _exportResolutionMode = "canvas";
    private int _exportWidth = 1280;
    private int _exportHeight = 720;
    private bool _exportAspectLocked = true;
    private bool _showSafeAreaGuides;
    private double _exportLockedAspectRatio = 16.0 / 9.0;
    private int _exportAudioBitrateKbps = 192;
    private IReadOnlyList<FFmpegEncoderCapability> _encoderCapabilities = [];
    private double _videoScale = 1.0;
    private double _videoOffsetX;
    private double _videoOffsetY;
    private double _previewZoom = 1.0;
    private bool _isLoadingClip;
    private bool _isPreviewPlaying;
    private bool _isBuildingPreviewProxies;
    private bool _previewProxyBuildQueued;
    private bool _temporarilyUseOriginalPreview;
    private long _lastPreviewTick;
    private int _thumbnailLoadVersion;
    private int _libraryThumbnailLoadVersion;
    private bool _syncingTimelineState;
    private bool _syncingVideoTransformControls;
    private bool _syncingSelectedClipVisualControls;
    private bool _syncingMasterAudioControls;
    private bool _syncingClipAudioControls;
    private bool _syncingAudioTrackControls;
    private bool _syncingProjectName;
    private bool _syncingPreviewZoomControls;
    private bool _syncingExportControls;
    private bool _syncingTextSizeControls;
    private bool _syncingTextKeyframeControls;
    private bool _syncingSubtitleSizeControls;
    private bool _syncingTypographyControls;
    private bool _syncingBrandKitControls;
    private bool _syncingGraphicControls;
    private bool _syncingMarkerControls;
    private string _editorLibraryScope = "all";
    private string _editorLibrarySort = "newest";
    private string _editorLibraryCollection = string.Empty;
    private string? _selectedBrandKitId;
    private string? _previewProxyStatusOverride;
    private bool _editingMarkerMetadata;
    private bool _editingClipMetadata;
    private bool _isDraggingTextOverlay;
    private bool _isDraggingSubtitleOverlay;
    private bool _isDraggingGraphicOverlay;
    private bool _isResizingGraphicOverlay;
    private bool _isDraggingVideoTransform;
    private ResizeTarget _resizeTarget = ResizeTarget.None;
    private bool _suppressNextPreviewTap;
    private double _audioMeterPeakHold;
    private Point _textDragOffset;
    private Point _subtitleDragOffset;
    private Point _graphicDragOffset;
    private Point _graphicResizeStartPoint;
    private double _graphicResizeStartWidth;
    private double _graphicResizeStartHeight;
    private double _graphicResizeStartAnchorX;
    private double _graphicResizeStartAnchorY;
    private string _graphicResizeMode = string.Empty;
    private UIElement? _graphicPointerCaptureElement;
    private Point _videoDragStartPoint;
    private double _videoDragStartOffsetX;
    private double _videoDragStartOffsetY;
    private Point _resizeStartPoint;
    private Point _resizeStartCenter;
    private double _resizeStartDistance;
    private double _resizeStartTextSize;
    private double _resizeStartTextBoxWidth;
    private double _resizeStartSubtitleSize;
    private double _resizeStartSubtitleBoxWidth;
    private double _resizeStartVideoScale;
    private string _resizeHandleTag = string.Empty;
    private UIElement? _resizeCaptureElement;

    public event EventHandler? BackRequested;
    public event EventHandler<ClipEditorExportEventArgs>? ExportRequested;
    public event EventHandler<EditorProjectExportEventArgs>? ProjectExportRequested;
    public event EventHandler? LibraryImportRequested;
    public event EventHandler<EditorLibraryItemEventArgs>? LibraryFavoriteToggleRequested;
    public event EventHandler<EditorLibraryItemEventArgs>? LibraryOrganizeRequested;
    public event EventHandler? AudioTrackImportRequested;
    public event EventHandler? SubtitleSrtImportRequested;
    public event EventHandler? SubtitleSrtExportRequested;
    public event EventHandler? GraphicImportRequested;
    public event EventHandler? BrandLogoImportRequested;
    public event EventHandler? BrandFontImportRequested;
    public event EventHandler? LutImportRequested;
    public event EventHandler<MissingMediaRelinkEventArgs>? MissingMediaRelinkRequested;
    public event EventHandler<EditorExportJobActionEventArgs>? ExportJobActionRequested;
    public Func<string, IProgress<ClipImportProgress>?, CancellationToken, Task<ClipImportResult>>?
        MediaImportHandler { get; set; }

    public ObservableCollection<EditorLibraryItem> LibraryItems { get; } = [];
    public ObservableCollection<EditorLibraryItem> FilteredLibraryItems { get; } = [];
    public ObservableCollection<string> LibraryCollectionOptions { get; } = ["All collections"];
    public ObservableCollection<EditorProjectTextLayer> ExtraTextLayers { get; } = [];
    public ObservableCollection<EditorProjectTextLayer> SubtitleLayers { get; } = [];
    public ObservableCollection<EditorProjectGraphicLayer> GraphicLayers { get; } = [];
    public ObservableCollection<EditorProjectAudioTrack> AudioTracks { get; } = [];
    public ObservableCollection<SavedEditorProjectInfo> SavedProjects { get; } = [];
    public ObservableCollection<MissingEditorMediaItem> MissingMediaItems { get; } = [];
    public ObservableCollection<EditorExportQueueItem> ExportQueueItems { get; } = [];
    public ObservableCollection<EditorTextTemplate> TextTemplates { get; } = [];
    public ObservableCollection<EditorBrandKit> BrandKits { get; } = [];
    public ObservableCollection<EditorBrandLogoItem> BrandLogos { get; } = [];

    public SafeClipEditorView()
    {
        _missingMediaSearchService = new MissingMediaSearchService(_mediaProbeService);
        InitializeComponent();
        // Choose the video output path before opening a source. Switching a
        // paused MediaPlayerElement into frame-server mode after a seek can
        // return studio-range RGB and wash out the entire preview.
        NativePreviewPlayer.MediaPlayer.IsVideoFrameServerEnabled = true;
        TransitionPreviewPlayer.MediaPlayer.IsVideoFrameServerEnabled = true;
        OverlayPreviewPlayer.MediaPlayer.IsVideoFrameServerEnabled = true;
        PreviewAudioGainEffect.SetGain(NativePreviewPlayer.MediaPlayer, 0);
        PreviewAudioGainEffect.SetGain(TransitionPreviewPlayer.MediaPlayer, 0);
        ConfigureEditorSliders();

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000.0 / 30) };
        _positionTimer.Tick += OnPositionTimerTick;
        _projectChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _projectChangeTimer.Tick += OnProjectChangeTimerTick;

        EnsurePrimarySeekEvents();
        TransitionPreviewPlayer.MediaPlayer.MediaOpened += OnTransitionPreviewMediaOpened;
        TransitionPreviewPlayer.MediaPlayer.MediaFailed += OnTransitionPreviewMediaFailed;
        OverlayPreviewPlayer.MediaPlayer.MediaOpened += OnOverlayPreviewMediaOpened;

        LayerTimeline.HistoryChanged += OnTimelineHistoryChanged;
        LayerTimeline.ViewportChanged += (_, _) => UpdateTimelineChrome();
        LayerTimeline.DragCompleted += OnLayerTimelineDragCompleted;
        LayerTimeline.MediaClipsChanged += OnMediaClipsChanged;
        LayerTimeline.AudioTracksChanged += OnTimelineAudioTracksChanged;
        LayerTimeline.TracksChanged += OnTimelineTracksChanged;
        LayerTimeline.EditStarting += OnTimelineEditStarting;
        ConfigureTimelineFxControls();
        TimelineFxAmountSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnTimelineFxAmountPressed), true);
        TimelineFxAmountSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnTimelineFxAmountReleased), true);
        TimelineFxAmountSlider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnTimelineFxAmountReleased), true);
        LayerTimeline.EditStatusChanged += OnTimelineEditStatusChanged;
        LayerTimeline.LibraryClipDropped += OnLibraryClipDropped;
        LayerTimeline.GraphicSegmentsChanged += OnGraphicSegmentsChanged;
        LayerTimeline.GraphicSegmentSelected += OnGraphicSegmentSelected;
        LayerTimeline.MarkersChanged += OnMarkersChanged;
        LayerTimeline.MarkerSelected += OnMarkerSelected;
        Loaded += OnEditorLoaded;
        Unloaded += (_, _) => Stop();

        SetEditorTool("general");
        UpdateAllOptionButtons();
        UpdateTrimUi();
        UpdatePlaybackUi();
        UpdateVideoTransformPreview();
        UpdatePreviewStageZoom();
        UpdateTranscriptionReadyState();
        UpdateShortcutToolTips();
    }

    public void ConfigureShortcuts(IEnumerable<EditorShortcutBinding>? shortcuts)
    {
        _shortcuts = EditorShortcutCatalog.Normalize(shortcuts);
        UpdateShortcutToolTips();
    }

    private async void OnEditorLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnEditorLoaded;
        try
        {
            await RefreshTextTemplatesAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Klipp] Text templates could not be loaded: {ex.Message}");
            TextTemplateStatusText.Text = "Text templates could not be loaded.";
        }

        try
        {
            await RefreshBrandKitsAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Klipp] Brand kits could not be loaded: {ex.Message}");
            BrandKitStatusText.Text = "Brand kits could not be loaded.";
            BrandLogoStatusText.Text = "Brand assets are unavailable.";
        }

        try
        {
            _encoderCapabilities = await _ffmpegEncoderService.DetectAsync();
            UpdateExportControls();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Klipp] Encoder detection failed: {ex.Message}");
            ExportEncoderStatusText.Text = "Hardware check failed. CPU fallback will be used.";
        }
    }

    private void ConfigureEditorSliders()
    {
        _syncingVideoTransformControls = true;
        _syncingPreviewZoomControls = true;
        _syncingTextSizeControls = true;
        _syncingSubtitleSizeControls = true;
        _syncingSelectedClipVisualControls = true;
        _syncingMasterAudioControls = true;
        _syncingClipAudioControls = true;
        _syncingAudioTrackControls = true;
        _syncingExportControls = true;
        _syncingTextKeyframeControls = true;
        _syncingGraphicControls = true;
        try
        {
            ExportBitrateSlider.Value = 16;
            ExportAudioBitrateSlider.Value = 192;

            PreviewZoomSlider.Maximum = 200;
            PreviewZoomSlider.Value = 100;
            PreviewZoomSlider.Minimum = 25;

            VideoScaleSlider.Maximum = 300;
            VideoScaleSlider.Value = 100;
            VideoScaleSlider.Minimum = 25;

            SelectedClipScaleSlider.Maximum = 400;
            SelectedClipScaleSlider.Value = 100;
            SelectedClipScaleSlider.Minimum = 10;

            ClipContrastSlider.Maximum = 200;
            ClipContrastSlider.Value = 100;
            ClipContrastSlider.Minimum = 50;

            QuickContrastSlider.Maximum = 200;
            QuickContrastSlider.Value = 100;
            QuickContrastSlider.Minimum = 50;

            ClipSaturationSlider.Maximum = 300;
            ClipSaturationSlider.Value = 100;
            ClipSaturationSlider.Minimum = 0;

            ClipGammaSlider.Maximum = 200;
            ClipGammaSlider.Value = 100;
            ClipGammaSlider.Minimum = 50;

            ClipLutStrengthSlider.Maximum = 100;
            ClipLutStrengthSlider.Value = 100;
            ClipLutStrengthSlider.Minimum = 0;

            SelectedClipMaskRoundnessSlider.Value = 16;
            ClipFilterStrengthSlider.Value = 100;
            QuickClipFilterStrengthSlider.Value = 100;
            TextOpacitySlider.Value = 100;
            GraphicMaskRoundnessSlider.Value = 20;

            EditorVolumeSlider.Maximum = 200;
            EditorVolumeSlider.Value = 100;
            EditorVolumeSlider.Minimum = 0;

            ClipVolumeSlider.Maximum = 200;
            ClipVolumeSlider.Value = 100;
            ClipVolumeSlider.Minimum = 0;

            SelectedAudioTrackVolumeSlider.Maximum = 200;
            SelectedAudioTrackVolumeSlider.Value = 100;
            SelectedAudioTrackVolumeSlider.Minimum = 0;

            TextSizeSlider.Maximum = 220;
            TextSizeSlider.Value = DefaultTextSizePixels;
            TextSizeSlider.Minimum = 12;
            TextSizeValueText.Text = $"{DefaultTextSizePixels:0} px";

            SubtitleSizeSlider.Maximum = 160;
            SubtitleSizeSlider.Value = DefaultSubtitleSizePixels;
            SubtitleSizeSlider.Minimum = 18;
            SubtitleSizeValueText.Text = $"{DefaultSubtitleSizePixels:0} px";
        }
        finally
        {
            _syncingVideoTransformControls = false;
            _syncingPreviewZoomControls = false;
            _syncingTextSizeControls = false;
            _syncingSubtitleSizeControls = false;
            _syncingSelectedClipVisualControls = false;
            _syncingMasterAudioControls = false;
            _syncingClipAudioControls = false;
            _syncingAudioTrackControls = false;
            _syncingExportControls = false;
            _syncingTextKeyframeControls = false;
            _syncingGraphicControls = false;
        }
    }

    public async Task LoadClipAsync(string filePath, TimeSpan duration)
    {
        if (!File.Exists(filePath))
            return;

        var loadVersion = ++_projectLoadVersion;
        _previewProxyCts?.Cancel();
        _previewProxyCts = null;
        _isBuildingPreviewProxies = false;
        _previewProxyBuildQueued = false;
        ClearPreviewVisualCaches();
        _isLoadingClip = true;
        _suppressProjectChanges = true;

        try
        {
            PausePreview();
            ResetAudioSignalAnalyses();
            _previewPosition = TimeSpan.Zero;
            _usingNativePreview = false;
            _lastLiveSeekTicks = 0;
            ResetEditorAdjustments();

            var project = await _projectService.LoadOrCreateAsync(filePath, duration);
            if (loadVersion != _projectLoadVersion)
                return;
            await ReconcileProjectDurationsAsync(project);
            if (loadVersion != _projectLoadVersion)
                return;
            var separatedSourceAudio = await SeparateProjectSourceAudioAsync(project);
            if (loadVersion != _projectLoadVersion)
                return;

            _projectSourcePath = filePath;
            _project = project;
            _filePath = filePath;
            TitleText.Text = project.Name;
            UpdatePreviewCanvas();
            SyncExtraTextLayers(project);
            SyncSubtitleLayers(project);
            SyncGraphicLayers(project);
            LayerTimeline.SetTracks(project.Tracks);
            SyncAudioTracks(project);

            LayerTimeline.SetMediaClips(project.Clips);
            LayerTimeline.SetEffectLayers(project.EffectLayers);
            LayerTimeline.SetMarkers(project.Markers);
            QueueAudioSignalAnalyses(project.Clips.Select(clip => clip.SourcePath));
            _duration = TimeSpan.FromSeconds(project.DurationSeconds);
            LayerTimeline.Duration = _duration;
            LayerTimeline.Playhead = TimeSpan.Zero;
            ApplyProjectSettings(project.Settings);

            var firstClip = project.PrimaryClips.FirstOrDefault(clip => File.Exists(clip.SourcePath));
            if (firstClip is not null)
            {
                _filePath = firstClip.SourcePath;
                _activePreviewClipId = firstClip.Id;
                QueueThumbnailLoad(firstClip.SourcePath);
                QueueWaveformLoad(firstClip.SourcePath);
                LoadNativePreview(firstClip.SourcePath);
            }

            SeekTo(TimeSpan.Zero);
            _projectUndoHistory.Clear();
            _projectRedoHistory.Clear();
            _projectHistoryBaseline = EditorProjectService.Clone(project);
            _projectChangePending = false;
            ProjectStatusText.Text = File.Exists(_projectService.GetAutosavePath(filePath))
                ? "Recovered from autosave"
                : "Autosave on";
            UpdateProjectChrome();
            UpdateTrimUi();
            UpdateTransitionPreview(_previewPosition, seekExisting: false);
            UpdateOverlayPreview(_previewPosition, seekExisting: false);
            UpdatePlaybackUi();
            await RefreshSavedProjectsAsync();
            if (string.Equals(_activeTool, "general", StringComparison.Ordinal))
                JumpToClipInspectorSection("clip");
            if (separatedSourceAudio)
                QueueProjectSaveOnly();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Klipp] Safe editor kunde inte ladda klipp: {ex.Message}");
        }
        finally
        {
            if (loadVersion == _projectLoadVersion)
            {
                _isLoadingClip = false;
                _suppressProjectChanges = false;
            }
        }
        if (loadVersion == _projectLoadVersion)
            _ = BuildPreviewProxiesAsync(automatic: true);
    }

    public async Task RelinkMissingMediaAsync(
        string originalPath,
        string replacementPath,
        bool isAudio,
        bool isGraphic = false)
    {
        if (_project is null ||
            string.IsNullOrWhiteSpace(originalPath) ||
            string.IsNullOrWhiteSpace(replacementPath) ||
            !File.Exists(replacementPath))
        {
            return;
        }

        TimeSpan? duration = null;
        if (!isGraphic)
        {
            duration = await _mediaProbeService.GetDurationAsync(replacementPath);
            if (duration is null || duration <= TimeSpan.FromSeconds(0.1))
                throw new InvalidOperationException("The selected file could not be read as media.");
        }

        CommitPendingProjectChange();
        var selectedClipId = LayerTimeline.SelectedMediaClipId;
        if (!ApplyMediaReplacement(
                originalPath,
                replacementPath,
                isAudio,
                isGraphic,
                duration?.TotalSeconds))
        {
            return;
        }

        await SeparateProjectSourceAudioAsync(_project);
        CompleteMediaRelink(selectedClipId, "Media relinked");
    }

    private bool ApplyMediaReplacement(
        string originalPath,
        string replacementPath,
        bool isAudio,
        bool isGraphic,
        double? durationSeconds)
    {
        if (_project is null)
            return false;

        var changed = false;
        if (isGraphic)
        {
            foreach (var layer in _project.GraphicLayers.Where(layer =>
                         string.Equals(layer.SourcePath, originalPath, StringComparison.OrdinalIgnoreCase)))
            {
                layer.SourcePath = replacementPath;
                layer.Name = Path.GetFileNameWithoutExtension(replacementPath);
                changed = true;
            }
        }
        else if (isAudio && durationSeconds is { } audioDuration)
        {
            foreach (var track in _project.AudioTracks.Where(track =>
                         string.Equals(track.SourcePath, originalPath, StringComparison.OrdinalIgnoreCase)))
            {
                ReplaceAudioTrackSource(track, replacementPath, audioDuration);
                changed = true;
            }
        }
        else if (!isAudio && durationSeconds is { } videoDuration)
        {
            foreach (var clip in _project.Clips.Where(clip =>
                         string.Equals(clip.SourcePath, originalPath, StringComparison.OrdinalIgnoreCase)))
            {
                ReplaceClipSource(clip, replacementPath, videoDuration);
                changed = true;
            }
            foreach (var audio in _project.AudioTracks.Where(audio =>
                         !string.IsNullOrEmpty(audio.SourceClipId) &&
                         string.Equals(audio.SourcePath, originalPath, StringComparison.OrdinalIgnoreCase)))
            {
                ReplaceAudioTrackSource(audio, replacementPath, videoDuration);
                changed = true;
            }

            if (string.Equals(_projectSourcePath, originalPath, StringComparison.OrdinalIgnoreCase))
                _projectSourcePath = replacementPath;
        }

        return changed;
    }

    private void CompleteMediaRelink(string? selectedClipId, string status)
    {
        if (_project is null)
            return;

        _project.Normalize();
        _suppressProjectChanges = true;
        try
        {
            LayerTimeline.SetTracks(_project.Tracks);
            SyncAudioTracks(_project);
            SyncGraphicLayers(_project);
            LayerTimeline.SetMediaClips(_project.Clips, selectedClipId);
            LayerTimeline.SetEffectLayers(_project.EffectLayers, LayerTimeline.SelectedEffectLayerId);
            QueueAudioSignalAnalyses(_project.Clips.Select(clip => clip.SourcePath));
            _duration = TimeSpan.FromSeconds(_project.DurationSeconds);
            LayerTimeline.Duration = _duration;
        }
        finally
        {
            _suppressProjectChanges = false;
        }

        var previewClip = _project.FindClip(_activePreviewClipId) ??
                          _project.PrimaryClips.FirstOrDefault(clip => File.Exists(clip.SourcePath));
        if (previewClip is not null && File.Exists(previewClip.SourcePath))
        {
            _filePath = previewClip.SourcePath;
            _activePreviewClipId = previewClip.Id;
            QueueThumbnailLoad(previewClip.SourcePath);
            QueueWaveformLoad(previewClip.SourcePath);
            LoadNativePreview(previewClip.SourcePath);
        }

        ProjectStatusText.Text = status;
        UpdateProjectChrome();
        QueueProjectChange();
    }

    public async Task ImportGraphicLayerAsync(string filePath)
    {
        if (_project is null || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        if (extension is not ".png" and not ".jpg" and not ".jpeg" and not ".webp" and not ".bmp")
            throw new InvalidOperationException("Choose a PNG, JPG, WebP or BMP image.");

        uint pixelWidth;
        uint pixelHeight;
        await using (var stream = new FileStream(
                         filePath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.ReadWrite | FileShare.Delete,
                         bufferSize: 64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var randomAccessStream = stream.AsRandomAccessStream())
        {
            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream);
            pixelWidth = decoder.PixelWidth;
            pixelHeight = decoder.PixelHeight;
        }

        if (pixelWidth == 0 || pixelHeight == 0)
            throw new InvalidOperationException("The selected image has no readable dimensions.");

        CommitPendingProjectChange();
        var maxWidth = _project.CanvasWidth * 0.42;
        var maxHeight = _project.CanvasHeight * 0.42;
        var scale = Math.Min(1, Math.Min(maxWidth / pixelWidth, maxHeight / pixelHeight));
        var start = Math.Clamp(CurrentTimelinePosition.TotalSeconds, 0, Math.Max(0, _project.DurationSeconds - 0.1));
        var end = Math.Min(_project.DurationSeconds, start + 5);
        if (end <= start + 0.1)
            end = _project.DurationSeconds;

        var layer = new EditorProjectGraphicLayer
        {
            Name = Path.GetFileNameWithoutExtension(filePath),
            SourcePath = filePath,
            Width = Math.Max(16, pixelWidth * scale),
            Height = Math.Max(16, pixelHeight * scale),
            StartSeconds = start,
            EndSeconds = Math.Max(start + 0.1, end)
        };
        layer.Normalize(_project.DurationSeconds, _project.CanvasWidth, _project.CanvasHeight);
        _project.GraphicLayers.Add(layer);
        _selectedGraphicLayerId = layer.Id;
        SyncGraphicLayers(_project);
        SetEditorTool("elements");
        UpdateGraphicLayersPreview();
        ProjectStatusText.Text = "Graphic layer added";
        QueueProjectChange();
    }

    public async Task ImportBrandLogoAsync(string filePath)
    {
        var kit = GetSelectedBrandKit() ??
                  throw new InvalidOperationException("Select a brand kit before importing a logo.");
        var logo = await _brandKitService.ImportLogoAsync(kit.Id, filePath);
        await RefreshBrandKitsAsync(kit.Id);
        BrandLogoStatusText.Text = $"{logo.Name} added to {kit.Name}.";
    }

    public async Task ImportBrandFontAsync(string filePath)
    {
        var kit = GetSelectedBrandKit() ??
                  throw new InvalidOperationException("Select a brand kit before importing a font.");
        var font = await _brandKitService.ImportFontAsync(kit.Id, filePath);
        await RefreshBrandKitsAsync(kit.Id);
        BrandKitStatusText.Text = $"{font.FamilyName} imported and set as the kit font.";
    }

    public async Task ApplyLutToSelectedClipAsync(string filePath)
    {
        if (LayerTimeline.GetSelectedMediaClip() is null)
            throw new InvalidOperationException("Select a video clip before importing a LUT.");

        var lut = await _cubeLutService.ValidateAsync(filePath);
        CommitPendingProjectChange();
        if (!LayerTimeline.SetSelectedMediaClipLut(lut.Path, 1))
            return;

        SyncSelectedClipVisualControls();
        UpdateVfxPreview();
        ProjectStatusText.Text = $"LUT applied: {lut.Name} ({lut.Size}³)";
        QueueProjectChange();
    }

    public async Task ImportSubtitlesFromSrtAsync(string filePath)
    {
        if (_project is null || !File.Exists(filePath))
            return;

        var cues = SrtSubtitleService.Parse(await File.ReadAllTextAsync(filePath));
        var importable = cues
            .Where(cue => cue.Start.TotalSeconds < _project.DurationSeconds)
            .ToList();
        if (importable.Count == 0)
            throw new InvalidOperationException("The SRT file did not contain subtitles within this project.");

        CommitPendingProjectChange();
        foreach (var cue in importable)
        {
            var text = cue.Text.Trim();
            var layer = new EditorProjectTextLayer
            {
                Name = text.Length > 24 ? $"{text[..24]}..." : text,
                Text = text,
                Style = _subtitleStyle,
                Animation = "none",
                FontFamily = _subtitleFontFamily,
                FontFilePath = _subtitleFontFilePath,
                FontFamilyName = _subtitleFontFamilyName,
                Color = _subtitleColor,
                AnchorX = _subtitleAnchorX,
                AnchorY = _subtitleAnchorY,
                Size = _subtitleSize,
                BoxWidth = _subtitleBoxWidth,
                StartSeconds = cue.Start.TotalSeconds,
                EndSeconds = Math.Min(cue.End.TotalSeconds, _project.DurationSeconds)
            };
            layer.Normalize(_project.DurationSeconds);
            _project.SubtitleLayers.Add(layer);
        }

        SyncSubtitleLayers(_project);
        UpdatePreviewOverlays();
        ProjectStatusText.Text = $"Imported {importable.Count} subtitle blocks";
        QueueProjectChange();
    }

    public async Task ExportSubtitlesToSrtAsync(string filePath)
    {
        if (_project is null)
            return;

        var cues = _project.SubtitleLayers
            .Select(layer => new SubtitleCue(
                TimeSpan.FromSeconds(layer.StartSeconds),
                TimeSpan.FromSeconds(layer.EndSeconds),
                layer.Text))
            .ToList();
        if (LayerTimeline.SubtitleEnabled && !string.IsNullOrWhiteSpace(SubtitleTextBox.Text))
        {
            cues.Add(new SubtitleCue(
                LayerTimeline.SubtitleStart,
                LayerTimeline.SubtitleEnd,
                SubtitleTextBox.Text.Trim()));
        }

        if (cues.Count == 0)
            throw new InvalidOperationException("The project does not contain any subtitle blocks.");

        await File.WriteAllTextAsync(filePath, SrtSubtitleService.Serialize(cues));
        ProjectStatusText.Text = $"Exported {cues.Count} subtitle blocks";
    }

    public async Task ImportAudioTrackAsync(string filePath)
    {
        if (_project is null || !File.Exists(filePath))
            return;

        var duration = await _mediaProbeService.GetDurationAsync(filePath);
        if (duration is null || duration <= TimeSpan.FromSeconds(0.1))
            throw new InvalidOperationException("The selected audio file could not be read.");

        CommitPendingProjectChange();
        var track = new EditorProjectAudioTrack
        {
            Name = Path.GetFileNameWithoutExtension(filePath),
            SourcePath = filePath,
            SourceDurationSeconds = duration.Value.TotalSeconds,
            TrimEndSeconds = duration.Value.TotalSeconds,
            TimelineStartSeconds = CurrentTimelinePosition.TotalSeconds
        };
        track.Normalize();
        _project.AudioTracks.Add(track);
        _selectedAudioTrackId = track.Id;
        SyncAudioTracks(_project);
        LayerTimeline.SelectAudioTrack(track.Id);
        ProjectStatusText.Text = "Audio track added";
        QueueProjectChange();
    }

    private static void ReplaceClipSource(EditorProjectClip clip, string replacementPath, double durationSeconds)
    {
        var previousDuration = clip.SourceDurationSeconds;
        clip.SourcePath = replacementPath;
        clip.Name = Path.GetFileNameWithoutExtension(replacementPath);
        clip.SourceDurationSeconds = durationSeconds;
        if (clip.TrimEndSeconds >= previousDuration - 0.05 || clip.TrimEndSeconds > durationSeconds)
            clip.TrimEndSeconds = durationSeconds;
        clip.Normalize();
    }

    private static void ReplaceAudioTrackSource(EditorProjectAudioTrack track, string replacementPath, double durationSeconds)
    {
        var previousDuration = track.SourceDurationSeconds;
        track.SourcePath = replacementPath;
        track.Name = Path.GetFileNameWithoutExtension(replacementPath);
        track.SourceDurationSeconds = durationSeconds;
        if (track.TrimEndSeconds >= previousDuration - 0.05 || track.TrimEndSeconds > durationSeconds)
            track.TrimEndSeconds = durationSeconds;
        track.Normalize();
    }

    private void LoadNativePreview(string filePath)
    {
        try
        {
            EnsurePrimarySeekEvents();
            _primarySourceVersion++;
            _primaryDecoderSeek.Reset();
            ClearClipEffectPreview(PrimaryEffectPreviewHost);
            _primaryEffectPreviewSignature = null;
            var previewPath = ResolvePreviewMediaPath(filePath);
            _isPreviewOpening = true;
            _primaryOpeningAt = PreviewNowSeconds;
            _usingNativePreview = false;
            NativePreviewPlayer.MediaPlayer.Pause();
            _nativePreviewMediaPath = previewPath;
            NativePreviewPlayer.MediaPlayer.IsVideoFrameServerEnabled = true;
            NativePreviewPlayer.MediaPlayer.Source = MediaSource.CreateFromUri(new Uri(previewPath));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Klipp] Native preview kunde inte öppnas, faller tillbaka på thumbnail: {ex.Message}");
            _nativePreviewMediaPath = null;
            _isPreviewOpening = false;
            _usingNativePreview = false;
        }
    }

    private string ResolvePreviewMediaPath(string sourcePath)
    {
        if (!_temporarilyUseOriginalPreview &&
            PreviewProxyQuality.Normalize(_previewQuality) != "original" &&
            _previewProxyService.TryGetExistingProxy(sourcePath, _previewQuality, out var proxyPath))
        {
            return proxyPath;
        }

        return sourcePath;
    }

    public void Stop()
    {
        EndTimelineFxPreviewGesture(commit: true);
        CancelLookThumbnailLoading();
        DismissTransitionAudition();
        StopSourceViewer();
        _motionFrameCts?.Cancel();
        _motionTrackingCts?.Cancel();
        _videoProcessingCts?.Cancel();
        _projectCollectionCts?.Cancel();
        CommitPendingProjectChange();
        _ = SaveProjectAsync();
        _thumbnailLoadVersion++;
        _libraryThumbnailLoadVersion++;
        _projectLoadVersion++;
        ResetAudioSignalAnalyses();
        _audioSignalAnalysisCts?.Dispose();
        _audioSignalAnalysisCts = null;
        _missingMediaSearchCts?.Cancel();
        _missingMediaSearchCts?.Dispose();
        _missingMediaSearchCts = null;
        _audioTrackWaveformCts?.Cancel();
        _audioTrackWaveformCts?.Dispose();
        _audioTrackWaveformCts = null;
        _previewProxyCts?.Cancel();
        _previewProxyCts = null;
        _isBuildingPreviewProxies = false;
        _previewProxyBuildQueued = false;
        _transcriptionCts?.Cancel();
        _transcriptionCts?.Dispose();
        _transcriptionCts = null;
        PausePreview();
        ClearPreparedPreviewCut();
        DisposeAdditionalVideoTrackPreviews();

        try
        {
            NativePreviewPlayer.MediaPlayer.Source = null;
            TransitionPreviewPlayer.Source = null;
            OverlayPreviewPlayer.Source = null;
        }
        catch
        {
            // MediaPlayerElement kan redan vara på väg bort när unload triggas.
        }

        foreach (var state in _audioTrackPreviewPlayers.Values)
            state.Dispose();
        _audioTrackPreviewPlayers.Clear();

        _usingNativePreview = false;
        _isPreviewOpening = false;
        ClearPreviewVisualCaches();

        try
        {
            PreviewImage.Source = null;
            PreviewImage.Opacity = 0;
            PreviewFallback.Visibility = Visibility.Visible;
        }
        catch
        {
            // Vyn kan redan vara på väg bort när unload triggas.
        }

        try
        {
            LayerTimeline.SetAudioWaveform(null);
        }
        catch
        {
            // Vyn kan redan vara på väg bort när unload triggas.
        }

        _filePath = null;
        _nativePreviewMediaPath = null;
        _activePreviewClipId = null;
        _activeTransitionPreviewClipId = null;
        _activeOverlayPreviewClipId = null;
        _transitionPreviewSourcePath = null;
        _overlayPreviewSourcePath = null;
    }

    public void SetLibraryClips(IEnumerable<ClipViewModel> clips)
    {
        _libraryThumbnailLoadVersion++;
        LibraryItems.Clear();
        foreach (var clip in clips.Where(item => File.Exists(item.FilePath)))
        {
            var duration = TryParseDuration(clip.Duration);
            if (duration is { } parsedDuration && parsedDuration > TimeSpan.FromSeconds(0.1))
                _libraryDurationCache[clip.FilePath] = parsedDuration;

            LibraryItems.Add(new EditorLibraryItem
            {
                FilePath = clip.FilePath,
                Title = clip.Title,
                ThumbnailPath = clip.ThumbnailPath,
                DurationLabel = string.IsNullOrWhiteSpace(clip.Duration) ? "Klipp" : clip.Duration,
                Duration = duration,
                LastWriteTime = clip.LastWriteTime,
                FileSizeBytes = clip.FileSizeBytes,
                IsFavorite = clip.IsFavorite,
                TagsText = clip.TagsText,
                Collection = clip.Collection
            });
        }

        RefreshEditorLibraryCollectionOptions();
        ApplyEditorLibraryFilter();
    }

    public void SetExportJobs(IEnumerable<EditorExportJob> jobs)
    {
        var snapshots = jobs
            .OrderByDescending(job => job.CreatedUtc)
            .Select(EditorExportQueueItem.FromJob)
            .ToList();

        ExportQueueItems.Clear();
        foreach (var item in snapshots)
            ExportQueueItems.Add(item);

        if (ExportQueueCountText is not null)
            ExportQueueCountText.Text = snapshots.Count == 1 ? "1 job" : $"{snapshots.Count} jobs";
        if (ExportQueueEmptyText is not null)
            ExportQueueEmptyText.Visibility = snapshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ExportQueueList is not null)
            ExportQueueList.Visibility = snapshots.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    public void SetMediaImportStatus(string status)
    {
        if (ProjectStatusText is not null)
            ProjectStatusText.Text = status;
    }

    private void OnEditorLibrarySearchTextChanged(object sender, TextChangedEventArgs e) =>
        ApplyEditorLibraryFilter();

    private void OnEditorLibraryFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        _editorLibraryScope = (EditorLibraryScopeSelector?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
        _editorLibrarySort = (EditorLibrarySortSelector?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "newest";
        _editorLibraryCollection = EditorLibraryCollectionSelector?.SelectedItem?.ToString() ?? "All collections";
        if (string.Equals(_editorLibraryCollection, "All collections", StringComparison.OrdinalIgnoreCase))
            _editorLibraryCollection = string.Empty;
        ApplyEditorLibraryFilter();
    }

    private void OnImportProjectMediaClicked(object sender, RoutedEventArgs e) =>
        LibraryImportRequested?.Invoke(this, EventArgs.Empty);

    private void ApplyEditorLibraryFilter()
    {
        var query = EditorLibrarySearchBox?.Text?.Trim() ?? string.Empty;
        IEnumerable<EditorLibraryItem> filtered = LibraryItems;
        if (string.Equals(_editorLibraryScope, "favorites", StringComparison.OrdinalIgnoreCase))
            filtered = filtered.Where(item => item.IsFavorite);
        if (!string.IsNullOrWhiteSpace(_editorLibraryCollection))
        {
            filtered = filtered.Where(item => string.Equals(
                item.Collection,
                _editorLibraryCollection,
                StringComparison.CurrentCultureIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(item =>
                item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.FilePath.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.TagsText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Collection.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        filtered = _editorLibrarySort switch
        {
            "oldest" => filtered.OrderBy(item => item.LastWriteTime),
            "name" => filtered.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase),
            "favorites" => filtered
                .OrderByDescending(item => item.IsFavorite)
                .ThenByDescending(item => item.LastWriteTime),
            _ => filtered.OrderByDescending(item => item.LastWriteTime)
        };

        FilteredLibraryItems.Clear();
        foreach (var item in filtered)
            FilteredLibraryItems.Add(item);

        foreach (var item in FilteredLibraryItems.Take(8))
            QueueEditorLibraryThumbnailLoad(item);

        if (LibraryMediaCountText is not null)
        {
            var hasFilter = !string.IsNullOrWhiteSpace(query) ||
                            !string.Equals(_editorLibraryScope, "all", StringComparison.OrdinalIgnoreCase) ||
                            !string.IsNullOrWhiteSpace(_editorLibraryCollection);
            LibraryMediaCountText.Text = !hasFilter
                ? $"{LibraryItems.Count} clips"
                : $"{FilteredLibraryItems.Count}/{LibraryItems.Count}";
        }
    }

    private void RefreshEditorLibraryCollectionOptions()
    {
        var previous = _editorLibraryCollection;
        LibraryCollectionOptions.Clear();
        LibraryCollectionOptions.Add("All collections");
        foreach (var collection in LibraryItems
                     .Select(item => item.Collection)
                     .Where(collection => !string.IsNullOrWhiteSpace(collection))
                     .Distinct(StringComparer.CurrentCultureIgnoreCase)
                     .OrderBy(collection => collection, StringComparer.CurrentCultureIgnoreCase))
        {
            LibraryCollectionOptions.Add(collection);
        }

        _editorLibraryCollection = LibraryCollectionOptions.FirstOrDefault(collection => string.Equals(
            collection,
            previous,
            StringComparison.CurrentCultureIgnoreCase)) ?? string.Empty;
        if (EditorLibraryCollectionSelector is not null)
        {
            EditorLibraryCollectionSelector.SelectedItem = string.IsNullOrWhiteSpace(_editorLibraryCollection)
                ? LibraryCollectionOptions[0]
                : _editorLibraryCollection;
        }
    }

    private void SyncExtraTextLayers(EditorProject project)
    {
        ExtraTextLayers.Clear();
        foreach (var layer in project.ExtraTextLayers)
            ExtraTextLayers.Add(layer);

        UpdateExtraTextLayerChrome();
    }

    private void SyncSubtitleLayers(EditorProject project)
    {
        _editingSubtitleLayerId = null;
        SubtitleLayers.Clear();
        foreach (var layer in project.SubtitleLayers)
            SubtitleLayers.Add(layer);

        LayerTimeline.SetSubtitleSegments(project.SubtitleLayers);
        if (SaveSubtitleLayerButtonText is not null)
            SaveSubtitleLayerButtonText.Text = "Add subtitle block";
        UpdateSubtitleLayerChrome();
    }

    private void SyncGraphicLayers(EditorProject project)
    {
        GraphicLayers.Clear();
        foreach (var layer in project.GraphicLayers)
            GraphicLayers.Add(layer);

        if (!project.GraphicLayers.Any(layer => string.Equals(layer.Id, _selectedGraphicLayerId, StringComparison.Ordinal)))
            _selectedGraphicLayerId = null;

        GraphicLayerList.SelectedItem = GetSelectedGraphicLayer();
        UpdateGraphicLayerChrome();
        SyncSelectedGraphicControls();
        SyncGraphicTimelineRange();
    }

    private void UpdateExtraTextLayerChrome()
    {
        if (ExtraTextLayerCountText is not null)
            ExtraTextLayerCountText.Text = ExtraTextLayers.Count.ToString();
    }

    private void UpdateSubtitleLayerChrome()
    {
        if (SubtitleLayerCountText is not null)
            SubtitleLayerCountText.Text = SubtitleLayers.Count.ToString();
    }

    private void UpdateGraphicLayerChrome()
    {
        if (GraphicLayerCountText is not null)
            GraphicLayerCountText.Text = GraphicLayers.Count.ToString();
        if (GraphicLayerList is not null)
            GraphicLayerList.Visibility = GraphicLayers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (GraphicLayerEmptyState is not null)
            GraphicLayerEmptyState.Visibility = GraphicLayers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private EditorProjectGraphicLayer? GetSelectedGraphicLayer() =>
        _project?.GraphicLayers.FirstOrDefault(layer =>
            string.Equals(layer.Id, _selectedGraphicLayerId, StringComparison.Ordinal));

    private EditorProjectGraphicKeyframe? FindGraphicKeyframeAtPlayhead(
        EditorProjectGraphicLayer layer,
        double toleranceSeconds = 0.045)
    {
        var time = CurrentTimelinePosition.TotalSeconds;
        return layer.Keyframes
            .OrderBy(keyframe => Math.Abs(keyframe.TimeSeconds - time))
            .FirstOrDefault(keyframe => Math.Abs(keyframe.TimeSeconds - time) <= toleranceSeconds);
    }

    private EditorProjectGraphicKeyframe? GetOrCreateGraphicKeyframeForEdit(EditorProjectGraphicLayer layer)
    {
        if (_project is null || layer.Keyframes.Count == 0)
            return null;

        var existing = FindGraphicKeyframeAtPlayhead(layer);
        if (existing is not null)
            return existing;

        var time = Math.Clamp(CurrentTimelinePosition.TotalSeconds, layer.StartSeconds, layer.EndSeconds);
        var state = layer.Evaluate(time);
        var keyframe = new EditorProjectGraphicKeyframe
        {
            TimeSeconds = time,
            AnchorX = state.AnchorX,
            AnchorY = state.AnchorY,
            Width = state.Width,
            Height = state.Height,
            Opacity = state.Opacity,
            Easing = "linear"
        };
        keyframe.Normalize(_project.DurationSeconds, _project.CanvasWidth, _project.CanvasHeight);
        layer.Keyframes.Add(keyframe);
        layer.Keyframes = layer.Keyframes.OrderBy(item => item.TimeSeconds).ToList();
        SyncGraphicTimelineRange();
        return keyframe;
    }

    private void SyncGraphicTimelineRange()
    {
        if (_project is null)
            return;

        LayerTimeline.SetGraphicSegments(_project.GraphicLayers, _selectedGraphicLayerId);
    }

    private void SyncSelectedGraphicControls()
    {
        if (SelectedGraphicLayerCard is null || GraphicXBox is null || GraphicOpacitySlider is null)
            return;

        var layer = GetSelectedGraphicLayer();
        var enabled = layer is not null;
        var canvasWidth = _project?.CanvasWidth ?? (int)CanvasWidth;
        var canvasHeight = _project?.CanvasHeight ?? (int)CanvasHeight;
        var state = layer?.Evaluate(CurrentTimelinePosition.TotalSeconds) ??
                    new EditorProjectGraphicState(0.5, 0.5, 320, 180, 1);
        var currentKeyframe = layer is null ? null : FindGraphicKeyframeAtPlayhead(layer);
        _syncingGraphicControls = true;
        try
        {
            SelectedGraphicLayerCard.Opacity = enabled ? 1 : 0.55;
            SelectedGraphicNameText.Text = layer?.Name ?? "Select a layer";
            GraphicXBox.IsEnabled = enabled;
            GraphicYBox.IsEnabled = enabled;
            GraphicWidthBox.IsEnabled = enabled;
            GraphicHeightBox.IsEnabled = enabled;
            GraphicStartBox.IsEnabled = enabled;
            GraphicEndBox.IsEnabled = enabled;
            GraphicOpacitySlider.IsEnabled = enabled;
            GraphicBlendModeComboBox.IsEnabled = enabled;
            GraphicMaskShapeComboBox.IsEnabled = enabled;
            GraphicAddKeyframeButton.IsEnabled = enabled;
            GraphicPreviousKeyframeButton.IsEnabled = enabled && layer!.Keyframes.Count > 0;
            GraphicNextKeyframeButton.IsEnabled = enabled && layer!.Keyframes.Count > 0;
            GraphicDeleteKeyframeButton.IsEnabled = currentKeyframe is not null;
            GraphicKeyframeEasingComboBox.IsEnabled = currentKeyframe is not null;

            GraphicXBox.Minimum = 0;
            GraphicXBox.Maximum = canvasWidth;
            GraphicYBox.Minimum = 0;
            GraphicYBox.Maximum = canvasHeight;
            GraphicWidthBox.Maximum = canvasWidth * 2;
            GraphicHeightBox.Maximum = canvasHeight * 2;
            GraphicStartBox.Maximum = Math.Max(0, _duration.TotalSeconds - 0.1);
            GraphicEndBox.Maximum = Math.Max(0.1, _duration.TotalSeconds);

            GraphicXBox.Value = layer is null ? double.NaN : Math.Round(state.AnchorX * canvasWidth);
            GraphicYBox.Value = layer is null ? double.NaN : Math.Round(state.AnchorY * canvasHeight);
            GraphicWidthBox.Value = layer is null ? double.NaN : Math.Round(state.Width);
            GraphicHeightBox.Value = layer is null ? double.NaN : Math.Round(state.Height);
            GraphicStartBox.Value = layer is null ? double.NaN : layer.StartSeconds;
            GraphicEndBox.Value = layer is null ? double.NaN : layer.EndSeconds;
            GraphicOpacitySlider.Value = state.Opacity * 100;
            GraphicOpacityValueText.Text = $"{Math.Round(state.Opacity * 100):0}%";
            GraphicBlendModeComboBox.SelectedItem = GraphicBlendModeComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), layer?.BlendMode ?? "normal", StringComparison.Ordinal));
            GraphicMaskShapeComboBox.SelectedItem = GraphicMaskShapeComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), layer?.MaskShape ?? "none", StringComparison.Ordinal));
            var roundedMask = enabled && string.Equals(layer?.MaskShape, "rounded", StringComparison.Ordinal);
            GraphicMaskRoundnessSlider.IsEnabled = roundedMask;
            GraphicMaskRoundnessSlider.Opacity = roundedMask ? 1 : 0.45;
            GraphicMaskRoundnessSlider.Value = (layer?.MaskRoundness ?? 0.2) * 100;
            GraphicMaskRoundnessValueText.Opacity = roundedMask ? 1 : 0.45;
            GraphicMaskRoundnessValueText.Text = $"{Math.Round((layer?.MaskRoundness ?? 0.2) * 100):0}%";
            GraphicKeyframeCountText.Text = (layer?.Keyframes.Count ?? 0).ToString();
            GraphicAddKeyframeButtonText.Text = currentKeyframe is null ? "Add keyframe" : "Update keyframe";
            GraphicKeyframeStatusText.Text = layer is null
                ? "Select a graphic layer."
                : currentKeyframe is not null
                    ? $"Keyframe at {FormatTimestamp(TimeSpan.FromSeconds(currentKeyframe.TimeSeconds))}"
                    : layer.Keyframes.Count > 0
                        ? "Values are interpolated here. Editing creates a keyframe."
                        : "Add a keyframe at the playhead to animate this layer.";
            GraphicKeyframeEasingComboBox.SelectedItem = GraphicKeyframeEasingComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(),
                    currentKeyframe?.Easing ?? "linear",
                    StringComparison.Ordinal));
        }
        finally
        {
            _syncingGraphicControls = false;
        }
    }

    private void SyncAudioTracks(EditorProject project)
    {
        PruneAudioDuckingStates(project);
        _duration = TimeSpan.FromSeconds(project.DurationSeconds);
        LayerTimeline.SetTracks(project.Tracks);
        for (var index = 0; index < project.AudioTracks.Count; index++)
        {
            var track = project.AudioTracks[index];
            if (index >= AudioTracks.Count) AudioTracks.Add(track);
            else if (!ReferenceEquals(AudioTracks[index], track)) AudioTracks[index] = track;
        }
        while (AudioTracks.Count > project.AudioTracks.Count) AudioTracks.RemoveAt(AudioTracks.Count - 1);

        if (!project.AudioTracks.Any(track => string.Equals(track.Id, _selectedAudioTrackId, StringComparison.Ordinal)))
            _selectedAudioTrackId = null;
        LayerTimeline.SetAudioTrackSegments(project.AudioTracks);
        SyncAudioTrackPreviewPlayers(project.AudioTracks);
        QueueAudioTrackWaveforms(project.AudioTracks);
        QueueAudioSignalAnalyses(project.AudioTracks.Select(track => (track.SourcePath, track.SourceAudioStreamIndex)));
        SyncSelectedAudioTrackControls();
    }

    private void ResetAudioSignalAnalyses()
    {
        _duckingPreviewStates.Clear();
        _audioSignalAnalysisCts?.Cancel();
        _audioSignalAnalysisCts?.Dispose();
        _audioSignalAnalysisCts = new CancellationTokenSource();
        _audioSignalAnalyses.Clear();
        _audioSignalAnalysisInFlight.Clear();
        _audioMeterPeakHold = 0;
        UpdateAudioMeter(0, 0, hasSignalData: false);
    }

    private void QueueAudioSignalAnalyses(IEnumerable<string?> sourcePaths)
        => QueueAudioSignalAnalyses(sourcePaths.Where(path => path is not null).Select(path => (path!, 0)));

    private void QueueAudioSignalAnalyses(IEnumerable<(string Path, int Stream)> sources)
    {
        _audioSignalAnalysisCts ??= new CancellationTokenSource();
        foreach (var source in sources.Where(source => !string.IsNullOrWhiteSpace(source.Path) && File.Exists(source.Path)))
        {
            var sourcePath = Path.GetFullPath(source.Path);
            var key = AudioSignalKey(sourcePath, source.Stream);
            if (_audioSignalAnalyses.ContainsKey(key) ||
                !_audioSignalAnalysisInFlight.Add(key))
            {
                continue;
            }

            _ = AnalyzeAudioSignalAsync(sourcePath, source.Stream, _audioSignalAnalysisCts.Token);
        }
    }

    private static string AudioSignalKey(string path, int stream) => $"{path}|audio:{stream}";

    private async Task AnalyzeAudioSignalAsync(string sourcePath, int audioStreamIndex, CancellationToken cancellationToken)
    {
        var key = AudioSignalKey(sourcePath, audioStreamIndex);
        try
        {
            var analysis = await _audioSignalAnalysisService.AnalyzeAsync(
                    sourcePath,
                    cancellationToken: cancellationToken, audioStreamIndex: audioStreamIndex)
                .ConfigureAwait(false);
            DispatcherQueue.TryEnqueue(() =>
            {
                _audioSignalAnalysisInFlight.Remove(key);
                if (analysis is not null)
                    _audioSignalAnalyses[key] = analysis;
                UpdateAudioMeters();
            });
        }
        catch (OperationCanceledException)
        {
            DispatcherQueue.TryEnqueue(() => _audioSignalAnalysisInFlight.Remove(key));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Klipp] Audio signal analysis failed for {sourcePath}: {ex.Message}");
            DispatcherQueue.TryEnqueue(() => _audioSignalAnalysisInFlight.Remove(key));
        }
    }

    private void SyncAudioTrackPreviewPlayers(IEnumerable<EditorProjectAudioTrack> tracks)
    {
        RebuildAudioPreviewModelIndex(tracks);
        UpdateAudioTrackPreview(_previewPosition, seekExisting: false);
    }

    private void UpdateAudioTrackPreview(TimeSpan position, bool seekExisting)
    {
        if (_project is null)
            return;
        var modelsChanged = EnsureAudioPreviewModelIndex();
        ReconcileAudioPreviewPool(position, immediate: seekExisting || modelsChanged);
        var masterMuted = MuteAudioToggle?.IsOn ?? false;
        var shouldPlay = ShouldPlayAudioPreview();
        foreach (var state in _audioTrackPreviewPlayers.Values)
        {
            if (!_audioPreviewModels.TryGetValue(state.TrackId, out var track))
            {
                state.PauseIfPlaying();
                continue;
            }
            UpdateAudioPreviewState(state, track, position, seekExisting, shouldPlay, masterMuted);
        }
    }

    private List<(string Id, string SourcePath, int SourceAudioStreamIndex, TimeSpan Start, TimeSpan End, long Length, long Modified)>? _lastAudioWaveformRequests;
    private int _audioWaveformRequestProjectVersion = -1;

    private static (long Length, long Modified) ReadAudioWaveformSourceStamp(string path)
    {
        try { var file = new FileInfo(path); return (file.Length, file.LastWriteTimeUtc.Ticks); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (0, 0); }
    }

    private void QueueAudioTrackWaveforms(IEnumerable<EditorProjectAudioTrack> tracks)
    {
        var requests = tracks
            .Where(track => File.Exists(track.SourcePath))
            .Select(track =>
            {
                var stamp = ReadAudioWaveformSourceStamp(track.SourcePath);
                return (track.Id, track.SourcePath, track.SourceAudioStreamIndex,
                    Start: TimeSpan.FromSeconds(track.TrimStartSeconds), End: TimeSpan.FromSeconds(track.TrimEndSeconds),
                    stamp.Length, stamp.Modified);
            })
            .ToList();
        // Gain, mute, fades and track movement do not change source waveforms.
        // Keep the current work and images alive throughout a live adjustment.
        if (_audioWaveformRequestProjectVersion == _projectLoadVersion &&
            _lastAudioWaveformRequests?.SequenceEqual(requests) == true) return;
        _audioWaveformRequestProjectVersion = _projectLoadVersion;
        _lastAudioWaveformRequests = requests;
        _audioTrackWaveformCts?.Cancel();
        _audioTrackWaveformCts?.Dispose();
        var waveformCts = new CancellationTokenSource();
        _audioTrackWaveformCts = waveformCts;
        var waveformToken = waveformCts.Token;

        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                foreach (var request in requests)
                {
                    waveformToken.ThrowIfCancellationRequested();
                    var waveformPath = await _waveformService.GetOrCreateWaveformAsync(
                        request.SourcePath,
                        request.Start,
                        request.End,
                        BackgroundWorkPriority.Visible,
                        waveformToken, request.SourceAudioStreamIndex);
                    if (!ReferenceEquals(_audioTrackWaveformCts, waveformCts) ||
                        string.IsNullOrWhiteSpace(waveformPath) ||
                        !File.Exists(waveformPath))
                    {
                        continue;
                    }

                    LayerTimeline.SetAudioTrackWaveform(
                        request.Id,
                        new BitmapImage(new Uri(waveformPath)), request.SourcePath,
                        request.Start.TotalSeconds, request.End.TotalSeconds);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Audio waveform generation failed: {exception.Message}");
            }
            finally
            {
                if (ReferenceEquals(_audioTrackWaveformCts, waveformCts))
                {
                    _audioTrackWaveformCts = null;
                    waveformCts.Dispose();
                }
            }
        });
    }

    private async Task RefreshSavedProjectsAsync()
    {
        var projects = await _projectService.ListNamedProjectsAsync();
        SavedProjects.Clear();
        foreach (var project in projects)
            SavedProjects.Add(project);
    }

    private void OnEditorLibraryContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue)
            return;

        if (args.Item is EditorLibraryItem item)
            QueueEditorLibraryThumbnailLoad(item);
    }

    private void QueueEditorLibraryThumbnailLoad(EditorLibraryItem item)
    {
        if (item.ThumbnailLoadStarted || string.IsNullOrWhiteSpace(item.ThumbnailPath))
            return;

        item.ThumbnailLoadStarted = true;
        var version = _libraryThumbnailLoadVersion;

        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(30);

            if (version != _libraryThumbnailLoadVersion ||
                !LibraryItems.Contains(item) ||
                !File.Exists(item.ThumbnailPath))
            {
                return;
            }

            await EditorLibraryThumbnailDecodeGate.WaitAsync();
            try
            {
                if (version != _libraryThumbnailLoadVersion || !LibraryItems.Contains(item))
                    return;

                var bitmap = new BitmapImage { DecodePixelWidth = 164 };
                using var stream = new FileStream(
                    item.ThumbnailPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 32 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());

                if (version == _libraryThumbnailLoadVersion && LibraryItems.Contains(item))
                    item.ThumbnailSource = bitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Klipp] Editor library thumbnail load failed: {ex.Message}");
            }
            finally
            {
                EditorLibraryThumbnailDecodeGate.Release();
            }
        });
    }

    private void ApplyProjectSettings(EditorProjectSettings settings)
    {
        _syncingTimelineState = true;
        try
        {
            SetPlaybackSpeed(1);
            _textStyle = settings.TextStyle;
            _textAnimation = settings.TextAnimation;
            _textFontFamily = EditorTextTypography.NormalizeFontFamily(settings.TextFontFamily);
            _textFontFilePath = EditorTextTypography.NormalizeFontFilePath(settings.TextFontFilePath);
            _textFontFamilyName = EditorTextTypography.NormalizeCustomFontFamilyName(settings.TextFontFamilyName);
            _textColor = EditorTextTypography.NormalizeColor(settings.TextColor);
            _textAnchorX = settings.TextAnchorX;
            _textAnchorY = settings.TextAnchorY;
            _textSize = settings.TextSize;
            _textBoxWidth = settings.TextBoxWidth;
            _textOpacity = settings.TextOpacity;
            _textKeyframes = settings.TextKeyframes.Select(keyframe => keyframe.Clone()).ToList();
            _subtitleStyle = settings.SubtitleStyle;
            _subtitleFontFamily = EditorTextTypography.NormalizeFontFamily(settings.SubtitleFontFamily);
            _subtitleFontFilePath = EditorTextTypography.NormalizeFontFilePath(settings.SubtitleFontFilePath);
            _subtitleFontFamilyName = EditorTextTypography.NormalizeCustomFontFamilyName(settings.SubtitleFontFamilyName);
            _subtitleColor = EditorTextTypography.NormalizeColor(settings.SubtitleColor);
            _subtitleAnchorX = settings.SubtitleAnchorX;
            _subtitleAnchorY = settings.SubtitleAnchorY;
            _subtitleSize = settings.SubtitleSize;
            _subtitleBoxWidth = settings.SubtitleBoxWidth;
            _videoFilter = settings.VideoFilter;
            _elementOverlay = settings.ElementOverlay;
            _fadeInSeconds = settings.FadeInSeconds;
            _fadeOutSeconds = settings.FadeOutSeconds;
            _freezeFrameSeconds = settings.FreezeFrameSeconds;
            _clipTransitionStyle = settings.TransitionStyle;
            _clipTransitionSeconds = settings.TransitionDurationSeconds;
            _previewQuality = PreviewProxyQuality.Normalize(settings.PreviewQuality);
            _exportQuality = settings.ExportQuality;
            _exportFrameRate = settings.ExportFrameRate;
            LayerTimeline.FrameRate = _exportFrameRate;
            _exportEncoder = settings.ExportEncoder;
            _exportBitrateMbps = settings.ExportBitrateMbps;
            _exportResolutionMode = settings.ExportResolutionMode;
            _exportWidth = settings.ExportWidth;
            _exportHeight = settings.ExportHeight;
            _exportAspectLocked = settings.ExportAspectLocked;
            _showSafeAreaGuides = settings.ShowSafeAreaGuides;
            _exportLockedAspectRatio = Math.Max(0.01, _exportWidth / (double)Math.Max(1, _exportHeight));
            _exportAudioBitrateKbps = settings.ExportAudioBitrateKbps;
            LayerTimeline.SetPrimaryTransition(_clipTransitionStyle, _clipTransitionSeconds);
            _videoScale = settings.VideoScale;
            _videoOffsetX = settings.VideoOffsetX;
            _videoOffsetY = settings.VideoOffsetY;

            TextOverlayBox.Text = settings.Text;
            SubtitleTextBox.Text = settings.SubtitleText;
            EditorVolumeSlider.Value = Math.Clamp(settings.AudioVolume * 100, 0, 200);
            MuteAudioToggle.IsOn = settings.MuteAudio;
            SafeAreaGuidesToggle.IsOn = _showSafeAreaGuides;

            LayerTimeline.SetLayerRange(
                EditorTimelineLayer.Text,
                settings.TextEnabled && !string.IsNullOrWhiteSpace(settings.Text),
                TimeSpan.FromSeconds(settings.TextStartSeconds),
                TimeSpan.FromSeconds(settings.TextEndSeconds),
                GetLayerLabel(settings.Text, settings.TextStyle));
            _textTimelineRangeStart = LayerTimeline.TextStart;
            _textTimelineRangeEnd = LayerTimeline.TextEnd;
            LayerTimeline.SetTextKeyframes(_textKeyframes);
            LayerTimeline.SetLayerRange(
                EditorTimelineLayer.Subtitle,
                settings.SubtitleEnabled && !string.IsNullOrWhiteSpace(settings.SubtitleText),
                TimeSpan.FromSeconds(settings.SubtitleStartSeconds),
                TimeSpan.FromSeconds(settings.SubtitleEndSeconds),
                GetLayerLabel(settings.SubtitleText, settings.SubtitleStyle));
            LayerTimeline.SetLayerRange(
                EditorTimelineLayer.Vfx,
                settings.VfxEnabled && settings.VideoFilter != "none",
                TimeSpan.FromSeconds(settings.VfxStartSeconds),
                TimeSpan.FromSeconds(settings.VfxEndSeconds),
                GetVfxLabel(settings.VideoFilter));
            LayerTimeline.SetLayerRange(
                EditorTimelineLayer.Element,
                settings.ElementEnabled && settings.ElementOverlay != "none",
                TimeSpan.FromSeconds(settings.ElementStartSeconds),
                TimeSpan.FromSeconds(settings.ElementEndSeconds),
                GetElementLabel(settings.ElementOverlay));
            SyncGraphicTimelineRange();
        }
        finally
        {
            _syncingTimelineState = false;
        }

        SetTextSize(settings.TextSize, recordKeyframe: false);
        UpdateTextKeyframeControls();
        SetSubtitleSize(settings.SubtitleSize);
        UpdateVideoTransformPreview();
        UpdateSafeAreaGuides();
        UpdatePreviewOverlays();
        UpdateAllOptionButtons();
    }

    private void CaptureProjectFromUi()
    {
        if (_project is null)
            return;

        _project.Clips = LayerTimeline.GetMediaClips().ToList();
        _project.EffectLayers = LayerTimeline.GetEffectLayers().ToList();
        _project.Markers = LayerTimeline.GetMarkers().ToList();
        _project.Name = TitleText.Text;
        _project.Settings = new EditorProjectSettings
        {
            PreviewQuality = _previewQuality,
            ExportQuality = _exportQuality,
            ExportFrameRate = _exportFrameRate,
            ExportEncoder = _exportEncoder,
            ExportBitrateMbps = _exportBitrateMbps,
            ExportResolutionMode = _exportResolutionMode,
            ExportWidth = _exportWidth,
            ExportHeight = _exportHeight,
            ExportAspectLocked = _exportAspectLocked,
            ShowSafeAreaGuides = _showSafeAreaGuides,
            ExportAudioBitrateKbps = _exportAudioBitrateKbps,
            TransitionStyle = _clipTransitionStyle,
            TransitionDurationSeconds = _clipTransitionSeconds,
            Text = TextOverlayBox.Text,
            TextStyle = _textStyle,
            TextAnimation = _textAnimation,
            TextFontFamily = _textFontFamily,
            TextFontFilePath = _textFontFilePath,
            TextFontFamilyName = _textFontFamilyName,
            TextColor = _textColor,
            TextAnchorX = _textAnchorX,
            TextAnchorY = _textAnchorY,
            TextSize = _textSize,
            TextBoxWidth = _textBoxWidth,
            TextOpacity = _textOpacity,
            TextKeyframes = _textKeyframes.Select(keyframe => keyframe.Clone()).ToList(),
            TextEnabled = LayerTimeline.TextEnabled,
            TextStartSeconds = LayerTimeline.TextStart.TotalSeconds,
            TextEndSeconds = LayerTimeline.TextEnd.TotalSeconds,
            SubtitleText = SubtitleTextBox.Text,
            SubtitleStyle = _subtitleStyle,
            SubtitleFontFamily = _subtitleFontFamily,
            SubtitleFontFilePath = _subtitleFontFilePath,
            SubtitleFontFamilyName = _subtitleFontFamilyName,
            SubtitleColor = _subtitleColor,
            SubtitleAnchorX = _subtitleAnchorX,
            SubtitleAnchorY = _subtitleAnchorY,
            SubtitleSize = _subtitleSize,
            SubtitleBoxWidth = _subtitleBoxWidth,
            SubtitleEnabled = LayerTimeline.SubtitleEnabled,
            SubtitleStartSeconds = LayerTimeline.SubtitleStart.TotalSeconds,
            SubtitleEndSeconds = LayerTimeline.SubtitleEnd.TotalSeconds,
            VideoFilter = _videoFilter,
            VfxEnabled = LayerTimeline.VfxEnabled,
            VfxStartSeconds = LayerTimeline.VfxStart.TotalSeconds,
            VfxEndSeconds = LayerTimeline.VfxEnd.TotalSeconds,
            ElementOverlay = _elementOverlay,
            ElementEnabled = LayerTimeline.ElementEnabled,
            ElementStartSeconds = LayerTimeline.ElementStart.TotalSeconds,
            ElementEndSeconds = LayerTimeline.ElementEnd.TotalSeconds,
            FadeInSeconds = _fadeInSeconds,
            FadeOutSeconds = _fadeOutSeconds,
            FreezeFrameSeconds = _freezeFrameSeconds,
            AudioVolume = ExportAudioVolume,
            MuteAudio = MuteAudioToggle.IsOn,
            VideoScale = _videoScale,
            VideoOffsetX = _videoOffsetX,
            VideoOffsetY = _videoOffsetY
        };
        _project.Normalize();
        UpdateExportPresetSummary();
    }

    private void QueueProjectChange()
    {
        if (_suppressProjectChanges || _isLoadingClip || _project is null)
            return;

        CaptureProjectFromUi();
        _projectChangePending = true;
        ProjectStatusText.Text = "Saving changes...";
        _projectChangeTimer.Stop();
        _projectChangeTimer.Start();
    }

    private void CommitPendingProjectChange()
    {
        if (!_projectChangePending || _project is null || _projectHistoryBaseline is null)
            return;

        CaptureProjectFromUi();
        var before = JsonSerializer.Serialize(_projectHistoryBaseline);
        var after = JsonSerializer.Serialize(_project);
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            _projectUndoHistory.Add(EditorProjectService.Clone(_projectHistoryBaseline));
            if (_projectUndoHistory.Count > MaxProjectHistory)
                _projectUndoHistory.RemoveAt(0);
            _projectRedoHistory.Clear();
            _projectHistoryBaseline = EditorProjectService.Clone(_project);
        }

        _projectChangePending = false;
        UpdateProjectChrome();
    }

    private async void OnProjectChangeTimerTick(object? sender, object e)
    {
        _projectChangeTimer.Stop();
        CommitPendingProjectChange();
        await SaveProjectAsync();
    }

    private async Task SaveProjectAsync()
    {
        if (_project is null || string.IsNullOrWhiteSpace(_projectSourcePath))
            return;
        if (_projectSaveInProgress)
        {
            _projectSaveQueued = true;
            return;
        }
        _projectSaveInProgress = true;
        try
        {
            do
            {
                _projectSaveQueued = false;
                var document = _project;
                var sourcePath = _projectSourcePath;
                if (document is null || string.IsNullOrWhiteSpace(sourcePath))
                    break;
                var snapshot = EditorProjectService.Clone(document);
                try
                {
                    await _projectService.SaveAutosaveAsync(snapshot, sourcePath);
                    if (ReferenceEquals(_project, document) && string.Equals(_projectSourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                        ProjectStatusText.Text = $"Saved {DateTime.Now:HH:mm:ss}";
                }
                catch (Exception ex)
                {
                    if (ReferenceEquals(_project, document))
                        ProjectStatusText.Text = "Autosave failed";
                    Debug.WriteLine($"[Klipp] Autosave failed: {ex.Message}");
                }
            } while (_projectSaveQueued);
        }
        finally
        {
            _projectSaveInProgress = false;
        }
    }

    private void UndoProjectChange()
    {
        CommitPendingProjectChange();
        if (_project is null || _projectUndoHistory.Count == 0)
            return;

        _projectRedoHistory.Add(EditorProjectService.Clone(_project));
        var previous = _projectUndoHistory[^1];
        _projectUndoHistory.RemoveAt(_projectUndoHistory.Count - 1);
        RestoreProjectSnapshot(previous);
    }

    private void RedoProjectChange()
    {
        CommitPendingProjectChange();
        if (_project is null || _projectRedoHistory.Count == 0)
            return;

        _projectUndoHistory.Add(EditorProjectService.Clone(_project));
        var next = _projectRedoHistory[^1];
        _projectRedoHistory.RemoveAt(_projectRedoHistory.Count - 1);
        RestoreProjectSnapshot(next);
    }

    private void RestoreProjectSnapshot(EditorProject snapshot)
    {
        var selectedEffect = LayerTimeline.GetSelectedEffectLayer();
        var selectedEffectLayerId = selectedEffect?.Id;
        var selectedEffectTarget = selectedEffect is null ? null : LayerTimeline.FindMediaClipById(selectedEffect.TargetClipId);
        var selectedMediaClipId = LayerTimeline.SelectedMediaClipId;
        var selectedAudioTrackId = LayerTimeline.SelectedAudioTrackId;
        var selectedMediaClipIds = LayerTimeline.SelectedMediaClipIds.ToArray();
        var selectedAudioTrackIds = LayerTimeline.SelectedAudioTrackIds.ToArray();
        var selectedVideos = selectedMediaClipIds.Select(LayerTimeline.FindMediaClipById)
            .Where(clip => clip is not null).Select(clip => HistoryClipSelection.FromVideo(clip!)).ToArray();
        var selectedAudio = LayerTimeline.GetAudioTrackSegments().Where(clip => selectedAudioTrackIds.Contains(clip.Id))
            .Select(HistoryClipSelection.FromAudio).ToArray();
        _suppressProjectChanges = true;
        try
        {
            _project = EditorProjectService.Clone(snapshot);
            SyncExtraTextLayers(_project);
            SyncSubtitleLayers(_project);
            SyncGraphicLayers(_project);
            LayerTimeline.SetTracks(_project.Tracks);
            SyncAudioTracks(_project);
            LayerTimeline.SetMediaClips(_project.Clips, selectedMediaClipId);
            LayerTimeline.SetEffectLayers(_project.EffectLayers, selectedEffectLayerId);
            LayerTimeline.SetMarkers(_project.Markers);
            QueueAudioSignalAnalyses(_project.Clips.Select(clip => clip.SourcePath));
            _duration = TimeSpan.FromSeconds(_project.DurationSeconds);
            LayerTimeline.Duration = _duration;
            ApplyProjectSettings(_project.Settings);
            var restoredVideos = _project.Clips.Select(HistoryClipSelection.FromVideo).ToArray();
            var restoredAudio = _project.AudioTracks.Select(HistoryClipSelection.FromAudio).ToArray();
            selectedEffectLayerId = ResolveHistoryEffectSelection(selectedEffect, _project.EffectLayers,
                selectedEffectTarget is null ? null : ResolveHistoryClipSelection(HistoryClipSelection.FromVideo(selectedEffectTarget), restoredVideos));
            LayerTimeline.RestoreClipSelection(
                selectedVideos.Select(clip => ResolveHistoryClipSelection(clip, restoredVideos)).OfType<string>(),
                selectedAudio.Select(clip => ResolveHistoryClipSelection(clip, restoredAudio)).OfType<string>(),
                ResolveHistoryClipSelection(selectedVideos.FirstOrDefault(clip => clip.Id == selectedMediaClipId), restoredVideos),
                ResolveHistoryClipSelection(selectedAudio.FirstOrDefault(clip => clip.Id == selectedAudioTrackId), restoredAudio));
            if (selectedEffectLayerId is not null)
                LayerTimeline.SelectEffectLayerForEditing(selectedEffectLayerId);
            _selectedAudioTrackId = LayerTimeline.SelectedAudioTrackId;
            _projectHistoryBaseline = EditorProjectService.Clone(_project);
            RefreshPreviewAfterTimelineEdit();
        }
        finally
        {
            _suppressProjectChanges = false;
        }

        SyncPreviewSourceAfterProjectRestore();
        _projectChangePending = false;
        UpdateProjectChrome();
        QueueProjectSaveOnly();
    }

    private void SyncPreviewSourceAfterProjectRestore()
    {
        if (_project is null)
            return;
        RefreshPreviewAfterTimelineEdit();
    }

    private void QueueProjectSaveOnly()
    {
        _projectChangeTimer.Stop();
        _projectChangeTimer.Start();
    }

    private void UpdateProjectChrome()
    {
        ProjectClipCountText.Text = _project is null
            ? "0 clips"
            : $"{_project.Clips.Count} clip{(_project.Clips.Count == 1 ? string.Empty : "s")}";
        if (LibraryMediaCountText is not null && string.IsNullOrWhiteSpace(EditorLibrarySearchBox?.Text))
            LibraryMediaCountText.Text = $"{LibraryItems.Count} clips";
        RefreshMissingMediaItems();
        UpdateExtraTextLayerChrome();
        UpdateSubtitleLayerChrome();
        UpdateGraphicLayerChrome();
        SyncSelectedClipAudioControls();
        SyncSelectedAudioTrackControls();
        SyncSelectedClipVisualControls();
        SyncMarkerControls();
        RefreshPreviewProxyControls();
        UpdateTransitionButtons();
        SyncEffectStackControls();
        SyncTimelineFxInspector();
        SyncVideoProcessingControls();
        SyncMotionToolsControls();
        UndoTimelineButton.IsEnabled = _projectUndoHistory.Count > 0 || _projectChangePending;
        RedoTimelineButton.IsEnabled = _projectRedoHistory.Count > 0;
        SplitClipButton.IsEnabled = _project is not null &&
            (_project.Clips.Count > 0 || _project.AudioTracks.Count > 0);
        DetachAudioButton.IsEnabled = GetSelectedVideoSourceAudio() is not null;
        var graphicSelected = LayerTimeline.ActiveLayer == EditorTimelineLayer.Element &&
                              GetSelectedGraphicLayer() is not null;
        DuplicateClipButton.IsEnabled = graphicSelected || LayerTimeline.SelectedClipCount > 0;
        DeleteClipButton.IsEnabled = LayerTimeline.SelectedAudioTrackId is not null || graphicSelected ||
                                     LayerTimeline.SelectedClipCount > 0 || LayerTimeline.SelectedEffectLayerId is not null;
    }

    private void SyncMarkerControls()
    {
        if (MarkerListPanel is null || SelectedMarkerEditor is null)
            return;

        var selected = LayerTimeline.GetSelectedMarker();
        _syncingMarkerControls = true;
        try
        {
            RefreshMarkerList();
            SelectedMarkerEditor.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
            SelectedMarkerLabelBox.Text = selected?.Label ?? string.Empty;
            SelectedMarkerNotesBox.Text = selected?.Notes ?? string.Empty;
            UpdateColorSwatchButtons(
                selected?.ColorLabel ?? "yellow",
                MarkerColorNoneButton,
                MarkerColorRedButton,
                MarkerColorOrangeButton,
                MarkerColorYellowButton,
                MarkerColorGreenButton,
                MarkerColorTealButton,
                MarkerColorBlueButton,
                MarkerColorPurpleButton);
        }
        finally
        {
            _syncingMarkerControls = false;
        }
    }

    private void RefreshMarkerList()
    {
        var markers = LayerTimeline.GetMarkers()
            .OrderBy(marker => marker.TimeSeconds)
            .ToList();
        var manualMarkers = markers
            .Where(marker => !string.Equals(marker.Kind, "beat", StringComparison.Ordinal))
            .ToList();
        var beatCount = markers.Count - manualMarkers.Count;
        var selectedId = LayerTimeline.SelectedMarkerId;
        MarkerCountText.Text = beatCount > 0
            ? $"{manualMarkers.Count} markers · {beatCount} beats"
            : $"{manualMarkers.Count} marker{(manualMarkers.Count == 1 ? string.Empty : "s")}";
        MarkerEmptyText.Text = beatCount > 0
            ? $"{beatCount} beat markers in timeline"
            : "No markers yet";
        MarkerEmptyText.Visibility = manualMarkers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MarkerListPanel.Children.Clear();
        foreach (var marker in manualMarkers)
            MarkerListPanel.Children.Add(CreateMarkerListButton(marker, marker.Id == selectedId));
    }

    private Button CreateMarkerListButton(EditorProjectMarker marker, bool selected)
    {
        var swatch = new Border
        {
            Width = 10,
            Height = 22,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(GetColorLabelColor(marker.ColorLabel))
        };
        var label = new TextBlock
        {
            Text = marker.Label,
            Foreground = ResourceBrush("KlippTextPrimaryBrush"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var time = new TextBlock
        {
            Text = marker.TimeLabel,
            Foreground = ResourceBrush("KlippTextMutedBrush"),
            FontFamily = new FontFamily(Application.Current.Resources["KlippFontFamilyMono"].ToString() ?? "Consolas"),
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var content = new Grid { ColumnSpacing = 9 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(label, 1);
        Grid.SetColumn(time, 2);
        content.Children.Add(swatch);
        content.Children.Add(label);
        content.Children.Add(time);

        var button = new Button
        {
            Tag = marker.Id,
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 6, 8, 6),
            CornerRadius = new CornerRadius(6),
            Background = ResourceBrush(selected ? "KlippAccentSoftBrush" : "KlippSurfaceBrush"),
            BorderBrush = ResourceBrush(selected ? "KlippAccentBrush" : "KlippBorderSubtleBrush"),
            BorderThickness = new Thickness(1)
        };
        button.Click += OnMarkerListItemClicked;
        return button;
    }

    private void RefreshMissingMediaItems()
    {
        MissingMediaItems.Clear();
        if (_missingMediaSearchCts is null)
        {
            MissingMediaSearchStatusText.Text = string.Empty;
            MissingMediaSearchStatusText.Visibility = Visibility.Collapsed;
        }
        if (_project is null)
        {
            MissingMediaCard.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (var group in _project.Clips.Where(clip => clip.IsMissing)
                     .GroupBy(clip => clip.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            MissingMediaItems.Add(new MissingEditorMediaItem
            {
                OriginalPath = group.Key,
                Name = group.First().Name,
                IsAudio = false,
                ExpectedDurationSeconds = group.First().SourceDurationSeconds
            });
        }

        foreach (var group in _project.AudioTracks.Where(track => track.IsMissing)
                     .GroupBy(track => track.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            MissingMediaItems.Add(new MissingEditorMediaItem
            {
                OriginalPath = group.Key,
                Name = group.First().Name,
                IsAudio = true,
                ExpectedDurationSeconds = group.First().SourceDurationSeconds
            });
        }

        foreach (var group in _project.GraphicLayers.Where(layer => layer.IsMissing)
                     .GroupBy(layer => layer.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            MissingMediaItems.Add(new MissingEditorMediaItem
            {
                OriginalPath = group.Key,
                Name = group.First().Name,
                IsGraphic = true,
                ExpectedDurationSeconds = 0
            });
        }

        MissingMediaCard.Visibility = MissingMediaItems.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SyncSelectedClipAudioControls()
    {
        if (SelectedClipAudioCard is null || ClipVolumeSlider is null || ClipMuteAudioToggle is null)
            return;

        var clip = LayerTimeline.GetSelectedMediaClip();
        SelectedClipAudioCard.Visibility = clip is { SourceAudioSeparated: false } && _selectedAudioTrackId is null
            ? Visibility.Visible : Visibility.Collapsed;
        var localTime = clip is null
            ? 0
            : LayerTimeline.GetMediaClipLocalTime(clip.Id, CurrentTimelinePosition);
        var currentVolume = clip?.EvaluateVolume(localTime) ?? 1;
        var keyframe = clip is null ? null : FindClipVolumeKeyframeAtPlayhead(clip);
        var hasKeyframes = clip?.VolumeKeyframes.Count > 0;
        _syncingClipAudioControls = true;
        try
        {
            SelectedClipAudioCard.Opacity = clip is null ? 0.55 : 1;
            ClipVolumeSlider.IsEnabled = clip is not null;
            ClipMuteAudioToggle.IsEnabled = clip is not null;
            SelectedClipAudioNameText.Text = clip?.Name ?? "Select one clip";
            ClipVolumeSlider.Value = currentVolume * 100;
            ClipMuteAudioToggle.IsOn = clip?.Muted ?? false;
            ClipAnalyzeLoudnessButton.IsEnabled = clip is not null;
            ClipNormalizeToggle.IsEnabled = clip?.IntegratedLoudnessLufs is not null;
            ClipNormalizeToggle.IsOn = clip?.NormalizeAudio ?? false;
            ClipLoudnessStatusText.Text = FormatLoudnessStatus(
                clip?.IntegratedLoudnessLufs,
                clip?.NormalizationGainDb ?? 0);
            ClipNoiseReductionToggle.IsEnabled = clip is not null;
            ClipNoiseReductionToggle.IsOn = clip?.NoiseReductionEnabled ?? false;
            ClipNoiseReductionSlider.IsEnabled = clip?.NoiseReductionEnabled ?? false;
            ClipNoiseReductionSlider.Value = (clip?.NoiseReductionStrength ?? 0.45) * 100;
            ClipNoiseReductionValueText.Text = $"{Math.Round((clip?.NoiseReductionStrength ?? 0.45) * 100):0}%";
            ClipDetectBeatsButton.IsEnabled = clip is not null && File.Exists(clip.SourcePath);
            ClipVolumeValueText.Text = $"{Math.Round(currentVolume * 100):0}%";
            ClipVolumeKeyframeCountText.Text = (clip?.VolumeKeyframes.Count ?? 0).ToString();
            ClipVolumeKeyframeStatusText.Text = clip is null
                ? "Select one clip to automate its volume."
                : keyframe is not null
                    ? $"Keyframe at {localTime:0.00}s. Drag volume to update it."
                    : hasKeyframes
                        ? $"Interpolated value at {localTime:0.00}s. Drag volume to add a keyframe."
                        : "Animate clip volume from the playhead.";
            ClipVolumeAddKeyframeButtonText.Text = keyframe is null ? "Add keyframe" : "Update keyframe";
            ClipVolumeAddKeyframeButton.IsEnabled = clip is not null;
            ClipVolumePreviousKeyframeButton.IsEnabled = hasKeyframes;
            ClipVolumeNextKeyframeButton.IsEnabled = hasKeyframes;
            ClipVolumeDeleteKeyframeButton.IsEnabled = keyframe is not null;
            ClearClipVolumeKeyframesButton.IsEnabled = hasKeyframes;
            ClipVolumeKeyframeEasingComboBox.IsEnabled = keyframe is not null;
            ClipVolumeKeyframeEasingComboBox.SelectedItem = ClipVolumeKeyframeEasingComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(),
                    keyframe?.Easing ?? "linear",
                    StringComparison.Ordinal));
        }
        finally
        {
            _syncingClipAudioControls = false;
        }
    }

    private EditorProjectClipVolumeKeyframe? FindClipVolumeKeyframeAtPlayhead(EditorProjectClip clip)
    {
        var localTime = LayerTimeline.GetMediaClipLocalTime(clip.Id, CurrentTimelinePosition);
        return clip.VolumeKeyframes
            .OrderBy(keyframe => Math.Abs(keyframe.TimeSeconds - localTime))
            .FirstOrDefault(keyframe => Math.Abs(keyframe.TimeSeconds - localTime) <= 1.0 / 60.0);
    }

    private EditorProjectAudioTrack? GetSelectedAudioTrack() =>
        _project?.AudioTracks.FirstOrDefault(track =>
            string.Equals(track.Id, _selectedAudioTrackId, StringComparison.Ordinal));

    private void SyncSelectedAudioTrackControls()
    {
        if (SelectedAudioTrackCard is null || SelectedAudioTrackVolumeSlider is null)
            return;

        var track = GetSelectedAudioTrack();
        var enabled = track is not null && _project?.GetTrack(track.TrackId)?.Locked != true;
        _syncingAudioTrackControls = true;
        try
        {
            SelectedAudioTrackCard.Opacity = track is not null ? 1 : 0.55;
            SelectedAudioTrackNameText.Text = track?.Name ?? "Select an audio clip in the timeline";
            var previewVolume = track?.EvaluateVolume(Math.Max(0, CurrentTimelinePosition.TotalSeconds - track.TimelineStartSeconds)) ?? 1;
            SelectedAudioTrackVolumeText.Text = FormatAudioLevel(previewVolume);
            SelectedAudioTrackVolumeSlider.IsEnabled = enabled;
            SelectedAudioTrackVolumeSlider.Value = previewVolume * 100;
            SelectedAudioTrackMuteToggle.IsEnabled = enabled;
            SelectedAudioTrackMuteToggle.IsOn = track?.Muted ?? false;
            SelectedAudioTrackAnalyzeLoudnessButton.IsEnabled = enabled;
            SelectedAudioTrackNormalizeToggle.IsEnabled = enabled && track?.IntegratedLoudnessLufs is not null;
            SelectedAudioTrackNormalizeToggle.IsOn = track?.NormalizeAudio ?? false;
            SelectedAudioTrackLoudnessStatusText.Text = FormatLoudnessStatus(
                track?.IntegratedLoudnessLufs,
                track?.NormalizationGainDb ?? 0);
            SelectedAudioTrackDuckingToggle.IsEnabled = enabled;
            SelectedAudioTrackDuckingToggle.IsOn = track?.DuckingEnabled ?? false;
            SelectedAudioTrackDuckingSlider.IsEnabled = enabled && (track?.DuckingEnabled ?? false);
            SelectedAudioTrackDuckingSlider.Value = (track?.DuckingStrength ?? 0.65) * 100;
            SelectedAudioTrackDuckingValueText.Text = $"{Math.Round((track?.DuckingStrength ?? 0.65) * 100):0}%";
            SelectedAudioTrackNoiseReductionToggle.IsEnabled = enabled;
            SelectedAudioTrackNoiseReductionToggle.IsOn = track?.NoiseReductionEnabled ?? false;
            SelectedAudioTrackNoiseReductionSlider.IsEnabled = enabled && (track?.NoiseReductionEnabled ?? false);
            SelectedAudioTrackNoiseReductionSlider.Value = (track?.NoiseReductionStrength ?? 0.45) * 100;
            SelectedAudioTrackNoiseReductionValueText.Text = $"{Math.Round((track?.NoiseReductionStrength ?? 0.45) * 100):0}%";
            SelectedAudioTrackDetectBeatsButton.IsEnabled = enabled && File.Exists(track!.SourcePath);
            SelectedAudioTrackTimelineStartBox.IsEnabled = enabled;
            SelectedAudioTrackTimelineStartBox.Value = track?.TimelineStartSeconds ?? 0;
            SelectedAudioTrackTrimStartBox.IsEnabled = enabled;
            SelectedAudioTrackTrimStartBox.Maximum = Math.Max(0, (track?.SourceDurationSeconds ?? 1) - 0.1);
            SelectedAudioTrackTrimStartBox.Value = track?.TrimStartSeconds ?? 0;
            SelectedAudioTrackTrimEndBox.IsEnabled = enabled;
            SelectedAudioTrackTrimEndBox.Maximum = track?.SourceDurationSeconds ?? 1;
            SelectedAudioTrackTrimEndBox.Value = track?.TrimEndSeconds ?? 1;
            SyncAudioAdjustmentControls(track, enabled, previewVolume);
        }
        finally
        {
            _syncingAudioTrackControls = false;
        }
    }

    private void SyncSelectedClipVisualControls()
    {
        if (SelectedClipVisualCard is null || SelectedClipScaleSlider is null)
            return;

        var clip = LayerTimeline.GetSelectedMediaClip();
        var clipLocalTime = clip is null
            ? 0
            : LayerTimeline.GetMediaClipLocalTime(clip.Id, CurrentTimelinePosition);
        var transformState = clip?.EvaluateTransform(clipLocalTime) ??
                             new EditorProjectClipTransformState(1, 0, 0);
        var currentTransformKeyframe = clip?.TransformKeyframes
            .OrderBy(keyframe => Math.Abs(keyframe.TimeSeconds - clipLocalTime))
            .FirstOrDefault(keyframe => Math.Abs(keyframe.TimeSeconds - clipLocalTime) <= 1.0 / 60.0);
        var filterStrength = clip?.EvaluateFilterStrength(clipLocalTime) ?? 1;
        var currentFilterKeyframe = clip?.FilterStrengthKeyframes
            .OrderBy(keyframe => Math.Abs(keyframe.TimeSeconds - clipLocalTime))
            .FirstOrDefault(keyframe => Math.Abs(keyframe.TimeSeconds - clipLocalTime) <= 1.0 / 60.0);
        _syncingSelectedClipVisualControls = true;
        try
        {
            var enabled = clip is not null;
            SelectedClipVisualCard.Opacity = enabled ? 1 : 0.55;
            SelectedClipNotesBox.IsEnabled = enabled;
            SelectedClipScaleSlider.IsEnabled = enabled;
            SelectedClipXSlider.IsEnabled = enabled;
            SelectedClipYSlider.IsEnabled = enabled;
            UpsertClipTransformKeyframeButton.IsEnabled = enabled;
            PreviousClipTransformKeyframeButton.IsEnabled = enabled && clip!.TransformKeyframes.Count > 0;
            NextClipTransformKeyframeButton.IsEnabled = enabled && clip!.TransformKeyframes.Count > 0;
            RemoveClipTransformKeyframeButton.IsEnabled = currentTransformKeyframe is not null;
            ClipTransformEasingComboBox.IsEnabled = currentTransformKeyframe is not null;
            ClearClipTransformAnimationButton.IsEnabled = enabled && clip!.TransformKeyframes.Count > 0;
            SelectedClipCropLeftSlider.IsEnabled = enabled;
            SelectedClipCropTopSlider.IsEnabled = enabled;
            SelectedClipCropRightSlider.IsEnabled = enabled;
            SelectedClipCropBottomSlider.IsEnabled = enabled;
            PipFullButton.IsEnabled = enabled;
            PipTopLeftButton.IsEnabled = enabled;
            PipTopRightButton.IsEnabled = enabled;
            PipBottomLeftButton.IsEnabled = enabled;
            PipBottomRightButton.IsEnabled = enabled;
            SelectedClipMaskShapeComboBox.IsEnabled = enabled;
            var roundedVideoMask = enabled &&
                                   string.Equals(clip?.MaskShape, "rounded", StringComparison.Ordinal);
            SelectedClipMaskRoundnessSlider.IsEnabled = roundedVideoMask;
            SelectedClipMaskRoundnessSlider.Opacity = roundedVideoMask ? 1 : 0.45;
            SelectedClipMaskRoundnessValueText.Opacity = roundedVideoMask ? 1 : 0.45;
            ClipFilterStrengthSlider.IsEnabled = enabled;
            ClipFilterAddKeyframeButton.IsEnabled = enabled;
            ClipFilterPreviousKeyframeButton.IsEnabled = enabled && clip!.FilterStrengthKeyframes.Count > 0;
            ClipFilterNextKeyframeButton.IsEnabled = enabled && clip!.FilterStrengthKeyframes.Count > 0;
            ClipFilterDeleteKeyframeButton.IsEnabled = currentFilterKeyframe is not null;
            ClipFilterKeyframeEasingComboBox.IsEnabled = currentFilterKeyframe is not null;
            ClearClipFilterKeyframesButton.IsEnabled = enabled && clip!.FilterStrengthKeyframes.Count > 0;
            ClipBrightnessSlider.IsEnabled = enabled;
            ClipContrastSlider.IsEnabled = enabled;
            ClipSaturationSlider.IsEnabled = enabled;
            ClipTemperatureSlider.IsEnabled = enabled;
            ClipShadowsSlider.IsEnabled = enabled;
            ClipHighlightsSlider.IsEnabled = enabled;
            ClipGammaSlider.IsEnabled = enabled;
            ClipHueSlider.IsEnabled = enabled;
            ToneCurveEditor.IsEnabled = enabled;
            ClipSharpenSlider.IsEnabled = enabled;
            ClipBlurSlider.IsEnabled = enabled;
            ClipVignetteSlider.IsEnabled = enabled;
            ImportClipLutButton.IsEnabled = enabled;
            var hasLut = enabled && !string.IsNullOrWhiteSpace(clip?.LutPath);
            ClearClipLutButton.IsEnabled = hasLut;
            ClipLutStrengthSlider.IsEnabled = hasLut;
            SelectedClipVisualNameText.Text = clip?.Name ?? "Select one clip";
            SelectedClipNotesBox.Text = clip?.Notes ?? string.Empty;
            SelectedClipScaleSlider.Value = transformState.Scale * 100;
            SelectedClipXSlider.Value = transformState.OffsetX * 100;
            SelectedClipYSlider.Value = transformState.OffsetY * 100;
            ClipTransformKeyframeCountText.Text = $"{clip?.TransformKeyframes.Count ?? 0} keyframes";
            ClipTransformKeyframeActionText.Text = currentTransformKeyframe is null ? "Add keyframe" : "Update keyframe";
            ClipTransformKeyframeStatusText.Text = clip is null
                ? "Select one clip"
                : currentTransformKeyframe is not null
                    ? $"Keyframe at {clipLocalTime:0.00}s"
                    : clip.TransformKeyframes.Count > 0
                        ? $"Interpolated at {clipLocalTime:0.00}s"
                        : "Static transform";
            ClipTransformEasingComboBox.SelectedItem = ClipTransformEasingComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(),
                    currentTransformKeyframe?.Easing ?? "linear",
                    StringComparison.Ordinal));
            SelectedClipCropLeftSlider.Value = (clip?.CropLeft ?? 0) * 100;
            SelectedClipCropTopSlider.Value = (clip?.CropTop ?? 0) * 100;
            SelectedClipCropRightSlider.Value = (clip?.CropRight ?? 0) * 100;
            SelectedClipCropBottomSlider.Value = (clip?.CropBottom ?? 0) * 100;
            SelectedClipMaskShapeComboBox.SelectedItem = SelectedClipMaskShapeComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(),
                    clip?.MaskShape ?? "none",
                    StringComparison.Ordinal));
            SelectedClipMaskRoundnessSlider.Value = (clip?.MaskRoundness ?? 0.16) * 100;
            SelectedClipMaskRoundnessValueText.Text = $"{Math.Round((clip?.MaskRoundness ?? 0.16) * 100):0}%";
            ClipFilterStrengthSlider.Value = filterStrength * 100;
            ClipFilterStrengthValueText.Text = $"{Math.Round(filterStrength * 100):0}%";
            SyncQuickClipEffectControls(enabled, filterStrength);
            ClipFilterKeyframeCountText.Text = (clip?.FilterStrengthKeyframes.Count ?? 0).ToString();
            ClipFilterAddKeyframeButtonText.Text = currentFilterKeyframe is null ? "Add keyframe" : "Update keyframe";
            ClipFilterKeyframeStatusText.Text = clip is null
                ? "Select one clip to animate its filter mix."
                : currentFilterKeyframe is not null
                    ? $"Keyframe at {clipLocalTime:0.00}s."
                    : clip.FilterStrengthKeyframes.Count > 0
                        ? $"Interpolated mix at {clipLocalTime:0.00}s."
                        : "Animate the original/effect mix from the playhead.";
            ClipFilterKeyframeEasingComboBox.SelectedItem = ClipFilterKeyframeEasingComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(),
                    currentFilterKeyframe?.Easing ?? "linear",
                    StringComparison.Ordinal));
            ClipBrightnessSlider.Value = (clip?.Brightness ?? 0) * 100;
            ClipContrastSlider.Value = (clip?.Contrast ?? 1) * 100;
            ClipSaturationSlider.Value = (clip?.Saturation ?? 1) * 100;
            ClipTemperatureSlider.Value = (clip?.Temperature ?? 0) * 100;
            ClipShadowsSlider.Value = (clip?.Shadows ?? 0) * 100;
            ClipHighlightsSlider.Value = (clip?.Highlights ?? 0) * 100;
            ClipGammaSlider.Value = (clip?.Gamma ?? 1) * 100;
            ClipHueSlider.Value = clip?.HueDegrees ?? 0;
            ToneCurveEditor.SetPoints(clip?.ToneCurve ?? []);
            ClipSharpenSlider.Value = (clip?.Sharpen ?? 0) * 100;
            ClipBlurSlider.Value = (clip?.Blur ?? 0) * 10;
            ClipVignetteSlider.Value = (clip?.Vignette ?? 0) * 100;
            ClipLutStrengthSlider.Value = (clip?.LutStrength ?? 1) * 100;
            SelectedClipLutNameText.Text = hasLut
                ? $"{Path.GetFileNameWithoutExtension(clip!.LutPath)}{(File.Exists(clip.LutPath) ? string.Empty : " (missing)")}"
                : "No LUT";
            SelectedClipScaleValueText.Text = $"{Math.Round(transformState.Scale * 100):0}%";
            SelectedClipCropLeftValueText.Text = $"{Math.Round((clip?.CropLeft ?? 0) * 100):0}%";
            SelectedClipCropTopValueText.Text = $"{Math.Round((clip?.CropTop ?? 0) * 100):0}%";
            SelectedClipCropRightValueText.Text = $"{Math.Round((clip?.CropRight ?? 0) * 100):0}%";
            SelectedClipCropBottomValueText.Text = $"{Math.Round((clip?.CropBottom ?? 0) * 100):0}%";
            ClipBrightnessValueText.Text = $"{Math.Round((clip?.Brightness ?? 0) * 100):0}";
            ClipContrastValueText.Text = $"{Math.Round((clip?.Contrast ?? 1) * 100):0}%";
            ClipSaturationValueText.Text = $"{Math.Round((clip?.Saturation ?? 1) * 100):0}%";
            ClipTemperatureValueText.Text = $"{Math.Round((clip?.Temperature ?? 0) * 100):0}";
            ClipShadowsValueText.Text = $"{Math.Round((clip?.Shadows ?? 0) * 100):0}";
            ClipHighlightsValueText.Text = $"{Math.Round((clip?.Highlights ?? 0) * 100):0}";
            ClipGammaValueText.Text = $"{(clip?.Gamma ?? 1):0.00}";
            ClipHueValueText.Text = $"{Math.Round(clip?.HueDegrees ?? 0):0}°";
            ClipSharpenValueText.Text = $"{Math.Round((clip?.Sharpen ?? 0) * 100):0}";
            ClipBlurValueText.Text = $"{(clip?.Blur ?? 0):0.0}";
            ClipVignetteValueText.Text = $"{Math.Round((clip?.Vignette ?? 0) * 100):0}%";
            ClipLutStrengthValueText.Text = $"{Math.Round((clip?.LutStrength ?? 1) * 100):0}%";
            UpdatePipPresetButtons(transformState, enabled);
            UpdateSelectedClipFilterButtons(clip?.Filter ?? "none", enabled);
            SyncQuickColorControls();
            UpdateColorSwatchButtons(
                clip?.ColorLabel ?? "none",
                ClipColorNoneButton,
                ClipColorRedButton,
                ClipColorOrangeButton,
                ClipColorYellowButton,
                ClipColorGreenButton,
                ClipColorTealButton,
                ClipColorBlueButton,
                ClipColorPurpleButton);
            foreach (var button in GetClipColorButtons())
                button.IsEnabled = enabled;
        }
        finally
        {
            _syncingSelectedClipVisualControls = false;
        }
    }

    private void UpdateSelectedClipFilterButtons(string filter, bool enabled)
    {
        ClipFilterNoneButton.IsEnabled = enabled;
        ClipFilterBoostButton.IsEnabled = enabled;
        ClipFilterWarmButton.IsEnabled = enabled;
        ClipFilterCoolButton.IsEnabled = enabled;
        ClipFilterMonoButton.IsEnabled = enabled;
        ClipFilterCinemaButton.IsEnabled = enabled;
        SetOptionButtonState(ClipFilterNoneButton, filter == "none");
        SetOptionButtonState(ClipFilterBoostButton, filter == "boost");
        SetOptionButtonState(ClipFilterWarmButton, filter == "warm");
        SetOptionButtonState(ClipFilterCoolButton, filter == "cool");
        SetOptionButtonState(ClipFilterMonoButton, filter == "mono");
        SetOptionButtonState(ClipFilterCinemaButton, filter == "cinematic");
    }

    private void UpdatePipPresetButtons(EditorProjectClipTransformState state, bool enabled)
    {
        var full = enabled && Math.Abs(state.Scale - 1) < 0.01 &&
                   Math.Abs(state.OffsetX) < 0.01 && Math.Abs(state.OffsetY) < 0.01;
        var compact = enabled && Math.Abs(state.Scale - 0.36) < 0.01;
        SetOptionButtonState(PipFullButton, full);
        SetOptionButtonState(PipTopLeftButton, compact && state.OffsetX < -0.85 && state.OffsetY < -0.85);
        SetOptionButtonState(PipTopRightButton, compact && state.OffsetX > 0.85 && state.OffsetY < -0.85);
        SetOptionButtonState(PipBottomLeftButton, compact && state.OffsetX < -0.85 && state.OffsetY > 0.85);
        SetOptionButtonState(PipBottomRightButton, compact && state.OffsetX > 0.85 && state.OffsetY > 0.85);
    }

    private static TimeSpan? TryParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length == 2 &&
            double.TryParse(parts[0], out var minutes) &&
            double.TryParse(parts[1], out var seconds))
        {
            return TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        }

        return TimeSpan.TryParse(value, out var parsed) ? parsed : null;
    }

    private async Task ReconcileProjectDurationsAsync(EditorProject project)
    {
        foreach (var group in project.Clips.GroupBy(clip => clip.SourcePath, StringComparer.OrdinalIgnoreCase))
        {
            var duration = await _mediaProbeService.GetDurationAsync(group.Key);
            if (duration is null || duration <= TimeSpan.FromSeconds(0.1))
                continue;

            foreach (var clip in group)
            {
                var previousDuration = clip.SourceDurationSeconds;
                clip.SourceDurationSeconds = duration.Value.TotalSeconds;
                if (clip.TrimEndSeconds >= previousDuration - 0.05 ||
                    clip.TrimEndSeconds > clip.SourceDurationSeconds)
                {
                    clip.TrimEndSeconds = clip.SourceDurationSeconds;
                }
                clip.Normalize();
            }
        }

        project.Normalize();
    }

    private void QueueThumbnailLoad(string filePath)
    {
        var version = ++_thumbnailLoadVersion;
        PreviewImage.Source = null;
        PreviewImage.Opacity = 0;
        PreviewFallback.Visibility = Visibility.Visible;

        DispatcherQueue.TryEnqueue(async () =>
        {
            await Task.Delay(60);

            if (version != _thumbnailLoadVersion ||
                !string.Equals(_filePath, filePath, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(filePath))
            {
                return;
            }

            try
            {
                var thumbnailPath = await _thumbnailService.GetOrCreateThumbnailAsync(
                    filePath,
                    BackgroundWorkPriority.Interactive);
                if (version != _thumbnailLoadVersion ||
                    string.IsNullOrWhiteSpace(thumbnailPath) ||
                    !File.Exists(thumbnailPath))
                {
                    return;
                }

                PreviewImage.Source = new BitmapImage(new Uri(thumbnailPath));
                PreviewImage.Opacity = _usingNativePreview ? 0 : 1;
                PreviewFallback.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Klipp] Safe editor kunde inte ladda thumbnail-preview: {ex.Message}");
                if (!_usingNativePreview && version == _thumbnailLoadVersion)
                    PreviewFallback.Visibility = Visibility.Visible;
            }
        });
    }

    private void QueueWaveformLoad(string filePath)
    {
        // Delar invalideringsräknare med QueueThumbnailLoad — den anropas alltid
        // precis innan denna i LoadClip och har redan bumpat _thumbnailLoadVersion
        // för den här klipp-sessionen, så vi fångar bara det aktuella värdet här.
        var version = _thumbnailLoadVersion;
        LayerTimeline.SetAudioWaveform(null);

        DispatcherQueue.TryEnqueue(async () =>
        {
            if (version != _thumbnailLoadVersion ||
                !string.Equals(_filePath, filePath, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(filePath))
            {
                return;
            }

            try
            {
                var waveformPath = await _waveformService.GetOrCreateWaveformAsync(
                    filePath,
                    null,
                    null,
                    BackgroundWorkPriority.Interactive);
                if (version != _thumbnailLoadVersion ||
                    string.IsNullOrWhiteSpace(waveformPath) ||
                    !File.Exists(waveformPath))
                {
                    return;
                }

                LayerTimeline.SetAudioWaveform(new BitmapImage(new Uri(waveformPath)), filePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Klipp] Safe editor kunde inte ladda waveform: {ex.Message}");
            }
        });
    }

    private void OnPositionTimerTick(object? sender, object e)
    {
        if (!_isPreviewPlaying)
            return;
        if (_transitionPreviewStopPosition is { } stop && _previewPosition >= stop)
        {
            PausePreview();
            UpdatePlaybackUi();
            return;
        }

        PrepareUpcomingPreviewCut();

        var activeClip = _usingNativePreview ? GetActivePreviewClip() : null;
        var primaryBlocked = IsPrimaryPreviewDecoderBlocked();
        var secondaryBlocked = IsOverlayPreviewDecoderBlocked() || IsTransitionPreviewDecoderBlocked() ||
            IsAudioPreviewDecoderBlocked(_previewPosition);
        var decoderBlocked = primaryBlocked || secondaryBlocked;
        double? sourcePosition = activeClip is not null && !decoderBlocked
            ? NativePreviewPlayer.MediaPlayer.PlaybackSession.Position.TotalSeconds
            : null;
        AdvanceFreePreviewClock(activeClip, sourcePosition, decoderBlocked);

        // A decoder opening or buffering is not elapsed project time. Keep all
        // secondary audio players stopped until the picture can advance again.
        if (decoderBlocked)
        {
            _wasPreviewBuffering = true;
            _lastPreviewTick = 0;
            if (secondaryBlocked && !primaryBlocked && _usingNativePreview)
            {
                try { NativePreviewPlayer.MediaPlayer.Pause(); _pausedPrimaryForSecondaryBuffering = true; } catch { }
            }
            try { TransitionPreviewPlayer.MediaPlayer.Pause(); } catch { }
            try { OverlayPreviewPlayer.MediaPlayer.Pause(); } catch { }
            PauseAdditionalVideoTrackPreviews();
            foreach (var state in _audioTrackPreviewPlayers.Values)
                state.PauseIfPlaying();
            return;
        }

        if (_wasPreviewBuffering)
        {
            _wasPreviewBuffering = false;
            if (_pausedPrimaryForSecondaryBuffering && _usingNativePreview)
                try { NativePreviewPlayer.MediaPlayer.Play(); } catch { }
            _pausedPrimaryForSecondaryBuffering = false;
            UpdateTransitionPreview(_previewPosition, seekExisting: false);
            UpdateOverlayPreview(_previewPosition, seekExisting: false);
            UpdateAudioTrackPreview(_previewPosition, seekExisting: false);
        }

        if (activeClip is not null && sourcePosition >= activeClip.TrimEndSeconds - 0.5 / _exportFrameRate)
        {
            if (TryAdvancePreviewClip())
                return;
            FinishPreviewRange();
            return;
        }

        if (_previewPosition >= LayerTimeline.End)
        {
            FinishPreviewRange();
            return;
        }

        SynchronizePrimaryPreview(_previewPosition, seekExisting: false);
        UpdateTransitionPreview(_previewPosition, seekExisting: false);
        UpdateOverlayPreview(_previewPosition, seekExisting: false);
        UpdatePlaybackUi();
    }

    private void FinishPreviewRange()
    {
        if (_loopPreview)
        {
            SeekTo(LayerTimeline.Start);
            _lastPreviewTick = 0;
            if (_usingNativePreview)
                try { NativePreviewPlayer.MediaPlayer.Play(); } catch { }
        }
        else
        {
            _previewPosition = LayerTimeline.End;
            PausePreview();
        }
        UpdatePlaybackUi();
    }

    private void PausePreview()
    {
        StopReviewShuttle();
        _transitionPreviewStopPosition = null;
        _isPreviewPlaying = false;
        _lastPreviewTick = 0;
        _wasPreviewBuffering = false;
        _pausedPrimaryForSecondaryBuffering = false;
        _positionTimer.Stop();

        if (_usingNativePreview)
        {
            try { NativePreviewPlayer.MediaPlayer.Pause(); }
            catch { /* spelaren kan vara mitt i teardown */ }
        }
        try { TransitionPreviewPlayer.MediaPlayer.Pause(); } catch { }
        try { OverlayPreviewPlayer.MediaPlayer.Pause(); } catch { }
        PauseAdditionalVideoTrackPreviews();
        foreach (var state in _audioTrackPreviewPlayers.Values)
        {
            state.PauseIfPlaying();
        }
    }

    private void OnPreviewMediaOpened(MediaPlayer sender, object args)
    {
        var sourceVersion = _primarySourceVersion;
        var loadVersion = _projectLoadVersion;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, NativePreviewPlayer.MediaPlayer) ||
                sourceVersion != _primarySourceVersion || loadVersion != _projectLoadVersion ||
                string.IsNullOrEmpty(_filePath) || string.IsNullOrEmpty(_nativePreviewMediaPath) || sender.Source is null)
                return;

            _usingNativePreview = true;
            _isPreviewOpening = false;
            PreviewFallback.Visibility = Visibility.Collapsed;
            PreviewImage.Opacity = 0;

            try
            {
                var clip = GetActivePreviewClip();
                sender.PlaybackSession.PlaybackRate = clip?.PlaybackSpeed ?? _playbackSpeed;
                PreviewAudioGainEffect.SetGain(sender,
                    LivePreviewVolume * EvaluatePreviewClipVolume(clip, _previewPosition));
                sender.IsMuted = (MuteAudioToggle?.IsOn ?? false) || (clip?.Muted ?? false) ||
                    (clip?.SourceAudioSeparated ?? false);

                RequestDecoderSeek(sender, _primaryDecoderSeek, _pendingPreviewSourcePosition, force: true);
                if (_isPreviewPlaying)
                    sender.Play();
            }
            catch
            {
                // Ointressant om initial-synken misslyckas — nästa seek rättar till det.
            }

            UpdatePlaybackUi();
        });
    }

    private void OnPreviewMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var sourceVersion = _primarySourceVersion;
        var loadVersion = _projectLoadVersion;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, NativePreviewPlayer.MediaPlayer) ||
                sourceVersion != _primarySourceVersion || loadVersion != _projectLoadVersion || sender.Source is null)
                return;
            Debug.WriteLine($"[Klipp] Native preview misslyckades, visar thumbnail: {args.Error} {args.ErrorMessage}");
            _usingNativePreview = false;
            _isPreviewOpening = false;

            if (!string.IsNullOrWhiteSpace(_filePath) &&
                !string.IsNullOrWhiteSpace(_nativePreviewMediaPath) &&
                !string.Equals(_filePath, _nativePreviewMediaPath, StringComparison.OrdinalIgnoreCase) &&
                File.Exists(_filePath))
            {
                try { NativePreviewPlayer.MediaPlayer.Source = null; } catch { }
                _previewProxyService.DeleteProxy(_filePath, _previewQuality);
                _temporarilyUseOriginalPreview = true;
                _previewProxyStatusOverride = "Proxy playback failed. Using original media until proxies are rebuilt.";
                ProjectStatusText.Text = "Proxy failed - using original media";
                LoadNativePreview(_filePath);
                RefreshPreviewProxyControls();
                return;
            }

            if (PreviewImage.Source is null)
                PreviewFallback.Visibility = Visibility.Visible;
            else
                PreviewImage.Opacity = 1;
            PausePreview();
            UpdatePlaybackUi();
        });
    }

    private void OnOverlayPreviewMediaOpened(MediaPlayer sender, object args)
    {
        var sourceVersion = _overlaySourceVersion;
        var loadVersion = _projectLoadVersion;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (sourceVersion != _overlaySourceVersion || loadVersion != _projectLoadVersion ||
                _activeOverlayPreviewClipId is null || sender.Source is null) return;
            _overlayPreviewOpening = false;
            try
            {
                sender.IsMuted = true;
                var clip = GetActiveOverlayPreviewClip();
                sender.PlaybackSession.PlaybackRate = clip?.PlaybackSpeed ?? 1;
                RequestDecoderSeek(sender, _overlayDecoderSeek, _pendingOverlaySourcePosition, force: true);
                UpdateSelectedClipTransformPreview();
                UpdateVfxPreview();
                if (_isPreviewPlaying && !_wasPreviewBuffering)
                    sender.Play();
            }
            catch
            {
                // Overlay-preview är en visuell hjälp; huvudspelaren fortsätter vid fel.
            }
        });
    }

    private void OnTransitionPreviewMediaOpened(MediaPlayer sender, object args)
    {
        var version = _transitionPreviewSourceVersion;
        var loadVersion = _projectLoadVersion;
        var source = sender.Source;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (version != _transitionPreviewSourceVersion || loadVersion != _projectLoadVersion ||
                !ReferenceEquals(source, sender.Source) || _activeTransitionPreviewClipId is null)
                return;
            try
            {
                var clip = GetActiveTransitionPreviewClip();
                if (clip is null) return;
                _isTransitionPreviewOpening = false;
                sender.PlaybackSession.PlaybackRate = clip?.PlaybackSpeed ?? 1;
                RequestDecoderSeek(sender, _transitionDecoderSeek, _pendingTransitionSourcePosition, force: true);
                UpdateTransitionPreview(_previewPosition, seekExisting: false);
                UpdateSelectedClipTransformPreview();
                UpdateVfxPreview();
                if (_isPreviewPlaying && !_wasPreviewBuffering)
                    sender.Play();
                else sender.Pause();
            }
            catch
            {
                ClearTransitionPreview();
            }
        });
    }

    private void OnTransitionPreviewMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var version = _transitionPreviewSourceVersion;
        var loadVersion = _projectLoadVersion;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (version != _transitionPreviewSourceVersion || loadVersion != _projectLoadVersion)
                return;
            Debug.WriteLine($"[Klipp] Transition preview failed: {args.Error} {args.ErrorMessage}");
            SkipFailedTransitionPreview("A transition could not be previewed. Other video and audio tracks will continue.");
        });
    }

    private void OnPreviewMediaEnded(MediaPlayer sender, object args)
    {
        var endedClipId = _activePreviewClipId;
        var sourceVersion = _primarySourceVersion;
        var loadVersion = _projectLoadVersion;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, NativePreviewPlayer.MediaPlayer) ||
                sourceVersion != _primarySourceVersion || loadVersion != _projectLoadVersion ||
                !_usingNativePreview || !_isPreviewPlaying || IsPrimaryPreviewDecoderBlocked() ||
                !string.Equals(endedClipId, _activePreviewClipId, StringComparison.Ordinal))
                return;
            if (GetActivePreviewClip() is { } clip &&
                sender.PlaybackSession.Position.TotalSeconds < clip.TrimEndSeconds - 0.05)
                return;

            if (TryAdvancePreviewClip())
                return;

            FinishPreviewRange();
        });
    }

    private void OnPreviewStateChanged(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!ReferenceEquals(sender, NativePreviewPlayer.MediaPlayer) || !_usingNativePreview)
                return;

            UpdatePlaybackUi();
        });
    }

    private void OnBackClicked(object sender, RoutedEventArgs e)
    {
        Stop();
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnProjectNameChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingProjectName || _suppressProjectChanges || _isLoadingClip || _project is null)
            return;

        _project.Name = string.IsNullOrWhiteSpace(TitleText.Text) ? "Untitled" : TitleText.Text.Trim();
        QueueProjectChange();
    }

    private async void OnSaveNamedProjectClicked(object sender, RoutedEventArgs e)
    {
        if (_project is null)
            return;

        CommitPendingProjectChange();
        CaptureProjectFromUi();
        _project.Name = string.IsNullOrWhiteSpace(TitleText.Text) ? "Untitled" : TitleText.Text.Trim();
        try
        {
            await _projectService.SaveNamedProjectAsync(EditorProjectService.Clone(_project));
            await RefreshSavedProjectsAsync();
            ProjectStatusText.Text = "Project saved";
        }
        catch (Exception ex)
        {
            ProjectStatusText.Text = "Project save failed";
            Debug.WriteLine($"[Klipp] Named project save failed: {ex}");
        }
    }

    private async void OnOpenSavedProjectClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string projectPath })
            return;
        try { await OpenProjectFileAsync(projectPath); }
        catch (Exception exception) { ProjectStatusText.Text = exception.Message; }
    }

    private void OnRelinkMissingMediaClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string originalPath })
            return;

        var item = MissingMediaItems.FirstOrDefault(candidate =>
            string.Equals(candidate.OriginalPath, originalPath, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
            MissingMediaRelinkRequested?.Invoke(
                this,
                new MissingMediaRelinkEventArgs(item.OriginalPath, item.IsAudio, item.IsGraphic));
    }

    private async void OnFindMissingMediaClicked(object sender, RoutedEventArgs e)
    {
        if (_project is null || MissingMediaItems.Count == 0)
            return;

        _missingMediaSearchCts?.Cancel();
        _missingMediaSearchCts?.Dispose();
        var searchCts = new CancellationTokenSource();
        _missingMediaSearchCts = searchCts;

        FindMissingMediaButton.IsEnabled = false;
        MissingMediaSearchProgress.IsActive = true;
        MissingMediaSearchProgress.Visibility = Visibility.Visible;
        MissingMediaSearchStatusText.Text = "Searching media folders...";
        MissingMediaSearchStatusText.Visibility = Visibility.Visible;

        try
        {
            var missingItems = MissingMediaItems.ToList();
            var requests = missingItems
                .Select(item => new MissingMediaSearchRequest(
                    item.OriginalPath,
                    item.IsAudio,
                    item.IsGraphic,
                    item.ExpectedDurationSeconds))
                .ToList();
            var results = await _missingMediaSearchService.FindMatchesAsync(
                requests,
                BuildMissingMediaSearchRoots(),
                searchCts.Token);
            if (!ReferenceEquals(_missingMediaSearchCts, searchCts))
                return;

            var confidentMatches = results
                .Where(result => result.IsConfident &&
                                 !string.IsNullOrWhiteSpace(result.ReplacementPath) &&
                                 File.Exists(result.ReplacementPath))
                .ToList();
            if (confidentMatches.Count == 0)
            {
                MissingMediaSearchStatusText.Text = results.Any(result => result.CandidateCount > 0)
                    ? "Possible files were found, but none were unambiguous. Use Relink to choose manually."
                    : "No matching files were found in the project and media folders.";
                return;
            }

            CommitPendingProjectChange();
            var selectedClipId = LayerTimeline.SelectedMediaClipId;
            var changedCount = 0;
            foreach (var match in confidentMatches)
            {
                if (ApplyMediaReplacement(
                        match.Request.OriginalPath,
                        match.ReplacementPath!,
                        match.Request.IsAudio,
                        match.Request.IsGraphic,
                        match.DurationSeconds))
                {
                    changedCount++;
                }
            }

            if (changedCount == 0)
                return;

            await SeparateProjectSourceAudioAsync(_project!);
            var remainingCount = Math.Max(0, missingItems.Count - changedCount);
            CompleteMediaRelink(
                selectedClipId,
                remainingCount == 0
                    ? $"Relinked {changedCount} missing file{(changedCount == 1 ? string.Empty : "s")}"
                    : $"Relinked {changedCount} file{(changedCount == 1 ? string.Empty : "s")} - {remainingCount} still missing");
            MissingMediaSearchStatusText.Text = remainingCount == 0
                ? "All missing media was found and relinked."
                : $"Relinked {changedCount}. {remainingCount} file{(remainingCount == 1 ? " is" : "s are")} still missing.";
        }
        catch (OperationCanceledException)
        {
            MissingMediaSearchStatusText.Text = "Search stopped.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Klipp] Missing media search failed: {ex.Message}");
            MissingMediaSearchStatusText.Text = "The media search could not be completed.";
        }
        finally
        {
            if (ReferenceEquals(_missingMediaSearchCts, searchCts))
            {
                _missingMediaSearchCts = null;
                searchCts.Dispose();
                FindMissingMediaButton.IsEnabled = true;
                MissingMediaSearchProgress.IsActive = false;
                MissingMediaSearchProgress.Visibility = Visibility.Collapsed;
            }
        }
    }

    private IReadOnlyList<string> BuildMissingMediaSearchRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddRoot(string? root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return;
            try { roots.Add(Path.GetFullPath(root)); } catch { }
        }

        void AddParent(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try { AddRoot(Path.GetDirectoryName(path)); } catch { }
        }

        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        AddRoot(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        AddParent(_projectSourcePath);

        foreach (var item in LibraryItems)
            AddParent(item.FilePath);

        if (_project is not null)
        {
            foreach (var clip in _project.Clips.Where(clip => !clip.IsMissing))
                AddParent(clip.SourcePath);
            foreach (var track in _project.AudioTracks.Where(track => !track.IsMissing))
                AddParent(track.SourcePath);
            foreach (var layer in _project.GraphicLayers.Where(layer => !layer.IsMissing))
                AddParent(layer.SourcePath);
        }

        foreach (var item in MissingMediaItems)
            AddParent(item.OriginalPath);

        return roots.ToList();
    }

    private void OnExportClicked(object sender, RoutedEventArgs e)
    {
        CommitPendingProjectChange();
        CaptureProjectFromUi();
        if (_project is not null)
        {
            ProjectExportRequested?.Invoke(
                this,
                new EditorProjectExportEventArgs(EditorProjectService.Clone(_project)));
            return;
        }

        if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath))
            return;

        ExportRequested?.Invoke(
            this,
            new ClipEditorExportEventArgs(
                _filePath,
                LayerTimeline.Start,
                LayerTimeline.End,
                _playbackSpeed,
                ExportAudioVolume,
                MuteAudioToggle.IsOn,
                LayerTimeline.VfxEnabled ? _videoFilter : "none",
                TextOverlayBox.Text.Trim(),
                _textStyle,
                _textPosition,
                LayerTimeline.ElementEnabled ? _elementOverlay : "none",
                _fadeInSeconds,
                _fadeOutSeconds,
                _freezeFrameSeconds,
                SubtitleTextBox.Text.Trim(),
                LayerTimeline.TextEnabled,
                LayerTimeline.TextStart,
                LayerTimeline.TextEnd,
                LayerTimeline.VfxEnabled,
                LayerTimeline.VfxStart,
                LayerTimeline.VfxEnd,
                LayerTimeline.SubtitleEnabled,
                LayerTimeline.SubtitleStart,
                LayerTimeline.SubtitleEnd,
                LayerTimeline.ElementEnabled,
                LayerTimeline.ElementStart,
                LayerTimeline.ElementEnd,
                _textAnchorX,
                _textAnchorY,
                _textSize,
                _subtitleStyle,
                _subtitleAnchorX,
                _subtitleAnchorY,
                _subtitleSize,
                _videoScale,
                _videoOffsetX,
                _videoOffsetY,
                _textAnimation,
                _textBoxWidth,
                _subtitleBoxWidth,
                _textFontFamily,
                _textColor,
                _subtitleFontFamily,
                _subtitleColor));
    }

    private void OnCancelExportJobClicked(object sender, RoutedEventArgs e) =>
        RaiseExportJobAction(sender, EditorExportJobAction.Cancel);

    private void OnRetryExportJobClicked(object sender, RoutedEventArgs e) =>
        RaiseExportJobAction(sender, EditorExportJobAction.Retry);

    private void OnOpenExportJobClicked(object sender, RoutedEventArgs e) =>
        RaiseExportJobAction(sender, EditorExportJobAction.Open);

    private void RaiseExportJobAction(object sender, EditorExportJobAction action)
    {
        if (sender is Button { Tag: string jobId } && !string.IsNullOrWhiteSpace(jobId))
            ExportJobActionRequested?.Invoke(this, new EditorExportJobActionEventArgs(jobId, action));
    }

    private void OnPreviewTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_suppressNextPreviewTap)
        {
            _suppressNextPreviewTap = false;
            e.Handled = true;
            return;
        }

        e.Handled = true;
    }

    private void OnOverlayTapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnPlayPauseClicked(object sender, RoutedEventArgs e)
    {
        TogglePlayPause();
    }

    private void OnPreviewWorkspaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (PreviewCanvasViewport is not null)
            PreviewCanvasViewport.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height))
            };
        SetPreviewClips();
        UpdatePreviewStageZoom();
    }

    private void OnPreviewWorkspacePointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_fxPreviewDragId is not null)
        {
            e.Handled = true;
            return;
        }
        var delta = e.GetCurrentPoint(PreviewWorkspace).Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        SetPreviewZoom(_previewZoom + Math.Sign(delta) * 0.05);
        e.Handled = true;
    }

    private void OnPreviewSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        SetPreviewClips();
        ApplyTextOverlayPlacement();
        ApplySubtitleOverlayPlacement();
        UpdateVideoTransformPreview(syncControls: false);
        UpdateSelectedClipTransformPreview();
        UpdateSafeAreaGuides();
        UpdateTransitionPreview(_previewPosition, seekExisting: false);
    }

    private void SetPreviewClips()
    {
        if (PreviewWorkspace is not null)
        {
            PreviewWorkspace.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, PreviewWorkspace.ActualWidth, PreviewWorkspace.ActualHeight)
            };
        }

        if (PreviewSurface is not null)
        {
            PreviewSurface.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, PreviewSurface.ActualWidth, PreviewSurface.ActualHeight)
            };
        }
    }

    private void OnPreviewSurfacePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_activeTool != "general" ||
            _resizeTarget != ResizeTarget.None ||
            IsWithin(e.OriginalSource as DependencyObject, TextOverlayBackground) ||
            IsWithin(e.OriginalSource as DependencyObject, SubtitleOverlayBackground))
        {
            return;
        }

        var pointerPoint = e.GetCurrentPoint(PreviewSurface);
        if (!pointerPoint.Properties.IsLeftButtonPressed)
            return;

        _isDraggingVideoTransform = true;
        _videoDragStartPoint = pointerPoint.Position;
        _videoDragStartOffsetX = _videoOffsetX;
        _videoDragStartOffsetY = _videoOffsetY;
        PreviewSurface.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPreviewSurfacePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingVideoTransform)
            return;

        var pointerPoint = e.GetCurrentPoint(PreviewSurface);
        if (!pointerPoint.Properties.IsLeftButtonPressed)
        {
            FinishVideoTransformDrag(e);
            return;
        }

        var width = Math.Max(1, PreviewSurface.ActualWidth);
        var height = Math.Max(1, PreviewSurface.ActualHeight);
        var dx = pointerPoint.Position.X - _videoDragStartPoint.X;
        var dy = pointerPoint.Position.Y - _videoDragStartPoint.Y;

        if (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)
            _suppressNextPreviewTap = true;

        _videoOffsetX = Math.Clamp(_videoDragStartOffsetX + SnapToCanvasGrid(dx) / width, -0.5, 0.5);
        _videoOffsetY = Math.Clamp(_videoDragStartOffsetY + SnapToCanvasGrid(dy) / height, -0.5, 0.5);
        UpdateVideoTransformPreview();
        e.Handled = true;
    }

    private void OnPreviewSurfacePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        FinishVideoTransformDrag(e);
    }

    private void OnPreviewSurfacePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        FinishVideoTransformDrag(e);
    }

    private void FinishVideoTransformDrag(PointerRoutedEventArgs e)
    {
        if (!_isDraggingVideoTransform)
            return;

        _isDraggingVideoTransform = false;
        PreviewSurface.ReleasePointerCapture(e.Pointer);
        e.Handled = _suppressNextPreviewTap;
        QueueProjectChange();
    }

    private void OnVideoFramePointerPressed(object sender, PointerRoutedEventArgs e) => OnPreviewSurfacePointerPressed(sender, e);
    private void OnVideoFramePointerMoved(object sender, PointerRoutedEventArgs e) => OnPreviewSurfacePointerMoved(sender, e);
    private void OnVideoFramePointerReleased(object sender, PointerRoutedEventArgs e) => OnPreviewSurfacePointerReleased(sender, e);
    private void OnVideoFramePointerCanceled(object sender, PointerRoutedEventArgs e) => OnPreviewSurfacePointerCanceled(sender, e);

    private void OnTextOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyTextOverlayPlacement();
    }

    private void OnSubtitleOverlaySizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplySubtitleOverlayPlacement();
    }

    private void OnTextOverlayPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (TextOverlayBackground.Visibility != Visibility.Visible)
            return;

        var pointerPoint = e.GetCurrentPoint(PreviewSurface);
        if (!pointerPoint.Properties.IsLeftButtonPressed)
            return;

        _isDraggingTextOverlay = true;
        _textDragOffset = new Point(
            pointerPoint.Position.X - TextOverlayBackground.Margin.Left,
            pointerPoint.Position.Y - TextOverlayBackground.Margin.Top);
        TextOverlayBackground.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnTextOverlayPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingTextOverlay)
            return;

        var pointerPoint = e.GetCurrentPoint(PreviewSurface);
        if (!pointerPoint.Properties.IsLeftButtonPressed)
        {
            FinishTextOverlayDrag(e);
            return;
        }

        var left = SnapToCanvasGrid(pointerPoint.Position.X - _textDragOffset.X);
        var top = SnapToCanvasGrid(pointerPoint.Position.Y - _textDragOffset.Y);
        UpdateTextAnchorFromLayout(left, top);
        _textPosition = "custom";
        UpdateTextButtons();
        ApplyTextOverlayPlacement();
        e.Handled = true;
    }

    private void OnTextOverlayPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        FinishTextOverlayDrag(e);
    }

    private void OnTextOverlayPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        FinishTextOverlayDrag(e);
    }

    private void FinishTextOverlayDrag(PointerRoutedEventArgs e)
    {
        if (!_isDraggingTextOverlay)
            return;

        _isDraggingTextOverlay = false;
        TextOverlayBackground.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        QueueProjectChange();
    }

    private void OnSubtitleOverlayPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (SubtitleOverlayBackground.Visibility != Visibility.Visible)
            return;

        var pointerPoint = e.GetCurrentPoint(PreviewSurface);
        if (!pointerPoint.Properties.IsLeftButtonPressed)
            return;

        _isDraggingSubtitleOverlay = true;
        _subtitleDragOffset = new Point(
            pointerPoint.Position.X - SubtitleOverlayBackground.Margin.Left,
            pointerPoint.Position.Y - SubtitleOverlayBackground.Margin.Top);
        SubtitleOverlayBackground.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnSubtitleOverlayPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingSubtitleOverlay)
            return;

        var pointerPoint = e.GetCurrentPoint(PreviewSurface);
        if (!pointerPoint.Properties.IsLeftButtonPressed)
        {
            FinishSubtitleOverlayDrag(e);
            return;
        }

        var left = SnapToCanvasGrid(pointerPoint.Position.X - _subtitleDragOffset.X);
        var top = SnapToCanvasGrid(pointerPoint.Position.Y - _subtitleDragOffset.Y);
        UpdateSubtitleAnchorFromLayout(left, top);
        _subtitlePosition = "custom";
        UpdateSubtitleButtons();
        ApplySubtitleOverlayPlacement();
        e.Handled = true;
    }

    private void OnSubtitleOverlayPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        FinishSubtitleOverlayDrag(e);
    }

    private void OnSubtitleOverlayPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        FinishSubtitleOverlayDrag(e);
    }

    private void FinishSubtitleOverlayDrag(PointerRoutedEventArgs e)
    {
        if (!_isDraggingSubtitleOverlay)
            return;

        _isDraggingSubtitleOverlay = false;
        SubtitleOverlayBackground.ReleasePointerCapture(e.Pointer);
        e.Handled = true;
        QueueProjectChange();
    }

    private void OnVideoResizeHandlePointerPressed(object sender, PointerRoutedEventArgs e) =>
        BeginResize(ResizeTarget.Video, sender as UIElement, e);

    private void OnTextResizeHandlePointerPressed(object sender, PointerRoutedEventArgs e) =>
        BeginResize(ResizeTarget.Text, sender as UIElement, e);

    private void OnSubtitleResizeHandlePointerPressed(object sender, PointerRoutedEventArgs e) =>
        BeginResize(ResizeTarget.Subtitle, sender as UIElement, e);

    private void BeginResize(ResizeTarget target, UIElement? captureElement, PointerRoutedEventArgs e)
    {
        if (PreviewSurface is null || captureElement is null)
            return;

        var pointerPoint = e.GetCurrentPoint(PreviewSurface);
        if (!pointerPoint.Properties.IsLeftButtonPressed)
            return;

        _resizeTarget = target;
        _isDraggingTextOverlay = false;
        _isDraggingSubtitleOverlay = false;
        _isDraggingVideoTransform = false;
        _resizeStartPoint = pointerPoint.Position;
        _resizeStartCenter = GetResizeCenter(target);
        _resizeStartDistance = Math.Max(24, Distance(_resizeStartPoint, _resizeStartCenter));
        _resizeStartTextSize = GetCurrentTextState().Size;
        _resizeStartTextBoxWidth = _textBoxWidth;
        _resizeStartSubtitleSize = _subtitleSize;
        _resizeStartSubtitleBoxWidth = _subtitleBoxWidth;
        _resizeStartVideoScale = _videoScale;
        _resizeHandleTag = (captureElement as FrameworkElement)?.Tag?.ToString()?.ToLowerInvariant() ?? string.Empty;
        _resizeCaptureElement = captureElement;
        captureElement.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnResizeHandlePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_resizeTarget == ResizeTarget.None || PreviewSurface is null)
            return;

        var pointerPoint = e.GetCurrentPoint(PreviewSurface);
        if (!pointerPoint.Properties.IsLeftButtonPressed)
        {
            FinishResize(e);
            return;
        }

        var isHorizontalBoxResize = _resizeHandleTag is "w" or "e"
            && _resizeTarget is ResizeTarget.Text or ResizeTarget.Subtitle;

        if (isHorizontalBoxResize)
        {
            ResizeTextBoxWidth(pointerPoint.Position);
        }
        else
        {
            var distance = Math.Max(1, Distance(pointerPoint.Position, _resizeStartCenter));
            var ratio = Math.Clamp(distance / _resizeStartDistance, 0.25, 4.0);

            switch (_resizeTarget)
            {
                case ResizeTarget.Video:
                    _videoScale = Math.Clamp(Math.Round(_resizeStartVideoScale * ratio * 100.0) / 100.0, 0.25, 3.0);
                    UpdateVideoTransformPreview();
                    break;
                case ResizeTarget.Text:
                    SetTextSize(SnapToCanvasGrid(_resizeStartTextSize * ratio));
                    break;
                case ResizeTarget.Subtitle:
                    SetSubtitleSize(SnapToCanvasGrid(_resizeStartSubtitleSize * ratio));
                    break;
            }
        }

        _suppressNextPreviewTap = true;
        e.Handled = true;
    }

    private void ResizeTextBoxWidth(Point pointerPosition)
    {
        var surfaceWidth = Math.Max(1, PreviewSurface.ActualWidth);
        var delta = SnapToCanvasGrid(pointerPosition.X - _resizeStartPoint.X);
        var direction = _resizeHandleTag == "w" ? -1 : 1;
        var minWidth = _resizeTarget == ResizeTarget.Subtitle ? 220 : 96;
        var maxWidth = Math.Max(minWidth, surfaceWidth - 96);

        if (_resizeTarget == ResizeTarget.Text)
        {
            var width = Math.Clamp(SnapToCanvasGrid(_resizeStartTextBoxWidth + direction * delta), minWidth, maxWidth);
            _textBoxWidth = width;
            var anchorX = Math.Clamp(SnapToCanvasGrid(_resizeStartCenter.X + delta / 2.0) / surfaceWidth, 0, 1);
            ApplyTextStateChange(GetCurrentTextState() with { AnchorX = anchorX });
            return;
        }

        if (_resizeTarget == ResizeTarget.Subtitle)
        {
            var width = Math.Clamp(SnapToCanvasGrid(_resizeStartSubtitleBoxWidth + direction * delta), minWidth, maxWidth);
            _subtitleBoxWidth = width;
            _subtitleAnchorX = Math.Clamp(SnapToCanvasGrid(_resizeStartCenter.X + delta / 2.0) / surfaceWidth, 0, 1);
            UpdateSubtitlePreview();
        }
    }

    private void OnResizeHandlePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        FinishResize(e);
    }

    private void OnResizeHandlePointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        FinishResize(e);
    }

    private void FinishResize(PointerRoutedEventArgs e)
    {
        if (_resizeTarget == ResizeTarget.None)
            return;

        _resizeCaptureElement?.ReleasePointerCapture(e.Pointer);
        _resizeCaptureElement = null;
        _resizeTarget = ResizeTarget.None;
        _resizeHandleTag = string.Empty;
        e.Handled = true;
        QueueProjectChange();
    }

    private void TogglePlayPause()
    {
        if (_reviewShuttleTimer?.IsEnabled == true)
        {
            PausePreview();
            UpdatePlaybackUi();
            return;
        }
        StopReviewShuttle();
        PauseSourceViewer();
        if (_project is null)
            return;

        if (_isPreviewPlaying)
        {
            PausePreview();
        }
        else
        {
            if (_previewPosition >= LayerTimeline.End - TimeSpan.FromMilliseconds(50))
                SeekTo(LayerTimeline.Start);

            var activeClip = GetActivePreviewClip();
            if (activeClip is not null && ((!_usingNativePreview && !_isPreviewOpening && File.Exists(activeClip.SourcePath)) || !string.Equals(
                    _nativePreviewMediaPath, ResolvePreviewMediaPath(activeClip.SourcePath), StringComparison.OrdinalIgnoreCase)))
                LoadPreviewClipAt(activeClip, _previewPosition);

            _isPreviewPlaying = true;
            _lastPreviewTick = 0;

            if (_usingNativePreview)
            {
                try { NativePreviewPlayer.MediaPlayer.Play(); }
                catch { /* faller tillbaka på den syntetiska klockan */ }
            }
            try
            {
                if (_activeTransitionPreviewClipId is not null)
                    TransitionPreviewPlayer.MediaPlayer.Play();
            }
            catch { }
            try
            {
                if (_activeOverlayPreviewClipId is not null)
                    OverlayPreviewPlayer.MediaPlayer.Play();
            }
            catch { }

            UpdateAudioTrackPreview(_previewPosition, seekExisting: true);

            _positionTimer.Start();
        }

        UpdatePlaybackUi();
    }

    private void OnLayerTimelineChanged(object sender, EditorTimelineChangedEventArgs e)
    {
        if (_isLoadingClip)
        {
            UpdateTrimUi();
            return;
        }

        _lastTimelinePreviewTime = e.PreviewTime;

        if (!LayerTimeline.IsDragging && e.ChangedLayer is EditorTimelineLayer.Audio or EditorTimelineLayer.Media)
        {
            _selectedAudioTrackId = LayerTimeline.SelectedAudioTrackId;
            SyncSelectedAudioTrackControls();
        }

        if (e.IsSelectionOnly)
        {
            var keepInspector = e.ChangedLayer == EditorTimelineLayer.Media &&
                _activeTool is "vfx" or "transitions";
            if (_activeTool != "elements" && !keepInspector)
                SelectToolForTimelineLayer(e.ChangedLayer);
            UpdateProjectChrome();
            UpdatePresetPickers();
            UpdateTrimUi();
            return;
        }

        if (e.IsSeekOnly)
        {
            PausePreview();
            SeekToThrottled(e.PreviewTime);
            return;
        }

        SyncTimelineEffectsToProject();

        if (e.ChangedLayer == EditorTimelineLayer.Text)
        {
            RetimeTextKeyframes(
                _textTimelineRangeStart,
                _textTimelineRangeEnd,
                e.TextStart,
                e.TextEnd);
            _textTimelineRangeStart = e.TextStart;
            _textTimelineRangeEnd = e.TextEnd;
        }

        if (e.ChangedLayer == EditorTimelineLayer.Media)
        {
            var selected = LayerTimeline.FindMediaClipById(LayerTimeline.SelectedMediaClipId);
            if (selected is not null)
            {
                _playbackSpeed = selected.PlaybackSpeed;
                UpdateSpeedButtons();
                if (!LayerTimeline.IsDragging)
                    UpdateProjectChrome();
            }
        }

        if (_isPreviewPlaying)
            PausePreview();
        RefreshPreviewAfterTimelineEdit();
        if (LayerTimeline.IsDragging && e.ChangedLayer is EditorTimelineLayer.Media or EditorTimelineLayer.Audio or EditorTimelineLayer.Vfx)
            return;
        var keepClipInspector = e.ChangedLayer == EditorTimelineLayer.Media &&
            _activeTool is "vfx" or "transitions";
        if (_activeTool != "elements" && !keepClipInspector)
            SelectToolForTimelineLayer(e.ChangedLayer);
        UpdatePresetPickers();
        SyncDisabledTimelineLayer(e);
        UpdateTrimUi();
        UpdatePreviewOverlays();
        if (e.ChangedLayer is not (EditorTimelineLayer.Media or EditorTimelineLayer.Audio))
            QueueProjectChange();
    }

    private void OnLayerTimelineDragCompleted(object? sender, EventArgs e)
    {
        _lastLiveSeekTicks = 0;
        _selectedAudioTrackId = LayerTimeline.SelectedAudioTrackId;
        SyncSelectedAudioTrackControls();
        if (LayerTimeline.LastDragWasSelectionOnly)
        {
            UpdateProjectChrome();
            UpdateTrimUi();
            return;
        }
        if (LayerTimeline.LastDragWasSeekOnly)
            SeekTo(_lastTimelinePreviewTime);
        else
            RefreshPreviewAfterTimelineEdit();
        if (_project is not null && LayerTimeline.ActiveLayer == EditorTimelineLayer.Element)
            SyncGraphicLayers(_project);
        if (_project is not null && LayerTimeline.ActiveLayer == EditorTimelineLayer.Audio)
            SyncAudioTracks(_project);
        if (!LayerTimeline.LastDragWasSeekOnly &&
            LayerTimeline.ActiveLayer is not (EditorTimelineLayer.Media or EditorTimelineLayer.Audio))
            QueueProjectChange();
    }

    private void OnMediaClipsChanged(object? sender, EditorMediaClipsChangedEventArgs e)
    {
        if (_project is null)
            return;

        _project.Clips = e.Clips.ToList();
        SyncTimelineEffectsToProject();
        _duration = TimeSpan.FromSeconds(_project.DurationSeconds);
        // During a drag the timeline already owns the current clip state.
        // Commit inspector/history/autosave work once on release.
        if (LayerTimeline.IsDragging)
            return;
        if (!_editingClipMetadata)
            UpdateProjectChrome();
        else
            RefreshPreviewProxyControls();
        QueueProjectChange();
        if (e.ChangeKind == EditorMediaChangeKind.Added)
            _ = BuildPreviewProxiesAsync(automatic: true);
    }

    private void OnMarkersChanged(object? sender, EditorMarkersChangedEventArgs e)
    {
        if (_project is null)
            return;

        _project.Markers = e.Markers.ToList();
        if (_editingMarkerMetadata)
            RefreshMarkerList();
        else
            SyncMarkerControls();
        QueueProjectChange();
    }

    private void OnMarkerSelected(object? sender, EditorMarkerSelectedEventArgs e)
    {
        SyncMarkerControls();
    }

    private void OnGraphicSegmentsChanged(object? sender, EditorGraphicSegmentsChangedEventArgs e)
    {
        if (_project is null)
            return;

        foreach (var updated in e.Segments)
        {
            var layer = _project.GraphicLayers.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, updated.Id, StringComparison.Ordinal));
            if (layer is null)
                continue;

            layer.StartSeconds = updated.StartSeconds;
            layer.EndSeconds = updated.EndSeconds;
            layer.Keyframes = updated.Keyframes.Select(keyframe => new EditorProjectGraphicKeyframe
            {
                TimeSeconds = keyframe.TimeSeconds,
                AnchorX = keyframe.AnchorX,
                AnchorY = keyframe.AnchorY,
                Width = keyframe.Width,
                Height = keyframe.Height,
                Opacity = keyframe.Opacity,
                Easing = keyframe.Easing
            }).ToList();
            layer.Normalize(_project.DurationSeconds, _project.CanvasWidth, _project.CanvasHeight);
        }

        _selectedGraphicLayerId = e.SelectedSegmentId;
        GraphicLayerList.SelectedItem = GetSelectedGraphicLayer();
        SyncSelectedGraphicControls();
        UpdateGraphicLayersPreview();
        QueueProjectChange();
    }

    private void OnGraphicSegmentSelected(object? sender, EditorGraphicSegmentSelectedEventArgs e)
    {
        if (_project is null)
            return;

        var layer = _project.GraphicLayers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, e.SegmentId, StringComparison.Ordinal));
        if (layer is null)
            return;

        _selectedGraphicLayerId = layer.Id;
        GraphicLayerList.SelectedItem = layer;
        LayerTimeline.SelectGraphicSegment(layer.Id);
        SyncSelectedGraphicControls();
        UpdateGraphicLayersPreview();
        SetEditorTool("elements");
        UpdateProjectChrome();
    }

    private async void OnLibraryClipDropped(object? sender, EditorLibraryClipDropEventArgs e)
    {
        var destinationProject = _project;
        var position = e.TimelinePosition;
        var addedCount = 0;
        var reusedCount = 0;
        var convertedCount = 0;
        foreach (var filePath in e.FilePaths)
        {
            if (!ReferenceEquals(_project, destinationProject))
                break;
            var projectPath = filePath;
            var isLibraryItem = LibraryItems.Any(item =>
                string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (!isLibraryItem && MediaImportHandler is not null)
            {
                try
                {
                    var progress = new Progress<ClipImportProgress>(value =>
                    {
                        var percent = value.Fraction is { } fraction
                            ? $" {Math.Round(fraction * 100):0}%"
                            : string.Empty;
                        ProjectStatusText.Text = $"{value.Stage}{percent}";
                    });
                    var result = await MediaImportHandler(filePath, progress, CancellationToken.None);
                    projectPath = result.FilePath;
                    if (result.WasDuplicate)
                        reusedCount++;
                    if (result.Kind == ClipImportKind.Transcoded)
                        convertedCount++;
                }
                catch (Exception ex)
                {
                    ProjectStatusText.Text = $"Could not import {Path.GetFileName(filePath)}";
                    Debug.WriteLine($"[Klipp] External timeline import failed: {ex}");
                    continue;
                }
            }

            var addedDuration = await TryAddLibraryClipAsync(projectPath, e.Track, position, timelineTrackId: e.TrackId);
            if (addedDuration is null)
                continue;

            addedCount++;
            position += addedDuration.Value;
        }

        ProjectStatusText.Text = addedCount switch
        {
            0 => "No supported clips were added",
            1 when convertedCount > 0 => "Clip converted and added to timeline",
            1 when reusedCount > 0 => "Existing library clip added to timeline",
            1 => "Clip added to timeline",
            _ when convertedCount > 0 || reusedCount > 0 =>
                $"{addedCount} clips added · {convertedCount} converted · {reusedCount} reused",
            _ => $"{addedCount} clips added to timeline"
        };
    }

    private void OnLibraryDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        if (e.Items.FirstOrDefault() is not EditorLibraryItem item)
            return;

        e.Data.SetData(LayerTimelineEditor.LibraryClipDataFormat, item.FilePath);
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void OnLibraryItemDragStarting(UIElement sender, DragStartingEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string filePath })
            return;

        e.Data.SetData(LayerTimelineEditor.LibraryClipDataFormat, filePath);
        e.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private async void OnAddLibraryClipClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string filePath })
            await TryAddLibraryClipAsync(filePath, EditorClipTrack.Primary, _duration);
    }

    private async void OnAddLibraryOverlayClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string filePath })
            await TryAddLibraryClipAsync(filePath, EditorClipTrack.Overlay, _previewPosition);
    }

    private void OnToggleLibraryFavoriteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string filePath })
            LibraryFavoriteToggleRequested?.Invoke(this, new EditorLibraryItemEventArgs(filePath));
    }

    private void OnOrganizeLibraryItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string filePath })
            LibraryOrganizeRequested?.Invoke(this, new EditorLibraryItemEventArgs(filePath));
    }

    private async Task<TimeSpan?> TryAddLibraryClipAsync(string filePath, EditorClipTrack track, TimeSpan position,
        double? sourceInSeconds = null, double? sourceOutSeconds = null, string? timelineTrackId = null)
    {
        var destinationProject = _project;
        await _addClipGate.WaitAsync();
        try
        {
            if (destinationProject is null || !ReferenceEquals(_project, destinationProject) || !File.Exists(filePath))
                return null;

            var libraryItem = LibraryItems.FirstOrDefault(item =>
                string.Equals(item.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            var duration = await GetLibraryClipDurationAsync(filePath, libraryItem);
            if (duration is null || duration <= TimeSpan.FromSeconds(0.1))
                return null;

            var clip = new EditorProjectClip
            {
                SourcePath = filePath,
                Name = libraryItem?.Title ?? Path.GetFileNameWithoutExtension(filePath),
                SourceDurationSeconds = duration.Value.TotalSeconds,
                TrimEndSeconds = duration.Value.TotalSeconds,
                Track = track,
                TrackId = timelineTrackId ?? string.Empty
            };
            clip.TrimStartSeconds = Math.Clamp(sourceInSeconds ?? 0, 0, duration.Value.TotalSeconds - 0.1);
            clip.TrimEndSeconds = Math.Clamp(sourceOutSeconds ?? duration.Value.TotalSeconds,
                clip.TrimStartSeconds + 0.1, duration.Value.TotalSeconds);
            var sourceAudioStreams = await GetSourceAudioStreamsAsync(filePath);
            if (!ReferenceEquals(_project, destinationProject))
                return null;
            // The timeline stores a clone, so mark source audio ownership before
            // adding it and create its independent segment at the accepted position.
            clip.SourceAudioSeparated = sourceAudioStreams is not null;
            CommitPendingProjectChange();
            PausePreview();
            if (!LayerTimeline.AddMediaClip(clip, track, position))
                return null;
            if (_project is not null && sourceAudioStreams is not null)
            {
                var addedClip = _project.FindClip(LayerTimeline.SelectedMediaClipId);
                if (addedClip is not null)
                {
                    addedClip.SourceAudioSeparated = false;
                    EditorSourceAudioService.SeparateClipAudioStreams(_project, addedClip, sourceAudioStreams);
                    SyncAudioTracks(_project);
                    UpdateProjectChrome();
                }
            }
            QueueAudioSignalAnalyses([clip.SourcePath]);
            QueueProjectChange();
            return TimeSpan.FromSeconds(clip.EffectiveDurationSeconds);
        }
        catch (Exception ex)
        {
            ProjectStatusText.Text = "The clip could not be added";
            Debug.WriteLine($"[Klipp] Kunde inte lägga till projektklipp: {ex}");
        }
        finally
        {
            _addClipGate.Release();
        }

        return null;
    }

    private async Task<TimeSpan?> GetLibraryClipDurationAsync(string filePath, EditorLibraryItem? libraryItem)
    {
        if (_libraryDurationCache.TryGetValue(filePath, out var cached) &&
            cached > TimeSpan.FromSeconds(0.1))
        {
            return cached;
        }

        if (libraryItem?.Duration is { } listedDuration &&
            listedDuration > TimeSpan.FromSeconds(0.1))
        {
            _libraryDurationCache[filePath] = listedDuration;
            return listedDuration;
        }

        var probedDuration = await _mediaProbeService.GetDurationAsync(filePath);
        if (probedDuration is { } duration && duration > TimeSpan.FromSeconds(0.1))
            _libraryDurationCache[filePath] = duration;

        return probedDuration;
    }

    private void OnSplitClipClicked(object sender, RoutedEventArgs e)
    {
        CommitPendingProjectChange();
        PausePreview();
        if (LayerTimeline.SplitAtPlayhead())
            QueueProjectChange();
        else
            ProjectStatusText.Text = LayerTimeline.LastEditStatus;
    }

    private void OnDeleteTimelineClipClicked(object sender, RoutedEventArgs e)
    {
        CommitPendingProjectChange();
        PausePreview();
        if (LayerTimeline.SelectedEffectLayerId is not null && LayerTimeline.DeleteSelectedEffectLayer())
        {
            QueueProjectChange();
            return;
        }
        if (LayerTimeline.SelectedAudioTrackId is not null && LayerTimeline.DeleteSelectedAudioTrack())
        {
            QueueProjectChange();
            return;
        }
        if (LayerTimeline.DeleteSelectedMarker())
        {
            QueueProjectChange();
            return;
        }

        if (LayerTimeline.ActiveLayer == EditorTimelineLayer.Element &&
            GetSelectedGraphicLayer() is { } graphic)
        {
            RemoveGraphicLayer(graphic.Id);
            return;
        }

        if (LayerTimeline.DeleteSelectedMediaClips() || LayerTimeline.DeleteSelectedLayer())
            QueueProjectChange();
    }

    private void OnDuplicateTimelineClipClicked(object sender, RoutedEventArgs e)
    {
        PausePreview();
        if (LayerTimeline.SelectedEffectLayerId is not null && LayerTimeline.DuplicateSelectedEffectLayer())
        {
            QueueProjectChange();
            RevealSelectedTimelineEffect();
            return;
        }
        if (LayerTimeline.ActiveLayer == EditorTimelineLayer.Element && DuplicateSelectedGraphicLayer())
            return;

        if (LayerTimeline.DuplicateSelectedMediaClips())
            QueueProjectChange();
    }

    private void OnEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_fxPreviewDragId is not null)
        {
            if (e.Key == Windows.System.VirtualKey.Escape) EndTimelineFxPreviewGesture(commit: false);
            e.Handled = true;
            return;
        }
        var modifiers = GetEditorShortcutModifiers();
        var binding = EditorShortcutCatalog.FindMatch(_shortcuts, modifiers, (uint)e.Key);
        if (e.Key == Windows.System.VirtualKey.F1 && binding?.Action == EditorShortcutActions.ShowShortcuts)
        {
            OnEditorShortcutsClicked(sender, e);
            e.Handled = true;
            return;
        }
        var focused = FocusManager.GetFocusedElement(XamlRoot);
        if (focused is TextBox or RichEditBox or PasswordBox or AutoSuggestBox or NumberBox)
            return;

        if (focused is ComboBox { IsDropDownOpen: true }) return;
        if (modifiers == 0 && focused is (Button or ComboBox or ToggleSwitch or Slider or CheckBox or Microsoft.UI.Xaml.Controls.Primitives.ToggleButton) &&
            binding?.Action is not (EditorShortcutActions.ReviewReverse or EditorShortcutActions.ReviewPause or EditorShortcutActions.ReviewForward))
            return;

        if (modifiers == 0 && e.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)
        {
            SeekToAdjacentEdit(next: e.Key == Windows.System.VirtualKey.Down);
            e.Handled = true;
            return;
        }

        if (binding is null)
            return;

        switch (binding.Action)
        {
            case EditorShortcutActions.Undo:
                PausePreview();
                UndoProjectChange();
                break;
            case EditorShortcutActions.Redo:
                PausePreview();
                RedoProjectChange();
                break;
            case EditorShortcutActions.Duplicate:
                OnDuplicateTimelineClipClicked(sender, e);
                break;
            case EditorShortcutActions.Split:
                OnSplitClipClicked(sender, e);
                break;
            case EditorShortcutActions.Delete:
                OnDeleteTimelineClipClicked(sender, e);
                break;
            case EditorShortcutActions.PlayPause:
                TogglePlayPause();
                break;
            case EditorShortcutActions.ReviewReverse:
                StartReviewShuttle(-1);
                break;
            case EditorShortcutActions.ReviewPause:
                PausePreview();
                UpdatePlaybackUi();
                break;
            case EditorShortcutActions.ReviewForward:
                StartReviewShuttle(1);
                break;
            case EditorShortcutActions.AddMarker:
                OnAddMarkerClicked(sender, e);
                break;
            case EditorShortcutActions.OpenLibrary:
                SetEditorTool("library");
                break;
            case EditorShortcutActions.Copy:
                OnCopyTimelineSelectionClicked(sender, e);
                break;
            case EditorShortcutActions.Cut:
                OnCutTimelineSelectionClicked(sender, e);
                break;
            case EditorShortcutActions.Paste:
                OnPasteTimelineSelectionClicked(sender, e);
                break;
            case EditorShortcutActions.RippleDelete:
                OnRippleDeleteClicked(sender, e);
                break;
            case EditorShortcutActions.GoToTimecode:
                OnGoToTimecodeClicked(sender, e);
                break;
            case EditorShortcutActions.ShowShortcuts:
                OnEditorShortcutsClicked(sender, e);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private static uint GetEditorShortcutModifiers()
    {
        var modifiers = EditorShortcutModifiers.None;
        if (IsEditorKeyDown(Windows.System.VirtualKey.Control))
            modifiers |= EditorShortcutModifiers.Control;
        if (IsEditorKeyDown(Windows.System.VirtualKey.Shift))
            modifiers |= EditorShortcutModifiers.Shift;
        if (IsEditorKeyDown(Windows.System.VirtualKey.Menu))
            modifiers |= EditorShortcutModifiers.Alt;
        return (uint)modifiers;
    }

    private static bool IsEditorKeyDown(Windows.System.VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void UpdateShortcutToolTips()
    {
        SetShortcutToolTip(UndoTimelineButton, "Undo", EditorShortcutActions.Undo);
        SetShortcutToolTip(RedoTimelineButton, "Redo", EditorShortcutActions.Redo);
        SetShortcutToolTip(SplitClipButton, "Split at playhead", EditorShortcutActions.Split);
        SetShortcutToolTip(DuplicateClipButton, "Duplicate selection", EditorShortcutActions.Duplicate);
        SetShortcutToolTip(DeleteClipButton, "Delete selection", EditorShortcutActions.Delete);
        SetShortcutToolTip(TimelinePlayButton, "Play or pause", EditorShortcutActions.PlayPause);
        var reverseReview = EditorShortcutCatalog.FormatGesture(_shortcuts.First(binding => binding.Action == EditorShortcutActions.ReviewReverse));
        var pauseReview = EditorShortcutCatalog.FormatGesture(_shortcuts.First(binding => binding.Action == EditorShortcutActions.ReviewPause));
        var forwardReview = EditorShortcutCatalog.FormatGesture(_shortcuts.First(binding => binding.Action == EditorShortcutActions.ReviewForward));
        var playPauseReview = EditorShortcutCatalog.FormatGesture(_shortcuts.First(binding => binding.Action == EditorShortcutActions.PlayPause));
        ToolTipService.SetToolTip(TimelinePlayButton,
            $"Play or pause ({playPauseReview})\nReview: {reverseReview} backward · {pauseReview} pause · {forwardReview} forward\nPress again for 2×, 4× or 8×. Fast and reverse review are silent.");
        SetShortcutToolTip(AddMarkerButton, "Add marker", EditorShortcutActions.AddMarker);
        SetShortcutToolTip(LibraryToolButton, "Open media", EditorShortcutActions.OpenLibrary);
        SetShortcutToolTip(TimecodeJumpButton, "Go to timecode", EditorShortcutActions.GoToTimecode);
        SetShortcutToolTip(EditorShortcutsButton, "Editor shortcuts", EditorShortcutActions.ShowShortcuts);
        LayerTimeline.ConfigureClipboardGestures(
            EditorShortcutCatalog.FormatGesture(_shortcuts.First(binding => binding.Action == EditorShortcutActions.Copy)),
            EditorShortcutCatalog.FormatGesture(_shortcuts.First(binding => binding.Action == EditorShortcutActions.Cut)),
            EditorShortcutCatalog.FormatGesture(_shortcuts.First(binding => binding.Action == EditorShortcutActions.Paste)));
    }

    private void SetShortcutToolTip(FrameworkElement element, string label, string action)
    {
        var binding = _shortcuts.First(item => item.Action == action);
        ToolTipService.SetToolTip(element, $"{label} ({EditorShortcutCatalog.FormatGesture(binding)})");
    }

    private void SelectToolForTimelineLayer(EditorTimelineLayer layer)
    {
        switch (layer)
        {
            case EditorTimelineLayer.Text:
                SetEditorTool("text");
                break;
            case EditorTimelineLayer.Vfx:
                SetEditorTool("vfx");
                break;
            case EditorTimelineLayer.Audio:
                SetEditorTool("audio");
                break;
            case EditorTimelineLayer.Subtitle:
                SetEditorTool("subtitles");
                break;
            case EditorTimelineLayer.Element:
                SetEditorTool("elements");
                break;
            case EditorTimelineLayer.Trim:
            case EditorTimelineLayer.Marker:
            case EditorTimelineLayer.Media:
                SetEditorTool("general");
                break;
        }
    }

    private void SyncDisabledTimelineLayer(EditorTimelineChangedEventArgs e)
    {
        _syncingTimelineState = true;
        try
        {
            if (e.ChangedLayer == EditorTimelineLayer.Text && !e.TextEnabled && TextOverlayBox is not null)
            {
                TextOverlayBox.Text = string.Empty;
                _textKeyframes.Clear();
                SyncTextKeyframesToTimeline();
            }

            if (e.ChangedLayer == EditorTimelineLayer.Subtitle && !e.SubtitleEnabled && SubtitleTextBox is not null)
                SubtitleTextBox.Text = string.Empty;

            if (e.ChangedLayer == EditorTimelineLayer.Vfx && !e.VfxEnabled)
            {
                _videoFilter = "none";
                UpdateFilterButtons();
                UpdateVfxPreview();
            }

            if (e.ChangedLayer == EditorTimelineLayer.Element && !e.ElementEnabled)
            {
                _elementOverlay = "none";
                UpdateElementButtons();
                UpdateElementPreview();
            }
        }
        finally
        {
            _syncingTimelineState = false;
        }
    }

    private void RetimeTextKeyframes(
        TimeSpan oldStart,
        TimeSpan oldEnd,
        TimeSpan newStart,
        TimeSpan newEnd)
    {
        if (_textKeyframes.Count == 0 || (oldStart == newStart && oldEnd == newEnd))
            return;

        var oldDuration = oldEnd - oldStart;
        var newDuration = newEnd - newStart;
        var startDelta = (newStart - oldStart).TotalSeconds;
        var endDelta = (newEnd - oldEnd).TotalSeconds;
        if (Math.Abs((newDuration - oldDuration).TotalSeconds) < 0.0001 &&
            Math.Abs(startDelta - endDelta) < 0.0001)
        {
            foreach (var keyframe in _textKeyframes)
                keyframe.TimeSeconds += startDelta;
        }
        else
        {
            foreach (var keyframe in _textKeyframes)
                keyframe.TimeSeconds = Math.Clamp(keyframe.TimeSeconds, newStart.TotalSeconds, newEnd.TotalSeconds);
        }

        _textKeyframes = _textKeyframes
            .OrderBy(keyframe => keyframe.TimeSeconds)
            .GroupBy(keyframe => Math.Round(keyframe.TimeSeconds * 120) / 120)
            .Select(group => group.Last())
            .ToList();
        SyncTextKeyframesToTimeline();
        UpdateTextKeyframeControls();
    }

    private void OnSeekStartClicked(object sender, RoutedEventArgs e)
    {
        PausePreview();
        SeekTo(LayerTimeline.Start);
    }

    private void OnSeekEndClicked(object sender, RoutedEventArgs e)
    {
        PausePreview();
        SeekTo(LayerTimeline.End);
    }

    private void OnResetTrimClicked(object sender, RoutedEventArgs e)
    {
        PausePreview();
        LayerTimeline.ResetTrimRange();
        SeekTo(TimeSpan.Zero);
        UpdateTrimUi();
    }

    private void OnTimelineZoomOutClicked(object sender, RoutedEventArgs e)
    {
        LayerTimeline.ZoomOut();
        UpdateTimelineChrome();
    }

    private void OnTimelineZoomResetClicked(object sender, RoutedEventArgs e)
    {
        LayerTimeline.ResetZoom();
        UpdateTimelineChrome();
    }

    private void OnTimelineZoomInClicked(object sender, RoutedEventArgs e)
    {
        LayerTimeline.ZoomIn();
        UpdateTimelineChrome();
    }

    private void OnTimelineUndoClicked(object sender, RoutedEventArgs e)
    {
        PausePreview();
        UndoProjectChange();
    }

    private void OnTimelineRedoClicked(object sender, RoutedEventArgs e)
    {
        PausePreview();
        RedoProjectChange();
    }

    private void OnTimelineHistoryChanged(object? sender, EventArgs e)
    {
        UpdateProjectChrome();
    }

    private void OnGeneralToolClicked(object sender, RoutedEventArgs e)
    {
        SetEditorTool("general");
        JumpToClipInspectorSection("clip");
    }
    private void OnLibraryToolClicked(object sender, RoutedEventArgs e) => SetEditorTool("library");
    private void OnTextToolClicked(object sender, RoutedEventArgs e) => SetEditorTool("text");
    private void OnElementsToolClicked(object sender, RoutedEventArgs e) => SetEditorTool("elements");
    private void OnVfxToolClicked(object sender, RoutedEventArgs e)
    {
        if (LayerTimeline.SelectedMediaClipIds.Count == 0 && GetActivePreviewClip() is { } active)
            LayerTimeline.SelectMediaClipForEditing(active.Id);
        SetEditorTool("vfx");
    }
    private void OnTransitionsToolClicked(object sender, RoutedEventArgs e)
    {
        SelectTransitionNearPlayhead();
        SetEditorTool("transitions");
        UpdateTransitionButtons();
    }
    private void OnAudioToolClicked(object sender, RoutedEventArgs e) => SetEditorTool("audio");
    private void OnSubtitlesToolClicked(object sender, RoutedEventArgs e) => SetEditorTool("subtitles");

    private void OnClipInspectorJumpClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string section })
            JumpToClipInspectorSection(section);
    }

    private void JumpToClipInspectorSection(string section)
    {
        _generalInspectorSection = section;
        ApplyGeneralInspectorSection();
        InspectorScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void OnAddMarkerClicked(object sender, RoutedEventArgs e)
    {
        CommitPendingProjectChange();
        LayerTimeline.AddMarker(CurrentTimelinePosition);
        ProjectStatusText.Text = "Marker added";
    }

    private void OnMarkerListItemClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string markerId })
            LayerTimeline.SelectMarker(markerId);
    }

    private void OnSelectedMarkerMetadataChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingMarkerControls)
            return;

        var selected = LayerTimeline.GetSelectedMarker();
        if (selected is null)
            return;

        _editingMarkerMetadata = true;
        try
        {
            LayerTimeline.SetSelectedMarkerMetadata(
                SelectedMarkerLabelBox.Text,
                SelectedMarkerNotesBox.Text,
                selected.ColorLabel);
        }
        finally
        {
            _editingMarkerMetadata = false;
        }
    }

    private void OnMarkerColorClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string colorLabel })
            return;

        var selected = LayerTimeline.GetSelectedMarker();
        if (selected is null)
            return;

        _editingMarkerMetadata = true;
        try
        {
            LayerTimeline.SetSelectedMarkerMetadata(
                SelectedMarkerLabelBox.Text,
                SelectedMarkerNotesBox.Text,
                colorLabel);
        }
        finally
        {
            _editingMarkerMetadata = false;
        }
        SyncMarkerControls();
    }

    private void OnDeleteMarkerClicked(object sender, RoutedEventArgs e)
    {
        CommitPendingProjectChange();
        if (LayerTimeline.DeleteSelectedMarker())
            ProjectStatusText.Text = "Marker deleted";
    }

    private void OnLayerMoveBackClicked(object sender, RoutedEventArgs e) =>
        AdjustSelectedLayer(() => LayerTimeline.MoveSelectedRange(TimeSpan.FromTicks(-LayerTimeline.TimelineStep.Ticks)));

    private void OnLayerMoveForwardClicked(object sender, RoutedEventArgs e) =>
        AdjustSelectedLayer(() => LayerTimeline.MoveSelectedRange(LayerTimeline.TimelineStep));

    private void OnLayerStartBackClicked(object sender, RoutedEventArgs e) =>
        AdjustSelectedLayer(() => LayerTimeline.TrimSelectedStart(TimeSpan.FromTicks(-LayerTimeline.TimelineStep.Ticks)));

    private void OnLayerStartForwardClicked(object sender, RoutedEventArgs e) =>
        AdjustSelectedLayer(() => LayerTimeline.TrimSelectedStart(LayerTimeline.TimelineStep));

    private void OnLayerEndBackClicked(object sender, RoutedEventArgs e) =>
        AdjustSelectedLayer(() => LayerTimeline.TrimSelectedEnd(TimeSpan.FromTicks(-LayerTimeline.TimelineStep.Ticks)));

    private void OnLayerEndForwardClicked(object sender, RoutedEventArgs e) =>
        AdjustSelectedLayer(() => LayerTimeline.TrimSelectedEnd(LayerTimeline.TimelineStep));

    private void OnSpeed05Clicked(object sender, RoutedEventArgs e) => SetPlaybackSpeed(0.5);
    private void OnSpeed1Clicked(object sender, RoutedEventArgs e) => SetPlaybackSpeed(1.0);
    private void OnSpeed15Clicked(object sender, RoutedEventArgs e) => SetPlaybackSpeed(1.5);
    private void OnSpeed2Clicked(object sender, RoutedEventArgs e) => SetPlaybackSpeed(2.0);

    private void OnPreviewOriginalClicked(object sender, RoutedEventArgs e) => SetPreviewQuality("original");
    private void OnPreview720pClicked(object sender, RoutedEventArgs e) => SetPreviewQuality("720p");
    private void OnPreview540pClicked(object sender, RoutedEventArgs e) => SetPreviewQuality("540p");

    private async void OnBuildPreviewProxiesClicked(object sender, RoutedEventArgs e)
    {
        await BuildPreviewProxiesAsync(automatic: false);
    }

    private async Task BuildPreviewProxiesAsync(bool automatic)
    {
        if (PreviewProxyQuality.Normalize(_previewQuality) == "original")
            return;
        if (_isBuildingPreviewProxies)
        {
            _previewProxyBuildQueued = true;
            return;
        }
        _previewProxyBuildQueued = false;

        var sources = GetProjectProxySources();
        if (sources.Count == 0 || sources.All(path => _previewProxyService.TryGetExistingProxy(path, _previewQuality, out _)))
        {
            RefreshPreviewProxyControls();
            return;
        }

        _previewProxyCts?.Cancel();
        var proxyCts = new CancellationTokenSource();
        var project = _project;
        var loadVersion = _projectLoadVersion;
        var quality = _previewQuality;
        var completed = false;
        _previewProxyCts = proxyCts;
        _isBuildingPreviewProxies = true;
        _temporarilyUseOriginalPreview = false;
        _previewProxyStatusOverride = null;
        RefreshPreviewProxyControls();
        PreviewProxyProgressBar.Visibility = Visibility.Visible;
        PreviewProxyProgressBar.Value = 0;

        try
        {
            for (var index = 0; index < sources.Count; index++)
            {
                proxyCts.Token.ThrowIfCancellationRequested();
                var sourcePath = sources[index];
                var currentIndex = index;
                PreviewProxyStatusText.Text =
                    $"Building {currentIndex + 1}/{sources.Count}: {Path.GetFileName(sourcePath)}";
                var progress = new Progress<double>(fraction =>
                {
                    if (proxyCts.IsCancellationRequested || !ReferenceEquals(_previewProxyCts, proxyCts) ||
                        !ReferenceEquals(_project, project) || loadVersion != _projectLoadVersion)
                        return;
                    var totalFraction = (currentIndex + Math.Clamp(fraction, 0, 1)) / sources.Count;
                    PreviewProxyProgressBar.Value = totalFraction * 100;
                    QuickPreviewQualityText.Text = $"{quality} · {totalFraction:P0}";
                });
                await _previewProxyService.GetOrCreateProxyAsync(
                    sourcePath,
                    quality,
                    progress,
                    proxyCts.Token,
                    priority: automatic ? BackgroundWorkPriority.Background : BackgroundWorkPriority.Interactive);
            }

            if (ReferenceEquals(_project, project) && loadVersion == _projectLoadVersion)
                ProjectStatusText.Text = "Preview proxies ready";
            completed = true;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_previewProxyCts, proxyCts))
            {
                ProjectStatusText.Text = "Proxy build canceled";
                _previewProxyStatusOverride = "Build canceled. Completed proxies remain available.";
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_previewProxyCts, proxyCts))
            {
                ProjectStatusText.Text = "Proxy build failed";
                _previewProxyStatusOverride = $"Build failed: {FormatProxyError(ex.Message)}";
            }
            Debug.WriteLine($"[Klipp] Proxy build failed: {ex}");
        }
        finally
        {
            if (ReferenceEquals(_previewProxyCts, proxyCts))
            {
                _previewProxyCts = null;
                _isBuildingPreviewProxies = false;
                _temporarilyUseOriginalPreview = false;
                if (!_isPreviewPlaying)
                    ReloadPreviewSourcesForQuality();
                RefreshPreviewProxyControls();
                if (completed && _previewProxyBuildQueued && !proxyCts.IsCancellationRequested)
                {
                    _previewProxyBuildQueued = false;
                    _ = BuildPreviewProxiesAsync(automatic: true);
                }
            }
            proxyCts.Dispose();
        }
    }

    private void OnCancelPreviewProxiesClicked(object sender, RoutedEventArgs e) =>
        _previewProxyCts?.Cancel();

    private async void OnRemovePreviewProxiesClicked(object sender, RoutedEventArgs e)
    {
        if (_isBuildingPreviewProxies || PreviewProxyQuality.Normalize(_previewQuality) == "original")
            return;

        var sources = GetProjectProxySources();
        var project = _project;
        var quality = _previewQuality;
        var loadVersion = _projectLoadVersion;
        _temporarilyUseOriginalPreview = true;
        ReloadPreviewSourcesForQuality();
        await Task.Delay(120);
        if (!ReferenceEquals(_project, project) || loadVersion != _projectLoadVersion || quality != _previewQuality)
            return;

        var removed = sources.Count(sourcePath =>
            _previewProxyService.DeleteProxy(sourcePath, quality));
        ProjectStatusText.Text = removed == 0
            ? "No project proxies to remove"
            : $"Removed {removed} project prox{(removed == 1 ? "y" : "ies")}";
        _previewProxyStatusOverride = removed == 0
            ? "No cached proxies were found for this project."
            : $"Removed {removed} cached project prox{(removed == 1 ? "y" : "ies")}.";
        RefreshPreviewProxyControls();
    }

    private void SetPreviewQuality(string quality)
    {
        var normalized = PreviewProxyQuality.Normalize(quality);
        if (_previewQuality == normalized && !_temporarilyUseOriginalPreview)
        {
            RefreshPreviewProxyControls();
            return;
        }

        _previewProxyCts?.Cancel();
        _previewProxyCts = null;
        _isBuildingPreviewProxies = false;
        _previewQuality = normalized;
        _temporarilyUseOriginalPreview = false;
        _previewProxyStatusOverride = null;
        ReloadPreviewSourcesForQuality();
        RefreshPreviewProxyControls();
        QueueProjectChange();
        _ = BuildPreviewProxiesAsync(automatic: true);
    }

    private List<string> GetProjectProxySources() => _project?.Clips
        .Select(clip => clip.SourcePath)
        .Where(File.Exists)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToList() ?? [];

    private void ReloadPreviewSourcesForQuality()
    {
        if (_project is null)
            return;

        var resumePlayback = _isPreviewPlaying;
        PausePreview();
        var activeClip = GetActivePreviewClip() ??
                         LayerTimeline.FindMediaClipAt(_previewPosition, EditorClipTrack.Primary) ??
                         _project.PrimaryClips.FirstOrDefault(clip => File.Exists(clip.SourcePath));
        if (activeClip is not null)
        {
            _nativePreviewMediaPath = null;
            LoadPreviewClipAt(activeClip, _previewPosition);
        }

        UpdateTransitionPreview(_previewPosition, seekExisting: true);
        UpdateOverlayPreview(_previewPosition, seekExisting: true);

        if (resumePlayback)
        {
            _isPreviewPlaying = true;
            _lastPreviewTick = 0;
            try { NativePreviewPlayer.MediaPlayer.Play(); } catch { }
            try { TransitionPreviewPlayer.MediaPlayer.Play(); } catch { }
            try { OverlayPreviewPlayer.MediaPlayer.Play(); } catch { }
            _positionTimer.Start();
        }

        UpdatePlaybackUi();
    }

    private void RefreshPreviewProxyControls()
    {
        if (PreviewOriginalButton is null)
            return;

        var quality = PreviewProxyQuality.Normalize(_previewQuality);
        QuickPreviewQualityText.Text = quality == "original" ? "Preview · Original" : $"Preview · {quality}";
        SetOptionButtonState(PreviewOriginalButton, quality == "original");
        SetOptionButtonState(Preview720pButton, quality == "720p");
        SetOptionButtonState(Preview540pButton, quality == "540p");

        var sources = GetProjectProxySources();
        var readyCount = quality == "original"
            ? sources.Count
            : sources.Count(sourcePath =>
                _previewProxyService.TryGetExistingProxy(sourcePath, quality, out _));
        var proxyBytes = quality == "original"
            ? 0
            : sources.Sum(sourcePath => _previewProxyService.GetExistingProxySize(sourcePath, quality));
        PreviewProxyReadyText.Text = quality == "original"
            ? "Original"
            : $"{readyCount}/{sources.Count}";

        if (!_isBuildingPreviewProxies)
        {
            PreviewProxyStatusText.Text = _previewProxyStatusOverride ??
                (quality == "original"
                    ? "Original media is decoded. Export always uses originals."
                    : $"{readyCount}/{sources.Count} ready | {FormatProxyBytes(proxyBytes)} | export uses originals.");
        }

        BuildPreviewProxiesButton.IsEnabled =
            !_isBuildingPreviewProxies && quality != "original" && readyCount < sources.Count;
        CancelPreviewProxiesButton.IsEnabled = _isBuildingPreviewProxies;
        RemovePreviewProxiesButton.IsEnabled =
            !_isBuildingPreviewProxies && quality != "original" && readyCount > 0;
        PreviewOriginalButton.IsEnabled = !_isBuildingPreviewProxies;
        Preview720pButton.IsEnabled = !_isBuildingPreviewProxies;
        Preview540pButton.IsEnabled = !_isBuildingPreviewProxies;
        PreviewProxyProgressBar.Visibility = _isBuildingPreviewProxies
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string FormatProxyBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 MB";
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024d * 1024 * 1024):0.0} GB";
        return $"{Math.Max(0.1, bytes / (1024d * 1024)):0.0} MB";
    }

    private static string FormatProxyError(string message)
    {
        var firstLine = message
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim() ?? "Unknown FFmpeg error";
        return firstLine.Length <= 140 ? firstLine : firstLine[..137] + "...";
    }

    private void OnCanvasWideClicked(object sender, RoutedEventArgs e) => SetCanvasPreset(1280, 720);
    private void OnCanvasVerticalClicked(object sender, RoutedEventArgs e) => SetCanvasPreset(720, 1280);
    private void OnCanvasSquareClicked(object sender, RoutedEventArgs e) => SetCanvasPreset(1080, 1080);
    private void OnCanvasPortraitClicked(object sender, RoutedEventArgs e) => SetCanvasPreset(1080, 1350);
    private void OnExportDraftClicked(object sender, RoutedEventArgs e) => SetExportQuality("draft");
    private void OnExportHighClicked(object sender, RoutedEventArgs e) => SetExportQuality("high");
    private void OnExportMaxClicked(object sender, RoutedEventArgs e) => SetExportQuality("max");
    private void OnExport30FpsClicked(object sender, RoutedEventArgs e) => SetExportFrameRate(30);
    private void OnExport60FpsClicked(object sender, RoutedEventArgs e) => SetExportFrameRate(60);
    private void OnExportCanvasResolutionClicked(object sender, RoutedEventArgs e) => SetExportResolutionPreset("canvas");
    private void OnExport1080pResolutionClicked(object sender, RoutedEventArgs e) => SetExportResolutionPreset("1080p");
    private void OnExport1440pResolutionClicked(object sender, RoutedEventArgs e) => SetExportResolutionPreset("1440p");
    private void OnExport2160pResolutionClicked(object sender, RoutedEventArgs e) => SetExportResolutionPreset("2160p");

    private void OnExportEncoderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingExportControls || ExportEncoderComboBox.SelectedItem is not ComboBoxItem item)
            return;

        var profile = FFmpegEncoderService.NormalizeProfile(item.Tag as string);
        if (_exportEncoder == profile)
            return;

        _exportEncoder = profile;
        UpdateExportEncoderStatus();
        QueueProjectChange();
    }

    private void OnExportBitrateChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var bitrate = Math.Clamp((int)Math.Round(e.NewValue), 2, 120);
        ExportBitrateValueText.Text = $"{bitrate} Mbps";
        if (_syncingExportControls || _exportBitrateMbps == bitrate)
            return;

        _exportBitrateMbps = bitrate;
        QueueProjectChange();
    }

    private void OnExportAudioBitrateChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var bitrate = Math.Clamp((int)Math.Round(e.NewValue / 16.0) * 16, 64, 320);
        ExportAudioBitrateValueText.Text = $"{bitrate} kbps";
        if (_syncingExportControls || _exportAudioBitrateKbps == bitrate)
            return;

        _exportAudioBitrateKbps = bitrate;
        QueueProjectChange();
    }

    private void OnExportWidthChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (_syncingExportControls || _project is null || double.IsNaN(e.NewValue))
            return;

        var aspect = GetLockedExportAspectRatio();
        var width = NormalizeExportDimension(e.NewValue, 320);
        var height = _exportAspectLocked
            ? NormalizeExportDimension(width / aspect, 180)
            : _exportHeight;
        if (_exportResolutionMode == "custom" && _exportWidth == width && _exportHeight == height)
            return;

        _exportResolutionMode = "custom";
        _exportWidth = width;
        _exportHeight = height;
        if (!_exportAspectLocked)
            _exportLockedAspectRatio = _exportWidth / (double)_exportHeight;
        UpdateExportControls();
        QueueProjectChange();
    }

    private void OnExportHeightChanged(object sender, NumberBoxValueChangedEventArgs e)
    {
        if (_syncingExportControls || _project is null || double.IsNaN(e.NewValue))
            return;

        var aspect = GetLockedExportAspectRatio();
        var height = NormalizeExportDimension(e.NewValue, 180);
        var width = _exportAspectLocked
            ? NormalizeExportDimension(height * aspect, 320)
            : _exportWidth;
        if (_exportResolutionMode == "custom" && _exportWidth == width && _exportHeight == height)
            return;

        _exportResolutionMode = "custom";
        _exportWidth = width;
        _exportHeight = height;
        if (!_exportAspectLocked)
            _exportLockedAspectRatio = _exportWidth / (double)_exportHeight;
        UpdateExportControls();
        QueueProjectChange();
    }

    private void OnExportAspectLockToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingExportControls || _project is null || _exportAspectLocked == ExportAspectLockToggle.IsOn)
            return;

        _exportAspectLocked = ExportAspectLockToggle.IsOn;
        if (_exportAspectLocked)
            _exportLockedAspectRatio = _exportWidth / (double)Math.Max(1, _exportHeight);
        UpdateExportControls();
        QueueProjectChange();
    }

    private void SetExportQuality(string quality)
    {
        _exportQuality = quality;
        UpdateCanvasPresetButtons();
        QueueProjectChange();
    }

    private void SetExportFrameRate(int frameRate)
    {
        _exportFrameRate = frameRate is 30 or 60 ? frameRate : 60;
        LayerTimeline.FrameRate = _exportFrameRate;
        UpdateCanvasPresetButtons();
        QueueProjectChange();
    }

    private void SetExportResolutionPreset(string mode)
    {
        if (_project is null)
            return;

        var normalizedMode = mode is "720p" or "1080p" or "1440p" or "2160p" ? mode : "canvas";
        var (width, height) = GetExportPresetDimensions(normalizedMode);
        if (_exportResolutionMode == normalizedMode && _exportWidth == width && _exportHeight == height)
            return;

        _exportResolutionMode = normalizedMode;
        _exportWidth = width;
        _exportHeight = height;
        _exportLockedAspectRatio = width / (double)height;
        UpdateExportControls();
        QueueProjectChange();
    }

    private (int Width, int Height) GetExportPresetDimensions(string mode)
    {
        return Klipp.Core.Editing.EditorExportPresetMath.ResolveDimensions(mode,
            _project?.CanvasWidth ?? 1280, _project?.CanvasHeight ?? 720);
    }

    private double GetLockedExportAspectRatio()
    {
        return _exportLockedAspectRatio > 0.01
            ? _exportLockedAspectRatio
            : Math.Max(0.01, _exportWidth / (double)Math.Max(1, _exportHeight));
    }

    private static int NormalizeExportDimension(double value, int minimum)
    {
        var rounded = (int)Math.Round(value / 2.0) * 2;
        return Math.Clamp(rounded, minimum, 7680);
    }

    private void UpdateExportControls()
    {
        _syncingExportControls = true;
        try
        {
            var selectedItem = ExportEncoderComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    _exportEncoder,
                    StringComparison.OrdinalIgnoreCase));
            ExportEncoderComboBox.SelectedItem = selectedItem ?? ExportEncoderComboBox.Items[0];
            ExportBitrateSlider.Value = _exportBitrateMbps;
            ExportBitrateValueText.Text = $"{_exportBitrateMbps} Mbps";
            ExportWidthNumberBox.Value = _exportWidth;
            ExportHeightNumberBox.Value = _exportHeight;
            ExportAspectLockToggle.IsOn = _exportAspectLocked;
            ExportAudioBitrateSlider.Value = _exportAudioBitrateKbps;
            ExportAudioBitrateValueText.Text = $"{_exportAudioBitrateKbps} kbps";
            ExportResolutionSummaryText.Text = $"{_exportWidth} x {_exportHeight} px" +
                (_exportResolutionMode == "canvas" ? " | matches editing canvas" : " | scaled on export");
            SetOptionButtonState(ExportCanvasResolutionButton, _exportResolutionMode == "canvas");
            SetOptionButtonState(Export1080pResolutionButton, _exportResolutionMode == "1080p");
            SetOptionButtonState(Export1440pResolutionButton, _exportResolutionMode == "1440p");
            SetOptionButtonState(Export2160pResolutionButton, _exportResolutionMode == "2160p");
        }
        finally
        {
            _syncingExportControls = false;
        }

        UpdateExportEncoderStatus();
        UpdateExportPresetSummary();
    }

    private void UpdateExportEncoderStatus()
    {
        if (_encoderCapabilities.Count == 0)
        {
            ExportEncoderStatusText.Text = "Checking available encoders...";
            return;
        }

        var choice = FFmpegEncoderService.Resolve(_exportEncoder, _encoderCapabilities);
        if (_exportEncoder == "auto")
        {
            ExportEncoderStatusText.Text = $"Auto: {choice.DisplayName}";
            return;
        }

        ExportEncoderStatusText.Text = choice.UsedFallback
            ? $"Selected encoder is unavailable. CPU (x264) will be used."
            : $"Ready: {choice.DisplayName}";
    }

    private void SetCanvasPreset(int width, int height)
    {
        if (_project is null ||
            (_project.CanvasWidth == width && _project.CanvasHeight == height))
        {
            return;
        }

        _project.CanvasWidth = width;
        _project.CanvasHeight = height;
        if (_exportResolutionMode != "custom")
        {
            (_exportWidth, _exportHeight) = GetExportPresetDimensions(_exportResolutionMode);
            _exportLockedAspectRatio = _exportWidth / (double)_exportHeight;
        }
        UpdatePreviewCanvas();
        UpdateCanvasPresetButtons();
        QueueProjectChange();
    }

    private void UpdatePreviewCanvas()
    {
        if (PreviewSurface is null)
            return;

        var width = _project?.CanvasWidth ?? 1280;
        var height = _project?.CanvasHeight ?? 720;
        PreviewSurface.Width = width;
        PreviewSurface.Height = height;
        CanvasPresetValueText.Text = $"{width} x {height}";
        UpdatePreviewStageZoom();
        UpdateCanvasPresetButtons();
    }

    private void OnResetVideoTransformClicked(object sender, RoutedEventArgs e)
    {
        ResetVideoTransform();
    }

    private void OnVideoScaleChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingVideoTransformControls)
            return;

        _videoScale = Math.Clamp(e.NewValue / 100.0, 0.25, 3.0);
        UpdateVideoTransformPreview();
        QueueProjectChange();
    }

    private void OnVideoOffsetXChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingVideoTransformControls)
            return;

        _videoOffsetX = Math.Clamp(e.NewValue / 100.0, -0.5, 0.5);
        UpdateVideoTransformPreview();
        QueueProjectChange();
    }

    private void OnVideoOffsetYChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingVideoTransformControls)
            return;

        _videoOffsetY = Math.Clamp(e.NewValue / 100.0, -0.5, 0.5);
        UpdateVideoTransformPreview();
        QueueProjectChange();
    }

    private void OnSelectedClipScaleChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingSelectedClipVisualControls)
            return;

        SelectedClipScaleValueText.Text = $"{Math.Round(e.NewValue):0}%";
        ApplySelectedClipTransform();
    }

    private void OnSelectedClipNotesChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingSelectedClipVisualControls)
            return;

        var clip = LayerTimeline.GetSelectedMediaClip();
        if (clip is null)
            return;

        _editingClipMetadata = true;
        try
        {
            LayerTimeline.SetSelectedMediaClipMetadata(SelectedClipNotesBox.Text, clip.ColorLabel);
        }
        finally
        {
            _editingClipMetadata = false;
        }
    }

    private void OnSelectedClipColorClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string colorLabel })
            return;

        _editingClipMetadata = true;
        try
        {
            LayerTimeline.SetSelectedMediaClipMetadata(SelectedClipNotesBox.Text, colorLabel);
        }
        finally
        {
            _editingClipMetadata = false;
        }
        SyncSelectedClipVisualControls();
    }

    private void OnSelectedClipOffsetChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingSelectedClipVisualControls)
            return;

        ApplySelectedClipTransform();
    }

    private void OnSelectedClipCropChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingSelectedClipVisualControls)
            return;

        if (LayerTimeline.SetSelectedMediaClipCrop(
                SelectedClipCropLeftSlider.Value / 100.0,
                SelectedClipCropTopSlider.Value / 100.0,
                SelectedClipCropRightSlider.Value / 100.0,
                SelectedClipCropBottomSlider.Value / 100.0))
        {
            UpdateSelectedClipTransformPreview();
            QueueProjectChange();
        }

        SyncSelectedClipVisualControls();
    }

    private void OnPipPresetClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string preset })
            return;

        var (scale, offsetX, offsetY) = preset switch
        {
            "top-left" => (0.36, -0.92, -0.92),
            "top-right" => (0.36, 0.92, -0.92),
            "bottom-left" => (0.36, -0.92, 0.92),
            "bottom-right" => (0.36, 0.92, 0.92),
            _ => (1.0, 0.0, 0.0)
        };
        var clip = LayerTimeline.GetSelectedMediaClip();
        if (clip is null)
            return;

        var changed = clip.TransformKeyframes.Count > 0
            ? LayerTimeline.UpsertSelectedMediaClipTransformKeyframe(
                CurrentTimelinePosition,
                scale,
                offsetX,
                offsetY)
            : LayerTimeline.SetSelectedMediaClipTransform(scale, offsetX, offsetY);
        if (changed)
        {
            UpdateSelectedClipTransformPreview();
            QueueProjectChange();
        }
        SyncSelectedClipVisualControls();
    }

    private void OnSelectedClipMaskShapeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelectedClipVisualControls ||
            SelectedClipMaskShapeComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        ApplySelectedClipMask(item.Tag?.ToString() ?? "none", SelectedClipMaskRoundnessSlider.Value / 100.0);
    }

    private void OnSelectedClipMaskRoundnessChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        SelectedClipMaskRoundnessValueText.Text = $"{Math.Round(e.NewValue):0}%";
        if (_syncingSelectedClipVisualControls)
            return;

        var shape = (SelectedClipMaskShapeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "none";
        if (string.Equals(shape, "rounded", StringComparison.Ordinal))
            ApplySelectedClipMask(shape, e.NewValue / 100.0);
    }

    private void ApplySelectedClipMask(string shape, double roundness)
    {
        if (LayerTimeline.SetSelectedMediaClipMask(shape, roundness))
        {
            UpdateSelectedClipTransformPreview();
            QueueProjectChange();
        }
        SyncSelectedClipVisualControls();
    }

    private void OnSelectedClipAdjustmentChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingSelectedClipVisualControls)
            return;

        if (LayerTimeline.SetSelectedMediaClipFilterAdjustments(
                ClipBrightnessSlider.Value / 100.0,
                ClipContrastSlider.Value / 100.0,
                ClipSaturationSlider.Value / 100.0,
                ClipTemperatureSlider.Value / 100.0,
                ClipShadowsSlider.Value / 100.0,
                ClipHighlightsSlider.Value / 100.0,
                ClipGammaSlider.Value / 100.0,
                ClipHueSlider.Value,
                ClipSharpenSlider.Value / 100.0,
                ClipBlurSlider.Value / 10.0,
                ClipVignetteSlider.Value / 100.0))
        {
            ResetOriginalComparison();
            UpdateVfxPreview();
            QueueProjectChange();
        }

        SyncSelectedClipVisualControls();
    }

    private void OnImportClipLutClicked(object sender, RoutedEventArgs e)
    {
        if (LayerTimeline.GetSelectedMediaClip() is not null)
            LutImportRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnClearClipLutClicked(object sender, RoutedEventArgs e)
    {
        if (LayerTimeline.SetSelectedMediaClipLut(string.Empty, 1))
        {
            ResetOriginalComparison();
            SyncSelectedClipVisualControls();
            UpdateVfxPreview();
            ProjectStatusText.Text = "LUT removed";
            QueueProjectChange();
        }
    }

    private void OnClipLutStrengthChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        ClipLutStrengthValueText.Text = $"{Math.Round(e.NewValue):0}%";
        if (_syncingSelectedClipVisualControls)
            return;

        var clip = LayerTimeline.GetSelectedMediaClip();
        if (clip is null || string.IsNullOrWhiteSpace(clip.LutPath))
            return;

        if (LayerTimeline.SetSelectedMediaClipLut(clip.LutPath, e.NewValue / 100.0))
        {
            ResetOriginalComparison();
            QueueProjectChange();
            SyncSelectedClipVisualControls();
            UpdateVfxPreview();
        }
    }

    private void OnResetSelectedClipVisualClicked(object sender, RoutedEventArgs e)
    {
        LayerTimeline.ClearSelectedMediaClipTransformKeyframes(CurrentTimelinePosition, preserveCurrentState: false);
        if (LayerTimeline.SetSelectedMediaClipTransform(1, 0, 0))
        {
            UpdateSelectedClipTransformPreview();
            QueueProjectChange();
        }

        if (LayerTimeline.SetSelectedMediaClipCrop(0, 0, 0, 0))
        {
            UpdateSelectedClipTransformPreview();
            QueueProjectChange();
        }

        if (LayerTimeline.SetSelectedMediaClipMask("none", 0.16))
        {
            UpdateSelectedClipTransformPreview();
            QueueProjectChange();
        }

        if (LayerTimeline.SetSelectedMediaClipFilterAdjustments(0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0))
        {
            ResetOriginalComparison();
            UpdateVfxPreview();
            QueueProjectChange();
        }

        if (LayerTimeline.SetSelectedMediaClipLut(string.Empty, 1))
            QueueProjectChange();

        LayerTimeline.ClearSelectedMediaClipFilterKeyframes(
            CurrentTimelinePosition,
            preserveCurrentValue: false);
        if (LayerTimeline.SetSelectedMediaClipFilterStrength(1))
            QueueProjectChange();

        SetSelectedClipFilter("none");
        SyncSelectedClipVisualControls();
    }

    private void OnResetClipAdjustmentsClicked(object sender, RoutedEventArgs e)
    {
        if (LayerTimeline.SetSelectedMediaClipFilterAdjustments(0, 1, 1, 0, 0, 0, 1, 0, 0, 0, 0))
        {
            ResetOriginalComparison();
            UpdateVfxPreview();
            QueueProjectChange();
        }

        SyncSelectedClipVisualControls();
    }

    private void OnToneCurveInteractionStarted(object? sender, EventArgs e)
    {
        LayerTimeline.BeginSelectedMediaClipCurveEdit();
    }

    private void OnToneCurvePreviewChanged(object? sender, ColorCurveChangedEventArgs e)
    {
        if (LayerTimeline.PreviewSelectedMediaClipToneCurve(e.Points))
        {
            ResetOriginalComparison();
            UpdateVfxPreview();
        }
    }

    private void OnToneCurveCommitted(object? sender, ColorCurveChangedEventArgs e)
    {
        if (LayerTimeline.CommitSelectedMediaClipToneCurve(e.Points))
            UpdateVfxPreview();
        SyncSelectedClipVisualControls();
    }

    private void OnResetToneCurveClicked(object sender, RoutedEventArgs e)
    {
        if (LayerTimeline.SetSelectedMediaClipToneCurve([]))
        {
            ResetOriginalComparison();
            ToneCurveEditor.ResetToNeutral();
            UpdateVfxPreview();
            QueueProjectChange();
        }

        SyncSelectedClipVisualControls();
    }

    private void ApplySelectedClipTransform()
    {
        var clip = LayerTimeline.GetSelectedMediaClip();
        if (clip is null)
            return;

        var changed = clip.TransformKeyframes.Count > 0
            ? LayerTimeline.UpsertSelectedMediaClipTransformKeyframe(
                CurrentTimelinePosition,
                SelectedClipScaleSlider.Value / 100.0,
                SelectedClipXSlider.Value / 100.0,
                SelectedClipYSlider.Value / 100.0)
            : LayerTimeline.SetSelectedMediaClipTransform(
                SelectedClipScaleSlider.Value / 100.0,
                SelectedClipXSlider.Value / 100.0,
                SelectedClipYSlider.Value / 100.0);
        if (changed)
        {
            UpdateSelectedClipTransformPreview();
            QueueProjectChange();
        }
    }

    private void OnUpsertClipTransformKeyframeClicked(object sender, RoutedEventArgs e)
    {
        var easing = (ClipTransformEasingComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "linear";
        if (LayerTimeline.UpsertSelectedMediaClipTransformKeyframe(
                CurrentTimelinePosition,
                SelectedClipScaleSlider.Value / 100.0,
                SelectedClipXSlider.Value / 100.0,
                SelectedClipYSlider.Value / 100.0,
                easing))
        {
            SyncSelectedClipVisualControls();
            UpdateSelectedClipTransformPreview();
            QueueProjectChange();
        }
    }

    private void OnRemoveClipTransformKeyframeClicked(object sender, RoutedEventArgs e)
    {
        if (!LayerTimeline.RemoveSelectedMediaClipTransformKeyframe(CurrentTimelinePosition))
            return;

        SyncSelectedClipVisualControls();
        UpdateSelectedClipTransformPreview();
        QueueProjectChange();
    }

    private void OnClearClipTransformAnimationClicked(object sender, RoutedEventArgs e)
    {
        if (!LayerTimeline.ClearSelectedMediaClipTransformKeyframes(CurrentTimelinePosition, preserveCurrentState: true))
            return;

        SyncSelectedClipVisualControls();
        UpdateSelectedClipTransformPreview();
        QueueProjectChange();
    }

    private void OnPreviousClipTransformKeyframeClicked(object sender, RoutedEventArgs e) =>
        SeekSelectedClipTransformKeyframe(next: false);

    private void OnNextClipTransformKeyframeClicked(object sender, RoutedEventArgs e) =>
        SeekSelectedClipTransformKeyframe(next: true);

    private void SeekSelectedClipTransformKeyframe(bool next)
    {
        if (!LayerTimeline.TryGetSelectedMediaClipTransformKeyframePosition(
                CurrentTimelinePosition,
                next,
                out var target))
        {
            return;
        }

        PausePreview();
        SeekTo(target);
        SyncSelectedClipVisualControls();
    }

    private void OnClipTransformEasingSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelectedClipVisualControls ||
            ClipTransformEasingComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (LayerTimeline.SetSelectedMediaClipTransformKeyframeEasing(
                CurrentTimelinePosition,
                item.Tag?.ToString() ?? "linear"))
        {
            UpdateSelectedClipTransformPreview();
            QueueProjectChange();
        }
    }

    private void OnClipFilterStrengthChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingSelectedClipVisualControls)
            return;

        ClipFilterStrengthValueText.Text = $"{Math.Round(e.NewValue):0}%";
        var clip = LayerTimeline.GetSelectedMediaClip();
        if (clip is null)
            return;

        var changed = clip.FilterStrengthKeyframes.Count > 0
            ? LayerTimeline.UpsertSelectedMediaClipFilterKeyframe(
                CurrentTimelinePosition,
                e.NewValue / 100.0)
            : LayerTimeline.SetSelectedMediaClipFilterStrength(e.NewValue / 100.0);
        if (!changed)
            return;

        ResetOriginalComparison();
        SyncSelectedClipVisualControls();
        UpdateVfxPreview();
        QueueProjectChange();
    }

    private void OnAddClipFilterKeyframeClicked(object sender, RoutedEventArgs e)
    {
        var easing = (ClipFilterKeyframeEasingComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "linear";
        if (!LayerTimeline.UpsertSelectedMediaClipFilterKeyframe(
                CurrentTimelinePosition,
                ClipFilterStrengthSlider.Value / 100.0,
                easing))
        {
            return;
        }

        SyncSelectedClipVisualControls();
        UpdateVfxPreview();
        QueueProjectChange();
    }

    private void OnPreviousClipFilterKeyframeClicked(object sender, RoutedEventArgs e) =>
        SeekSelectedClipFilterKeyframe(next: false);

    private void OnNextClipFilterKeyframeClicked(object sender, RoutedEventArgs e) =>
        SeekSelectedClipFilterKeyframe(next: true);

    private void SeekSelectedClipFilterKeyframe(bool next)
    {
        if (!LayerTimeline.TryGetSelectedMediaClipFilterKeyframePosition(
                CurrentTimelinePosition,
                next,
                out var target))
        {
            return;
        }

        PausePreview();
        SeekTo(target);
        SyncSelectedClipVisualControls();
    }

    private void OnDeleteClipFilterKeyframeClicked(object sender, RoutedEventArgs e)
    {
        if (!LayerTimeline.RemoveSelectedMediaClipFilterKeyframe(CurrentTimelinePosition))
            return;

        SyncSelectedClipVisualControls();
        UpdateVfxPreview();
        QueueProjectChange();
    }

    private void OnClearClipFilterKeyframesClicked(object sender, RoutedEventArgs e)
    {
        if (!LayerTimeline.ClearSelectedMediaClipFilterKeyframes(
                CurrentTimelinePosition,
                preserveCurrentValue: true))
        {
            return;
        }

        SyncSelectedClipVisualControls();
        UpdateVfxPreview();
        QueueProjectChange();
    }

    private void OnClipFilterKeyframeEasingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelectedClipVisualControls ||
            ClipFilterKeyframeEasingComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (LayerTimeline.SetSelectedMediaClipFilterKeyframeEasing(
                CurrentTimelinePosition,
                item.Tag?.ToString() ?? "linear"))
        {
            UpdateVfxPreview();
            QueueProjectChange();
        }
    }

    private void OnClipFilterNoneClicked(object sender, RoutedEventArgs e) => SetSelectedClipFilter("none");
    private void OnClipFilterBoostClicked(object sender, RoutedEventArgs e) => SetSelectedClipFilter("boost");
    private void OnClipFilterWarmClicked(object sender, RoutedEventArgs e) => SetSelectedClipFilter("warm");
    private void OnClipFilterCoolClicked(object sender, RoutedEventArgs e) => SetSelectedClipFilter("cool");
    private void OnClipFilterMonoClicked(object sender, RoutedEventArgs e) => SetSelectedClipFilter("mono");
    private void OnClipFilterCinemaClicked(object sender, RoutedEventArgs e) => SetSelectedClipFilter("cinematic");

    private void SetSelectedClipFilter(string filter)
    {
        if (filter != "none")
        {
            AddTimelineEffect(filter);
            return;
        }
        ApplySelectedClipMutation(clip => EditorClipEffectWorkflowService.ApplyLook(clip, filter));
        UpdatePresetPickers();
    }

    private void OnPreviewZoomChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingPreviewZoomControls)
            return;

        SetPreviewZoom(e.NewValue / 100.0);
    }

    private void OnPreviewZoomOutClicked(object sender, RoutedEventArgs e)
    {
        SetPreviewZoom(_previewZoom - 0.10);
    }

    private void OnPreviewZoomFitClicked(object sender, RoutedEventArgs e)
    {
        SetPreviewZoom(1.0);
    }

    private void OnPreviewZoomInClicked(object sender, RoutedEventArgs e)
    {
        SetPreviewZoom(_previewZoom + 0.10);
    }

    private void OnSafeAreaGuidesToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingTimelineState)
            return;

        _showSafeAreaGuides = SafeAreaGuidesToggle.IsOn;
        UpdateSafeAreaGuides();
        QueueProjectChange();
    }

    private void OnTextOverlayChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingTimelineState)
        {
            UpdateTextOverlayPreview();
            return;
        }

        LayerTimeline.SetLayerEnabled(
            EditorTimelineLayer.Text,
            !string.IsNullOrWhiteSpace(TextOverlayBox.Text),
            CurrentTimelinePosition,
            GetLayerLabel(TextOverlayBox.Text, _textStyle));
        _textTimelineRangeStart = LayerTimeline.TextStart;
        _textTimelineRangeEnd = LayerTimeline.TextEnd;
        UpdateTextOverlayPreview();
        UpdateTextKeyframeControls();
        QueueProjectChange();
    }

    private void OnAddExtraTextLayerClicked(object sender, RoutedEventArgs e)
    {
        if (_project is null)
            return;

        var text = TextOverlayBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            ProjectStatusText.Text = "Write text before adding an overlay";
            return;
        }

        var start = LayerTimeline.TextEnabled
            ? LayerTimeline.TextStart.TotalSeconds
            : CurrentTimelinePosition.TotalSeconds;
        var end = LayerTimeline.TextEnabled
            ? LayerTimeline.TextEnd.TotalSeconds
            : Math.Min(_duration.TotalSeconds, start + 5);
        if (end <= start + 0.1)
            end = Math.Min(_duration.TotalSeconds, start + 1);

        var layer = _project.ExtraTextLayers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, _editingExtraTextLayerId, StringComparison.Ordinal));
        var isNew = layer is null;
        layer ??= new EditorProjectTextLayer();
        layer.Name = text.Length > 24 ? $"{text[..24]}..." : text;
        layer.Text = text;
        layer.Style = _textStyle;
        layer.Animation = _textAnimation;
        layer.FontFamily = _textFontFamily;
        layer.FontFilePath = _textFontFilePath;
        layer.FontFamilyName = _textFontFamilyName;
        layer.Color = _textColor;
        layer.AnchorX = _textAnchorX;
        layer.AnchorY = _textAnchorY;
        layer.Size = _textSize;
        layer.BoxWidth = _textBoxWidth;
        layer.Opacity = _textOpacity;
        layer.StartSeconds = start;
        layer.EndSeconds = end;
        layer.Keyframes = _textKeyframes.Select(keyframe => keyframe.Clone()).ToList();
        layer.Normalize(_duration.TotalSeconds);
        if (isNew)
            _project.ExtraTextLayers.Add(layer);

        _editingExtraTextLayerId = null;
        _textKeyframes.Clear();
        LayerTimeline.SetTextKeyframes(_textKeyframes);
        _syncingTimelineState = true;
        try
        {
            TextOverlayBox.Text = string.Empty;
            LayerTimeline.SetLayerEnabled(EditorTimelineLayer.Text, false, CurrentTimelinePosition, "Text");
        }
        finally
        {
            _syncingTimelineState = false;
        }
        SyncExtraTextLayers(_project);
        SaveExtraTextLayerButtonText.Text = "Add current text as overlay";
        ProjectStatusText.Text = isNew ? "Text overlay added" : "Text overlay updated";
        UpdatePreviewOverlays();
        QueueProjectChange();
    }

    private void OnExtraTextLayerItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not EditorProjectTextLayer layer)
            return;

        _editingExtraTextLayerId = layer.Id;
        _suppressProjectChanges = true;
        _syncingTimelineState = true;
        try
        {
            _textStyle = layer.Style;
            _textAnimation = layer.Animation;
            _textFontFamily = layer.FontFamily;
            _textFontFilePath = layer.FontFilePath;
            _textFontFamilyName = layer.FontFamilyName;
            _textColor = layer.Color;
            _textAnchorX = layer.AnchorX;
            _textAnchorY = layer.AnchorY;
            _textSize = layer.Size;
            _textBoxWidth = layer.BoxWidth;
            _textOpacity = layer.Opacity;
            _textKeyframes = layer.Keyframes.Select(keyframe => keyframe.Clone()).ToList();
            TextOverlayBox.Text = layer.Text;
            LayerTimeline.SetLayerRange(
                EditorTimelineLayer.Text,
                true,
                TimeSpan.FromSeconds(layer.StartSeconds),
                TimeSpan.FromSeconds(layer.EndSeconds),
                GetLayerLabel(layer.Text, layer.Style),
                notify: false);
            _textTimelineRangeStart = LayerTimeline.TextStart;
            _textTimelineRangeEnd = LayerTimeline.TextEnd;
            LayerTimeline.SetTextKeyframes(_textKeyframes);
        }
        finally
        {
            _syncingTimelineState = false;
            _suppressProjectChanges = false;
        }

        SaveExtraTextLayerButtonText.Text = "Update selected overlay";
        UpdateTextButtons();
        UpdateTextKeyframeControls();
        UpdateTextOverlayPreview();
        UpdateExtraTextLayersPreview();
        RefreshPreviewAfterTimelineEdit();
    }

    private void OnRemoveExtraTextLayerClicked(object sender, RoutedEventArgs e)
    {
        if (_project is null || sender is not FrameworkElement { Tag: string id })
            return;

        _project.ExtraTextLayers.RemoveAll(layer => string.Equals(layer.Id, id, StringComparison.Ordinal));
        var item = ExtraTextLayers.FirstOrDefault(layer => string.Equals(layer.Id, id, StringComparison.Ordinal));
        if (item is not null)
            ExtraTextLayers.Remove(item);

        if (string.Equals(_editingExtraTextLayerId, id, StringComparison.Ordinal))
        {
            _editingExtraTextLayerId = null;
            _textKeyframes.Clear();
            LayerTimeline.SetTextKeyframes(_textKeyframes);
            SaveExtraTextLayerButtonText.Text = "Add current text as overlay";
        }

        UpdateExtraTextLayerChrome();
        QueueProjectChange();
    }

    private void OnAddSubtitleLayerClicked(object sender, RoutedEventArgs e)
    {
        if (_project is null)
            return;

        var text = SubtitleTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            ProjectStatusText.Text = "Write a subtitle before adding a block";
            return;
        }

        var start = LayerTimeline.SubtitleEnabled
            ? LayerTimeline.SubtitleStart.TotalSeconds
            : CurrentTimelinePosition.TotalSeconds;
        var end = LayerTimeline.SubtitleEnabled
            ? LayerTimeline.SubtitleEnd.TotalSeconds
            : Math.Min(_duration.TotalSeconds, start + 3);
        if (end <= start + 0.1)
            end = Math.Min(_duration.TotalSeconds, start + 1);

        var layer = _project.SubtitleLayers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, _editingSubtitleLayerId, StringComparison.Ordinal));
        var isNew = layer is null;
        layer ??= new EditorProjectTextLayer();
        layer.Name = text.Length > 24 ? $"{text[..24]}..." : text;
        layer.Text = text;
        layer.Style = _subtitleStyle;
        layer.Animation = "none";
        layer.FontFamily = _subtitleFontFamily;
        layer.FontFilePath = _subtitleFontFilePath;
        layer.FontFamilyName = _subtitleFontFamilyName;
        layer.Color = _subtitleColor;
        layer.AnchorX = _subtitleAnchorX;
        layer.AnchorY = _subtitleAnchorY;
        layer.Size = _subtitleSize;
        layer.BoxWidth = _subtitleBoxWidth;
        layer.StartSeconds = start;
        layer.EndSeconds = end;
        layer.Normalize(_duration.TotalSeconds);
        if (isNew)
            _project.SubtitleLayers.Add(layer);
        SyncSubtitleLayers(_project);
        _editingSubtitleLayerId = null;
        SaveSubtitleLayerButtonText.Text = "Add subtitle block";

        _syncingTimelineState = true;
        try
        {
            SubtitleTextBox.Text = string.Empty;
            LayerTimeline.SetLayerEnabled(
                EditorTimelineLayer.Subtitle,
                false,
                CurrentTimelinePosition,
                "Subtitle");
        }
        finally
        {
            _syncingTimelineState = false;
        }

        UpdatePreviewOverlays();
        ProjectStatusText.Text = isNew ? "Subtitle block added" : "Subtitle block updated";
        QueueProjectChange();
    }

    private void OnSubtitleLayerItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not EditorProjectTextLayer layer)
            return;

        _editingSubtitleLayerId = layer.Id;
        _suppressProjectChanges = true;
        _syncingTimelineState = true;
        try
        {
            SubtitleTextBox.Text = layer.Text;
            _subtitleStyle = layer.Style;
            _subtitleFontFamily = layer.FontFamily;
            _subtitleFontFilePath = layer.FontFilePath;
            _subtitleFontFamilyName = layer.FontFamilyName;
            _subtitleColor = layer.Color;
            _subtitlePosition = "custom";
            _subtitleAnchorX = layer.AnchorX;
            _subtitleAnchorY = layer.AnchorY;
            _subtitleBoxWidth = layer.BoxWidth;
            SetSubtitleSize(layer.Size);
            LayerTimeline.SetLayerRange(
                EditorTimelineLayer.Subtitle,
                true,
                TimeSpan.FromSeconds(layer.StartSeconds),
                TimeSpan.FromSeconds(layer.EndSeconds),
                GetLayerLabel(layer.Text, layer.Style),
                notify: false);
        }
        finally
        {
            _syncingTimelineState = false;
            _suppressProjectChanges = false;
        }

        SaveSubtitleLayerButtonText.Text = "Update subtitle block";
        UpdateSubtitleButtons();
        UpdateSubtitlePreview();
        RefreshPreviewAfterTimelineEdit();
    }

    private void OnRemoveSubtitleLayerClicked(object sender, RoutedEventArgs e)
    {
        if (_project is null || sender is not FrameworkElement { Tag: string id })
            return;

        _project.SubtitleLayers.RemoveAll(layer => string.Equals(layer.Id, id, StringComparison.Ordinal));
        var item = SubtitleLayers.FirstOrDefault(layer => string.Equals(layer.Id, id, StringComparison.Ordinal));
        if (item is not null)
            SubtitleLayers.Remove(item);

        if (string.Equals(_editingSubtitleLayerId, id, StringComparison.Ordinal))
        {
            _editingSubtitleLayerId = null;
            SaveSubtitleLayerButtonText.Text = "Add subtitle block";
        }

        LayerTimeline.SetSubtitleSegments(_project.SubtitleLayers);
        UpdateSubtitleLayerChrome();
        UpdatePreviewOverlays();
        QueueProjectChange();
    }

    private void OnAddSelectedClipAudioTrackClicked(object sender, RoutedEventArgs e)
    {
        if (_project is null)
            return;

        var clip = LayerTimeline.GetSelectedMediaClip();
        if (clip is null)
        {
            ProjectStatusText.Text = "Select a clip before adding its audio";
            return;
        }

        var track = new EditorProjectAudioTrack
        {
            Name = $"{clip.Name} audio",
            SourcePath = clip.SourcePath,
            SourceDurationSeconds = clip.SourceDurationSeconds,
            PlaybackSpeed = clip.PlaybackSpeed,
            TrimStartSeconds = clip.TrimStartSeconds,
            TrimEndSeconds = clip.TrimEndSeconds,
            TimelineStartSeconds = CurrentTimelinePosition.TotalSeconds
        };
        track.Normalize();
        EditorSourceAudioService.AssignAvailableAudioTrack(_project, track);
        _project.AudioTracks.Add(track);
        _selectedAudioTrackId = track.Id;
        SyncAudioTracks(_project);
        LayerTimeline.SelectAudioTrack(track.Id);
        QueueProjectChange();
    }

    private void OnImportAudioTrackClicked(object sender, RoutedEventArgs e) =>
        AudioTrackImportRequested?.Invoke(this, EventArgs.Empty);

    private void OnAudioTrackItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not EditorProjectAudioTrack track)
            return;

        SelectAudioClipForAdjustment(track);
    }

    private void OnRemoveAudioTrackClicked(object sender, RoutedEventArgs e)
    {
        if (_project is null || sender is not FrameworkElement { Tag: string id })
            return;
        var selected = _project.AudioTracks.FirstOrDefault(track => track.Id == id);
        if (selected is null || _project.GetTrack(selected.TrackId)?.Locked == true)
            return;

        _project.AudioTracks.RemoveAll(track => string.Equals(track.Id, id, StringComparison.Ordinal));
        if (string.Equals(_selectedAudioTrackId, id, StringComparison.Ordinal))
            _selectedAudioTrackId = null;

        SyncAudioTracks(_project);
        QueueProjectChange();
    }

    private void OnSelectedAudioTrackVolumeChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingAudioTrackControls || !double.IsFinite(e.NewValue))
            return;

        SetSelectedAudioTrackVolume(e.NewValue);
    }

    private void OnSelectedAudioTrackMuteToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingAudioTrackControls)
            return;

        ApplySelectedAudioTrackChange(track => track.Muted = SelectedAudioTrackMuteToggle.IsOn);
    }

    private void OnSelectedAudioTrackNormalizeToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingAudioTrackControls)
            return;

        ApplySelectedAudioTrackChange(track =>
            track.NormalizeAudio = SelectedAudioTrackNormalizeToggle.IsOn &&
                                   track.IntegratedLoudnessLufs is not null);
    }

    private void OnSelectedAudioTrackDuckingToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingAudioTrackControls)
            return;

        ApplySelectedAudioTrackChange(track =>
            track.DuckingEnabled = SelectedAudioTrackDuckingToggle.IsOn);
    }

    private void OnSelectedAudioTrackDuckingStrengthChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingAudioTrackControls)
            return;

        ApplySelectedAudioTrackChange(track => track.DuckingStrength = e.NewValue / 100.0);
    }

    private void OnSelectedAudioTrackNoiseReductionToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingAudioTrackControls)
            return;

        ApplySelectedAudioTrackChange(track =>
            track.NoiseReductionEnabled = SelectedAudioTrackNoiseReductionToggle.IsOn);
    }

    private void OnSelectedAudioTrackNoiseReductionStrengthChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingAudioTrackControls)
            return;

        SelectedAudioTrackNoiseReductionValueText.Text = $"{Math.Round(e.NewValue):0}%";
        ApplySelectedAudioTrackChange(track => track.NoiseReductionStrength = e.NewValue / 100.0);
    }

    private async void OnDetectSelectedAudioTrackBeatsClicked(object sender, RoutedEventArgs e)
    {
        var selectedTrack = GetSelectedAudioTrack();
        if (selectedTrack is null || _project?.GetTrack(selectedTrack.TrackId)?.Locked == true || !File.Exists(selectedTrack.SourcePath))
            return;

        CommitPendingProjectChange();
        var document = _project;
        var trackId = selectedTrack.Id;
        var sourcePath = selectedTrack.SourcePath;
        var streamIndex = selectedTrack.SourceAudioStreamIndex;
        var trimStart = selectedTrack.TrimStartSeconds;
        var trimEnd = selectedTrack.TrimEndSeconds;
        SelectedAudioTrackDetectBeatsButton.IsEnabled = false;
        SelectedAudioTrackBeatStatusText.Text = "Analyzing beats...";
        try
        {
            var analysis = await _audioSignalAnalysisService.AnalyzeAsync(
                sourcePath, trimStart, trimEnd, audioStreamIndex: streamIndex);
            var currentTrack = GetSelectedAudioTrack();
            if (analysis is null || currentTrack?.Id != trackId || !ReferenceEquals(document, _project) ||
                _project?.GetTrack(currentTrack.TrackId)?.Locked == true || currentTrack.SourcePath != sourcePath ||
                currentTrack.SourceAudioStreamIndex != streamIndex || currentTrack.TrimStartSeconds != trimStart || currentTrack.TrimEndSeconds != trimEnd)
            {
                SelectedAudioTrackBeatStatusText.Text = analysis is null ? "No audio signal found." : string.Empty;
                return;
            }

            var beatTimes = analysis.BeatSeconds.Select(seconds =>
                currentTrack.TimelineStartSeconds + seconds / Math.Max(0.25, currentTrack.PlaybackSpeed));
            var count = LayerTimeline.ReplaceBeatMarkers(beatTimes, currentTrack.Name);
            SelectedAudioTrackBeatStatusText.Text = $"{count} beat markers added.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Klipp] Audio track beat detection failed: {ex}");
            SelectedAudioTrackBeatStatusText.Text = "Beat detection failed.";
        }
        finally
        {
            SyncSelectedAudioTrackControls();
        }
    }

    private async void OnAnalyzeSelectedAudioTrackLoudnessClicked(object sender, RoutedEventArgs e)
    {
        var selectedTrack = GetSelectedAudioTrack();
        if (selectedTrack is null || _project?.GetTrack(selectedTrack.TrackId)?.Locked == true)
            return;

        CommitPendingProjectChange();
        var document = _project;
        var trackId = selectedTrack.Id;
        var sourcePath = selectedTrack.SourcePath;
        var streamIndex = selectedTrack.SourceAudioStreamIndex;
        var trimStart = selectedTrack.TrimStartSeconds;
        var trimEnd = selectedTrack.TrimEndSeconds;
        SelectedAudioTrackAnalyzeLoudnessButton.IsEnabled = false;
        SelectedAudioTrackLoudnessStatusText.Text = "Analyzing audio...";
        try
        {
            var result = await _audioLoudnessAnalysisService.AnalyzeAsync(
                sourcePath, trimStart, trimEnd, audioStreamIndex: streamIndex);
            var track = _project?.AudioTracks.FirstOrDefault(candidate => candidate.Id == trackId);
            if (track is null || !ReferenceEquals(_project, document) ||
                _project!.GetTrack(track.TrackId)?.Locked == true || track.SourcePath != sourcePath || track.SourceAudioStreamIndex != streamIndex ||
                Math.Abs(track.TrimStartSeconds - trimStart) > 0.00001 || Math.Abs(track.TrimEndSeconds - trimEnd) > 0.00001)
                return;
            if (result is null)
            {
                SelectedAudioTrackLoudnessStatusText.Text = "Could not measure this track.";
                return;
            }

            CommitPendingProjectChange();
            track.IntegratedLoudnessLufs = result.IntegratedLoudnessLufs;
            track.NormalizationGainDb = result.GainDb;
            track.NormalizeAudio = true;
            track.LoudnessAnalysisTrimStartSeconds = track.TrimStartSeconds;
            track.LoudnessAnalysisTrimEndSeconds = track.TrimEndSeconds;
            track.Normalize();
            SyncAudioTracks(_project!);
            QueueProjectChange();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Klipp] Audio track loudness analysis failed: {ex}");
            SelectedAudioTrackLoudnessStatusText.Text = "Could not measure this track.";
        }
        finally
        {
            SyncSelectedAudioTrackControls();
        }
    }

    private void OnSelectedAudioTrackTimingChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (_syncingAudioTrackControls || !double.IsFinite(e.NewValue) ||
            !double.IsFinite(SelectedAudioTrackTimelineStartBox.Value) ||
            !double.IsFinite(SelectedAudioTrackTrimStartBox.Value) || !double.IsFinite(SelectedAudioTrackTrimEndBox.Value))
            return;

        ApplySelectedAudioTrackChange(track =>
        {
            track.TimelineStartSeconds = Math.Max(0, SelectedAudioTrackTimelineStartBox.Value);
            track.TrimStartSeconds = Math.Max(0, SelectedAudioTrackTrimStartBox.Value);
            track.TrimEndSeconds = Math.Max(track.TrimStartSeconds + 0.1, SelectedAudioTrackTrimEndBox.Value);
        });
    }

    private void ApplySelectedAudioTrackChange(Action<EditorProjectAudioTrack> change)
    {
        var track = GetSelectedAudioTrack();
        if (track is null || _project?.GetTrack(track.TrackId)?.Locked == true)
            return;

        change(track);
        track.Normalize();
        SyncAudioTracks(_project!);
        QueueProjectChange();
    }

    private async Task RefreshBrandKitsAsync(string? selectedKitId = null)
    {
        var kits = await _brandKitService.GetAllAsync();
        _syncingBrandKitControls = true;
        try
        {
            BrandKits.Clear();
            foreach (var kit in kits)
                BrandKits.Add(kit);

            var preferredId = selectedKitId ?? _selectedBrandKitId;
            var selected = BrandKits.FirstOrDefault(kit =>
                               string.Equals(kit.Id, preferredId, StringComparison.OrdinalIgnoreCase)) ??
                           BrandKits.FirstOrDefault();
            _selectedBrandKitId = selected?.Id;
            BrandKitSelector.SelectedItem = selected;
            UpdateBrandKitControls(selected);
        }
        finally
        {
            _syncingBrandKitControls = false;
        }
    }

    private EditorBrandKit? GetSelectedBrandKit() =>
        BrandKitSelector?.SelectedItem as EditorBrandKit ??
        BrandKits.FirstOrDefault(kit =>
            string.Equals(kit.Id, _selectedBrandKitId, StringComparison.OrdinalIgnoreCase));

    private void OnBrandKitSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingBrandKitControls)
            return;

        var selected = BrandKitSelector.SelectedItem as EditorBrandKit;
        _selectedBrandKitId = selected?.Id;
        UpdateBrandKitControls(selected);
    }

    private void UpdateBrandKitControls(EditorBrandKit? kit)
    {
        if (BrandKitNameBox is null || BrandKitFontComboBox is null)
            return;

        _syncingBrandKitControls = true;
        try
        {
            BrandKitNameBox.Text = kit?.Name ?? string.Empty;
            var fontFamily = EditorBrandKitService.NormalizeFontFamily(kit?.FontFamily);
            BrandKitFontComboBox.SelectedItem = BrandKitFontComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(),
                    fontFamily,
                    StringComparison.OrdinalIgnoreCase));
            BrandCustomFontNameText.Text = kit?.FontAsset switch
            {
                null => "No imported font",
                { IsMissing: true } font => $"Missing: {font.FamilyName}",
                var font => font.FamilyName
            };
            RemoveBrandFontButton.IsEnabled = kit?.FontAsset is not null;
            SetBrandColorControl(BrandPrimaryColorBox, BrandPrimaryColorButton, kit?.PrimaryColor ?? "#A970FF");
            SetBrandColorControl(BrandSecondaryColorBox, BrandSecondaryColorButton, kit?.SecondaryColor ?? "#4CEBFF");
            SetBrandColorControl(BrandAccentColorBox, BrandAccentColorButton, kit?.AccentColor ?? "#B0FF50");
            SetBrandColorControl(BrandTextColorBox, BrandTextColorButton, kit?.TextColor ?? "#FFFFFF");
            DeleteBrandKitButton.IsEnabled = kit is not null;

            BrandLogos.Clear();
            if (kit is not null)
            {
                foreach (var logo in kit.Logos)
                    BrandLogos.Add(new EditorBrandLogoItem(logo));
            }
            BrandLogoKitNameText.Text = kit?.Name ?? "Select a brand kit";
            BrandLogoEmptyState.Visibility = BrandLogos.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            _syncingBrandKitControls = false;
        }
    }

    private static void SetBrandColorControl(TextBox textBox, Button swatch, string color)
    {
        var normalized = EditorBrandKitService.TryNormalizeColor(color, out var value)
            ? value
            : "#FFFFFF";
        textBox.Text = normalized;
        swatch.Background = new SolidColorBrush(ParseEditorTextColor(normalized));
    }

    private void OnBrandKitColorBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_syncingBrandKitControls && sender is TextBox textBox)
            ValidateBrandColorBox(textBox);
    }

    private void OnBrandKitColorBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || sender is not TextBox textBox)
            return;
        ValidateBrandColorBox(textBox);
        e.Handled = true;
    }

    private bool ValidateBrandColorBox(TextBox textBox)
    {
        if (!EditorBrandKitService.TryNormalizeColor(textBox.Text, out var normalized))
        {
            var kit = GetSelectedBrandKit();
            var fallback = ReferenceEquals(textBox, BrandPrimaryColorBox) ? kit?.PrimaryColor ?? "#A970FF"
                : ReferenceEquals(textBox, BrandSecondaryColorBox) ? kit?.SecondaryColor ?? "#4CEBFF"
                : ReferenceEquals(textBox, BrandAccentColorBox) ? kit?.AccentColor ?? "#B0FF50"
                : kit?.TextColor ?? "#FFFFFF";
            textBox.Text = fallback;
            BrandKitStatusText.Text = "Use a color like #A970FF.";
            return false;
        }

        textBox.Text = normalized;
        var swatch = ReferenceEquals(textBox, BrandPrimaryColorBox) ? BrandPrimaryColorButton
            : ReferenceEquals(textBox, BrandSecondaryColorBox) ? BrandSecondaryColorButton
            : ReferenceEquals(textBox, BrandAccentColorBox) ? BrandAccentColorButton
            : BrandTextColorButton;
        swatch.Background = new SolidColorBrush(ParseEditorTextColor(normalized));
        return true;
    }

    private bool TryBuildBrandKitDraft(out EditorBrandKit draft)
    {
        draft = null!;
        var selected = GetSelectedBrandKit();
        if (selected is null)
        {
            BrandKitStatusText.Text = "Select or create a brand kit first.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(BrandKitNameBox.Text))
        {
            BrandKitStatusText.Text = "Enter a name for the brand kit.";
            return false;
        }
        if (!ValidateBrandColorBox(BrandPrimaryColorBox) ||
            !ValidateBrandColorBox(BrandSecondaryColorBox) ||
            !ValidateBrandColorBox(BrandAccentColorBox) ||
            !ValidateBrandColorBox(BrandTextColorBox))
        {
            return false;
        }

        var fontFamily = BrandKitFontComboBox.SelectedItem is ComboBoxItem item
            ? item.Tag?.ToString()
            : selected.FontFamily;
        draft = selected.Clone();
        draft.Name = BrandKitNameBox.Text.Trim();
        draft.FontFamily = EditorBrandKitService.NormalizeFontFamily(fontFamily);
        draft.PrimaryColor = BrandPrimaryColorBox.Text;
        draft.SecondaryColor = BrandSecondaryColorBox.Text;
        draft.AccentColor = BrandAccentColorBox.Text;
        draft.TextColor = BrandTextColorBox.Text;
        return true;
    }

    private async void OnCreateBrandKitClicked(object sender, RoutedEventArgs e)
    {
        var name = await PromptForBrandKitNameAsync("New brand kit");
        if (string.IsNullOrWhiteSpace(name))
            return;

        try
        {
            var saved = await _brandKitService.SaveAsync(new EditorBrandKit
            {
                Id = string.Empty,
                Name = name,
                FontFamily = EditorBrandKitService.NormalizeFontFamily(_textFontFamily),
                TextColor = EditorTextTypography.ResolveColor(_textColor, _textStyle)
            });
            await RefreshBrandKitsAsync(saved.Id);
            BrandKitStatusText.Text = $"{saved.Name} created.";
        }
        catch (Exception ex)
        {
            BrandKitStatusText.Text = ex.Message;
        }
    }

    private async void OnSaveBrandKitClicked(object sender, RoutedEventArgs e)
    {
        if (!TryBuildBrandKitDraft(out var draft))
            return;

        try
        {
            var saved = await _brandKitService.SaveAsync(draft);
            await RefreshBrandKitsAsync(saved.Id);
            BrandKitStatusText.Text = $"{saved.Name} saved for every project.";
        }
        catch (Exception ex)
        {
            BrandKitStatusText.Text = ex.Message;
        }
    }

    private async void OnDeleteBrandKitClicked(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedBrandKit();
        if (selected is null)
            return;

        var dialog = AccessibleDialogFactory.Create(
            XamlRoot,
            "Delete brand kit?",
            $"{selected.Name} will be removed from the library. Existing project layers will stay unchanged.",
            primaryButtonText: "Delete",
            closeButtonText: "Cancel",
            defaultButton: ContentDialogButton.Close);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            if (await _brandKitService.DeleteAsync(selected.Id))
            {
                _selectedBrandKitId = null;
                await RefreshBrandKitsAsync();
                BrandKitStatusText.Text = $"{selected.Name} deleted.";
            }
        }
        catch (Exception ex)
        {
            BrandKitStatusText.Text = ex.Message;
        }
    }

    private async Task<string?> PromptForBrandKitNameAsync(string suggestedName)
    {
        var input = new TextBox
        {
            Text = suggestedName,
            PlaceholderText = "Brand kit name",
            MaxLength = 48,
            MinWidth = 300
        };
        AutomationProperties.SetName(input, "Brand kit name");
        AutomationProperties.SetHelpText(input, "This name appears in the brand kit library.");
        input.Loaded += (_, _) =>
        {
            input.Focus(FocusState.Programmatic);
            input.SelectAll();
        };
        var dialog = AccessibleDialogFactory.Create(
            XamlRoot,
            "Create brand kit",
            input,
            primaryButtonText: "Create",
            closeButtonText: "Cancel",
            defaultButton: ContentDialogButton.Primary);
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? input.Text.Trim()
            : null;
    }

    private void OnApplyBrandKitToTextClicked(object sender, RoutedEventArgs e)
    {
        if (!TryBuildBrandKitDraft(out var kit))
            return;

        CommitPendingProjectChange();
        _textFontFamily = EditorTextTypography.NormalizeFontFamily(kit.FontFamily);
        _textFontFilePath = EditorTextTypography.NormalizeFontFilePath(kit.FontAsset?.StoredPath);
        _textFontFamilyName = EditorTextTypography.NormalizeCustomFontFamilyName(kit.FontAsset?.FamilyName);
        _textColor = kit.TextColor;
        UpdateTextButtons();
        UpdateTextOverlayPreview();
        QueueProjectChange();
        BrandKitStatusText.Text = $"{kit.Name} font and text color applied.";
    }

    private void OnApplyBrandKitToSubtitlesClicked(object sender, RoutedEventArgs e)
    {
        if (!TryBuildBrandKitDraft(out var kit))
            return;

        CommitPendingProjectChange();
        _subtitleFontFamily = EditorTextTypography.NormalizeFontFamily(kit.FontFamily);
        _subtitleFontFilePath = EditorTextTypography.NormalizeFontFilePath(kit.FontAsset?.StoredPath);
        _subtitleFontFamilyName = EditorTextTypography.NormalizeCustomFontFamilyName(kit.FontAsset?.FamilyName);
        _subtitleColor = kit.TextColor;
        UpdateSubtitleButtons();
        UpdateSubtitlePreview();
        QueueProjectChange();
        BrandKitStatusText.Text = $"{kit.Name} font and text color applied to subtitles.";
    }

    private void OnApplyBrandColorClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string role })
            return;
        var textBox = role switch
        {
            "primary" => BrandPrimaryColorBox,
            "secondary" => BrandSecondaryColorBox,
            "accent" => BrandAccentColorBox,
            _ => BrandTextColorBox
        };
        if (!ValidateBrandColorBox(textBox))
            return;

        SetTextColor(textBox.Text);
        BrandKitStatusText.Text = $"{role} color applied to text.";
    }

    private async Task RefreshTextTemplatesAsync(string? selectedTemplateId = null)
    {
        var templates = await _textTemplateService.GetAllAsync();
        TextTemplates.Clear();
        foreach (var template in templates)
            TextTemplates.Add(template);

        TextTemplateCountText.Text = TextTemplates.Count.ToString();
        TextTemplateList.SelectedItem = string.IsNullOrWhiteSpace(selectedTemplateId)
            ? null
            : TextTemplates.FirstOrDefault(template =>
                string.Equals(template.Id, selectedTemplateId, StringComparison.OrdinalIgnoreCase));
        UpdateTextTemplateSelection();
    }

    private void OnTextTemplateItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not EditorTextTemplate template)
            return;

        TextTemplateList.SelectedItem = template;
        ApplyTextTemplate(template);
    }

    private void OnTextTemplateSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateTextTemplateSelection();

    private void UpdateTextTemplateSelection()
    {
        var selected = TextTemplateList?.SelectedItem as EditorTextTemplate;
        if (DeleteTextTemplateButton is not null)
            DeleteTextTemplateButton.IsEnabled = selected is { IsBuiltIn: false };
    }

    private void ApplyTextTemplate(EditorTextTemplate template)
    {
        CommitPendingProjectChange();
        _textStyle = EditorTextTemplateService.NormalizeStyle(template.Style);
        _textAnimation = EditorTextTemplateService.NormalizeAnimation(template.Animation);
        _textFontFamily = EditorTextTypography.NormalizeFontFamily(template.FontFamily);
        _textFontFilePath = EditorTextTypography.NormalizeFontFilePath(template.FontFilePath);
        _textFontFamilyName = EditorTextTypography.NormalizeCustomFontFamilyName(template.FontFamilyName);
        _textColor = EditorTextTypography.NormalizeColor(template.Color);
        _textBoxWidth = Math.Clamp(Math.Round(template.BoxWidth / CanvasSnapPixels) * CanvasSnapPixels, 96, 1280);

        var currentState = GetCurrentTextState() with
        {
            Size = Math.Clamp(Math.Round(template.Size), 12, 220),
            Opacity = Math.Clamp(template.Opacity, 0, 1)
        };
        if (_textKeyframes.Count > 0)
        {
            UpsertTextKeyframe(currentState);
            SyncTextKeyframesToTimeline();
        }
        else
        {
            _textSize = currentState.Size;
            _textOpacity = currentState.Opacity;
        }

        if (!string.IsNullOrWhiteSpace(TextOverlayBox.Text))
        {
            LayerTimeline.SetLayerEnabled(
                EditorTimelineLayer.Text,
                true,
                CurrentTimelinePosition,
                GetLayerLabel(TextOverlayBox.Text, _textStyle));
        }
        UpdateTextButtons();
        UpdateTextKeyframeControls();
        UpdateTextOverlayPreview();
        TextTemplateStatusText.Text = _textKeyframes.Count > 0
            ? $"{template.Name} applied · size and opacity updated at playhead"
            : $"{template.Name} applied";
        QueueProjectChange();
    }

    private async void OnSaveTextTemplateClicked(object sender, RoutedEventArgs e)
    {
        var suggestedName = $"{CaptureTemplateLabel(_textStyle)} + {CaptureTemplateLabel(_textAnimation)}";
        var name = await PromptForTextTemplateNameAsync(suggestedName);
        if (string.IsNullOrWhiteSpace(name))
            return;

        try
        {
            var state = GetCurrentTextState();
            var saved = await _textTemplateService.SaveCustomAsync(
                name,
                _textStyle,
                _textAnimation,
                state.Size,
                _textBoxWidth,
                state.Opacity,
                _textFontFamily,
                _textColor,
                _textFontFilePath,
                _textFontFamilyName);
            await RefreshTextTemplatesAsync(saved.Id);
            TextTemplateStatusText.Text = $"{saved.Name} saved for all projects";
        }
        catch (Exception ex)
        {
            TextTemplateStatusText.Text = ex.Message;
        }
    }

    private async void OnDeleteTextTemplateClicked(object sender, RoutedEventArgs e)
    {
        if (TextTemplateList.SelectedItem is not EditorTextTemplate { IsBuiltIn: false } template)
            return;

        var dialog = AccessibleDialogFactory.Create(
            XamlRoot,
            "Delete text template?",
            $"{template.Name} will be removed from the template library. Existing text layers will stay unchanged.",
            primaryButtonText: "Delete",
            closeButtonText: "Cancel",
            defaultButton: ContentDialogButton.Close);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            if (await _textTemplateService.DeleteCustomAsync(template.Id))
            {
                await RefreshTextTemplatesAsync();
                TextTemplateStatusText.Text = $"{template.Name} removed";
            }
        }
        catch (Exception ex)
        {
            TextTemplateStatusText.Text = ex.Message;
        }
    }

    private async Task<string?> PromptForTextTemplateNameAsync(string suggestedName)
    {
        var input = new TextBox
        {
            Text = suggestedName,
            PlaceholderText = "Text template name",
            MaxLength = 48,
            MinWidth = 300
        };
        AutomationProperties.SetName(input, "Text template name");
        AutomationProperties.SetHelpText(input, "This name appears in the text template library.");
        input.Loaded += (_, _) =>
        {
            input.Focus(FocusState.Programmatic);
            input.SelectAll();
        };
        var dialog = AccessibleDialogFactory.Create(
            XamlRoot,
            "Save text template",
            input,
            primaryButtonText: "Save",
            closeButtonText: "Cancel",
            defaultButton: ContentDialogButton.Primary);
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? input.Text.Trim()
            : null;
    }

    private static string CaptureTemplateLabel(string value) => value switch
    {
        "none" => "Still",
        "slide-up" => "Slide up",
        "slide-left" => "Slide left",
        "type" => "Type",
        _ when string.IsNullOrWhiteSpace(value) => "Plain",
        _ => char.ToUpperInvariant(value[0]) + value[1..]
    };

    private void OnTextPlainClicked(object sender, RoutedEventArgs e) => SetTextStyle("plain");
    private void OnTextBoldClicked(object sender, RoutedEventArgs e) => SetTextStyle("bold");
    private void OnTextMemeClicked(object sender, RoutedEventArgs e) => SetTextStyle("meme");
    private void OnTextHighlightClicked(object sender, RoutedEventArgs e) => SetTextStyle("highlight");
    private void OnTextPixelClicked(object sender, RoutedEventArgs e) => SetTextStyle("pixel");
    private void OnTextOutlineClicked(object sender, RoutedEventArgs e) => SetTextStyle("outline");
    private void OnTextNeonClicked(object sender, RoutedEventArgs e) => SetTextStyle("neon");
    private void OnTextShadowClicked(object sender, RoutedEventArgs e) => SetTextStyle("shadow");
    private void OnTextTypewriterClicked(object sender, RoutedEventArgs e) => SetTextStyle("typewriter");
    private void OnTextImpactClicked(object sender, RoutedEventArgs e) => SetTextStyle("impact");
    private void OnTextStickerClicked(object sender, RoutedEventArgs e) => SetTextStyle("sticker");
    private void OnTextCaptionClicked(object sender, RoutedEventArgs e) => SetTextStyle("caption");
    private void OnTextAnimationNoneClicked(object sender, RoutedEventArgs e) => SetTextAnimation("none");
    private void OnTextAnimationFadeClicked(object sender, RoutedEventArgs e) => SetTextAnimation("fade");
    private void OnTextAnimationPopClicked(object sender, RoutedEventArgs e) => SetTextAnimation("pop");
    private void OnTextAnimationSlideUpClicked(object sender, RoutedEventArgs e) => SetTextAnimation("slide-up");
    private void OnTextAnimationSlideLeftClicked(object sender, RoutedEventArgs e) => SetTextAnimation("slide-left");
    private void OnTextAnimationTypeClicked(object sender, RoutedEventArgs e) => SetTextAnimation("type");
    private void OnTextSizeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingTextSizeControls || _syncingTextKeyframeControls)
            return;

        SetTextSize(e.NewValue);
    }

    private void OnTextOpacityChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingTextKeyframeControls)
            return;

        var state = GetCurrentTextState() with { Opacity = Math.Clamp(e.NewValue / 100.0, 0, 1) };
        ApplyTextStateChange(state);
    }

    private void OnAddTextKeyframeClicked(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TextOverlayBox.Text) || !LayerTimeline.TextEnabled)
            return;

        CommitPendingProjectChange();
        UpsertTextKeyframe(GetCurrentTextState());
        SyncTextKeyframesToTimeline();
        UpdateTextKeyframeControls();
        UpdateTextOverlayPreview();
        QueueProjectChange();
    }

    private void OnDeleteTextKeyframeClicked(object sender, RoutedEventArgs e)
    {
        var keyframe = FindTextKeyframeAtPlayhead();
        if (keyframe is null)
            return;

        CommitPendingProjectChange();
        var state = keyframe.ToState();
        _textKeyframes.Remove(keyframe);
        if (_textKeyframes.Count == 0)
        {
            _textAnchorX = state.AnchorX;
            _textAnchorY = state.AnchorY;
            _textSize = state.Size;
            _textOpacity = state.Opacity;
        }
        SyncTextKeyframesToTimeline();
        UpdateTextKeyframeControls();
        UpdateTextOverlayPreview();
        QueueProjectChange();
    }

    private void OnPreviousTextKeyframeClicked(object sender, RoutedEventArgs e) =>
        SeekToAdjacentTextKeyframe(next: false);

    private void OnNextTextKeyframeClicked(object sender, RoutedEventArgs e) =>
        SeekToAdjacentTextKeyframe(next: true);

    private void SeekToAdjacentTextKeyframe(bool next)
    {
        if (_textKeyframes.Count == 0)
            return;

        PausePreview();
        var time = CurrentTimelinePosition.TotalSeconds;
        var target = next
            ? _textKeyframes.Where(keyframe => keyframe.TimeSeconds > time + 0.045)
                .OrderBy(keyframe => keyframe.TimeSeconds)
                .FirstOrDefault() ?? _textKeyframes.OrderBy(keyframe => keyframe.TimeSeconds).First()
            : _textKeyframes.Where(keyframe => keyframe.TimeSeconds < time - 0.045)
                .OrderByDescending(keyframe => keyframe.TimeSeconds)
                .FirstOrDefault() ?? _textKeyframes.OrderByDescending(keyframe => keyframe.TimeSeconds).First();
        SeekTo(TimeSpan.FromSeconds(target.TimeSeconds));
        UpdateTextKeyframeControls();
    }

    private void OnTextKeyframeEasingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTextKeyframeControls || FindTextKeyframeAtPlayhead() is not { } keyframe ||
            TextKeyframeEasingComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var easing = item.Tag?.ToString() ?? "linear";
        if (string.Equals(keyframe.Easing, easing, StringComparison.Ordinal))
            return;

        keyframe.Easing = easing;
        UpdateTextOverlayPreview();
        QueueProjectChange();
    }

    private void OnClearTextKeyframesClicked(object sender, RoutedEventArgs e)
    {
        if (_textKeyframes.Count == 0)
            return;

        CommitPendingProjectChange();
        var state = GetCurrentTextState();
        _textKeyframes.Clear();
        _textAnchorX = state.AnchorX;
        _textAnchorY = state.AnchorY;
        _textSize = state.Size;
        _textOpacity = state.Opacity;
        SyncTextKeyframesToTimeline();
        UpdateTextKeyframeControls();
        UpdateTextOverlayPreview();
        QueueProjectChange();
    }

    private void OnTextPositionTopClicked(object sender, RoutedEventArgs e) => SetTextPosition("top");
    private void OnTextPositionCenterClicked(object sender, RoutedEventArgs e) => SetTextPosition("center");
    private void OnTextPositionBottomClicked(object sender, RoutedEventArgs e) => SetTextPosition("bottom");

    private void OnFilterNoneClicked(object sender, RoutedEventArgs e) => SetVideoFilter("none");
    private void OnFilterBoostClicked(object sender, RoutedEventArgs e) => SetVideoFilter("boost");
    private void OnFilterWarmClicked(object sender, RoutedEventArgs e) => SetVideoFilter("warm");
    private void OnFilterCoolClicked(object sender, RoutedEventArgs e) => SetVideoFilter("cool");
    private void OnFilterMonoClicked(object sender, RoutedEventArgs e) => SetVideoFilter("mono");
    private void OnFilterCinematicClicked(object sender, RoutedEventArgs e) => SetVideoFilter("cinematic");
    private void OnFilterSharpenClicked(object sender, RoutedEventArgs e) => SetVideoFilter("sharpen");
    private void OnFilterBlurClicked(object sender, RoutedEventArgs e) => SetVideoFilter("blur");
    private void OnFilterVintageClicked(object sender, RoutedEventArgs e) => SetVideoFilter("vintage");
    private void OnFilterVibrantClicked(object sender, RoutedEventArgs e) => SetVideoFilter("vibrant");
    private void OnFilterNoirClicked(object sender, RoutedEventArgs e) => SetVideoFilter("noir");
    private void OnFilterDreamClicked(object sender, RoutedEventArgs e) => SetVideoFilter("dream");
    private void OnFilterGlitchClicked(object sender, RoutedEventArgs e) => SetVideoFilter("glitch");
    private void OnFilterVignetteClicked(object sender, RoutedEventArgs e) => SetVideoFilter("vignette");

    private void OnElementNoneClicked(object sender, RoutedEventArgs e) => SetElementOverlay("none");
    private void OnElementFocusClicked(object sender, RoutedEventArgs e) => SetElementOverlay("focus");
    private void OnElementLetterboxClicked(object sender, RoutedEventArgs e) => SetElementOverlay("letterbox");
    private void OnElementCenterClicked(object sender, RoutedEventArgs e) => SetElementOverlay("center");

    private void OnImportGraphicLayerClicked(object sender, RoutedEventArgs e) =>
        GraphicImportRequested?.Invoke(this, EventArgs.Empty);

    private void OnImportBrandLogoClicked(object sender, RoutedEventArgs e)
    {
        if (GetSelectedBrandKit() is null)
        {
            BrandLogoStatusText.Text = "Select a brand kit before importing a logo.";
            return;
        }
        BrandLogoImportRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnImportBrandFontClicked(object sender, RoutedEventArgs e)
    {
        if (GetSelectedBrandKit() is null)
        {
            BrandKitStatusText.Text = "Select a brand kit before importing a font.";
            return;
        }
        BrandFontImportRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void OnRemoveBrandFontClicked(object sender, RoutedEventArgs e)
    {
        var kit = GetSelectedBrandKit();
        if (kit?.FontAsset is null)
            return;
        var fontName = kit.FontAsset.FamilyName;
        var dialog = AccessibleDialogFactory.Create(
            XamlRoot,
            "Delete imported font?",
            $"{fontName} will be removed from {kit.Name}. Existing text layers will keep their font reference.",
            primaryButtonText: "Delete",
            closeButtonText: "Cancel",
            defaultButton: ContentDialogButton.Close);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            if (await _brandKitService.RemoveFontAsync(kit.Id))
            {
                await RefreshBrandKitsAsync(kit.Id);
                BrandKitStatusText.Text = $"{fontName} removed from the kit.";
            }
        }
        catch (Exception ex)
        {
            BrandKitStatusText.Text = ex.Message;
        }
    }

    private async void OnAddBrandLogoToProjectClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string logoId })
            return;
        var logo = BrandLogos.FirstOrDefault(item =>
            string.Equals(item.Id, logoId, StringComparison.OrdinalIgnoreCase));
        if (logo is null || !File.Exists(logo.StoredPath))
        {
            BrandLogoStatusText.Text = "The selected logo file is missing.";
            return;
        }

        try
        {
            await ImportGraphicLayerAsync(logo.StoredPath);
            BrandLogoStatusText.Text = $"{logo.Name} added at the playhead.";
        }
        catch (Exception ex)
        {
            BrandLogoStatusText.Text = ex.Message;
        }
    }

    private async void OnRemoveBrandLogoClicked(object sender, RoutedEventArgs e)
    {
        var kit = GetSelectedBrandKit();
        if (kit is null || sender is not FrameworkElement { Tag: string logoId })
            return;
        var logo = BrandLogos.FirstOrDefault(item =>
            string.Equals(item.Id, logoId, StringComparison.OrdinalIgnoreCase));
        if (logo is null)
            return;

        var dialog = AccessibleDialogFactory.Create(
            XamlRoot,
            "Remove logo from brand kit?",
            $"{logo.Name} will be removed from {kit.Name}. Existing project layers will stay unchanged.",
            primaryButtonText: "Delete",
            closeButtonText: "Cancel",
            defaultButton: ContentDialogButton.Close);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        try
        {
            if (await _brandKitService.RemoveLogoAsync(kit.Id, logo.Id))
            {
                await RefreshBrandKitsAsync(kit.Id);
                BrandLogoStatusText.Text = $"{logo.Name} removed from the kit.";
            }
        }
        catch (Exception ex)
        {
            BrandLogoStatusText.Text = ex.Message;
        }
    }

    private void OnGraphicLayerItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not EditorProjectGraphicLayer layer)
            return;

        _selectedGraphicLayerId = layer.Id;
        GraphicLayerList.SelectedItem = layer;
        LayerTimeline.SelectGraphicSegment(layer.Id);
        SyncSelectedGraphicControls();
        UpdateProjectChrome();
        UpdateGraphicLayersPreview();
    }

    private void OnRemoveGraphicLayerClicked(object sender, RoutedEventArgs e)
    {
        if (_project is null || sender is not FrameworkElement { Tag: string id })
            return;

        RemoveGraphicLayer(id);
    }

    private bool RemoveGraphicLayer(string id)
    {
        if (_project is null || !_project.GraphicLayers.Any(layer =>
                string.Equals(layer.Id, id, StringComparison.Ordinal)))
        {
            return false;
        }

        CommitPendingProjectChange();
        _project.GraphicLayers.RemoveAll(layer => string.Equals(layer.Id, id, StringComparison.Ordinal));
        if (string.Equals(_selectedGraphicLayerId, id, StringComparison.Ordinal))
            _selectedGraphicLayerId = null;
        SyncGraphicLayers(_project);
        UpdateGraphicLayersPreview();
        QueueProjectChange();
        return true;
    }

    private bool DuplicateSelectedGraphicLayer()
    {
        if (_project is null || GetSelectedGraphicLayer() is not { } source)
            return false;

        CommitPendingProjectChange();
        var offsetX = 24.0 / Math.Max(1, _project.CanvasWidth);
        var offsetY = 24.0 / Math.Max(1, _project.CanvasHeight);
        var copy = new EditorProjectGraphicLayer
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"{source.Name} copy",
            SourcePath = source.SourcePath,
            AnchorX = Math.Clamp(source.AnchorX + offsetX, 0, 1),
            AnchorY = Math.Clamp(source.AnchorY + offsetY, 0, 1),
            Width = source.Width,
            Height = source.Height,
            Opacity = source.Opacity,
            BlendMode = source.BlendMode,
            MaskShape = source.MaskShape,
            MaskRoundness = source.MaskRoundness,
            StartSeconds = source.StartSeconds,
            EndSeconds = source.EndSeconds,
            Keyframes = source.Keyframes.Select(keyframe => new EditorProjectGraphicKeyframe
            {
                TimeSeconds = keyframe.TimeSeconds,
                AnchorX = Math.Clamp(keyframe.AnchorX + offsetX, 0, 1),
                AnchorY = Math.Clamp(keyframe.AnchorY + offsetY, 0, 1),
                Width = keyframe.Width,
                Height = keyframe.Height,
                Opacity = keyframe.Opacity,
                Easing = keyframe.Easing
            }).ToList()
        };
        copy.Normalize(_project.DurationSeconds, _project.CanvasWidth, _project.CanvasHeight);
        _project.GraphicLayers.Add(copy);
        _selectedGraphicLayerId = copy.Id;
        SyncGraphicLayers(_project);
        UpdateGraphicLayersPreview();
        QueueProjectChange();
        return true;
    }

    private void OnResetGraphicLayerClicked(object sender, RoutedEventArgs e)
    {
        var layer = GetSelectedGraphicLayer();
        if (layer is null || _project is null)
            return;

        layer.AnchorX = 0.5;
        layer.AnchorY = 0.5;
        layer.Opacity = 1;
        layer.BlendMode = "normal";
        layer.MaskShape = "none";
        layer.MaskRoundness = 0.2;
        layer.Keyframes.Clear();
        SyncGraphicTimelineRange();
        SyncSelectedGraphicControls();
        UpdateGraphicLayersPreview();
        QueueProjectChange();
    }

    private void OnGraphicLayerLayoutChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        var layer = GetSelectedGraphicLayer();
        if (_syncingGraphicControls || layer is null || _project is null || double.IsNaN(e.NewValue))
            return;

        var keyframe = GetOrCreateGraphicKeyframeForEdit(layer);
        var anchorX = Math.Clamp(GraphicXBox.Value / _project.CanvasWidth, 0, 1);
        var anchorY = Math.Clamp(GraphicYBox.Value / _project.CanvasHeight, 0, 1);
        var width = Math.Clamp(GraphicWidthBox.Value, 16, _project.CanvasWidth * 2);
        var height = Math.Clamp(GraphicHeightBox.Value, 16, _project.CanvasHeight * 2);
        if (keyframe is null)
        {
            layer.AnchorX = anchorX;
            layer.AnchorY = anchorY;
            layer.Width = width;
            layer.Height = height;
        }
        else
        {
            keyframe.AnchorX = anchorX;
            keyframe.AnchorY = anchorY;
            keyframe.Width = width;
            keyframe.Height = height;
        }
        SyncGraphicTimelineRange();
        SyncSelectedGraphicControls();
        UpdateGraphicLayersPreview();
        QueueProjectChange();
    }

    private void OnGraphicLayerTimingChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        var layer = GetSelectedGraphicLayer();
        if (_syncingGraphicControls || layer is null || _project is null || double.IsNaN(e.NewValue))
            return;

        layer.StartSeconds = Math.Clamp(GraphicStartBox.Value, 0, Math.Max(0, _project.DurationSeconds - 0.1));
        layer.EndSeconds = Math.Clamp(
            GraphicEndBox.Value,
            layer.StartSeconds + 0.1,
            Math.Max(layer.StartSeconds + 0.1, _project.DurationSeconds));
        layer.Normalize(_project.DurationSeconds, _project.CanvasWidth, _project.CanvasHeight);
        SyncGraphicTimelineRange();
        SyncSelectedGraphicControls();
        UpdateGraphicLayersPreview();
        QueueProjectChange();
    }

    private void OnGraphicOpacityChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var layer = GetSelectedGraphicLayer();
        if (_syncingGraphicControls || layer is null)
            return;

        var opacity = Math.Clamp(e.NewValue / 100.0, 0, 1);
        var keyframe = GetOrCreateGraphicKeyframeForEdit(layer);
        if (keyframe is null)
            layer.Opacity = opacity;
        else
            keyframe.Opacity = opacity;
        GraphicOpacityValueText.Text = $"{Math.Round(e.NewValue):0}%";
        SyncGraphicTimelineRange();
        UpdateGraphicLayersPreview();
        QueueProjectChange();
    }

    private void OnGraphicBlendModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingGraphicControls || GetSelectedGraphicLayer() is not { } layer ||
            GraphicBlendModeComboBox.SelectedItem is not ComboBoxItem item)
            return;

        var blendMode = item.Tag?.ToString() ?? "normal";
        if (string.Equals(layer.BlendMode, blendMode, StringComparison.Ordinal))
            return;

        layer.BlendMode = blendMode;
        UpdateGraphicLayersPreview();
        QueueProjectChange();
    }

    private void OnAddGraphicKeyframeClicked(object sender, RoutedEventArgs e)
    {
        if (_project is null || GetSelectedGraphicLayer() is not { } layer)
            return;

        CommitPendingProjectChange();
        var time = Math.Clamp(CurrentTimelinePosition.TotalSeconds, layer.StartSeconds, layer.EndSeconds);
        var state = layer.Evaluate(time);
        var keyframe = FindGraphicKeyframeAtPlayhead(layer);
        if (keyframe is null)
        {
            keyframe = new EditorProjectGraphicKeyframe { TimeSeconds = time };
            layer.Keyframes.Add(keyframe);
        }

        keyframe.AnchorX = state.AnchorX;
        keyframe.AnchorY = state.AnchorY;
        keyframe.Width = state.Width;
        keyframe.Height = state.Height;
        keyframe.Opacity = state.Opacity;
        keyframe.Normalize(_project.DurationSeconds, _project.CanvasWidth, _project.CanvasHeight);
        layer.Keyframes = layer.Keyframes.OrderBy(item => item.TimeSeconds).ToList();
        SyncGraphicTimelineRange();
        SyncSelectedGraphicControls();
        UpdateGraphicLayersPreview();
        QueueProjectChange();
    }

    private void OnDeleteGraphicKeyframeClicked(object sender, RoutedEventArgs e)
    {
        if (GetSelectedGraphicLayer() is not { } layer || FindGraphicKeyframeAtPlayhead(layer) is not { } keyframe)
            return;

        CommitPendingProjectChange();
        layer.Keyframes.Remove(keyframe);
        SyncGraphicTimelineRange();
        SyncSelectedGraphicControls();
        UpdateGraphicLayersPreview();
        QueueProjectChange();
    }

    private void OnPreviousGraphicKeyframeClicked(object sender, RoutedEventArgs e) =>
        SeekToAdjacentGraphicKeyframe(next: false);

    private void OnNextGraphicKeyframeClicked(object sender, RoutedEventArgs e) =>
        SeekToAdjacentGraphicKeyframe(next: true);

    private void SeekToAdjacentGraphicKeyframe(bool next)
    {
        if (GetSelectedGraphicLayer() is not { } layer || layer.Keyframes.Count == 0)
            return;

        PausePreview();
        var time = CurrentTimelinePosition.TotalSeconds;
        var target = next
            ? layer.Keyframes.Where(keyframe => keyframe.TimeSeconds > time + 0.045)
                .OrderBy(keyframe => keyframe.TimeSeconds)
                .FirstOrDefault() ?? layer.Keyframes.OrderBy(keyframe => keyframe.TimeSeconds).First()
            : layer.Keyframes.Where(keyframe => keyframe.TimeSeconds < time - 0.045)
                .OrderByDescending(keyframe => keyframe.TimeSeconds)
                .FirstOrDefault() ?? layer.Keyframes.OrderByDescending(keyframe => keyframe.TimeSeconds).First();
        SeekTo(TimeSpan.FromSeconds(target.TimeSeconds));
        SyncSelectedGraphicControls();
    }

    private void OnGraphicKeyframeEasingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingGraphicControls || GetSelectedGraphicLayer() is not { } layer ||
            FindGraphicKeyframeAtPlayhead(layer) is not { } keyframe ||
            GraphicKeyframeEasingComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        var easing = item.Tag?.ToString() ?? "linear";
        if (string.Equals(keyframe.Easing, easing, StringComparison.Ordinal))
            return;

        keyframe.Easing = easing;
        UpdateGraphicLayersPreview();
        QueueProjectChange();
    }

    private void OnGraphicMaskShapeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingGraphicControls || GetSelectedGraphicLayer() is not { } layer ||
            GraphicMaskShapeComboBox.SelectedItem is not ComboBoxItem item)
            return;

        var maskShape = item.Tag?.ToString() ?? "none";
        if (string.Equals(layer.MaskShape, maskShape, StringComparison.Ordinal))
            return;

        layer.MaskShape = maskShape;
        SyncSelectedGraphicControls();
        UpdateGraphicLayersPreview();
        QueueProjectChange();
    }

    private void OnGraphicMaskRoundnessChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        var layer = GetSelectedGraphicLayer();
        if (_syncingGraphicControls || layer is null)
            return;

        layer.MaskRoundness = Math.Clamp(e.NewValue / 100.0, 0, 0.5);
        GraphicMaskRoundnessValueText.Text = $"{Math.Round(e.NewValue):0}%";
        UpdateGraphicLayersPreview();
        QueueProjectChange();
    }

    private void OnFadeInNoneClicked(object sender, RoutedEventArgs e) => SetFadeIn(0);
    private void OnFadeIn05Clicked(object sender, RoutedEventArgs e) => SetFadeIn(0.5);
    private void OnFadeIn1Clicked(object sender, RoutedEventArgs e) => SetFadeIn(1.0);
    private void OnFadeOutNoneClicked(object sender, RoutedEventArgs e) => SetFadeOut(0);
    private void OnFadeOut05Clicked(object sender, RoutedEventArgs e) => SetFadeOut(0.5);
    private void OnFadeOut1Clicked(object sender, RoutedEventArgs e) => SetFadeOut(1.0);
    private void OnFreeze0Clicked(object sender, RoutedEventArgs e) => SetFreezeFrame(0);
    private void OnFreeze1Clicked(object sender, RoutedEventArgs e) => SetFreezeFrame(1);
    private void OnFreeze2Clicked(object sender, RoutedEventArgs e) => SetFreezeFrame(2);
    private void OnFreeze3Clicked(object sender, RoutedEventArgs e) => SetFreezeFrame(3);
    private void OnTransitionCutClicked(object sender, RoutedEventArgs e) => SetClipTransition("cut");
    private void OnTransitionDissolveClicked(object sender, RoutedEventArgs e) => SetClipTransition("dissolve");
    private void OnTransitionBlackClicked(object sender, RoutedEventArgs e) => SetClipTransition("fadeblack");
    private void OnTransitionWipeClicked(object sender, RoutedEventArgs e) => SetClipTransition("wipeleft");
    private void OnTransitionInheritClicked(object sender, RoutedEventArgs e) => SetClipTransition("inherit");
    private void OnTransitionDuration025Clicked(object sender, RoutedEventArgs e) => SetClipTransitionDuration(0.25);
    private void OnTransitionDuration05Clicked(object sender, RoutedEventArgs e) => SetClipTransitionDuration(0.5);
    private void OnTransitionDuration1Clicked(object sender, RoutedEventArgs e) => SetClipTransitionDuration(1.0);

    private void OnEditorVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingMasterAudioControls)
            return;

        if (VolumeValueText is not null)
            VolumeValueText.Text = $"{Math.Round(e.NewValue):0}%";

        UpdateNativePreviewAudio();
        QueueProjectChange();
    }

    private void OnMuteAudioToggled(object sender, RoutedEventArgs e)
    {
        // Mute-inställningen används alltid vid export. Om vi har en riktig
        // video-preview speglar vi den live så man hör direkt vad som exporteras.
        UpdateNativePreviewAudio();
        QueueProjectChange();
    }

    private void OnClipVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingClipAudioControls)
            return;

        if (ClipVolumeValueText is not null)
            ClipVolumeValueText.Text = $"{Math.Round(e.NewValue):0}%";

        var clip = LayerTimeline.GetSelectedMediaClip();
        if (clip is null)
            return;

        var changed = clip.VolumeKeyframes.Count > 0
            ? LayerTimeline.UpsertSelectedMediaClipVolumeKeyframe(
                CurrentTimelinePosition,
                e.NewValue / 100.0)
            : LayerTimeline.SetSelectedMediaClipAudio(e.NewValue / 100.0, ClipMuteAudioToggle.IsOn);
        if (changed)
        {
            SyncSelectedClipAudioControls();
            UpdateNativePreviewAudio();
            QueueProjectChange();
        }
    }

    private void OnClipMuteAudioToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingClipAudioControls)
            return;

        var clip = LayerTimeline.GetSelectedMediaClip();
        if (clip is null)
            return;

        if (LayerTimeline.SetSelectedMediaClipAudio(clip.Volume, ClipMuteAudioToggle.IsOn))
        {
            UpdateNativePreviewAudio();
            QueueProjectChange();
        }
    }

    private void OnClipNormalizeToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingClipAudioControls)
            return;

        var clip = LayerTimeline.GetSelectedMediaClip();
        if (clip is null)
            return;
        if (LayerTimeline.SetSelectedMediaClipNormalization(
                ClipNormalizeToggle.IsOn && clip.IntegratedLoudnessLufs is not null,
                clip.NormalizationGainDb,
                clip.IntegratedLoudnessLufs))
        {
            SyncSelectedClipAudioControls();
            UpdateNativePreviewAudio();
            QueueProjectChange();
        }
    }

    private void OnClipNoiseReductionToggled(object sender, RoutedEventArgs e)
    {
        if (_syncingClipAudioControls)
            return;

        if (LayerTimeline.SetSelectedMediaClipNoiseReduction(
                ClipNoiseReductionToggle.IsOn,
                ClipNoiseReductionSlider.Value / 100.0))
        {
            SyncSelectedClipAudioControls();
            QueueProjectChange();
        }
    }

    private void OnClipNoiseReductionStrengthChanged(
        object sender,
        Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingClipAudioControls)
            return;

        ClipNoiseReductionValueText.Text = $"{Math.Round(e.NewValue):0}%";
        if (LayerTimeline.SetSelectedMediaClipNoiseReduction(
                ClipNoiseReductionToggle.IsOn,
                e.NewValue / 100.0))
        {
            QueueProjectChange();
        }
    }

    private async void OnDetectClipBeatsClicked(object sender, RoutedEventArgs e)
    {
        var selectedClip = LayerTimeline.GetSelectedMediaClip();
        if (selectedClip is null || !File.Exists(selectedClip.SourcePath))
            return;

        CommitPendingProjectChange();
        var clipId = selectedClip.Id;
        ClipDetectBeatsButton.IsEnabled = false;
        ClipBeatStatusText.Text = "Analyzing beats...";
        try
        {
            var analysis = await _audioSignalAnalysisService.AnalyzeAsync(
                selectedClip.SourcePath,
                selectedClip.TrimStartSeconds,
                selectedClip.TrimEndSeconds);
            var currentClip = LayerTimeline.GetSelectedMediaClip();
            if (analysis is null || currentClip?.Id != clipId)
            {
                ClipBeatStatusText.Text = analysis is null ? "No audio signal found." : string.Empty;
                return;
            }

            var timelineStart = LayerTimeline.GetMediaClipTimelineStart(currentClip.Id).TotalSeconds;
            var beatTimes = analysis.BeatSeconds.Select(seconds =>
                timelineStart + seconds / Math.Max(0.25, currentClip.PlaybackSpeed));
            var count = LayerTimeline.ReplaceBeatMarkers(beatTimes, currentClip.Name);
            ClipBeatStatusText.Text = $"{count} beat markers added.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Klipp] Clip beat detection failed: {ex}");
            ClipBeatStatusText.Text = "Beat detection failed.";
        }
        finally
        {
            SyncSelectedClipAudioControls();
        }
    }

    private async void OnAnalyzeClipLoudnessClicked(object sender, RoutedEventArgs e)
    {
        var selectedClip = LayerTimeline.GetSelectedMediaClip();
        if (selectedClip is null)
            return;

        CommitPendingProjectChange();
        var clipId = selectedClip.Id;
        ClipAnalyzeLoudnessButton.IsEnabled = false;
        ClipLoudnessStatusText.Text = "Analyzing audio...";
        try
        {
            var result = await _audioLoudnessAnalysisService.AnalyzeAsync(
                selectedClip.SourcePath,
                selectedClip.TrimStartSeconds,
                selectedClip.TrimEndSeconds);
            var currentClip = LayerTimeline.GetSelectedMediaClip();
            if (currentClip?.Id != clipId)
                return;
            if (result is null)
            {
                ClipLoudnessStatusText.Text = "Could not measure this clip.";
                return;
            }

            if (LayerTimeline.SetSelectedMediaClipNormalization(
                    enabled: true,
                    result.GainDb,
                    result.IntegratedLoudnessLufs))
            {
                QueueProjectChange();
            }
            UpdateNativePreviewAudio();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Klipp] Clip loudness analysis failed: {ex}");
            ClipLoudnessStatusText.Text = "Could not measure this clip.";
        }
        finally
        {
            SyncSelectedClipAudioControls();
        }
    }

    private void OnAddClipVolumeKeyframeClicked(object sender, RoutedEventArgs e)
    {
        var easing = (ClipVolumeKeyframeEasingComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "linear";
        if (!LayerTimeline.UpsertSelectedMediaClipVolumeKeyframe(
                CurrentTimelinePosition,
                ClipVolumeSlider.Value / 100.0,
                easing))
        {
            return;
        }

        SyncSelectedClipAudioControls();
        UpdateNativePreviewAudio();
        QueueProjectChange();
    }

    private void OnPreviousClipVolumeKeyframeClicked(object sender, RoutedEventArgs e) =>
        SeekSelectedClipVolumeKeyframe(next: false);

    private void OnNextClipVolumeKeyframeClicked(object sender, RoutedEventArgs e) =>
        SeekSelectedClipVolumeKeyframe(next: true);

    private void SeekSelectedClipVolumeKeyframe(bool next)
    {
        if (!LayerTimeline.TryGetSelectedMediaClipVolumeKeyframePosition(
                CurrentTimelinePosition,
                next,
                out var target))
        {
            return;
        }

        PausePreview();
        SeekTo(target);
        SyncSelectedClipAudioControls();
    }

    private void OnDeleteClipVolumeKeyframeClicked(object sender, RoutedEventArgs e)
    {
        if (!LayerTimeline.RemoveSelectedMediaClipVolumeKeyframe(CurrentTimelinePosition))
            return;

        SyncSelectedClipAudioControls();
        UpdateNativePreviewAudio();
        QueueProjectChange();
    }

    private void OnClearClipVolumeKeyframesClicked(object sender, RoutedEventArgs e)
    {
        if (!LayerTimeline.ClearSelectedMediaClipVolumeKeyframes(
                CurrentTimelinePosition,
                preserveCurrentValue: true))
        {
            return;
        }

        SyncSelectedClipAudioControls();
        UpdateNativePreviewAudio();
        QueueProjectChange();
    }

    private void OnClipVolumeKeyframeEasingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingClipAudioControls ||
            ClipVolumeKeyframeEasingComboBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (LayerTimeline.SetSelectedMediaClipVolumeKeyframeEasing(
                CurrentTimelinePosition,
                item.Tag?.ToString() ?? "linear"))
        {
            UpdateNativePreviewAudio();
            QueueProjectChange();
        }
    }

    private void UpdateNativePreviewAudio()
    {
        UpdateAudioTrackPreview(_previewPosition, seekExisting: false);

        if (!_usingNativePreview)
            return;

        if (TryGetPreviewTransition(_previewPosition, out var transition))
        {
            ApplyTransitionPreviewAudio(transition);
            return;
        }

        try
        {
            var clip = GetActivePreviewClip();
            PreviewAudioGainEffect.SetGain(NativePreviewPlayer.MediaPlayer,
                LivePreviewVolume * EvaluatePreviewClipVolume(clip, _previewPosition));
            NativePreviewPlayer.MediaPlayer.IsMuted = (MuteAudioToggle?.IsOn ?? false) ||
                (clip?.Muted ?? false) || (clip?.SourceAudioSeparated ?? false);
        }
        catch
        {
            // Preview can be between MediaSource instances while the playhead crosses a clip boundary.
        }
    }

    private double EvaluatePreviewClipVolume(EditorProjectClip? clip, TimeSpan projectPosition)
    {
        if (clip is null)
            return 1;

        var localTime = LayerTimeline.GetMediaClipLocalTime(clip.Id, projectPosition);
        var normalizationGain = clip.NormalizeAudio
            ? DecibelsToLinear(clip.NormalizationGainDb)
            : 1;
        return Math.Clamp(clip.EvaluateVolume(localTime) * normalizationGain, 0, 8);
    }

    private static double DecibelsToLinear(double decibels) =>
        Math.Pow(10, Math.Clamp(decibels, -24, 12) / 20);

    private void UpdateAudioMeters()
    {
        if (_project is null || (MuteAudioToggle?.IsOn ?? false))
        {
            UpdateAudioMeter(0, 0, hasSignalData: _audioSignalAnalyses.Count > 0);
            return;
        }

        var left = 0d;
        var right = 0d;
        var hasSignalData = false;

        void AddClip(EditorProjectClip clip, double transitionGain)
        {
            if (clip.Muted || clip.SourceAudioSeparated || !_project.IsTrackActive(clip.TrackId) || !TryGetAudioSignalFrame(
                    clip.SourcePath,
                    clip.TrimStartSeconds +
                    LayerTimeline.GetMediaClipLocalTime(clip.Id, _previewPosition) * clip.PlaybackSpeed,
                    out var frame))
            {
                return;
            }

            hasSignalData = true;
            var gain = LivePreviewVolume * EvaluatePreviewClipVolume(clip, _previewPosition) * transitionGain;
            left += frame.PeakLeft * gain;
            right += frame.PeakRight * gain;
        }

        if (TryGetPreviewTransition(_previewPosition, out var transition))
        {
            AddClip(transition.OutgoingClip, 1 - transition.Progress);
            AddClip(transition.IncomingClip, transition.Progress);
        }
        else if (LayerTimeline.FindMediaClipAt(_previewPosition, EditorClipTrack.Primary) is { } clip)
        {
            AddClip(clip, 1);
        }

        foreach (var track in _project.AudioTracks)
        {
            var localSeconds = _previewPosition.TotalSeconds - track.TimelineStartSeconds;
            if (track.Muted || !_project.IsTrackActive(track.TrackId) || localSeconds < 0 || localSeconds >= track.EffectiveDurationSeconds)
                continue;
            if (!TryGetAudioSignalFrame(
                    track.SourcePath,
                    track.TrimStartSeconds + localSeconds * track.PlaybackSpeed,
                    out var frame, track.SourceAudioStreamIndex))
            {
                continue;
            }

            hasSignalData = true;
            var fadeGain = 1d;
            if (track.FadeInSeconds > 0.001)
                fadeGain *= Math.Clamp(localSeconds / track.FadeInSeconds, 0, 1);
            if (track.FadeOutSeconds > 0.001)
                fadeGain *= Math.Clamp(
                    (track.EffectiveDurationSeconds - localSeconds) / track.FadeOutSeconds,
                    0,
                    1);
            var normalizationGain = track.NormalizeAudio
                ? DecibelsToLinear(track.NormalizationGainDb)
                : 1;
            var duckingGain = GetPreviewAudioDuckingGain(track, _previewPosition);
            var gain = LivePreviewVolume * track.EvaluateVolume(localSeconds) * normalizationGain * fadeGain * duckingGain;
            left += frame.PeakLeft * gain;
            right += frame.PeakRight * gain;
        }

        UpdateAudioMeter(left, right, hasSignalData);
    }

    private bool TryGetAudioSignalFrame(
        string sourcePath,
        double sourceSeconds,
        out AudioSignalFrame frame, int audioStreamIndex = 0)
    {
        frame = null!;
        if (string.IsNullOrWhiteSpace(sourcePath))
            return false;

        string fullPath;
        try { fullPath = Path.GetFullPath(sourcePath); }
        catch { return false; }
        if (!_audioSignalAnalyses.TryGetValue(AudioSignalKey(fullPath, audioStreamIndex), out var analysis))
            return false;

        frame = analysis.GetFrame(sourceSeconds)!;
        return frame is not null;
    }

    private void UpdateAudioMeter(double left, double right, bool hasSignalData)
    {
        if (AudioMeterLeftTrack is null || AudioMeterRightTrack is null)
            return;

        left = Math.Max(0, left);
        right = Math.Max(0, right);
        var currentPeak = Math.Max(left, right);
        _audioMeterPeakHold = Math.Max(currentPeak, _audioMeterPeakHold * 0.94);
        var meterLeft = AmplitudeToMeterPosition(left);
        var meterRight = AmplitudeToMeterPosition(right);
        AudioMeterLeftFill.Width = Math.Max(0, AudioMeterLeftTrack.ActualWidth - 2) * meterLeft;
        AudioMeterRightFill.Width = Math.Max(0, AudioMeterRightTrack.ActualWidth - 2) * meterRight;

        var displayPeak = Math.Max(currentPeak, _audioMeterPeakHold);
        var decibels = displayPeak > 0.00001 ? 20 * Math.Log10(displayPeak) : double.NegativeInfinity;
        AudioMeterPeakText.Text = !hasSignalData && _audioSignalAnalysisInFlight.Count > 0
            ? "Analyzing..."
            : double.IsNegativeInfinity(decibels)
                ? "-∞ dBFS"
                : $"{Math.Max(-60, decibels):0.0} dBFS";
        var color = currentPeak >= 1
            ? Color.FromArgb(255, 248, 113, 113)
            : currentPeak >= 0.25
                ? Color.FromArgb(255, 250, 204, 21)
                : Color.FromArgb(255, 45, 212, 191);
        var brush = new SolidColorBrush(color);
        AudioMeterLeftFill.Background = brush;
        AudioMeterRightFill.Background = brush;
        AudioMeterPeakText.Foreground = new SolidColorBrush(color);
    }

    private static double AmplitudeToMeterPosition(double amplitude)
    {
        if (amplitude <= 0.001)
            return 0;
        var decibels = Math.Clamp(20 * Math.Log10(amplitude), -60, 0);
        return (decibels + 60) / 60;
    }

    private static string FormatLoudnessStatus(double? integratedLoudnessLufs, double gainDb)
    {
        if (integratedLoudnessLufs is null)
            return "Not analyzed";
        var gainPrefix = gainDb >= 0 ? "+" : string.Empty;
        return $"{integratedLoudnessLufs:0.0} LUFS  |  {gainPrefix}{gainDb:0.0} dB";
    }

    private void OnSubtitleTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingTimelineState)
        {
            UpdateSubtitlePreview();
            return;
        }

        LayerTimeline.SetLayerEnabled(
            EditorTimelineLayer.Subtitle,
            !string.IsNullOrWhiteSpace(SubtitleTextBox.Text),
            CurrentTimelinePosition,
            GetLayerLabel(SubtitleTextBox.Text, _subtitleStyle));
        UpdateSubtitlePreview();
        QueueProjectChange();
    }

    private void OnSubtitlePlainClicked(object sender, RoutedEventArgs e) => SetSubtitleStyle("subtitle");
    private void OnSubtitleBoldClicked(object sender, RoutedEventArgs e) => SetSubtitleStyle("bold");
    private void OnSubtitleHighlightClicked(object sender, RoutedEventArgs e) => SetSubtitleStyle("highlight");
    private void OnSubtitlePixelClicked(object sender, RoutedEventArgs e) => SetSubtitleStyle("pixel");
    private void OnSubtitleSizeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_syncingSubtitleSizeControls)
            return;

        SetSubtitleSize(e.NewValue);
    }

    private void OnSubtitlePositionTopClicked(object sender, RoutedEventArgs e) => SetSubtitlePosition("top");
    private void OnSubtitlePositionCenterClicked(object sender, RoutedEventArgs e) => SetSubtitlePosition("center");
    private void OnSubtitlePositionBottomClicked(object sender, RoutedEventArgs e) => SetSubtitlePosition("bottom");
    private async void OnTranscribeSelectedClipClicked(object sender, RoutedEventArgs e)
    {
        if (_project is null || _transcriptionCts is not null)
            return;

        var selectedClip = LayerTimeline.GetSelectedMediaClip();
        if (selectedClip is null || !File.Exists(selectedClip.SourcePath))
        {
            TranscriptionStatusText.Text = "Select one available timeline clip";
            return;
        }

        var selectedClipId = selectedClip.Id;
        var language = (TranscriptionLanguageComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        _transcriptionCts = new CancellationTokenSource();
        SetTranscriptionBusy(true, "Preparing local transcription");
        var progress = new Progress<WhisperTranscriptionProgress>(value =>
        {
            TranscriptionProgressBar.IsIndeterminate = value.Value <= 0;
            TranscriptionProgressBar.Value = Math.Clamp(value.Value * 100, 0, 100);
            TranscriptionStatusText.Text = value.Status;
        });

        try
        {
            var result = await _whisperTranscriptionService.TranscribeAsync(
                selectedClip.SourcePath,
                selectedClip.TrimStartSeconds,
                selectedClip.TrimEndSeconds,
                language,
                progress,
                _transcriptionCts.Token);
            if (_project is null)
                return;

            var currentClip = _project.Clips.FirstOrDefault(clip =>
                string.Equals(clip.Id, selectedClipId, StringComparison.Ordinal));
            if (currentClip is null)
            {
                TranscriptionStatusText.Text = "The source clip is no longer on the timeline";
                return;
            }

            var timelineStart = LayerTimeline.GetMediaClipTimelineStart(currentClip.Id).TotalSeconds;
            var mappedCues = WhisperTranscriptionService.MapCuesToTimeline(
                result.Cues,
                timelineStart,
                currentClip.PlaybackSpeed,
                currentClip.EffectiveDurationSeconds,
                _project.DurationSeconds);
            if (mappedCues.Count == 0)
            {
                TranscriptionStatusText.Text = "No speech was detected in the selected clip";
                return;
            }

            CommitPendingProjectChange();
            _project.SubtitleLayers.RemoveAll(layer =>
                string.Equals(layer.Origin, "transcription", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(layer.SourceClipId, currentClip.Id, StringComparison.Ordinal));
            foreach (var cue in mappedCues)
            {
                var text = cue.Text.Trim();
                _project.SubtitleLayers.Add(new EditorProjectTextLayer
                {
                    Name = text.Length > 24 ? $"{text[..24]}..." : text,
                    Text = text,
                    Style = _subtitleStyle,
                    Animation = "none",
                    Origin = "transcription",
                    SourceClipId = currentClip.Id,
                    FontFamily = _subtitleFontFamily,
                    FontFilePath = _subtitleFontFilePath,
                    FontFamilyName = _subtitleFontFamilyName,
                    Color = _subtitleColor,
                    AnchorX = _subtitleAnchorX,
                    AnchorY = _subtitleAnchorY,
                    Size = _subtitleSize,
                    BoxWidth = _subtitleBoxWidth,
                    StartSeconds = cue.Start.TotalSeconds,
                    EndSeconds = cue.End.TotalSeconds
                });
            }

            _project.Normalize();
            SyncSubtitleLayers(_project);
            UpdatePreviewOverlays();
            QueueProjectChange();
            SeekTo(mappedCues[0].Start);
            TranscriptionStatusText.Text = $"{mappedCues.Count} editable caption blocks added";
            ProjectStatusText.Text = "Automatic captions added";
        }
        catch (OperationCanceledException)
        {
            TranscriptionStatusText.Text = "Transcription cancelled";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Klipp] Local transcription failed: {ex}");
            TranscriptionStatusText.Text = ex.Message;
            ProjectStatusText.Text = "Transcription failed";
        }
        finally
        {
            _transcriptionCts?.Dispose();
            _transcriptionCts = null;
            SetTranscriptionBusy(false);
            UpdateTranscriptionReadyState(preserveStatus: true);
        }
    }

    private void OnCancelTranscriptionClicked(object sender, RoutedEventArgs e)
    {
        CancelTranscriptionButton.IsEnabled = false;
        TranscriptionStatusText.Text = "Cancelling transcription...";
        _transcriptionCts?.Cancel();
    }

    private void SetTranscriptionBusy(bool isBusy, string? status = null)
    {
        TranscribeSelectedClipButton.IsEnabled = !isBusy;
        TranscriptionLanguageComboBox.IsEnabled = !isBusy;
        CancelTranscriptionButton.IsEnabled = isBusy;
        TranscriptionProgressHost.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        if (isBusy)
        {
            TranscriptionProgressBar.Value = 0;
            TranscriptionProgressBar.IsIndeterminate = true;
        }
        else
        {
            TranscriptionProgressBar.IsIndeterminate = false;
        }
        if (!string.IsNullOrWhiteSpace(status))
            TranscriptionStatusText.Text = status;
    }

    private void UpdateTranscriptionReadyState(bool preserveStatus = false)
    {
        TranscriptionModelStatusText.Text = _whisperTranscriptionService.IsReady
            ? "Whisper base · ready"
            : "Whisper base · 142 MB first setup";
        if (!preserveStatus && _transcriptionCts is null)
            TranscriptionStatusText.Text = "Select one timeline clip";
    }

    private void OnImportSubtitleSrtClicked(object sender, RoutedEventArgs e) =>
        SubtitleSrtImportRequested?.Invoke(this, EventArgs.Empty);
    private void OnExportSubtitleSrtClicked(object sender, RoutedEventArgs e) =>
        SubtitleSrtExportRequested?.Invoke(this, EventArgs.Empty);

    private void SeekTo(TimeSpan position)
    {
        ResetTransitionPreviewFailure();
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        if (position > _duration)
            position = _duration;
        _previewPosition = position;

        var lookupPosition = position >= _duration
            ? _duration - TimeSpan.FromMilliseconds(1)
            : position;
        var clip = ResolvePrimaryPreviewClipAt(lookupPosition);
        if (clip is not null)
        {
            if (!string.Equals(_activePreviewClipId, clip.Id, StringComparison.Ordinal))
            {
                LoadPreviewClipAt(clip, position);
            }
            else if (_usingNativePreview)
            {
                try
                {
                    RequestPrimaryPreviewSeek(clip, position);
                }
                catch
                {
                    // MediaOpened använder pending-positionen om spelaren inte är redo.
                }
            }
            else
            {
                // Retain the latest scrub target while MediaOpened is pending.
                _pendingPreviewSourcePosition = GetSourcePosition(clip, position);
            }
        }
        else if (_usingNativePreview || _isPreviewOpening || _activePreviewClipId is not null)
        {
            SynchronizePrimaryPreview(position, seekExisting: true);
        }

        UpdateTransitionPreview(position, seekExisting: true);
        UpdateOverlayPreview(position, seekExisting: true);
        UpdateAudioTrackPreview(position, seekExisting: true);
        UpdatePlaybackUi();
    }

    private void LoadPreviewClipAt(EditorProjectClip clip, TimeSpan projectPosition, bool allowContinuousPlayback = false)
    {
        if (!File.Exists(clip.SourcePath))
        {
            _activePreviewClipId = clip.Id;
            _filePath = clip.SourcePath;
            _nativePreviewMediaPath = null;
            _usingNativePreview = false;
            _isPreviewOpening = false;
            NativePreviewPlayer.MediaPlayer.Source = null;
            PreviewImage.Source = null;
            PreviewImage.Opacity = 0;
            PreviewFallback.Visibility = Visibility.Visible;
            UpdateSelectedClipTransformPreview();
            return;
        }

        QueueAudioSignalAnalyses([clip.SourcePath]);
        var desiredPreviewPath = ResolvePreviewMediaPath(clip.SourcePath);
        var continueSource = allowContinuousPlayback &&
            CanContinuePrimarySource(GetActivePreviewClip(), clip, desiredPreviewPath);
        var reuseCurrentSource =
            (_usingNativePreview || _isPreviewOpening) &&
            string.Equals(_filePath, clip.SourcePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_nativePreviewMediaPath, desiredPreviewPath, StringComparison.OrdinalIgnoreCase);
        _activePreviewClipId = clip.Id;
        _pendingPreviewSourcePosition = GetSourcePosition(clip, projectPosition);
        if (allowContinuousPlayback && !continueSource && TryUsePreparedPreviewCut(clip, projectPosition, desiredPreviewPath))
        {
            UpdateSelectedClipTransformPreview();
            UpdateVfxPreview();
            return;
        }
        if (reuseCurrentSource && _usingNativePreview && !continueSource)
        {
            try
            {
                RequestPrimaryPreviewSeek(clip, projectPosition);
            }
            catch
            {
                // Om återanvändning misslyckas laddas källan om nedan.
                reuseCurrentSource = false;
            }
        }

        if (reuseCurrentSource)
        {
            UpdateSelectedClipTransformPreview();
            UpdateVfxPreview();
            return;
        }

        _filePath = clip.SourcePath;
        _usingNativePreview = false;
        QueueThumbnailLoad(clip.SourcePath);
        QueueWaveformLoad(clip.SourcePath);
        LoadNativePreview(clip.SourcePath);
        UpdateSelectedClipTransformPreview();
        UpdateVfxPreview();
    }

    private TimeSpan GetSourcePosition(EditorProjectClip clip, TimeSpan projectPosition)
    {
        var timelineStart = LayerTimeline.GetMediaClipTimelineStart(clip.Id);
        var localSeconds = Math.Max(0, (projectPosition - timelineStart).TotalSeconds);
        return TimeSpan.FromSeconds(Math.Clamp(
            clip.TrimStartSeconds + localSeconds * clip.PlaybackSpeed,
            clip.TrimStartSeconds,
            clip.TrimEndSeconds));
    }

    private EditorProjectClip? GetActivePreviewClip()
    {
        return LayerTimeline.FindMediaClipById(_activePreviewClipId);
    }

    private EditorProjectClip? GetActiveOverlayPreviewClip()
    {
        return LayerTimeline.FindMediaClipById(_activeOverlayPreviewClipId);
    }

    private EditorProjectClip? GetActiveTransitionPreviewClip()
    {
        return LayerTimeline.FindMediaClipById(_activeTransitionPreviewClipId);
    }

    private void UpdateTransitionPreview(TimeSpan position, bool seekExisting)
    {
        if (!TryGetPreviewTransition(position, out var transition) ||
            !File.Exists(transition.OutgoingClip.SourcePath) ||
            !File.Exists(transition.IncomingClip.SourcePath))
        {
            ClearTransitionPreview();
            return;
        }

        var incoming = transition.IncomingClip;
        var sourcePosition = GetSourcePosition(incoming, position);
        var previewSourcePath = ResolvePreviewMediaPath(incoming.SourcePath);
        var sourceChanged = !string.Equals(
            _transitionPreviewSourcePath,
            previewSourcePath,
            StringComparison.OrdinalIgnoreCase);
        var clipChanged = !string.Equals(
            _activeTransitionPreviewClipId,
            incoming.Id,
            StringComparison.Ordinal);

        _activeTransitionPreviewClipId = incoming.Id;
        _transitionPreviewSourcePath = previewSourcePath;
        _pendingTransitionSourcePosition = sourcePosition;
        TransitionPreviewRevealHost.Visibility = Visibility.Visible;
        ApplyTransitionPreviewVisuals(transition);
        UpdateSelectedClipTransformPreview();

        if (clipChanged || sourceChanged)
        {
            try
            {
                EnsureTransitionSeekEvents();
                _transitionPreviewSourceVersion++;
                _transitionDecoderSeek.Reset();
                _isTransitionPreviewOpening = true;
                ClearClipEffectPreview(TransitionEffectPreviewHost);
                _transitionEffectPreviewSignature = null;
                TransitionPreviewHost.Opacity = 0;
                TransitionPreviewPlayer.MediaPlayer.IsVideoFrameServerEnabled = true;
                TransitionPreviewPlayer.Source = MediaSource.CreateFromUri(new Uri(previewSourcePath));
            }
            catch
            {
                ClearTransitionPreview();
            }
            return;
        }

        if (_isTransitionPreviewOpening)
        {
            TransitionPreviewHost.Opacity = 0;
            return;
        }

        try
        {
            var session = TransitionPreviewPlayer.MediaPlayer.PlaybackSession;
            session.PlaybackRate = incoming.PlaybackSpeed;
            var drift = Math.Abs((session.Position - sourcePosition).TotalSeconds);
            if (seekExisting || drift > 0.14)
                RequestDecoderSeek(TransitionPreviewPlayer.MediaPlayer, _transitionDecoderSeek, sourcePosition, force: seekExisting);
            if (_isPreviewPlaying && !_wasPreviewBuffering)
            {
                if (session.PlaybackState != MediaPlaybackState.Playing) TransitionPreviewPlayer.MediaPlayer.Play();
            }
            else TransitionPreviewPlayer.MediaPlayer.Pause();
        }
        catch
        {
            // MediaOpened applies the pending source position when the decoder is ready.
        }
    }

    private bool TryGetPreviewTransition(TimeSpan position, out PreviewTransition transition)
    {
        transition = null!;
        if (_project is null || FindPrimaryPreviewClipAt(position) is not { } incoming ||
            !_project.IsTrackActive(incoming.TrackId) ||
            (incoming.Id == _failedTransitionPreviewClipId && incoming.SourcePath == _failedTransitionPreviewSourcePath) ||
            _project.GetTransitionPredecessor(incoming) is not { } outgoing)
            return false;

        var overlap = _project.GetTransitionOverlapSeconds(outgoing, incoming);
        var local = position.TotalSeconds - incoming.TimelineStartSeconds;
        if (overlap <= 0.001 || local < -0.001 || local >= overlap - 0.001)
            return false;

        transition = new PreviewTransition(outgoing, incoming,
            _project.GetTransitionStyle(incoming), Math.Clamp(local / overlap, 0, 1));
        return true;
    }

    private void ApplyTransitionPreviewVisuals(PreviewTransition transition)
    {
        var progress = Math.Clamp(transition.Progress, 0, 1);
        var width = Math.Max(1, PreviewSurface.ActualWidth > 0 ? PreviewSurface.ActualWidth : PreviewSurface.Width);
        var height = Math.Max(1, PreviewSurface.ActualHeight > 0 ? PreviewSurface.ActualHeight : PreviewSurface.Height);
        TransitionPreviewRevealGeometry.Rect = new Rect(0, 0, width, height);

        PrimaryTransitionMotionTransform.TranslateX = PrimaryTransitionMotionTransform.TranslateY = 0;
        IncomingTransitionMotionTransform.TranslateX = IncomingTransitionMotionTransform.TranslateY = 0;
        TransitionWhiteDipOverlay.Visibility = Visibility.Collapsed;

        switch (transition.Style)
        {
            case "fadeblack":
                PrimaryPreviewHost.Opacity = progress < 0.5 ? 1 - progress * 2 : 0;
                TransitionPreviewHost.Opacity = progress < 0.5 ? 0 : (progress - 0.5) * 2;
                break;
            case "fadewhite":
                PrimaryPreviewHost.Opacity = progress < 0.5 ? 1 : 0;
                TransitionPreviewHost.Opacity = progress < 0.5 ? 0 : 1;
                TransitionWhiteDipOverlay.Visibility = Visibility.Visible;
                TransitionWhiteDipOverlay.Opacity = progress < 0.5 ? progress * 2 : (1 - progress) * 2;
                break;
            case "wipeleft":
                PrimaryPreviewHost.Opacity = 1;
                TransitionPreviewHost.Opacity = 1;
                var revealWidth = width * progress;
                TransitionPreviewRevealGeometry.Rect = new Rect(width - revealWidth, 0, revealWidth, height);
                break;
            case "wiperight":
                PrimaryPreviewHost.Opacity = TransitionPreviewHost.Opacity = 1;
                TransitionPreviewRevealGeometry.Rect = new Rect(0, 0, width * progress, height);
                break;
            case "wipeup":
                PrimaryPreviewHost.Opacity = TransitionPreviewHost.Opacity = 1;
                TransitionPreviewRevealGeometry.Rect = new Rect(0, height * (1 - progress), width, height * progress);
                break;
            case "wipedown":
                PrimaryPreviewHost.Opacity = TransitionPreviewHost.Opacity = 1;
                TransitionPreviewRevealGeometry.Rect = new Rect(0, 0, width, height * progress);
                break;
            case "slideleft":
            case "slideright":
                PrimaryPreviewHost.Opacity = TransitionPreviewHost.Opacity = 1;
                var directionX = transition.Style == "slideleft" ? -1 : 1;
                PrimaryTransitionMotionTransform.TranslateX = directionX * width * progress;
                IncomingTransitionMotionTransform.TranslateX = -directionX * width * (1 - progress);
                break;
            case "slideup":
            case "slidedown":
                PrimaryPreviewHost.Opacity = TransitionPreviewHost.Opacity = 1;
                var directionY = transition.Style == "slideup" ? -1 : 1;
                PrimaryTransitionMotionTransform.TranslateY = directionY * height * progress;
                IncomingTransitionMotionTransform.TranslateY = -directionY * height * (1 - progress);
                break;
            case "circleopen": case "rectcrop": case "horzopen": case "vertopen": case "diagtl":
                var shapeFrame = EditorTransitionAuditionMath.Evaluate(transition.Style, progress);
                PrimaryPreviewHost.Opacity = shapeFrame.OutgoingOpacity;
                TransitionPreviewHost.Opacity = shapeFrame.IncomingOpacity;
                TransitionPreviewRevealGeometry.Rect = new Rect(shapeFrame.RevealX * width, shapeFrame.RevealY * height,
                    shapeFrame.RevealWidth * width, shapeFrame.RevealHeight * height);
                break;
            default:
                PrimaryPreviewHost.Opacity = 1;
                TransitionPreviewHost.Opacity = progress;
                break;
        }

        ApplyTransitionShapeMask(TransitionPreviewShapeHost,
            EditorTransitionAuditionMath.Evaluate(transition.Style, progress), width, height);
        ApplyTransitionPreviewAudio(transition);
    }

    private void ApplyTransitionPreviewAudio(PreviewTransition transition)
    {
        var progress = Math.Clamp(transition.Progress, 0, 1);
        var masterMuted = MuteAudioToggle?.IsOn ?? false;
        try
        {
            PreviewAudioGainEffect.SetGain(NativePreviewPlayer.MediaPlayer,
                LivePreviewVolume * EvaluatePreviewClipVolume(transition.OutgoingClip, _previewPosition) * (1 - progress));
            NativePreviewPlayer.MediaPlayer.IsMuted = masterMuted || transition.OutgoingClip.Muted ||
                transition.OutgoingClip.SourceAudioSeparated;
            PreviewAudioGainEffect.SetGain(TransitionPreviewPlayer.MediaPlayer,
                LivePreviewVolume * EvaluatePreviewClipVolume(transition.IncomingClip, _previewPosition) * progress);
            TransitionPreviewPlayer.MediaPlayer.IsMuted = masterMuted || transition.IncomingClip.Muted ||
                transition.IncomingClip.SourceAudioSeparated;
        }
        catch
        {
            // One of the decoders may still be opening; the next timer tick retries.
        }
    }

    private void ClearTransitionPreview()
    {
        _transitionPreviewSourceVersion++;
        _isTransitionPreviewOpening = false;
        _transitionDecoderSeek.Reset();
        _transitionPreviewBlockedSince = 0;
        if (_activeTransitionPreviewClipId is not null)
        {
            try { TransitionPreviewPlayer.Source = null; } catch { }
        }

        _activeTransitionPreviewClipId = null;
        _transitionPreviewSourcePath = null;
        PrimaryTransitionMotionTransform.TranslateX = PrimaryTransitionMotionTransform.TranslateY = 0;
        IncomingTransitionMotionTransform.TranslateX = IncomingTransitionMotionTransform.TranslateY = 0;
        TransitionWhiteDipOverlay.Visibility = Visibility.Collapsed;
        TransitionPreviewRevealHost.Visibility = Visibility.Collapsed;
        TransitionPreviewHost.Opacity = 0;
        PrimaryPreviewHost.Opacity = 1;
        var width = Math.Max(1, PreviewSurface.ActualWidth > 0 ? PreviewSurface.ActualWidth : PreviewSurface.Width);
        var height = Math.Max(1, PreviewSurface.ActualHeight > 0 ? PreviewSurface.ActualHeight : PreviewSurface.Height);
        TransitionPreviewRevealGeometry.Rect = new Rect(0, 0, width, height);

        try
        {
            var clip = GetActivePreviewClip();
            ClearTransitionShapeMask(TransitionPreviewShapeHost);
            PreviewAudioGainEffect.SetGain(NativePreviewPlayer.MediaPlayer,
                LivePreviewVolume * EvaluatePreviewClipVolume(clip, _previewPosition));
            NativePreviewPlayer.MediaPlayer.IsMuted = (MuteAudioToggle?.IsOn ?? false) || (clip?.Muted ?? false) ||
                (clip?.SourceAudioSeparated ?? false);
        }
        catch
        {
        }
    }

    private void UpdateOverlayPreview(TimeSpan position, bool seekExisting)
    {
        UpdateMultiTrackOverlayPreview(position, seekExisting);
    }

    private bool TryAdvancePreviewClip()
    {
        var clip = GetActivePreviewClip();
        if (clip is null)
            return false;

        _previewPosition = TimeSpan.FromSeconds(Klipp.Core.Editing.EditorPreviewClockMath.ContinueAfterClipEnd(
            _previewPosition.TotalSeconds, clip.TimelineStartSeconds, clip.EffectiveDurationSeconds));
        if (_previewPosition >= LayerTimeline.End)
            return false;

        // Continue at the same project time. Empty space is played as black,
        // and an overlapping clip is resolved by its absolute timeline position.
        SynchronizePrimaryPreview(_previewPosition, seekExisting: false);
        _lastPreviewTick = Stopwatch.GetTimestamp();
        UpdateTransitionPreview(_previewPosition, seekExisting: false);
        UpdateOverlayPreview(_previewPosition, seekExisting: false);
        UpdateAudioTrackPreview(_previewPosition, seekExisting: false);
        UpdatePlaybackUi();
        return true;
    }

    /// <summary>
    /// Som SeekTo, men medan användaren drar i timeline begränsas de riktiga
    /// avkodar-seekarna till var ~45 ms så scrubbing känns responsivt istället för
    /// att hacka. Playhead/tidsetiketter uppdateras ändå varje anrop.
    /// </summary>
    private void SeekToThrottled(TimeSpan position)
    {
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;
        if (position > _duration)
            position = _duration;

        if (!LayerTimeline.IsDragging)
        {
            SeekTo(position);
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var elapsedMs = _lastLiveSeekTicks == 0
            ? double.MaxValue
            : (now - _lastLiveSeekTicks) * 1000.0 / Stopwatch.Frequency;

        if (elapsedMs < 45)
        {
            _previewPosition = position;
            _pendingPreviewSourcePosition = ResolvePrimaryPreviewClipAt(position) is { } pendingClip
                ? GetSourcePosition(pendingClip, position) : TimeSpan.Zero;
            UpdatePlaybackUi();
            return;
        }

        _lastLiveSeekTicks = now;
        SeekTo(position);
    }

    private double ExportAudioVolume => EditorVolumeSlider is null
        ? 1.0
        : Math.Clamp(EditorVolumeSlider.Value / 100.0, 0, 2);

    private double LivePreviewVolume => EditorVolumeSlider is null
        ? 1.0
        : Math.Clamp(EditorVolumeSlider.Value / 100.0, 0, 2);

    private TimeSpan CurrentTimelinePosition => _previewPosition;

    private void ResetEditorAdjustments()
    {
        SetPlaybackSpeed(1.0);
        SetPreviewZoom(1.0);
        _textFontFamily = EditorTextTypography.Automatic;
        _textFontFilePath = string.Empty;
        _textFontFamilyName = string.Empty;
        _textColor = EditorTextTypography.Automatic;
        SetTextStyle("plain");
        SetTextAnimation("none");
        SetTextPosition("custom");
        _textAnchorX = DefaultTextAnchorX;
        _textAnchorY = DefaultTextAnchorY;
        _textBoxWidth = DefaultTextBoxWidthPixels;
        _textOpacity = 1;
        _textKeyframes.Clear();
        SyncTextKeyframesToTimeline();
        SetTextSize(DefaultTextSizePixels, recordKeyframe: false);
        _subtitleFontFamily = EditorTextTypography.Automatic;
        _subtitleFontFilePath = string.Empty;
        _subtitleFontFamilyName = string.Empty;
        _subtitleColor = EditorTextTypography.Automatic;
        SetSubtitleStyle("subtitle");
        SetSubtitlePosition("bottom");
        _subtitleBoxWidth = DefaultSubtitleBoxWidthPixels;
        SetSubtitleSize(DefaultSubtitleSizePixels);
        SetVideoFilter("none");
        SetElementOverlay("none");
        SetFadeIn(0);
        SetFadeOut(0);
        SetFreezeFrame(0);
        _clipTransitionStyle = "cut";
        _clipTransitionSeconds = 0.5;
        _previewQuality = "original";
        _temporarilyUseOriginalPreview = false;
        _exportQuality = "high";
        _exportFrameRate = 60;
        _exportEncoder = "auto";
        _exportBitrateMbps = 16;
        _exportResolutionMode = "canvas";
        _exportWidth = _project?.CanvasWidth ?? 1280;
        _exportHeight = _project?.CanvasHeight ?? 720;
        _exportAspectLocked = true;
        _exportLockedAspectRatio = _exportWidth / (double)_exportHeight;
        _exportAudioBitrateKbps = 192;
        LayerTimeline.SetPrimaryTransition("cut", 0.5);
        ClearTransitionPreview();
        ResetVideoTransform();
        SetEditorTool("general");
        LayerTimeline.ResetLayers();

        if (TextOverlayBox is not null)
            TextOverlayBox.Text = string.Empty;
        if (SubtitleTextBox is not null)
            SubtitleTextBox.Text = string.Empty;
        if (EditorVolumeSlider is not null)
            EditorVolumeSlider.Value = 100;
        if (MuteAudioToggle is not null)
            MuteAudioToggle.IsOn = false;

        UpdatePreviewOverlays();
        UpdateAllOptionButtons();
    }

    private void SetEditorTool(string tool)
    {
        if (!string.Equals(tool, "library", StringComparison.Ordinal))
            PauseSourceViewer();
        var toolChanged = !string.Equals(_activeTool, tool, StringComparison.Ordinal);
        _activeTool = tool;
        if (tool != "vfx") CancelLookThumbnailLoading();
        if (tool != "vfx" && CompareOriginalButton?.IsChecked == true)
            CompareOriginalButton.IsChecked = false;

        GeneralInspectorNavigation.Visibility = tool == "general" ? Visibility.Visible : Visibility.Collapsed;
        LibraryPanel.Visibility = tool == "library" ? Visibility.Visible : Visibility.Collapsed;
        GeneralPanel.Visibility = tool == "general" ? Visibility.Visible : Visibility.Collapsed;
        TextPanel.Visibility = tool == "text" ? Visibility.Visible : Visibility.Collapsed;
        ElementsPanel.Visibility = tool == "elements" ? Visibility.Visible : Visibility.Collapsed;
        VfxPanel.Visibility = tool == "vfx" ? Visibility.Visible : Visibility.Collapsed;
        TransitionsPanel.Visibility = tool == "transitions" ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = tool == "audio" ? Visibility.Visible : Visibility.Collapsed;
        SubtitlesPanel.Visibility = tool == "subtitles" ? Visibility.Visible : Visibility.Collapsed;

        UpdateToolButtons();
        ApplyGeneralInspectorSection();
        UpdatePresetPickers();
        UpdateInspectorLayout();
        UpdateVideoTransformPreview(syncControls: false);
        if (tool == "text")
            UpdateTextKeyframeControls();
        UpdateTimelineChrome();
        if (toolChanged)
            DispatcherQueue.TryEnqueue(() => InspectorScrollViewer.ChangeView(null, 0, null, disableAnimation: true));
    }

    private void OnEditorWorkspaceSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateInspectorLayout();

    private void UpdateInspectorLayout()
    {
        if (WorkspaceInspectorColumn is null || ToolInspectorColumn is null || EditorWorkspaceRoot is null)
            return;

        var availableWidth = EditorWorkspaceRoot.ActualWidth;
        var width = availableWidth < 1100 ? 320 : availableWidth < 1500 ? 360 : 400;
        WorkspaceInspectorColumn.Width = new GridLength(width);
        ToolInspectorColumn.Width = new GridLength(width);
    }

    private void SetPlaybackSpeed(double speed)
    {
        _playbackSpeed = Math.Clamp(speed, 0.5, 2.0);
        _lastPreviewTick = 0;

        if (!_suppressProjectChanges && !_isLoadingClip)
            LayerTimeline.SetSelectedMediaClipPlaybackSpeed(_playbackSpeed);

        if (_usingNativePreview)
        {
            try { NativePreviewPlayer.MediaPlayer.PlaybackSession.PlaybackRate = GetActivePreviewClip()?.PlaybackSpeed ?? 1; }
            catch { /* ignorera — UI visar ändå rätt vald hastighet */ }
        }

        UpdateSpeedButtons();
        UpdatePlaybackUi();
        QueueProjectChange();
    }

    private void SetPreviewZoom(double zoom)
    {
        _previewZoom = Math.Clamp(zoom, 0.25, 2.0);
        UpdatePreviewStageZoom();
    }

    private void SetTextStyle(string style)
    {
        _textStyle = style;
        UpdateTextButtons();
        UpdateTextOverlayPreview();
        if (!string.IsNullOrWhiteSpace(TextOverlayBox.Text))
        {
            LayerTimeline.SetLayerEnabled(
                EditorTimelineLayer.Text,
                true,
                CurrentTimelinePosition,
                GetLayerLabel(TextOverlayBox.Text, _textStyle));
        }
        QueueProjectChange();
    }

    private void OnTextFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTypographyControls || TextFontComboBox.SelectedItem is not ComboBoxItem item)
            return;
        _textFontFamily = EditorTextTypography.NormalizeFontFamily(item.Tag?.ToString());
        _textFontFilePath = string.Empty;
        _textFontFamilyName = string.Empty;
        UpdateTextTypographyControls();
        UpdateTextOverlayPreview();
        QueueProjectChange();
    }

    private void OnTextColorSwatchClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string color })
            SetTextColor(color);
    }

    private void OnTextColorHexLostFocus(object sender, RoutedEventArgs e) => ApplyTextHexColor();

    private void OnTextColorHexKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        ApplyTextHexColor();
        e.Handled = true;
    }

    private void OnResetTextColorClicked(object sender, RoutedEventArgs e) =>
        SetTextColor(EditorTextTypography.Automatic);

    private void ApplyTextHexColor()
    {
        if (_syncingTypographyControls || TextColorHexBox is null)
            return;
        if (!TryNormalizeExplicitTextColor(TextColorHexBox.Text, out var color))
        {
            ProjectStatusText.Text = "Use a color like #FFFFFF";
            UpdateTextTypographyControls();
            return;
        }
        SetTextColor(color);
    }

    private void SetTextColor(string color)
    {
        _textColor = EditorTextTypography.NormalizeColor(color);
        UpdateTextTypographyControls();
        UpdateTextOverlayPreview();
        QueueProjectChange();
    }

    private void SetTextAnimation(string animation)
    {
        _textAnimation = string.IsNullOrWhiteSpace(animation)
            ? "none"
            : animation.Trim().ToLowerInvariant();
        UpdateTextButtons();
        UpdateTextOverlayPreview();
        QueueProjectChange();
    }

    private void SetTextSize(double size, bool recordKeyframe = true)
    {
        var normalized = Math.Clamp(Math.Round(size), 12, 220);
        if (recordKeyframe && _textKeyframes.Count > 0)
        {
            ApplyTextStateChange(GetCurrentTextState() with { Size = normalized });
            return;
        }

        _textSize = normalized;
        UpdateTextKeyframeControls();
        UpdateTextOverlayPreview();
        if (recordKeyframe)
            QueueProjectChange();
    }

    private EditorProjectTextState GetCurrentTextState() =>
        EditorProjectTextKeyframe.Evaluate(
            _textKeyframes,
            new EditorProjectTextState(_textAnchorX, _textAnchorY, _textSize, _textOpacity),
            CurrentTimelinePosition.TotalSeconds);

    private EditorProjectTextKeyframe? FindTextKeyframeAtPlayhead()
    {
        var time = CurrentTimelinePosition.TotalSeconds;
        return _textKeyframes.FirstOrDefault(keyframe => Math.Abs(keyframe.TimeSeconds - time) <= 1.0 / 60.0);
    }

    private void UpsertTextKeyframe(EditorProjectTextState state)
    {
        var start = LayerTimeline.TextStart.TotalSeconds;
        var end = LayerTimeline.TextEnd.TotalSeconds;
        var time = Math.Clamp(CurrentTimelinePosition.TotalSeconds, start, end);
        if (_textKeyframes.Count == 0 && time > start + 0.001)
        {
            _textKeyframes.Add(new EditorProjectTextKeyframe
            {
                TimeSeconds = start,
                AnchorX = _textAnchorX,
                AnchorY = _textAnchorY,
                Size = _textSize,
                Opacity = _textOpacity,
                Easing = "linear"
            });
        }

        var keyframe = _textKeyframes.FirstOrDefault(item => Math.Abs(item.TimeSeconds - time) <= 1.0 / 60.0);
        if (keyframe is null)
        {
            keyframe = new EditorProjectTextKeyframe { TimeSeconds = time };
            _textKeyframes.Add(keyframe);
        }

        keyframe.AnchorX = Math.Clamp(state.AnchorX, 0, 1);
        keyframe.AnchorY = Math.Clamp(state.AnchorY, 0, 1);
        keyframe.Size = Math.Clamp(state.Size, 12, 220);
        keyframe.Opacity = Math.Clamp(state.Opacity, 0, 1);
        keyframe.Normalize(start, end);
        _textKeyframes = _textKeyframes
            .OrderBy(item => item.TimeSeconds)
            .GroupBy(item => Math.Round(item.TimeSeconds * 120) / 120)
            .Select(group => group.Last())
            .ToList();
    }

    private void ApplyTextStateChange(EditorProjectTextState state)
    {
        state = new EditorProjectTextState(
            Math.Clamp(state.AnchorX, 0, 1),
            Math.Clamp(state.AnchorY, 0, 1),
            Math.Clamp(Math.Round(state.Size), 12, 220),
            Math.Clamp(state.Opacity, 0, 1));
        if (_textKeyframes.Count > 0)
        {
            UpsertTextKeyframe(state);
            SyncTextKeyframesToTimeline();
        }
        else
        {
            _textAnchorX = state.AnchorX;
            _textAnchorY = state.AnchorY;
            _textSize = state.Size;
            _textOpacity = state.Opacity;
        }

        UpdateTextKeyframeControls();
        UpdateTextOverlayPreview();
        QueueProjectChange();
    }

    private void SyncTextKeyframesToTimeline()
    {
        LayerTimeline.SetTextKeyframes(_textKeyframes);
    }

    private void UpdateTextKeyframeControls()
    {
        if (TextOpacitySlider is null || TextKeyframeCountText is null || TextSizeSlider is null)
            return;

        var state = GetCurrentTextState();
        var currentKeyframe = FindTextKeyframeAtPlayhead();
        var enabled = TextOverlayBox is not null && !string.IsNullOrWhiteSpace(TextOverlayBox.Text) && LayerTimeline.TextEnabled;
        _syncingTextSizeControls = true;
        _syncingTextKeyframeControls = true;
        try
        {
            TextSizeSlider.Value = state.Size;
            TextSizeValueText.Text = $"{state.Size:0} px";
            TextOpacitySlider.Value = state.Opacity * 100;
            TextOpacityValueText.Text = $"{Math.Round(state.Opacity * 100):0}%";
            TextKeyframeCountText.Text = _textKeyframes.Count.ToString();
            TextAddKeyframeButton.IsEnabled = enabled;
            TextPreviousKeyframeButton.IsEnabled = enabled && _textKeyframes.Count > 0;
            TextNextKeyframeButton.IsEnabled = enabled && _textKeyframes.Count > 0;
            TextDeleteKeyframeButton.IsEnabled = currentKeyframe is not null;
            TextKeyframeEasingComboBox.IsEnabled = currentKeyframe is not null;
            ClearTextKeyframesButton.IsEnabled = _textKeyframes.Count > 0;
            TextAddKeyframeButtonText.Text = currentKeyframe is null ? "Add keyframe" : "Update keyframe";
            TextKeyframeStatusText.Text = !enabled
                ? "Write or select a text overlay first."
                : currentKeyframe is not null
                    ? $"Keyframe at {FormatTimestamp(TimeSpan.FromSeconds(currentKeyframe.TimeSeconds))}"
                    : _textKeyframes.Count > 0
                        ? "Values are interpolated here. Editing creates a keyframe."
                        : "Animate position, size and opacity from the playhead.";
            TextKeyframeEasingComboBox.SelectedItem = TextKeyframeEasingComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(),
                    currentKeyframe?.Easing ?? "linear",
                    StringComparison.Ordinal));
        }
        finally
        {
            _syncingTextKeyframeControls = false;
            _syncingTextSizeControls = false;
        }
    }

    private void SetTextPosition(string position)
    {
        _textPosition = position;
        if (string.Equals(position, "custom", StringComparison.OrdinalIgnoreCase))
        {
            UpdateTextButtons();
            UpdateTextOverlayPreview();
            QueueProjectChange();
            return;
        }

        var anchor = position switch
        {
            "top" => (0.5, 0.16),
            "center" => (0.5, 0.5),
            _ => (DefaultTextAnchorX, DefaultTextAnchorY)
        };
        ApplyTextStateChange(GetCurrentTextState() with { AnchorX = anchor.Item1, AnchorY = anchor.Item2 });
        UpdateTextButtons();
    }

    private void SetSubtitleStyle(string style)
    {
        _subtitleStyle = style;
        UpdateSubtitleButtons();
        UpdateSubtitlePreview();
        if (!string.IsNullOrWhiteSpace(SubtitleTextBox.Text))
        {
            LayerTimeline.SetLayerEnabled(
                EditorTimelineLayer.Subtitle,
                true,
                CurrentTimelinePosition,
                GetLayerLabel(SubtitleTextBox.Text, _subtitleStyle));
        }
        QueueProjectChange();
    }

    private void OnSubtitleFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingTypographyControls || SubtitleFontComboBox.SelectedItem is not ComboBoxItem item)
            return;
        _subtitleFontFamily = EditorTextTypography.NormalizeFontFamily(item.Tag?.ToString());
        _subtitleFontFilePath = string.Empty;
        _subtitleFontFamilyName = string.Empty;
        UpdateSubtitleTypographyControls();
        UpdateSubtitlePreview();
        QueueProjectChange();
    }

    private void OnSubtitleColorSwatchClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string color })
            SetSubtitleColor(color);
    }

    private void OnSubtitleColorHexLostFocus(object sender, RoutedEventArgs e) => ApplySubtitleHexColor();

    private void OnSubtitleColorHexKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        ApplySubtitleHexColor();
        e.Handled = true;
    }

    private void OnResetSubtitleColorClicked(object sender, RoutedEventArgs e) =>
        SetSubtitleColor(EditorTextTypography.Automatic);

    private void ApplySubtitleHexColor()
    {
        if (_syncingTypographyControls || SubtitleColorHexBox is null)
            return;
        if (!TryNormalizeExplicitTextColor(SubtitleColorHexBox.Text, out var color))
        {
            ProjectStatusText.Text = "Use a color like #FFFFFF";
            UpdateSubtitleTypographyControls();
            return;
        }
        SetSubtitleColor(color);
    }

    private void SetSubtitleColor(string color)
    {
        _subtitleColor = EditorTextTypography.NormalizeColor(color);
        UpdateSubtitleTypographyControls();
        UpdateSubtitlePreview();
        QueueProjectChange();
    }

    private static bool TryNormalizeExplicitTextColor(string? value, out string color)
    {
        color = EditorTextTypography.NormalizeColor(value);
        return !string.Equals(color, EditorTextTypography.Automatic, StringComparison.Ordinal);
    }

    private void SetSubtitleSize(double size)
    {
        _subtitleSize = Math.Clamp(Math.Round(size), 18, 160);
        if (SubtitleSizeSlider is not null)
        {
            _syncingSubtitleSizeControls = true;
            try
            {
                SubtitleSizeSlider.Value = _subtitleSize;
                SubtitleSizeValueText.Text = $"{_subtitleSize:0} px";
            }
            finally
            {
                _syncingSubtitleSizeControls = false;
            }
        }

        UpdateSubtitlePreview();
        QueueProjectChange();
    }

    private void SetSubtitlePosition(string position)
    {
        _subtitlePosition = position;
        (_subtitleAnchorX, _subtitleAnchorY) = position switch
        {
            "top" => (0.5, 0.16),
            "center" => (0.5, 0.5),
            _ => (DefaultSubtitleAnchorX, DefaultSubtitleAnchorY)
        };
        UpdateSubtitleButtons();
        UpdateSubtitlePreview();
        QueueProjectChange();
    }

    private void SetVideoFilter(string filter)
    {
        if (filter != "none")
        {
            AddTimelineEffect(filter);
            return;
        }
        _videoFilter = filter;
        LayerTimeline.SetLayerEnabled(
            EditorTimelineLayer.Vfx,
            !string.Equals(filter, "none", StringComparison.OrdinalIgnoreCase),
            CurrentTimelinePosition,
            GetVfxLabel(filter));
        UpdateFilterButtons();
        UpdateVfxPreview();
        QueueProjectChange();
    }

    private void SetElementOverlay(string overlay)
    {
        _elementOverlay = overlay;
        var enabled = !string.Equals(overlay, "none", StringComparison.OrdinalIgnoreCase);
        var label = enabled ? GetElementLabel(overlay) : "Elements";
        LayerTimeline.SetLayerEnabled(
            EditorTimelineLayer.Element,
            enabled,
            CurrentTimelinePosition,
            label);
        UpdateElementButtons();
        UpdateElementPreview();
        QueueProjectChange();
    }

    private void SetFadeIn(double seconds)
    {
        _fadeInSeconds = seconds;
        UpdateTransitionButtons();
        UpdateFadePreview();
        QueueProjectChange();
    }

    private void SetFadeOut(double seconds)
    {
        _fadeOutSeconds = seconds;
        UpdateTransitionButtons();
        UpdateFadePreview();
        QueueProjectChange();
    }

    private void SetFreezeFrame(double seconds)
    {
        _freezeFrameSeconds = seconds;
        UpdateTransitionButtons();
        UpdateFadePreview();
        QueueProjectChange();
    }

    private void SetClipTransition(string style)
    {
        if (!LayerTimeline.TryGetSelectedMediaClipTransition(out _, out var duration, out _))
        {
            ProjectStatusText.Text = GetTransitionSelectionPrompt();
            UpdateTransitionButtons();
            return;
        }

        CommitPendingProjectChange();
        PausePreview();
        if (LayerTimeline.SetSelectedMediaClipTransition(style, duration))
        {
            _duration = LayerTimeline.Duration;
            ProjectStatusText.Text = style == "inherit" ? "Transition uses project default" : "Transition updated";
            QueueProjectChange();
            CommitPendingProjectChange();
        }
        RefreshTransitionEditPreview();
    }

    private void SetClipTransitionDuration(double seconds)
    {
        if (!LayerTimeline.TryGetSelectedMediaClipTransition(out var style, out _, out var inherited))
        {
            ProjectStatusText.Text = GetTransitionSelectionPrompt();
            UpdateTransitionButtons();
            return;
        }

        var explicitStyle = inherited ? _clipTransitionStyle : style;
        CommitPendingProjectChange();
        PausePreview();
        if (LayerTimeline.SetSelectedMediaClipTransition(explicitStyle, Math.Clamp(seconds, 0.1, 2)))
        {
            _duration = LayerTimeline.Duration;
            ProjectStatusText.Text = "Transition duration updated";
            UpdateTrimUi();
            QueueProjectChange();
            CommitPendingProjectChange();
        }
        RefreshTransitionEditPreview();
    }

    private void UpdatePreviewOverlays()
    {
        UpdateGraphicLayersPreview();
        UpdateExtraTextLayersPreview();
        UpdateTextOverlayPreview();
        UpdateSubtitlePreview();
        UpdateVfxPreview();
        UpdateElementPreview();
        UpdateFadePreview();
    }

    private void UpdateGraphicLayersPreview()
    {
        if (GraphicOverlayCanvas is null || PreviewSurface is null)
            return;

        if (_project is null)
        {
            GraphicOverlayCanvas.Children.Clear();
            _graphicPreviewVisuals.Clear();
            return;
        }

        var visibleIds = new HashSet<string>(StringComparer.Ordinal);
        var visualIndex = 0;
        var position = CurrentTimelinePosition.TotalSeconds;
        var stageScale = Math.Max(0.05, PreviewStageTransform?.ScaleX ?? 1);
        var interactionSize = Math.Clamp(12 / stageScale, 14, 64);
        var interactionBorder = Math.Clamp(2 / stageScale, 2, 8);
        foreach (var layer in _project.GraphicLayers)
        {
            if (layer.IsMissing || position < layer.StartSeconds || position > layer.EndSeconds)
                continue;

            var state = layer.Evaluate(position);
            var selected = string.Equals(layer.Id, _selectedGraphicLayerId, StringComparison.Ordinal);
            visibleIds.Add(layer.Id);
            var signature = JsonSerializer.Serialize(new
            {
                layer.SourcePath, layer.MaskShape, layer.MaskRoundness,
                layer.BlendMode, selected, stageScale
            });
            if (_graphicPreviewVisuals.TryGetValue(layer.Id, out var existing) && existing.Signature == signature)
            {
                UpdateGraphicPreviewVisual(existing, layer, state);
                PlacePreviewVisual(GraphicOverlayCanvas, existing.Frame, visualIndex++);
                continue;
            }
            if (existing is not null)
                GraphicOverlayCanvas.Children.Remove(existing.Frame);
            var image = new Image
            {
                Source = new BitmapImage(new Uri(layer.SourcePath, UriKind.Absolute)),
                Stretch = Stretch.Fill,
                Opacity = 1,
                IsHitTestVisible = false
            };
            var content = new Grid();
            var mediaHost = new Grid { IsHitTestVisible = false };
            ApplyGraphicMaskPreview(mediaHost, layer, state);
            content.Children.Add(mediaHost);
            if (string.Equals(layer.BlendMode, "normal", StringComparison.Ordinal))
            {
                mediaHost.Children.Add(image);
            }
            else
            {
                var blendHost = new Grid { IsHitTestVisible = false };
                mediaHost.Children.Add(blendHost);
                if (!TryApplyGraphicBlendPreview(blendHost, layer, state))
                    mediaHost.Children.Add(image);
            }
            if (selected)
            {
                content.Children.Add(CreateGraphicResizeHandle(
                    "width",
                    HorizontalAlignment.Right,
                    VerticalAlignment.Center,
                    interactionSize,
                    interactionBorder));
                content.Children.Add(CreateGraphicResizeHandle(
                    "height",
                    HorizontalAlignment.Center,
                    VerticalAlignment.Bottom,
                    interactionSize,
                    interactionBorder));
                content.Children.Add(CreateGraphicResizeHandle(
                    "both",
                    HorizontalAlignment.Right,
                    VerticalAlignment.Bottom,
                    interactionSize,
                    interactionBorder));
            }

            var frame = new Border
            {
                Tag = layer.Id,
                Width = state.Width,
                Height = state.Height,
                Child = content,
                Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
                BorderBrush = new SolidColorBrush(selected
                    ? Color.FromArgb(255, 169, 112, 255)
                    : Color.FromArgb(70, 255, 255, 255)),
                BorderThickness = new Thickness(Math.Clamp((selected ? 2 : 1) / stageScale, 1, 8)),
                CornerRadius = new CornerRadius(4)
            };
            ToolTipService.SetToolTip(frame, selected ? "Drag to move graphic" : "Select graphic");
            frame.PointerPressed += OnGraphicOverlayPointerPressed;
            frame.PointerMoved += OnGraphicOverlayPointerMoved;
            frame.PointerReleased += OnGraphicOverlayPointerReleased;
            frame.PointerCanceled += OnGraphicOverlayPointerCanceled;
            Canvas.SetLeft(frame, state.AnchorX * _project.CanvasWidth - state.Width / 2);
            Canvas.SetTop(frame, state.AnchorY * _project.CanvasHeight - state.Height / 2);
            var cachedVisual = new GraphicPreviewVisual(signature, frame, mediaHost);
            _graphicPreviewVisuals[layer.Id] = cachedVisual;
            UpdateGraphicPreviewVisual(cachedVisual, layer, state);
            PlacePreviewVisual(GraphicOverlayCanvas, frame, visualIndex++);
        }
        RemoveInactivePreviewVisuals(GraphicOverlayCanvas, _graphicPreviewVisuals, visibleIds);
    }

    private static void ApplyGraphicMaskPreview(
        Grid host,
        EditorProjectGraphicLayer layer,
        EditorProjectGraphicState state)
    {
        var visual = ElementCompositionPreview.GetElementVisual(host);
        var compositor = visual.Compositor;
        if (string.Equals(layer.MaskShape, "ellipse", StringComparison.Ordinal))
        {
            var geometry = compositor.CreateEllipseGeometry();
            geometry.Center = new Vector2((float)(state.Width / 2), (float)(state.Height / 2));
            geometry.Radius = new Vector2((float)(state.Width / 2), (float)(state.Height / 2));
            visual.Clip = compositor.CreateGeometricClip(geometry);
            return;
        }

        if (string.Equals(layer.MaskShape, "rounded", StringComparison.Ordinal))
        {
            var radius = (float)(Math.Min(state.Width, state.Height) * layer.MaskRoundness);
            var geometry = compositor.CreateRoundedRectangleGeometry();
            geometry.Size = new Vector2((float)state.Width, (float)state.Height);
            geometry.CornerRadius = new Vector2(radius, radius);
            visual.Clip = compositor.CreateGeometricClip(geometry);
            return;
        }

        visual.Clip = null;
    }

    private static bool TryApplyGraphicBlendPreview(
        Grid host,
        EditorProjectGraphicLayer layer,
        EditorProjectGraphicState state)
    {
        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            var effect = new BlendEffect
            {
                Mode = layer.BlendMode switch
                {
                    "multiply" => BlendEffectMode.Multiply,
                    "screen" => BlendEffectMode.Screen,
                    "overlay" => BlendEffectMode.Overlay,
                    "softlight" => BlendEffectMode.SoftLight,
                    "difference" => BlendEffectMode.Difference,
                    _ => BlendEffectMode.Multiply
                },
                Background = new CompositionEffectSourceParameter("Backdrop"),
                Foreground = new CompositionEffectSourceParameter("Graphic")
            };
            var effectBrush = compositor.CreateEffectFactory(effect).CreateBrush();
            effectBrush.SetSourceParameter("Backdrop", compositor.CreateBackdropBrush());

            var surface = LoadedImageSurface.StartLoadFromUri(
                new Uri(layer.SourcePath, UriKind.Absolute),
                new Size(Math.Max(16, state.Width), Math.Max(16, state.Height)));
            var imageBrush = compositor.CreateSurfaceBrush(surface);
            imageBrush.Stretch = CompositionStretch.Fill;
            effectBrush.SetSourceParameter("Graphic", imageBrush);

            var visual = compositor.CreateSpriteVisual();
            visual.Brush = effectBrush;
            visual.Size = new Vector2((float)state.Width, (float)state.Height);
            visual.Opacity = 1;
            ElementCompositionPreview.SetElementChildVisual(host, visual);
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Graphic blend preview fallback: {exception.Message}");
            return false;
        }
    }

    private Border CreateGraphicResizeHandle(
        string mode,
        HorizontalAlignment horizontalAlignment,
        VerticalAlignment verticalAlignment,
        double size,
        double borderThickness)
    {
        var handle = new Border
        {
            Tag = mode,
            Width = size,
            Height = size,
            Background = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 169, 112, 255)),
            BorderThickness = new Thickness(borderThickness),
            CornerRadius = new CornerRadius(size / 2),
            HorizontalAlignment = horizontalAlignment,
            VerticalAlignment = verticalAlignment,
            Margin = new Thickness(-size / 2)
        };
        ToolTipService.SetToolTip(handle, mode switch
        {
            "width" => "Drag to change width",
            "height" => "Drag to change height",
            _ => "Drag to resize"
        });
        handle.PointerPressed += OnGraphicResizeHandlePointerPressed;
        handle.PointerMoved += OnGraphicResizeHandlePointerMoved;
        handle.PointerReleased += OnGraphicResizeHandlePointerReleased;
        handle.PointerCanceled += OnGraphicResizeHandlePointerCanceled;
        return handle;
    }

    private void OnGraphicOverlayPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isResizingGraphicOverlay || sender is not Border { Tag: string id } frame)
            return;

        var point = e.GetCurrentPoint(PreviewSurface);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        var layer = _project?.GraphicLayers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.Ordinal));
        if (layer is null)
            return;

        _selectedGraphicLayerId = layer.Id;
        GraphicLayerList.SelectedItem = layer;
        LayerTimeline.SelectGraphicSegment(layer.Id);
        _isDraggingGraphicOverlay = true;
        _graphicPointerCaptureElement = frame;
        _graphicDragOffset = new Point(
            point.Position.X - Canvas.GetLeft(frame),
            point.Position.Y - Canvas.GetTop(frame));
        frame.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 169, 112, 255));
        frame.BorderThickness = new Thickness(2);
        frame.CapturePointer(e.Pointer);
        SyncSelectedGraphicControls();
        UpdateProjectChrome();
        e.Handled = true;
    }

    private void OnGraphicOverlayPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDraggingGraphicOverlay || sender is not Border frame || _project is null)
            return;

        var point = e.GetCurrentPoint(PreviewSurface);
        if (!point.Properties.IsLeftButtonPressed)
        {
            FinishGraphicOverlayDrag(e);
            return;
        }

        var layer = GetSelectedGraphicLayer();
        if (layer is null)
            return;
        var keyframe = GetOrCreateGraphicKeyframeForEdit(layer);
        var state = keyframe?.ToState() ?? layer.Evaluate(CurrentTimelinePosition.TotalSeconds);
        var left = SnapToCanvasGrid(point.Position.X - _graphicDragOffset.X);
        var top = SnapToCanvasGrid(point.Position.Y - _graphicDragOffset.Y);
        var anchorX = Math.Clamp((left + state.Width / 2) / _project.CanvasWidth, 0, 1);
        var anchorY = Math.Clamp((top + state.Height / 2) / _project.CanvasHeight, 0, 1);
        if (keyframe is null)
        {
            layer.AnchorX = anchorX;
            layer.AnchorY = anchorY;
        }
        else
        {
            keyframe.AnchorX = anchorX;
            keyframe.AnchorY = anchorY;
        }
        Canvas.SetLeft(frame, anchorX * _project.CanvasWidth - state.Width / 2);
        Canvas.SetTop(frame, anchorY * _project.CanvasHeight - state.Height / 2);
        SyncSelectedGraphicControls();
        e.Handled = true;
    }

    private void OnGraphicOverlayPointerReleased(object sender, PointerRoutedEventArgs e) => FinishGraphicOverlayDrag(e);
    private void OnGraphicOverlayPointerCanceled(object sender, PointerRoutedEventArgs e) => FinishGraphicOverlayDrag(e);

    private void FinishGraphicOverlayDrag(PointerRoutedEventArgs e)
    {
        if (!_isDraggingGraphicOverlay)
            return;

        _isDraggingGraphicOverlay = false;
        _graphicPointerCaptureElement?.ReleasePointerCapture(e.Pointer);
        _graphicPointerCaptureElement = null;
        SyncGraphicTimelineRange();
        SyncSelectedGraphicControls();
        UpdateGraphicLayersPreview();
        QueueProjectChange();
        e.Handled = true;
    }

    private void OnGraphicResizeHandlePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border { Tag: string mode } handle || GetSelectedGraphicLayer() is not { } layer)
            return;

        var point = e.GetCurrentPoint(PreviewSurface);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        var keyframe = GetOrCreateGraphicKeyframeForEdit(layer);
        var state = keyframe?.ToState() ?? layer.Evaluate(CurrentTimelinePosition.TotalSeconds);
        _isResizingGraphicOverlay = true;
        _graphicResizeMode = mode;
        _graphicResizeStartPoint = point.Position;
        _graphicResizeStartWidth = state.Width;
        _graphicResizeStartHeight = state.Height;
        _graphicResizeStartAnchorX = state.AnchorX;
        _graphicResizeStartAnchorY = state.AnchorY;
        _graphicPointerCaptureElement = handle;
        handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnGraphicResizeHandlePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isResizingGraphicOverlay || _project is null || GetSelectedGraphicLayer() is not { } layer)
            return;

        var point = e.GetCurrentPoint(PreviewSurface);
        if (!point.Properties.IsLeftButtonPressed)
        {
            FinishGraphicResize(e);
            return;
        }

        var dx = point.Position.X - _graphicResizeStartPoint.X;
        var dy = point.Position.Y - _graphicResizeStartPoint.Y;
        var keyframe = GetOrCreateGraphicKeyframeForEdit(layer);
        var width = keyframe?.Width ?? layer.Width;
        var height = keyframe?.Height ?? layer.Height;
        var anchorX = keyframe?.AnchorX ?? layer.AnchorX;
        var anchorY = keyframe?.AnchorY ?? layer.AnchorY;
        if (_graphicResizeMode is "width" or "both")
        {
            width = Math.Clamp(SnapToCanvasGrid(_graphicResizeStartWidth + dx), 16, _project.CanvasWidth * 2);
            anchorX = Math.Clamp(
                _graphicResizeStartAnchorX + (width - _graphicResizeStartWidth) / (2 * _project.CanvasWidth),
                0,
                1);
        }
        if (_graphicResizeMode is "height" or "both")
        {
            height = Math.Clamp(SnapToCanvasGrid(_graphicResizeStartHeight + dy), 16, _project.CanvasHeight * 2);
            anchorY = Math.Clamp(
                _graphicResizeStartAnchorY + (height - _graphicResizeStartHeight) / (2 * _project.CanvasHeight),
                0,
                1);
        }

        if (keyframe is null)
        {
            layer.Width = width;
            layer.Height = height;
            layer.AnchorX = anchorX;
            layer.AnchorY = anchorY;
        }
        else
        {
            keyframe.Width = width;
            keyframe.Height = height;
            keyframe.AnchorX = anchorX;
            keyframe.AnchorY = anchorY;
        }

        var frame = GraphicOverlayCanvas.Children.OfType<Border>().FirstOrDefault(candidate =>
            string.Equals(candidate.Tag as string, layer.Id, StringComparison.Ordinal));
        if (frame is not null)
        {
            frame.Width = width;
            frame.Height = height;
            Canvas.SetLeft(frame, anchorX * _project.CanvasWidth - width / 2);
            Canvas.SetTop(frame, anchorY * _project.CanvasHeight - height / 2);
        }
        SyncSelectedGraphicControls();
        e.Handled = true;
    }

    private void OnGraphicResizeHandlePointerReleased(object sender, PointerRoutedEventArgs e) => FinishGraphicResize(e);
    private void OnGraphicResizeHandlePointerCanceled(object sender, PointerRoutedEventArgs e) => FinishGraphicResize(e);

    private void FinishGraphicResize(PointerRoutedEventArgs e)
    {
        if (!_isResizingGraphicOverlay)
            return;

        _isResizingGraphicOverlay = false;
        _graphicPointerCaptureElement?.ReleasePointerCapture(e.Pointer);
        _graphicPointerCaptureElement = null;
        _graphicResizeMode = string.Empty;
        SyncGraphicTimelineRange();
        SyncSelectedGraphicControls();
        UpdateGraphicLayersPreview();
        QueueProjectChange();
        e.Handled = true;
    }

    private void UpdateExtraTextLayersPreview()
    {
        if (ExtraTextOverlayCanvas is null || PreviewSurface is null)
            return;

        var width = PreviewSurface.ActualWidth;
        var height = PreviewSurface.ActualHeight;
        if (width < 1 || height < 1 || _project is null)
        {
            ExtraTextOverlayCanvas.Children.Clear();
            _textPreviewVisuals.Clear();
            return;
        }

        var visibleIds = new HashSet<string>(StringComparer.Ordinal);
        var visualIndex = 0;
        var position = CurrentTimelinePosition.TotalSeconds;
        var timedLayers = _project.ExtraTextLayers
            .Select(layer => (Layer: layer, IsSubtitle: false))
            .Concat(_project.SubtitleLayers.Select(layer => (Layer: layer, IsSubtitle: true)));
        foreach (var item in timedLayers.Where(item =>
                     position >= item.Layer.StartSeconds && position <= item.Layer.EndSeconds &&
                     (item.IsSubtitle || !string.Equals(item.Layer.Id, _editingExtraTextLayerId, StringComparison.Ordinal))))
        {
            var layer = item.Layer;
            var state = layer.Evaluate(position);
            var animationProgress = Math.Clamp((position - layer.StartSeconds) / 0.45, 0, 1);
            var minimumWidth = item.IsSubtitle ? 220 : 96;
            var boxWidth = Math.Clamp(layer.BoxWidth, minimumWidth, Math.Max(minimumWidth, width - 48));
            var text = layer.Style is "meme" or "impact"
                ? layer.Text.ToUpperInvariant()
                : layer.Text;
            if (!item.IsSubtitle && string.Equals(layer.Animation, "type", StringComparison.OrdinalIgnoreCase) && text.Length > 0)
            {
                var count = Math.Clamp((int)Math.Ceiling(text.Length * animationProgress), 1, text.Length);
                text = text[..count];
            }
            visibleIds.Add(layer.Id);
            var styleBox = GetTextPreviewVisual(layer, item.IsSubtitle);
            var textBlock = (TextBlock)styleBox.Child;
            if (textBlock.Text != text)
                textBlock.Text = text;
            textBlock.FontSize = state.Size;
            styleBox.Width = boxWidth;
            styleBox.Opacity = state.Opacity * (item.IsSubtitle ? 1 : layer.Animation switch
            {
                "fade" or "pop" or "slide-up" or "slide-left" or "type" => animationProgress,
                _ => 1
            });
            var transform = (CompositeTransform)styleBox.RenderTransform;
            transform.TranslateX = !item.IsSubtitle && layer.Animation == "slide-left" ? (1 - animationProgress) * 72 : 0;
            transform.TranslateY = !item.IsSubtitle && layer.Animation == "slide-up" ? (1 - animationProgress) * 56 : 0;
            transform.ScaleX = transform.ScaleY = !item.IsSubtitle && layer.Animation == "pop" ? 0.78 + 0.22 * animationProgress : 1;
            Canvas.SetLeft(styleBox, Math.Clamp(width * state.AnchorX - boxWidth / 2.0, 0, Math.Max(0, width - boxWidth)));
            Canvas.SetTop(styleBox, Math.Clamp(height * state.AnchorY - state.Size / 1.5, 0, Math.Max(0, height - state.Size * 2)));
            PlacePreviewVisual(ExtraTextOverlayCanvas, styleBox, visualIndex++);
        }
        RemoveInactivePreviewVisuals(ExtraTextOverlayCanvas, _textPreviewVisuals, visibleIds);
    }

    private void UpdateTextOverlayPreview()
    {
        if (TextOverlayBackground is null || TextOverlayStyleBox is null || TextOverlayPreview is null || TextOverlayBox is null)
            return;

        var text = TextOverlayBox.Text.Trim();
        var state = GetCurrentTextState();
        TextOverlayBackground.Visibility = string.IsNullOrWhiteSpace(text) || !LayerTimeline.IsTimeInLayer(EditorTimelineLayer.Text, CurrentTimelinePosition)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var previewText = _textStyle is "meme" or "impact"
            ? text.ToUpperInvariant()
            : text;
        TextOverlayPreview.Text = GetAnimatedPreviewText(previewText);
        if (PreviewSurface is not null && PreviewSurface.ActualWidth > 0)
        {
            var maxWidth = Math.Max(96, PreviewSurface.ActualWidth - 96);
            _textBoxWidth = Math.Clamp(_textBoxWidth, 96, maxWidth);
            TextOverlayStyleBox.Width = _textBoxWidth;
            TextOverlayBackground.MaxWidth = maxWidth + 48;
        }

        TextOverlayPreview.FontFamily = new FontFamily(EditorTextTypography.ResolvePreviewFontFamily(
            _textFontFamily,
            _textStyle,
            _textFontFilePath,
            _textFontFamilyName));
        TextOverlayPreview.FontWeight = _textStyle is "bold" or "meme" or "outline" or "neon" or "shadow" or "impact" or "sticker"
            ? FontWeights.Bold
            : FontWeights.SemiBold;
        TextOverlayPreview.FontSize = state.Size;
        TextOverlayPreview.Foreground = new SolidColorBrush(ParseEditorTextColor(
            EditorTextTypography.ResolveColor(_textColor, _textStyle)));
        TextOverlayStyleBox.Background = new SolidColorBrush(_textStyle == "highlight"
            ? Color.FromArgb(230, 238, 255, 38)
            : _textStyle == "sticker"
                ? Color.FromArgb(232, 169, 112, 255)
            : _textStyle == "caption"
                ? Color.FromArgb(184, 0, 0, 0)
            : _textStyle == "shadow"
                ? Color.FromArgb(128, 0, 0, 0)
            : Color.FromArgb(0, 0, 0, 0));
        TextOverlayStyleBox.BorderBrush = new SolidColorBrush(_textStyle == "neon"
            ? Color.FromArgb(255, 76, 235, 255)
            : _textStyle == "sticker"
                ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(255, 255, 255, 255));
        TextOverlayStyleBox.BorderThickness = new Thickness(_textStyle is "outline" or "neon" or "sticker" ? 2 : 0);
        TextOverlayBackground.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        TextOverlayBackground.BorderBrush = new SolidColorBrush(Color.FromArgb(190, 169, 112, 255));
        TextOverlayBackground.BorderThickness = new Thickness(1.5);
        UpdateTextAnimationPreview(state.Opacity);

        ApplyTextOverlayPlacement();
    }

    private string GetAnimatedPreviewText(string text)
    {
        if (!string.Equals(_textAnimation, "type", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(text))
            return text;

        var progress = GetTextAnimationProgress();
        var count = Math.Clamp((int)Math.Ceiling(text.Length * progress), 1, text.Length);
        return text[..count];
    }

    private void UpdateTextAnimationPreview(double keyframeOpacity)
    {
        if (TextOverlayBackground is null || TextOverlayStyleBox is null || TextOverlayTransform is null)
            return;

        var progress = GetTextAnimationProgress();
        var animationOpacity = _textAnimation switch
        {
            "fade" => progress,
            "pop" => Math.Clamp(progress * 1.35, 0, 1),
            "slide-up" or "slide-left" or "type" => Math.Clamp(progress * 1.25, 0, 1),
            _ => 1
        };
        TextOverlayBackground.Opacity = 1;
        TextOverlayStyleBox.Opacity = Math.Clamp(keyframeOpacity * animationOpacity, 0, 1);

        TextOverlayTransform.TranslateX = _textAnimation == "slide-left"
            ? (1 - progress) * 72
            : 0;
        TextOverlayTransform.TranslateY = _textAnimation == "slide-up"
            ? (1 - progress) * 56
            : 0;

        var popScale = _textAnimation == "pop"
            ? 0.78 + 0.22 * progress
            : 1.0;
        TextOverlayTransform.ScaleX = popScale;
        TextOverlayTransform.ScaleY = popScale;
    }

    private double GetTextAnimationProgress()
    {
        if (LayerTimeline is null)
            return 1;

        var elapsed = Math.Max(0, (CurrentTimelinePosition - LayerTimeline.TextStart).TotalSeconds);
        return Math.Clamp(elapsed / 0.45, 0, 1);
    }

    private void UpdateSubtitlePreview()
    {
        if (SubtitleOverlayBackground is null || SubtitleOverlayStyleBox is null || SubtitleOverlayPreview is null || SubtitleTextBox is null)
            return;

        var text = SubtitleTextBox.Text.Trim();
        SubtitleOverlayBackground.Visibility = string.IsNullOrWhiteSpace(text) || !LayerTimeline.IsTimeInLayer(EditorTimelineLayer.Subtitle, CurrentTimelinePosition)
            ? Visibility.Collapsed
            : Visibility.Visible;
        SubtitleOverlayPreview.Text = text;
        if (PreviewSurface is not null && PreviewSurface.ActualWidth > 0)
        {
            var maxWidth = Math.Max(220, PreviewSurface.ActualWidth - 96);
            _subtitleBoxWidth = Math.Clamp(_subtitleBoxWidth, 220, maxWidth);
            SubtitleOverlayStyleBox.Width = _subtitleBoxWidth;
            SubtitleOverlayBackground.MaxWidth = maxWidth + 48;
        }

        SubtitleOverlayPreview.FontFamily = new FontFamily(EditorTextTypography.ResolvePreviewFontFamily(
            _subtitleFontFamily,
            _subtitleStyle,
            _subtitleFontFilePath,
            _subtitleFontFamilyName));
        SubtitleOverlayPreview.FontWeight = _subtitleStyle is "bold"
            ? FontWeights.Bold
            : FontWeights.SemiBold;
        SubtitleOverlayPreview.FontSize = _subtitleSize;
        SubtitleOverlayPreview.Foreground = new SolidColorBrush(ParseEditorTextColor(
            EditorTextTypography.ResolveColor(_subtitleColor, _subtitleStyle)));
        SubtitleOverlayStyleBox.Background = new SolidColorBrush(_subtitleStyle == "highlight"
            ? Color.FromArgb(238, 238, 255, 38)
            : Color.FromArgb(166, 0, 0, 0));
        SubtitleOverlayStyleBox.BorderThickness = new Thickness(0);
        SubtitleOverlayBackground.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        SubtitleOverlayBackground.BorderBrush = new SolidColorBrush(Color.FromArgb(190, 169, 112, 255));
        SubtitleOverlayBackground.BorderThickness = new Thickness(1.5);

        ApplySubtitleOverlayPlacement();
    }

    private void UpdateVfxPreview()
    {
        if (VfxTintOverlay is null)
            return;
        UpdateAdditionalVideoTrackEffects();

        var activeClip = GetActivePreviewClip();
        bool StageEnabled(string kind) => !IsShowingOriginal(activeClip) && activeClip?.Effects.Any(node => node.Enabled && node.Kind == kind) == true;
        var clipFilter = StageEnabled("look") ? activeClip!.Filter : "none";
        var hasClipFilter = !string.Equals(clipFilter, "none", StringComparison.OrdinalIgnoreCase);
        var filter = hasClipFilter
            ? clipFilter
            : LayerTimeline.IsTimeInLayer(EditorTimelineLayer.Vfx, CurrentTimelinePosition) ? _videoFilter : "none";
        var clipLocalTime = activeClip is null
            ? 0
            : LayerTimeline.GetMediaClipLocalTime(activeClip.Id, CurrentTimelinePosition);
        var filterStrength = IsShowingOriginal(activeClip) ? 0 : activeClip?.EvaluateFilterStrength(clipLocalTime) ?? 1;
        var primaryGpuPreview = TryApplyClipEffectPreview(
            PrimaryPreviewCropHost, PrimaryEffectPreviewHost, activeClip, ref _primaryEffectPreviewSignature);
        TryApplyClipEffectPreview(TransitionPreviewCropHost, TransitionEffectPreviewHost,
            GetActiveTransitionPreviewClip(), ref _transitionEffectPreviewSignature);
        TryApplyClipEffectPreview(OverlayPreviewCropHost, OverlayEffectPreviewHost,
            GetActiveOverlayPreviewClip(), ref _overlayEffectPreviewSignature);
        if (primaryGpuPreview)
        {
            VfxTintOverlay.Opacity = 0;
            ClipBrightnessPreviewOverlay.Opacity = 0;
            ClipTemperaturePreviewOverlay.Opacity = 0;
            ClipHuePreviewOverlay.Opacity = 0;
            ClipVignettePreviewOverlay.Opacity = 0;
            UpdateQuickEffectPreviewStatus();
            return;
        }
        var isActive = !string.Equals(filter, "none", StringComparison.OrdinalIgnoreCase);
        (Color color, double opacity) = isActive ? filter switch
        {
            "warm" => (Color.FromArgb(255, 255, 136, 56), 0.16),
            "cool" => (Color.FromArgb(255, 70, 130, 255), 0.14),
            "cinematic" => (Color.FromArgb(255, 0, 0, 0), 0.15),
            "mono" => (Color.FromArgb(255, 160, 160, 160), 0.14),
            "boost" => (Color.FromArgb(255, 157, 122, 234), 0.08),
            "vintage" => (Color.FromArgb(255, 197, 137, 72), 0.16),
            "vibrant" => (Color.FromArgb(255, 255, 68, 196), 0.08),
            "noir" => (Color.FromArgb(255, 38, 38, 38), 0.22),
            "dream" => (Color.FromArgb(255, 168, 142, 255), 0.14),
            "glitch" => (Color.FromArgb(255, 0, 255, 220), 0.10),
            "vignette" => (Color.FromArgb(255, 0, 0, 0), 0.13),
            _ => (Color.FromArgb(0, 0, 0, 0), 0)
        } : (Color.FromArgb(0, 0, 0, 0), 0);

        SetFallbackPreviewColor(VfxTintOverlay, color);
        VfxTintOverlay.Opacity = opacity * (hasClipFilter ? filterStrength : 1);

        var brightness = StageEnabled("color") ? Math.Clamp(activeClip!.Brightness, -1, 1) : 0;
        var shadows = StageEnabled("shadows") ? Math.Clamp(activeClip!.Shadows, -1, 1) : 0;
        var highlights = StageEnabled("shadows") ? Math.Clamp(activeClip!.Highlights, -1, 1) : 0;
        var gamma = StageEnabled("color") ? Math.Clamp(activeClip!.Gamma, 0.25, 4) : 1;
        var curveLift = StageEnabled("curve") && activeClip?.ToneCurve is { Count: >= 2 } curve
            ? Math.Clamp(curve.Average(point => point.Y - point.X) * 1.4, -0.35, 0.35)
            : 0;
        var previewLuma = Math.Clamp(
            brightness + shadows * 0.12 + highlights * 0.10 + (gamma - 1) * 0.16 + curveLift,
            -1,
            1) * filterStrength;
        SetFallbackPreviewColor(ClipBrightnessPreviewOverlay, previewLuma >= 0
            ? Color.FromArgb(255, 255, 255, 255)
            : Color.FromArgb(255, 0, 0, 0));
        ClipBrightnessPreviewOverlay.Opacity = Math.Abs(previewLuma) * 0.28;

        var temperature = StageEnabled("temperature") ? Math.Clamp(activeClip!.Temperature, -1, 1) : 0;
        SetFallbackPreviewColor(ClipTemperaturePreviewOverlay, temperature >= 0
            ? Color.FromArgb(255, 255, 126, 54)
            : Color.FromArgb(255, 56, 128, 255));
        ClipTemperaturePreviewOverlay.Opacity = Math.Abs(temperature) * 0.18 * filterStrength;

        var hue = StageEnabled("hue") ? Math.Clamp(activeClip!.HueDegrees, -180, 180) : 0;
        SetFallbackPreviewColor(ClipHuePreviewOverlay, GetHuePreviewColor(hue));
        ClipHuePreviewOverlay.Opacity = Math.Abs(hue) / 180.0 * 0.12 * filterStrength;

        var vignette = StageEnabled("vignette") ? Math.Clamp(activeClip!.Vignette, 0, 1) : 0;
        ClipVignettePreviewOverlay.BorderThickness = new Thickness(vignette * filterStrength * 72);
        ClipVignettePreviewOverlay.Opacity = vignette * filterStrength * 0.65;

        UpdateQuickEffectPreviewStatus();
    }

    private bool TryApplyClipEffectPreview(
        Grid sourceHost,
        Grid effectHost,
        EditorProjectClip? clip,
        ref string? currentSignature)
    {
        var localTime = clip is null ? 0 : LayerTimeline.GetMediaClipLocalTime(clip.Id, CurrentTimelinePosition);
        var strength = IsShowingOriginal(clip) ? 0 : clip?.EvaluateFilterStrength(localTime) ?? 0;
        var signature = clip is null ? string.Empty : BuildPreviewEffectSignature(clip);
        var decoderOpening = ReferenceEquals(sourceHost, PrimaryPreviewCropHost) ? _isPreviewOpening
            : ReferenceEquals(sourceHost, OverlayPreviewCropHost) ? _overlayPreviewOpening
            : ReferenceEquals(sourceHost, TransitionPreviewCropHost) && _isTransitionPreviewOpening;
        if (decoderOpening)
            return false;
        if (string.IsNullOrEmpty(signature) || !HasPreviewEffects(clip!))
        {
            if (currentSignature is not null)
            {
                ClearClipEffectPreview(effectHost);
                currentSignature = null;
            }
            return false;
        }
        if (sourceHost.Clip is RectangleGeometry crop &&
            (effectHost.Clip is not RectangleGeometry effectCrop || effectCrop.Rect != crop.Rect))
            effectHost.Clip = new RectangleGeometry { Rect = crop.Rect };
        if (string.Equals(currentSignature, "fallback|" + signature, StringComparison.Ordinal))
            return false;
        var player = ReferenceEquals(sourceHost, PrimaryPreviewCropHost) ? NativePreviewPlayer.MediaPlayer
            : ReferenceEquals(sourceHost, TransitionPreviewCropHost) ? TransitionPreviewPlayer.MediaPlayer
            : OverlayPreviewPlayer.MediaPlayer;
        if (KeepNativePreviewFallback(player, signature, ref currentSignature))
            return false;
        if (_clipEffectPreviews.TryGetValue(effectHost, out var existing) && existing.UsesPlayer(player))
        {
            existing.Update(clip!, strength, GetPreviewTimelineEffectLayers(clip!),
                CurrentTimelinePosition.TotalSeconds, IsShowingOriginal(clip), signature);
            currentSignature = signature;
            return true;
        }
        try
        {
            ClearClipEffectPreview(effectHost);
            _clipEffectPreviews[effectHost] = new ClipEffectPreviewResources(player, effectHost, clip!, strength,
                GetPreviewTimelineEffectLayers(clip!), CurrentTimelinePosition.TotalSeconds, IsShowingOriginal(clip),
                signature, DispatcherQueue,
                (failedSignature, exception) => OnFrameEffectPreviewFailed(effectHost, failedSignature, exception));
            currentSignature = signature;
            return true;
        }
        catch (Exception exception)
        {
            ClearClipEffectPreview(effectHost);
            currentSignature = "fallback|" + signature;
            RestoreNativePreviewAfterFrameFailure(player);
            Debug.WriteLine($"Video effect preview fallback: {exception}");
            WarnTimelineEffectPreviewUnavailable(clip);
            return false;
        }
    }

    private void OnFrameEffectPreviewFailed(Grid host, string signature, Exception exception)
    {
        ClearClipEffectPreview(host);
        RestoreNativePreviewAfterFrameFailure(ReferenceEquals(host, PrimaryEffectPreviewHost) ? NativePreviewPlayer.MediaPlayer
            : ReferenceEquals(host, TransitionEffectPreviewHost) ? TransitionPreviewPlayer.MediaPlayer : OverlayPreviewPlayer.MediaPlayer);
        if (ReferenceEquals(host, PrimaryEffectPreviewHost)) _primaryEffectPreviewSignature = "fallback|" + signature;
        else if (ReferenceEquals(host, TransitionEffectPreviewHost)) _transitionEffectPreviewSignature = "fallback|" + signature;
        else _overlayEffectPreviewSignature = "fallback|" + signature;
        Debug.WriteLine($"Video effect preview fallback: {exception}");
        WarnTimelineEffectPreviewUnavailable(ReferenceEquals(host, PrimaryEffectPreviewHost) ? GetActivePreviewClip()
            : ReferenceEquals(host, TransitionEffectPreviewHost) ? GetActiveTransitionPreviewClip() : GetActiveOverlayPreviewClip());
#if DEBUG
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "Klipp-preview-effects.log"), $"{DateTimeOffset.Now:O} frame preview: {exception}\n"); } catch { }
#endif
        UpdateQuickEffectPreviewStatus();
    }
    private static bool HasGpuPreviewEffects(EditorProjectClip clip) => clip.Effects.Any(node => node.Enabled && node.Kind switch
    {
        "look" => !string.Equals(clip.Filter, "none", StringComparison.OrdinalIgnoreCase),
        "color" => Math.Abs(clip.Brightness) > 0.001 || Math.Abs(clip.Contrast - 1) > 0.001 ||
                   Math.Abs(clip.Saturation - 1) > 0.001 || Math.Abs(clip.Gamma - 1) > 0.001,
        "shadows" => Math.Abs(clip.Shadows) > 0.001 || Math.Abs(clip.Highlights) > 0.001,
        "curve" => clip.ToneCurve.Any(point => Math.Abs(point.Y - point.X) > 0.0005),
        "temperature" => Math.Abs(clip.Temperature) > 0.001,
        "hue" => Math.Abs(clip.HueDegrees) > 0.001,
        "sharpen" => clip.Sharpen > 0.001,
        "blur" => clip.Blur > 0.001,
        "vignette" => clip.Vignette > 0.001,
        "lut" => clip.LutStrength > 0.001 && !string.IsNullOrWhiteSpace(clip.LutPath),
        _ => false
    });

    private static string BuildClipEffectSignature(EditorProjectClip clip, double strength)
    {
        static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        var curve = string.Join(';', clip.ToneCurve.Select(point => $"{F(point.X)}/{F(point.Y)}"));
        return string.Join('|',
            clip.Filter,
            F(strength),
            F(clip.Brightness),
            F(clip.Contrast),
            F(clip.Saturation),
            F(clip.Shadows),
            F(clip.Highlights),
            F(clip.Temperature),
            F(clip.Gamma),
            F(clip.HueDegrees),
            F(clip.Sharpen),
            F(clip.Blur),
            F(clip.Vignette),
            curve, clip.LutPath, F(clip.LutStrength),
            string.Join(';', clip.Effects.Select(node => $"{node.Kind}:{node.Enabled}")));
    }

    private static Color GetHuePreviewColor(double degrees)
    {
        var hue = (degrees % 360 + 360) % 360;
        var sector = hue / 60.0;
        var x = 1 - Math.Abs(sector % 2 - 1);
        (double red, double green, double blue) = sector switch
        {
            < 1 => (1.0, x, 0.0),
            < 2 => (x, 1.0, 0.0),
            < 3 => (0.0, 1.0, x),
            < 4 => (0.0, x, 1.0),
            < 5 => (x, 0.0, 1.0),
            _ => (1.0, 0.0, x)
        };
        return Color.FromArgb(
            255,
            (byte)Math.Round(red * 255),
            (byte)Math.Round(green * 255),
            (byte)Math.Round(blue * 255));
    }

    private void UpdateElementPreview()
    {
        var isActive = LayerTimeline.IsTimeInLayer(EditorTimelineLayer.Element, CurrentTimelinePosition);
        ElementFocusFrame.Visibility = isActive && _elementOverlay == "focus" ? Visibility.Visible : Visibility.Collapsed;
        ElementLetterboxOverlay.Visibility = isActive && _elementOverlay == "letterbox" ? Visibility.Visible : Visibility.Collapsed;
        ElementCenterMarker.Visibility = isActive && _elementOverlay == "center" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateFadePreview()
    {
        if (FadePreviewOverlay is null || LayerTimeline is null)
            return;

        var trimDuration = Math.Max(0.001, (LayerTimeline.End - LayerTimeline.Start).TotalSeconds);
        var positionInTrim = Math.Clamp((CurrentTimelinePosition - LayerTimeline.Start).TotalSeconds, 0, trimDuration);
        var opacity = 0.0;

        if (_fadeInSeconds > 0.001 && positionInTrim < _fadeInSeconds)
            opacity = Math.Max(opacity, 1.0 - positionInTrim / _fadeInSeconds);

        if (_fadeOutSeconds > 0.001)
        {
            var remaining = trimDuration - positionInTrim;
            if (remaining < _fadeOutSeconds)
                opacity = Math.Max(opacity, 1.0 - remaining / _fadeOutSeconds);
        }

        FadePreviewOverlay.Opacity = Math.Clamp(opacity, 0, 1);
        FadePreviewOverlay.Visibility = opacity > 0.001 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyTextOverlayPlacement()
    {
        if (PreviewSurface is null || TextOverlayBackground is null)
            return;

        var surfaceWidth = PreviewSurface.ActualWidth;
        var surfaceHeight = PreviewSurface.ActualHeight;
        if (surfaceWidth <= 0 || surfaceHeight <= 0)
            return;

        var overlayWidth = TextOverlayBackground.ActualWidth;
        var overlayHeight = TextOverlayBackground.ActualHeight;
        if (overlayWidth <= 0 || overlayHeight <= 0)
            return;

        var state = GetCurrentTextState();
        var maxLeft = Math.Max(0, surfaceWidth - overlayWidth);
        var maxTop = Math.Max(0, surfaceHeight - overlayHeight);
        var left = Math.Clamp(surfaceWidth * state.AnchorX - overlayWidth / 2.0, 0, maxLeft);
        var top = Math.Clamp(surfaceHeight * state.AnchorY - overlayHeight / 2.0, 0, maxTop);

        TextOverlayBackground.Margin = new Thickness(left, top, 0, 0);
    }

    private void UpdateTextAnchorFromLayout(double left, double top)
    {
        var surfaceWidth = Math.Max(1, PreviewSurface.ActualWidth);
        var surfaceHeight = Math.Max(1, PreviewSurface.ActualHeight);
        var overlayWidth = Math.Max(1, TextOverlayBackground.ActualWidth);
        var overlayHeight = Math.Max(1, TextOverlayBackground.ActualHeight);

        var centerX = SnapToCanvasGrid(left + overlayWidth / 2.0);
        var centerY = SnapToCanvasGrid(top + overlayHeight / 2.0);
        ApplyTextStateChange(GetCurrentTextState() with
        {
            AnchorX = Math.Clamp(centerX / surfaceWidth, 0, 1),
            AnchorY = Math.Clamp(centerY / surfaceHeight, 0, 1)
        });
    }

    private void ApplySubtitleOverlayPlacement()
    {
        if (PreviewSurface is null || SubtitleOverlayBackground is null)
            return;

        var surfaceWidth = PreviewSurface.ActualWidth;
        var surfaceHeight = PreviewSurface.ActualHeight;
        if (surfaceWidth <= 0 || surfaceHeight <= 0)
            return;

        var overlayWidth = SubtitleOverlayBackground.ActualWidth;
        var overlayHeight = SubtitleOverlayBackground.ActualHeight;
        if (overlayWidth <= 0 || overlayHeight <= 0)
            return;

        var maxLeft = Math.Max(0, surfaceWidth - overlayWidth);
        var maxTop = Math.Max(0, surfaceHeight - overlayHeight);
        var left = Math.Clamp(surfaceWidth * _subtitleAnchorX - overlayWidth / 2.0, 0, maxLeft);
        var top = Math.Clamp(surfaceHeight * _subtitleAnchorY - overlayHeight / 2.0, 0, maxTop);

        SubtitleOverlayBackground.Margin = new Thickness(left, top, 0, 0);
    }

    private void UpdateSubtitleAnchorFromLayout(double left, double top)
    {
        var surfaceWidth = Math.Max(1, PreviewSurface.ActualWidth);
        var surfaceHeight = Math.Max(1, PreviewSurface.ActualHeight);
        var overlayWidth = Math.Max(1, SubtitleOverlayBackground.ActualWidth);
        var overlayHeight = Math.Max(1, SubtitleOverlayBackground.ActualHeight);

        var centerX = SnapToCanvasGrid(left + overlayWidth / 2.0);
        var centerY = SnapToCanvasGrid(top + overlayHeight / 2.0);
        _subtitleAnchorX = Math.Clamp(centerX / surfaceWidth, 0, 1);
        _subtitleAnchorY = Math.Clamp(centerY / surfaceHeight, 0, 1);
    }

    private Point GetResizeCenter(ResizeTarget target)
    {
        var surfaceWidth = Math.Max(1, PreviewSurface.ActualWidth);
        var surfaceHeight = Math.Max(1, PreviewSurface.ActualHeight);

        return target switch
        {
            ResizeTarget.Video => new Point(
                surfaceWidth / 2.0 + _videoOffsetX * surfaceWidth,
                surfaceHeight / 2.0 + _videoOffsetY * surfaceHeight),
            ResizeTarget.Text => new Point(
                TextOverlayBackground.Margin.Left + TextOverlayBackground.ActualWidth / 2.0,
                TextOverlayBackground.Margin.Top + TextOverlayBackground.ActualHeight / 2.0),
            ResizeTarget.Subtitle => new Point(
                SubtitleOverlayBackground.Margin.Left + SubtitleOverlayBackground.ActualWidth / 2.0,
                SubtitleOverlayBackground.Margin.Top + SubtitleOverlayBackground.ActualHeight / 2.0),
            _ => new Point(surfaceWidth / 2.0, surfaceHeight / 2.0)
        };
    }

    private static double Distance(Point first, Point second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double SnapToCanvasGrid(double value)
    {
        return Math.Round(value / CanvasSnapPixels) * CanvasSnapPixels;
    }

    private void UpdatePreviewStageZoom(bool syncControls = true)
    {
        if (PreviewWorkspace is null || PreviewSurface is null || PreviewStageTransform is null)
            return;

        var availableWidth = Math.Max(1, PreviewWorkspace.ActualWidth - 48);
        var availableHeight = Math.Max(1, PreviewWorkspace.ActualHeight - 48);
        var fitScale = Math.Min(availableWidth / PreviewSurface.Width, availableHeight / PreviewSurface.Height);
        fitScale = Math.Clamp(fitScale, 0.05, 2.0);
        var scale = fitScale * _previewZoom;

        // The source-sized canvas is a Canvas child so its unscaled dimensions
        // never become the editor's minimum layout size. Center it explicitly;
        // the render transform then scales around its existing center origin.
        Canvas.SetLeft(PreviewSurface, (PreviewWorkspace.ActualWidth - PreviewSurface.Width) / 2);
        Canvas.SetTop(PreviewSurface, (PreviewWorkspace.ActualHeight - PreviewSurface.Height) / 2);
        PreviewStageTransform.ScaleX = scale;
        PreviewStageTransform.ScaleY = scale;
        UpdateVideoTransformFrame();
        UpdateTimelineFxPreviewAdorner();

        if (!syncControls || PreviewZoomSlider is null)
            return;

        _syncingPreviewZoomControls = true;
        try
        {
            PreviewZoomSlider.Value = _previewZoom * 100.0;
            var label = $"{Math.Round(_previewZoom * 100):0}%";
            PreviewZoomValueText.Text = label;
            if (PreviewZoomOverlayText is not null)
                PreviewZoomOverlayText.Text = label;
        }
        finally
        {
            _syncingPreviewZoomControls = false;
        }
    }

    private void ResetVideoTransform()
    {
        _videoScale = 1.0;
        _videoOffsetX = 0;
        _videoOffsetY = 0;
        UpdateVideoTransformPreview();
        QueueProjectChange();
    }

    private void UpdateVideoTransformPreview(bool syncControls = true)
    {
        if (PreviewMediaTransform is not null && PreviewSurface is not null)
        {
            PreviewMediaTransform.ScaleX = _videoScale;
            PreviewMediaTransform.ScaleY = _videoScale;
            PreviewMediaTransform.TranslateX = _videoOffsetX * PreviewSurface.ActualWidth;
            PreviewMediaTransform.TranslateY = _videoOffsetY * PreviewSurface.ActualHeight;
        }

        UpdateSelectedClipTransformPreview();

        if (!syncControls || VideoScaleSlider is null)
            return;

        _syncingVideoTransformControls = true;
        try
        {
            VideoScaleSlider.Value = _videoScale * 100.0;
            VideoXSlider.Value = _videoOffsetX * 100.0;
            VideoYSlider.Value = _videoOffsetY * 100.0;
            VideoScaleValueText.Text = $"{Math.Round(_videoScale * 100):0}%";
            VideoXValueText.Text = $"{Math.Round(_videoOffsetX * 100):0}";
            VideoYValueText.Text = $"{Math.Round(_videoOffsetY * 100):0}";
        }
        finally
        {
            _syncingVideoTransformControls = false;
        }
    }

    private void UpdateSelectedClipTransformPreview()
    {
        if (PreviewSurface is null)
            return;

        ApplyClipPreviewTransform(
            PrimaryPreviewHost,
            PrimaryPreviewTransform,
            PrimaryPreviewClipGeometry,
            GetActivePreviewClip());
        ApplyClipPreviewTransform(
            TransitionPreviewHost,
            TransitionPreviewTransform,
            TransitionPreviewClipGeometry,
            GetActiveTransitionPreviewClip());
        ApplyClipPreviewTransform(
            OverlayPreviewHost,
            OverlayPreviewTransform,
            OverlayPreviewClipGeometry,
            GetActiveOverlayPreviewClip());
        UpdateAdditionalVideoTrackTransforms();
        UpdateVideoTransformFrame();
        UpdateTimelineFxPreviewAdorner();
    }

    private void ApplyClipPreviewTransform(
        Grid? host,
        CompositeTransform? transform,
        RectangleGeometry? clipGeometry,
        EditorProjectClip? clip)
    {
        if (host is null || transform is null || clipGeometry is null || PreviewSurface is null)
            return;

        var localTime = clip is null
            ? 0
            : LayerTimeline.GetMediaClipLocalTime(clip.Id, CurrentTimelinePosition);
        var state = clip?.EvaluateTransform(localTime) ?? new EditorProjectClipTransformState(1, 0, 0);
        var scale = Math.Clamp(state.Scale, 0.1, 4);
        var offsetX = Math.Clamp(state.OffsetX, -1, 1);
        var offsetY = Math.Clamp(state.OffsetY, -1, 1);
        transform.ScaleX = scale;
        transform.ScaleY = scale;
        transform.TranslateX = (1 - scale) * offsetX * PreviewSurface.ActualWidth / 2.0;
        transform.TranslateY = (1 - scale) * offsetY * PreviewSurface.ActualHeight / 2.0;

        var width = Math.Max(1, PreviewSurface.ActualWidth > 0 ? PreviewSurface.ActualWidth : PreviewSurface.Width);
        var height = Math.Max(1, PreviewSurface.ActualHeight > 0 ? PreviewSurface.ActualHeight : PreviewSurface.Height);
        ApplySourceCropRefit(host, clip, clipGeometry, width, height);
        ApplyClipMaskPreview(host, clip, width, height);
    }

    private static void ApplyClipMaskPreview(
        Grid host,
        EditorProjectClip? clip,
        double width,
        double height)
    {
        if (ApplySourcePolygonOrInvertedMask(host, clip, width, height))
            return;
        var visual = ElementCompositionPreview.GetElementVisual(host);
        if (clip is null || string.Equals(clip.MaskShape, "none", StringComparison.Ordinal))
        {
            visual.Clip = null;
            return;
        }

        var compositor = visual.Compositor;
        if (string.Equals(clip.MaskShape, "ellipse", StringComparison.Ordinal))
        {
            var geometry = compositor.CreateEllipseGeometry();
            geometry.Center = new Vector2((float)(width / 2), (float)(height / 2));
            geometry.Radius = new Vector2((float)(width / 2), (float)(height / 2));
            visual.Clip = compositor.CreateGeometricClip(geometry);
            return;
        }

        var radius = (float)(Math.Min(width, height) * Math.Clamp(clip.MaskRoundness, 0, 0.5));
        var rounded = compositor.CreateRoundedRectangleGeometry();
        rounded.Size = new Vector2((float)width, (float)height);
        rounded.CornerRadius = new Vector2(radius, radius);
        visual.Clip = compositor.CreateGeometricClip(rounded);
    }

    private void UpdateAllOptionButtons()
    {
        UpdateToolButtons();
        UpdateCanvasPresetButtons();
        RefreshPreviewProxyControls();
        UpdateSpeedButtons();
        UpdateTextButtons();
        UpdateSubtitleButtons();
        UpdateFilterButtons();
        UpdateElementButtons();
        UpdateTransitionButtons();
    }

    private void UpdateToolButtons()
    {
        SetToolButtonState(LibraryToolButton, _activeTool == "library");
        SetToolButtonState(GeneralToolButton, _activeTool == "general");
        SetToolButtonState(TextToolButton, _activeTool == "text");
        SetToolButtonState(ElementsToolButton, _activeTool == "elements");
        SetToolButtonState(VfxToolButton, _activeTool == "vfx");
        SetToolButtonState(TransitionsToolButton, _activeTool == "transitions");
        SetToolButtonState(AudioToolButton, _activeTool == "audio");
        SetToolButtonState(SubtitlesToolButton, _activeTool == "subtitles");
    }

    private void UpdateCanvasPresetButtons()
    {
        var width = _project?.CanvasWidth ?? 1280;
        var height = _project?.CanvasHeight ?? 720;
        CanvasPresetValueText.Text = $"{width} x {height}";
        SetOptionButtonState(CanvasWideButton, width == 1280 && height == 720);
        SetOptionButtonState(CanvasVerticalButton, width == 720 && height == 1280);
        SetOptionButtonState(CanvasSquareButton, width == 1080 && height == 1080);
        SetOptionButtonState(CanvasPortraitButton, width == 1080 && height == 1350);
        SetOptionButtonState(ExportDraftButton, _exportQuality == "draft");
        SetOptionButtonState(ExportHighButton, _exportQuality == "high");
        SetOptionButtonState(ExportMaxButton, _exportQuality == "max");
        SetOptionButtonState(Export30FpsButton, _exportFrameRate == 30);
        SetOptionButtonState(Export60FpsButton, _exportFrameRate == 60);
        UpdateExportControls();
    }

    private void UpdateSafeAreaGuides()
    {
        if (SafeAreaGuidesOverlay is null || PreviewSurface is null)
            return;

        SafeAreaGuidesOverlay.Visibility = _showSafeAreaGuides
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!_showSafeAreaGuides)
            return;

        var width = Math.Max(1, PreviewSurface.ActualWidth > 0 ? PreviewSurface.ActualWidth : PreviewSurface.Width);
        var height = Math.Max(1, PreviewSurface.ActualHeight > 0 ? PreviewSurface.ActualHeight : PreviewSurface.Height);
        ActionSafeAreaBorder.Width = width * 0.90;
        ActionSafeAreaBorder.Height = height * 0.90;
        TitleSafeAreaBorder.Width = width * 0.80;
        TitleSafeAreaBorder.Height = height * 0.80;
    }

    private void UpdateSpeedButtons()
    {
        SetOptionButtonState(Speed05Button, Math.Abs(_playbackSpeed - 0.5) < 0.001);
        SetOptionButtonState(Speed1Button, Math.Abs(_playbackSpeed - 1.0) < 0.001);
        SetOptionButtonState(Speed15Button, Math.Abs(_playbackSpeed - 1.5) < 0.001);
        SetOptionButtonState(Speed2Button, Math.Abs(_playbackSpeed - 2.0) < 0.001);
    }

    private void UpdateTextButtons()
    {
        SetOptionButtonState(TextPlainButton, _textStyle == "plain");
        SetOptionButtonState(TextBoldButton, _textStyle == "bold");
        SetOptionButtonState(TextMemeButton, _textStyle == "meme");
        SetOptionButtonState(TextHighlightButton, _textStyle == "highlight");
        SetOptionButtonState(TextPixelButton, _textStyle == "pixel");
        SetOptionButtonState(TextOutlineButton, _textStyle == "outline");
        SetOptionButtonState(TextNeonButton, _textStyle == "neon");
        SetOptionButtonState(TextShadowButton, _textStyle == "shadow");
        SetOptionButtonState(TextTypewriterButton, _textStyle == "typewriter");
        SetOptionButtonState(TextImpactButton, _textStyle == "impact");
        SetOptionButtonState(TextStickerButton, _textStyle == "sticker");
        SetOptionButtonState(TextCaptionButton, _textStyle == "caption");
        SetOptionButtonState(TextAnimationNoneButton, _textAnimation == "none");
        SetOptionButtonState(TextAnimationFadeButton, _textAnimation == "fade");
        SetOptionButtonState(TextAnimationPopButton, _textAnimation == "pop");
        SetOptionButtonState(TextAnimationSlideUpButton, _textAnimation == "slide-up");
        SetOptionButtonState(TextAnimationSlideLeftButton, _textAnimation == "slide-left");
        SetOptionButtonState(TextAnimationTypeButton, _textAnimation == "type");
        SetOptionButtonState(TextPositionTopButton, _textPosition == "top");
        SetOptionButtonState(TextPositionCenterButton, _textPosition == "center");
        SetOptionButtonState(TextPositionBottomButton, _textPosition == "bottom");
        UpdateTextTypographyControls();
    }

    private void UpdateSubtitleButtons()
    {
        SetOptionButtonState(SubtitlePlainButton, _subtitleStyle == "subtitle");
        SetOptionButtonState(SubtitleBoldButton, _subtitleStyle == "bold");
        SetOptionButtonState(SubtitleHighlightButton, _subtitleStyle == "highlight");
        SetOptionButtonState(SubtitlePixelButton, _subtitleStyle == "pixel");
        SetOptionButtonState(SubtitlePositionTopButton, _subtitlePosition == "top");
        SetOptionButtonState(SubtitlePositionCenterButton, _subtitlePosition == "center");
        SetOptionButtonState(SubtitlePositionBottomButton, _subtitlePosition == "bottom");
        UpdateSubtitleTypographyControls();
    }

    private void UpdateTextTypographyControls()
    {
        if (TextFontComboBox is null || TextColorHexBox is null || TextColorPreview is null)
            return;
        UpdateTypographyControls(
            TextFontComboBox,
            TextColorHexBox,
            TextColorPreview,
            _textFontFamily,
            _textColor,
            _textStyle);
        TextCustomFontStatusText.Text = FormatCustomFontStatus(
            _textFontFilePath,
            _textFontFamilyName);
        UpdateTypographySample(TextTypographySample, _textStyle, _textFontFamily, _textColor,
            _textFontFilePath, _textFontFamilyName);
    }

    private void UpdateSubtitleTypographyControls()
    {
        if (SubtitleFontComboBox is null || SubtitleColorHexBox is null || SubtitleColorPreview is null)
            return;
        UpdateTypographyControls(
            SubtitleFontComboBox,
            SubtitleColorHexBox,
            SubtitleColorPreview,
            _subtitleFontFamily,
            _subtitleColor,
            _subtitleStyle);
        SubtitleCustomFontStatusText.Text = FormatCustomFontStatus(
            _subtitleFontFilePath,
            _subtitleFontFamilyName);
        UpdateTypographySample(SubtitleTypographySample, _subtitleStyle, _subtitleFontFamily, _subtitleColor,
            _subtitleFontFilePath, _subtitleFontFamilyName);
    }

    private static string FormatCustomFontStatus(string? fontFilePath, string? familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName))
            return "System font";

        return File.Exists(EditorTextTypography.NormalizeFontFilePath(fontFilePath))
            ? $"Imported: {familyName}"
            : $"Missing: {familyName} (system fallback)";
    }

    private void UpdateTypographyControls(
        ComboBox fontComboBox,
        TextBox colorHexBox,
        Border colorPreview,
        string fontFamily,
        string color,
        string style)
    {
        _syncingTypographyControls = true;
        try
        {
            UpdateFontPickerSamples(fontComboBox, style);
            var normalizedFont = EditorTextTypography.NormalizeFontFamily(fontFamily);
            fontComboBox.SelectedItem = fontComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(),
                    normalizedFont,
                    StringComparison.OrdinalIgnoreCase));
            var normalizedColor = EditorTextTypography.NormalizeColor(color);
            colorHexBox.Text = string.Equals(normalizedColor, EditorTextTypography.Automatic, StringComparison.Ordinal)
                ? string.Empty
                : normalizedColor;
            colorPreview.Background = new SolidColorBrush(ParseEditorTextColor(
                EditorTextTypography.ResolveColor(normalizedColor, style)));
        }
        finally
        {
            _syncingTypographyControls = false;
        }
    }

    private static Color ParseEditorTextColor(string color)
    {
        var normalized = EditorTextTypography.ResolveColor(color, "plain");
        return Color.FromArgb(
            255,
            Convert.ToByte(normalized.Substring(1, 2), 16),
            Convert.ToByte(normalized.Substring(3, 2), 16),
            Convert.ToByte(normalized.Substring(5, 2), 16));
    }

    private void UpdateFilterButtons()
    {
        SetOptionButtonState(FilterNoneButton, _videoFilter == "none");
        SetOptionButtonState(FilterBoostButton, _videoFilter == "boost");
        SetOptionButtonState(FilterWarmButton, _videoFilter == "warm");
        SetOptionButtonState(FilterCoolButton, _videoFilter == "cool");
        SetOptionButtonState(FilterMonoButton, _videoFilter == "mono");
        SetOptionButtonState(FilterCinematicButton, _videoFilter == "cinematic");
        SetOptionButtonState(FilterSharpenButton, _videoFilter == "sharpen");
        SetOptionButtonState(FilterBlurButton, _videoFilter == "blur");
        SetOptionButtonState(FilterVintageButton, _videoFilter == "vintage");
        SetOptionButtonState(FilterVibrantButton, _videoFilter == "vibrant");
        SetOptionButtonState(FilterNoirButton, _videoFilter == "noir");
        SetOptionButtonState(FilterDreamButton, _videoFilter == "dream");
        SetOptionButtonState(FilterGlitchButton, _videoFilter == "glitch");
        SetOptionButtonState(FilterVignetteButton, _videoFilter == "vignette");
    }

    private void UpdateElementButtons()
    {
        SetOptionButtonState(ElementNoneButton, _elementOverlay == "none");
        SetOptionButtonState(ElementFocusButton, _elementOverlay == "focus");
        SetOptionButtonState(ElementLetterboxButton, _elementOverlay == "letterbox");
        SetOptionButtonState(ElementCenterButton, _elementOverlay == "center");
    }

    private void UpdateTransitionButtons()
    {
        UpdatePresetPickers();
        SyncTransitionAuditionControls();
        var available = LayerTimeline.TryGetSelectedMediaClipTransition(
            out var style,
            out var duration,
            out var inherited);
        var selected = LayerTimeline.GetSelectedMediaClip();
        var actualDuration = TryGetSelectedTransitionContext(out _, out _, out var overlap) ? overlap : 0;
        SelectedTransitionClipText.Text = available && selected is not null
            ? style == "cut" ? $"Cut into {selected.Name}" : $"Transition into {selected.Name} · {actualDuration:0.##}s"
            : GetTransitionSelectionPrompt();

        TransitionInheritButton.IsEnabled = available;
        TransitionCutButton.IsEnabled = available;
        TransitionDissolveButton.IsEnabled = available;
        TransitionBlackButton.IsEnabled = available;
        TransitionWipeButton.IsEnabled = available;
        var durationEnabled = available && !string.Equals(style, "cut", StringComparison.OrdinalIgnoreCase);
        TransitionDuration025Button.IsEnabled = durationEnabled;
        TransitionDuration05Button.IsEnabled = durationEnabled;
        TransitionDuration1Button.IsEnabled = durationEnabled;

        SetOptionButtonState(TransitionInheritButton, available && inherited);
        SetOptionButtonState(TransitionCutButton, available && !inherited && style == "cut");
        SetOptionButtonState(TransitionDissolveButton, available && !inherited && style == "dissolve");
        SetOptionButtonState(TransitionBlackButton, available && !inherited && style == "fadeblack");
        SetOptionButtonState(TransitionWipeButton, available && !inherited && style == "wipeleft");
        SetOptionButtonState(TransitionDuration025Button, durationEnabled && Math.Abs(actualDuration - 0.25) < 0.001);
        SetOptionButtonState(TransitionDuration05Button, durationEnabled && Math.Abs(actualDuration - 0.5) < 0.001);
        SetOptionButtonState(TransitionDuration1Button, durationEnabled && Math.Abs(actualDuration - 1) < 0.001);
        SetOptionButtonState(FadeInNoneButton, Math.Abs(_fadeInSeconds) < 0.001);
        SetOptionButtonState(FadeIn05Button, Math.Abs(_fadeInSeconds - 0.5) < 0.001);
        SetOptionButtonState(FadeIn1Button, Math.Abs(_fadeInSeconds - 1.0) < 0.001);
        SetOptionButtonState(FadeOutNoneButton, Math.Abs(_fadeOutSeconds) < 0.001);
        SetOptionButtonState(FadeOut05Button, Math.Abs(_fadeOutSeconds - 0.5) < 0.001);
        SetOptionButtonState(FadeOut1Button, Math.Abs(_fadeOutSeconds - 1.0) < 0.001);
        SetOptionButtonState(Freeze0Button, Math.Abs(_freezeFrameSeconds) < 0.001);
        SetOptionButtonState(Freeze1Button, Math.Abs(_freezeFrameSeconds - 1.0) < 0.001);
        SetOptionButtonState(Freeze2Button, Math.Abs(_freezeFrameSeconds - 2.0) < 0.001);
        SetOptionButtonState(Freeze3Button, Math.Abs(_freezeFrameSeconds - 3.0) < 0.001);
    }

    private static void SetToolButtonState(Button? button, bool selected)
    {
        if (button is null)
            return;

        button.Background = ResourceBrush(selected ? "KlippSurfaceElevatedBrush" : "KlippSurfaceBrush");
        button.BorderBrush = selected ? ResourceBrush("KlippAccentBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.Foreground = ResourceBrush(selected ? "KlippTextPrimaryBrush" : "KlippTextSecondaryBrush");
        button.BorderThickness = new Thickness(1);
    }

    private static void SetOptionButtonState(Button? button, bool selected)
    {
        if (button is null)
            return;

        button.Background = ResourceBrush(selected ? "KlippAccentSoftBrush" : "KlippSurfaceElevatedBrush");
        button.BorderBrush = ResourceBrush(selected ? "KlippAccentBrush" : "KlippBorderBrush");
        button.Foreground = selected ? new SolidColorBrush(Microsoft.UI.Colors.White) : ResourceBrush("KlippTextSecondaryBrush");
        button.BorderThickness = new Thickness(1);
    }

    private IEnumerable<Button> GetClipColorButtons()
    {
        yield return ClipColorNoneButton;
        yield return ClipColorRedButton;
        yield return ClipColorOrangeButton;
        yield return ClipColorYellowButton;
        yield return ClipColorGreenButton;
        yield return ClipColorTealButton;
        yield return ClipColorBlueButton;
        yield return ClipColorPurpleButton;
    }

    private static void UpdateColorSwatchButtons(string selectedColor, params Button[] buttons)
    {
        selectedColor = EditorProjectColorLabels.Normalize(selectedColor);
        foreach (var button in buttons)
        {
            var selected = string.Equals(button.Tag as string, selectedColor, StringComparison.Ordinal);
            button.BorderBrush = selected
                ? new SolidColorBrush(Microsoft.UI.Colors.White)
                : ResourceBrush("KlippBorderSubtleBrush");
            button.BorderThickness = new Thickness(selected ? 3 : 1);
            button.Opacity = selected ? 1 : 0.72;
        }
    }

    private static Color GetColorLabelColor(string? colorLabel)
    {
        return EditorProjectColorLabels.Normalize(colorLabel) switch
        {
            "red" => Color.FromArgb(255, 248, 113, 113),
            "orange" => Color.FromArgb(255, 251, 146, 60),
            "yellow" => Color.FromArgb(255, 250, 204, 21),
            "green" => Color.FromArgb(255, 74, 222, 128),
            "teal" => Color.FromArgb(255, 45, 212, 191),
            "blue" => Color.FromArgb(255, 96, 165, 250),
            "purple" => Color.FromArgb(255, 192, 132, 252),
            _ => Color.FromArgb(255, 113, 113, 122)
        };
    }

    private static Brush ResourceBrush(string key)
    {
        return (Brush)Application.Current.Resources[key];
    }

    private static bool IsWithin(DependencyObject? source, DependencyObject? target)
    {
        if (source is null || target is null)
            return false;

        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, target))
                return true;

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void UpdatePlaybackUi()
    {
        var position = _previewPosition;
        if (position < TimeSpan.Zero)
            position = TimeSpan.Zero;

        _previewPosition = position;
        if (LayerTimeline.Playhead != position)
            LayerTimeline.Playhead = position;

        var isPlaying = _isPreviewPlaying || _reviewShuttleTimer?.IsEnabled == true;
        var playIcon = isPlaying ? "\uE769" : "\uE768";
        if (TimelinePlayIcon.Text != playIcon)
            TimelinePlayIcon.Text = playIcon;
        var totalFrames = (long)Math.Round(position.TotalSeconds * _exportFrameRate, MidpointRounding.AwayFromZero);
        var frame = totalFrames % _exportFrameRate;
        var timecodeSeconds = TimeSpan.FromSeconds(totalFrames / _exportFrameRate);
        var timecode = $"{FormatTimestamp(timecodeSeconds)}:{frame:00} / {FormatTimestamp(_duration)}";
        if (_reviewShuttleTimer?.IsEnabled == true)
            timecode += $" · {Klipp.Core.Playback.PlaybackReviewMath.FormatRate(_reviewShuttleRate)}";
        if (PositionText.Text != timecode) PositionText.Text = timecode;
        UpdateSelectedClipTransformPreview();
        UpdateNativePreviewAudio();
        var now = Stopwatch.GetTimestamp();
        if (!_isPreviewPlaying || (now - _lastMeterUpdateTicks) / (double)Stopwatch.Frequency >= 0.1)
        {
            UpdateAudioMeters();
            _lastMeterUpdateTicks = now;
        }
        if (!_isPreviewPlaying && !LayerTimeline.IsDragging && _activeTool == "general")
            SyncSelectedClipVisualControls();
        if (!_isPreviewPlaying && !LayerTimeline.IsDragging && _activeTool == "audio")
            SyncSelectedClipAudioControls();
        if (!_isPreviewPlaying && !LayerTimeline.IsDragging && _activeTool == "text")
            UpdateTextKeyframeControls();
        if (_activeTool == "vfx")
        {
            UpdateLookPreviewContext();
            SyncTimelineFxMotionControls();
            if (!_isPreviewPlaying) DrawTimelineFxArea(LayerTimeline.GetSelectedEffectLayer());
        }
        if (!_isPreviewPlaying && !LayerTimeline.IsDragging)
            UpdateTimelineChrome();
        UpdatePreviewOverlays();
        if (!_isPreviewPlaying && !_isDraggingGraphicOverlay && !_isResizingGraphicOverlay && GetSelectedGraphicLayer() is not null)
            SyncSelectedGraphicControls();
    }

    private void UpdateTrimUi()
    {
        StartText.Text = FormatTimestamp(LayerTimeline.Start);
        EndText.Text = FormatTimestamp(LayerTimeline.End);
        SelectionText.Text = FormatTimestamp(LayerTimeline.End - LayerTimeline.Start);
        UpdateExportSizeEstimate();
        UpdateTimelineChrome();
    }

    private void UpdateTimelineChrome()
    {
        SyncTimelineSnappingControl();
        if (TimelineZoomText is not null)
            TimelineZoomText.Text = LayerTimeline.ZoomLabel;
        if (TimelineStatusText is not null)
        {
            var selection = LayerTimeline.SelectedClipCount > 1
                ? $"{LayerTimeline.SelectedClipCount} clips selected" : LayerTimeline.ActiveLayerLabel;
            TimelineStatusText.Text = $"{selection} • {LayerTimeline.SnapGridLabel}";
        }
        UpdateTimelineHistoryButtons();
        UpdateLayerInspectorUi();
    }

    private void UpdateTimelineHistoryButtons()
    {
        SetTimelineActionButtonState(UndoTimelineButton, _projectUndoHistory.Count > 0 || _projectChangePending);
        SetTimelineActionButtonState(RedoTimelineButton, _projectRedoHistory.Count > 0);
    }

    private static void SetTimelineActionButtonState(Button? button, bool enabled)
    {
        if (button is null)
            return;

        button.IsEnabled = enabled;
        button.Opacity = enabled ? 1 : 0.45;
    }

    private void AdjustSelectedLayer(Func<bool> editAction)
    {
        PausePreview();
        if (!editAction())
            return;

        UpdateTrimUi();
        UpdateLayerInspectorUi();
        UpdatePreviewOverlays();
        QueueProjectChange();
    }

    private void UpdateLayerInspectorUi()
    {
        var hasRange = LayerTimeline.TryGetSelectedRange(out _, out var start, out var end);
        var duration = end - start;

        if (LayerInspectorTitle is not null)
            LayerInspectorTitle.Text = hasRange ? LayerTimeline.ActiveLayerLabel : "Timeline";
        if (LayerInspectorRangeText is not null)
            LayerInspectorRangeText.Text = hasRange ? EditorTimecodeMath.Format(duration.TotalSeconds, _exportFrameRate) : "--";
        if (LayerStartValueText is not null)
            LayerStartValueText.Text = hasRange ? EditorTimecodeMath.Format(start.TotalSeconds, _exportFrameRate) : "--";
        if (LayerEndValueText is not null)
            LayerEndValueText.Text = hasRange ? EditorTimecodeMath.Format(end.TotalSeconds, _exportFrameRate) : "--";
        if (LayerDurationValueText is not null)
            LayerDurationValueText.Text = hasRange ? EditorTimecodeMath.Format(duration.TotalSeconds, _exportFrameRate) : "--";

        SetLayerInspectorButtonState(hasRange);
    }

    private void SetLayerInspectorButtonState(bool enabled)
    {
        Button?[] buttons =
        [
            LayerMoveBackButton,
            LayerMoveForwardButton,
            LayerStartBackButton,
            LayerStartForwardButton,
            LayerEndBackButton,
            LayerEndForwardButton
        ];

        foreach (var button in buttons)
        {
            if (button is null)
                continue;

            button.IsEnabled = enabled;
        }
    }

    private static string FormatTimestamp(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    private static string GetLayerLabel(string value, string fallback)
    {
        var text = value.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        return text.Length <= 18 ? text : $"{text[..16]}...";
    }

    private static string GetVfxLabel(string filter)
    {
        return filter switch
        {
            "boost" => "Boost",
            "warm" => "Warm",
            "cool" => "Cool",
            "mono" => "Mono",
            "cinematic" => "Cinematic",
            "sharpen" => "Sharpen",
            "blur" => "Soft blur",
            "vintage" => "Vintage",
            "vibrant" => "Vibrant",
            "noir" => "Noir",
            "dream" => "Dream",
            "natural" => "Natural",
            "matte" => "Matte",
            "goldenhour" => "Golden hour",
            "steel" => "Steel",
            "film" => "Film",
            "soft" => "Soft contrast",
            "tealorange" => "Teal & amber",
            "glitch" => "Glitch",
            "vignette" => "Vignette",
            _ => "VFX"
        };
    }

    private static string GetElementLabel(string element)
    {
        return element switch
        {
            "focus" => "Focus frame",
            "letterbox" => "Letterbox",
            "center" => "Center mark",
            _ => "Element"
        };
    }
}

public sealed class EditorLibraryItemEventArgs : EventArgs
{
    public EditorLibraryItemEventArgs(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }
}
