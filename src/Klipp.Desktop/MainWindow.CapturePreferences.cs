using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Klipp.Encoding.Audio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Klipp.Desktop;

public sealed partial class MainWindow
{
    private sealed record AudioAppChoice(string ProcessName, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
    private IReadOnlyList<ProcessAudioApplication> _audioApps = [];
    private bool _syncingAudioApps;
    private bool _refreshingAudioApps;

    private void SyncAdvancedCapturePreferences()
    {
        var settings = _settings.Current;
        SettingsEncoderModeSelector.SelectedItem = SettingsEncoderModeSelector.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag as string == settings.CaptureEncoderMode);
        SettingsAudioCaptureModeSelector.SelectedIndex = settings.AudioCaptureMode == "game" ? 1 : 0;
        SettingsDiscordAudioToggle.IsOn = settings.CaptureDiscordAudio;
        SettingsMusicAudioToggle.IsOn = settings.CaptureMusicAudio;
        SettingsDiscordGainSlider.Value = settings.DiscordAudioGain * 100;
        SettingsMusicGainSlider.Value = settings.MusicAudioGain * 100;
        SettingsDiscordGainSlider.Header = $"Discord recording level · {settings.DiscordAudioGain * 100:0}%";
        SettingsMusicGainSlider.Header = $"Music recording level · {settings.MusicAudioGain * 100:0}%";
        SettingsAudioLimiterToggle.IsOn = settings.AudioLimiterEnabled;
        PopulateAudioAppChoices();
        UpdateAudioRoutingControls();
    }

    private async void OnCaptureEncoderChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_uiReady || _syncingSettingsUi || sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string mode } }) return;
        _settings.Current.CaptureEncoderMode = mode;
        SettingsEncoderStatusText.Text = _recording.IsRecording || _clipMode.IsMonitoring
            ? "Encoder changes apply when capture next starts." : "Encoder preference saved.";
        await _settings.SaveAsync();
    }

    private async Task RefreshAudioAppChoicesAsync()
    {
        if (_refreshingAudioApps || _captureWindowClosing) return;
        _refreshingAudioApps = true;
        try
        {
            _audioApps = await Task.Run(AudioApplicationCatalog.GetApplications);
            if (!_captureWindowClosing) PopulateAudioAppChoices();
        }
        catch (Exception exception) { SettingsAudioRoutingStatusText.Text = $"Could not list apps: {exception.Message}"; }
        finally { _refreshingAudioApps = false; }
    }

    private void PopulateAudioAppChoices()
    {
        _syncingAudioApps = true;
        try
        {
            void Populate(ComboBox selector, string selected, string empty)
            {
                var choices = new List<AudioAppChoice> { new(string.Empty, empty) };
                choices.AddRange(_audioApps.Select(app => new AudioAppChoice(app.ProcessName, app.DisplayName)));
                if (selected.Length > 0 && !choices.Any(choice => string.Equals(choice.ProcessName, selected, StringComparison.OrdinalIgnoreCase)))
                    choices.Add(new(selected, $"{selected} · waiting for app"));
                selector.ItemsSource = choices;
                selector.SelectedItem = choices.First(choice => string.Equals(choice.ProcessName, selected, StringComparison.OrdinalIgnoreCase));
            }
            Populate(SettingsGameAudioAppSelector, _settings.Current.GameAudioProcessName, "Follow captured game / window");
            Populate(SettingsDiscordAppSelector, _settings.Current.DiscordProcessName, "Choose an app");
            Populate(SettingsMusicAppSelector, _settings.Current.MusicProcessName, "Choose an app");
        }
        finally { _syncingAudioApps = false; }
    }

    private void UpdateAudioRoutingControls()
    {
        var gameOnly = _settings.Current.AudioCaptureMode == "game";
        var supported = ProcessAudioCaptureSupport.IsSupported;
        SettingsAppAudioSupportText.Text = gameOnly ? ProcessAudioCaptureSupport.Description : "Capture all apps together, or select Game only to keep game, Discord and music separate.";
        SettingsGameAudioAppPanel.Visibility = gameOnly ? Visibility.Visible : Visibility.Collapsed;
        SettingsSeparateAppsCard.Visibility = gameOnly ? Visibility.Visible : Visibility.Collapsed;
        SettingsDesktopAudioDevicePanel.Visibility = gameOnly ? Visibility.Collapsed : Visibility.Visible;
        SettingsDesktopAudioTestPanel.Visibility = gameOnly ? Visibility.Collapsed : Visibility.Visible;
        SettingsDesktopAudioTestNote.Visibility = gameOnly ? Visibility.Collapsed : Visibility.Visible;
        SettingsMainAudioTitle.Text = gameOnly ? "Record game audio" : "Record desktop audio";
        SettingsMainAudioDescription.Text = gameOnly ? "Capture only the selected game's sound." : "Include sound from all apps on the selected output.";
        SettingsGameAudioAppSelector.IsEnabled = supported;
        SettingsDiscordAudioToggle.IsEnabled = supported && _settings.Current.DiscordProcessName.Length > 0;
        SettingsMusicAudioToggle.IsEnabled = supported && _settings.Current.MusicProcessName.Length > 0;
        SettingsDiscordGainSlider.IsEnabled = supported && _settings.Current.CaptureDiscordAudio;
        SettingsMusicGainSlider.IsEnabled = supported && _settings.Current.CaptureMusicAudio;
        SettingsAudioRoutingStatusText.Text = AudioRoutingProblem() ?? (!supported ? ProcessAudioCaptureSupport.Description
            : _settings.Current.MusicProcessName.Length == 0 ? "Open your music app, refresh the list, then select it to add a music track."
            : "App choices are remembered. A closed app stays silent until it returns.");
    }

    private string? AudioRoutingProblem()
    {
        var settings = _settings.Current;
        if (settings.AudioCaptureMode != "game") return null;
        if (!(settings.CaptureSystemAudio || settings.CaptureDiscordAudio || settings.CaptureMusicAudio)) return null;
        if (!ProcessAudioCaptureSupport.IsSupported) return ProcessAudioCaptureSupport.Description;
        var game = settings.GameAudioProcessName.Length > 0 ? settings.GameAudioProcessName : _selectedCaptureTarget?.ProcessName ?? string.Empty;
        if (settings.CaptureSystemAudio && settings.GameAudioProcessName.Length == 0 &&
            _selectedCaptureTarget?.Target.Kind == Klipp.Capture.Models.CaptureTargetKind.Monitor)
            return "Choose the game audio app when recording a whole display.";
        if (settings.CaptureDiscordAudio && settings.DiscordProcessName.Length == 0 || settings.CaptureMusicAudio && settings.MusicProcessName.Length == 0)
            return "Choose an app for each enabled audio track.";
        var names = new List<string>();
        if (settings.CaptureSystemAudio && game.Length > 0) names.Add(game);
        if (settings.CaptureDiscordAudio) names.Add(settings.DiscordProcessName);
        if (settings.CaptureMusicAudio) names.Add(settings.MusicProcessName);
        return names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count
            ? "Choose different apps for game, Discord and music. The same app cannot be recorded twice." : null;
    }

    private async void OnAudioCaptureModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_uiReady || _syncingSettingsUi || sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: string mode } }) return;
        _settings.Current.AudioCaptureMode = mode;
        if (mode == "desktop")
        {
            _settings.Current.CaptureDiscordAudio = false;
            _settings.Current.CaptureMusicAudio = false;
            var previous = _syncingSettingsUi;
            _syncingSettingsUi = true;
            try { SettingsDiscordAudioToggle.IsOn = false; SettingsMusicAudioToggle.IsOn = false; }
            finally { _syncingSettingsUi = previous; }
        }
        StopSettingsAudioTests();
        UpdateAudioRoutingControls();
        RefreshSettingsAudioSummary();
        await _settings.SaveAsync();
        if (mode == "game") await RefreshAudioAppChoicesAsync();
    }

    private async void OnAudioAppSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_uiReady || _syncingSettingsUi || _syncingAudioApps || sender is not ComboBox { SelectedItem: AudioAppChoice choice }) return;
        if (ReferenceEquals(sender, SettingsGameAudioAppSelector)) _settings.Current.GameAudioProcessName = choice.ProcessName;
        else if (ReferenceEquals(sender, SettingsDiscordAppSelector)) _settings.Current.DiscordProcessName = choice.ProcessName;
        else _settings.Current.MusicProcessName = choice.ProcessName;
        if (choice.ProcessName.Length == 0)
        {
            var previous = _syncingSettingsUi;
            _syncingSettingsUi = true;
            try
            {
                if (ReferenceEquals(sender, SettingsDiscordAppSelector))
                { _settings.Current.CaptureDiscordAudio = false; SettingsDiscordAudioToggle.IsOn = false; }
                if (ReferenceEquals(sender, SettingsMusicAppSelector))
                { _settings.Current.CaptureMusicAudio = false; SettingsMusicAudioToggle.IsOn = false; }
            }
            finally { _syncingSettingsUi = previous; }
        }
        UpdateAudioRoutingControls();
        RefreshSettingsAudioSummary();
        await _settings.SaveAsync();
    }

    private async void OnExtraAudioSourceToggled(object sender, RoutedEventArgs args)
    {
        if (!_uiReady || _syncingSettingsUi) return;
        _settings.Current.CaptureDiscordAudio = SettingsDiscordAudioToggle.IsOn;
        _settings.Current.CaptureMusicAudio = SettingsMusicAudioToggle.IsOn;
        UpdateAudioRoutingControls();
        RefreshSettingsAudioSummary();
        await _settings.SaveAsync();
    }

    private async void OnExtraAudioGainChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_uiReady || _syncingSettingsUi || !double.IsFinite(args.NewValue)) return;
        if (ReferenceEquals(sender, SettingsDiscordGainSlider))
        {
            _settings.Current.DiscordAudioGain = args.NewValue / 100;
            SettingsDiscordGainSlider.Header = $"Discord recording level · {args.NewValue:0}%";
        }
        else
        {
            _settings.Current.MusicAudioGain = args.NewValue / 100;
            SettingsMusicGainSlider.Header = $"Music recording level · {args.NewValue:0}%";
        }
        await _settings.SaveAsync();
    }

    private async void OnAudioLimiterToggled(object sender, RoutedEventArgs args)
    {
        if (!_uiReady || _syncingSettingsUi) return;
        _settings.Current.AudioLimiterEnabled = SettingsAudioLimiterToggle.IsOn;
        await _settings.SaveAsync();
    }

    private async void OnRefreshAudioAppsClicked(object sender, RoutedEventArgs args) => await RefreshAudioAppChoicesAsync();
}
