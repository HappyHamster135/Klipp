using System.Collections.Concurrent;
using Klipp.Core.Performance;

namespace Klipp.Tests.Performance;

public sealed class PriorityBackgroundWorkQueueTests
{
    [Fact]
    public async Task InteractiveWorkRunsBeforeQueuedBackgroundWork()
    {
        await using var queue = new PriorityBackgroundWorkQueue(maxConcurrency: 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<string>();

        var first = queue.RunAsync(
            "Thumbnail",
            BackgroundWorkPriority.Background,
            async cancellationToken =>
            {
                order.Enqueue("running");
                started.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return 1;
            });
        await started.Task;

        var background = queue.RunAsync(
            "Thumbnail",
            BackgroundWorkPriority.Background,
            _ =>
            {
                order.Enqueue("background");
                return Task.FromResult(2);
            });
        var interactive = queue.RunAsync(
            "Waveform",
            BackgroundWorkPriority.Interactive,
            _ =>
            {
                order.Enqueue("interactive");
                return Task.FromResult(3);
            });

        release.SetResult();
        await Task.WhenAll(first, background, interactive);

        order.ShouldBe(["running", "interactive", "background"]);
    }

    [Fact]
    public async Task SnapshotIncludesCacheAndExecutionMetrics()
    {
        await using var queue = new PriorityBackgroundWorkQueue(maxConcurrency: 1);
        queue.RecordCacheHit("Thumbnail");
        queue.RecordCacheMiss("Thumbnail");

        await queue.RunAsync(
            "Thumbnail",
            BackgroundWorkPriority.Visible,
            _ => Task.FromResult("done"));

        var snapshot = queue.GetSnapshot();
        var thumbnail = snapshot.Categories.Single(category => category.Category == "Thumbnail");

        snapshot.Queued.ShouldBe(0);
        snapshot.Running.ShouldBe(0);
        thumbnail.CacheHits.ShouldBe(1);
        thumbnail.CacheMisses.ShouldBe(1);
        thumbnail.Completed.ShouldBe(1);
        thumbnail.Failed.ShouldBe(0);
        thumbnail.AverageDurationMilliseconds.ShouldBeGreaterThanOrEqualTo(0);
    }
}
