using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Updates;
using Klipp.Desktop.Controls;
using Klipp.Desktop.Models;
using Klipp.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Klipp.Desktop;

public sealed partial class MainWindow
{
    private readonly AppUpdateService _appUpdates = new();
    private readonly CancellationTokenSource _updateLifetime = new();
    private DispatcherTimer? _updateTimer;
    private DispatcherTimer? _updateUiTimer;
    private bool _updatesInitialized;
    private bool _updatesDisposed;
    private bool _updateRequestRunning;
    private bool _updateRestarting;
    private bool _updateDownloadPauseRequested;
    private bool _updateDownloadPausedByUser;
    private int _updateBlockingOperations;
    private AppUpdatePromptPolicy _updatePromptPolicy = new();
    private bool _updateWindowActive;
    private string? _updateMessageOverride;
    private string? _updateRestartError;
    private string? _updateRestartErrorVersion;
    private string? _updatePromptVersion;
    private string? _updateHighlightsVersion;
    private string? _updateHighlightsMarkdown;
    private IReadOnlyList<string> _updateHighlights = Array.Empty<string>();

    private void InitializeUpdates()
    {
        if (_updatesInitialized || _updatesDisposed) return;
        _updatePromptPolicy = new AppUpdatePromptPolicy(_settings.Current.DeferredUpdateVersion);
        _updatesInitialized = true;
        _appUpdates.StateChanged += OnAppUpdateStateChanged;
        // Capture's status timer stops while idle. Keep restart availability and
        // playback-aware download pausing current independently of capture.
        _updateUiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _updateUiTimer.Tick += (_, _) => RefreshUpdateUi();
        _updateUiTimer.Start();
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _updateTimer.Tick += async (_, _) => await CheckAutomaticUpdateAsync();
        _updateTimer.Start();
        SyncUpdateSettings();
        _ = CheckUpdateAfterStartupAsync();
    }

    private async Task CheckUpdateAfterStartupAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), _updateLifetime.Token);
            await CheckAutomaticUpdateAsync();
        }
        catch (OperationCanceledException) { }
    }

    private bool CanDownloadUpdate() => GetUpdateActivity().CanDownloadAutomatically;

    private void OnUpdateWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        _updateWindowActive = args.WindowActivationState != WindowActivationState.Deactivated;
        RefreshUpdateUi();
    }

    private bool CanShowUpdatePrompt(AppUpdateActivity activity) =>
        _updatesInitialized && !_updateRestarting && !_captureWindowClosing && !_trayExitPending &&
        _updateWindowActive && !_hiddenInTray && !_sideMenuOpen &&
        LibraryView.Visibility == Visibility.Visible && SideMenuOverlay.Visibility != Visibility.Visible &&
        InlineFullscreenHost.Visibility != Visibility.Visible && activity.RestartBlockedReason is null &&
        AppRoot.XamlRoot is { } root && VisualTreeHelper.GetOpenPopupsForXamlRoot(root).All(popup => popup.Child is ToolTip);

    private async Task CheckAutomaticUpdateAsync()
    {
        if (!_updatesInitialized || _updatesDisposed || _updateRestarting || _updateRequestRunning ||
            !_appUpdates.IsInstalled || !_settings.Current.AutomaticUpdatesEnabled || _updateDownloadPausedByUser || !CanDownloadUpdate()) return;
        _updateRequestRunning = true;
        try
        {
            // A capture can start while metadata is in flight. Recheck activity
            // after that await instead of authorizing a later download in advance.
            await _appUpdates.CheckAsync(downloadAutomatically: false, cancellationToken: _updateLifetime.Token);
            if (!_updatesDisposed && _settings.Current.AutomaticUpdatesEnabled && CanDownloadUpdate() &&
                _appUpdates.Snapshot.Status == AppUpdateStatus.Available)
                await _appUpdates.DownloadAsync(manual: false, cancellationToken: _updateLifetime.Token);
        }
        catch (OperationCanceledException) { }
        finally { _updateRequestRunning = false; }
    }

    private void OnAppUpdateStateChanged(object? sender, EventArgs args)
    {
        if (!_updatesDisposed) DispatcherQueue.TryEnqueue(RefreshUpdateUi);
    }

    private void SyncUpdateSettings()
    {
        if (SettingsAutomaticUpdatesToggle is null) return;
        var syncing = _syncingSettingsUi;
        _syncingSettingsUi = true;
        try { SettingsAutomaticUpdatesToggle.IsOn = _settings.Current.AutomaticUpdatesEnabled; }
        finally { _syncingSettingsUi = syncing; }
        RefreshUpdateUi();
    }

    private AppUpdateActivity GetUpdateActivity(bool ignoreCaptureGate = false)
    {
        var recording = _recording.IsRecording;
        var instantClips = _clipMode.IsMonitoring || _instantCaptureRequested;
        return new AppUpdateActivity(
            Recording: recording,
            InstantClips: instantClips,
            Exporting: !recording && !instantClips && _exportQueue.Jobs.Any(job =>
                job.Status is EditorExportJobStatus.Pending or EditorExportJobStatus.Rendering),
            Editing: EditorHost.Visibility == Visibility.Visible,
            FileOperation: _updateBlockingOperations != 0 || _reviewingRecovery,
            CaptureChanging: !_updatesInitialized || _isSwitchingMode ||
                !ignoreCaptureGate && _captureOperationGate.CurrentCount == 0,
            Maintenance: _cacheOperationRunning || _retentionOperationRunning,
            // PreviewStarting runs before ClipCard publishes its active player.
            // The persistent preview host closes that one-frame safety gap.
            Playback: _hostedInlineCard is not null || ClipCard.HasActivePreview ||
                PlayerHost.Visibility == Visibility.Visible || InlineFullscreenHost.Visibility == Visibility.Visible);
    }

    private void RefreshUpdateUi()
    {
        if (_updatesDisposed || SettingsUpdateStatusText is null) return;
        if (!_updatesInitialized)
        {
            AppUpdatePrompt?.Hide();
            return;
        }
        var state = _appUpdates.Snapshot;
        var activity = GetUpdateActivity();
        if (state.Status == AppUpdateStatus.Downloading && !CanDownloadUpdate())
        {
            if (!_updateDownloadPauseRequested)
            {
                _updateDownloadPauseRequested = true;
                _appUpdates.CancelDownload();
            }
        }
        else _updateDownloadPauseRequested = false;

        var ready = state.Status == AppUpdateStatus.Ready;
        var downloading = state.Status == AppUpdateStatus.Downloading;
        var checking = state.Status == AppUpdateStatus.Checking;
        var reason = activity.RestartBlockedReason;
        SettingsAppVersionText.Text = $"Klipp {state.CurrentVersion}";
        SettingsUpdateStatusText.Text = _updateRestarting ? "Saving your work before restarting…"
            : _updateMessageOverride ?? (ready && reason is not null ? $"{state.Message} {reason}" : state.Message);
        SettingsUpdateLastCheckedText.Text = state.LastCheckedUtc is { } checkedAt
            ? $"Last checked {checkedAt.LocalDateTime:g}" : string.Empty;
        SettingsUpdateProgress.Visibility = downloading || checking ? Visibility.Visible : Visibility.Collapsed;
        SettingsUpdateProgress.IsIndeterminate = checking;
        SettingsUpdateProgress.Value = state.ProgressPercent;
        SettingsUpdateCheckButton.Visibility = _appUpdates.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        SettingsUpdateCheckButton.IsEnabled = _updatesInitialized && !_updateRestarting && !checking && !downloading;
        SettingsUpdateDownloadButton.Visibility = state.Status == AppUpdateStatus.Available ? Visibility.Visible : Visibility.Collapsed;
        SettingsUpdatePauseButton.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        SettingsUpdateDownloadButton.IsEnabled = !_updateRestarting && CanDownloadUpdate();
        SettingsUpdateRestartButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        SettingsUpdateRestartButton.IsEnabled = ready && !_updateRestarting && reason is null;
        SettingsUpdateInstallerButton.Visibility = _appUpdates.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
        SettingsAutomaticUpdatesToggle.IsEnabled = _appUpdates.IsInstalled && !_updateRestarting;
        ToolTipService.SetToolTip(SettingsUpdateRestartButton, reason ?? "Save your work and restart Klipp to install the downloaded update.");
        ToolTipService.SetToolTip(SettingsUpdateDownloadButton,
            activity.CanDownloadAutomatically ? "Download the update in the background." : "Finish capture, editing and exports before downloading.");

        if (_updatePromptPolicy.ShouldShow(ready ? state.AvailableVersion : null, CanShowUpdatePrompt(activity)))
        {
            if (_updateHighlightsVersion != state.AvailableVersion || _updateHighlightsMarkdown != state.ReleaseNotesMarkdown)
            {
                _updateHighlightsVersion = state.AvailableVersion;
                _updateHighlightsMarkdown = state.ReleaseNotesMarkdown;
                _updateHighlights = AppUpdateReleaseNotes.Summarize(state.ReleaseNotesMarkdown);
            }
            _updatePromptVersion = state.AvailableVersion;
            AppUpdatePrompt.Show(state.AvailableVersion!,
                state.AvailableVersion == _updateRestartErrorVersion ? _updateRestartError : null, _updateHighlights);
        }
        else
        {
            _updatePromptVersion = null;
            AppUpdatePrompt.Hide();
        }
    }

    private async void OnUpdateLaterRequested(object? sender, EventArgs args)
    {
        // Dismiss only this verified version. Checks stay enabled and Settings
        // keeps its explicit restart action, including after reopening Klipp.
        if (_updatesDisposed || _updateRestarting || _updatePromptVersion is not { } version) return;
        _updatePromptPolicy.Defer(version);
        _settings.Current.DeferredUpdateVersion = version;
        RefreshUpdateUi();
        await _settings.SaveAsync();
    }

    private void OnUpdateRestartRequested(object? sender, EventArgs args)
    {
        // The version whose changes were reviewed must still be the prepared
        // update. Refresh rather than approving an unseen replacement.
        if (_updatePromptVersion is null || _updatePromptVersion != _appUpdates.Snapshot.AvailableVersion)
        {
            RefreshUpdateUi();
            return;
        }
        OnRestartForUpdateClicked(sender ?? this, new RoutedEventArgs());
    }

    private async void OnUpdatePromptReleaseNotesRequested(object? sender, EventArgs args)
    {
        if (_updatesDisposed || _updateRestarting || _updatePromptVersion is not { } version) return;
        await OpenUpdateLinkAsync(GetVersionReleaseNotesUri(version));
    }

    private static Uri GetVersionReleaseNotesUri(string version) =>
        new(AppUpdateService.RepositoryUrl + "/releases/tag/v" + Uri.EscapeDataString(version));

    private async void OnAutomaticUpdatesToggled(object sender, RoutedEventArgs args)
    {
        if (!_uiReady || _syncingSettingsUi || _updateRestarting) return;
        _settings.Current.AutomaticUpdatesEnabled = SettingsAutomaticUpdatesToggle.IsOn;
        _updateDownloadPausedByUser = false;
        if (!_settings.Current.AutomaticUpdatesEnabled) _appUpdates.CancelDownload();
        await _settings.SaveAsync();
        if (_settings.Current.AutomaticUpdatesEnabled) await CheckAutomaticUpdateAsync();
    }

    private async void OnCheckForUpdatesClicked(object sender, RoutedEventArgs args)
    {
        if (!_updatesInitialized || _updatesDisposed || _updateRestarting) return;
        _updateMessageOverride = null;
        try { await _appUpdates.CheckAsync(downloadAutomatically: false, manual: true, _updateLifetime.Token); }
        catch (OperationCanceledException) { }
        RefreshUpdateUi();
    }

    private async void OnDownloadUpdateClicked(object sender, RoutedEventArgs args)
    {
        if (_updatesDisposed || _updateRestarting || !CanDownloadUpdate()) return;
        _updateDownloadPausedByUser = false;
        _updateMessageOverride = null;
        try { await _appUpdates.DownloadAsync(_updateLifetime.Token); }
        catch (OperationCanceledException) { }
        RefreshUpdateUi();
    }

    private void OnPauseUpdateDownloadClicked(object sender, RoutedEventArgs args)
    {
        _updateDownloadPausedByUser = true;
        _appUpdates.CancelDownload();
    }

    private async void OnGetInstallerClicked(object sender, RoutedEventArgs args) =>
        await OpenUpdateLinkAsync(AppUpdateService.InstallerUri);

    private async void OnUpdateReleaseNotesClicked(object sender, RoutedEventArgs args) =>
        await OpenUpdateLinkAsync(_appUpdates.Snapshot.AvailableVersion is { } version
            ? GetVersionReleaseNotesUri(version) : AppUpdateService.ReleasesUri);

    private async Task OpenUpdateLinkAsync(Uri uri)
    {
        try
        {
            if (!await Launcher.LaunchUriAsync(uri)) throw new InvalidOperationException();
        }
        catch
        {
            _updateMessageOverride = "Could not open your browser. Get the installer from github.com/HappyHamster135/Klipp/releases.";
            RefreshUpdateUi();
        }
    }

    private async void OnRestartForUpdateClicked(object sender, RoutedEventArgs args)
    {
        if (_updateRestarting || _updatesDisposed || _appUpdates.Snapshot.Status != AppUpdateStatus.Ready) return;
        var blocked = GetUpdateActivity().RestartBlockedReason;
        if (blocked is not null || !await _captureOperationGate.WaitAsync(0))
        {
            _updateMessageOverride = blocked ?? "Wait for capture to finish changing before restarting.";
            RefreshUpdateUi();
            return;
        }

        var wasClosing = _captureWindowClosing;
        var wasCaptureReady = _captureSessionReady;
        var sourceTimerRunning = _captureSourceTimer?.IsEnabled == true;
        var cacheTimerRunning = _cacheTimer.IsEnabled;
        var healthTimerRunning = _healthTimer.IsEnabled;
        var restartPrepared = false;
        _updateRestarting = true;
        _updateMessageOverride = null;
        _updateRestartError = null;
        _captureWindowClosing = true;
        _captureSessionReady = false;
        _captureSourceTimer?.Stop();
        _cacheTimer.Stop();
        _healthTimer.Stop();
        AppRoot.IsHitTestVisible = false;
        RefreshUpdateUi();
        try
        {
            ClipCard.StopActivePreview();
            StopPlayerIfCreated();
            StopSettingsAudioTests();
            if (_editorView is not null) await _editorView.FlushProjectForUpdateAsync(_updateLifetime.Token);
            await _settings.SaveForUpdateAsync(_updateLifetime.Token);
            if (GetUpdateActivity(ignoreCaptureGate: true).RestartBlockedReason is { } changed)
                throw new InvalidOperationException(changed);
            if (!_appUpdates.PrepareRestart())
                throw new InvalidOperationException("The updater could not start. Try restarting to update again.");
            restartPrepared = true;
            // Window.Closed releases the single-instance mutex and other app
            // resources. The updater waits for this process before replacement.
            Close();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"[Klipp] Update restart was canceled: {exception.GetType().Name}");
            _updateMessageOverride = exception is InvalidOperationException ? exception.Message
                : "Could not finish saving your work. Klipp stayed open. Check free disk space and try again.";
            _updateRestartError = _updateMessageOverride;
            _updateRestartErrorVersion = _appUpdates.Snapshot.AvailableVersion;
        }
        finally
        {
            _captureOperationGate.Release();
            if (!restartPrepared && !_updatesDisposed)
            {
                _updateRestarting = false;
                _captureWindowClosing = wasClosing;
                _captureSessionReady = wasCaptureReady;
                AppRoot.IsHitTestVisible = true;
                if (sourceTimerRunning) _captureSourceTimer?.Start();
                if (cacheTimerRunning) _cacheTimer.Start();
                if (healthTimerRunning) _healthTimer.Start();
                RefreshUpdateUi();
            }
        }
    }

    private IDisposable BeginUpdateBlockingOperation()
    {
        _updateBlockingOperations++;
        RefreshUpdateUi();
        return new UpdateWorkScope(this);
    }

    private sealed class UpdateWorkScope(MainWindow owner) : IDisposable
    {
        private MainWindow? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } window) return;
            window._updateBlockingOperations--;
            window.RefreshUpdateUi();
        }
    }

    private void DisposeUpdates()
    {
        if (_updatesDisposed) return;
        _updatesDisposed = true;
        AppUpdatePrompt.Hide();
        Activated -= OnUpdateWindowActivated;
        _updateTimer?.Stop();
        _updateUiTimer?.Stop();
        _updateLifetime.Cancel();
        _appUpdates.StateChanged -= OnAppUpdateStateChanged;
        _appUpdates.Dispose();
    }
}
