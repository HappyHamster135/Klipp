using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Collections.ObjectModel;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Storage.Pickers;
using Windows.UI.ViewManagement;
using Klipp.Core.Input;
using Klipp.Core.Models;
using Klipp.Desktop.Controls;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;

namespace Klipp.Desktop;

public sealed partial class MainWindow : Window
{
    /// <summary>Appens två inspelningslägen.</summary>
    private enum AppMode { Recording, Clip }

    private readonly RecordingService _recording = new();
    private readonly ClipModeService _clipMode = new();
    private readonly ClipEditService _clipEditor = new();
    private readonly EditorProjectExportService _projectExporter = new();
    private readonly ExportQueueService _exportQueue;
    private readonly MediaProbeService _mediaProbe = new();
    private readonly SettingsService _settings = new();
    private readonly CaptureRecommendationService _captureRecommendations = new();
    private readonly CaptureProfileService _captureProfiles = new();
    private readonly CaptureHealthService _captureHealth = new();
    private readonly CacheMaintenanceService _cacheMaintenance = new();
    private readonly RecycleBinService _recycleBin = new();
    private readonly OriginalClipRetentionService _originalClipRetention;
    private readonly AccessibilitySettings _accessibilitySettings = new();
    public ClipLibraryService LibraryService { get; } = new();
    public ObservableCollection<ClipViewModel> VisibleClips { get; } = new();
    public ObservableCollection<EditorShortcutViewModel> EditorShortcutItems { get; } = [];

    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _healthTimer;
    private readonly DispatcherTimer _cacheTimer;
    private AppMode _mode = AppMode.Clip;
    private bool _uiReady;
    private HotkeyManager? _hotkeyManager;
    private bool _capturingHotkey;
    private string? _capturingEditorShortcutAction;
    private AppWindow? _appWindow;
    private bool _playerFullscreen;
    private bool _isSwitchingMode;
    private bool _syncingSettingsUi;
    private bool _sideMenuOpen;
    private bool _suppressLibraryFilter;
    private bool _cacheOperationRunning;
    private bool _retentionOperationRunning;
    private bool? _titleBarHighContrast;
    private ClipPlayerView? _playerView;
    private SafeClipEditorView? _editorView;

    public MainWindow()
    {
        _exportQueue = new ExportQueueService(_projectExporter);
        _originalClipRetention = new OriginalClipRetentionService(
            new EditorProjectService(),
            _recycleBin);
        InitializeComponent();
        ApplyAccessibilityTheme();
        InitializeAppWindow();

        _exportQueue.JobsChanged += OnExportQueueJobsChanged;
        _exportQueue.JobCompleted += OnExportQueueJobCompleted;
        Closed += OnMainWindowClosed;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) =>
        {
            UpdateStatusUI();
            ApplyAccessibilityTheme();
        };
        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _healthTimer.Tick += async (_, _) =>
        {
            _captureProfiles.ObserveForegroundApplication();
            UpdateCaptureHealthUI();
            await CheckCaptureDiskProtectionAsync();
        };
        _healthTimer.Start();
        _cacheTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _cacheTimer.Tick += OnCacheTimerTick;

        LibraryService.Clips.CollectionChanged += (_, _) =>
        {
            if (!_suppressLibraryFilter)
                ApplyLibraryFilter();
        };
        this.Content.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler(OnWindowKeyDown), true);
        this.Content.PreviewKeyDown += OnInlineFullscreenPreviewKeyDown;
        ClipLengthSelector.Visibility = Visibility.Visible;
        UpdateModeChrome();
        UpdateStatusUI();

        _uiReady = true;
        SetupGlobalHotkey();
        InitializeTrayIcon();
        _ = InitializeAsync();
    }

    private ClipPlayerView PlayerView => _playerView ??= CreatePlayerView();

    private SafeClipEditorView EditorView => _editorView ??= CreateEditorView();

    private ClipPlayerView CreatePlayerView()
    {
        var view = new ClipPlayerView();
        view.BackRequested += OnPlayerBackRequested;
        view.DeleteRequested += OnPlayerDeleteRequested;
        view.RenameRequested += OnPlayerRenameRequested;
        view.TrimRequested += OnPlayerTrimRequested;
        view.PrevRequested += OnPlayerPrevRequested;
        view.NextRequested += OnPlayerNextRequested;
        view.FullscreenRequested += OnPlayerFullscreenRequested;
        PlayerHost.Children.Add(view);
        return view;
    }

    private SafeClipEditorView CreateEditorView()
    {
        var view = new SafeClipEditorView();
        view.BackRequested += OnEditorBackRequested;
        view.ExportRequested += OnEditorExportRequested;
        view.ProjectExportRequested += OnEditorProjectExportRequested;
        view.LibraryImportRequested += OnEditorLibraryImportRequested;
        view.LibraryFavoriteToggleRequested += OnEditorLibraryFavoriteToggleRequested;
        view.LibraryOrganizeRequested += OnEditorLibraryOrganizeRequested;
        view.AudioTrackImportRequested += OnEditorAudioTrackImportRequested;
        view.GraphicImportRequested += OnEditorGraphicImportRequested;
        view.BrandLogoImportRequested += OnEditorBrandLogoImportRequested;
        view.BrandFontImportRequested += OnEditorBrandFontImportRequested;
        view.LutImportRequested += OnEditorLutImportRequested;
        view.SourceMediaImportRequested += OnEditorSourceMediaImportRequested;
        view.ProjectFileOpenRequested += OnEditorProjectFileOpenRequested;
        view.ProjectMediaCollectRequested += OnEditorProjectMediaCollectRequested;
        view.SubtitleSrtImportRequested += OnEditorSubtitleSrtImportRequested;
        view.SubtitleSrtExportRequested += OnEditorSubtitleSrtExportRequested;
        view.MissingMediaRelinkRequested += OnEditorMissingMediaRelinkRequested;
        view.ExportJobActionRequested += OnEditorExportJobActionRequested;
        view.MediaImportHandler = async (path, progress, cancellationToken) =>
        {
            if (_updateRestarting) throw new OperationCanceledException("Klipp is restarting to update.");
            using var updateWork = BeginUpdateBlockingOperation();
            var result = await LibraryService.ImportClipWithDetailsAsync(path, progress, cancellationToken);
            ApplyLibraryFilter();
            view.SetLibraryClips(LibraryService.Clips);
            return result;
        };
        view.SetExportJobs(_exportQueue.Jobs);
        view.ConfigureShortcuts(_settings.Current.EditorShortcuts);
        EditorHost.Children.Add(view);
        return view;
    }

    private void StopPlayerIfCreated()
    {
        _playerView?.Stop();
    }

    private void StopEditorIfCreated()
    {
        _editorView?.Stop();
    }

    private void InitializeAppWindow()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            ApplyAppWindowIcon();
            ApplyAccessibilityTheme();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Klipp] AppWindow-init fel: {ex.Message}");
        }
    }

    private void ApplyAppWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Klipp.ico");
        if (_appWindow is null || !File.Exists(iconPath)) return;
        try
        {
            _appWindow.SetIcon(iconPath);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[Klipp] Window icon could not be applied: {exception.Message}");
        }
    }

    private static Task RunServiceOperationAsync(Func<Task> operation)
    {
        return Task.Run(async () => await operation().ConfigureAwait(false));
    }

    private static Task<T> RunServiceOperationAsync<T>(Func<Task<T>> operation)
    {
        return Task.Run(async () => await operation().ConfigureAwait(false));
    }

    /// <summary>
    /// Registrerar den globala hotkeyen (Ctrl+Shift+S) för att spara klipp
    /// även när Klipp inte har fokus (t.ex. mitt i ett fullskärmsspel).
    /// </summary>
    private void SetupGlobalHotkey()
    {
        try
        {
            // Hämta fönstrets HWND
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            _hotkeyManager = new HotkeyManager(hwnd, hotkeyId: 1);
            _hotkeyManager.HotkeyPressed += OnGlobalHotkeyPressed;
            // Själva registreringen sker i ApplyLoadedSettings() när settings laddats.
            System.Diagnostics.Debug.WriteLine("[Klipp] HotkeyManager skapad");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Klipp] Hotkey-setup fel: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves an instant clip or toggles recording in the selected capture mode.
    /// </summary>
    private void OnGlobalHotkeyPressed(object? sender, EventArgs e)
    {
        // Hotkey-eventet kommer från WndProc (UI-tråden) men vi går via dispatcher
        // för säkerhets skull, eftersom vi rör UI-element.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_capturingHotkey || _capturingBookmarkHotkey || _capturingEditorShortcutAction is not null) return;
            if (_mode == AppMode.Recording)
            {
                OnRecordClicked(this, new RoutedEventArgs());
            }
            else if (_mode == AppMode.Clip && _clipMode.IsMonitoring)
            {
                OnSaveClipClicked(this, new RoutedEventArgs());
            }
        });
    }

    /// <summary>
    /// Klick på hotkey-knappen → gå in i "fånga"-läge. Nästa tangentkombination
    /// användaren trycker blir den nya hotkeyen.
    /// </summary>
    private void OnHotkeyButtonClicked(object sender, RoutedEventArgs e)
    {
        StopBookmarkHotkeyCapture();
        StopEditorShortcutCapture();
        if (_capturingHotkey)
        {
            // Redan i capture-läge → avbryt
            StopHotkeyCapture();
            return;
        }

        _capturingHotkey = true;
        HotkeyText.Text = "Press a shortcut…";

        // Lyssna på tangenttryck på fönstret medan vi fångar
        this.Content.KeyDown += OnHotkeyCaptureKeyDown;
    }

    private void OnWindowKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_updateRestarting)
        {
            e.Handled = true;
            return;
        }
        if (e.Key != Windows.System.VirtualKey.Escape)
            return;

        if (_fullscreenInlineCard is not null)
        {
            ExitInlineFullscreen();
            e.Handled = true;
            return;
        }

        if (e.Handled) return;

        if (_capturingBookmarkHotkey)
        {
            StopBookmarkHotkeyCapture();
            e.Handled = true;
            return;
        }

        if (_capturingHotkey)
        {
            StopHotkeyCapture();
            e.Handled = true;
            return;
        }

        if (_capturingEditorShortcutAction is not null)
        {
            StopEditorShortcutCapture();
            e.Handled = true;
            return;
        }

        if (_sideMenuOpen)
        {
            HideSideMenu();
            e.Handled = true;
        }
    }

    private void StopHotkeyCapture()
    {
        _capturingHotkey = false;
        this.Content.KeyDown -= OnHotkeyCaptureKeyDown;
        HotkeyText.Text = _settings.Current.HotkeyDisplay;
    }

    private async void OnHotkeyCaptureKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (!_capturingHotkey) return;

        var key = e.Key;

        // Ignorera när användaren bara trycker en modifier själv — vänta på en riktig tangent
        if (key is Windows.System.VirtualKey.Control
                or Windows.System.VirtualKey.Shift
                or Windows.System.VirtualKey.Menu        // Alt
                or Windows.System.VirtualKey.LeftWindows
                or Windows.System.VirtualKey.RightWindows)
        {
            return;
        }

        e.Handled = true;

        // Läs av vilka modifiers som hålls nere just nu
        var ctrlDown = IsKeyDown(Windows.System.VirtualKey.Control);
        var shiftDown = IsKeyDown(Windows.System.VirtualKey.Shift);
        var altDown = IsKeyDown(Windows.System.VirtualKey.Menu);

        HotkeyManager.Modifiers mods = HotkeyManager.Modifiers.None;
        if (ctrlDown) mods |= HotkeyManager.Modifiers.Control;
        if (shiftDown) mods |= HotkeyManager.Modifiers.Shift;
        if (altDown) mods |= HotkeyManager.Modifiers.Alt;

        // Bygg läsbar text
        var parts = new System.Collections.Generic.List<string>();
        if (ctrlDown) parts.Add("Ctrl");
        if (shiftDown) parts.Add("Shift");
        if (altDown) parts.Add("Alt");
        parts.Add(key.ToString());
        var display = string.Join(" + ", parts);

        var editorModifiers = ToEditorShortcutModifiers(ctrlDown, shiftDown, altDown);
        if ((uint)mods == _settings.Current.BookmarkHotkeyModifiers && (uint)key == _settings.Current.BookmarkHotkeyVirtualKey)
        {
            StopHotkeyCapture();
            await ShowErrorAsync("Shortcut already in use", "This shortcut marks recording highlights. Choose another shortcut.");
            return;
        }
        if (EditorShortcutCatalog.FindMatch(_settings.Current.EditorShortcuts, editorModifiers, (uint)key) is { } conflict)
        {
            StopHotkeyCapture();
            var label = EditorShortcutCatalog.Definitions
                .First(definition => definition.Action == conflict.Action)
                .Label;
            await ShowErrorAsync(
                "Shortcut already in use",
                $"{display} is assigned to the editor command '{label}'. Choose another shortcut.");
            return;
        }

        // Försök registrera den nya hotkeyen
        var vk = (uint)key;
        var success = _hotkeyManager?.Register(mods, vk) ?? false;

        StopHotkeyCapture();

        if (success)
        {
            _settings.Current.HotkeyModifiers = (uint)mods;
            _settings.Current.HotkeyVirtualKey = vk;
            _settings.Current.HotkeyDisplay = display;
            HotkeyText.Text = display;
            await _settings.SaveAsync();
        }
        else
        {
            // Registrering misslyckades (t.ex. kombinationen är upptagen av annan app)
            HotkeyText.Text = _settings.Current.HotkeyDisplay;
            await ShowErrorAsync(
                "Could not set shortcut",
                $"Could not register {display}. Another app may be using it. Try a different shortcut.");
        }
    }

    /// <summary>Kollar om en tangent hålls nere just nu.</summary>
    private static bool IsKeyDown(Windows.System.VirtualKey key)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    private void OnEditorShortcutButtonClicked(object sender, RoutedEventArgs e)
    {
        StopBookmarkHotkeyCapture();
        if (sender is not FrameworkElement { Tag: string action } ||
            EditorShortcutCatalog.Definitions.All(definition => definition.Action != action))
        {
            return;
        }

        StopHotkeyCapture();
        if (_capturingEditorShortcutAction == action)
        {
            StopEditorShortcutCapture();
            return;
        }

        StopEditorShortcutCapture();
        _capturingEditorShortcutAction = action;
        EditorShortcutStatusText.Text = "Press a shortcut. Escape cancels.";
        SyncEditorShortcutItems();
        this.Content.KeyDown += OnEditorShortcutCaptureKeyDown;
    }

    private async void OnEditorShortcutCaptureKeyDown(
        object sender,
        Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (_capturingEditorShortcutAction is not { } action)
            return;

        var key = e.Key;
        if (key is Windows.System.VirtualKey.Control
                or Windows.System.VirtualKey.Shift
                or Windows.System.VirtualKey.Menu
                or Windows.System.VirtualKey.LeftWindows
                or Windows.System.VirtualKey.RightWindows)
        {
            return;
        }

        e.Handled = true;
        var modifiers = ToEditorShortcutModifiers(
            IsKeyDown(Windows.System.VirtualKey.Control),
            IsKeyDown(Windows.System.VirtualKey.Shift),
            IsKeyDown(Windows.System.VirtualKey.Menu));
        var virtualKey = (uint)key;
        if (!EditorShortcutCatalog.IsValidGesture(modifiers, virtualKey))
        {
            EditorShortcutStatusText.Text = "This shortcut is reserved for timeline navigation. Choose another.";
            return;
        }

        if (IsGlobalHotkey(modifiers, virtualKey))
        {
            EditorShortcutStatusText.Text = "This shortcut is assigned to capture or recording highlights. Choose another.";
            return;
        }

        _settings.Current.EditorShortcuts = EditorShortcutCatalog.Rebind(
            _settings.Current.EditorShortcuts,
            action,
            modifiers,
            virtualKey);
        StopEditorShortcutCapture(clearStatus: false);
        _editorView?.ConfigureShortcuts(_settings.Current.EditorShortcuts);
        var binding = _settings.Current.EditorShortcuts.Single(item => item.Action == action);
        EditorShortcutStatusText.Text = $"Saved: {EditorShortcutCatalog.FormatGesture(binding)}";
        await _settings.SaveAsync();
    }

    private async void OnResetEditorShortcutsClicked(object sender, RoutedEventArgs e)
    {
        StopHotkeyCapture();
        StopBookmarkHotkeyCapture();
        StopEditorShortcutCapture();
        var defaults = EditorShortcutCatalog.CreateDefaults();
        var conflict = defaults.FirstOrDefault(binding => IsGlobalHotkey(binding.Modifiers, binding.VirtualKey));
        if (conflict is not null)
        {
            EditorShortcutStatusText.Text = $"{EditorShortcutCatalog.FormatGesture(conflict)} is assigned to capture or highlights. Change that capture shortcut before resetting editor shortcuts.";
            return;
        }
        _settings.Current.EditorShortcuts = defaults;
        SyncEditorShortcutItems();
        _editorView?.ConfigureShortcuts(_settings.Current.EditorShortcuts);
        EditorShortcutStatusText.Text = "Editor shortcuts reset.";
        await _settings.SaveAsync();
    }

    private void StopEditorShortcutCapture(bool clearStatus = true)
    {
        if (_capturingEditorShortcutAction is null)
            return;

        this.Content.KeyDown -= OnEditorShortcutCaptureKeyDown;
        _capturingEditorShortcutAction = null;
        if (clearStatus)
            EditorShortcutStatusText.Text = string.Empty;
        SyncEditorShortcutItems();
    }

    private void SyncEditorShortcutItems()
    {
        var normalized = EditorShortcutCatalog.Normalize(_settings.Current.EditorShortcuts);
        _settings.Current.EditorShortcuts = normalized;
        EditorShortcutItems.Clear();
        foreach (var definition in EditorShortcutCatalog.Definitions)
        {
            var binding = normalized.Single(item => item.Action == definition.Action);
            var display = definition.Action == _capturingEditorShortcutAction
                ? "Press a shortcut…"
                : EditorShortcutCatalog.FormatGesture(binding);
            EditorShortcutItems.Add(new EditorShortcutViewModel(definition.Action, definition.Label, display));
        }
    }

    private bool IsGlobalHotkey(uint editorModifiers, uint virtualKey)
    {
        var flags = (EditorShortcutModifiers)editorModifiers;
        var native = (uint)(HotkeyManager.Modifiers.None);
        if (flags.HasFlag(EditorShortcutModifiers.Control)) native |= 2;
        if (flags.HasFlag(EditorShortcutModifiers.Shift)) native |= 4;
        if (flags.HasFlag(EditorShortcutModifiers.Alt)) native |= 1;
        if (native == _settings.Current.BookmarkHotkeyModifiers && virtualKey == _settings.Current.BookmarkHotkeyVirtualKey) return true;
        var hotkeyModifiers = (HotkeyManager.Modifiers)_settings.Current.HotkeyModifiers;
        return virtualKey == _settings.Current.HotkeyVirtualKey &&
               flags.HasFlag(EditorShortcutModifiers.Control) == hotkeyModifiers.HasFlag(HotkeyManager.Modifiers.Control) &&
               flags.HasFlag(EditorShortcutModifiers.Shift) == hotkeyModifiers.HasFlag(HotkeyManager.Modifiers.Shift) &&
               flags.HasFlag(EditorShortcutModifiers.Alt) == hotkeyModifiers.HasFlag(HotkeyManager.Modifiers.Alt);
    }

    private static uint ToEditorShortcutModifiers(bool control, bool shift, bool alt)
    {
        var modifiers = EditorShortcutModifiers.None;
        if (control)
            modifiers |= EditorShortcutModifiers.Control;
        if (shift)
            modifiers |= EditorShortcutModifiers.Shift;
        if (alt)
            modifiers |= EditorShortcutModifiers.Alt;
        return (uint)modifiers;
    }


    private async Task InitializeAsync()
    {
        await _settings.LoadAsync();
        await ApplyPerformanceDefaultsMigrationAsync();
        ApplyClipsDirectorySetting();
        ApplyLoadedSettings();
        InitializeRecordingTools();
        SyncSettingsExperience();
        await InitializeCaptureSourcesAsync();
        await RunAutomaticCacheCleanupAsync();
        _cacheTimer.Start();

        _mode = AppMode.Clip;
        ClipLengthSelector.Visibility = Visibility.Visible;
        UpdateStatusUI();

        await _captureOperationGate.WaitAsync();
        try
        {
            if (_settings.Current.AutoStartClipMode)
                await StartAutoMonitoringAsync();
            else
                UpdateStatusUI();
        }
        finally { _captureOperationGate.Release(); }

        StartCaptureSourceMonitoring();

        await LoadLibraryAsync();
        await RefreshRecordingRecoveryAsync();
        UpdateOriginalClipRetentionControls();
        await RunAutomaticOriginalClipRetentionAsync();
        await RefreshOriginalClipRetentionStatusAsync();
        try
        {
            await _exportQueue.InitializeAsync();
            _editorView?.SetExportJobs(_exportQueue.Jobs);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Klipp] Export queue history could not be loaded: {ex.Message}");
        }
        InitializeUpdates();
    }

    private async Task ApplyPerformanceDefaultsMigrationAsync()
    {
        if (_settings.Current.PerformanceDefaultsVersion >= 1)
            return;

        var recommendation = _captureRecommendations.GetRecommendation();
        _settings.Current.CaptureResolutionMode = recommendation.ResolutionMode;
        _settings.Current.CaptureFrameRate = recommendation.FrameRate;
        _settings.Current.CaptureBitrateMbps = recommendation.BitrateMbps;
        _settings.Current.AutoStartClipMode = false;
        _settings.Current.PerformanceDefaultsVersion = 1;

        await _settings.SaveAsync();
    }

    private void ApplyClipsDirectorySetting()
    {
        try
        {
            LibraryService.SetClipsDirectory(_settings.Current.ClipsDirectory);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Klipp] Ogiltig klipp-mapp i settings: {ex.Message}");
            LibraryService.SetClipsDirectory(null);
        }

        _settings.Current.ClipsDirectory = LibraryService.ClipsDirectory;
    }

    private async Task LoadLibraryAsync()
    {
        _suppressLibraryFilter = true;
        try
        {
            await LibraryService.LoadFromDiskAsync();
        }
        finally
        {
            _suppressLibraryFilter = false;
        }

        RefreshLibraryCollectionFilter();
        ApplyLibraryFilter();
    }

    /// <summary>
    /// Applicerar inlästa inställningar på UI + hotkey efter att settings laddats.
    /// </summary>
    private void ApplyLoadedSettings()
    {
        _syncingSettingsUi = true;
        try
        {
            SetClipLengthSelection(NormalizeClipLength(_settings.Current.ClipLengthSeconds));

            HotkeyText.Text = _settings.Current.HotkeyDisplay;
            _settings.Current.EditorShortcuts = EditorShortcutCatalog.Normalize(_settings.Current.EditorShortcuts);
            SyncEditorShortcutItems();
            _editorView?.ConfigureShortcuts(_settings.Current.EditorShortcuts);
            SyncCaptureSelectorsFromSettings();

            AutoStartClipModeToggle.IsOn = _settings.Current.AutoStartClipMode;
            HideCaptureBorderToggle.IsOn = _settings.Current.HideCaptureBorder;
            AutoCaptureApplicationProfileToggle.IsOn = _settings.Current.AutoSelectCaptureApplicationProfiles;
            AutoCleanCacheToggle.IsOn = _settings.Current.AutoCleanCache;
            SyncCacheBudgetSelectorFromSettings();
            OriginalClipRetentionToggle.IsOn = _settings.Current.AutoCleanOldOriginalClips;
            SyncOriginalClipRetentionDaysSelectorFromSettings();
            UpdateOriginalClipRetentionControls();
            RefreshCaptureRecommendationUi();
            RefreshCaptureApplicationUi();
        }
        finally
        {
            _syncingSettingsUi = false;
        }

        // Registrera om hotkeyen med inlästa värden
        _hotkeyManager?.Register(
            (HotkeyManager.Modifiers)_settings.Current.HotkeyModifiers,
            _settings.Current.HotkeyVirtualKey);
    }

    private void SyncCaptureSelectorsFromSettings()
    {
        CaptureProfileSelector.SelectedIndex = _settings.Current.CaptureProfileId switch
        {
            "recommended" => 0,
            "eco" => 1,
            "balanced" => 2,
            "quality" => 3,
            "smooth" => 4,
            _ => 5
        };

        ResolutionSelector.SelectedIndex = NormalizeResolutionMode(_settings.Current.CaptureResolutionMode) switch
        {
            "540p" => 3,
            "720p" => 2,
            "1080p" => 1,
            _ => 0
        };

        FrameRateSelector.SelectedIndex = NormalizeFrameRate(_settings.Current.CaptureFrameRate) switch
        {
            15 => 0,
            24 => 1,
            60 => 3,
            _ => 2
        };

        BitrateSelector.SelectedIndex = NormalizeBitrate(_settings.Current.CaptureBitrateMbps) switch
        {
            5 => 0,
            8 => 1,
            24 => 3,
            35 => 4,
            _ => 2
        };
    }

    private void SyncCacheBudgetSelectorFromSettings()
    {
        CacheBudgetSelector.SelectedIndex = CacheMaintenanceService.NormalizeBudgetMb(
            _settings.Current.CacheBudgetMb) switch
        {
            512 => 0,
            1024 => 1,
            2048 => 2,
            8192 => 4,
            _ => 3
        };
    }

    private void SyncOriginalClipRetentionDaysSelectorFromSettings()
    {
        OriginalClipRetentionDaysSelector.SelectedIndex =
            OriginalClipRetentionPolicy.NormalizeRetentionDays(
                _settings.Current.OriginalClipRetentionDays) switch
            {
                7 => 0,
                30 => 1,
                60 => 2,
                180 => 4,
                365 => 5,
                _ => 3
            };
    }

    private void RefreshCaptureRecommendationUi()
    {
        if (RecommendedCaptureText is null || RecommendedCaptureReasonText is null)
            return;

        var recommendation = _captureRecommendations.GetRecommendation();
        RecommendedCaptureText.Text = recommendation.Summary;
        RecommendedCaptureReasonText.Text = recommendation.Reason;
    }

    private void RefreshCaptureApplicationUi()
    {
        if (CaptureApplicationSelector is null || CaptureApplicationSummaryText is null)
            return;

        var previouslySelected = (CaptureApplicationSelector.SelectedItem as CaptureApplicationInfo)?.ProcessName;
        var applications = _captureProfiles.GetRunningApplications().ToList();
        foreach (var saved in _settings.Current.CaptureApplicationProfiles)
        {
            if (applications.Any(application =>
                    string.Equals(application.ProcessName, saved.ProcessName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var displayName = string.IsNullOrWhiteSpace(saved.DisplayName)
                ? saved.ProcessName
                : saved.DisplayName;
            applications.Add(new CaptureApplicationInfo(
                saved.ProcessName,
                $"{displayName} · not running",
                string.Empty));
        }
        applications = applications
            .OrderBy(application => application.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        CaptureApplicationSelector.ItemsSource = applications;
        var preferredProcess = previouslySelected ?? _captureProfiles.LastExternalApplication?.ProcessName;
        CaptureApplicationSelector.SelectedItem = applications.FirstOrDefault(application =>
            string.Equals(application.ProcessName, preferredProcess, StringComparison.OrdinalIgnoreCase));
        if (CaptureApplicationSelector.SelectedItem is null && applications.Count > 0)
            CaptureApplicationSelector.SelectedIndex = 0;

        var count = _settings.Current.CaptureApplicationProfiles.Count;
        CaptureApplicationSummaryText.Text = count == 0
            ? "No application profiles saved"
            : $"{count} application profile{(count == 1 ? string.Empty : "s")} saved";
    }

    /// <summary>
    /// Startar Klipp-läge + övervakning automatiskt vid appstart, så användaren
    /// kan klippa direkt utan att klicka. Hoppar över om FFmpeg inte är installerat
    /// (vi vill inte trigga en tyst nedladdning vid allra första körningen).
    /// </summary>
    private async Task StartAutoMonitoringAsync()
    {
        _instantCaptureRequested = true;
        if (!_clipMode.IsFFmpegInstalled)
        {
            // FFmpeg saknas — låt användaren starta manuellt så de ser nedladdningen.
            _instantCaptureRequested = false;
            return;
        }
        if (!await EnsureCaptureStorageAvailableAsync(showDialog: false))
            return;
        if (!await PrepareCaptureTargetForStartAsync())
        {
            UpdateStatusUI();
            return;
        }

        StatusPill.BorderBrush = (Brush)Application.Current.Resources["KlippWarningBrush"];
        StatusDot.Fill = (Brush)Application.Current.Resources["KlippWarningBrush"];
        StatusText.Foreground = (Brush)Application.Current.Resources["KlippTextPrimaryBrush"];
        StatusText.Text = "Starting capture…";
        RecordButton.Content = "Starting…";
        RecordButton.IsEnabled = false;

        try
        {
            LibraryService.PauseBackgroundMetadata();
            var captureOptions = GetCaptureOptions();
            _activeCaptureOptions = captureOptions;
            await RunServiceOperationAsync(() => _clipMode.StartMonitoringAsync(captureOptions));
            _statusTimer.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Klipp] Auto-monitoring misslyckades: {ex.Message}");
            _instantCaptureRequested = false;
        }
        finally
        {
            RecordButton.IsEnabled = true;
            if (!_recording.IsRecording && !_clipMode.IsMonitoring)
                LibraryService.ResumeBackgroundMetadata();
            UpdateStatusUI();
        }
    }

    // === Lägesväljare ===

    private void OnMenuButtonClicked(object sender, RoutedEventArgs e)
    {
        if (_sideMenuOpen)
            HideSideMenu();
        else
            ShowSideMenu();
    }

    private void OnCloseSideMenuClicked(object sender, RoutedEventArgs e)
    {
        HideSideMenu();
    }

    private void OnSideMenuBackdropTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        HideSideMenu();
    }

    private void OnSideMenuLibraryClicked(object sender, RoutedEventArgs e)
    {
        HideSideMenu();
        if (PlayerHost.Visibility == Visibility.Visible || EditorHost.Visibility == Visibility.Visible)
            ShowLibrary();
    }

    private void ShowSideMenu()
    {
        ClipCard.StopActivePreview();
        SyncSettingsExperience();
        RefreshCaptureApplicationUi();
        UpdateOriginalClipRetentionControls();
        _ = RefreshCacheStatusAsync();
        _ = RefreshOriginalClipRetentionStatusAsync();
        _sideMenuOpen = true;
        UpdateCaptureAudioMeterState();
        SideMenuOverlay.Visibility = Visibility.Visible;
        SideMenuPanel.IsHitTestVisible = true;
        SideMenuBackdrop.IsHitTestVisible = true;
        SideMenuPanelTransform.X = 0;
        SideMenuPanel.Opacity = 1;
        SideMenuBackdrop.Opacity = 1;
        MenuButton.BorderBrush = (Brush)Application.Current.Resources["KlippAccentBrush"];
        MenuButton.Background = (Brush)Application.Current.Resources["KlippSurfaceElevatedBrush"];
    }

    private void HideSideMenu()
    {
        StopSettingsShortcutCapture();
        if (!_sideMenuOpen)
            return;

        _sideMenuOpen = false;
        UpdateCaptureAudioMeterState();
        SideMenuOverlay.Visibility = Visibility.Collapsed;
        SideMenuPanelTransform.X = 0;
        SideMenuPanel.Opacity = 1;
        SideMenuBackdrop.Opacity = 1;
        MenuButton.BorderBrush = (Brush)Application.Current.Resources["KlippBorderBrush"];
        MenuButton.Background = (Brush)Application.Current.Resources["KlippSurfaceBrush"];
        SideMenuPanel.IsHitTestVisible = false;
        SideMenuBackdrop.IsHitTestVisible = false;
    }

    private void OnTopBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateCaptureToolbarLayout(e.NewSize.Width);
    }

    private void UpdateCaptureToolbarLayout(double width)
    {
        if (width <= 0)
            return;
        HotkeyButton.Visibility = width >= 1100 ? Visibility.Visible : Visibility.Collapsed;
        ClipLengthSelector.Visibility = _mode == AppMode.Clip && width >= 820
            ? Visibility.Visible
            : Visibility.Collapsed;
        CaptureModeControls.Spacing = width >= 900 ? 16 : 8;
        CaptureSourceButton.Width = width >= 1200 ? 216 : width >= 950 ? 192 : width >= 600 ? 154 : 130;
        var compact = width < 780;
        Grid.SetColumnSpan(CaptureModeControls, compact ? 3 : 1);
        Grid.SetRow(CaptureActionControls, compact ? 1 : 0);
        Grid.SetColumnSpan(CaptureStatusControls, compact ? 2 : 3);
        MenuBrandText.Visibility = width < 600 ? Visibility.Collapsed : Visibility.Visible;
        StatusText.MaxWidth = width < 600 ? 150 : double.PositiveInfinity;
        StatusText.TextTrimming = TextTrimming.CharacterEllipsis;
        RecordingBookmarkLabel.Visibility = width < 700 ? Visibility.Collapsed : Visibility.Visible;
        CaptureMixerLabel.Visibility = width < 700 ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnRecordingModeClicked(object sender, RoutedEventArgs e)
    {
        await SwitchModeAsync(AppMode.Recording);
    }

    private async void OnClipModeClicked(object sender, RoutedEventArgs e)
    {
        await SwitchModeAsync(AppMode.Clip);
    }

    private async Task SwitchModeAsync(AppMode newMode)
    {
        if (!_uiReady || !_captureSessionReady || _isSwitchingMode || newMode == _mode ||
            !await _captureOperationGate.WaitAsync(0)) return;

        _isSwitchingMode = true;
        try
        {
            _instantCaptureRequested = false;
            SetModeSwitchingState(true, newMode == AppMode.Clip ? "Starting instant clips…" : "Switching to recording…");
            _mode = newMode;
            UpdateModeChrome();
            UpdateCaptureToolbarLayout(TopBar.ActualWidth);
            UpdateStatusUI();
            await Task.Yield();

            // Sömlöst lägesbyte: stoppa det gamla läget innan vi byter.
            try
            {
                if (_recording.IsRecording)
                {
                    // Pågående inspelning avslutas och sparas
                    var recordedSeconds = _recording.RecordedSeconds;
                    var outputPath = await RunServiceOperationAsync(_recording.StopAsync);
                    _statusTimer.Stop();
                    if (outputPath is not null)
                        LibraryService.AddNewClip(outputPath);
                    NotifyRecordingStopped(outputPath, recordedSeconds, "Switched to Instant clips.");
                }
                else if (_clipMode.IsMonitoring)
                {
                    await RunServiceOperationAsync(() => _clipMode.StopMonitoringAsync());
                    _statusTimer.Stop();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Klipp] Fel vid lägesbyte: {ex.Message}");
            }

            // I Klipp-läge: starta övervakning automatiskt (om FFmpeg finns)
            if (_mode == AppMode.Clip)
            {
                await StartAutoMonitoringAsync();
            }
        }
        finally
        {
            _isSwitchingMode = false;
            _captureOperationGate.Release();
            SetModeSwitchingState(false);
            UpdateStatusUI();
            await RefreshRecordingRecoveryAsync();
        }
    }

    private void SetModeSwitchingState(bool isSwitching, string? statusText = null)
    {
        RecordingModeButton.IsEnabled = !isSwitching;
        ClipModeButton.IsEnabled = !isSwitching;
        RecordButton.IsEnabled = !isSwitching;
        SaveClipButton.IsEnabled = !isSwitching && _mode == AppMode.Clip && _clipMode.IsMonitoring;

        if (isSwitching && statusText is not null)
        {
            StatusPill.BorderBrush = (Brush)Application.Current.Resources["KlippWarningBrush"];
            StatusText.Text = statusText;
            StatusText.Foreground = (Brush)Application.Current.Resources["KlippTextPrimaryBrush"];
            StatusDot.Fill = (Brush)Application.Current.Resources["KlippWarningBrush"];
        }
    }

    /// <summary>Markerar valt läge separat från om capture faktiskt är aktiv.</summary>
    private void UpdateModeChrome()
    {
        var recordingSelected = _mode == AppMode.Recording;
        var clipSelected = _mode == AppMode.Clip;

        ApplyModeButtonChrome(
            RecordingModeButton,
            RecordingModeText,
            RecordingModeDot,
            RecordingModeScale,
            selected: recordingSelected,
            activeBrush: _recording.IsRecording
                ? (Brush)Application.Current.Resources["KlippDangerBrush"]
                : null);

        ApplyModeButtonChrome(
            ClipModeButton,
            ClipModeText,
            ClipModeDot,
            ClipModeScale,
            selected: clipSelected,
            activeBrush: _clipMode.IsMonitoring
                ? (Brush)Application.Current.Resources["KlippSuccessBrush"]
                : null);
    }

    private static void ApplyModeButtonChrome(
        Button button,
        TextBlock text,
        Ellipse dot,
        ScaleTransform scale,
        bool selected,
        Brush? activeBrush)
    {
        var resources = Application.Current.Resources;

        button.Background = selected
            ? (Brush)resources["KlippSurfaceElevatedBrush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.BorderBrush = selected
            ? (Brush)resources["KlippBorderBrush"]
            : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.BorderThickness = selected ? new Thickness(1) : new Thickness(0);

        text.FontWeight = selected
            ? Microsoft.UI.Text.FontWeights.SemiBold
            : Microsoft.UI.Text.FontWeights.Normal;
        text.Foreground = selected
            ? (Brush)resources["KlippTextPrimaryBrush"]
            : (Brush)resources["KlippTextMutedBrush"];

        if (activeBrush is not null)
        {
            dot.Fill = activeBrush;
            dot.Opacity = 1;
        }
        else
        {
            dot.Fill = selected
                ? (Brush)resources["KlippAccentBrush"]
                : (Brush)resources["KlippTextMutedBrush"];
            dot.Opacity = selected ? 0.9 : 0.55;
        }

        scale.ScaleX = 1;
        scale.ScaleY = 1;
    }

    /// <summary>Sparar klipplängd-valet när användaren ändrar dropdownen.</summary>
    private async void OnClipLengthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi) return;

        _settings.Current.ClipLengthSeconds = GetSelectedClipLengthSeconds();
        SetClipLengthSelection(_settings.Current.ClipLengthSeconds);
        await _settings.SaveAsync();
    }

    /// <summary>Läser vald klipplängd i sekunder från dropdownen.</summary>
    private int GetSelectedClipLengthSeconds()
    {
        return ClipLengthSelector.SelectedIndex switch
        {
            0 => 15,
            1 => 30,
            2 => 60,
            3 => 120,
            _ => 30
        };
    }

    private async void OnDrawerClipLengthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi) return;

        _settings.Current.ClipLengthSeconds = GetSelectedDrawerClipLengthSeconds();
        SetClipLengthSelection(_settings.Current.ClipLengthSeconds);
        await _settings.SaveAsync();
    }

    private async void OnCaptureProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi ||
            CaptureProfileSelector.SelectedItem is not ComboBoxItem item ||
            item.Tag is not string profileId)
        {
            return;
        }

        if (string.Equals(profileId, "recommended", StringComparison.OrdinalIgnoreCase))
        {
            var recommendation = _captureRecommendations.GetRecommendation();
            _settings.Current.CaptureProfileId = "recommended";
            _settings.Current.CaptureResolutionMode = recommendation.ResolutionMode;
            _settings.Current.CaptureFrameRate = recommendation.FrameRate;
            _settings.Current.CaptureBitrateMbps = recommendation.BitrateMbps;
        }
        else if (CaptureProfileService.FindBuiltIn(profileId) is { } profile)
        {
            CaptureProfileService.ApplyBuiltIn(_settings.Current, profile);
        }
        else
        {
            _settings.Current.CaptureProfileId = "custom";
        }

        var wasSyncing = _syncingSettingsUi;
        _syncingSettingsUi = true;
        try { SyncCaptureSelectorsFromSettings(); }
        finally { _syncingSettingsUi = wasSyncing; }
        await _settings.SaveAsync();

        StatusText.Text = _recording.IsRecording || _clipMode.IsMonitoring
            ? "Capture profile applies when capture restarts"
            : $"{CaptureProfileService.GetProfileName(_settings.Current.CaptureProfileId)} profile selected";
    }

    private async void OnFrameRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi) return;

        MarkCaptureProfileCustom();
        _settings.Current.CaptureFrameRate = GetSelectedFrameRate();
        await _settings.SaveAsync();

        if (_recording.IsRecording || _clipMode.IsMonitoring)
            StatusText.Text = "Frame rate applies to the next recording";
    }

    private async void OnResolutionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi) return;

        MarkCaptureProfileCustom();
        _settings.Current.CaptureResolutionMode = GetSelectedResolutionMode();
        await _settings.SaveAsync();

        if (_recording.IsRecording || _clipMode.IsMonitoring)
            StatusText.Text = "Resolution applies to the next recording";
    }

    private async void OnBitrateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi) return;

        MarkCaptureProfileCustom();
        _settings.Current.CaptureBitrateMbps = GetSelectedBitrate();
        await _settings.SaveAsync();

        if (_recording.IsRecording || _clipMode.IsMonitoring)
            StatusText.Text = "Bitrate applies to the next recording";
    }

    private async void OnApplyRecommendedCaptureClicked(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;

        var recommendation = _captureRecommendations.GetRecommendation();
        _settings.Current.CaptureResolutionMode = recommendation.ResolutionMode;
        _settings.Current.CaptureFrameRate = recommendation.FrameRate;
        _settings.Current.CaptureBitrateMbps = recommendation.BitrateMbps;
        _settings.Current.CaptureProfileId = "recommended";

        var wasSyncing = _syncingSettingsUi;
        _syncingSettingsUi = true;
        try
        {
            SyncCaptureSelectorsFromSettings();
            RefreshCaptureRecommendationUi();
        }
        finally
        {
            _syncingSettingsUi = wasSyncing;
        }

        await _settings.SaveAsync();

        StatusText.Text = _recording.IsRecording || _clipMode.IsMonitoring
            ? "Recommended profile applies when capture restarts"
            : "Recommended capture profile selected";
    }

    private async void OnAutoCaptureApplicationProfileToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi) return;

        _settings.Current.AutoSelectCaptureApplicationProfiles = AutoCaptureApplicationProfileToggle.IsOn;
        await _settings.SaveAsync();
    }

    private async void OnSaveCaptureApplicationProfileClicked(object sender, RoutedEventArgs e)
    {
        var application = CaptureApplicationSelector.SelectedItem as CaptureApplicationInfo ??
            _captureProfiles.LastExternalApplication;
        if (application is null)
        {
            await ShowErrorAsync(
                "No application selected",
                "Open the game or application, then reopen Settings and select it from the list.");
            return;
        }

        CaptureProfileService.SaveApplicationProfile(
            _settings.Current,
            application,
            GetConfiguredCaptureOptions(),
            CaptureProfileService.GetProfileName(_settings.Current.CaptureProfileId));
        _captureProfiles.RememberApplication(application);
        _settings.Current.AutoSelectCaptureApplicationProfiles = true;
        AutoCaptureApplicationProfileToggle.IsOn = true;
        await _settings.SaveAsync();
        RefreshCaptureApplicationUi();
        StatusText.Text = $"Capture profile saved for {application.ProcessName}";
    }

    private async void OnRemoveCaptureApplicationProfileClicked(object sender, RoutedEventArgs e)
    {
        var application = CaptureApplicationSelector.SelectedItem as CaptureApplicationInfo ??
            _captureProfiles.LastExternalApplication;
        if (application is null ||
            !CaptureProfileService.RemoveApplicationProfile(_settings.Current, application.ProcessName))
        {
            StatusText.Text = "No saved profile for this application";
            return;
        }

        await _settings.SaveAsync();
        RefreshCaptureApplicationUi();
        StatusText.Text = $"Application profile removed for {application.ProcessName}";
    }

    private async void OnAutoStartClipModeToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi) return;

        _settings.Current.AutoStartClipMode = AutoStartClipModeToggle.IsOn;
        await _settings.SaveAsync();
    }

    private async void OnHideCaptureBorderToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi) return;

        _settings.Current.HideCaptureBorder = HideCaptureBorderToggle.IsOn;
        await _settings.SaveAsync();

        if (_recording.IsRecording || _clipMode.IsMonitoring)
            StatusText.Text = "Capture border setting applies to the next recording";
    }

    private async void OnCacheBudgetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi) return;

        _settings.Current.CacheBudgetMb = GetSelectedCacheBudgetMb();
        await _settings.SaveAsync();
        if (_settings.Current.AutoCleanCache)
            await RunAutomaticCacheCleanupAsync();
        else
            await RefreshCacheStatusAsync();
    }

    private async void OnAutoCleanCacheToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi) return;

        _settings.Current.AutoCleanCache = AutoCleanCacheToggle.IsOn;
        await _settings.SaveAsync();
        if (_settings.Current.AutoCleanCache)
            await RunAutomaticCacheCleanupAsync();
    }

    private async void OnOriginalClipRetentionToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi) return;

        if (OriginalClipRetentionToggle.IsOn)
        {
            var retentionDays = GetSelectedOriginalClipRetentionDays();
            var dialog = AccessibleDialogFactory.Create(
                this.Content.XamlRoot,
                "Enable automatic clip cleanup?",
                $"Klipp can move original clips older than {retentionDays} days to the Windows Recycle Bin. " +
                "Favorites, media used in saved projects, and the 20 newest clips are always kept.",
                primaryButtonText: "Enable",
                closeButtonText: "Cancel",
                defaultButton: ContentDialogButton.Close,
                automationHelpText: "Confirm automatic cleanup of old original clips.");
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                _syncingSettingsUi = true;
                try { OriginalClipRetentionToggle.IsOn = false; }
                finally { _syncingSettingsUi = false; }
                return;
            }
        }

        _settings.Current.AutoCleanOldOriginalClips = OriginalClipRetentionToggle.IsOn;
        if (_settings.Current.AutoCleanOldOriginalClips)
            _settings.Current.LastOriginalClipRetentionRunUtc = null;
        await _settings.SaveAsync();
        UpdateOriginalClipRetentionControls();
        await RefreshOriginalClipRetentionStatusAsync();
    }

    private async void OnOriginalClipRetentionDaysChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi) return;

        _settings.Current.OriginalClipRetentionDays = GetSelectedOriginalClipRetentionDays();
        await _settings.SaveAsync();
        await RefreshOriginalClipRetentionStatusAsync();
    }

    private async void OnRunOriginalClipRetentionClicked(object sender, RoutedEventArgs e)
    {
        await RunOriginalClipRetentionAsync(requireConfirmation: true);
    }

    private async void OnRefreshCacheDiagnosticsClicked(object sender, RoutedEventArgs e)
    {
        await RefreshCacheStatusAsync();
    }

    private async void OnClearCacheClicked(object sender, RoutedEventArgs e)
    {
        if (_cacheOperationRunning) return;

        _cacheOperationRunning = true;
        ClearCacheButton.IsEnabled = false;
        try
        {
            var result = await _cacheMaintenance.ClearAsync(TimeSpan.Zero);
            StatusText.Text = result.FailedFiles > 0
                ? $"Cleared {result.DeletedFiles} files · {result.FailedFiles} still in use"
                : result.DeletedFiles == 0
                    ? "The cache is already empty"
                    : $"Cleared {result.DeletedFiles} cache files · {FormatHealthBytes(result.ReclaimedBytes)}";
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not clear cache", ex.Message);
        }
        finally
        {
            _cacheOperationRunning = false;
            ClearCacheButton.IsEnabled = true;
            await RefreshCacheStatusAsync();
        }
    }

    private int GetSelectedFrameRate()
    {
        if (FrameRateSelector.SelectedItem is ComboBoxItem item &&
            item.Tag is string text &&
            int.TryParse(text, out var frameRate))
        {
            return NormalizeFrameRate(frameRate);
        }

        return 30;
    }

    private string GetSelectedResolutionMode()
    {
        if (ResolutionSelector.SelectedItem is ComboBoxItem item &&
            item.Tag is string mode)
        {
            return NormalizeResolutionMode(mode);
        }

        return "Native";
    }

    private int GetSelectedBitrate()
    {
        if (BitrateSelector.SelectedItem is ComboBoxItem item &&
            item.Tag is string text &&
            int.TryParse(text, out var bitrate))
        {
            return NormalizeBitrate(bitrate);
        }

        return 16;
    }

    private int GetSelectedCacheBudgetMb()
    {
        if (CacheBudgetSelector.SelectedItem is ComboBoxItem item &&
            item.Tag is string text &&
            int.TryParse(text, out var budgetMb))
        {
            return CacheMaintenanceService.NormalizeBudgetMb(budgetMb);
        }

        return 4096;
    }

    private int GetSelectedOriginalClipRetentionDays()
    {
        if (OriginalClipRetentionDaysSelector.SelectedItem is ComboBoxItem item &&
            item.Tag is string text &&
            int.TryParse(text, out var retentionDays))
        {
            return OriginalClipRetentionPolicy.NormalizeRetentionDays(retentionDays);
        }

        return OriginalClipRetentionPolicy.DefaultRetentionDays;
    }

    private void MarkCaptureProfileCustom()
    {
        _settings.Current.CaptureProfileId = "custom";
        var wasSyncing = _syncingSettingsUi;
        _syncingSettingsUi = true;
        try { CaptureProfileSelector.SelectedIndex = 5; }
        finally { _syncingSettingsUi = wasSyncing; }
    }

    private int GetSelectedDrawerClipLengthSeconds()
    {
        if (DrawerClipLengthSelector.SelectedItem is ComboBoxItem item &&
            item.Tag is string text &&
            int.TryParse(text, out var seconds))
        {
            return NormalizeClipLength(seconds);
        }

        return GetSelectedClipLengthSeconds();
    }

    private void SetClipLengthSelection(int seconds)
    {
        var wasSyncing = _syncingSettingsUi;
        _syncingSettingsUi = true;
        try
        {
            var index = NormalizeClipLength(seconds) switch
            {
                15 => 0,
                60 => 2,
                120 => 3,
                _ => 1
            };

            ClipLengthSelector.SelectedIndex = index;
            DrawerClipLengthSelector.SelectedIndex = index;
        }
        finally
        {
            _syncingSettingsUi = wasSyncing;
        }
    }

    private static string NormalizeResolutionMode(string? mode)
    {
        return mode switch
        {
            "540p" => "540p",
            "720p" => "720p",
            "1080p" => "1080p",
            _ => "Native"
        };
    }

    private static int NormalizeFrameRate(int frameRate)
    {
        return frameRate switch
        {
            15 => 15,
            24 => 24,
            60 => 60,
            _ => 30
        };
    }

    private static int NormalizeBitrate(int bitrateMbps)
    {
        return bitrateMbps switch
        {
            5 => 5,
            8 => 8,
            24 => 24,
            35 => 35,
            _ => 16
        };
    }

    private static int NormalizeClipLength(int seconds)
    {
        return seconds switch
        {
            15 => 15,
            60 => 60,
            120 => 120,
            _ => 30
        };
    }

    private CaptureOptions GetConfiguredCaptureOptions()
    {
        return new CaptureOptions
        {
            FrameRate = NormalizeFrameRate(_settings.Current.CaptureFrameRate),
            ResolutionMode = NormalizeResolutionMode(_settings.Current.CaptureResolutionMode),
            BitrateMbps = NormalizeBitrate(_settings.Current.CaptureBitrateMbps),
            EncoderMode = _settings.Current.CaptureEncoderMode,
            HideCaptureBorder = _settings.Current.HideCaptureBorder,
            EnableExclusiveGameCapture = _settings.Current.EnableExclusiveGameCapture,
            CaptureSystemAudio = _settings.Current.CaptureSystemAudio,
            SystemAudioDeviceId = _settings.Current.SystemAudioDeviceId,
            CaptureMicrophone = _settings.Current.CaptureMicrophone,
            MicrophoneDeviceId = _settings.Current.MicrophoneDeviceId,
            SystemAudioGain = _settings.Current.SystemAudioGain,
            MicrophoneGain = _settings.Current.MicrophoneGain,
            AudioCaptureMode = _settings.Current.AudioCaptureMode,
            GameAudioProcessName = _settings.Current.GameAudioProcessName,
            CaptureDiscordAudio = _settings.Current.CaptureDiscordAudio,
            DiscordProcessName = _settings.Current.DiscordProcessName,
            DiscordAudioGain = _settings.Current.DiscordAudioGain,
            CaptureMusicAudio = _settings.Current.CaptureMusicAudio,
            MusicProcessName = _settings.Current.MusicProcessName,
            MusicAudioGain = _settings.Current.MusicAudioGain,
            AudioLimiterEnabled = _settings.Current.AudioLimiterEnabled,
            CaptureCursor = _settings.Current.CaptureCursor
        };
    }

    private CaptureOptions GetCaptureOptions()
    {
        var selected = _selectedCaptureTarget ?? throw new InvalidOperationException("Choose an available window or desktop display before starting capture.");
        if (AudioRoutingProblem() is { } audioProblem) throw new InvalidOperationException(audioProblem);
        var configured = GetConfiguredCaptureOptions();
        var resolved = selected.Target.Kind == Klipp.Capture.Models.CaptureTargetKind.Window
            ? _captureProfiles.ResolveForApplication(_settings.Current, configured, selected.ProcessName,
                new CaptureApplicationInfo(selected.ProcessName, selected.DisplayName, selected.WindowTitle))
            : _captureProfiles.Resolve(_settings.Current, configured);
        return resolved.Options with { Target = selected.Target, CaptureSystemAudio = configured.CaptureSystemAudio,
            CaptureCursor = configured.CaptureCursor };
    }

    private async void OnCacheTimerTick(object? sender, object e)
    {
        await RunAutomaticCacheCleanupAsync();
        await RunAutomaticOriginalClipRetentionAsync();
    }

    private async Task RunAutomaticCacheCleanupAsync()
    {
        if (_cacheOperationRunning || !_settings.Current.AutoCleanCache ||
            _recording.IsRecording || _clipMode.IsMonitoring ||
            EditorHost.Visibility == Visibility.Visible)
        {
            return;
        }

        _cacheOperationRunning = true;
        try
        {
            var budgetMb = CacheMaintenanceService.NormalizeBudgetMb(_settings.Current.CacheBudgetMb);
            await _cacheMaintenance.EnforceBudgetAsync(budgetMb * 1024L * 1024L);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Klipp] Automatisk cache-rensning misslyckades: {ex.Message}");
        }
        finally
        {
            _cacheOperationRunning = false;
            await RefreshCacheStatusAsync();
        }
    }

    private async Task RefreshCacheStatusAsync()
    {
        if (CacheUsageText is null)
            return;

        try
        {
            var snapshot = await _cacheMaintenance.InspectAsync();
            var budgetMb = CacheMaintenanceService.NormalizeBudgetMb(_settings.Current.CacheBudgetMb);
            var categories = string.Join(
                " · ",
                snapshot.Directories
                    .Where(directory => directory.SizeBytes > 0)
                    .Select(directory => $"{directory.Name} {FormatHealthBytes(directory.SizeBytes)}"));
            CacheUsageText.Text =
                $"{FormatHealthBytes(snapshot.TotalBytes)} of {FormatHealthBytes(budgetMb * 1024L * 1024L)}" +
                $" · {snapshot.FileCount} files" +
                (snapshot.TotalBytes > budgetMb * 1024L * 1024L
                    ? " · clears files when they are no longer in use"
                    : string.Empty) +
                (string.IsNullOrWhiteSpace(categories) ? string.Empty : $"\n{categories}");

            var work = MediaPreviewWorkQueue.Shared.GetSnapshot();
            var totalLookups = work.CacheHits + work.CacheMisses;
            var hitRate = totalLookups == 0
                ? "no cache requests yet"
                : $"{work.CacheHits * 100d / totalLookups:0}% cache hit rate";
            var workCategories = string.Join(
                " · ",
                work.Categories
                    .Where(category =>
                        category.CacheHits > 0 || category.CacheMisses > 0 ||
                        category.Completed > 0 || category.Queued > 0 || category.Running > 0)
                    .Select(category =>
                    {
                        var average = category.AverageDurationMilliseconds >= 1000
                            ? $"{category.AverageDurationMilliseconds / 1000:0.0}s"
                            : $"{category.AverageDurationMilliseconds:0}ms";
                        return $"{category.Category}: {category.Completed} complete, {average} average";
                    }));
            CacheWorkStatusText.Text =
                $"{work.Running} active · {work.Queued} queued " +
                $"({work.QueuedInteractive}/{work.QueuedVisible}/{work.QueuedBackground}) · {hitRate}" +
                (string.IsNullOrWhiteSpace(workCategories) ? string.Empty : $"\n{workCategories}") +
                (work.Failed > 0 ? $" · {work.Failed} failed" : string.Empty);

            using var process = Process.GetCurrentProcess();
            var gcInfo = GC.GetGCMemoryInfo();
            var uptime = DateTime.Now - process.StartTime;
            AppPerformanceText.Text =
                $"App memory {FormatHealthBytes(process.WorkingSet64)} · " +
                $"GC-heap {FormatHealthBytes(gcInfo.HeapSizeBytes)} · " +
                $"{process.Threads.Count} threads · running {FormatCompactDuration(uptime)}";
        }
        catch (Exception ex)
        {
            CacheUsageText.Text = $"Cache status unavailable · {ex.Message}";
            CacheWorkStatusText.Text = "Background task status is unavailable.";
            AppPerformanceText.Text = "Performance status is unavailable.";
        }
    }

    private async Task RunAutomaticOriginalClipRetentionAsync()
    {
        if (_retentionOperationRunning || !_settings.Current.AutoCleanOldOriginalClips ||
            _recording.IsRecording || _clipMode.IsMonitoring ||
            PlayerHost.Visibility == Visibility.Visible ||
            EditorHost.Visibility == Visibility.Visible)
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;
        if (_settings.Current.LastOriginalClipRetentionRunUtc is { } lastRun)
        {
            var lastRunUtc = lastRun.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(lastRun, DateTimeKind.Utc)
                : lastRun.ToUniversalTime();
            if (nowUtc - lastRunUtc < TimeSpan.FromHours(24))
                return;
        }

        if (await RunOriginalClipRetentionAsync(requireConfirmation: false))
        {
            _settings.Current.LastOriginalClipRetentionRunUtc = nowUtc;
            await _settings.SaveAsync();
        }
    }

    private async Task<bool> RunOriginalClipRetentionAsync(bool requireConfirmation)
    {
        if (_retentionOperationRunning)
            return false;

        if (_recording.IsRecording || _clipMode.IsMonitoring ||
            PlayerHost.Visibility == Visibility.Visible ||
            EditorHost.Visibility == Visibility.Visible)
        {
            if (requireConfirmation)
            {
                await ShowErrorAsync(
                    "Original clips are in use",
                    "Stop recording and instant clips, then close the player and editor before cleaning up old original clips.");
            }
            return false;
        }

        _retentionOperationRunning = true;
        UpdateOriginalClipRetentionControls();
        try
        {
            var retentionDays = GetSelectedOriginalClipRetentionDays();
            var plan = await _originalClipRetention.InspectAsync(
                LibraryService.Clips,
                retentionDays);
            var candidates = plan.Candidates;
            if (candidates.Count == 0)
            {
                OriginalClipRetentionStatusText.Text =
                    $"No unprotected original clips are older than {plan.RetentionDays} days.";
                return true;
            }

            if (requireConfirmation)
            {
                var dialog = AccessibleDialogFactory.Create(
                    this.Content.XamlRoot,
                    "Move old original clips to the Recycle Bin?",
                    $"{candidates.Count} clips ({FormatHealthBytes(plan.CandidateBytes)}) are older than " +
                    $"{plan.RetentionDays} days and can be moved to the Windows Recycle Bin. " +
                    "Favorites, project media, and the 20 newest clips are kept.",
                    primaryButtonText: "Move to Recycle Bin",
                    closeButtonText: "Cancel",
                    defaultButton: ContentDialogButton.Close,
                    automationHelpText: "Confirm moving old original clips to the Windows Recycle Bin.");
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return false;
            }

            var result = await _originalClipRetention.ExecuteAsync(plan);
            RemoveRetainedClipsFromLibrary(result.MovedPaths);

            var skipped = result.ChangedFiles + result.MissingFiles + result.FailedFiles;
            var summary = result.MovedPaths.Count == 0
                ? "No original clips were moved"
                : $"Moved {result.MovedPaths.Count} clips · freed {FormatHealthBytes(result.ReclaimedBytes)}";
            if (skipped > 0)
                summary += $" · skipped {skipped}";

            OriginalClipRetentionStatusText.Text = summary;
            StatusText.Text = summary;
            return true;
        }
        catch (Exception ex)
        {
            OriginalClipRetentionStatusText.Text = "Clip cleanup could not be completed.";
            if (requireConfirmation)
                await ShowErrorAsync("Could not clean up old original clips", ex.Message);
            else
                Debug.WriteLine($"[Klipp] Automatisk retention misslyckades: {ex.Message}");
            return false;
        }
        finally
        {
            _retentionOperationRunning = false;
            UpdateOriginalClipRetentionControls();
        }
    }

    private async Task RefreshOriginalClipRetentionStatusAsync()
    {
        if (OriginalClipRetentionStatusText is null || _retentionOperationRunning)
            return;

        try
        {
            var plan = await _originalClipRetention.InspectAsync(
                LibraryService.Clips,
                GetSelectedOriginalClipRetentionDays());
            if (plan.Decisions.Count == 0)
            {
                OriginalClipRetentionStatusText.Text = "The library has no original clips.";
                return;
            }

            var mode = _settings.Current.AutoCleanOldOriginalClips ? "Auto" : "Off";
            var candidateText = plan.Candidates.Count == 0
                ? $"no clips older than {plan.RetentionDays} days can be cleaned up"
                : $"{plan.Candidates.Count} clips · {FormatHealthBytes(plan.CandidateBytes)} can be cleaned up";
            var favoriteCount = plan.CountProtectedBy(ClipRetentionProtectionReason.Favorite);
            var projectCount = plan.CountProtectedBy(ClipRetentionProtectionReason.ProjectReference);
            OriginalClipRetentionStatusText.Text =
                $"{mode} · {candidateText}\n" +
                $"{favoriteCount} favorites · {projectCount} project files · newest " +
                $"{Math.Min(OriginalClipRetentionPolicy.DefaultMinimumLibrarySize, plan.Decisions.Count)} protected";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Klipp] Retention-status kunde inte läsas: {ex.Message}");
            OriginalClipRetentionStatusText.Text = "Clip cleanup status is unavailable.";
        }
    }

    private void RemoveRetainedClipsFromLibrary(IReadOnlyList<string> movedPaths)
    {
        if (movedPaths.Count == 0)
            return;

        var moved = movedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in moved)
            LibraryService.RemoveClipMetadata(path);

        _suppressLibraryFilter = true;
        try
        {
            for (var index = LibraryService.Clips.Count - 1; index >= 0; index--)
            {
                if (moved.Contains(LibraryService.Clips[index].FilePath))
                    LibraryService.Clips.RemoveAt(index);
            }
        }
        finally
        {
            _suppressLibraryFilter = false;
        }

        RefreshLibraryCollectionFilter();
        ApplyLibraryFilter();
        _editorView?.SetLibraryClips(LibraryService.Clips);
    }

    private void UpdateOriginalClipRetentionControls()
    {
        if (OriginalClipRetentionToggle is null ||
            OriginalClipRetentionDaysSelector is null ||
            RunOriginalClipRetentionButton is null)
        {
            return;
        }

        OriginalClipRetentionToggle.IsEnabled = !_retentionOperationRunning;
        OriginalClipRetentionDaysSelector.IsEnabled = !_retentionOperationRunning;
        var mediaBusy = _recording.IsRecording || _clipMode.IsMonitoring ||
                        PlayerHost.Visibility == Visibility.Visible ||
                        EditorHost.Visibility == Visibility.Visible;
        RunOriginalClipRetentionButton.IsEnabled =
            !_retentionOperationRunning && !mediaBusy && LibraryService.Clips.Count > 0;
    }

    private static string FormatCompactDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{duration.TotalDays:0.0}d";
        if (duration.TotalHours >= 1)
            return $"{duration.TotalHours:0.0}h";
        if (duration.TotalMinutes >= 1)
            return $"{duration.TotalMinutes:0}m";
        return $"{Math.Max(0, duration.TotalSeconds):0}s";
    }

    private CaptureStorageHealth GetCaptureStorageHealth()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return _captureHealth.GetStorageHealth(
            [
                LibraryService.ClipsDirectory,
                Path.Combine(localAppData, "Klipp", "segments"),
                Path.Combine(localAppData, "Klipp", "temp")
            ],
            NormalizeBitrate(_settings.Current.CaptureBitrateMbps));
    }

    private async Task<bool> EnsureCaptureStorageAvailableAsync(bool showDialog)
    {
        var storage = GetCaptureStorageHealth();
        if (storage.Severity != CaptureHealthSeverity.Critical)
            return true;

        var message = storage.FailureMessage ??
            $"Only {FormatHealthBytes(storage.AvailableBytes)} is free on {storage.DriveName}. " +
            "Free up disk space before starting capture.";
        StatusText.Text = "Capture stopped · not enough disk space";
        UpdateCaptureHealthUI();
        if (showDialog)
            await ShowErrorAsync("Cannot start capture", message);
        return false;
    }

    // === Record/Monitor-knappen ===

    private async void OnRecordClicked(object sender, RoutedEventArgs e)
    {
        if (!_captureSessionReady || !await _captureOperationGate.WaitAsync(0)) return;
        try
        {
            UpdateStatusUI();
            if (_mode == AppMode.Recording)
                await HandleRecordingModeButtonAsync();
            else
                await HandleClipModeButtonAsync();
        }
        finally
        {
            _captureOperationGate.Release();
            UpdateStatusUI();
        }
    }

    private async Task HandleRecordingModeButtonAsync()
    {
        RecordButton.IsEnabled = false;
        RecordingModeButton.IsEnabled = false;
        ClipModeButton.IsEnabled = false;

        try
        {
            if (_recording.IsRecording)
            {
                var recordedSeconds = _recording.RecordedSeconds;
                StatusText.Text = "Saving…";
                var outputPath = await RunServiceOperationAsync(_recording.StopAsync);
                _statusTimer.Stop();

                if (outputPath is not null)
                    LibraryService.AddNewClip(outputPath);
                NotifyRecordingStopped(outputPath, recordedSeconds);
            }
            else
            {
                if (!await PrepareCaptureTargetForStartAsync()) return;
                if (!await EnsureCaptureStorageAvailableAsync(showDialog: true))
                    return;

                if (!_recording.IsFFmpegInstalled)
                {
                    StatusText.Text = "Downloading FFmpeg…";
                    await _recording.EnsureFFmpegInstalledAsync();
                }

                LibraryService.PauseBackgroundMetadata();
                var outputPath = LibraryService.GenerateClipPath();
                var captureOptions = GetCaptureOptions();
                _activeCaptureOptions = captureOptions;
                await RunServiceOperationAsync(() => _recording.StartAsync(outputPath, captureOptions));
                _statusTimer.Start();
                ShowCaptureNotification(CaptureNotificationKind.RecordingStarted, "Recording started",
                    $"Capturing {CaptureSourceDisplayName} · {captureOptions.ResolutionMode} · {captureOptions.FrameRate} fps");
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Recording failed", ex.Message);
        }
        finally
        {
            RecordButton.IsEnabled = true;
            RecordingModeButton.IsEnabled = true;
            ClipModeButton.IsEnabled = true;
            if (!_recording.IsRecording && !_clipMode.IsMonitoring)
                LibraryService.ResumeBackgroundMetadata();
            UpdateStatusUI();
            await RefreshRecordingRecoveryAsync();
        }
    }

    private async Task HandleClipModeButtonAsync()
    {
        RecordButton.IsEnabled = false;
        RecordingModeButton.IsEnabled = false;
        ClipModeButton.IsEnabled = false;

        try
        {
            if (_clipMode.IsMonitoring || _instantCaptureRequested)
            {
                _instantCaptureRequested = false;
                StatusText.Text = "Stopping…";
                await RunServiceOperationAsync(() => _clipMode.StopMonitoringAsync());
                _statusTimer.Stop();
            }
            else
            {
                _instantCaptureRequested = true;
                if (!await EnsureCaptureStorageAvailableAsync(showDialog: true))
                {
                    _instantCaptureRequested = false;
                    return;
                }

                if (!_clipMode.IsFFmpegInstalled)
                {
                    StatusText.Text = "Downloading FFmpeg…";
                    await _clipMode.EnsureFFmpegInstalledAsync();
                }

                if (!await PrepareCaptureTargetForStartAsync()) return;

                LibraryService.PauseBackgroundMetadata();
                var captureOptions = GetCaptureOptions();
                _activeCaptureOptions = captureOptions;
                await RunServiceOperationAsync(() => _clipMode.StartMonitoringAsync(captureOptions));
                _statusTimer.Start();
            }
        }
        catch (Exception ex)
        {
            _instantCaptureRequested = false;
            await ShowErrorAsync("Instant clips failed", ex.Message);
        }
        finally
        {
            RecordButton.IsEnabled = true;
            RecordingModeButton.IsEnabled = true;
            ClipModeButton.IsEnabled = true;
            if (!_recording.IsRecording && !_clipMode.IsMonitoring)
                LibraryService.ResumeBackgroundMetadata();
            UpdateStatusUI();
        }
    }

    // === Spara klipp-knappen (bara aktiv i Klipp-läge under övervakning) ===

    private async void OnSaveClipClicked(object sender, RoutedEventArgs e)
    {
        if (_mode != AppMode.Clip || !_clipMode.IsMonitoring || !await _captureOperationGate.WaitAsync(0))
            return;

        try
        {
            if (!await EnsureCaptureStorageAvailableAsync(showDialog: true)) return;
            SaveClipButton.IsEnabled = false;
            var seconds = GetSelectedClipLengthSeconds();
            var outputPath = LibraryService.GenerateClipPath();

            // ClipExtractor klipper det som finns — om vi bara övervakat 3s
            // får vi ett 3s-klipp, inte ett fel.
            var result = await RunServiceOperationAsync(() => _clipMode.SaveClipAsync(outputPath, seconds));
            LibraryService.AddNewClip(result.OutputPath);
            var savedDuration = await _mediaProbe.GetDurationAsync(result.OutputPath);
            ShowCaptureNotification(CaptureNotificationKind.ClipSaved, "Clip saved",
                $"{FormatCompactDuration(savedDuration ?? TimeSpan.FromSeconds(Math.Min(seconds, result.ApproximateDurationSeconds)))} · {Path.GetFileName(result.OutputPath)}");
        }
        catch (InvalidOperationException)
        {
            // Inga segment alls än (övervakning precis startad) — visa kort i statusraden
            // istället för en störande popup.
            StatusText.Text = "No buffered video yet";
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not save clip", ex.Message);
        }
        finally
        {
            _captureOperationGate.Release();
            SaveClipButton.IsEnabled = _clipMode.IsMonitoring;
            UpdateStatusUI();
        }
    }

    // === Klippbibliotek + spelare ===

    private void OnClipCardClicked(object sender, ClipCardClickEventArgs e)
    {
        if (string.IsNullOrEmpty(e.FilePath) || !File.Exists(e.FilePath))
            return;

        ShowPlayer(e.FilePath, e.Position, e.AutoPlay, e.IsMuted, e.Volume);
    }

    private void OnClipCardOpenFolderRequested(object sender, ClipCardClickEventArgs e)
    {
        OpenClipInExplorer(e.FilePath);
    }

    private async void OnClipCardEditRequested(object sender, ClipCardClickEventArgs e)
    {
        try
        {
            await ShowEditorAsync(e.FilePath);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not open editor", ex.Message);
            ShowLibrary();
        }
    }

    private void OnClipCardFavoriteRequested(object sender, ClipCardClickEventArgs e)
    {
        try
        {
            LibraryService.ToggleClipFavorite(e.FilePath);
            RefreshLibraryAfterMetadataChange();
            StatusText.Text = "Favorite updated";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Klipp] Kunde inte uppdatera favorit: {ex.Message}");
        }
    }

    private async void OnClipCardOrganizeRequested(object sender, ClipCardClickEventArgs e)
    {
        await ShowOrganizeClipDialogAsync(e.FilePath);
    }

    private void OnEditorLibraryFavoriteToggleRequested(object? sender, EditorLibraryItemEventArgs e)
    {
        try
        {
            LibraryService.ToggleClipFavorite(e.FilePath);
            RefreshLibraryAfterMetadataChange();
            StatusText.Text = "Favorite updated";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Klipp] Kunde inte uppdatera favorit: {ex.Message}");
        }
    }

    private async void OnEditorLibraryOrganizeRequested(object? sender, EditorLibraryItemEventArgs e)
    {
        await ShowOrganizeClipDialogAsync(e.FilePath);
    }

    private async void OnClipCardRenameRequested(object sender, ClipCardClickEventArgs e)
    {
        await RenameClipAsync(e.FilePath, reloadPlayer: false);
    }

    private async void OnClipCardDeleteRequested(object sender, ClipCardClickEventArgs e)
    {
        await DeleteClipAsync(e.FilePath, returnToLibrary: false);
    }

    private void OnPlayerBackRequested(object? sender, EventArgs e)
    {
        ShowLibrary();
    }

    private async void OnPlayerDeleteRequested(object? sender, ClipDeleteEventArgs e)
    {
        await DeleteClipAsync(e.FilePath, returnToLibrary: true);
    }

    private async void OnPlayerRenameRequested(object? sender, ClipRenameEventArgs e)
    {
        await RenameClipAsync(e.FilePath, reloadPlayer: true);
    }

    private async Task RenameClipAsync(string filePath, bool reloadPlayer)
    {
        if (_updateRestarting) return;
        using var updateWork = BeginUpdateBlockingOperation();
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return;
        ClipCard.StopActivePreview(filePath);

        var currentTitle = Path.GetFileNameWithoutExtension(filePath);
        if (currentTitle.StartsWith("klipp_", StringComparison.OrdinalIgnoreCase))
            currentTitle = currentTitle["klipp_".Length..];

        var newTitle = await ShowRenameDialogAsync(currentTitle);
        if (newTitle is null)
            return;

        try
        {
            if (reloadPlayer)
            {
                _playerView?.Stop();
                await Task.Delay(150);
            }

            var newPath = LibraryService.RenameClip(filePath, newTitle);
            RefreshLibraryCollectionFilter();
            ApplyLibraryFilter();
            _editorView?.SetLibraryClips(LibraryService.Clips);

            if (reloadPlayer)
            {
                PlayerView.LoadClip(newPath);
                UpdatePlayerNavigationButtons(newPath);
            }

            StatusText.Text = "Clip renamed";
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not rename clip", ex.Message);
        }
    }

    private async Task DeleteClipAsync(string filePath, bool returnToLibrary)
    {
        if (_updateRestarting) return;
        using var updateWork = BeginUpdateBlockingOperation();
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return;
        ClipCard.StopActivePreview(filePath);

        var confirm = await ShowConfirmAsync(
            "Move clip to the Recycle Bin?",
            $"'{Path.GetFileName(filePath)}' can be restored from the Windows Recycle Bin.");

        if (!confirm) return;

        try
        {
            await _recycleBin.MoveFileAsync(filePath);
            LibraryService.RemoveClipMetadata(filePath);

            for (int i = LibraryService.Clips.Count - 1; i >= 0; i--)
            {
                if (string.Equals(LibraryService.Clips[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                {
                    LibraryService.Clips.RemoveAt(i);
                    break;
                }
            }

            RefreshLibraryCollectionFilter();
            ApplyLibraryFilter();
            _editorView?.SetLibraryClips(LibraryService.Clips);

            if (returnToLibrary)
                ShowLibrary();

            StatusText.Text = "Clip moved to the Recycle Bin";
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not move clip to the Recycle Bin", ex.Message);
        }
    }

    private void OpenClipInExplorer(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return;

        Process.Start("explorer.exe", $"/select,\"{filePath}\"");
    }

    private async void OnPlayerTrimRequested(object? sender, ClipTrimEventArgs e)
    {
        try
        {
            await ShowEditorAsync(e.FilePath);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not open editor", ex.Message);
            ShowLibrary();
        }
    }

    private async Task ExportTrimAsync(
        string filePath,
        TimeSpan start,
        TimeSpan end,
        ClipEditOptions options)
    {
        if (_updateRestarting) return;
        using var updateWork = BeginUpdateBlockingOperation();
        try
        {
            var outputPath = LibraryService.GenerateEditedClipPath(filePath, options.RequiresReencode ? "edit" : "trim");
            StatusText.Text = options.RequiresReencode ? "Exporting edit…" : "Exporting trim…";

            await RunServiceOperationAsync(() =>
                _clipEditor.TrimAsync(filePath, outputPath, start, end, options));

            LibraryService.AddNewClip(outputPath);
            ApplyLibraryFilter();

            ShowPlayer(outputPath);
            StatusText.Text = options.RequiresReencode ? "Edit saved" : "Trimmed clip saved";
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not export clip", ex.Message);
            UpdateStatusUI();
        }
    }

    private void OnEditorBackRequested(object? sender, EventArgs e)
    {
        ShowLibrary();
    }

    private async void OnEditorExportRequested(object? sender, ClipEditorExportEventArgs e)
    {
        var options = new ClipEditOptions(
            e.PlaybackSpeed,
            e.AudioVolume,
            e.MuteAudio,
            e.VideoFilter,
            e.TextOverlay,
            e.TextStyle,
            e.TextPosition,
            e.ElementOverlay,
            e.FadeInSeconds,
            e.FadeOutSeconds,
            e.FreezeFrameSeconds,
            e.SubtitleText,
            e.TextEnabled,
            e.TextStart,
            e.TextEnd,
            e.VfxEnabled,
            e.VfxStart,
            e.VfxEnd,
            e.SubtitleEnabled,
            e.SubtitleStart,
            e.SubtitleEnd,
            e.ElementEnabled,
            e.ElementStart,
            e.ElementEnd,
            e.TextAnchorX,
            e.TextAnchorY,
            e.TextSize,
            e.SubtitleStyle,
            e.SubtitleAnchorX,
            e.SubtitleAnchorY,
            e.SubtitleSize,
            e.VideoScale,
            e.VideoOffsetX,
            e.VideoOffsetY,
            true,
            e.TextAnimation,
            e.TextBoxWidth,
            e.SubtitleBoxWidth,
            textFontFamily: e.TextFontFamily,
            textColor: e.TextColor,
            subtitleFontFamily: e.SubtitleFontFamily,
            subtitleColor: e.SubtitleColor);
        await ExportTrimAsync(e.FilePath, e.Start, e.End, options);
    }

    private async void OnEditorProjectExportRequested(object? sender, EditorProjectExportEventArgs e)
    {
        if (_updateRestarting) return;
        using var updateWork = BeginUpdateBlockingOperation();
        var sourcePath = e.Project.PrimaryClips
            .Select(clip => clip.SourcePath)
            .FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            await ShowErrorAsync(
                "Could not export project",
                "Project media is missing. Open the project and relink its files before exporting.");
            return;
        }

        try
        {
            var outputPath = LibraryService.GenerateEditedClipPath(sourcePath, "project");
            var job = await _exportQueue.EnqueueAsync(e.Project, outputPath);
            _editorView?.SetExportJobs(_exportQueue.Jobs);
            StatusText.Text = $"Export queued: {job.ProjectName}";
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not export project", ex.Message);
            UpdateStatusUI();
        }
    }

    private void OnExportQueueJobsChanged(object? sender, EventArgs e)
    {
        var jobs = _exportQueue.Jobs;
        DispatcherQueue.TryEnqueue(() =>
        {
            _editorView?.SetExportJobs(jobs);
            RefreshUpdateUi();
        });
    }

    private void OnExportQueueJobCompleted(object? sender, EditorExportJobEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (!File.Exists(e.Job.OutputPath))
                    return;

                LibraryService.AddNewClip(e.Job.OutputPath);
                ApplyLibraryFilter();
                _editorView?.SetLibraryClips(LibraryService.Clips);
                _editorView?.SetExportJobs(_exportQueue.Jobs);
                StatusText.Text = $"Export complete: {e.Job.ProjectName}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Klipp] Could not add completed export to library: {ex.Message}");
            }
        });
    }

    private async void OnEditorExportJobActionRequested(object? sender, EditorExportJobActionEventArgs e)
    {
        if (_updateRestarting) return;
        using var updateWork = BeginUpdateBlockingOperation();
        try
        {
            switch (e.Action)
            {
                case EditorExportJobAction.Cancel:
                    if (await _exportQueue.CancelAsync(e.JobId))
                        StatusText.Text = "Cancelling export...";
                    break;
                case EditorExportJobAction.Retry:
                    if (await _exportQueue.RetryAsync(e.JobId))
                        StatusText.Text = "Export queued again";
                    break;
                case EditorExportJobAction.Open:
                    var job = _exportQueue.Jobs.FirstOrDefault(item =>
                        string.Equals(item.Id, e.JobId, StringComparison.Ordinal));
                    if (job?.CanOpen == true)
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = $"/select,\"{job.OutputPath}\"",
                            UseShellExecute = true
                        });
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Export queue", ex.Message);
        }
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        DisposeUpdates();
        DisposeTrayIcon();
        _inlineWindowClosing = true;
        ExitInlineFullscreen(restoreWindow: false);
        ClipCard.StopActivePreview();
        StopCaptureSourceMonitoring();
        CloseCaptureNotifications();
        DisposeSettingsAudioDevices();
        StopSettingsShortcutCapture();
        StopHotkeyCapture();
        StopBookmarkHotkeyCapture();
        StopEditorShortcutCapture();
        _statusTimer.Stop();
        _healthTimer.Stop();
        _cacheTimer.Stop();
        _captureAudioMeterTimer?.Stop();
        _exportQueue.Dispose();
    }

    private void ApplyAccessibilityTheme()
    {
        var highContrast = false;
        try
        {
            highContrast = _accessibilitySettings.HighContrast;
        }
        catch (COMException)
        {
            // Some unpackaged WinUI hosts do not expose AccessibilitySettings events/state.
        }

        var requestedTheme = highContrast
            ? ElementTheme.Default
            : ElementTheme.Dark;
        if (AppRoot.RequestedTheme != requestedTheme)
            AppRoot.RequestedTheme = requestedTheme;

        if (_appWindow is not null && _titleBarHighContrast != highContrast)
        {
            _titleBarHighContrast = highContrast;
            var titleBar = _appWindow.TitleBar;
            titleBar.BackgroundColor = highContrast ? null : Windows.UI.Color.FromArgb(255, 28, 31, 35);
            titleBar.ForegroundColor = highContrast ? null : Windows.UI.Color.FromArgb(255, 230, 232, 235);
            titleBar.InactiveBackgroundColor = highContrast ? null : Windows.UI.Color.FromArgb(255, 28, 31, 35);
            titleBar.InactiveForegroundColor = highContrast ? null : Windows.UI.Color.FromArgb(255, 146, 154, 164);
            titleBar.ButtonBackgroundColor = titleBar.BackgroundColor;
            titleBar.ButtonForegroundColor = titleBar.ForegroundColor;
            titleBar.ButtonInactiveBackgroundColor = titleBar.InactiveBackgroundColor;
            titleBar.ButtonInactiveForegroundColor = titleBar.InactiveForegroundColor;
            titleBar.ButtonHoverBackgroundColor = highContrast ? null : Windows.UI.Color.FromArgb(255, 52, 58, 66);
            titleBar.ButtonHoverForegroundColor = titleBar.ForegroundColor;
            titleBar.ButtonPressedBackgroundColor = highContrast ? null : Windows.UI.Color.FromArgb(255, 41, 45, 50);
            titleBar.ButtonPressedForegroundColor = titleBar.ForegroundColor;
        }
    }

    private async void OnEditorLibraryImportRequested(object? sender, EventArgs e)
    {
        if (_updateRestarting) return;
        using var updateWork = BeginUpdateBlockingOperation();
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.VideosLibrary
            };
            foreach (var extension in new[]
                     {
                         ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".ts", ".mts", ".m2ts"
                     })
            {
                picker.FileTypeFilter.Add(extension);
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var files = await picker.PickMultipleFilesAsync();
            if (files.Count == 0)
                return;

            var results = new List<ClipImportResult>();
            var failures = new List<string>();
            for (var index = 0; index < files.Count; index++)
            {
                var itemNumber = index + 1;
                var progress = new Progress<ClipImportProgress>(value =>
                {
                    var percent = value.Fraction is { } fraction
                        ? $" {Math.Round(fraction * 100):0}%"
                        : string.Empty;
                    var status = files.Count == 1
                        ? $"{value.Stage}{percent}"
                        : $"{itemNumber}/{files.Count}: {value.Stage}{percent}";
                    StatusText.Text = status;
                    EditorView.SetMediaImportStatus(status);
                });
                try
                {
                    results.Add(await LibraryService.ImportClipWithDetailsAsync(files[index].Path, progress));
                }
                catch (Exception ex)
                {
                    failures.Add($"{files[index].Name}: {ex.Message}");
                }
            }

            if (results.Count == 0)
                throw new InvalidOperationException(string.Join(Environment.NewLine, failures));

            ApplyLibraryFilter();
            EditorView.SetLibraryClips(LibraryService.Clips);
            if (failures.Count > 0)
            {
                var status = $"{results.Count} complete · {failures.Count} failed";
                StatusText.Text = status;
                EditorView.SetMediaImportStatus(status);
                await ShowErrorAsync(
                    $"{failures.Count} clips could not be imported",
                    string.Join(Environment.NewLine, failures.Take(5)));
            }
            else if (results.Count == 1)
            {
                StatusText.Text = results[0].StatusText;
                EditorView.SetMediaImportStatus(results[0].StatusText);
            }
            else
            {
                var duplicates = results.Count(result => result.WasDuplicate);
                var converted = results.Count(result => result.Kind == ClipImportKind.Transcoded);
                var status = duplicates > 0 || converted > 0
                    ? $"{results.Count} clips ready · {duplicates} duplicates · {converted} converted"
                    : $"{results.Count} clips imported";
                StatusText.Text = status;
                EditorView.SetMediaImportStatus(status);
            }
        }
        catch (Exception ex)
        {
            _editorView?.SetMediaImportStatus("Import failed");
            await ShowErrorAsync("Could not import clips", ex.Message);
            UpdateStatusUI();
        }
    }

    private async void OnEditorMissingMediaRelinkRequested(object? sender, MissingMediaRelinkEventArgs e)
    {
        if (_updateRestarting) return;
        using var updateWork = BeginUpdateBlockingOperation();
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = e.IsGraphic
                    ? PickerLocationId.PicturesLibrary
                    : e.IsAudio
                    ? PickerLocationId.MusicLibrary
                    : PickerLocationId.VideosLibrary
            };

            var extensions = e.IsGraphic
                ? new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }
                : e.IsAudio
                ? new[] { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".mp4" }
                : new[] { ".mp4", ".mov", ".mkv", ".webm", ".avi" };
            foreach (var extension in extensions)
                picker.FileTypeFilter.Add(extension);

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            await EditorView.RelinkMissingMediaAsync(e.OriginalPath, file.Path, e.IsAudio, e.IsGraphic);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not relink media", ex.Message);
        }
    }

    private async void OnEditorGraphicImportRequested(object? sender, EventArgs e)
    {
        if (_updateRestarting) return;
        using var updateWork = BeginUpdateBlockingOperation();
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" })
                picker.FileTypeFilter.Add(extension);

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            await EditorView.ImportGraphicLayerAsync(file.Path);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not import graphics", ex.Message);
        }
    }

    private async void OnEditorBrandLogoImportRequested(object? sender, EventArgs e)
    {
        if (_updateRestarting) return;
        using var updateWork = BeginUpdateBlockingOperation();
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" })
                picker.FileTypeFilter.Add(extension);

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            await EditorView.ImportBrandLogoAsync(file.Path);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not import logo", ex.Message);
        }
    }

    private async void OnEditorBrandFontImportRequested(object? sender, EventArgs e)
    {
        if (_updateRestarting) return;
        using var updateWork = BeginUpdateBlockingOperation();
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add(".ttf");
            picker.FileTypeFilter.Add(".otf");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            await EditorView.ImportBrandFontAsync(file.Path);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not import font", ex.Message);
        }
    }

    private async void OnEditorLutImportRequested(object? sender, EventArgs e)
    {
        if (_updateRestarting) return;
        using var updateWork = BeginUpdateBlockingOperation();
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add(".cube");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            await EditorView.ApplyLutToSelectedClipAsync(file.Path);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not import LUT", ex.Message);
        }
    }

    private async void OnEditorSubtitleSrtImportRequested(object? sender, EventArgs e)
    {
        if (_updateRestarting) return;
        using var updateWork = BeginUpdateBlockingOperation();
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add(".srt");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            await EditorView.ImportSubtitlesFromSrtAsync(file.Path);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not import subtitles", ex.Message);
        }
    }

    private async void OnEditorAudioTrackImportRequested(object? sender, EventArgs e)
    {
        if (_updateRestarting) return;
        using var updateWork = BeginUpdateBlockingOperation();
        try
        {
            var picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.MusicLibrary
            };
            foreach (var extension in new[] { ".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".mp4" })
                picker.FileTypeFilter.Add(extension);

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            await EditorView.ImportAudioTrackAsync(file.Path);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not import audio", ex.Message);
        }
    }

    private async void OnEditorSubtitleSrtExportRequested(object? sender, EventArgs e)
    {
        if (_updateRestarting) return;
        using var updateWork = BeginUpdateBlockingOperation();
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "klipp-subtitles"
            };
            picker.FileTypeChoices.Add("SubRip subtitles", new[] { ".srt" });

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSaveFileAsync();
            if (file is null)
                return;

            await EditorView.ExportSubtitlesToSrtAsync(file.Path);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not export subtitles", ex.Message);
        }
    }

    private void ShowPlayer(string filePath, TimeSpan? startPosition = null, bool autoPlay = true,
        bool? isMuted = null, double? volume = null)
    {
        ClipCard.StopActivePreview();
        HideSideMenu();
        TopBar.Visibility = Visibility.Collapsed;
        StopEditorIfCreated();
        SetViewVisibility(EditorHost, false);
        SetViewVisibility(LibraryView, false);
        SetViewVisibility(PlayerHost, true);
        PlayerView.LoadClip(filePath, startPosition, autoPlay, isMuted, volume);
        UpdatePlayerNavigationButtons(filePath);
    }

    private async Task ShowEditorAsync(string filePath)
    {
        if (_updateRestarting) return;
        using var updateWork = BeginUpdateBlockingOperation();
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return;
        ClipCard.StopActivePreview();

        try
        {
            var duration = await _mediaProbe.GetDurationAsync(filePath);
            if (_updateRestarting) return;
            if (duration is null || duration.Value <= TimeSpan.FromSeconds(0.25))
            {
                await ShowErrorAsync("Could not open editor", "The clip duration could not be read.");
                return;
            }

            HideSideMenu();
            TopBar.Visibility = Visibility.Collapsed;
            StopPlayerIfCreated();
            SetViewVisibility(PlayerHost, false);
            SetViewVisibility(LibraryView, false);

            EditorHost.Visibility = Visibility.Visible;
            EditorHost.IsHitTestVisible = true;
            EditorHost.Opacity = 0;
            await EditorView.LoadClipAsync(filePath, duration.Value);
            EditorView.SetLibraryClips(LibraryService.Clips);
            SetViewVisibility(EditorHost, true);
        }
        catch
        {
            StopEditorIfCreated();
            SetViewVisibility(EditorHost, false);
            SetViewVisibility(LibraryView, true);
            TopBar.Visibility = Visibility.Visible;
            throw;
        }
    }

    private void ShowLibrary()
    {
        HideSideMenu();
        SetPlayerFullscreen(false);
        TopBar.Visibility = Visibility.Visible;
        StopPlayerIfCreated();
        StopEditorIfCreated();

        SetViewVisibility(PlayerHost, false);
        SetViewVisibility(EditorHost, false);
        SetViewVisibility(LibraryView, true);
    }

    private static void SetViewVisibility(UIElement element, bool visible)
    {
        // Immediate state changes keep keyboard navigation responsive and prevent
        // an older transition callback from hiding a view opened more recently.
        element.Opacity = 1;
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        element.IsHitTestVisible = visible;
    }

    private void OnPlayerFullscreenRequested(object? sender, PlayerFullscreenRequestEventArgs e)
    {
        SetPlayerFullscreen(e.IsFullscreen ?? !_playerFullscreen);
    }

    private void SetPlayerFullscreen(bool fullscreen)
    {
        if (_playerFullscreen == fullscreen)
        {
            _playerView?.SetHostFullscreenState(fullscreen);
            return;
        }

        try
        {
            _appWindow?.SetPresenter(
                fullscreen
                    ? AppWindowPresenterKind.FullScreen
                    : AppWindowPresenterKind.Default);

            _playerFullscreen = fullscreen;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Klipp] Kunde inte ändra fullscreen-läge: {ex.Message}");
        }
        finally
        {
            _playerView?.SetHostFullscreenState(_playerFullscreen);
        }
    }

    private void UpdatePlayerNavigationButtons(string currentFilePath)
    {
        var clips = GetNavigationClips(currentFilePath);
        var index = FindClipIndex(clips, currentFilePath);
        PlayerView.CanGoPrev = index > 0;
        PlayerView.CanGoNext = index >= 0 && index < clips.Count - 1;
    }

    private ObservableCollection<ClipViewModel> GetNavigationClips(string currentFilePath)
    {
        return VisibleClips.Any(clip => string.Equals(clip.FilePath, currentFilePath, StringComparison.OrdinalIgnoreCase))
            ? VisibleClips
            : LibraryService.Clips;
    }

    private static int FindClipIndex(ObservableCollection<ClipViewModel> clips, string filePath)
    {
        for (int i = 0; i < clips.Count; i++)
        {
            if (string.Equals(clips[i].FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private void OnPlayerPrevRequested(object? sender, EventArgs e)
    {
        NavigateToClip(-1);
    }

    private void OnPlayerNextRequested(object? sender, EventArgs e)
    {
        NavigateToClip(+1);
    }

    private void NavigateToClip(int offset)
    {
        var currentPath = _playerView?.CurrentFilePath;
        if (string.IsNullOrEmpty(currentPath)) return;

        var clips = GetNavigationClips(currentPath);
        var currentIndex = FindClipIndex(clips, currentPath);
        if (currentIndex < 0) return;

        var newIndex = currentIndex + offset;
        if (newIndex < 0 || newIndex >= clips.Count) return;

        var newClip = clips[newIndex];
        PlayerView.LoadClip(newClip.FilePath);
        UpdatePlayerNavigationButtons(newClip.FilePath);
    }

    // === Status-UI ===

    private void UpdateStatusUI()
    {
        RefreshUpdateUi();
        UpdateRecordingTools();
        if (_mode == AppMode.Recording)
        {
            UpdateRecordingModeStatus();
        }
        else
        {
            UpdateClipModeStatus();
        }

        UpdateModeChrome();
        UpdateStatusPulse();
        UpdateCaptureHealthUI();
        UpdateOriginalClipRetentionControls();

        UpdateCaptureSourceToolbar();

        UpdateTrayStatus();

        if (_isSwitchingMode)
        {
            RecordingModeButton.IsEnabled = false;
            ClipModeButton.IsEnabled = false;
            RecordButton.IsEnabled = false;
            SaveClipButton.IsEnabled = false;
        }
    }

    private void UpdateCaptureHealthUI()
    {
        if (CaptureHealthButton is null)
            return;

        var recordingHealth = _recording.GetHealthSnapshot();
        var clipHealth = _clipMode.GetHealthSnapshot();
        var runtime = recordingHealth.Active
            ? recordingHealth
            : clipHealth.Active
                ? clipHealth
                : _mode == AppMode.Recording
                    ? recordingHealth
                    : clipHealth;
        var storage = GetCaptureStorageHealth();
        var severity = (CaptureHealthSeverity)Math.Max((int)runtime.Severity, (int)storage.Severity);
        var healthBrush = (Brush)Application.Current.Resources[severity switch
        {
            CaptureHealthSeverity.Critical => "KlippDangerBrush",
            CaptureHealthSeverity.Warning => "KlippWarningBrush",
            _ => "KlippSuccessBrush"
        }];
        CaptureHealthSummaryDot.Fill = healthBrush;
        CaptureHealthButton.BorderBrush = severity == CaptureHealthSeverity.Healthy
            ? (Brush)Application.Current.Resources["KlippBorderBrush"]
            : healthBrush;

        CaptureHealthSummaryText.Text = storage.FailureMessage is null
            ? runtime.Active
                ? $"{runtime.MeasuredFrameRate:0} fps · {FormatHealthBytes(storage.AvailableBytes)}"
                : $"{FormatHealthBytes(storage.AvailableBytes)} free"
            : "Disk error";

        if (CaptureHealthDetailDot is null || CaptureHealthStateText is null ||
            CaptureHealthFramesText is null || CaptureHealthEncoderText is null ||
            CaptureHealthAudioText is null || CaptureHealthStorageText is null ||
            CaptureHealthWarningText is null || CaptureHealthWarningBorder is null)
        {
            return;
        }

        CaptureHealthDetailDot.Fill = healthBrush;

        CaptureHealthStateText.Text = runtime.Active
            ? runtime.CaptureStalled
                ? "No new frame for at least 3 seconds"
                : $"Active · {runtime.FrameWidth}×{runtime.FrameHeight} · target {runtime.TargetFrameRate} fps"
            : "Paused";
        CaptureHealthFramesText.Text = runtime.Active
            ? $"{runtime.CapturedFrames:N0} captured · {runtime.DroppedFrames + runtime.InvalidFrames:N0} dropped ({runtime.DropRate:P1})"
            : "-";
        CaptureHealthEncoderText.Text = runtime.Active
            ? $"{runtime.EncoderName} · {(runtime.EncoderRunning ? "running" : "waiting")} · {runtime.EncodedFrames:N0} frames"
            : runtime.EncoderName;
        CaptureHealthAudioText.Text = !runtime.Active
            ? "Checked when capture starts"
            : _activeCaptureOptions is { CaptureSystemAudio: false, CaptureMicrophone: false, CaptureDiscordAudio: false, CaptureMusicAudio: false }
                ? "Audio off"
            : !runtime.AudioConfigured
                ? "Unavailable · video continues"
                : runtime.AudioSignal
                    ? "Audio active"
                    : "Waiting for audio";
        CaptureHealthStorageText.Text = storage.FailureMessage is null
            ? $"{FormatHealthBytes(storage.AvailableBytes)} free on {storage.DriveName} · about {FormatHealthDuration(storage.EstimatedSecondsRemaining)}"
            : storage.FailureMessage;

        var encoderFallback = recordingHealth.Active ? _recording.EncoderFallbackReason : _clipMode.EncoderFallbackReason;
        if (runtime.Active) SettingsEncoderStatusText.Text = runtime.EncoderName +
            (string.IsNullOrWhiteSpace(encoderFallback) ? string.Empty : $" · {encoderFallback}");

        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(storage.FailureMessage))
            warnings.Add(storage.FailureMessage);
        else if (storage.Severity == CaptureHealthSeverity.Critical)
            warnings.Add("Critically low disk space. Stop capture or free up space now.");
        else if (storage.Severity == CaptureHealthSeverity.Warning)
            warnings.Add("Disk space is running low.");
        if (!string.IsNullOrWhiteSpace(runtime.FailureMessage))
            warnings.Add(runtime.FailureMessage);
        if (runtime.CaptureStalled)
            warnings.Add("Capture has stalled and is not producing new frames.");
        else if (runtime.Active && runtime.ActiveSeconds > 3 && !runtime.EncoderRunning)
            warnings.Add("The FFmpeg encoder is not responding.");
        if (runtime.Active && runtime.ActiveSeconds > 5 && runtime.DropRate >= 0.05)
            warnings.Add($"{runtime.DropRate:P1} of capture frames are being dropped. Lower the frame rate or resolution.");
        if (runtime.Active && runtime.ActiveSeconds > 5 && runtime.AudioConfigured && !runtime.AudioSignal)
            warnings.Add("No audio signal has been detected from the selected devices.");

        CaptureHealthWarningText.Text = string.Join(Environment.NewLine, warnings.Distinct());
        CaptureHealthWarningBorder.BorderBrush = healthBrush;
        CaptureHealthWarningBorder.Visibility = warnings.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string FormatHealthBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024 * 1024)
            return $"{bytes / (1024d * 1024 * 1024 * 1024):0.0} TB";
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024d * 1024 * 1024):0.0} GB";
        return $"{Math.Max(0, bytes) / (1024d * 1024):0} MB";
    }

    private static string FormatHealthDuration(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0)
            return "0 min remaining";
        var duration = TimeSpan.FromSeconds(Math.Min(seconds, TimeSpan.FromDays(99).TotalSeconds));
        return duration.TotalHours >= 1
            ? $"{duration.TotalHours:0.0} h remaining"
            : $"{Math.Max(1, duration.TotalMinutes):0} min remaining";
    }

    private void UpdateStatusPulse()
    {
        // Capture state is conveyed by the persistent dot and status text.
        StatusDot.Opacity = 1;
        StatusDotScale.ScaleX = 1;
        StatusDotScale.ScaleY = 1;
    }

    private void UpdateRecordingModeStatus()
    {
        if (_recording.IsRecording)
        {
            if (StatusDot is not null)
                StatusDot.Fill = (Brush)Application.Current.Resources["KlippDangerBrush"];
            StatusPill.BorderBrush = (Brush)Application.Current.Resources["KlippDangerBrush"];
            StatusText.Foreground = (Brush)Application.Current.Resources["KlippTextPrimaryBrush"];
            StatusText.Text = $"REC \u2022 {_recording.RecordedSeconds:F0}s";
            RecordButton.Content = "Stop & save";
            RecordButton.Background = (Brush)Application.Current.Resources["KlippDangerBrush"];
            RecordButton.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 20, 25));
            RecordButton.BorderThickness = new Thickness(0);
            ResetSaveClipButtonChrome();
            SaveClipButton.IsEnabled = false;
        }
        else
        {
            if (StatusDot is not null)
                StatusDot.Fill = (Brush)Application.Current.Resources["KlippTextMutedBrush"];
            StatusPill.BorderBrush = (Brush)Application.Current.Resources["KlippBorderBrush"];
            StatusText.Foreground = (Brush)Application.Current.Resources["KlippTextSecondaryBrush"];
            StatusText.Text = "Ready to record";
            RecordButton.Content = "Record";
            RecordButton.Background = (Brush)Application.Current.Resources["KlippAccentBrush"];
            RecordButton.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 20, 25));
            RecordButton.BorderThickness = new Thickness(0);
            ResetSaveClipButtonChrome();
            SaveClipButton.IsEnabled = false;
        }
    }

    private void UpdateClipModeStatus()
    {
        if (_clipMode.IsMonitoring)
        {
            if (StatusDot is not null)
                StatusDot.Fill = (Brush)Application.Current.Resources["KlippSuccessBrush"];
            StatusPill.BorderBrush = (Brush)Application.Current.Resources["KlippSuccessBrush"];
            StatusText.Foreground = (Brush)Application.Current.Resources["KlippTextPrimaryBrush"];
            StatusText.Text = $"Buffering \u2022 {GetSelectedClipLengthSeconds()}s";
            RecordButton.Content = "Stop";
            RecordButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            RecordButton.Foreground = (Brush)Application.Current.Resources["KlippDangerBrush"];
            RecordButton.BorderBrush = (Brush)Application.Current.Resources["KlippDangerBrush"];
            RecordButton.BorderThickness = new Thickness(1);
            SaveClipButton.Background = (Brush)Application.Current.Resources["KlippAccentBrush"];
            SaveClipButton.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 20, 25));
            SaveClipButton.BorderThickness = new Thickness(0);
            SaveClipButton.IsEnabled = true;
        }
        else
        {
            if (StatusDot is not null)
                StatusDot.Fill = (Brush)Application.Current.Resources["KlippTextMutedBrush"];
            StatusPill.BorderBrush = (Brush)Application.Current.Resources["KlippBorderBrush"];
            StatusText.Foreground = (Brush)Application.Current.Resources["KlippTextSecondaryBrush"];
            StatusText.Text = "Instant clips paused";
            RecordButton.Content = "Start capture";
            RecordButton.Background = (Brush)Application.Current.Resources["KlippAccentBrush"];
            RecordButton.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 16, 20, 25));
            RecordButton.BorderThickness = new Thickness(0);
            SaveClipButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            SaveClipButton.Foreground = (Brush)Application.Current.Resources["KlippTextSecondaryBrush"];
            SaveClipButton.BorderBrush = (Brush)Application.Current.Resources["KlippBorderBrush"];
            SaveClipButton.BorderThickness = new Thickness(1);
            SaveClipButton.IsEnabled = false;
        }
    }

    private void ResetSaveClipButtonChrome()
    {
        SaveClipButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        SaveClipButton.Foreground = (Brush)Application.Current.Resources["KlippTextSecondaryBrush"];
        SaveClipButton.BorderBrush = (Brush)Application.Current.Resources["KlippBorderBrush"];
        SaveClipButton.BorderThickness = new Thickness(1);
    }

    private void OnLibrarySearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady || _suppressLibraryFilter) return;

        ApplyLibraryFilter();
    }

    private void OnLibrarySortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _suppressLibraryFilter) return;

        ApplyLibraryFilter();
    }

    private void OnLibraryFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _suppressLibraryFilter) return;

        ApplyLibraryFilter();
    }

    private void ApplyLibraryFilter()
    {
        if (_fullscreenInlineCard is not null)
        {
            _inlineLibraryRefreshPending = true;
            return;
        }
        _inlineLibraryRefreshPending = false;
        if (LibraryStatsText is null ||
            EmptyState is null ||
            NoMatchesState is null ||
            ClipsScroller is null)
        {
            return;
        }

        var query = LibrarySearchBox?.Text?.Trim() ?? string.Empty;
        var sort = (LibrarySortSelector?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Newest";
        var scope = (LibraryScopeSelector?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        var collection = (LibraryCollectionSelector?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;

        var clips = LibraryService.Clips.AsEnumerable();
        if (string.Equals(scope, "Favorites", StringComparison.OrdinalIgnoreCase))
            clips = clips.Where(clip => clip.IsFavorite);
        if (!string.IsNullOrWhiteSpace(collection))
        {
            clips = clips.Where(clip => string.Equals(
                clip.Collection,
                collection,
                StringComparison.CurrentCultureIgnoreCase));
        }
        if (!string.IsNullOrWhiteSpace(query))
        {
            clips = clips.Where(clip =>
                clip.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                clip.Meta.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                clip.TagsText.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                clip.Collection.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                Path.GetFileName(clip.FilePath).Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        clips = sort switch
        {
            "Oldest" => clips.OrderBy(GetClipWriteTime),
            "Largest" => clips.OrderByDescending(GetClipSize),
            "Smallest" => clips.OrderBy(GetClipSize),
            "Name" => clips.OrderBy(clip => clip.Title, StringComparer.CurrentCultureIgnoreCase),
            "Favorites" => clips
                .OrderByDescending(clip => clip.IsFavorite)
                .ThenByDescending(GetClipWriteTime),
            _ => clips.OrderByDescending(GetClipWriteTime)
        };

        ClipCard.StopActivePreview();
        VisibleClips.Clear();
        foreach (var clip in clips)
            VisibleClips.Add(clip);

        UpdateLibraryUI();
    }

    private void RefreshLibraryCollectionFilter()
    {
        if (LibraryCollectionSelector is null)
            return;

        var previous = (LibraryCollectionSelector.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? string.Empty;
        var wasSuppressed = _suppressLibraryFilter;
        _suppressLibraryFilter = true;
        try
        {
            LibraryCollectionSelector.Items.Clear();
            LibraryCollectionSelector.Items.Add(new ComboBoxItem
            {
                Content = "All collections",
                Tag = string.Empty
            });
            foreach (var collection in LibraryService.Collections)
            {
                LibraryCollectionSelector.Items.Add(new ComboBoxItem
                {
                    Content = collection,
                    Tag = collection
                });
            }

            LibraryCollectionSelector.SelectedItem = LibraryCollectionSelector.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag?.ToString(),
                    previous,
                    StringComparison.CurrentCultureIgnoreCase)) ?? LibraryCollectionSelector.Items[0];
        }
        finally
        {
            _suppressLibraryFilter = wasSuppressed;
        }
    }

    private void RefreshLibraryAfterMetadataChange()
    {
        RefreshLibraryCollectionFilter();
        ApplyLibraryFilter();
        _editorView?.SetLibraryClips(LibraryService.Clips);
    }

    private static DateTime GetClipWriteTime(ClipViewModel clip)
    {
        return clip.LastWriteTime == default
            ? DateTime.MinValue
            : clip.LastWriteTime;
    }

    private static long GetClipSize(ClipViewModel clip)
    {
        return clip.FileSizeBytes;
    }

    private void UpdateLibraryUI()
    {
        var totalCount = LibraryService.Clips.Count;
        var visibleCount = VisibleClips.Count;
        var hasQuery = !string.IsNullOrWhiteSpace(LibrarySearchBox?.Text) ||
                       !string.Equals(
                           (LibraryScopeSelector?.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
                           "All",
                           StringComparison.OrdinalIgnoreCase) ||
                       !string.IsNullOrWhiteSpace(
                           (LibraryCollectionSelector?.SelectedItem as ComboBoxItem)?.Tag?.ToString());
        var totalBytes = LibraryService.Clips.Sum(GetClipSize);
        var visibleBytes = VisibleClips.Sum(GetClipSize);

        LibraryStatsText.Text = hasQuery
            ? $"{visibleCount} of {FormatClipCount(totalCount)} \u2022 {FormatBytes(visibleBytes)} shown"
            : $"{FormatClipCount(totalCount)} \u2022 {FormatBytes(totalBytes)}";

        var hasClips = totalCount > 0;
        var hasVisibleClips = visibleCount > 0;
        EmptyState.Visibility = hasClips ? Visibility.Collapsed : Visibility.Visible;
        NoMatchesState.Visibility = hasClips && !hasVisibleClips ? Visibility.Visible : Visibility.Collapsed;
        ClipsScroller.Visibility = hasVisibleClips ? Visibility.Visible : Visibility.Collapsed;
        if (LibrarySearchBox is not null)
            LibrarySearchBox.IsEnabled = hasClips;
        if (LibrarySortSelector is not null)
            LibrarySortSelector.IsEnabled = hasClips;
        if (LibraryScopeSelector is not null)
            LibraryScopeSelector.IsEnabled = hasClips;
        if (LibraryCollectionSelector is not null)
            LibraryCollectionSelector.IsEnabled = hasClips;
        if (StorageSummaryText is not null)
        {
            StorageSummaryText.Text = hasClips
                ? $"{FormatClipCount(totalCount)} saved \u2022 {FormatBytes(totalBytes)}"
                : "No clips yet \u2022 0 MB";
        }
        if (StoragePathText is not null)
        {
            StoragePathText.Text = LibraryService.ClipsDirectory;
            ToolTipService.SetToolTip(StoragePathText, LibraryService.ClipsDirectory);
        }
    }

    private static string FormatClipCount(int count)
    {
        return count == 1 ? "1 clip" : $"{count} clips";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
            return "0 MB";

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit <= 1 ? $"{value:F0} {units[unit]}" : $"{value:F1} {units[unit]}";
    }

    private async void OnOpenClipsFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(LibraryService.ClipsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = LibraryService.ClipsDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not open clips folder", ex.Message);
        }
    }

    private async void OnChangeClipsFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.VideosLibrary
            };
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
                return;

            await SetClipsDirectoryAsync(folder.Path);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not select folder", ex.Message);
        }
    }

    private async void OnResetClipsFolderClicked(object sender, RoutedEventArgs e)
    {
        await SetClipsDirectoryAsync(ClipLibraryService.GetDefaultClipsDirectory());
    }

    private async Task SetClipsDirectoryAsync(string directory)
    {
        if (_updateRestarting) return;
        using var updateWork = BeginUpdateBlockingOperation();
        try
        {
            var before = LibraryService.ClipsDirectory;
            LibraryService.SetClipsDirectory(directory);
            _settings.Current.ClipsDirectory = LibraryService.ClipsDirectory;
            await _settings.SaveAsync();

            if (!string.Equals(before, LibraryService.ClipsDirectory, StringComparison.OrdinalIgnoreCase))
                await LoadLibraryAsync();
            else
                UpdateLibraryUI();

            StatusText.Text = "Clips folder updated";
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Could not change clips folder", ex.Message);
        }
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        if (_hiddenInTray) RestoreFromTray();
        var dialog = AccessibleDialogFactory.Create(
            this.Content.XamlRoot,
            title,
            message,
            closeButtonText: "OK",
            defaultButton: ContentDialogButton.Close);
        await dialog.ShowAsync();
    }

    private async Task<bool> ShowConfirmAsync(string title, string message)
    {
        var dialog = AccessibleDialogFactory.Create(
            this.Content.XamlRoot,
            title,
            message,
            primaryButtonText: "Move to Recycle Bin",
            closeButtonText: "Cancel",
            defaultButton: ContentDialogButton.Close);

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    private async Task<string?> ShowRenameDialogAsync(string currentTitle)
    {
        var input = new TextBox
        {
            Text = currentTitle,
            PlaceholderText = "Clip name",
            MaxLength = 100,
            MinWidth = 320
        };
        AutomationProperties.SetName(input, "New clip name");
        AutomationProperties.SetHelpText(input, "Enter the name to show in the library.");
        input.Loaded += (_, _) =>
        {
            input.Focus(FocusState.Programmatic);
            input.SelectAll();
        };

        var dialog = AccessibleDialogFactory.Create(
            this.Content.XamlRoot,
            "Rename clip",
            input,
            primaryButtonText: "Save",
            closeButtonText: "Cancel",
            defaultButton: ContentDialogButton.Primary,
            automationHelpText: "Change the clip name in the library.");

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return null;

        return input.Text.Trim();
    }

    private async Task ShowOrganizeClipDialogAsync(string filePath)
    {
        if (_updateRestarting) return;
        using var updateWork = BeginUpdateBlockingOperation();
        if (string.IsNullOrWhiteSpace(filePath))
            return;

        var metadata = LibraryService.GetClipMetadata(filePath);
        var favoriteToggle = new ToggleSwitch
        {
            Header = "Favorite",
            IsOn = metadata.IsFavorite
        };
        AutomationProperties.SetName(favoriteToggle, "Favorite");
        var tagsInput = new TextBox
        {
            Header = "Tags",
            Text = string.Join(", ", metadata.Tags),
            PlaceholderText = "gaming, highlight, tutorial",
            MaxLength = 420,
            TextWrapping = TextWrapping.Wrap
        };
        AutomationProperties.SetName(tagsInput, "Tags");
        AutomationProperties.SetHelpText(tagsInput, "Separate tags with commas or semicolons.");
        var collectionInput = new TextBox
        {
            Header = "Collection",
            Text = metadata.Collection,
            PlaceholderText = LibraryService.Collections.FirstOrDefault() ?? "For example, Favorites 2026",
            MaxLength = 48
        };
        AutomationProperties.SetName(collectionInput, "Collection");
        var content = new StackPanel
        {
            Spacing = 14,
            MinWidth = 300
        };
        content.Children.Add(new TextBlock
        {
            Text = Path.GetFileNameWithoutExtension(filePath),
            Foreground = (Brush)Application.Current.Resources["KlippTextSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        content.Children.Add(favoriteToggle);
        content.Children.Add(tagsInput);
        content.Children.Add(collectionInput);

        var dialog = AccessibleDialogFactory.Create(
            this.Content.XamlRoot,
            "Organize clip",
            content,
            primaryButtonText: "Save",
            closeButtonText: "Cancel",
            defaultButton: ContentDialogButton.Primary,
            automationHelpText: "Edit the clip's favorite status, tags, and collection.");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var tags = tagsInput.Text.Split(
            [',', ';', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        LibraryService.UpdateClipMetadata(
            filePath,
            favoriteToggle.IsOn,
            tags,
            collectionInput.Text);
        RefreshLibraryAfterMetadataChange();
        StatusText.Text = "Library details saved";
    }

    private async Task<(TimeSpan Start, TimeSpan End)?> ShowTrimDialogAsync(TimeSpan duration)
    {
        var selector = new TrimRangeSelector
        {
            Duration = duration,
            Start = TimeSpan.Zero,
            End = duration
        };

        selector.RangeChanged += (_, args) => _playerView?.SeekTo(args.PreviewTime);

        var validationText = new TextBlock
        {
            Text = string.Empty,
            Foreground = (Brush)Application.Current.Resources["KlippDangerBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        AutomationProperties.SetName(validationText, "Trim validation");
        AutomationProperties.SetLiveSetting(validationText, AutomationLiveSetting.Assertive);

        var content = new StackPanel
        {
            Spacing = 16,
            MinWidth = 300
        };
        content.Children.Add(new TextBlock
        {
            Text = $"Total duration: {FormatDialogTimestamp(duration)}",
            Foreground = (Brush)Application.Current.Resources["KlippTextSecondaryBrush"]
        });
        content.Children.Add(selector);
        content.Children.Add(validationText);

        var dialog = AccessibleDialogFactory.Create(
            this.Content.XamlRoot,
            "Trim clip",
            content,
            primaryButtonText: "Export",
            closeButtonText: "Cancel",
            defaultButton: ContentDialogButton.Primary,
            automationHelpText: "Choose the exact start and end for the export.");

        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (selector.End <= selector.Start)
            {
                validationText.Text = "Start must be before end.";
                validationText.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
            return null;

        return (
            selector.Start,
            selector.End);
    }

    private static string FormatDialogTimestamp(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }
}
