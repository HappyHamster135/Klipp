using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Klipp.MediaServer;
using Microsoft.Extensions.Logging.Abstractions;

static class HardeningChecks
{
    public static async Task RunAsync(MediaOptions options, Action<bool, string> check)
    {
        await MutationsAsync(options, check);
        await PlaybackAsync(check);
        await JanitorAsync(options, check);
        await GatewayAsync(options, check);
    }

    private static async Task CanceledAsync(Task task, Action<bool, string> check, string description)
    {
        try { await task; throw new Exception("Expected cancellation: " + description); }
        catch (OperationCanceledException) { check(true, description); }
    }

    private static async Task MutationsAsync(MediaOptions options, Action<bool, string> check)
    {
        var store = new PrivateMediaStore(options, new FixtureGuard());
        var used = new HashSet<uint>();
        Guid UniqueClip()
        {
            Guid result;
            do { result = Guid.NewGuid(); } while (!used.Add((uint)result.GetHashCode() % 64));
            return result;
        }
        var owner = Guid.NewGuid();
        var clip = UniqueClip();
        await using (var first = await store.AcquireClipAsync(owner, clip, default))
        {
            using var stalled = new CancellationTokenSource();
            var input = new StalledStream();
            var upload = store.StoreUploadAsync(owner, clip, "video", input, 1, new UnusedValidator(), stalled.Token);
            await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using var duplicateCancellation = new CancellationTokenSource();
            var duplicates = Enumerable.Range(0, 3).Select(_ => store.AcquireClipAsync(owner, clip, duplicateCancellation.Token)).ToArray();
            var peers = new List<IAsyncDisposable>();
            try
            {
                for (var i = 0; i < 3; i++) peers.Add(await store.AcquireClipAsync(Guid.NewGuid(), UniqueClip(), default).WaitAsync(TimeSpan.FromSeconds(2)));
                check(true, "duplicate slow uploads do not occupy other accounts' global mutation permits");
            }
            finally { foreach (var peer in peers) await peer.DisposeAsync(); }
            duplicateCancellation.Cancel();
            foreach (var duplicate in duplicates) await CanceledAsync(duplicate, check, "canceled duplicate releases waiting account/clip permits");
            stalled.Cancel();
            await CanceledAsync(upload, check, "canceled slow upload removes its partial file");
            check(!Directory.EnumerateFiles(options.StorageRoot, "*.partial", SearchOption.AllDirectories).Any(), "canceled upload leaves no partial bytes");
        }
        await using (var first = await store.AcquireClipAsync(owner, UniqueClip(), default))
        await using (var second = await store.AcquireClipAsync(owner, UniqueClip(), default))
        {
            using var canceled = new CancellationTokenSource();
            var third = store.AcquireClipAsync(owner, UniqueClip(), canceled.Token);
            check(!third.IsCompleted, "one account cannot hold more than two mutation slots");
            canceled.Cancel();
            await CanceledAsync(third, check, "per-account wait cancellation returns its reference");
        }
        var active = new List<IAsyncDisposable>();
        var waitingOwner = Guid.NewGuid();
        var waitingClip = UniqueClip();
        try
        {
            for (var i = 0; i < 4; i++) active.Add(await store.AcquireClipAsync(Guid.NewGuid(), UniqueClip(), default));
            using var canceled = new CancellationTokenSource();
            var fifth = store.AcquireClipAsync(waitingOwner, waitingClip, canceled.Token);
            check(!fifth.IsCompleted, "global mutation concurrency remains bounded to four");
            canceled.Cancel();
            await CanceledAsync(fifth, check, "global wait cancellation releases its acquired clip lock");
        }
        finally { foreach (var item in active) await item.DisposeAsync(); }
        await using var recovered = await store.AcquireClipAsync(waitingOwner, waitingClip, default).WaitAsync(TimeSpan.FromSeconds(2));
        check(true, "all mutation permits are reusable after cancellation and completion");
    }

    private static async Task PlaybackAsync(Action<bool, string> check)
    {
        var options = new MediaOptions { MaximumVideoStreams = 2, MaximumVideoStreamsPerClient = 1 };
        using var bucket = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        { TokenLimit = 65536, TokensPerPeriod = 65536, ReplenishmentPeriod = TimeSpan.FromDays(1), AutoReplenishment = false,
            QueueLimit = 131072, QueueProcessingOrder = QueueProcessingOrder.OldestFirst });
        using var governor = new PlaybackGovernor(options, bucket);
        Stream Open(string client, bool video = true, CancellationToken aborted = default) => governor.Open(() => new MemoryStream(new byte[131072]), client, video, aborted);
        var first = Open("one");
        void Busy(Action action, string description)
        {
            try { action(); throw new Exception("Expected stream rejection."); }
            catch (MediaException error) { check(error.StatusCode == 429 && error.Code == "playback_busy", description); }
        }
        Busy(() => Open("one"), "per-client video limit rejects extra streams before opening a file");
        var second = Open("two");
        Busy(() => Open("three"), "global video limit rejects excess streams");
        await using (var head = Open("one", false)) check(head.CanSeek && head.Length == 131072, "HEAD and thumbnails preserve seek/length without taking video slots");
        check(first.Seek(16, SeekOrigin.Begin) == 16 && first.Position == 16, "throttled streams retain exact range seeking");
        var data = new byte[32768];
        check(await first.ReadAsync(data) == data.Length && await second.ReadAsync(data) == data.Length,
            "separate streams consume one aggregate byte budget");
        check(bucket.GetStatistics()!.CurrentAvailablePermits == 0, "aggregate byte tokens cannot reset by opening another stream");
        using var cancellation = new CancellationTokenSource();
        var blocked = first.ReadAsync(data, cancellation.Token).AsTask();
        check(!blocked.IsCompleted, "exhausted bandwidth waits instead of sending unlimited clip bytes");
        cancellation.Cancel();
        await CanceledAsync(blocked, check, "canceled throttled read releases its video slot");
        await first.DisposeAsync();
        await first.DisposeAsync();
        await using (var reused = Open("one")) check(true, "stream cancellation and double disposal do not leak or over-release permits");
        await second.DisposeAsync();
        try { governor.Open(() => throw new IOException("Fixture open failure"), "failure", true, default); }
        catch (IOException) { }
        await using (var reused = Open("failure")) check(true, "failed file open releases its playback permit");
        using var aborted = new CancellationTokenSource();
        var stopped = Open("stopped", aborted: aborted.Token);
        var pending = stopped.ReadAsync(data).AsTask();
        aborted.Cancel();
        await CanceledAsync(pending, check, "request abort cancels a queued bandwidth wait and frees the stream");
        await using (var reused = Open("stopped")) check(true, "aborted request leaves playback capacity available");
        await stopped.DisposeAsync();
        using var idleAbort = new CancellationTokenSource();
        await using var idle = Open("idle", aborted: idleAbort.Token);
        idleAbort.Cancel();
        await using (var reused = Open("idle")) check(true, "request abort releases a stream even while no read is pending");

        var timedOptions = new MediaOptions { MaximumVideoStreams = 1, MaximumVideoStreamsPerClient = 1, PlaybackStreamTimeoutSeconds = 1 };
        using var timedGovernor = new PlaybackGovernor(timedOptions);
        var timedAbort = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var timed = timedGovernor.Open(() => new MemoryStream(new byte[16]), "timed", true, default, () => timedAbort.TrySetResult());
        await timedAbort.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await using var replacement = timedGovernor.Open(() => new MemoryStream(new byte[16]), "timed", true, default);
        check(true, "absolute playback lifetime aborts idle responses and releases capacity");
        timedGovernor.Dispose();
        check(!replacement.CanRead, "governor shutdown closes active streams immediately");
        try { timedGovernor.Open(() => new MemoryStream(), "late", true, default); throw new Exception("Expected shutdown rejection."); }
        catch (ObjectDisposedException) { check(true, "shutdown rejects new streams without leaking capacity"); }

        using var posters = new PlaybackGovernor(new MediaOptions { MaximumPlaybackStreams = 2, MaximumPlaybackStreamsPerClient = 1 });
        using var posterAbort = new CancellationTokenSource();
        await using var posterOne = posters.Open(() => new MemoryStream(new byte[16]), "poster-one", false, posterAbort.Token);
        Busy(() => posters.Open(() => new MemoryStream(), "poster-one", false, default), "thumbnail streams also obey a per-client file limit");
        await using var posterTwo = posters.Open(() => new MemoryStream(new byte[16]), "poster-two", false, default);
        Busy(() => posters.Open(() => new MemoryStream(), "poster-three", false, default), "thumbnail streams also obey the aggregate file limit");
        posterAbort.Cancel();
        await using var posterReuse = posters.Open(() => new MemoryStream(new byte[16]), "poster-one", false, default);
        check(true, "aborted thumbnail responses release file descriptors and playback slots");
    }

    private static async Task JanitorAsync(MediaOptions options, Action<bool, string> check)
    {
        var guard = new FixtureGuard();
        var store = new PrivateMediaStore(options, guard);
        var owner = Guid.NewGuid();
        var id = Guid.NewGuid();
        var video = store.AssetPath(owner, id, "video");
        Directory.CreateDirectory(Path.GetDirectoryName(video)!);
        await File.WriteAllTextAsync(video, "expired fixture");
        await File.WriteAllTextAsync(store.AssetPath(owner, id, "poster"), "expired poster");
        var gateway = new ExpiryFixture(owner, id) { FailFinishOnce = true };
        gateway.BeforeFinish = () => check(!File.Exists(video) && !File.Exists(store.AssetPath(owner, id, "poster")), "expiry cleanup removes both files before releasing quota");
        var janitor = new ExpiryJanitor(gateway, store, NullLogger<ExpiryJanitor>.Instance);
        check(await janitor.SweepAsync(default) == 0 && !gateway.Finished, "failed expiry acknowledgement retains the reservation for retry");
        check(await janitor.SweepAsync(default) == 1 && gateway.Finished, "expiry tombstone cleanup retries safely after files are gone");
        gateway.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sweep = janitor.SweepAsync(default);
        var calls = gateway.Claims;
        check(await janitor.SweepAsync(default) == 0 && gateway.Claims == calls, "periodic cleanup sweeps never overlap");
        gateway.Gate.SetResult();
        await sweep;
        gateway.Gate = null;
        guard.Unavailable = true;
        var before = gateway.Claims;
        try { await janitor.SweepAsync(default); throw new Exception("Expected mount rejection."); }
        catch (MediaException) { check(gateway.Claims == before, "missing storage mount prevents claiming expiry work"); }
        guard.Unavailable = false;
    }

    private static async Task GatewayAsync(MediaOptions options, Action<bool, string> check)
    {
        var id = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var calls = 0;
        using var http = new HttpClient(new FixtureHandler(async request =>
        {
            calls++;
            check(request.Headers.Authorization is null && request.Headers.GetValues("apikey").Single() == options.SupabasePublishableKey
                && request.Headers.GetValues("x-klipp-media-secret").Single() == options.GatewaySecret,
                "maintenance RPC uses only public key and scoped proof, never a user or service-role token");
            var body = await request.Content!.ReadAsStringAsync();
            if (request.RequestUri!.AbsolutePath.EndsWith("community_home_claim_expired_uploads", StringComparison.Ordinal))
            {
                check(JsonDocument.Parse(body).RootElement.GetProperty("p_limit").GetInt32() == 10, "expiry claims request a bounded batch of ten");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new[] { new { id, owner_id = owner } }), Encoding.UTF8, "application/json") };
            }
            check(JsonDocument.Parse(body).RootElement.GetProperty("p_clip_id").GetGuid() == id, "maintenance acknowledgement names only the cleaned clip");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"deleted\":true}", Encoding.UTF8, "application/json") };
        }));
        var gateway = new SupabaseCommunityGateway(http, options);
        var claims = await gateway.ClaimExpiredAsync(default);
        check(claims.Count == 1 && claims[0] == new ExpiredUpload(id, owner), "maintenance parses exact owner/clip cleanup identifiers");
        await gateway.FinishExpiredAsync(id, default);
        check(calls == 2, "expiry cleanup needs no authentication-session requests");
    }
}

sealed class EmptyExpiryGateway : IExpiryGateway
{
    public Task<IReadOnlyList<ExpiredUpload>> ClaimExpiredAsync(CancellationToken cancellation) => Task.FromResult<IReadOnlyList<ExpiredUpload>>([]);
    public Task FinishExpiredAsync(Guid id, CancellationToken cancellation) => throw new InvalidOperationException();
}

sealed class ExpiryFixture(Guid owner, Guid id) : IExpiryGateway
{
    public int Claims { get; private set; }
    public bool Finished { get; private set; }
    public bool FailFinishOnce { get; set; }
    public Action? BeforeFinish { get; set; }
    public TaskCompletionSource? Gate { get; set; }
    public async Task<IReadOnlyList<ExpiredUpload>> ClaimExpiredAsync(CancellationToken cancellation)
    { Claims++; if (Gate is not null) await Gate.Task.WaitAsync(cancellation); return Finished ? [] : [new(id, owner)]; }
    public Task FinishExpiredAsync(Guid requested, CancellationToken cancellation)
    {
        BeforeFinish?.Invoke();
        if (FailFinishOnce) { FailFinishOnce = false; throw MediaException.StorageUnavailable(); }
        if (requested != id) throw MediaException.Unavailable();
        Finished = true;
        return Task.CompletedTask;
    }
}

sealed class FixtureHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }

sealed class UnusedValidator : IMediaValidator
{
    public Task<double> ValidateVideoAsync(string path, CancellationToken token) => throw new InvalidOperationException();
    public Task ValidatePosterAsync(string path, CancellationToken token) => throw new InvalidOperationException();
}

sealed class StalledStream : Stream
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => 1;
    public override long Position { get => 0; set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
