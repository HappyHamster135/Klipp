using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Performance;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Desktop.Services;

/// <summary>
/// Genererar och cachar thumbnail-bilder (en stillbild ur videon) för klippkort.
/// Använder FFmpeg för att plocka en frame och spara som JPG i en cache-mapp.
/// </summary>
public sealed class ThumbnailService
{
    private const string WorkCategory = "Thumbnails";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> GenerationLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly FFmpegLocator _locator;
    private readonly PriorityBackgroundWorkQueue _workQueue;
    private readonly string _thumbnailDirectory;

    public ThumbnailService(
        FFmpegLocator? locator = null,
        PriorityBackgroundWorkQueue? workQueue = null)
    {
        _locator = locator ?? new FFmpegLocator();
        _workQueue = workQueue ?? MediaPreviewWorkQueue.Shared;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _thumbnailDirectory = Path.Combine(localAppData, "Klipp", "thumbnails");
        Directory.CreateDirectory(_thumbnailDirectory);
    }

    /// <summary>
    /// Returnerar sökväg till en thumbnail för videon. Genererar den om den inte finns.
    /// Returnerar null om generering misslyckas (då visar kortet sin fallback-ikon).
    /// </summary>
    public async Task<string?> GetOrCreateThumbnailAsync(
        string videoPath,
        CancellationToken cancellationToken = default) =>
        await GetOrCreateThumbnailAsync(
                videoPath,
                BackgroundWorkPriority.Background,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<string?> GetOrCreateThumbnailAsync(
        string videoPath,
        BackgroundWorkPriority priority,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(videoPath)) return null;

        var fullVideoPath = Path.GetFullPath(videoPath);
        var cacheKey = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(fullVideoPath.ToUpperInvariant())))[..12];
        var thumbName = $"{Path.GetFileNameWithoutExtension(videoPath)}-{cacheKey}.jpg";
        var thumbPath = Path.Combine(_thumbnailDirectory, thumbName);

        if (IsCurrent(thumbPath, fullVideoPath))
        {
            _workQueue.RecordCacheHit(WorkCategory);
            TryTouch(thumbPath);
            return thumbPath;
        }

        _workQueue.RecordCacheMiss(WorkCategory);
        try
        {
            return await _workQueue.RunAsync(
                    WorkCategory,
                    priority,
                    token => GenerateThumbnailAsync(fullVideoPath, thumbPath, token),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    private async Task<string?> GenerateThumbnailAsync(
        string videoPath,
        string thumbPath,
        CancellationToken cancellationToken)
    {
        var generationLock = GenerationLocks.GetOrAdd(thumbPath, _ => new SemaphoreSlim(1, 1));
        await generationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsCurrent(thumbPath, videoPath))
            {
                TryTouch(thumbPath);
                return thumbPath;
            }

            var ffmpegPath = await _locator.GetFFmpegPathAsync(progress: null, cancellationToken)
                .ConfigureAwait(false);
            var temporaryPath = Path.Combine(
                _thumbnailDirectory,
                $".{Path.GetFileNameWithoutExtension(thumbPath)}-{Guid.NewGuid():N}.tmp.jpg");

            try
            {
                var ok = await ExtractFrameAsync(
                        ffmpegPath,
                        videoPath,
                        temporaryPath,
                        "00:00:01",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!ok || !HasContent(temporaryPath))
                {
                    ok = await ExtractFrameAsync(
                            ffmpegPath,
                            videoPath,
                            temporaryPath,
                            "00:00:00",
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!ok || !HasContent(temporaryPath))
                    return null;

                File.Move(temporaryPath, thumbPath, overwrite: true);
                return thumbPath;
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
        finally
        {
            generationLock.Release();
        }
    }

    private static bool IsCurrent(string cachePath, string sourcePath)
    {
        try
        {
            return HasContent(cachePath) &&
                   File.GetLastWriteTimeUtc(cachePath) >= File.GetLastWriteTimeUtc(sourcePath);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasContent(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length > 0; }
        catch { return false; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static void TryTouch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }
    }

    private static async Task<bool> ExtractFrameAsync(
        string ffmpegPath,
        string videoPath,
        string thumbPath,
        string seekTime,
        CancellationToken cancellationToken)
    {
        // -ss FÖRE -i = snabb seek. Vi håller jobbet litet eftersom detta körs i bakgrunden.
        var args = $"-nostdin -hide_banner -loglevel error -threads 1 -ss {seekTime} " +
                   $"-i \"{videoPath}\" -an -sn -dn -frames:v 1 " +
                   $"-vf scale=300:-2 -q:v 5 -y \"{thumbPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null) return false;

        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        using var cancellationRegistration = cancellationToken.Register(
            static state =>
            {
                try
                {
                    var runningProcess = (Process)state!;
                    if (!runningProcess.HasExited)
                        runningProcess.Kill(entireProcessTree: true);
                }
                catch { }
            },
            process);

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stderrTask.ConfigureAwait(false);

        return process.ExitCode == 0;
    }
}
