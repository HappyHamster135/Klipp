using System;
using Klipp.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Klipp.Desktop;

public sealed partial class MainWindow
{
    private string _settingsPage = "capture";

    private void SyncSettingsExperience()
    {
        if (SettingsSourceModeSelector is null) return;
        var wasSyncing = _syncingSettingsUi;
        _syncingSettingsUi = true;
        try
        {
            var settings = _settings.Current;
            settings.NormalizeCapturePreferences();
            SettingsSourceModeSelector.SelectedIndex = settings.CaptureSourceMode switch
            {
                "desktop" => 1,
                "window" => 2,
                _ => 0
            };
            SettingsAutoDetectGamesToggle.IsOn = settings.AutoDetectGames;
            SettingsAutoDetectGamesToggle.IsEnabled = settings.CaptureSourceMode == "auto";
            SettingsCaptureCursorToggle.IsOn = settings.CaptureCursor;
            SettingsExclusiveGameCaptureToggle.IsOn = settings.EnableExclusiveGameCapture;
            SettingsSystemAudioToggle.IsOn = settings.CaptureSystemAudio;
            SettingsLowDiskProtectionToggle.IsOn = settings.LowDiskProtectionEnabled;
            SyncAdvancedCapturePreferences();
            SyncSettingsAudioExperience();
            SettingsNotifyClipSavedToggle.IsOn = settings.NotifyOnClipSaved;
            SettingsNotifyRecordingStartedToggle.IsOn = settings.NotifyOnRecordingStarted;
            SettingsNotifyRecordingStoppedToggle.IsOn = settings.NotifyOnRecordingStopped;
            SettingsNotifySourceChangeToggle.IsOn = settings.NotifyOnAutomaticSourceChange;
            SettingsNotifyBookmarkToggle.IsOn = settings.NotifyOnRecordingBookmark;
            SettingsNotifyAudioDeviceToggle.IsOn = settings.NotifyOnAudioDeviceChange;
            SettingsNotificationDurationBox.Value = settings.CaptureNotificationDurationSeconds;
            SyncUpdateSettings();
            RefreshSettingsSourceSummary();
            RefreshSettingsAudioSummary();
            SelectSettingsPage(_settingsPage);
            UpdateSettingsDrawerWidth(SideMenuOverlay.ActualWidth);
        }
        finally
        {
            _syncingSettingsUi = wasSyncing;
        }
    }

    private void RefreshSettingsSourceSummary()
    {
        if (SettingsSourceNameText is null) return;
        SettingsSourceNameText.Text = CaptureSourceDisplayName;
        SettingsSourceDetailText.Text = CaptureSourceDetail;
        SettingsSourceModeSelector.IsEnabled = _captureSessionReady && !_recording.IsRecording;
        SettingsAutoDetectGamesToggle.IsEnabled = _captureSessionReady && !_recording.IsRecording &&
            _settings.Current.CaptureSourceMode == "auto";
    }

    private void RefreshSettingsAudioSummary()
    {
        var settings = _settings.Current;
        var sources = new System.Collections.Generic.List<string>();
        if (settings.CaptureSystemAudio) sources.Add(settings.AudioCaptureMode == "game" ? "game audio" : "desktop audio");
        if (settings.CaptureMicrophone) sources.Add("microphone");
        if (settings.AudioCaptureMode == "game" && settings.CaptureDiscordAudio) sources.Add("Discord");
        if (settings.AudioCaptureMode == "game" && settings.CaptureMusicAudio) sources.Add("music");
        SettingsAudioSummaryText.Text = sources.Count == 0 ? "New captures will be silent. Enable an audio source to include sound."
            : $"New captures include {string.Join(", ", sources)}. Changes apply when capture next starts.";
    }

    private void OnSettingsOverlaySizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateSettingsDrawerWidth(e.NewSize.Width);

    private void UpdateSettingsDrawerWidth(double availableWidth)
    {
        if (SettingsDrawerColumn is null || SettingsNavigationColumn is null || availableWidth <= 0) return;
        // Keep room to dismiss against the backdrop, and retain useful content
        // width when the window is smaller than the preferred drawer size.
        var width = Math.Min(720, Math.Max(0, availableWidth - (availableWidth >= 600 ? 40 : 0)));
        SettingsDrawerColumn.Width = new GridLength(width);
        SettingsNavigationColumn.Width = new GridLength(width < 640 ? 132 : 154);
    }

    private void OnSettingsPageClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string page }) return;
        StopSettingsShortcutCapture();
        SelectSettingsPage(page);
    }

    private void SelectSettingsPage(string page)
    {
        if (page != "audio") StopSettingsAudioTests();
        _settingsPage = page;
        (string Id, Button Button, FrameworkElement Page)[] sections =
        [
            ("capture", SettingsCaptureNavButton, SettingsCapturePage),
            ("audio", SettingsAudioNavButton, SettingsAudioPage),
            ("storage", SettingsStorageNavButton, SettingsStoragePage),
            ("shortcuts", SettingsShortcutsNavButton, SettingsShortcutsPage),
            ("notifications", SettingsNotificationsNavButton, SettingsNotificationsPage),
            ("updates", SettingsUpdatesNavButton, SettingsUpdatesPage)
        ];
        foreach (var section in sections)
        {
            var selected = section.Id == page;
            section.Page.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            section.Button.Background = selected
                ? (Brush)Application.Current.Resources["KlippSurfaceElevatedBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            section.Button.Foreground = (Brush)Application.Current.Resources[
                selected ? "KlippTextPrimaryBrush" : "KlippTextSecondaryBrush"];
            AutomationProperties.SetName(section.Button,
                $"{section.Button.Content} settings{(selected ? ", selected" : string.Empty)}");
        }
        if (page == "audio")
        {
            _ = RefreshSettingsAudioDevicesAsync();
            _ = RefreshAudioAppChoicesAsync();
        }
        UpdateCaptureAudioMeterState();
    }

    private void StopSettingsShortcutCapture()
    {
        StopBookmarkHotkeyCapture();
        StopSettingsAudioTests();
        if (_capturingHotkey) StopHotkeyCapture();
        StopEditorShortcutCapture();
    }

    private async void OnSettingsSourceModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi ||
            SettingsSourceModeSelector.SelectedItem is not ComboBoxItem { Tag: string mode }) return;
        BeginCaptureSourcePreferenceChange();
        _settings.Current.CaptureSourceMode = mode;
        SettingsAutoDetectGamesToggle.IsEnabled = mode == "auto";
        await _settings.SaveAsync();
        await ApplyCaptureSourcePreferencesAsync();
        RefreshSettingsSourceSummary();
    }

    private async void OnSettingsAutoDetectGamesToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi) return;
        BeginCaptureSourcePreferenceChange();
        _settings.Current.AutoDetectGames = SettingsAutoDetectGamesToggle.IsOn;
        await _settings.SaveAsync();
        await ApplyCaptureSourcePreferencesAsync();
        RefreshSettingsSourceSummary();
    }

    private async void OnSettingsCaptureOptionToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi) return;
        if (ReferenceEquals(sender, SettingsSystemAudioToggle))
            _settings.Current.CaptureSystemAudio = SettingsSystemAudioToggle.IsOn;
        else if (ReferenceEquals(sender, SettingsCaptureCursorToggle))
            _settings.Current.CaptureCursor = SettingsCaptureCursorToggle.IsOn;
        else return;
        RefreshSettingsAudioSummary();
        await _settings.SaveAsync();
        StatusText.Text = _recording.IsRecording || _clipMode.IsMonitoring
            ? "Capture preferences apply when capture next starts"
            : "Capture preferences saved";
    }

    private async void OnSettingsExclusiveGameCaptureToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi) return;
        _settings.Current.EnableExclusiveGameCapture = SettingsExclusiveGameCaptureToggle.IsOn;
        await _settings.SaveAsync();
        StatusText.Text = _recording.IsRecording || _clipMode.IsMonitoring
            ? "Exclusive game capture applies when capture next starts"
            : "Exclusive game capture preference saved";
    }

    private async void OnSettingsNotificationToggled(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi) return;
        var settings = _settings.Current;
        settings.NotifyOnClipSaved = SettingsNotifyClipSavedToggle.IsOn;
        settings.NotifyOnRecordingStarted = SettingsNotifyRecordingStartedToggle.IsOn;
        settings.NotifyOnRecordingStopped = SettingsNotifyRecordingStoppedToggle.IsOn;
        settings.NotifyOnAutomaticSourceChange = SettingsNotifySourceChangeToggle.IsOn;
        settings.NotifyOnRecordingBookmark = SettingsNotifyBookmarkToggle.IsOn;
        settings.NotifyOnAudioDeviceChange = SettingsNotifyAudioDeviceToggle.IsOn;
        await _settings.SaveAsync();
    }

    private async void OnSettingsNotificationDurationChanged(NumberBox sender, NumberBoxValueChangedEventArgs e)
    {
        if (!_uiReady || _syncingSettingsUi) return;
        if (!double.IsFinite(e.NewValue))
        {
            var wasSyncing = _syncingSettingsUi;
            _syncingSettingsUi = true;
            try { sender.Value = _settings.Current.CaptureNotificationDurationSeconds; }
            finally { _syncingSettingsUi = wasSyncing; }
            return;
        }
        var duration = (int)Math.Clamp(Math.Round(e.NewValue), 2, 10);
        _settings.Current.CaptureNotificationDurationSeconds = duration;
        if (sender.Value != duration)
        {
            var wasSyncing = _syncingSettingsUi;
            _syncingSettingsUi = true;
            try { sender.Value = duration; }
            finally { _syncingSettingsUi = wasSyncing; }
        }
        await _settings.SaveAsync();
    }

    private void OnSettingsTestNotificationClicked(object sender, RoutedEventArgs e) =>
        ShowCaptureNotification(CaptureNotificationKind.Preview, "Klipp notifications", "Your capture confirmations will appear here.");

    private void OnSettingsCaptureHotkeyClicked(object sender, RoutedEventArgs e) =>
        OnHotkeyButtonClicked(sender, e);

    private async void OnSettingsWindowsSoundClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(new Uri("ms-settings:sound")))
                await ShowErrorAsync("Sound settings unavailable", "Open Windows Settings and choose System > Sound.");
        }
        catch (Exception exception)
        {
            await ShowErrorAsync("Could not open sound settings", exception.Message);
        }
    }
}
