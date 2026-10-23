using System.Runtime.InteropServices;
using System.Text.Json;

namespace Klipp.MediaServer;

public sealed partial class PrivateMediaStore(MediaOptions options, IStorageGuard guard)
{
    private readonly SemaphoreSlim[] _clipLocks = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private readonly SemaphoreSlim _mutations = new(4, 4);
    private readonly Dictionary<Guid, AccountSlots> _accounts = [];
    private readonly object _accountGate = new();

    public IStorageGuard Guard => guard;
    public string AssetPath(Guid owner, Guid clip, string asset)
    {
        if (owner == Guid.Empty || clip == Guid.Empty || asset is not ("video" or "poster"))
            throw MediaException.Unavailable();
        var path = Path.Combine(options.StorageRoot, "clips", owner.ToString("D"), clip.ToString("D"), asset == "video" ? "video.mp4" : "poster.jpg");
        LinuxStorageGuard.RejectLinks(path);
        return path;
    }

    public async Task<IAsyncDisposable> AcquireClipAsync(Guid owner, Guid clip, CancellationToken token)
    {
        if (owner == Guid.Empty || clip == Guid.Empty) throw MediaException.Unavailable();
        AccountSlots account;
        lock (_accountGate)
        {
            if (!_accounts.TryGetValue(owner, out account!)) _accounts.Add(owner, account = new AccountSlots());
            account.References++;
        }
        var clipLock = _clipLocks[(uint)clip.GetHashCode() % _clipLocks.Length];
        var hasAccount = false;
        var hasClip = false;
        var hasGlobal = false;
        void Release()
        {
            if (hasGlobal) _mutations.Release();
            if (hasClip) clipLock.Release();
            if (hasAccount) account.Slots.Release();
            lock (_accountGate)
            {
                if (--account.References == 0) { _accounts.Remove(owner); account.Slots.Dispose(); }
            }
        }
        try
        {
            hasAccount = await account.Slots.WaitAsync(TimeSpan.FromSeconds(2), token);
            if (!hasAccount) throw Busy();
            // Duplicate edits wait here without reserving the global work budget.
            hasClip = await clipLock.WaitAsync(TimeSpan.FromSeconds(10), token);
            if (!hasClip) throw Busy();
            hasGlobal = await _mutations.WaitAsync(TimeSpan.FromSeconds(10), token);
            if (!hasGlobal) throw Busy();
            return new AsyncRelease(Release);
        }
        catch { Release(); throw; }
    }

    private static MediaException Busy() => new(429, "server_busy", "Clip storage is busy. Try again shortly.");
    private sealed class AccountSlots { public readonly SemaphoreSlim Slots = new(2, 2); public int References; }

    public async Task<IAsyncDisposable> AcquireBackupReadLockAsync(CancellationToken token)
    {
        guard.Verify();
        Directory.CreateDirectory(options.StorageRoot);
        var lockPath = Path.Combine(options.StorageRoot, ".mutation.lock");
        LinuxStorageGuard.RejectLinks(lockPath);
        var file = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            if (OperatingSystem.IsLinux())
            {
                while (NativeMethods.Flock(file.SafeFileHandle.DangerousGetHandle().ToInt32(), 1 | 4) != 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != 11 && error != 4) throw MediaException.StorageUnavailable();
                    await Task.Delay(50, token);
                }
            }
            return new AsyncRelease(file.Dispose);
        }
        catch { file.Dispose(); throw; }
    }

    public async Task StoreUploadAsync(Guid owner, Guid clip, string asset, Stream source, long expectedBytes,
        IMediaValidator validator, CancellationToken token)
    {
        var limit = asset == "video" ? MediaOptions.MaximumVideoBytes : MediaOptions.MaximumPosterBytes;
        if (expectedBytes <= 0 || expectedBytes > limit) throw new MediaException(413, "clip_too_large", "The prepared clip exceeds the upload limit.");
        guard.VerifySpace(expectedBytes);
        var finalPath = AssetPath(owner, clip, asset);
        if (File.Exists(finalPath)) throw new MediaException(409, "already_uploaded", "This file has already been uploaded.");
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        LinuxStorageGuard.RejectLinks(finalPath);
        var temporary = Path.Combine(Path.GetDirectoryName(finalPath)!, $".{asset}.{Guid.NewGuid():N}.partial");
        try
        {
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                long count = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) != 0)
                {
                    count += read;
                    if (count > expectedBytes || count > limit) throw new MediaException(413, "invalid_size", "The uploaded file does not match its reservation.");
                    await destination.WriteAsync(buffer.AsMemory(0, read), token);
                }
                if (count != expectedBytes) throw new MediaException(400, "invalid_size", "The uploaded file does not match its reservation.");
                await destination.FlushAsync(token);
                destination.Flush(flushToDisk: true);
            }
            if (asset == "video") await validator.ValidateVideoAsync(temporary, token);
            else await validator.ValidatePosterAsync(temporary, token);
            await using var backupLock = await AcquireBackupReadLockAsync(token);
            guard.Verify();
            File.Move(temporary, finalPath, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public FileStream OpenAsset(Guid owner, Guid clip, string asset)
    {
        guard.Verify();
        var path = AssetPath(owner, clip, asset);
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan); }
        catch (FileNotFoundException) { throw MediaException.Unavailable(); }
        catch (DirectoryNotFoundException) { throw MediaException.Unavailable(); }
    }

    public void DeleteAssets(Guid owner, Guid clip)
    {
        guard.Verify();
        foreach (var asset in new[] { "video", "poster" })
        {
            var path = AssetPath(owner, clip, asset);
            if (File.Exists(path)) File.Delete(path);
        }
        var directory = Path.GetDirectoryName(AssetPath(owner, clip, "video"))!;
        var manifest = Path.Combine(directory, "manifest.json");
        LinuxStorageGuard.RejectLinks(manifest);
        if (File.Exists(manifest)) File.Delete(manifest);
        if (Directory.Exists(directory))
        {
            foreach (var partial in Directory.EnumerateFiles(directory, "*.partial"))
            { LinuxStorageGuard.RejectLinks(partial); File.Delete(partial); }
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    public async Task WriteManifestAsync(GatewayClip clip, MediaEvidence evidence, JsonElement published, CancellationToken token)
    {
        guard.Verify();
        var directory = Path.GetDirectoryName(AssetPath(clip.OwnerId, clip.Id, "video"))!;
        var final = Path.Combine(directory, "manifest.json");
        LinuxStorageGuard.RejectLinks(final);
        var temporary = Path.Combine(directory, $".manifest.{Guid.NewGuid():N}.partial");
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await System.Text.Json.JsonSerializer.SerializeAsync(file, new { version = 1, owner_id = clip.OwnerId,
                    clip_id = clip.Id, video_sha256 = evidence.VideoSha256, poster_sha256 = evidence.PosterSha256,
                    video_bytes = evidence.VideoBytes, poster_bytes = evidence.PosterBytes,
                    duration_seconds = evidence.DurationSeconds, clip = published }, cancellationToken: token);
                await file.FlushAsync(token);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, final, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed class AsyncRelease(Action release) : IAsyncDisposable
    {
        private Action? _release = release;
        public ValueTask DisposeAsync() { Interlocked.Exchange(ref _release, null)?.Invoke(); return ValueTask.CompletedTask; }
    }

    private static partial class NativeMethods
    {
        [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
        public static partial int Flock(int fd, int operation);
    }
}
