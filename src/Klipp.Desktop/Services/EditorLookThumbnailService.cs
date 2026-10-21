using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Performance;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Desktop.Services;

/// <summary>Small, cached previews made from the selected footage using the export look filters.</summary>
public sealed class EditorLookThumbnailService
{
    private const string WorkCategory = "Effect previews";
    private const string CacheVersion = "v1";
    private static readonly MediaCacheGenerationGate GenerationGate = new();
    private readonly FFmpegLocator _locator;
    private readonly PriorityBackgroundWorkQueue _workQueue;

    public EditorLookThumbnailService(
        FFmpegLocator? locator = null,
        PriorityBackgroundWorkQueue? workQueue = null,
        string? cacheDirectory = null)
    {
        _locator = locator ?? new FFmpegLocator();
        _workQueue = workQueue ?? MediaPreviewWorkQueue.Shared;
        CacheDirectory = Path.GetFullPath(cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Klipp", "effect-previews"));
        Directory.CreateDirectory(CacheDirectory);
    }

    public string CacheDirectory { get; }

    public async Task<string?> GetOrCreateAsync(
        string sourcePath,
        double sourceSeconds,
        string look,
        BackgroundWorkPriority priority,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                return null;
            var source = new FileInfo(Path.GetFullPath(sourcePath));
            var seconds = double.IsFinite(sourceSeconds) ? Math.Max(0, sourceSeconds) : 0;
            var seek = seconds.ToString("F6", CultureInfo.InvariantCulture);
            var sourceKey = Hash(string.Join('|', CacheVersion, source.FullName.ToUpperInvariant(),
                source.Length.ToString(CultureInfo.InvariantCulture),
                source.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), seek));
            var basePath = Path.Combine(CacheDirectory, $"{sourceKey}-source.png");
            var filters = string.Join(',', ClipEditService.GetVfxFilters(look?.Trim().ToLowerInvariant() ?? "none"));
            var lookPath = string.IsNullOrEmpty(filters)
                ? basePath
                : Path.Combine(CacheDirectory, $"{sourceKey}-{Hash(filters)}.png");

            if (HasContent(lookPath))
            {
                _workQueue.RecordCacheHit(WorkCategory);
                TryTouch(lookPath);
                return lookPath;
            }
            // Pictures are optional. Merely opening the effect browser must never download FFmpeg.
            if (!_locator.IsInstalled)
                return null;

            var cachedSource = await GetOrGenerateAsync(basePath, priority,
                (temporaryPath, token) => RenderAsync(source.FullName, temporaryPath,
                    "scale=320:180:force_original_aspect_ratio=decrease:flags=lanczos," +
                    "pad=320:180:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,format=rgb24", seek, token), ct)
                .ConfigureAwait(false);
            if (cachedSource is null || string.IsNullOrEmpty(filters))
                return cachedSource;

            return await GetOrGenerateAsync(lookPath, priority,
                (temporaryPath, token) => RenderAsync(cachedSource, temporaryPath,
                    filters + ",format=rgb24", seek: null, token), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { ct.ThrowIfCancellationRequested(); throw; }
        catch
        {
            return null;
        }
    }

    private async Task<string?> GetOrGenerateAsync(
        string cachePath,
        BackgroundWorkPriority priority,
        Func<string, CancellationToken, Task<bool>> generate,
        CancellationToken ct)
    {
        if (HasContent(cachePath))
        {
            _workQueue.RecordCacheHit(WorkCategory);
            TryTouch(cachePath);
            return cachePath;
        }

        // Acquire before queue admission: the two shared workers must never wait behind the same frame.
        using var lease = await GenerationGate.EnterAsync(cachePath, ct).ConfigureAwait(false);
        if (HasContent(cachePath))
        {
            _workQueue.RecordCacheHit(WorkCategory);
            TryTouch(cachePath);
            return cachePath;
        }

        _workQueue.RecordCacheMiss(WorkCategory);
        // Keep the lease until the worker has closed its process and removed its temporary file.
        // Queue cancellation otherwise completes RunAsync before the running callback has cleaned up.
        return await _workQueue.RunAsync<string?>(WorkCategory, priority, async queueToken =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, queueToken);
            var token = linked.Token;
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(CacheDirectory);
            var temporaryPath = Path.Combine(CacheDirectory,
                $".{Path.GetFileNameWithoutExtension(cachePath)}-{Guid.NewGuid():N}.tmp.png");
            try
            {
                if (!await generate(temporaryPath, token).ConfigureAwait(false) || !HasContent(temporaryPath))
                    return null;
                token.ThrowIfCancellationRequested();
                File.Move(temporaryPath, cachePath, overwrite: true);
                return cachePath;
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<bool> RenderAsync(
        string inputPath,
        string outputPath,
        string filters,
        string? seek,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var startInfo = new ProcessStartInfo
        {
            FileName = _locator.FFmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "1", "-filter_threads", "1" })
            startInfo.ArgumentList.Add(argument);
        if (seek is not null)
        {
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(seek);
        }
        foreach (var argument in new[] { "-i", inputPath, "-an", "-sn", "-dn", "-frames:v", "1", "-vf", filters,
                     "-c:v", "png", "-threads", "1", "-y", outputPath })
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null)
            return false;
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        using var registration = ct.Register(static state =>
        {
            try
            {
                var running = (Process)state!;
                if (!running.HasExited)
                    running.Kill(entireProcessTree: true);
            }
            catch { }
        }, process);
        var errorTask = process.StandardError.ReadToEndAsync();
        // Cancellation kills the process first; wait for its exit before releasing the file lease.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        await errorTask.ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return process.ExitCode == 0;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..32];

    private static bool HasContent(string path)
    {
        try { return new FileInfo(path) is { Exists: true, Length: > 0 }; }
        catch { return false; }
    }

    private static void TryTouch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
