using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Performance;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Desktop.Services;

/// <summary>
/// Genererar och cachar waveform-bilder (amplitud över tid) för klippens ljudspår,
/// använt av timeline-editorns Audio-lager. Samma cache/process-mönster som
/// ThumbnailService, men med FFmpegs showwavespic-filter istället för en frame-grab.
/// </summary>
public sealed class WaveformService
{
    private const string WorkCategory = "Waveforms";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> GenerationLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly FFmpegLocator _locator;
    private readonly PriorityBackgroundWorkQueue _workQueue;
    private readonly string _waveformDirectory;

    public WaveformService(
        FFmpegLocator? locator = null,
        PriorityBackgroundWorkQueue? workQueue = null)
    {
        _locator = locator ?? new FFmpegLocator();
        _workQueue = workQueue ?? MediaPreviewWorkQueue.Shared;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _waveformDirectory = Path.Combine(localAppData, "Klipp", "waveforms");
        Directory.CreateDirectory(_waveformDirectory);
    }

    /// <summary>
    /// Returnerar sökväg till en waveform-bild för videons ljudspår. Genererar den
    /// om den saknas eller är äldre än videofilen. Returnerar null om det misslyckas
    /// (t.ex. klipp utan ljud) — då visas inget waveform-lager, bara det vanliga blocket.
    /// </summary>
    public async Task<string?> GetOrCreateWaveformAsync(
        string videoPath,
        CancellationToken cancellationToken = default) =>
        await GetOrCreateWaveformAsync(
                videoPath,
                null,
                null,
                BackgroundWorkPriority.Background,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<string?> GetOrCreateWaveformAsync(
        string videoPath,
        TimeSpan? trimStart,
        TimeSpan? trimEnd,
        CancellationToken cancellationToken = default) =>
        await GetOrCreateWaveformAsync(
                videoPath,
                trimStart,
                trimEnd,
                BackgroundWorkPriority.Background,
                cancellationToken)
            .ConfigureAwait(false);

    public async Task<string?> GetOrCreateWaveformAsync(
        string videoPath,
        TimeSpan? trimStart,
        TimeSpan? trimEnd,
        BackgroundWorkPriority priority,
        CancellationToken cancellationToken = default, int audioStreamIndex = 0)
    {
        if (!File.Exists(videoPath)) return null;

        var fullPath = Path.GetFullPath(videoPath);
        var cacheKeySource = $"{fullPath.ToUpperInvariant()}|{trimStart?.Ticks ?? 0}|{trimEnd?.Ticks ?? 0}|{audioStreamIndex}";
        var cacheKey = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cacheKeySource)))[..16];
        var waveformName = $"{Path.GetFileNameWithoutExtension(videoPath)}-{cacheKey}.png";
        var waveformPath = Path.Combine(_waveformDirectory, waveformName);

        // Redan genererad och nyare än videon? Återanvänd.
        if (IsCurrent(waveformPath, fullPath))
        {
            _workQueue.RecordCacheHit(WorkCategory);
            TryTouch(waveformPath);
            return waveformPath;
        }

        _workQueue.RecordCacheMiss(WorkCategory);
        try
        {
            return await _workQueue.RunAsync(
                    WorkCategory,
                    priority,
                    token => GenerateWaveformAsync(
                        fullPath,
                        waveformPath,
                        trimStart,
                        trimEnd,
                        token, audioStreamIndex),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
    }

    private async Task<string?> GenerateWaveformAsync(
        string videoPath,
        string waveformPath,
        TimeSpan? trimStart,
        TimeSpan? trimEnd,
        CancellationToken cancellationToken, int audioStreamIndex)
    {
        var generationLock = GenerationLocks.GetOrAdd(waveformPath, _ => new SemaphoreSlim(1, 1));
        await generationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsCurrent(waveformPath, videoPath))
            {
                TryTouch(waveformPath);
                return waveformPath;
            }

            var ffmpegPath = await _locator.GetFFmpegPathAsync(progress: null, cancellationToken)
                .ConfigureAwait(false);
            var temporaryPath = Path.Combine(
                _waveformDirectory,
                $".{Path.GetFileNameWithoutExtension(waveformPath)}-{Guid.NewGuid():N}.tmp.png");

            try
            {
                var ok = await RenderWaveformAsync(
                        ffmpegPath,
                        videoPath,
                        temporaryPath,
                        trimStart,
                        trimEnd,
                        cancellationToken, audioStreamIndex)
                    .ConfigureAwait(false);
                if (!ok || !HasContent(temporaryPath))
                    return null;

                File.Move(temporaryPath, waveformPath, overwrite: true);
                return waveformPath;
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

    private static async Task<bool> RenderWaveformAsync(
        string ffmpegPath,
        string videoPath,
        string waveformPath,
        TimeSpan? trimStart,
        TimeSpan? trimEnd,
        CancellationToken cancellationToken, int audioStreamIndex)
    {
        // showwavespic ritar hela ljudspåret som en enda bred, låg PNG som sedan
        // stretchas över audio-blocket i timeline. Teal matchar audio-blockets kant.
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-threads");
        psi.ArgumentList.Add("1");
        if (trimStart is { } start && start > TimeSpan.Zero)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(start.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        if (trimStart is { } rangeStart && trimEnd is { } rangeEnd && rangeEnd > rangeStart)
        {
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add((rangeEnd - rangeStart).TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture));
        }
        psi.ArgumentList.Add("-filter_complex");
        psi.ArgumentList.Add($"[0:a:{Math.Max(0, audioStreamIndex)}]showwavespic=s=1600x64:colors=0x2DD4BF");
        psi.ArgumentList.Add("-frames:v");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add(waveformPath);

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
