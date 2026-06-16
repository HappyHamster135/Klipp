using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Klipp.Desktop.Controls;
using Klipp.Desktop.Services;

namespace Klipp.Desktop;

public sealed partial class MainWindow : Window
{
    /// <summary>Appens två inspelningslägen.</summary>
    private enum AppMode { Recording, Clip }

    private readonly RecordingService _recording = new();
    private readonly ClipModeService _clipMode = new();
    private readonly SettingsService _settings = new();
    public ClipLibraryService LibraryService { get; } = new();

    private readonly DispatcherTimer _statusTimer;
    private AppMode _mode = AppMode.Recording;
    private bool _uiReady;
    private HotkeyManager? _hotkeyManager;
    private bool _capturingHotkey;

    public MainWindow()
    {
        InitializeComponent();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatusUI();

        LibraryService.Clips.CollectionChanged += (_, _) => UpdateLibraryUI();

        _uiReady = true;
        SetupGlobalHotkey();
        _ = InitializeAsync();
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
    /// Triggas när den globala hotkeyen trycks. Sparar ett klipp om vi övervakar.
    /// </summary>
    private void OnGlobalHotkeyPressed(object? sender, EventArgs e)
    {
        // Hotkey-eventet kommer från WndProc (UI-tråden) men vi går via dispatcher
        // för säkerhets skull, eftersom vi rör UI-element.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_mode == AppMode.Clip && _clipMode.IsMonitoring)
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
        if (_capturingHotkey)
        {
            // Redan i capture-läge → avbryt
            StopHotkeyCapture();
            return;
        }

        _capturingHotkey = true;
        HotkeyText.Text = "Tryck en kombination...";

        // Lyssna på tangenttryck på fönstret medan vi fångar
        this.Content.KeyDown += OnHotkeyCaptureKeyDown;
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
                "Kunde inte sätta hotkey",
                $"Kombinationen {display} kunde inte registreras. Den kanske används av en annan app. Prova en annan.");
        }
    }

    /// <summary>Kollar om en tangent hålls nere just nu.</summary>
    private static bool IsKeyDown(Windows.System.VirtualKey key)
    {
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }


    private async Task InitializeAsync()
    {
        await _settings.LoadAsync();
        ApplyLoadedSettings();

        await LibraryService.LoadFromDiskAsync();
        UpdateLibraryUI();
        UpdateStatusUI();

        // Auto-starta övervakning i Klipp-läge så användaren kan klippa direkt.
        await StartAutoMonitoringAsync();
    }

    /// <summary>
    /// Applicerar inlästa inställningar på UI + hotkey efter att settings laddats.
    /// </summary>
    private void ApplyLoadedSettings()
    {
        // Klipplängd: mappa sekunder till dropdown-index
        ClipLengthSelector.SelectedIndex = _settings.Current.ClipLengthSeconds switch
        {
            15 => 0,
            30 => 1,
            60 => 2,
            120 => 3,
            _ => 1
        };

        // Hotkey-display
        HotkeyText.Text = _settings.Current.HotkeyDisplay;

        // Registrera om hotkeyen med inlästa värden
        _hotkeyManager?.Register(
            (HotkeyManager.Modifiers)_settings.Current.HotkeyModifiers,
            _settings.Current.HotkeyVirtualKey);
    }

    /// <summary>
    /// Startar Klipp-läge + övervakning automatiskt vid appstart, så användaren
    /// kan klippa direkt utan att klicka. Hoppar över om FFmpeg inte är installerat
    /// (vi vill inte trigga en tyst nedladdning vid allra första körningen).
    /// </summary>
    private async Task StartAutoMonitoringAsync()
    {
        if (!_clipMode.IsFFmpegInstalled)
        {
            // FFmpeg saknas — låt användaren starta manuellt så de ser nedladdningen.
            return;
        }

        try
        {
            // Växla UI till Klipp-läge
            _mode = AppMode.Clip;
            ModeSelector.SelectedIndex = 1;
            ClipLengthSelector.Visibility = Visibility.Visible;

            await _clipMode.StartMonitoringAsync();
            _statusTimer.Start();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Klipp] Auto-monitoring misslyckades: {ex.Message}");
        }
        finally
        {
            UpdateStatusUI();
        }
    }

    // === Lägesväljare ===

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged kan fyra under XAML-init innan alla element finns.
        if (!_uiReady) return;

        // Om en inspelning/övervakning pågår, byt inte läge mitt i
        if (_recording.IsRecording || _clipMode.IsMonitoring)
        {
            // Återställ selectorn till nuvarande läge
            ModeSelector.SelectedIndex = _mode == AppMode.Recording ? 0 : 1;
            return;
        }

        _mode = ModeSelector.SelectedIndex == 1 ? AppMode.Clip : AppMode.Recording;

        // Klipplängd-dropdownen syns bara i Klipp-läge
        if (ClipLengthSelector is not null)
        {
            ClipLengthSelector.Visibility =
                _mode == AppMode.Clip ? Visibility.Visible : Visibility.Collapsed;
        }

        UpdateStatusUI();
    }

    /// <summary>Sparar klipplängd-valet när användaren ändrar dropdownen.</summary>
    private async void OnClipLengthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady) return;

        _settings.Current.ClipLengthSeconds = GetSelectedClipLengthSeconds();
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

    // === Record/Monitor-knappen ===

    private async void OnRecordClicked(object sender, RoutedEventArgs e)
    {
        if (_mode == AppMode.Recording)
            await HandleRecordingModeButtonAsync();
        else
            await HandleClipModeButtonAsync();
    }

    private async Task HandleRecordingModeButtonAsync()
    {
        RecordButton.IsEnabled = false;

        try
        {
            if (_recording.IsRecording)
            {
                StatusText.Text = "Sparar...";
                var outputPath = await _recording.StopAsync();
                _statusTimer.Stop();

                if (outputPath is not null)
                    LibraryService.AddNewClip(outputPath);
            }
            else
            {
                if (!_recording.IsFFmpegInstalled)
                {
                    StatusText.Text = "Laddar ner FFmpeg...";
                    await _recording.EnsureFFmpegInstalledAsync();
                }

                var outputPath = LibraryService.GenerateClipPath();
                await _recording.StartAsync(outputPath);
                _statusTimer.Start();
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Fel vid inspelning", ex.Message);
        }
        finally
        {
            RecordButton.IsEnabled = true;
            UpdateStatusUI();
        }
    }

    private async Task HandleClipModeButtonAsync()
    {
        RecordButton.IsEnabled = false;

        try
        {
            if (_clipMode.IsMonitoring)
            {
                StatusText.Text = "Stoppar...";
                await _clipMode.StopMonitoringAsync();
                _statusTimer.Stop();
            }
            else
            {
                if (!_clipMode.IsFFmpegInstalled)
                {
                    StatusText.Text = "Laddar ner FFmpeg...";
                    await _clipMode.EnsureFFmpegInstalledAsync();
                }

                await _clipMode.StartMonitoringAsync();
                _statusTimer.Start();
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Fel i Klipp-läge", ex.Message);
        }
        finally
        {
            RecordButton.IsEnabled = true;
            UpdateStatusUI();
        }
    }

    // === Spara klipp-knappen (bara aktiv i Klipp-läge under övervakning) ===

    private async void OnSaveClipClicked(object sender, RoutedEventArgs e)
    {
        if (_mode != AppMode.Clip || !_clipMode.IsMonitoring)
            return;

        SaveClipButton.IsEnabled = false;

        try
        {
            var seconds = GetSelectedClipLengthSeconds();
            var outputPath = LibraryService.GenerateClipPath();

            // ClipExtractor klipper det som finns — om vi bara övervakat 3s
            // får vi ett 3s-klipp, inte ett fel.
            var result = await _clipMode.SaveClipAsync(outputPath, seconds);
            LibraryService.AddNewClip(result.OutputPath);
        }
        catch (InvalidOperationException)
        {
            // Inga segment alls än (övervakning precis startad) — visa kort i statusraden
            // istället för en störande popup.
            StatusText.Text = "Inget att klippa än";
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Kunde inte spara klipp", ex.Message);
        }
        finally
        {
            SaveClipButton.IsEnabled = _clipMode.IsMonitoring;
        }
    }

    // === Klippbibliotek + spelare ===

    private void OnClipCardClicked(object sender, ClipCardClickEventArgs e)
    {
        if (string.IsNullOrEmpty(e.FilePath) || !File.Exists(e.FilePath))
            return;

        ShowPlayer(e.FilePath);
    }

    private void OnPlayerBackRequested(object? sender, EventArgs e)
    {
        ShowLibrary();
    }

    private async void OnPlayerDeleteRequested(object? sender, ClipDeleteEventArgs e)
    {
        var confirm = await ShowConfirmAsync(
            "Ta bort klipp?",
            $"Vill du verkligen ta bort '{Path.GetFileName(e.FilePath)}'?");

        if (!confirm) return;

        try
        {
            File.Delete(e.FilePath);

            for (int i = LibraryService.Clips.Count - 1; i >= 0; i--)
            {
                if (LibraryService.Clips[i].FilePath == e.FilePath)
                {
                    LibraryService.Clips.RemoveAt(i);
                    break;
                }
            }

            ShowLibrary();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Kunde inte ta bort filen", ex.Message);
        }
    }

    private void ShowPlayer(string filePath)
    {
        LibraryView.Visibility = Visibility.Collapsed;
        PlayerView.Visibility = Visibility.Visible;
        PlayerView.LoadClip(filePath);
        UpdatePlayerNavigationButtons(filePath);
    }

    private void ShowLibrary()
    {
        PlayerView.Stop();
        PlayerView.Visibility = Visibility.Collapsed;
        LibraryView.Visibility = Visibility.Visible;
    }

    private void UpdatePlayerNavigationButtons(string currentFilePath)
    {
        var index = FindClipIndex(currentFilePath);
        PlayerView.CanGoPrev = index > 0;
        PlayerView.CanGoNext = index >= 0 && index < LibraryService.Clips.Count - 1;
    }

    private int FindClipIndex(string filePath)
    {
        for (int i = 0; i < LibraryService.Clips.Count; i++)
        {
            if (LibraryService.Clips[i].FilePath == filePath)
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
        var currentPath = PlayerView.CurrentFilePath;
        if (string.IsNullOrEmpty(currentPath)) return;

        var currentIndex = FindClipIndex(currentPath);
        if (currentIndex < 0) return;

        var newIndex = currentIndex + offset;
        if (newIndex < 0 || newIndex >= LibraryService.Clips.Count) return;

        var newClip = LibraryService.Clips[newIndex];
        PlayerView.LoadClip(newClip.FilePath);
        UpdatePlayerNavigationButtons(newClip.FilePath);
    }

    // === Status-UI ===

    private void UpdateStatusUI()
    {
        if (_mode == AppMode.Recording)
        {
            UpdateRecordingModeStatus();
        }
        else
        {
            UpdateClipModeStatus();
        }
    }

    private void UpdateRecordingModeStatus()
    {
        if (_recording.IsRecording)
        {
            if (StatusDot is not null)
                StatusDot.Fill = (Brush)Application.Current.Resources["KlippDangerBrush"];
            StatusText.Text = $"Spelar in \u2022 {_recording.RecordedSeconds:F0}s";
            RecordButton.Content = "Stoppa & spara";
            SaveClipButton.IsEnabled = false;
        }
        else
        {
            if (StatusDot is not null)
                StatusDot.Fill = (Brush)Application.Current.Resources["KlippTextMutedBrush"];
            StatusText.Text = "Inte aktiv";
            RecordButton.Content = "Spela in";
            SaveClipButton.IsEnabled = false;
        }
    }

    private void UpdateClipModeStatus()
    {
        if (_clipMode.IsMonitoring)
        {
            if (StatusDot is not null)
                StatusDot.Fill = (Brush)Application.Current.Resources["KlippSuccessBrush"];
            StatusText.Text = "\u00d6vervakar";
            RecordButton.Content = "Stoppa";
            SaveClipButton.IsEnabled = true;
        }
        else
        {
            if (StatusDot is not null)
                StatusDot.Fill = (Brush)Application.Current.Resources["KlippTextMutedBrush"];
            StatusText.Text = "Inte aktiv";
            RecordButton.Content = "Börja övervaka";
            SaveClipButton.IsEnabled = false;
        }
    }

    private void UpdateLibraryUI()
    {
        var count = LibraryService.Clips.Count;
        LibraryStatsText.Text = count == 1 ? "1 klipp" : $"{count} klipp";

        var hasClips = count > 0;
        EmptyState.Visibility = hasClips ? Visibility.Collapsed : Visibility.Visible;
        ClipsScroller.Visibility = hasClips ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = this.Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async Task<bool> ShowConfirmAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = "Ta bort",
            CloseButtonText = "Avbryt",
            XamlRoot = this.Content.XamlRoot,
            DefaultButton = ContentDialogButton.Close
        };

        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }
}
