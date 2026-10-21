using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Klipp.Desktop.Controls;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;

namespace Klipp.Desktop;

public sealed partial class MainWindow
{
    private TrayIconService? _trayIcon;
    private bool _hiddenInTray;
    private bool _trayExitPending;

    private void InitializeTrayIcon()
    {
        if (_appWindow is null) return;
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _trayIcon = new TrayIconService(hwnd, Path.Combine(AppContext.BaseDirectory, "Assets", "Klipp.ico"),
                MinimizeToTray, GetTrayMenuState, QueueTrayCommand,
                () => DispatcherQueue.TryEnqueue(() => RestoreFromTray(activate: false)));
            UpdateTrayStatus();
        }
        catch (Exception exception)
        {
            // Normal taskbar minimization remains available if the shell/icon is unavailable.
            Debug.WriteLine($"[Klipp] Tray icon unavailable: {exception.Message}");
        }
    }

    private bool MinimizeToTray()
    {
        if (_hiddenInTray) return true;
        if (_trayExitPending || _captureWindowClosing || _appWindow is null || _trayIcon?.EnsureRegistered() != true)
            return false;
        try
        {
            _hiddenInTray = true;
            ClipCard.PauseActivePreview();
            _playerView?.PausePlayback();
            _editorView?.PausePlayback();
            _appWindow.IsShownInSwitchers = false;
            _appWindow.Hide();
            return true;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[Klipp] Could not minimize to tray: {exception.Message}");
            _hiddenInTray = false;
            try { _appWindow.IsShownInSwitchers = true; _appWindow.Show(); } catch { }
            return false;
        }
    }

    internal void RestoreFromTray(bool activate = true)
    {
        if (_appWindow is null || _captureWindowClosing && !_trayExitPending) return;
        _hiddenInTray = false;
        UpdateCaptureAudioMeterState();
        _appWindow.IsShownInSwitchers = true;
        // SC_MINIMIZE is consumed before the presenter changes, preserving a
        // maximized window. Programmatic native minimization needs SW_RESTORE.
        if (activate)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (IsIconicForTray(hwnd)) ShowWindowForTray(hwnd, 9);
        }
        _appWindow.Show(activate);
        if (activate) Activate();
    }

    private TrayMenuState GetTrayMenuState()
    {
        var busy = _trayExitPending || _isSwitchingMode || _captureOperationGate.CurrentCount == 0;
        var status = _trayExitPending ? "Finishing capture…"
            : _recording.IsRecording ? $"Recording · {FormatCompactDuration(TimeSpan.FromSeconds(_recording.RecordedSeconds))}"
            : _clipMode.IsMonitoring ? $"Instant clips active · {GetSelectedClipLengthSeconds()}s"
            : _instantCaptureRequested ? CaptureSourceDisplayName : "Capture paused";
        return new TrayMenuState(status, $"Save last {GetSelectedClipLengthSeconds()} seconds",
            !busy && _mode == AppMode.Clip && _clipMode.IsMonitoring && SaveClipButton.IsEnabled,
            !busy && (_recording.IsRecording || _clipMode.IsMonitoring || _instantCaptureRequested), !_trayExitPending);
    }

    private void UpdateTrayStatus()
    {
        if (_trayIcon is null) return;
        _trayIcon.UpdateTooltip("Klipp · " + GetTrayMenuState().Status);
    }

    private void QueueTrayCommand(TrayCommand command) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_captureWindowClosing && !_trayExitPending) return;
        switch (command)
        {
            case TrayCommand.Open: RestoreFromTray(); break;
            case TrayCommand.SaveClip:
                if (GetTrayMenuState().CanSaveClip) OnSaveClipClicked(this, new RoutedEventArgs());
                break;
            case TrayCommand.StopCapture:
                if (GetTrayMenuState().CanStopCapture) OnRecordClicked(this, new RoutedEventArgs());
                break;
            case TrayCommand.Exit: ExitFromTray(); break;
        }
    });

    private async void ExitFromTray()
    {
        if (_trayExitPending) return;
        _trayExitPending = true;
        _captureWindowClosing = true;
        _captureSourceTimer?.Stop();
        _instantCaptureRequested = false;
        UpdateTrayStatus();
        await _captureOperationGate.WaitAsync();
        try
        {
            if (_recording.IsRecording)
            {
                var outputPath = await RunServiceOperationAsync(_recording.StopAsync);
                if (outputPath is not null) LibraryService.AddNewClip(outputPath);
            }
            if (_clipMode.IsMonitoring)
                await RunServiceOperationAsync(_clipMode.StopMonitoringAsync);
            _editorView?.Stop();
            Close();
        }
        catch (Exception exception)
        {
            _captureWindowClosing = false;
            _trayExitPending = false;
            _captureSourceTimer?.Start();
            RestoreFromTray();
            UpdateStatusUI();
            await ShowErrorAsync("Could not finish capture", exception.Message);
            await RefreshRecordingRecoveryAsync();
        }
        finally { _captureOperationGate.Release(); }
    }

    private void DisposeTrayIcon()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    [DllImport("user32.dll", EntryPoint = "IsIconic")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconicForTray(nint window);
    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindowForTray(nint window, int command);
}
