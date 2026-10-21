using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Klipp.Desktop.Services;

/// <summary>Serializes work for one cache entry without retaining every historical cache key.</summary>
internal sealed class MediaCacheGenerationGate
{
    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public async Task<IDisposable> EnterAsync(string key, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries.Add(key, entry);
            }
            entry.Users++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, key, entry);
        }
        catch
        {
            Release(key, entry, ownsSemaphore: false);
            throw;
        }
    }

    private void Release(string key, Entry entry, bool ownsSemaphore)
    {
        lock (_sync)
        {
            if (ownsSemaphore)
                entry.Semaphore.Release();
            if (--entry.Users == 0)
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Lease(MediaCacheGenerationGate owner, string key, Entry entry) : IDisposable
    {
        private MediaCacheGenerationGate? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Release(key, entry, ownsSemaphore: true);
    }
}
