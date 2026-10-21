using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;

namespace Klipp.Desktop;

public sealed partial class MainWindow
{
    public ObservableCollection<CaptureAudioMeterRow> CaptureAudioMeters { get; } = [];
    private DispatcherTimer? _captureAudioMeterTimer;
    private bool _captureMixerOpen;

    private void UpdateCaptureAudioMeterState()
    {
        var active = _recording.IsRecording || _clipMode.IsMonitoring;
        SettingsLiveAudioMetersCard.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        CaptureMixerButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        var visible = !_hiddenInTray && (_captureMixerOpen || _sideMenuOpen && _settingsPage == "audio");
        if (!active || !visible || _captureWindowClosing)
        {
            _captureAudioMeterTimer?.Stop();
            if (!active)
            {
                CaptureAudioMeters.Clear();
                CaptureMixerStatusText.Text = "Start capture to see live recording levels.";
            }
            return;
        }
        if (_captureAudioMeterTimer is null)
        {
            _captureAudioMeterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _captureAudioMeterTimer.Tick += (_, _) => UpdateCaptureAudioMeters();
        }
        UpdateCaptureAudioMeters();
        _captureAudioMeterTimer.Start();
    }

    private void UpdateCaptureAudioMeters()
    {
        if (_hiddenInTray || _captureWindowClosing || (!_captureMixerOpen && !(_sideMenuOpen && _settingsPage == "audio")))
        { _captureAudioMeterTimer?.Stop(); return; }
        var levels = _recording.IsRecording ? _recording.AudioLevels : _clipMode.AudioLevels;
        if (CaptureAudioMeters.Count != levels.Count || !CaptureAudioMeters.Select(row => row.Source).SequenceEqual(levels.Select(level => level.Source)))
        {
            CaptureAudioMeters.Clear();
            foreach (var level in levels) CaptureAudioMeters.Add(new CaptureAudioMeterRow(level.Source));
        }
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < levels.Count; index++) CaptureAudioMeters[index].Update(levels[index], now);
        CaptureMixerStatusText.Text = levels.Count == 0 ? "Audio is off for this capture." : "Live recording levels · lower a source if it clips.";
    }

    private void OnCaptureMixerOpened(object? sender, object args)
    { _captureMixerOpen = true; UpdateCaptureAudioMeterState(); }

    private void OnCaptureMixerClosed(object? sender, object args)
    { _captureMixerOpen = false; UpdateCaptureAudioMeterState(); }
}
