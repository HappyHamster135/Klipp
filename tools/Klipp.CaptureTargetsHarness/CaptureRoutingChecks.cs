using System.Runtime.CompilerServices;
using Klipp.Capture.Models;
using Klipp.Capture.Video;
using Klipp.Core.Enums;
using Klipp.Core.Models;
using Klipp.Desktop.Services;

internal static class CaptureRoutingChecks
{
    internal static async Task RunAsync(CaptureTarget target)
    {
        var calls = new List<string>();
        var game = new FakeSource(target, "game", calls);
        var windows = new FakeSource(target, "windows", calls);
        await using (var source = Create(game, windows, calls))
        {
            object? closeSender = null;
            source.TargetClosed += (sender, _) => closeSender = sender;
            await source.StartAsync();
            await source.StartAsync();
            Check(calls.SequenceEqual(["game-start"]), "A healthy game backend was started twice or unnecessarily fell back.");
            Check(source.BackendName == "DirectX 11 game capture", "The selected game backend was not reported.");
            await foreach (var frame in source.ReadSamplesAsync())
                Check(frame.Width == 2, "Frames were not read from the selected backend.");
            game.Close();
            Check(ReferenceEquals(closeSender, source), "Target-close events did not preserve the wrapper identity used by recording services.");
            await source.StopAsync();
        }
        Check(calls.Count(value => value == "game-dispose") == 1 && !calls.Contains("windows-start"),
            "A game source was not disposed exactly once.");

        calls.Clear();
        game = new FakeSource(target, "game", calls) { StartFailure = new InvalidOperationException("Unsupported swap chain"), CloseDuringStart = true };
        windows = new FakeSource(target, "windows", calls);
        await using (var source = Create(game, windows, calls))
        {
            var closes = 0;
            source.TargetClosed += (_, _) => closes++;
            await source.StartAsync();
            Check(calls.SequenceEqual(["game-start", "game-detach", "game-dispose", "all-detach", "windows-create", "windows-start"]),
                "Fallback started before the game source detached and disposed.");
            Check(closes == 0 && source.IsCapturing, "A failed candidate closed the recording instead of using safe fallback.");
            Check(source.BackendName == "Windows capture" && source.FallbackReason == "Unsupported swap chain",
                "Fallback status did not report the selected backend and original failure.");
            windows.Close();
            Check(closes == 1, "A Windows target-close event was lost after fallback.");
        }

        calls.Clear();
        windows = new FakeSource(target, "windows", calls);
        await using (var source = new RoutedVideoCaptureSource(target, null,
            () => { calls.Add("windows-create"); return windows; },
            _ => throw new InvalidOperationException("An unrequested game source was detached."),
            () => { calls.Add("all-detach"); return Task.CompletedTask; }))
        {
            await source.StartAsync();
            Check(calls.SequenceEqual(["all-detach", "windows-create", "windows-start"]),
                "Windows-only capture skipped the global clean-output barrier.");
        }

        calls.Clear();
        game = new FakeSource(target, "game", calls) { StartFailure = new InvalidOperationException("No clean frames") };
        windows = new FakeSource(target, "windows", calls);
        await using (var source = Create(game, windows, calls,
            detachFailure: new InvalidOperationException("Detach was not acknowledged")))
        {
            await ExpectFailureAsync(() => source.StartAsync(), "Detach was not acknowledged");
            Check(calls.Contains("game-dispose") && !calls.Contains("windows-create"),
                "A failed detach either leaked the candidate or allowed Windows capture to start.");
        }

        calls.Clear();
        await using (var source = new RoutedVideoCaptureSource(target, null,
            () => { calls.Add("windows-create"); return new FakeSource(target, "windows", calls); },
            _ => Task.CompletedTask,
            () => throw new InvalidOperationException("A previous popup did not detach")))
        {
            await ExpectFailureAsync(() => source.StartAsync(), "Game capture could not be safely detached");
            Check(!calls.Contains("windows-create"), "The global detach failure allowed an unsafe Windows capture.");
        }

        calls.Clear();
        using var cancellation = new CancellationTokenSource();
        game = new FakeSource(target, "game", calls)
        {
            BeforeStart = () => cancellation.Cancel(),
            StartFailure = new OperationCanceledException(cancellation.Token)
        };
        windows = new FakeSource(target, "windows", calls);
        await using (var source = Create(game, windows, calls))
        {
            var canceled = false;
            try { await source.StartAsync(cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && calls.SequenceEqual(["game-start", "game-detach", "game-dispose"]),
                "Cancellation leaked game capture or started another backend.");
        }

        calls.Clear();
        windows = new FakeSource(target, "windows", calls) { StartFailure = new InvalidOperationException("Window unavailable") };
        await using (var source = new RoutedVideoCaptureSource(target, null, () => windows,
            _ => Task.CompletedTask, () => Task.CompletedTask))
        {
            await ExpectFailureAsync(() => source.StartAsync(), "Window unavailable");
            Check(calls.SequenceEqual(["windows-start", "windows-dispose"]) && !source.IsCapturing,
                "A failed Windows source was retained after startup failed.");
        }
        Console.WriteLine("PASS: capture routing, ordered clean fallback, event identity, cancellation, and fail-closed detach barriers.");
    }

    private static RoutedVideoCaptureSource Create(FakeSource game, FakeSource windows, List<string> calls,
        Exception? detachFailure = null) => new(game.Target, () => game,
            () => { calls.Add("windows-create"); return windows; },
            _ =>
            {
                calls.Add("game-detach");
                if (detachFailure is not null) throw detachFailure;
                return Task.CompletedTask;
            },
            () => { calls.Add("all-detach"); return Task.CompletedTask; });

    private static async Task ExpectFailureAsync(Func<Task> action, string expected)
    {
        try { await action(); }
        catch (InvalidOperationException exception) when (exception.Message.Contains(expected, StringComparison.Ordinal)) { return; }
        throw new InvalidOperationException($"Expected capture failure: {expected}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeSource(CaptureTarget target, string name, List<string> calls) : IVideoCaptureSource
    {
        public CaptureTarget Target => target;
        public bool IsCapturing { get; private set; }
        public string? FailureMessage { get; private set; }
        public Exception? StartFailure { get; init; }
        public bool CloseDuringStart { get; init; }
        public Action? BeforeStart { get; init; }
        public event EventHandler? TargetClosed;
        public CaptureSourceStatistics GetStatistics() => default;
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            calls.Add($"{name}-start");
            BeforeStart?.Invoke();
            if (CloseDuringStart) Close();
            if (StartFailure is not null) throw StartFailure;
            IsCapturing = true;
            return Task.CompletedTask;
        }
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            calls.Add($"{name}-stop");
            IsCapturing = false;
            return Task.CompletedTask;
        }
        public async IAsyncEnumerable<VideoFrame> ReadSamplesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield return new VideoFrame(2, 2, PixelFormat.Bgra8, 0, new byte[16]);
        }
        public void Close()
        {
            IsCapturing = false;
            FailureMessage = "The selected source was closed.";
            TargetClosed?.Invoke(this, EventArgs.Empty);
        }
        public ValueTask DisposeAsync()
        {
            calls.Add($"{name}-dispose");
            IsCapturing = false;
            return ValueTask.CompletedTask;
        }
    }
}
