using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Klipp.Core.Input;
using Klipp.Core.Models;
using Klipp.Desktop.Services;
using Klipp.Encoding.Audio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Klipp.Desktop;

public sealed partial class MainWindow
{
    private const int BookmarkHotkeyId = 2;
    private bool _capturingBookmarkHotkey;
    private bool _recordingToolsInitialized;
    private bool _wasRecordingForTools;
    private bool _reviewingRecovery;
    private bool _scanningRecovery;
    private bool _rescanRecovery;
    private TaskCompletionSource? _recoveryScanFinished;
    private IReadOnlyList<RecoverableRecording> _recoverableRecordings = [];
    private readonly Dictionary<CaptureAudioSource, CaptureAudioDeviceStatus> _lastAudioDevices = [];

    private void InitializeRecordingTools()
    {
        _recordingToolsInitialized = true;
        BookmarkHotkeyText.Text = _settings.Current.BookmarkHotkeyDisplay;
        if (_hotkeyManager is not null)
        {
            _hotkeyManager.AdditionalHotkeyPressed += OnRecordingToolsHotkey;
            var conflict = EditorShortcutCatalog.FindMatch(_settings.Current.EditorShortcuts,
                ToEditorShortcutModifiers((_settings.Current.BookmarkHotkeyModifiers & 2) != 0,
                    (_settings.Current.BookmarkHotkeyModifiers & 4) != 0,
                    (_settings.Current.BookmarkHotkeyModifiers & 1) != 0), _settings.Current.BookmarkHotkeyVirtualKey);
            if (conflict is not null || !_hotkeyManager.RegisterAdditional(BookmarkHotkeyId,
                    (HotkeyManager.Modifiers)_settings.Current.BookmarkHotkeyModifiers, _settings.Current.BookmarkHotkeyVirtualKey))
                BookmarkShortcutStatusText.Text = "The highlight shortcut is in use. Choose another shortcut here.";
        }
        UpdateRecordingTools();
    }

    private void OnRecordingToolsHotkey(int id)
    {
        if (id == BookmarkHotkeyId) DispatcherQueue.TryEnqueue(() =>
        {
            if (!_capturingBookmarkHotkey && !_capturingHotkey && _capturingEditorShortcutAction is null)
                OnRecordingBookmarkClicked(this, new RoutedEventArgs());
        });
    }

    private async void OnRecordingBookmarkClicked(object sender, RoutedEventArgs e)
    {
        if (!_recording.IsRecording) return;
        try
        {
            if (_recording.TryAddMarker() is not { } marker) return;
            var time = TimeSpan.FromSeconds(marker.TimeSeconds);
            ShowCaptureNotification(CaptureNotificationKind.RecordingBookmark, "Highlight marked",
                $"{time:hh\\:mm\\:ss} · Find this moment on the editor timeline.");
        }
        catch (Exception exception) { await ShowErrorAsync("Could not save highlight", exception.Message); }
    }

    private void OnBookmarkHotkeyClicked(object sender, RoutedEventArgs e)
    {
        if (_capturingBookmarkHotkey) { StopBookmarkHotkeyCapture(); return; }
        StopHotkeyCapture();
        StopEditorShortcutCapture();
        _capturingBookmarkHotkey = true;
        BookmarkHotkeyText.Text = "Press a shortcut…";
        BookmarkShortcutStatusText.Text = "Use Ctrl, Shift or Alt with a key. Escape cancels.";
        Content.KeyDown += OnBookmarkHotkeyCaptureKeyDown;
    }

    private void StopBookmarkHotkeyCapture()
    {
        if (!_capturingBookmarkHotkey) return;
        _capturingBookmarkHotkey = false;
        Content.KeyDown -= OnBookmarkHotkeyCaptureKeyDown;
        BookmarkHotkeyText.Text = _settings.Current.BookmarkHotkeyDisplay;
        BookmarkShortcutStatusText.Text = string.Empty;
    }

    private async void OnBookmarkHotkeyCaptureKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (!_capturingBookmarkHotkey || e.Handled) return;
        if (e.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows) return;
        e.Handled = true;
        var ctrl = IsKeyDown(VirtualKey.Control);
        var shift = IsKeyDown(VirtualKey.Shift);
        var alt = IsKeyDown(VirtualKey.Menu);
        var modifiers = (uint)((ctrl ? 2 : 0) | (shift ? 4 : 0) | (alt ? 1 : 0));
        var key = (uint)e.Key;
        if (modifiers == 0 || !EditorShortcutCatalog.IsValidGesture(ToEditorShortcutModifiers(ctrl, shift, alt), key))
        { BookmarkShortcutStatusText.Text = "Use Ctrl, Shift or Alt with a letter, number or function key."; return; }
        if (modifiers == _settings.Current.HotkeyModifiers && key == _settings.Current.HotkeyVirtualKey ||
            EditorShortcutCatalog.FindMatch(_settings.Current.EditorShortcuts, ToEditorShortcutModifiers(ctrl, shift, alt), key) is not null)
        { BookmarkShortcutStatusText.Text = "This shortcut is already assigned in Klipp. Choose another."; return; }
        if (!(_hotkeyManager?.RegisterAdditional(BookmarkHotkeyId, (HotkeyManager.Modifiers)modifiers, key) ?? false))
        { BookmarkShortcutStatusText.Text = "Another app is using this shortcut. Choose another."; return; }
        var parts = new List<string>();
        if (ctrl) parts.Add("Ctrl");
        if (shift) parts.Add("Shift");
        if (alt) parts.Add("Alt");
        parts.Add(e.Key.ToString());
        _settings.Current.BookmarkHotkeyModifiers = modifiers;
        _settings.Current.BookmarkHotkeyVirtualKey = key;
        _settings.Current.BookmarkHotkeyDisplay = string.Join(" + ", parts);
        StopBookmarkHotkeyCapture();
        UpdateRecordingTools();
        await _settings.SaveAsync();
    }

    private void UpdateRecordingTools()
    {
        if (!_recordingToolsInitialized || _captureWindowClosing) return;
        var recording = _recording.IsRecording;
        RecordingBookmarkButton.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(RecordingBookmarkButton, $"Mark a highlight · {_settings.Current.BookmarkHotkeyDisplay}");
        if (_wasRecordingForTools && !recording) _ = RefreshRecordingRecoveryAsync();
        _wasRecordingForTools = recording;
        PollCaptureAudioDevices();
        UpdateCaptureAudioMeterState();
    }

    private void PollCaptureAudioDevices()
    {
        var devices = _recording.IsRecording ? _recording.AudioDeviceStatuses :
            _clipMode.IsMonitoring ? _clipMode.AudioDeviceStatuses : [];
        if (devices.Count == 0)
        {
            _lastAudioDevices.Clear();
            AudioReconnectStatusText.Visibility = Visibility.Collapsed;
            return;
        }
        var changes = new List<string>();
        foreach (var device in devices)
        {
            var label = CaptureAudioSourceName(device.Source);
            var connected = device.State == CaptureAudioConnectionState.Connected;
            if (_lastAudioDevices.TryGetValue(device.Source, out var previous))
            {
                if (previous.State != device.State || connected && !string.Equals(previous.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase))
                    changes.Add(connected ? $"{label} reconnected · {device.DeviceName}" : $"{label} disconnected · reconnecting automatically");
            }
            else if (!connected) changes.Add($"{label} unavailable · reconnecting automatically");
            _lastAudioDevices[device.Source] = device;
        }
        foreach (var source in _lastAudioDevices.Keys.Where(source => !devices.Any(d => d.Source == source)).ToArray())
            _lastAudioDevices.Remove(source);
        var waiting = devices.Where(d => d.State == CaptureAudioConnectionState.Reconnecting)
            .Select(d => CaptureAudioSourceName(d.Source)).ToArray();
        AudioReconnectStatusText.Visibility = waiting.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AudioReconnectStatusText.Text = $"Reconnecting {string.Join(" + ", waiting).ToLowerInvariant()}…";
        ToolTipService.SetToolTip(AudioReconnectStatusText, "Recording continues. The disconnected audio track stays silent until its device returns.");
        if (changes.Count > 0) ShowCaptureNotification(CaptureNotificationKind.AudioDeviceChanged,
            "Audio connection changed", string.Join(" · ", changes));
    }

    private static string CaptureAudioSourceName(CaptureAudioSource source) => source switch
    {
        CaptureAudioSource.Microphone => "Microphone", CaptureAudioSource.Discord => "Discord",
        CaptureAudioSource.Music => "Music", _ => "Game audio"
    };

    private async Task RefreshRecordingRecoveryAsync()
    {
        if (_captureWindowClosing) return;
        if (_scanningRecovery)
        {
            _rescanRecovery = true;
            if (_recoveryScanFinished is { } pending) await pending.Task;
            return;
        }
        _scanningRecovery = true;
        var finished = _recoveryScanFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            do
            {
                _rescanRecovery = false;
                _recoverableRecordings = await _recording.Recovery.GetRecoverableAsync();
                if (_captureWindowClosing) return;
                var count = _recoverableRecordings.Count;
                RecordingRecoveryInfoBar.IsOpen = count > 0;
                RecordingRecoveryInfoBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
                RecordingRecoveryInfoBar.Title = count == 1 ? "Unfinished recording" : $"{count} unfinished recordings";
                RecordingRecoveryInfoBar.Message = "Saving was interrupted. Review the available video and recover it to your library.";
                RecordingRecoveryStatusText.Text = count == 0 ? "No unfinished recordings. Recovery files are kept if saving is interrupted." : $"{count} unfinished recording{(count == 1 ? "" : "s")} available to review.";
                ReviewRecordingRecoveryButton.IsEnabled = count > 0 && !_reviewingRecovery;
            } while (_rescanRecovery && !_captureWindowClosing);
        }
        catch (Exception exception) { Debug.WriteLine($"Recording recovery scan: {exception}"); }
        finally
        {
            _scanningRecovery = false;
            _recoveryScanFinished = null;
            finished.TrySetResult();
        }
    }

    private async void OnReviewRecordingRecoveryClicked(object sender, RoutedEventArgs e)
    {
        if (_reviewingRecovery) return;
        _reviewingRecovery = true;
        using var updateWork = BeginUpdateBlockingOperation();
        ReviewRecordingRecoveryButton.IsEnabled = false;
        try
        {
            await RefreshRecordingRecoveryAsync();
            if (_recoverableRecordings.Count == 0) return;
            var panel = new StackPanel { Spacing = 12, MaxWidth = 520 };
            panel.Children.Add(new TextBlock { Text = "Recover selected recordings to your library. Recovery files stay available until saving succeeds. The last unfinished second may be missing.", TextWrapping = TextWrapping.Wrap });
            var choices = new List<(RecoverableRecording Item, CheckBox Choice)>();
            foreach (var item in _recoverableRecordings)
            {
                var detail = new StackPanel { Spacing = 3 };
                detail.Children.Add(new TextBlock { Text = item.SuggestedFileName, TextWrapping = TextWrapping.Wrap });
                detail.Children.Add(new TextBlock { Text = $"{item.StartedUtc.ToLocalTime():g} · {FormatHealthBytes(item.Bytes)}" + (item.DurationSeconds > 0 ? $" · {TimeSpan.FromSeconds(item.DurationSeconds):hh\\:mm\\:ss}" : ""), FontSize = 12, Opacity = 0.72 });
                if (!string.IsNullOrWhiteSpace(item.FailureReason)) detail.Children.Add(new TextBlock { Text = item.FailureReason, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.72 });
                var choice = new CheckBox { Content = detail, IsChecked = item.CanRecover, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                choices.Add((item, choice));
                panel.Children.Add(choice);
            }
            var dialog = AccessibleDialogFactory.Create(Content.XamlRoot, "Recover recordings",
                new ScrollViewer { Content = panel, MaxHeight = 350, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                primaryButtonText: "Recover selected", closeButtonText: "Later", defaultButton: ContentDialogButton.Primary);
            dialog.SecondaryButtonText = "Discard selected…";
            void UpdateButtons() { dialog.IsPrimaryButtonEnabled = choices.Any(c => c.Choice.IsChecked == true && c.Item.CanRecover); dialog.IsSecondaryButtonEnabled = choices.Any(c => c.Choice.IsChecked == true); }
            foreach (var choice in choices) { choice.Choice.Checked += (_, _) => UpdateButtons(); choice.Choice.Unchecked += (_, _) => UpdateButtons(); }
            UpdateButtons();
            var result = await dialog.ShowAsync();
            var selected = choices.Where(c => c.Choice.IsChecked == true).Select(c => c.Item).ToArray();
            if (result == ContentDialogResult.Secondary)
            {
                var confirmation = AccessibleDialogFactory.Create(Content.XamlRoot, "Discard recovery files?",
                    $"Permanently remove {selected.Length} selected unfinished recording{(selected.Length == 1 ? "" : "s")}? These files cannot be recovered after discarding.",
                    primaryButtonText: "Discard", closeButtonText: "Keep recordings", defaultButton: ContentDialogButton.Close);
                if (await confirmation.ShowAsync() == ContentDialogResult.Primary)
                    foreach (var item in selected) await _recording.Recovery.DiscardAsync(item.Id);
            }
            else if (result == ContentDialogResult.Primary)
            {
                var errors = new List<string>();
                foreach (var item in selected.Where(item => item.CanRecover))
                {
                    RecordingRecoveryInfoBar.IsOpen = true;
                    RecordingRecoveryInfoBar.Visibility = Visibility.Visible;
                    RecordingRecoveryInfoBar.Message = $"Recovering {item.SuggestedFileName}…";
                    try
                    {
                        var recovered = await _recording.Recovery.RecoverAsync(item.Id, LibraryService.ClipsDirectory);
                        LibraryService.AddNewClip(recovered.OutputPath);
                    }
                    catch (Exception exception) { errors.Add($"{item.SuggestedFileName}: {exception.Message}"); }
                }
                if (errors.Count > 0) await ShowErrorAsync("Some recordings need attention", string.Join("\n\n", errors) + "\n\nRecovery files have been kept. You can try again later.");
            }
        }
        catch (Exception exception) { await ShowErrorAsync("Recording recovery", exception.Message); }
        finally { _reviewingRecovery = false; await RefreshRecordingRecoveryAsync(); }
    }
}
