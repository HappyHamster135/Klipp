using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Performance;
using Klipp.Desktop.Models;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Desktop.Services;

public sealed class PreviewProxyService
{
    private const string WorkCategory = "Proxies";
    private const string ProxyFormatVersion = "v2";
    private readonly FFmpegLocator _locator;
    private readonly MediaProbeService _mediaProbeService;
    private readonly PriorityBackgroundWorkQueue _workQueue;
    private readonly MediaCacheGenerationGate _generationGate = new();

    public PreviewProxyService(
        string? cacheDirectory = null,
        FFmpegLocator? locator = null,
        MediaProbeService? mediaProbeService = null,
        PriorityBackgroundWorkQueue? workQueue = null)
    {
        _locator = locator ?? new FFmpegLocator();
        _mediaProbeService = mediaProbeService ?? new MediaProbeService(_locator);
        _workQueue = workQueue ?? MediaPreviewWorkQueue.Shared;
        CacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Klipp",
            "proxies");
        Directory.CreateDirectory(CacheDirectory);
    }

    public string CacheDirectory { get; }

    public static string NormalizeQuality(string? quality) => PreviewProxyQuality.Normalize(quality);

    public static int GetTargetHeight(string? quality) => NormalizeQuality(quality) switch
    {
        "720p" => 720,
        "540p" => 540,
        _ => 0
    };

    public static int GetTargetWidth(string? quality) => NormalizeQuality(quality) switch
    {
        "720p" => 1280,
        "540p" => 960,
        _ => 0
    };

    public string GetProxyPath(string sourcePath, string quality)
    {
        var normalizedQuality = NormalizeQuality(quality);
        if (normalizedQuality == "original")
            return Path.GetFullPath(sourcePath);

        var source = new FileInfo(Path.GetFullPath(sourcePath));
        var fingerprint = string.Join(
            '|',
            ProxyFormatVersion,
            source.FullName.ToUpperInvariant(),
            source.Exists ? source.Length : -1,
            source.Exists ? source.LastWriteTimeUtc.Ticks : 0,
            normalizedQuality);
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fingerprint)))
            .ToLowerInvariant();
        return Path.Combine(CacheDirectory, $"{hash[..24]}-{normalizedQuality}.mp4");
    }

    public bool TryGetExistingProxy(string sourcePath, string quality, out string proxyPath)
    {
        proxyPath = GetProxyPath(sourcePath, quality);
        if (NormalizeQuality(quality) == "original")
            return File.Exists(proxyPath);

        try
        {
            var exists = File.Exists(proxyPath) && new FileInfo(proxyPath).Length > 1024;
            if (exists)
                TryTouch(proxyPath);
            return exists;
        }
        catch
        {
            return false;
        }
    }

    public long GetExistingProxySize(string sourcePath, string quality)
    {
        if (!TryGetExistingProxy(sourcePath, quality, out var proxyPath))
            return 0;

        try { return new FileInfo(proxyPath).Length; }
        catch { return 0; }
    }

    public bool DeleteProxy(string sourcePath, string quality)
    {
        var normalizedQuality = NormalizeQuality(quality);
        if (normalizedQuality == "original")
            return false;

        var proxyPath = GetProxyPath(sourcePath, normalizedQuality);
        try
        {
            if (!File.Exists(proxyPath))
                return false;

            File.Delete(proxyPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> GetOrCreateProxyAsync(
        string sourcePath,
        string quality,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default,
        BackgroundWorkPriority priority = BackgroundWorkPriority.Interactive)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedQuality = NormalizeQuality(quality);
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
            throw new FileNotFoundException("Proxy source is missing.", fullSourcePath);
        if (normalizedQuality == "original")
            return fullSourcePath;
        if (TryGetExistingProxy(fullSourcePath, normalizedQuality, out var existingPath))
        {
            _workQueue.RecordCacheHit(WorkCategory);
            progress?.Report(1);
            return existingPath;
        }

        // Wait outside the shared worker queue: repeated requests for the same source
        // must not occupy workers needed by thumbnails, waveforms, or another proxy.
        using var lease = await _generationGate.EnterAsync(
            GetProxyPath(fullSourcePath, normalizedQuality), cancellationToken).ConfigureAwait(false);
        if (TryGetExistingProxy(fullSourcePath, normalizedQuality, out existingPath))
        {
            _workQueue.RecordCacheHit(WorkCategory);
            progress?.Report(1);
            return existingPath;
        }

        _workQueue.RecordCacheMiss(WorkCategory);
        var generationStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generationState = 0; // 0 = queued, 1 = running, 2 = abandoned before starting
        try
        {
            return await _workQueue.RunAsync(
                    WorkCategory,
                    priority,
                    async token =>
                    {
                        if (Interlocked.CompareExchange(ref generationState, 1, 0) != 0)
                            throw new OperationCanceledException(token);
                        try
                        {
                            return await GenerateProxyCoreAsync(
                                fullSourcePath,
                                normalizedQuality,
                                progress,
                                token).ConfigureAwait(false);
                        }
                        finally
                        {
                            generationStopped.TrySetResult();
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // The queue completes a canceled caller immediately. Retain the cache
            // gate until its FFmpeg worker has actually stopped and removed its temp file.
            if (Interlocked.CompareExchange(ref generationState, 2, 0) == 1)
                await generationStopped.Task.ConfigureAwait(false);
            throw;
        }
    }

    private async Task<string> GenerateProxyCoreAsync(
        string fullSourcePath,
        string normalizedQuality,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var proxyPath = GetProxyPath(fullSourcePath, normalizedQuality);
        if (TryGetExistingProxy(fullSourcePath, normalizedQuality, out var existingPath))
        {
            progress?.Report(1);
            return existingPath;
        }

        var sourceInfo = await _mediaProbeService.GetMediaInfoAsync(fullSourcePath, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Could not inspect proxy source.");
        var ffmpegPath = await _locator.GetFFmpegPathAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var temporaryPath = Path.Combine(
            CacheDirectory,
            $".{Path.GetFileNameWithoutExtension(proxyPath)}-{Guid.NewGuid():N}.tmp.mp4");

        try
        {
            await GenerateProxyAsync(
                ffmpegPath,
                fullSourcePath,
                temporaryPath,
                normalizedQuality,
                sourceInfo.Duration.TotalSeconds,
                progress is null ? null : new EncodingProgress(progress),
                cancellationToken).ConfigureAwait(false);
            await ValidateProxyAsync(temporaryPath, sourceInfo, normalizedQuality, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(proxyPath, GetProxyPath(fullSourcePath, normalizedQuality),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Proxy source changed while its preview was being prepared. Try again.");
            }
            File.Move(temporaryPath, proxyPath, overwrite: true);
            TryTouch(proxyPath);
            progress?.Report(1);
            return proxyPath;
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static async Task GenerateProxyAsync(
        string ffmpegPath,
        string sourcePath,
        string outputPath,
        string quality,
        double durationSeconds,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var targetHeight = GetTargetHeight(quality);
        var targetWidth = GetTargetWidth(quality);
        // Bound both axes, retain the display aspect ratio, and always produce even
        // dimensions. The original height-only scale fails on odd-sized recordings
        // and leaves ultrawide videos unnecessarily expensive to decode.
        var scale = $"scale=w='max(2,trunc(min(iw*sar,min({targetWidth},{targetHeight}*dar))/2)*2)'" +
                    ":h='max(2,trunc(ow/dar/2)*2)':flags=bilinear,setsar=1";
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-nostdin", "-hide_banner", "-loglevel", "error",
                     "-threads", "2", "-filter_threads", "1",
                     "-i", sourcePath,
                     "-map", "0:v:0", "-map", "0:a:0?",
                     "-map_metadata", "-1", "-map_chapters", "-1",
                     "-vf", $"fps=fps='if(gt(source_fps,0),min(source_fps,60),30)':start_time=0,{scale}",
                     "-c:v", "libx264", "-preset", "veryfast", "-crf", "24",
                     "-tune", "fastdecode", "-threads", "2",
                     "-pix_fmt", "yuv420p", "-bf", "0", "-g", "30",
                     "-force_key_frames", "expr:gte(t,n_forced*0.5)",
                     "-c:a", "aac", "-b:a", "128k", "-ac", "2", "-ar", "48000",
                     "-max_muxing_queue_size", "2048", "-movflags", "+faststart"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }
        FFmpegProgressReader.AddArguments(startInfo);
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add(outputPath);

        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start FFmpeg proxy generation.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

        var progressTask = FFmpegProgressReader.ReadAsync(
            process.StandardOutput,
            durationSeconds,
            progress,
            cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await progressTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error)
                        ? $"FFmpeg proxy generation failed with exit code {process.ExitCode}."
                        : error.Trim());
            }
        }
        catch
        {
            FFmpegProgressReader.Terminate(process);
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            try { await progressTask.ConfigureAwait(false); } catch { }
            try { await errorTask.ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private async Task ValidateProxyAsync(
        string proxyPath,
        MediaProbeInfo sourceInfo,
        string quality,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(proxyPath) || new FileInfo(proxyPath).Length <= 1024)
            throw new InvalidDataException("Generated proxy is empty.");

        var proxyInfo = await _mediaProbeService.GetMediaInfoAsync(proxyPath, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Generated proxy is unreadable.");
        if (!proxyInfo.IsNativeEditorFormat ||
            proxyInfo.Width < 2 || proxyInfo.Width > GetTargetWidth(quality) || proxyInfo.Width % 2 != 0 ||
            proxyInfo.Height < 2 || proxyInfo.Height > GetTargetHeight(quality) || proxyInfo.Height % 2 != 0 ||
            (sourceInfo.AudioCodecs.Count > 0 && proxyInfo.AudioCodecs.Count == 0))
            throw new InvalidDataException("Generated proxy has unexpected video dimensions or format.");

        var durationDelta = Math.Abs(proxyInfo.Duration.TotalSeconds - sourceInfo.Duration.TotalSeconds);
        if (durationDelta > Math.Max(0.35, sourceInfo.Duration.TotalSeconds * 0.01))
            throw new InvalidDataException("Generated proxy duration does not match its source.");
    }

    private static void TryTouch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }
    }

    private sealed class EncodingProgress(IProgress<double> progress) : IProgress<double>
    {
        // FFmpeg finishing is followed by validation and atomic publication.
        public void Report(double value) => progress.Report(Math.Clamp(value, 0, 0.98));
    }
}
