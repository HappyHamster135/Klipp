using System.Diagnostics;

namespace Klipp.Core.Performance;

public enum BackgroundWorkPriority
{
    Interactive = 0,
    Visible = 1,
    Background = 2
}

public sealed record BackgroundWorkCategorySnapshot(
    string Category,
    int Queued,
    int Running,
    long CacheHits,
    long CacheMisses,
    long Completed,
    long Failed,
    long Canceled,
    double AverageDurationMilliseconds,
    string? LastError);

public sealed record PriorityBackgroundWorkSnapshot(
    int QueuedInteractive,
    int QueuedVisible,
    int QueuedBackground,
    int Running,
    IReadOnlyList<BackgroundWorkCategorySnapshot> Categories)
{
    public int Queued => QueuedInteractive + QueuedVisible + QueuedBackground;
    public long CacheHits => Categories.Sum(category => category.CacheHits);
    public long CacheMisses => Categories.Sum(category => category.CacheMisses);
    public long Completed => Categories.Sum(category => category.Completed);
    public long Failed => Categories.Sum(category => category.Failed);
}

public sealed class PriorityBackgroundWorkQueue : IAsyncDisposable
{
    private sealed record WorkItem(
        string Category,
        BackgroundWorkPriority Priority,
        CancellationToken CancellationToken,
        Func<CancellationToken, Task<object?>> Callback,
        TaskCompletionSource<object?> Completion);

    private sealed class CategoryMetrics
    {
        public int Running { get; set; }
        public long CacheHits { get; set; }
        public long CacheMisses { get; set; }
        public long Completed { get; set; }
        public long Failed { get; set; }
        public long Canceled { get; set; }
        public long DurationSamples { get; set; }
        public long TotalDurationTicks { get; set; }
        public string? LastError { get; set; }
    }

    private readonly object _sync = new();
    private readonly PriorityQueue<WorkItem, (int Priority, long Sequence)> _pending = new();
    private readonly Dictionary<string, CategoryMetrics> _metrics = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private long _sequence;
    private bool _disposed;

    public PriorityBackgroundWorkQueue(int maxConcurrency = 2)
    {
        if (maxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));

        _workers = Enumerable.Range(0, maxConcurrency)
            .Select(_ => Task.Run(WorkerAsync))
            .ToArray();
    }

    public async Task<T> RunAsync<T>(
        string category,
        BackgroundWorkPriority priority,
        Func<CancellationToken, Task<T>> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();

        var completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(
            category.Trim(),
            priority,
            cancellationToken,
            async token => await callback(token).ConfigureAwait(false),
            completion);

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending.Enqueue(item, ((int)priority, _sequence++));
            GetMetrics(item.Category);
        }

        _signal.Release();
        using var registration = cancellationToken.Register(
            static state =>
            {
                var (source, token) = ((TaskCompletionSource<object?>, CancellationToken))state!;
                source.TrySetCanceled(token);
            },
            (completion, cancellationToken));

        var result = await completion.Task.ConfigureAwait(false);
        return result is null ? default! : (T)result;
    }

    public void RecordCacheHit(string category) => UpdateCacheMetric(category, cacheHit: true);

    public void RecordCacheMiss(string category) => UpdateCacheMetric(category, cacheHit: false);

    public PriorityBackgroundWorkSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var queuedByCategory = _pending.UnorderedItems
                .GroupBy(item => item.Element.Category, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var queuedInteractive = _pending.UnorderedItems.Count(item =>
                item.Element.Priority == BackgroundWorkPriority.Interactive);
            var queuedVisible = _pending.UnorderedItems.Count(item =>
                item.Element.Priority == BackgroundWorkPriority.Visible);
            var queuedBackground = _pending.Count - queuedInteractive - queuedVisible;
            var categories = _metrics
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair =>
                {
                    var metrics = pair.Value;
                    var averageMilliseconds = metrics.DurationSamples == 0
                        ? 0
                        : TimeSpan.FromTicks(metrics.TotalDurationTicks / metrics.DurationSamples).TotalMilliseconds;
                    return new BackgroundWorkCategorySnapshot(
                        pair.Key,
                        queuedByCategory.GetValueOrDefault(pair.Key),
                        metrics.Running,
                        metrics.CacheHits,
                        metrics.CacheMisses,
                        metrics.Completed,
                        metrics.Failed,
                        metrics.Canceled,
                        averageMilliseconds,
                        metrics.LastError);
                })
                .ToList();

            return new PriorityBackgroundWorkSnapshot(
                queuedInteractive,
                queuedVisible,
                queuedBackground,
                categories.Sum(category => category.Running),
                categories);
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<WorkItem> abandoned;
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            abandoned = [];
            while (_pending.TryDequeue(out var item, out _))
                abandoned.Add(item);
        }

        foreach (var item in abandoned)
            item.Completion.TrySetCanceled(_shutdown.Token);

        _shutdown.Cancel();
        _signal.Release(_workers.Length);
        try { await Task.WhenAll(_workers).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _signal.Dispose();
        _shutdown.Dispose();
    }

    private async Task WorkerAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);

                WorkItem? item;
                lock (_sync)
                    _pending.TryDequeue(out item, out _);
                if (item is null)
                    continue;

                if (item.CancellationToken.IsCancellationRequested || item.Completion.Task.IsCompleted)
                {
                    RecordCanceled(item.Category, TimeSpan.Zero, includeDuration: false);
                    continue;
                }

                lock (_sync)
                    GetMetrics(item.Category).Running++;

                var stopwatch = Stopwatch.StartNew();
                try
                {
                    using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        item.CancellationToken,
                        _shutdown.Token);
                    var result = await item.Callback(linkedCancellation.Token).ConfigureAwait(false);
                    item.Completion.TrySetResult(result);
                    RecordCompleted(item.Category, stopwatch.Elapsed);
                }
                catch (OperationCanceledException)
                {
                    item.Completion.TrySetCanceled(
                        item.CancellationToken.IsCancellationRequested
                            ? item.CancellationToken
                            : _shutdown.Token);
                    RecordCanceled(item.Category, stopwatch.Elapsed, includeDuration: true);
                }
                catch (Exception ex)
                {
                    item.Completion.TrySetException(ex);
                    RecordFailed(item.Category, stopwatch.Elapsed, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private void UpdateCacheMetric(string category, bool cacheHit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        lock (_sync)
        {
            var metrics = GetMetrics(category.Trim());
            if (cacheHit)
                metrics.CacheHits++;
            else
                metrics.CacheMisses++;
        }
    }

    private void RecordCompleted(string category, TimeSpan duration)
    {
        lock (_sync)
        {
            var metrics = GetMetrics(category);
            metrics.Running--;
            metrics.Completed++;
            AddDuration(metrics, duration);
        }
    }

    private void RecordFailed(string category, TimeSpan duration, string error)
    {
        lock (_sync)
        {
            var metrics = GetMetrics(category);
            metrics.Running--;
            metrics.Failed++;
            metrics.LastError = error;
            AddDuration(metrics, duration);
        }
    }

    private void RecordCanceled(string category, TimeSpan duration, bool includeDuration)
    {
        lock (_sync)
        {
            var metrics = GetMetrics(category);
            if (includeDuration)
            {
                metrics.Running--;
                AddDuration(metrics, duration);
            }
            metrics.Canceled++;
        }
    }

    private CategoryMetrics GetMetrics(string category)
    {
        if (!_metrics.TryGetValue(category, out var metrics))
        {
            metrics = new CategoryMetrics();
            _metrics[category] = metrics;
        }
        return metrics;
    }

    private static void AddDuration(CategoryMetrics metrics, TimeSpan duration)
    {
        metrics.DurationSamples++;
        metrics.TotalDurationTicks += Math.Max(0, duration.Ticks);
    }
}
