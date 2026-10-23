using System.Threading.RateLimiting;

namespace Klipp.MediaServer;

/// <summary>A shared byte budget applies to every file/range; video leases last until stream disposal.</summary>
public sealed class PlaybackGovernor : IDisposable
{
    private readonly MediaOptions _options;
    private readonly RateLimiter _bytes;
    private readonly Dictionary<string, ClientStreams> _clients = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private int _streams;
    private int _assetStreams;
    private int _disposed;

    public PlaybackGovernor(MediaOptions options) : this(options, new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
    {
        TokenLimit = options.PlaybackBurstBytes, TokensPerPeriod = options.PlaybackBytesPerSecond / 5,
        ReplenishmentPeriod = TimeSpan.FromMilliseconds(200), AutoReplenishment = true,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst, QueueLimit = 8 * 1024 * 1024
    })) { }

    public PlaybackGovernor(MediaOptions options, RateLimiter byteBudget) { _options = options; _bytes = byteBudget; }

    public Stream Open(Func<Stream> open, string client, bool video, CancellationToken aborted, Action? abort = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        aborted.ThrowIfCancellationRequested();
        var released = 0;
        lock (_gate)
        {
            var count = _clients.GetValueOrDefault(client);
            if (_assetStreams >= _options.MaximumPlaybackStreams || (count?.All ?? 0) >= _options.MaximumPlaybackStreamsPerClient
                || video && (_streams >= _options.MaximumVideoStreams || (count?.Videos ?? 0) >= _options.MaximumVideoStreamsPerClient))
                throw new MediaException(429, "playback_busy", "Too many videos are playing. Try again shortly.");
            if (count is null) _clients.Add(client, count = new ClientStreams());
            _assetStreams++; count.All++;
            if (video) { _streams++; count.Videos++; }
        }
        void Release()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            lock (_gate)
            {
                _assetStreams--;
                var count = _clients[client];
                if (video) { _streams--; count.Videos--; }
                if (--count.All == 0) _clients.Remove(client);
            }
        }
        Stream? source = null;
        try
        {
            source = open();
            return new BudgetStream(source, _bytes, Release, aborted, _stopping.Token, TimeSpan.FromSeconds(_options.PlaybackStreamTimeoutSeconds), abort);
        }
        catch { try { source?.Dispose(); } finally { Release(); } throw; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        _bytes.Dispose();
        _stopping.Dispose();
    }

    private sealed class ClientStreams { public int All; public int Videos; }

    private sealed class BudgetStream : Stream
    {
        private readonly Stream _source;
        private readonly RateLimiter _bytes;
        private readonly Action? _release;
        private readonly CancellationTokenSource _lifetime;
        private readonly CancellationTokenRegistration _registration;
        private int _disposed;
        private int _cleaned;

        public BudgetStream(Stream source, RateLimiter bytes, Action? release, CancellationToken aborted,
            CancellationToken stopping, TimeSpan timeout, Action? abort)
        {
            _source = source; _bytes = bytes; _release = release;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(aborted, stopping);
            _lifetime.CancelAfter(timeout);
            _registration = _lifetime.Token.Register(() =>
            {
                // A slow reader must not retain a slot while no ReadAsync is pending.
                try { CloseSource(); } catch (Exception) { /* Cancellation must never escape a timer callback. */ }
                try { abort?.Invoke(); } catch (Exception) { /* The transport may already be closed. */ }
            });
        }

        public override bool CanRead => _source.CanRead;
        public override bool CanSeek => _source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _source.Length;
        public override long Position { get => _source.Position; set => _source.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => _source.Seek(offset, origin);
        public override void Flush() => _source.Flush();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
            try
            {
                var count = await _source.ReadAsync(buffer[..Math.Min(buffer.Length, 64 * 1024)], linked.Token);
                if (count != 0)
                {
                    using var allowance = await _bytes.AcquireAsync(count, linked.Token);
                    if (!allowance.IsAcquired) throw new IOException("The playback byte budget is busy.");
                }
                return count;
            }
            catch { await DisposeAsync(); throw; }
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        private void CloseSource()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            { try { _source.Dispose(); } finally { _release?.Invoke(); } }
        }
        private void CleanupCancellation()
        {
            if (Interlocked.Exchange(ref _cleaned, 1) != 0) return;
            _registration.Dispose();
            _lifetime.Dispose();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { CloseSource(); } finally { CleanupCancellation(); } }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            { try { await _source.DisposeAsync(); } finally { _release?.Invoke(); CleanupCancellation(); } }
            else CleanupCancellation();
            GC.SuppressFinalize(this);
        }
    }
}
