using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Klipp.Desktop.Services;

public enum AppUpdateStatus
{
    InstallerRequired,
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Ready,
    Error
}

public sealed record AppUpdateSnapshot(
    AppUpdateStatus Status,
    string CurrentVersion,
    string? AvailableVersion,
    int ProgressPercent,
    string Message,
    DateTimeOffset? LastCheckedUtc);

/// <summary>An SDK release remains opaque to the state machine and its tests.</summary>
public sealed record AppUpdateRelease(string Version, object Handle);

public interface IAppUpdateClient
{
    bool IsInstalled { get; }
    string CurrentVersion { get; }
    AppUpdateRelease? GetPendingRestart();
    Task<AppUpdateRelease?> CheckAsync(CancellationToken cancellationToken);
    Task DownloadAsync(AppUpdateRelease release, Action<int> progress, CancellationToken cancellationToken);
    void PrepareRestart(AppUpdateRelease release);
}

/// <summary>
/// Checks and stages updates away from capture/preview work. Installation is
/// explicit: the host must save its work and finish capture before PrepareRestart.
/// </summary>
public sealed class AppUpdateService : IDisposable
{
    public const string ApplicationId = "HappyHamster135.Klipp";
    public const string RepositoryUrl = "https://github.com/HappyHamster135/Klipp";
    public static readonly Uri InstallerUri = new(RepositoryUrl + "/releases/latest/download/Klipp-Setup.exe");
    public static readonly Uri ReleasesUri = new(RepositoryUrl + "/releases/latest");
    public static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan FailedCheckRetryInterval = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ManualCheckInterval = TimeSpan.FromSeconds(15);

    private readonly object _sync = new();
    private readonly IAppUpdateClient _client;
    private readonly string _statePath;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _operation;
    private CancellationTokenSource? _downloadCancellation;
    private AppUpdateRelease? _available;
    private AppUpdateRelease? _ready;
    private DateTimeOffset? _lastAttemptUtc;
    private DateTimeOffset? _lastCheckedUtc;
    private DateTimeOffset? _lastDownloadAttemptUtc;
    private bool _downloadNeedsRetry;
    private AppUpdateSnapshot _snapshot;
    private bool _disposed;
    private bool _restartPrepared;

    public event EventHandler? StateChanged;
    public AppUpdateSnapshot Snapshot => Volatile.Read(ref _snapshot);
    public bool IsInstalled => _client.IsInstalled;

    public AppUpdateService(IAppUpdateClient? client = null, string? statePath = null, TimeProvider? timeProvider = null)
    {
        _client = client ?? new VelopackAppUpdateClient();
        _statePath = statePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Klipp", "updates", "check-state.json");
        _time = timeProvider ?? TimeProvider.System;
        ReadCheckState();
        _snapshot = new(_client.IsInstalled ? AppUpdateStatus.Idle : AppUpdateStatus.InstallerRequired,
            _client.CurrentVersion, null, 0, _client.IsInstalled
                ? "Klipp checks for updates when it is idle."
                : "Install Klipp once to receive automatic updates. Your clips and projects are kept.", _lastCheckedUtc);
    }

    public Task CheckAsync(bool downloadAutomatically, bool manual = false, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_disposed || _restartPrepared) return Task.CompletedTask;
            if (_operation is { IsCompleted: false }) return _operation;
            _operation = Task.Run(() => CheckCoreAsync(downloadAutomatically, manual, cancellationToken));
            return _operation;
        }
    }

    public Task DownloadAsync(CancellationToken cancellationToken = default) => DownloadAsync(manual: true, cancellationToken);

    public Task DownloadAsync(bool manual, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_disposed || _restartPrepared) return Task.CompletedTask;
            if (_operation is { IsCompleted: false }) return _operation;
            _operation = Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
                if (_available is not null && IsNewerStableRelease(_available, _ready?.Version ?? _client.CurrentVersion))
                    await DownloadCoreAsync(_available, manual, linked.Token).ConfigureAwait(false);
            });
            return _operation;
        }
    }

    public void CancelDownload()
    {
        lock (_sync) _downloadCancellation?.Cancel();
    }

    /// <summary>
    /// Arms the native updater without exiting this process. After this returns
    /// true the host must exit promptly; Velopack waits up to 60 seconds for it.
    /// Never call during a recording, replay save, export or unsaved editor work.
    /// </summary>
    public bool PrepareRestart()
    {
        lock (_sync)
        {
            if (_disposed || _ready is null || _operation is { IsCompleted: false }) return false;
            if (_restartPrepared) return true;
            try
            {
                _client.PrepareRestart(_ready);
                _restartPrepared = true;
                return true;
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"[Klipp] Could not prepare update restart: {exception.GetType().Name}");
                Publish(AppUpdateStatus.Ready, "Could not start the updater. Try Restart to update again.", 100, _ready.Version);
                return false;
            }
        }
    }

    private async Task CheckCoreAsync(bool downloadAutomatically, bool manual, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var token = linked.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            if (!_client.IsInstalled)
            {
                Publish(AppUpdateStatus.InstallerRequired,
                    "Install Klipp once to receive automatic updates. Your clips and projects are kept.");
                return;
            }

            if (_ready is null && _client.GetPendingRestart() is { } pending)
            {
                // Restore the verified package immediately, even offline. Future
                // idle checks may still replace it with a newer stable release.
                _ready = pending;
                PublishReady();
                return;
            }
            if (downloadAutomatically && _available is not null &&
                IsNewerStableRelease(_available, _ready?.Version ?? _client.CurrentVersion))
            {
                await DownloadCoreAsync(_available, manual, token).ConfigureAwait(false);
                return;
            }

            var now = _time.GetUtcNow();
            var interval = manual ? ManualCheckInterval : !_downloadNeedsRetry && _lastCheckedUtc >= _lastAttemptUtc
                ? AutomaticCheckInterval : FailedCheckRetryInterval;
            if (_lastAttemptUtc is { } attempted && now - attempted < interval)
            {
                PublishKnownUpdate();
                return;
            }
            _lastAttemptUtc = now;
            await SaveCheckStateAsync().ConfigureAwait(false);
            Publish(AppUpdateStatus.Checking, "Checking GitHub for updates…");
            var release = await _client.CheckAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _lastCheckedUtc = _time.GetUtcNow();
            if (release is not null && !IsNewerStableRelease(release, _ready?.Version ?? _client.CurrentVersion))
                release = null;
            if (release is null) _downloadNeedsRetry = false;
            await SaveCheckStateAsync().ConfigureAwait(false);
            _available = release;
            if (release is null)
            {
                if (_ready is not null) PublishReady();
                else Publish(AppUpdateStatus.UpToDate, "You have the latest version of Klipp.");
                return;
            }

            Publish(AppUpdateStatus.Available, $"Klipp {release.Version} is available.", 0, release.Version);
            if (downloadAutomatically) await DownloadCoreAsync(release, manual, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (!_disposed)
            {
                if (_ready is not null) PublishReady("The newer-update check was canceled.");
                else if (Snapshot.Status == AppUpdateStatus.Checking)
                    Publish(AppUpdateStatus.Idle, "Update check canceled. You can check again later.");
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[Klipp] Update check failed: {exception.GetType().Name}");
            if (_ready is not null) PublishReady(DescribeError(exception, downloading: false));
            else Publish(AppUpdateStatus.Error, DescribeError(exception, downloading: false));
        }
    }

    private async Task DownloadCoreAsync(AppUpdateRelease release, bool manual, CancellationToken cancellationToken)
    {
        using var download = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            download.Token.ThrowIfCancellationRequested();
            var now = _time.GetUtcNow();
            if (!manual && _lastDownloadAttemptUtc is { } attempted && now - attempted < FailedCheckRetryInterval)
            {
                if (_ready is not null) PublishReady();
                return;
            }
            lock (_sync)
            {
                if (_disposed) return;
                _downloadCancellation = download;
            }
            _lastDownloadAttemptUtc = now;
            _downloadNeedsRetry = true;
            await SaveCheckStateAsync().ConfigureAwait(false);
            download.Token.ThrowIfCancellationRequested();
            Publish(AppUpdateStatus.Downloading, $"Downloading Klipp {release.Version}…", 0, release.Version);
            await _client.DownloadAsync(release, percent =>
            {
                if (!download.IsCancellationRequested)
                    Publish(AppUpdateStatus.Downloading, $"Downloading Klipp {release.Version}…", Math.Clamp(percent, 0, 100), release.Version);
            }, download.Token).ConfigureAwait(false);
            download.Token.ThrowIfCancellationRequested();
            // The SDK verifies the package checksum before exposing a prepared
            // full release. Never offer restart based on progress alone.
            var prepared = _client.GetPendingRestart();
            if (prepared is null || !SameVersion(prepared.Version, release.Version) ||
                !IsNewerStableRelease(prepared, _ready?.Version ?? _client.CurrentVersion))
                throw new InvalidDataException("The requested verified update was not prepared.");
            _ready = prepared;
            _downloadNeedsRetry = false;
            await SaveCheckStateAsync().ConfigureAwait(false);
            PublishReady();
        }
        catch (OperationCanceledException) when (download.IsCancellationRequested)
        {
            if (!_disposed)
            {
                var hadReady = _ready is not null;
                if (RevalidateReady()) PublishReady("The newer download was paused. You can retry it later.");
                else Publish(AppUpdateStatus.Available, hadReady
                    ? "Download paused. The previous update is no longer prepared. Download this update again before restarting."
                    : "Download paused. You can download the update again.", 0, release.Version);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[Klipp] Update download failed: {exception.GetType().Name}");
            if (_disposed) return;
            var hadReady = _ready is not null;
            var message = DescribeError(exception, downloading: true);
            if (RevalidateReady()) PublishReady(message);
            else Publish(AppUpdateStatus.Available, message + (hadReady
                ? " The previous update is no longer prepared. Download this update again before restarting."
                : string.Empty), 0, release.Version);
        }
        finally
        {
            lock (_sync)
                if (ReferenceEquals(_downloadCancellation, download)) _downloadCancellation = null;
        }
    }

    private bool RevalidateReady()
    {
        if (_ready is null) return false;
        try
        {
            // The SDK may clean the previous package even when a replacement
            // download fails. Never keep a restart handle to a removed file.
            var pending = _client.GetPendingRestart();
            if (pending is not null && SameVersion(pending.Version, _ready.Version))
            {
                _ready = pending;
                return true;
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[Klipp] Could not revalidate the pending update: {exception.GetType().Name}");
        }
        _ready = null;
        return false;
    }

    private void PublishKnownUpdate()
    {
        if (_available is not null && IsNewerStableRelease(_available, _ready?.Version ?? _client.CurrentVersion))
            Publish(AppUpdateStatus.Available, $"Klipp {_available.Version} is available.", 0, _available.Version);
        else if (_ready is not null)
            PublishReady();
    }

    private void PublishReady(string? detail = null)
    {
        if (_ready is null) return;
        Publish(AppUpdateStatus.Ready,
            $"Klipp {_ready.Version} is ready. " + (detail ?? "Restart when you have finished editing or recording."),
            100, _ready.Version);
    }

    private static bool IsNewerStableRelease(AppUpdateRelease release, string baseline) =>
        SemanticVersion.TryParse(release.Version, out var offered) && !offered.IsPrerelease &&
        SemanticVersion.TryParse(baseline, out var current) && offered.CompareTo(current) > 0;

    private static bool SameVersion(string first, string second) =>
        SemanticVersion.TryParse(first, out var left) && SemanticVersion.TryParse(second, out var right) &&
        left.CompareTo(right) == 0;

    private void Publish(AppUpdateStatus status, string message, int progress = 0, string? availableVersion = null)
    {
        AppUpdateSnapshot next;
        lock (_sync)
        {
            if (_disposed) return;
            next = new(status, _client.CurrentVersion, availableVersion, progress, message, _lastCheckedUtc);
            if (next == _snapshot) return;
            Volatile.Write(ref _snapshot, next);
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ReadCheckState()
    {
        try
        {
            if (!File.Exists(_statePath) || new FileInfo(_statePath).Length > 4096) return;
            var state = JsonSerializer.Deserialize<CheckState>(File.ReadAllText(_statePath));
            var latest = _time.GetUtcNow().AddMinutes(1);
            _lastAttemptUtc = state?.LastAttemptUtc <= latest ? state?.LastAttemptUtc : null;
            _lastCheckedUtc = state?.LastCheckedUtc <= latest ? state?.LastCheckedUtc : null;
            _lastDownloadAttemptUtc = state?.LastDownloadAttemptUtc <= latest ? state?.LastDownloadAttemptUtc : null;
            _downloadNeedsRetry = state?.DownloadNeedsRetry == true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A cache failure must never prevent capture or opening the app.
        }
    }

    private async Task SaveCheckStateAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_statePath))!;
            Directory.CreateDirectory(directory);
            var temporary = _statePath + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new CheckState(_lastAttemptUtc, _lastCheckedUtc, _lastDownloadAttemptUtc, _downloadNeedsRetry))).ConfigureAwait(false);
            File.Move(temporary, _statePath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Klipp] Update check cache could not be saved: {exception.GetType().Name}");
        }
    }

    private static string DescribeError(Exception exception, bool downloading) => exception switch
    {
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests } =>
            "GitHub is limiting update checks. Try again later.",
        HttpRequestException or TaskCanceledException => "Could not reach GitHub. Check your connection and try again later.",
        ArgumentException when !downloading => "This GitHub release does not include an installer update yet. Try again later.",
        IOException or UnauthorizedAccessException when downloading => "The update could not be prepared. Check free disk space and try downloading again.",
        _ => downloading ? "The update could not be downloaded. Try again later."
            : "Could not check for updates. Try again later."
    };

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            _downloadCancellation?.Cancel();
            StateChanged = null;
        }
    }

    private sealed record CheckState(DateTimeOffset? LastAttemptUtc, DateTimeOffset? LastCheckedUtc, DateTimeOffset? LastDownloadAttemptUtc, bool DownloadNeedsRetry);
}

/// <summary>Only stable installer releases from Klipp's public repository.</summary>
public sealed class VelopackAppUpdateClient : IAppUpdateClient
{
    private readonly UpdateManager _manager;
    private readonly string _fallbackVersion;
    public bool IsInstalled => _manager.IsInstalled && !_manager.IsPortable &&
        string.Equals(_manager.AppId, AppUpdateService.ApplicationId, StringComparison.Ordinal);
    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? _fallbackVersion;

    public VelopackAppUpdateClient(UpdateManager? manager = null)
    {
        _manager = manager ?? new UpdateManager(new GithubSource(AppUpdateService.RepositoryUrl,
            accessToken: null, prerelease: false, downloader: new KlippUpdateDownloader()),
            new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = false });
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        _fallbackVersion = version is null ? "1.0.9" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    public AppUpdateRelease? GetPendingRestart()
    {
        var release = IsInstalled ? _manager.UpdatePendingRestart : null;
        return release is null ? null : new(release.Version.ToString(), release);
    }

    public async Task<AppUpdateRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return update is null ? null : new(update.TargetFullRelease.Version.ToString(), update);
    }

    public Task DownloadAsync(AppUpdateRelease release, Action<int> progress, CancellationToken cancellationToken) =>
        _manager.DownloadUpdatesAsync((UpdateInfo)release.Handle, progress, cancellationToken);

    public void PrepareRestart(AppUpdateRelease release) =>
        _manager.WaitExitThenApplyUpdates((VelopackAsset)release.Handle, silent: true, restart: true);

    private sealed class KlippUpdateDownloader : HttpClientFileDownloader
    {
        // Metadata is small; a disconnected launch must not wait through the
        // SDK's long package-transfer timeout. Full downloads retain that timeout.
        public override Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            base.DownloadString(url, headers, Math.Min(timeout, 0.5));

        public override Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30) =>
            base.DownloadBytes(url, headers, Math.Min(timeout, 0.5));
    }
}
