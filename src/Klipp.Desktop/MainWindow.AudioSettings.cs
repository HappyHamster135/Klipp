using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Klipp.Desktop;

public sealed partial class MainWindow
{
    private AudioDeviceSettingsService? _audioDeviceTests;
    private AudioDeviceInventory? _audioDeviceInventory;
    private DispatcherTimer? _audioLevelTimer;
    private CancellationTokenSource? _audioToneCancellation;
    private CancellationTokenSource? _audioLevelCancellation;
    private bool _syncingAudioDevices;
    private bool _audioLevelTestActive;
    private bool _audioLevelTestMicrophone;
    private bool _audioTonePlaying;
    private bool _applyingAudioSettings;
    private int _audioDeviceRefreshVersion;
    private int _audioTestVersion;
    private DateTimeOffset _audioLevelDeadline;

    private void SyncSettingsAudioExperience()
    {
        if (SettingsMicrophoneToggle is null) return;
        SettingsMicrophoneToggle.IsOn = _settings.Current.CaptureMicrophone;
        SettingsSystemAudioGainSlider.Value = _settings.Current.SystemAudioGain * 100;
        SettingsMicrophoneGainSlider.Value = _settings.Current.MicrophoneGain * 100;
        SettingsSystemAudioGainText.Text = $"{SettingsSystemAudioGainSlider.Value:0}%";
        SettingsMicrophoneGainText.Text = $"{SettingsMicrophoneGainSlider.Value:0}%";
        PopulateSettingsAudioDevices();
    }

    private async Task RefreshSettingsAudioDevicesAsync()
    {
        if (_captureWindowClosing) return;
        var version = ++_audioDeviceRefreshVersion;
        SettingsRefreshAudioDevicesButton.IsEnabled = false;
        try
        {
            var inventory = await Task.Run(AudioDeviceSettingsService.GetDevices);
            if (_captureWindowClosing || version != _audioDeviceRefreshVersion) return;
            _audioDeviceInventory = inventory;
            PopulateSettingsAudioDevices();
        }
        catch (Exception exception)
        {
            if (_captureWindowClosing || version != _audioDeviceRefreshVersion) return;
            SettingsAudioTestStatus.Text = $"Could not list audio devices: {exception.Message}";
        }
        finally
        {
            if (!_captureWindowClosing && version == _audioDeviceRefreshVersion)
                SettingsRefreshAudioDevicesButton.IsEnabled = true;
        }
    }

    private void PopulateSettingsAudioDevices()
    {
        if (SettingsSystemAudioDeviceSelector is null) return;
        _syncingAudioDevices = true;
        try
        {
            static List<AudioDeviceChoice> Choices(IReadOnlyList<AudioDeviceChoice>? connected,
                string? defaultName, string selectedId)
            {
                var choices = new List<AudioDeviceChoice>
                {
                    new(string.Empty, connected is null ? "Windows default · checking devices…" :
                        $"Windows default · {(string.IsNullOrEmpty(defaultName) ? "no device connected" : defaultName)}",
                        connected is not null && !string.IsNullOrEmpty(defaultName))
                };
                if (connected is not null) choices.AddRange(connected);
                if (!string.IsNullOrEmpty(selectedId) && !choices.Any(choice => string.Equals(choice.Id, selectedId, StringComparison.OrdinalIgnoreCase)))
                    choices.Add(new AudioDeviceChoice(selectedId, "Previously selected device · disconnected", false));
                return choices;
            }
            var outputs = Choices(_audioDeviceInventory?.Outputs, _audioDeviceInventory?.DefaultOutputName,
                _settings.Current.SystemAudioDeviceId);
            var inputs = Choices(_audioDeviceInventory?.Inputs, _audioDeviceInventory?.DefaultInputName,
                _settings.Current.MicrophoneDeviceId);
            SettingsSystemAudioDeviceSelector.ItemsSource = outputs;
            SettingsSystemAudioDeviceSelector.SelectedItem = outputs.First(choice => string.Equals(choice.Id, _settings.Current.SystemAudioDeviceId, StringComparison.OrdinalIgnoreCase));
            SettingsMicrophoneDeviceSelector.ItemsSource = inputs;
            SettingsMicrophoneDeviceSelector.SelectedItem = inputs.First(choice => string.Equals(choice.Id, _settings.Current.MicrophoneDeviceId, StringComparison.OrdinalIgnoreCase));
            UpdateSettingsAudioDeviceHints();
            UpdateSettingsAudioTestButtons();
        }
        finally { _syncingAudioDevices = false; }
    }

    private void UpdateSettingsAudioDeviceHints()
    {
        static string Hint(object? selection, bool microphone) => selection is AudioDeviceChoice { IsAvailable: false }
            ? "This device is unavailable. Connect it or choose another device."
            : selection is AudioDeviceChoice { Id.Length: 0 }
                ? microphone ? "Uses the Windows communications input selected when capture starts."
                    : "Uses the Windows default output selected when capture starts."
                : "This device stays selected even when the Windows default changes.";
        SettingsSystemAudioDeviceHint.Text = Hint(SettingsSystemAudioDeviceSelector.SelectedItem, false);
        SettingsMicrophoneDeviceHint.Text = Hint(SettingsMicrophoneDeviceSelector.SelectedItem, true);
    }

    private async void OnSettingsAudioDeviceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_uiReady || _syncingSettingsUi || _syncingAudioDevices ||
            sender is not ComboBox { SelectedItem: AudioDeviceChoice choice }) return;
        StopSettingsAudioTests();
        if (ReferenceEquals(sender, SettingsSystemAudioDeviceSelector)) _settings.Current.SystemAudioDeviceId = choice.Id;
        else _settings.Current.MicrophoneDeviceId = choice.Id;
        UpdateSettingsAudioDeviceHints();
        RefreshSettingsAudioSummary();
        UpdateSettingsAudioTestButtons();
        await _settings.SaveAsync();
    }

    private async void OnSettingsMicrophoneToggled(object sender, RoutedEventArgs args)
    {
        if (!_uiReady || _syncingSettingsUi) return;
        _settings.Current.CaptureMicrophone = SettingsMicrophoneToggle.IsOn;
        RefreshSettingsAudioSummary();
        await _settings.SaveAsync();
    }

    private async void OnSettingsAudioGainChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (!_uiReady || _syncingSettingsUi || !double.IsFinite(args.NewValue)) return;
        if (ReferenceEquals(sender, SettingsSystemAudioGainSlider))
        {
            _settings.Current.SystemAudioGain = Math.Clamp(args.NewValue / 100, 0, 2);
            SettingsSystemAudioGainText.Text = $"{args.NewValue:0}%";
        }
        else
        {
            _settings.Current.MicrophoneGain = Math.Clamp(args.NewValue / 100, 0, 2);
            SettingsMicrophoneGainText.Text = $"{args.NewValue:0}%";
        }
        await _settings.SaveAsync();
    }

    private async void OnSettingsRefreshAudioDevicesClicked(object sender, RoutedEventArgs args)
    {
        StopSettingsAudioTests();
        await RefreshSettingsAudioDevicesAsync();
    }

    private async void OnSettingsPlayTestSoundClicked(object sender, RoutedEventArgs args)
    {
        if (_audioTonePlaying || _applyingAudioSettings || _captureWindowClosing ||
            SettingsSystemAudioDeviceSelector.SelectedItem is not AudioDeviceChoice { IsAvailable: true } choice) return;
        var version = _audioTestVersion;
        _audioDeviceTests ??= new AudioDeviceSettingsService();
        _audioToneCancellation?.Cancel();
        _audioToneCancellation?.Dispose();
        _audioToneCancellation = new CancellationTokenSource();
        _audioTonePlaying = true;
        SettingsAudioTestTitle.Text = "Output device check";
        SettingsAudioTestStatus.Text = "Playing a short test sound through the selected output…";
        UpdateSettingsAudioTestButtons();
        BringAudioTestIntoView();
        try
        {
            await _audioDeviceTests.PlayTestToneAsync(choice.Id, _audioToneCancellation.Token);
            if (version == _audioTestVersion && !_captureWindowClosing)
                SettingsAudioTestStatus.Text = _audioLevelTestActive
                    ? "Test sound finished. The level check is still listening."
                    : "Test sound finished. If you heard it, this output is working.";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (version == _audioTestVersion && !_captureWindowClosing)
                SettingsAudioTestStatus.Text = $"Could not play the test sound: {exception.Message}";
        }
        finally
        {
            if (version == _audioTestVersion)
            {
                _audioTonePlaying = false;
                if (!_captureWindowClosing) UpdateSettingsAudioTestButtons();
            }
        }
    }

    private async void OnSettingsCheckSystemLevelClicked(object sender, RoutedEventArgs args) =>
        await StartSettingsAudioLevelTestAsync(false);

    private async void OnSettingsCheckMicrophoneLevelClicked(object sender, RoutedEventArgs args) =>
        await StartSettingsAudioLevelTestAsync(true);

    private async Task StartSettingsAudioLevelTestAsync(bool microphone)
    {
        var selection = microphone ? SettingsMicrophoneDeviceSelector.SelectedItem : SettingsSystemAudioDeviceSelector.SelectedItem;
        if (selection is not AudioDeviceChoice { IsAvailable: true } choice || _applyingAudioSettings || _captureWindowClosing) return;
        StopSettingsAudioTests();
        var version = _audioTestVersion;
        _audioDeviceTests ??= new AudioDeviceSettingsService();
        _audioLevelCancellation = new CancellationTokenSource();
        var cancellationToken = _audioLevelCancellation.Token;
        SettingsAudioTestTitle.Text = microphone ? "Microphone check" : "Game / system audio check";
        SettingsAudioTestStatus.Text = "Opening the selected device…";
        BringAudioTestIntoView();
        try
        {
            await Task.Run(() => _audioDeviceTests.StartLevelTest(microphone, choice.Id, cancellationToken));
            if (version != _audioTestVersion || _captureWindowClosing || _settingsPage != "audio") return;
            _audioLevelTestMicrophone = microphone;
            _audioLevelTestActive = true;
            _audioLevelDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
            _audioLevelTimer ??= CreateSettingsAudioLevelTimer();
            _audioLevelTimer.Start();
            UpdateSettingsAudioLevel();
        }
        catch (Exception exception)
        {
            if (version == _audioTestVersion && !_captureWindowClosing)
                SettingsAudioTestStatus.Text = $"Could not test this device: {exception.Message}";
        }
        finally { if (!_captureWindowClosing) UpdateSettingsAudioTestButtons(); }
    }

    private DispatcherTimer CreateSettingsAudioLevelTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => UpdateSettingsAudioLevel();
        return timer;
    }

    private void UpdateSettingsAudioLevel()
    {
        if (!_audioLevelTestActive || _audioDeviceTests is null) return;
        if (DateTimeOffset.UtcNow >= _audioLevelDeadline)
        {
            StopSettingsAudioTests();
            SettingsAudioTestStatus.Text = "Level check finished. No test audio was saved.";
            return;
        }
        if (_audioDeviceTests.TestError is { } error)
        {
            StopSettingsAudioTests();
            SettingsAudioTestStatus.Text = $"Audio device stopped responding: {error}";
            return;
        }
        var gain = _audioLevelTestMicrophone ? _settings.Current.MicrophoneGain : _settings.Current.SystemAudioGain;
        var peak = Math.Clamp(_audioDeviceTests.Peak * gain, 0, 1);
        SettingsAudioLevelMeter.Value = peak * 100;
        SettingsAudioLevelMeter.Foreground = (Brush)Application.Current.Resources[peak >= .98 ? "KlippDangerBrush" : "KlippAccentBrush"];
        var remaining = Math.Max(1, (int)Math.Ceiling((_audioLevelDeadline - DateTimeOffset.UtcNow).TotalSeconds));
        SettingsAudioTestStatus.Text = peak >= .98 ? $"Level is clipping. Lower the recording level. · {remaining}s left"
            : peak > .005 ? $"Signal detected · {20 * Math.Log10(peak):0} dB · {remaining}s left"
                : $"{(_audioLevelTestMicrophone ? "Speak into your microphone." : "Play sound through this output to check its level.")} · {remaining}s left";
    }

    private void OnSettingsStopAudioTestClicked(object sender, RoutedEventArgs args)
    {
        StopSettingsAudioTests();
        SettingsAudioTestStatus.Text = "Device check stopped. No test audio was saved.";
    }

    private void StopSettingsAudioTests()
    {
        var wasRunning = _audioLevelTestActive || _audioTonePlaying || _audioLevelCancellation is not null;
        _audioTestVersion++;
        _audioLevelTimer?.Stop();
        _audioLevelCancellation?.Cancel();
        _audioLevelCancellation?.Dispose();
        _audioLevelCancellation = null;
        _audioToneCancellation?.Cancel();
        _audioToneCancellation?.Dispose();
        _audioToneCancellation = null;
        _audioDeviceTests?.Stop();
        _audioLevelTestActive = false;
        _audioTonePlaying = false;
        if (SettingsAudioLevelMeter is null) return;
        SettingsAudioLevelMeter.Value = 0;
        SettingsAudioLevelMeter.Foreground = (Brush)Application.Current.Resources["KlippAccentBrush"];
        if (wasRunning) SettingsAudioTestStatus.Text = "Device check stopped. No test audio was saved.";
        UpdateSettingsAudioTestButtons();
    }

    private void DisposeSettingsAudioDevices()
    {
        StopSettingsAudioTests();
        _audioDeviceRefreshVersion++;
        _audioDeviceTests?.Dispose();
        _audioDeviceTests = null;
    }

    private void UpdateSettingsAudioTestButtons()
    {
        if (SettingsTestOutputButton is null) return;
        var outputAvailable = SettingsSystemAudioDeviceSelector.SelectedItem is AudioDeviceChoice { IsAvailable: true };
        var microphoneAvailable = SettingsMicrophoneDeviceSelector.SelectedItem is AudioDeviceChoice { IsAvailable: true };
        var canTest = !_applyingAudioSettings && !_captureWindowClosing;
        SettingsTestOutputButton.IsEnabled = outputAvailable && !_audioTonePlaying && canTest;
        SettingsDeviceCheckPlaySoundButton.IsEnabled = SettingsTestOutputButton.IsEnabled;
        SettingsTestSystemLevelButton.IsEnabled = outputAvailable && canTest;
        SettingsTestMicrophoneButton.IsEnabled = microphoneAvailable && canTest;
        SettingsStopAudioTestButton.IsEnabled = _audioLevelTestActive || _audioTonePlaying;
        SettingsApplyAudioButton.IsEnabled = !_applyingAudioSettings && _captureSessionReady && !_captureWindowClosing &&
            _captureOperationGate.CurrentCount > 0 && _clipMode.IsMonitoring && !_recording.IsRecording;
    }

    private async void OnSettingsApplyAudioClicked(object sender, RoutedEventArgs args)
    {
        if (_applyingAudioSettings || !_captureSessionReady || _captureWindowClosing ||
            _captureOperationGate.CurrentCount == 0 || !_clipMode.IsMonitoring || _recording.IsRecording) return;
        _applyingAudioSettings = true;
        StopSettingsAudioTests();
        SettingsApplyAudioButton.IsEnabled = false;
        SetSettingsAudioApplyStatus("Applying audio devices…");
        try
        {
            await _settings.SaveAsync();
            var applied = await ApplyAudioPreferencesToCaptureAsync();
            if (_captureWindowClosing) return;
            var message = applied
                ? "Audio devices applied. A fresh instant-clip buffer has started."
                : _captureSourceError is { Length: > 0 } error
                    ? $"Capture could not restart: {error} Check your selected devices and retry capture."
                    : "Audio changes are saved and will be used when capture next starts.";
            SettingsAudioTestStatus.Text = message;
            SetSettingsAudioApplyStatus(message, error: !applied && _captureSourceError is { Length: > 0 }, success: applied);
        }
        catch (Exception exception)
        {
            if (!_captureWindowClosing)
            {
                var message = $"Could not apply audio devices: {exception.Message} Check your selected devices and try again.";
                SettingsAudioTestStatus.Text = message;
                SetSettingsAudioApplyStatus(message, error: true);
            }
        }
        finally
        {
            _applyingAudioSettings = false;
            if (!_captureWindowClosing) UpdateSettingsAudioTestButtons();
        }
    }

    private void SetSettingsAudioApplyStatus(string message, bool error = false, bool success = false)
    {
        SettingsAudioApplyStatus.Text = message;
        SettingsAudioApplyStatus.Foreground = (Brush)Application.Current.Resources[
            error ? "KlippDangerBrush" : success ? "KlippSuccessBrush" : "KlippTextSecondaryBrush"];
        SettingsAudioApplyStatus.Visibility = Visibility.Visible;
    }

    private void BringAudioTestIntoView() =>
        SettingsAudioTestTitle.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = .5 });
}
