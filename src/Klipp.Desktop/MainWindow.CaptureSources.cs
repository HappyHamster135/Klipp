using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Capture.Models;
using Klipp.Core.Models;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Klipp.Desktop;

public sealed partial class MainWindow
{
    private readonly CaptureTargetService _captureTargets = new();
    private readonly SemaphoreSlim _captureOperationGate = new(1, 1);
    private readonly SemaphoreSlim _captureInventoryGate = new(1, 1);
    private IReadOnlyList<CaptureTargetInfo> _captureDisplays = [];
    private IReadOnlyList<CaptureTargetInfo> _captureWindows = [];
    private CaptureTargetInfo? _selectedCaptureTarget;
    private DispatcherTimer? _captureSourceTimer;
    private bool _captureSessionReady;
    private bool _capturePollRunning;
    private bool _changingCaptureSource;
    private bool _instantCaptureRequested;
    private bool _captureWindowClosing;
    private bool _captureSourceEnded;
    private string? _preferredManualWindowId;
    private bool _manualCapturePreferencePending;
    private int _capturePreferenceVersion;
    private CaptureOptions? _activeCaptureOptions;
    private string? _pendingAutomaticTargetId;
    private DateTime _pendingAutomaticTargetSince;
    private string? _failedCaptureTargetId;
    private DateTime _retryCaptureTargetAfter;
    private string? _captureSourceError;

    private bool IsCaptureSourceChanging => !_recording.IsRecording &&
        (_manualCapturePreferencePending || _changingCaptureSource);

    private string CaptureSourceDisplayName => IsCaptureSourceChanging ? "Switching source…" :
        _selectedCaptureTarget?.DisplayName ??
        (_settings.Current.CaptureSourceMode == "auto" ? "Waiting for a game" : "Source unavailable");

    private string CaptureSourceDescription => _recording.IsRecording
        ? "Recording · this source stays fixed until you stop"
        : IsCaptureSourceChanging ? "Applying the selected capture source"
        : _captureSourceError ?? (_settings.Current.CaptureSourceMode switch
        {
            "desktop" => "Whole display · desktop and visible windows",
            "window" => "Specific window · automatic game switching is off",
            _ => !_settings.Current.AutoDetectGames ? "Automatic game detection is off"
                : _selectedCaptureTarget is null ? "Automatic game · open a game or choose a source"
                : "Automatic game · keeps the game selected when you Alt-Tab"
        });

    private string? ActiveGameCaptureBackend => _recording.IsRecording ? _recording.CaptureBackendName :
        _clipMode.IsMonitoring ? _clipMode.CaptureBackendName : null;

    private string? GameCaptureFallbackReason => _recording.IsRecording ? _recording.GameCaptureFallbackReason :
        _clipMode.IsMonitoring ? _clipMode.GameCaptureFallbackReason : null;

    private string CaptureSourceDetail => ActiveGameCaptureBackend is { } backend &&
        (_activeCaptureOptions?.EnableExclusiveGameCapture == true || backend == "DirectX 11 game capture")
        ? CaptureSourceDescription + "\n" + (backend == "DirectX 11 game capture"
            ? "DirectX 11 game capture · in-game popups enabled"
            : "Windows capture · " + (GameCaptureFallbackReason ?? "Exclusive game capture is unavailable for this source."))
        : CaptureSourceDescription;

    private static bool SameCaptureSource(CaptureTargetInfo? first, CaptureTargetInfo? second) =>
        ReferenceEquals(first, second) ||
        first is not null && second is not null &&
        string.Equals(first.Id, second.Id, StringComparison.Ordinal) &&
        first.Target.Kind == second.Target.Kind &&
        first.Target.Handle == second.Target.Handle &&
        string.Equals(first.Target.DeviceId, second.Target.DeviceId, StringComparison.OrdinalIgnoreCase) &&
        first.Target.ProcessId == second.Target.ProcessId &&
        first.Target.ProcessCreated == second.Target.ProcessCreated;

    private async Task InitializeCaptureSourcesAsync()
    {
        _clipMode.CaptureTargetClosed += OnCaptureSourceClosed;
        _recording.CaptureTargetClosed += OnCaptureSourceClosed;
        await RefreshCaptureSourceInventoryAsync();
        _selectedCaptureTarget = ResolvePreferredCaptureTarget();
        UpdateCaptureSourceToolbar();
    }

    private void StartCaptureSourceMonitoring()
    {
        _captureSessionReady = true;
        _captureSourceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _captureSourceTimer.Tick += async (_, _) => await PollCaptureSourcesAsync();
        _captureSourceTimer.Start();
        UpdateStatusUI();
    }

    private void StopCaptureSourceMonitoring()
    {
        _captureWindowClosing = true;
        _captureSessionReady = false;
        _captureSourceTimer?.Stop();
        _clipMode.CaptureTargetClosed -= OnCaptureSourceClosed;
        _recording.CaptureTargetClosed -= OnCaptureSourceClosed;
    }

    private void OnCaptureSourceClosed(object? sender, EventArgs e)
    {
        var endedTarget = ReferenceEquals(sender, _recording) ? _recording.ActiveTarget : _clipMode.ActiveTarget;
        DispatcherQueue.TryEnqueue(() =>
        {
            var activeTarget = ReferenceEquals(sender, _recording) ? _recording.ActiveTarget : _clipMode.ActiveTarget;
            if (_captureWindowClosing || endedTarget is null || !ReferenceEquals(endedTarget, activeTarget)) return;
            _captureSourceEnded = true;
            _ = PollCaptureSourcesAsync();
        });
    }

    private async Task RefreshCaptureSourceInventoryAsync()
    {
        await _captureInventoryGate.WaitAsync();
        try
        {
            var snapshot = await Task.Run(() => (_captureTargets.GetMonitors(), _captureTargets.GetWindows()));
            if (_captureWindowClosing) return;
            _captureDisplays = snapshot.Item1;
            var remembered = _settings.Current.CaptureGameProcessNames;
            _captureWindows = snapshot.Item2.Select(window => remembered.Contains(window.ProcessName,
                StringComparer.OrdinalIgnoreCase)
                ? window with { IsGame = true, GameReason = "Remembered game" } : window).ToArray();
        }
        finally { _captureInventoryGate.Release(); }
    }

    private CaptureTargetInfo? ResolvePreferredCaptureTarget()
    {
        var settings = _settings.Current;
        if (settings.CaptureSourceMode == "desktop")
            return string.IsNullOrWhiteSpace(settings.CaptureMonitorId)
                ? _captureDisplays.FirstOrDefault(display => display.IsPrimary) ?? _captureDisplays.FirstOrDefault()
                : _captureDisplays.FirstOrDefault(display => display.Id == settings.CaptureMonitorId);
        if (settings.CaptureSourceMode == "window")
        {
            var windows = _captureWindows.Where(window => string.Equals(window.ProcessName,
                settings.CaptureWindowProcessName, StringComparison.OrdinalIgnoreCase)).ToArray();
            return windows.FirstOrDefault(window => window.Id == _preferredManualWindowId) ??
                windows.FirstOrDefault(window => window.Id == _selectedCaptureTarget?.Id) ??
                windows.FirstOrDefault(window => window.WindowTitle == settings.CaptureWindowTitle) ??
                (windows.Length == 1 ? windows[0] : null);
        }
        return _captureTargets.SelectAutomaticTarget(_captureWindows, _selectedCaptureTarget?.Id, settings.AutoDetectGames);
    }

    private async Task PollCaptureSourcesAsync()
    {
        if (!_captureSessionReady || _captureWindowClosing || _capturePollRunning || _isSwitchingMode || _manualCapturePreferencePending ||
            _captureOperationGate.CurrentCount == 0) return;
        _capturePollRunning = true;
        try
        {
            await RefreshCaptureSourceInventoryAsync();
            if (_captureWindowClosing || _manualCapturePreferencePending) return;
            if (_recording.IsRecording)
            {
                // A manual recording remains one file with one source. Closing
                // that source finalizes the recording instead of capturing an
                // unrelated desktop or silently changing its contents.
                if (_captureSourceEnded || (_selectedCaptureTarget is not null && !_captureTargets.IsEligible(_selectedCaptureTarget.Target)))
                    await FinishRecordingForClosedSourceAsync();
                else UpdateCaptureSourceToolbar();
                return;
            }
            var next = ResolvePreferredCaptureTarget();
            var changed = !SameCaptureSource(next, _selectedCaptureTarget);
            if (changed && _settings.Current.CaptureSourceMode == "auto" &&
                _selectedCaptureTarget is not null && _captureTargets.IsEligible(_selectedCaptureTarget.Target))
            {
                var candidate = next?.Id ?? "waiting";
                if (_pendingAutomaticTargetId != candidate)
                {
                    _pendingAutomaticTargetId = candidate;
                    _pendingAutomaticTargetSince = DateTime.UtcNow;
                    return;
                }
                if (DateTime.UtcNow - _pendingAutomaticTargetSince < TimeSpan.FromSeconds(1)) return;
            }
            _pendingAutomaticTargetId = null;
            if (_captureSourceEnded || changed || (_instantCaptureRequested && !_clipMode.IsMonitoring && next is not null &&
                (next.Id != _failedCaptureTargetId || DateTime.UtcNow >= _retryCaptureTargetAfter)))
                await ChangeCaptureSourceAsync(next, automatic: true, forceRestart: _captureSourceEnded);
            else
            {
                if (next is not null) _selectedCaptureTarget = next;
                UpdateCaptureSourceToolbar();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Capture source scan: {ex.Message}");
        }
        finally { _capturePollRunning = false; }
    }

    private void BeginCaptureSourcePreferenceChange()
    {
        _capturePreferenceVersion++;
        _manualCapturePreferencePending = true;
        UpdateCaptureSourceToolbar();
    }

    private async Task ApplyCaptureSourcePreferencesAsync()
    {
        if (!_manualCapturePreferencePending) BeginCaptureSourcePreferenceChange();
        var version = _capturePreferenceVersion;
        _captureSourceError = null;
        _failedCaptureTargetId = null;
        _pendingAutomaticTargetId = null;
        try
        {
            await RefreshCaptureSourceInventoryAsync();
            if (_captureWindowClosing || _recording.IsRecording) return;
            await ChangeCaptureSourceAsync(ResolvePreferredCaptureTarget(), automatic: false, waitForOperation: true);
            SyncSettingsExperience();
        }
        finally
        {
            if (_capturePreferenceVersion == version) _manualCapturePreferencePending = false;
            if (!_captureWindowClosing) UpdateStatusUI();
        }
    }

    private async Task<bool> ApplyAudioPreferencesToCaptureAsync()
    {
        if (!_captureSessionReady || _captureWindowClosing || _recording.IsRecording ||
            !_instantCaptureRequested || _mode != AppMode.Clip) return false;
        StopSettingsAudioTests();
        await ChangeCaptureSourceAsync(ResolvePreferredCaptureTarget(), automatic: false,
            forceRestart: true, waitForOperation: true);
        return _clipMode.IsMonitoring;
    }

    private async Task ChangeCaptureSourceAsync(CaptureTargetInfo? next, bool automatic, bool forceRestart = false, bool waitForOperation = false)
    {
        if (waitForOperation) await _captureOperationGate.WaitAsync();
        else if (!await _captureOperationGate.WaitAsync(0)) return;
        var previous = _selectedCaptureTarget;
        var wasMonitoring = _clipMode.IsMonitoring;
        var changed = !SameCaptureSource(previous, next);
        try
        {
            if (_captureWindowClosing || _recording.IsRecording) return;
            _changingCaptureSource = true;
            _captureSourceEnded = false;
            UpdateStatusUI();
            if (wasMonitoring && (changed || forceRestart))
            {
                await RunServiceOperationAsync(() => _clipMode.StopMonitoringAsync());
                _statusTimer.Stop();
            }
            _selectedCaptureTarget = next;
            _captureSourceError = null;
            if (_instantCaptureRequested && _mode == AppMode.Clip && next is not null && !_clipMode.IsMonitoring)
            {
                if (!await EnsureCaptureStorageAvailableAsync(showDialog: false))
                {
                    _instantCaptureRequested = false;
                    return;
                }
                if (_clipMode.IsFFmpegInstalled)
                {
                    LibraryService.PauseBackgroundMetadata();
                    var options = GetCaptureOptions();
                    _activeCaptureOptions = options;
                    await RunServiceOperationAsync(() => _clipMode.StartMonitoringAsync(options));
                    _statusTimer.Start();
                }
            }
            if (automatic && !_manualCapturePreferencePending && _instantCaptureRequested && (changed || !wasMonitoring))
            {
                if (_clipMode.IsMonitoring && next is not null)
                    ShowCaptureNotification(CaptureNotificationKind.AutomaticSourceChange,
                        changed && previous is not null ? "Capture window changed" :
                            _settings.Current.CaptureSourceMode == "auto" ? "Game detected" : "Capture resumed",
                        _settings.Current.CaptureSourceMode == "auto"
                            ? $"Automatic game selection: {next.DisplayName}. A new instant-clip buffer has started."
                            : $"{next.DisplayName} is available again. Instant clips have resumed.");
                else if (wasMonitoring && next is null)
                    ShowCaptureNotification(CaptureNotificationKind.AutomaticSourceChange, "Capture paused",
                        _settings.Current.CaptureSourceMode == "auto"
                            ? "The game closed. Waiting for another game; your desktop is not being captured."
                            : "The selected source closed or disconnected. Choose another window or display to continue.");
            }
            _failedCaptureTargetId = null;
        }
        catch (Exception ex)
        {
            _captureSourceError = $"Could not capture this source: {ex.Message}";
            _failedCaptureTargetId = next?.Id;
            _retryCaptureTargetAfter = DateTime.UtcNow.AddSeconds(30);
            System.Diagnostics.Debug.WriteLine(_captureSourceError);
        }
        finally
        {
            _changingCaptureSource = false;
            _captureOperationGate.Release();
            if (!_recording.IsRecording && !_clipMode.IsMonitoring) LibraryService.ResumeBackgroundMetadata();
            if (!_captureWindowClosing) UpdateStatusUI();
        }
    }

    private async Task<bool> PrepareCaptureTargetForStartAsync()
    {
        await RefreshCaptureSourceInventoryAsync();
        _selectedCaptureTarget = ResolvePreferredCaptureTarget();
        _captureSourceEnded = false;
        _captureSourceError = null;
        UpdateCaptureSourceToolbar();
        if (_selectedCaptureTarget is not null) return true;
        StatusText.Text = _settings.Current.CaptureSourceMode == "auto"
            ? "Waiting for a game · or choose a desktop/window"
            : "Source unavailable · choose another window or display";
        return false;
    }

    private async Task FinishRecordingForClosedSourceAsync()
    {
        if (!await _captureOperationGate.WaitAsync(0)) return;
        try
        {
            if (!_recording.IsRecording) return;
            _captureSourceEnded = false;
            var seconds = _recording.RecordedSeconds;
            var output = await RunServiceOperationAsync(_recording.StopAsync);
            _statusTimer.Stop();
            if (output is not null) LibraryService.AddNewClip(output);
            NotifyRecordingStopped(output, seconds, "The capture source became unavailable.");
        }
        catch (Exception ex) { _captureSourceError = $"Recording stopped: {ex.Message}"; }
        finally
        {
            _captureOperationGate.Release();
            LibraryService.ResumeBackgroundMetadata();
            UpdateStatusUI();
            await RefreshRecordingRecoveryAsync();
        }
    }

    private void NotifyRecordingStopped(string? output, double seconds, string? reason = null) =>
        ShowCaptureNotification(CaptureNotificationKind.RecordingStopped,
            output is null ? "Recording stopped" : "Recording saved",
            output is null ? "No video was produced." :
                $"{FormatCompactDuration(TimeSpan.FromSeconds(seconds))} · {Path.GetFileName(output)}" +
                (string.IsNullOrWhiteSpace(reason) ? string.Empty : $"\n{reason}"));

    private void UpdateCaptureSourceToolbar()
    {
        if (CaptureSourceButton is null) return;
        var busy = !_captureSessionReady || _changingCaptureSource || _isSwitchingMode || _manualCapturePreferencePending || _captureOperationGate.CurrentCount == 0;
        CaptureSourceNameText.Text = CaptureSourceDisplayName;
        CaptureSourceModeText.Text = _recording.IsRecording ? "Recording · source locked" :
            IsCaptureSourceChanging ? "Applying source selection" : _settings.Current.CaptureSourceMode switch
        {
            "desktop" => "Whole desktop display",
            "window" => "Specific window",
            _ => "Automatic game"
        };
        if (!busy && ActiveGameCaptureBackend == "DirectX 11 game capture")
            CaptureSourceModeText.Text = _recording.IsRecording ? "DirectX 11 · recording" : "DirectX 11 game capture";
        else if (!busy && ActiveGameCaptureBackend is not null && GameCaptureFallbackReason is not null)
            CaptureSourceModeText.Text = "Windows capture · fallback";
        CaptureSourceIcon.Glyph = _selectedCaptureTarget?.Target.Kind == CaptureTargetKind.Monitor ? "\uE7F4" : "\uE737";
        CaptureSourceButton.IsEnabled = !busy && !_recording.IsRecording;
        ToolTipService.SetToolTip(CaptureSourceButton, $"{CaptureSourceDisplayName}\n{CaptureSourceDetail}");
        AutomationProperties.SetName(CaptureSourceButton, $"Capture source: {CaptureSourceDisplayName}. {CaptureSourceDetail}");
        if (!_clipMode.IsMonitoring && _mode == AppMode.Clip && _instantCaptureRequested)
        {
            StatusText.Text = _captureSourceError is not null ? "Capture unavailable · choose another source"
                : _selectedCaptureTarget is null && _settings.Current.CaptureSourceMode == "auto"
                    ? "Waiting for a game" : "Capture source unavailable";
            RecordButton.Content = "Cancel waiting";
        }
        if (!busy)
        {
            RecordingModeButton.IsEnabled = true;
            ClipModeButton.IsEnabled = true;
            RecordButton.IsEnabled = true;
        }
        else
        {
            RecordingModeButton.IsEnabled = false;
            ClipModeButton.IsEnabled = false;
            RecordButton.IsEnabled = false;
            SaveClipButton.IsEnabled = false;
        }
        RefreshSettingsSourceSummary();
        UpdateSettingsAudioTestButtons();
    }

    private async void OnCaptureSourceFlyoutOpening(object sender, object e)
    {
        try { await RefreshCaptureSourceInventoryAsync(); PopulateCaptureSourceChoices(); }
        catch (Exception ex) { CaptureSourceFlyoutHint.Text = $"Could not list capture sources: {ex.Message}"; }
    }

    private async void OnRefreshCaptureSourcesClicked(object sender, RoutedEventArgs e)
    {
        await RefreshCaptureSourceInventoryAsync();
        PopulateCaptureSourceChoices();
    }

    private void PopulateCaptureSourceChoices()
    {
        CaptureDisplayChoices.Children.Clear();
        CaptureWindowChoices.Children.Clear();
        void AddChoice(StackPanel panel, CaptureTargetInfo source)
        {
            var selected = source.Id == _selectedCaptureTarget?.Id;
            var label = new TextBlock { Text = source.DisplayName, FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis };
            var detail = new TextBlock { Text = source.Target.Kind == CaptureTargetKind.Monitor
                ? "Capture this whole display" : $"{source.ProcessName}{(source.IsGame ? " · game" : "")}",
                FontSize = 10, Foreground = (Brush)Application.Current.Resources["KlippTextSecondaryBrush"] };
            var content = new StackPanel { Spacing = 2 }; content.Children.Add(label); content.Children.Add(detail);
            var button = new Button { Content = content, Tag = source, Padding = new Thickness(10, 7, 10, 7),
                HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                BorderBrush = (Brush)Application.Current.Resources[selected ? "KlippAccentBrush" : "KlippBorderBrush"] };
            ToolTipService.SetToolTip(button, source.DisplayName);
            AutomationProperties.SetName(button, $"Capture {source.DisplayName}{(selected ? ", selected" : "")}");
            button.Click += OnManualCaptureSourceClicked;
            panel.Children.Add(button);
        }
        foreach (var display in _captureDisplays) AddChoice(CaptureDisplayChoices, display);
        foreach (var window in _captureWindows.Where(window => !window.IsMinimized || window.Id == _selectedCaptureTarget?.Id))
            AddChoice(CaptureWindowChoices, window);
        if (CaptureWindowChoices.Children.Count == 0)
            CaptureWindowChoices.Children.Add(new TextBlock { Text = "Open a game or application, then refresh.",
                FontSize = 11, TextWrapping = TextWrapping.Wrap });
        AutomaticCaptureSourceChoice.BorderBrush = (Brush)Application.Current.Resources[
            _settings.Current.CaptureSourceMode == "auto" ? "KlippAccentBrush" : "KlippBorderBrush"];
        RememberCaptureGameButton.Visibility = _selectedCaptureTarget is { IsGame: false, Target.Kind: CaptureTargetKind.Window }
            ? Visibility.Visible : Visibility.Collapsed;
        CaptureSourceFlyoutHint.Text = _captureSourceError ??
            "Switching sources starts a fresh instant-clip buffer. Manual recordings keep their source until stopped.";
    }

    private async void OnManualCaptureSourceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CaptureTargetInfo source } || _recording.IsRecording || _captureOperationGate.CurrentCount == 0) return;
        CaptureSourceButton.Flyout?.Hide();
        BeginCaptureSourcePreferenceChange();
        _settings.Current.CaptureSourceMode = source.Target.Kind == CaptureTargetKind.Monitor ? "desktop" : "window";
        if (source.Target.Kind == CaptureTargetKind.Monitor) _settings.Current.CaptureMonitorId = source.Id;
        else
        {
            _settings.Current.CaptureWindowProcessName = source.ProcessName;
            _settings.Current.CaptureWindowTitle = source.WindowTitle;
            _preferredManualWindowId = source.Id;
        }
        await _settings.SaveAsync();
        await ApplyCaptureSourcePreferencesAsync();
    }

    private async void OnAutomaticCaptureSourceClicked(object sender, RoutedEventArgs e)
    {
        if (_recording.IsRecording || _captureOperationGate.CurrentCount == 0) return;
        CaptureSourceButton.Flyout?.Hide();
        BeginCaptureSourcePreferenceChange();
        _settings.Current.CaptureSourceMode = "auto";
        _settings.Current.AutoDetectGames = true;
        await _settings.SaveAsync();
        await ApplyCaptureSourcePreferencesAsync();
    }

    private async void OnRememberCaptureGameClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedCaptureTarget is not { Target.Kind: CaptureTargetKind.Window } selected) return;
        if (!_settings.Current.CaptureGameProcessNames.Contains(selected.ProcessName, StringComparer.OrdinalIgnoreCase))
            _settings.Current.CaptureGameProcessNames.Add(selected.ProcessName.ToLowerInvariant());
        await _settings.SaveAsync();
        await RefreshCaptureSourceInventoryAsync();
        _selectedCaptureTarget = _captureWindows.FirstOrDefault(window => window.Id == selected.Id) ?? selected;
        PopulateCaptureSourceChoices();
        CaptureSourceFlyoutHint.Text = $"{selected.ProcessName} will be recognized in Automatic game mode.";
    }
}
