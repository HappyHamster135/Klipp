using System;
using System.Diagnostics;
using Klipp.Core.Models;
using Klipp.Desktop.Services;

namespace Klipp.Desktop;

public sealed partial class MainWindow
{
    private CaptureNotificationService? _captureNotifications;
    private bool _captureNotificationsClosed;

    private void ShowCaptureNotification(CaptureNotificationKind kind, string title, string detail)
    {
        if (_captureNotificationsClosed) return;
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => ShowCaptureNotification(kind, title, detail));
            return;
        }
        var settings = _settings.Current;
        var enabled = kind switch
        {
            CaptureNotificationKind.ClipSaved => settings.NotifyOnClipSaved,
            CaptureNotificationKind.RecordingStarted => settings.NotifyOnRecordingStarted,
            CaptureNotificationKind.RecordingStopped => settings.NotifyOnRecordingStopped,
            CaptureNotificationKind.AutomaticSourceChange => settings.NotifyOnAutomaticSourceChange,
            CaptureNotificationKind.RecordingBookmark => settings.NotifyOnRecordingBookmark,
            CaptureNotificationKind.AudioDeviceChanged => settings.NotifyOnAudioDeviceChange,
            CaptureNotificationKind.CaptureProtection => true,
            CaptureNotificationKind.Preview => true,
            _ => false
        };
        if (!enabled) return;
        try
        {
            _captureNotifications ??= new CaptureNotificationService(DispatcherQueue);
            _captureNotifications.Show(CaptureNotificationMessage.Create(kind, title, detail,
                DateTimeOffset.UtcNow, settings.CaptureNotificationDurationSeconds));
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Capture notification could not be shown: {exception}");
        }
    }

    private void CloseCaptureNotifications()
    {
        _captureNotificationsClosed = true;
        _captureNotifications?.Dispose();
        _captureNotifications = null;
    }
}
