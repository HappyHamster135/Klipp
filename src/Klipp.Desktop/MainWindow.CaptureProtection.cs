using System;
using System.IO;
using System.Threading.Tasks;
using Klipp.Core.Capture;
using Klipp.Core.Models;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;

namespace Klipp.Desktop;

public sealed partial class MainWindow
{
    private readonly CaptureDiskProtectionService _diskProtection = new();
    private bool _checkingDiskProtection;
    private bool _stoppingForLowDisk;

    private async Task CheckCaptureDiskProtectionAsync()
    {
        if (_checkingDiskProtection || _stoppingForLowDisk || _captureWindowClosing ||
            !_settings.Current.LowDiskProtectionEnabled || (!_recording.IsRecording && !_clipMode.IsMonitoring)) return;
        _checkingDiskProtection = true;
        try
        {
            var manual = _recording.IsRecording;
            var checkedCapture = _activeCaptureOptions;
            var recordingPath = _recording.ActiveOutputPath;
            var options = _activeCaptureOptions ?? GetConfiguredCaptureOptions();
            var sourceCount = (options.CaptureSystemAudio ? 1 : 0) + (options.CaptureMicrophone ? 1 : 0) +
                (options.CaptureDiscordAudio ? 1 : 0) + (options.CaptureMusicAudio ? 1 : 0);
            var seconds = _recording.RecordedSeconds;
            var clipLength = GetSelectedClipLengthSeconds();
            var output = manual && recordingPath is not null ? recordingPath : LibraryService.ClipsDirectory;
            var temporary = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klipp", "temp");
            var check = await Task.Run(() =>
            {
                var sizes = manual ? _recording.GetPendingMediaSizes() : (VideoBytes: 0L, AudioBytes: 0L);
                var reserve = CaptureDiskProtectionPolicy.CalculateReserve(manual, sizes.VideoBytes, sizes.AudioBytes,
                    seconds, sourceCount, options.BitrateMbps, clipLength);
                return _diskProtection.Inspect(temporary, output, reserve);
            });
            if (!check.ShouldStop || _captureWindowClosing || !_settings.Current.LowDiskProtectionEnabled ||
                manual != _recording.IsRecording || (!_recording.IsRecording && !_clipMode.IsMonitoring)) return;
            // Wait for the current save/source operation, then recheck before
            // stopping; the user may already have ended capture while we waited.
            await _captureOperationGate.WaitAsync();
            try
            {
                if (_captureWindowClosing || !_settings.Current.LowDiskProtectionEnabled ||
                    !ReferenceEquals(checkedCapture, _activeCaptureOptions) ||
                    manual != _recording.IsRecording || manual && recordingPath != _recording.ActiveOutputPath ||
                    (!_recording.IsRecording && !_clipMode.IsMonitoring)) return;
                output = manual && recordingPath is not null ? recordingPath : LibraryService.ClipsDirectory;
                var confirmed = await Task.Run(() =>
                {
                    var sizes = manual ? _recording.GetPendingMediaSizes() : (VideoBytes: 0L, AudioBytes: 0L);
                    var reserve = CaptureDiskProtectionPolicy.CalculateReserve(manual, sizes.VideoBytes, sizes.AudioBytes,
                        _recording.RecordedSeconds, sourceCount, options.BitrateMbps, clipLength);
                    return _diskProtection.Inspect(temporary, output, reserve);
                });
                if (!confirmed.ShouldStop || _captureWindowClosing || !_settings.Current.LowDiskProtectionEnabled ||
                    !ReferenceEquals(checkedCapture, _activeCaptureOptions) ||
                    manual != _recording.IsRecording || manual && recordingPath != _recording.ActiveOutputPath ||
                    !string.Equals(output, manual ? _recording.ActiveOutputPath : LibraryService.ClipsDirectory, StringComparison.OrdinalIgnoreCase)) return;
                _stoppingForLowDisk = true;
                _instantCaptureRequested = false;
                string? saved = null;
                string? error = null;
                if (_recording.IsRecording)
                {
                    try { saved = await RunServiceOperationAsync(_recording.StopAsync); }
                    catch (Exception exception) { error = exception.Message; }
                }
                else if (_clipMode.IsMonitoring)
                {
                    try
                    {
                        var emergencyPath = LibraryService.GenerateClipPath();
                        var result = await RunServiceOperationAsync(() => _clipMode.SaveClipAsync(emergencyPath, clipLength));
                        saved = result.OutputPath;
                    }
                    catch (Exception exception) { error = exception.Message; }
                    finally
                    {
                        try
                        {
                            if (saved is null) await RunServiceOperationAsync(_clipMode.StopAndPreserveBufferAsync);
                            else await RunServiceOperationAsync(_clipMode.StopMonitoringAsync);
                        }
                        catch (Exception exception) { error = (error is null ? string.Empty : error + " ") + exception.Message; }
                    }
                }
                _statusTimer.Stop();
                if (saved is not null) LibraryService.AddNewClip(saved);
                LibraryService.ResumeBackgroundMetadata();
                UpdateStatusUI();
                CaptureSafetyInfoBar.IsOpen = true;
                CaptureSafetyInfoBar.Visibility = Visibility.Visible;
                CaptureSafetyInfoBar.Title = saved is not null ? "Capture saved · disk space low" : "Capture stopped · disk space low";
                CaptureSafetyInfoBar.Message = saved is not null
                    ? $"{Path.GetFileName(saved)} was saved. Free up space before recording again."
                    : error is not null ? "Saving needs attention. Review recording recovery in Storage settings. " + error
                        : check.Detail;
                ShowCaptureNotification(CaptureNotificationKind.CaptureProtection, CaptureSafetyInfoBar.Title, CaptureSafetyInfoBar.Message);
                await RefreshRecordingRecoveryAsync();
            }
            finally { _stoppingForLowDisk = false; _captureOperationGate.Release(); }
        }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine($"Capture disk protection: {exception}"); }
        finally { _checkingDiskProtection = false; }
    }

    private async void OnLowDiskProtectionToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi) return;
        _settings.Current.LowDiskProtectionEnabled = SettingsLowDiskProtectionToggle.IsOn;
        await _settings.SaveAsync();
        await CheckCaptureDiskProtectionAsync();
    }

    private void OnLibraryNoticeClosed(Microsoft.UI.Xaml.Controls.InfoBar sender, Microsoft.UI.Xaml.Controls.InfoBarClosedEventArgs args)
    {
        if (!sender.IsOpen) sender.Visibility = Visibility.Collapsed;
    }
}
