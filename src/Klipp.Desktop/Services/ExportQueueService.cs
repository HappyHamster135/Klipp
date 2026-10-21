using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Desktop.Models;

namespace Klipp.Desktop.Services;

public sealed class ExportQueueService : IDisposable
{
    private const int MaxHistoryItems = 50;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly List<EditorExportJob> _jobs = [];
    private readonly EditorProjectExportService _exporter;
    private readonly EditorProjectService _projectService;
    private readonly string _queueDirectory;
    private readonly string _historyPath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Task? _workerTask;
    private CancellationTokenSource? _activeJobCts;
    private string? _activeJobId;
    private bool _initialized;
    private bool _disposed;

    public ExportQueueService(
        EditorProjectExportService? exporter = null,
        string? queueDirectory = null)
    {
        _exporter = exporter ?? new EditorProjectExportService();
        _queueDirectory = queueDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Klipp",
            "Exports");
        _historyPath = Path.Combine(_queueDirectory, "history.json");
        _projectService = new EditorProjectService(Path.Combine(_queueDirectory, "Projects"));
    }

    public event EventHandler? JobsChanged;
    public event EventHandler<EditorExportJobEventArgs>? JobCompleted;

    public IReadOnlyList<EditorExportJob> Jobs
    {
        get
        {
            lock (_gate)
            {
                return _jobs
                    .OrderByDescending(job => job.CreatedUtc)
                    .Select(job => job.Copy())
                    .ToList();
            }
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Directory.CreateDirectory(_queueDirectory);

        List<EditorExportJob> loaded = [];
        if (File.Exists(_historyPath))
        {
            try
            {
                await using var stream = File.OpenRead(_historyPath);
                loaded = await JsonSerializer.DeserializeAsync<List<EditorExportJob>>(
                             stream,
                             JsonOptions,
                             cancellationToken).ConfigureAwait(false) ?? [];
            }
            catch (JsonException)
            {
                loaded = [];
            }
            catch (IOException)
            {
                loaded = [];
            }
        }

        lock (_gate)
        {
            if (_initialized)
                return;

            foreach (var job in loaded.OrderBy(job => job.CreatedUtc).TakeLast(MaxHistoryItems))
            {
                if (job.Status == EditorExportJobStatus.Rendering)
                {
                    job.Status = EditorExportJobStatus.Pending;
                    job.Progress = 0;
                    job.ErrorMessage = "Export resumed after Klipp was closed.";
                }

                if (job.Status == EditorExportJobStatus.Pending && !File.Exists(job.ProjectSnapshotPath))
                {
                    job.Status = EditorExportJobStatus.Failed;
                    job.ErrorMessage = "The queued project snapshot is missing.";
                }

                _jobs.Add(job);
            }

            _initialized = true;
            StartWorkerLocked();
        }

        await SaveHistoryAsync().ConfigureAwait(false);
        RaiseJobsChanged();
    }

    public async Task<EditorExportJob> EnqueueAsync(
        EditorProject project,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!_initialized)
            await InitializeAsync(cancellationToken).ConfigureAwait(false);

        var snapshot = EditorProjectService.Clone(project);
        snapshot.Normalize();
        var id = Guid.NewGuid().ToString("N");
        var projectPath = Path.Combine(_queueDirectory, "Projects", $"{id}.klippproject");
        await _projectService.SaveAsync(snapshot, projectPath, cancellationToken).ConfigureAwait(false);

        EditorExportJob job;
        lock (_gate)
        {
            job = new EditorExportJob
            {
                Id = id,
                ProjectName = string.IsNullOrWhiteSpace(snapshot.Name) ? "Untitled" : snapshot.Name,
                ProjectSnapshotPath = projectPath,
                OutputPath = GetUniqueOutputPathLocked(outputPath)
            };
            _jobs.Add(job);
            TrimHistoryLocked();
            StartWorkerLocked();
        }

        await SaveHistoryAsync().ConfigureAwait(false);
        RaiseJobsChanged();
        return job.Copy();
    }

    public async Task<bool> CancelAsync(string jobId)
    {
        ThrowIfDisposed();
        CancellationTokenSource? cancellation = null;
        var changed = false;

        lock (_gate)
        {
            var job = FindJobLocked(jobId);
            if (job?.Status == EditorExportJobStatus.Pending)
            {
                job.Status = EditorExportJobStatus.Cancelled;
                job.Progress = 0;
                job.CompletedUtc = DateTime.UtcNow;
                job.ErrorMessage = string.Empty;
                changed = true;
            }
            else if (job?.Status == EditorExportJobStatus.Rendering &&
                     string.Equals(_activeJobId, jobId, StringComparison.Ordinal))
            {
                cancellation = _activeJobCts;
                changed = true;
            }
        }

        cancellation?.Cancel();
        if (!changed)
            return false;

        await SaveHistoryAsync().ConfigureAwait(false);
        RaiseJobsChanged();
        return true;
    }

    public async Task<bool> RetryAsync(string jobId)
    {
        ThrowIfDisposed();
        var changed = false;
        lock (_gate)
        {
            var job = FindJobLocked(jobId);
            if (job is null || !job.CanRetry || !File.Exists(job.ProjectSnapshotPath))
                return false;

            job.OutputPath = GetUniqueOutputPathLocked(job.OutputPath, job.Id);
            job.Status = EditorExportJobStatus.Pending;
            job.Progress = 0;
            job.ErrorMessage = string.Empty;
            job.StartedUtc = null;
            job.CompletedUtc = null;
            changed = true;
            StartWorkerLocked();
        }

        if (!changed)
            return false;

        await SaveHistoryAsync().ConfigureAwait(false);
        RaiseJobsChanged();
        return true;
    }

    private void StartWorkerLocked()
    {
        if (!_initialized || _workerTask is { IsCompleted: false })
            return;

        _workerTask = Task.Run(ProcessQueueAsync);
    }

    private async Task ProcessQueueAsync()
    {
        while (!_lifetimeCts.IsCancellationRequested)
        {
            EditorExportJob? job;
            CancellationTokenSource jobCts;
            lock (_gate)
            {
                job = _jobs
                    .Where(candidate => candidate.Status == EditorExportJobStatus.Pending)
                    .OrderBy(candidate => candidate.CreatedUtc)
                    .FirstOrDefault();
                if (job is null)
                {
                    _workerTask = null;
                    return;
                }

                job.Status = EditorExportJobStatus.Rendering;
                job.Progress = 0;
                job.ErrorMessage = string.Empty;
                job.StartedUtc = DateTime.UtcNow;
                job.CompletedUtc = null;
                job.Attempt++;
                _activeJobId = job.Id;
                _activeJobCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
                jobCts = _activeJobCts;
            }

            await SaveHistoryAsync().ConfigureAwait(false);
            RaiseJobsChanged();

            try
            {
                var project = await _projectService.TryLoadAsync(job.ProjectSnapshotPath, jobCts.Token)
                    .ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The queued project snapshot could not be read.");
                Directory.CreateDirectory(Path.GetDirectoryName(job.OutputPath)!);

                var lastPercent = -1;
                var progress = new InlineProgress<double>(value =>
                {
                    var percent = Math.Clamp((int)Math.Round(value * 100), 0, 100);
                    var notify = false;
                    lock (_gate)
                    {
                        var current = FindJobLocked(job.Id);
                        if (current?.Status != EditorExportJobStatus.Rendering || percent == lastPercent)
                            return;

                        lastPercent = percent;
                        current.Progress = percent / 100.0;
                        notify = true;
                    }

                    if (notify)
                        RaiseJobsChanged();
                });

                await _exporter.ExportAsync(project, job.OutputPath, progress, jobCts.Token)
                    .ConfigureAwait(false);

                EditorExportJob completed;
                lock (_gate)
                {
                    var current = FindJobLocked(job.Id)!;
                    current.Status = EditorExportJobStatus.Completed;
                    current.Progress = 1;
                    current.CompletedUtc = DateTime.UtcNow;
                    current.ErrorMessage = string.Empty;
                    completed = current.Copy();
                }

                await SaveHistoryAsync().ConfigureAwait(false);
                RaiseJobsChanged();
                JobCompleted?.Invoke(this, new EditorExportJobEventArgs(completed));
            }
            catch (OperationCanceledException) when (jobCts.IsCancellationRequested)
            {
                try { File.Delete(job.OutputPath); } catch { }
                lock (_gate)
                {
                    var current = FindJobLocked(job.Id);
                    if (current is not null)
                    {
                        current.Status = EditorExportJobStatus.Cancelled;
                        current.Progress = 0;
                        current.CompletedUtc = DateTime.UtcNow;
                        current.ErrorMessage = string.Empty;
                    }
                }

                await SaveHistoryAsync().ConfigureAwait(false);
                RaiseJobsChanged();
            }
            catch (Exception ex)
            {
                try { File.Delete(job.OutputPath); } catch { }
                lock (_gate)
                {
                    var current = FindJobLocked(job.Id);
                    if (current is not null)
                    {
                        current.Status = EditorExportJobStatus.Failed;
                        current.CompletedUtc = DateTime.UtcNow;
                        current.ErrorMessage = ex.Message;
                    }
                }

                await SaveHistoryAsync().ConfigureAwait(false);
                RaiseJobsChanged();
            }
            finally
            {
                lock (_gate)
                {
                    if (string.Equals(_activeJobId, job.Id, StringComparison.Ordinal))
                    {
                        _activeJobId = null;
                        _activeJobCts?.Dispose();
                        _activeJobCts = null;
                    }
                }
            }
        }
    }

    private EditorExportJob? FindJobLocked(string id) =>
        _jobs.FirstOrDefault(job => string.Equals(job.Id, id, StringComparison.Ordinal));

    private string GetUniqueOutputPathLocked(string requestedPath, string? ignoredJobId = null)
    {
        var directory = Path.GetDirectoryName(requestedPath) ?? _queueDirectory;
        var name = Path.GetFileNameWithoutExtension(requestedPath);
        var extension = Path.GetExtension(requestedPath);
        var candidate = requestedPath;
        var suffix = 2;
        while (File.Exists(candidate) || _jobs.Any(job =>
                   !string.Equals(job.Id, ignoredJobId, StringComparison.Ordinal) &&
                   string.Equals(job.OutputPath, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = Path.Combine(directory, $"{name}_{suffix++}{extension}");
        }

        return candidate;
    }

    private void TrimHistoryLocked()
    {
        while (_jobs.Count > MaxHistoryItems)
        {
            var removable = _jobs
                .Where(job => !job.CanCancel)
                .OrderBy(job => job.CreatedUtc)
                .FirstOrDefault();
            if (removable is null)
                return;

            _jobs.Remove(removable);
            try { File.Delete(removable.ProjectSnapshotPath); } catch { }
        }
    }

    private async Task SaveHistoryAsync()
    {
        List<EditorExportJob> snapshot;
        lock (_gate)
            snapshot = _jobs.Select(job => job.Copy()).ToList();

        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_queueDirectory);
            var temporaryPath = _historyPath + ".tmp";
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }

            File.Move(temporaryPath, _historyPath, overwrite: true);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void RaiseJobsChanged() => JobsChanged?.Invoke(this, EventArgs.Empty);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _lifetimeCts.Cancel();
        _activeJobCts?.Cancel();
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public InlineProgress(Action<T> report)
        {
            _report = report;
        }

        public void Report(T value) => _report(value);
    }
}
