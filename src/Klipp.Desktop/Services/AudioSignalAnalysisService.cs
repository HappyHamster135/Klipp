using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Desktop.Services;

public sealed class AudioSignalAnalysisService
{
    private const int CacheVersion = 1;
    private const int AnalysisSampleRate = 8000;
    private const double WindowSeconds = 0.05;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly FFmpegLocator _locator;
    private readonly string _cacheDirectory;

    public AudioSignalAnalysisService(FFmpegLocator? locator = null, string? cacheDirectory = null)
    {
        _locator = locator ?? new FFmpegLocator();
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Klipp",
            "audio-analysis");
        Directory.CreateDirectory(_cacheDirectory);
    }

    public async Task<AudioSignalAnalysis?> AnalyzeAsync(
        string sourcePath,
        double trimStartSeconds = 0,
        double? trimEndSeconds = null,
        CancellationToken cancellationToken = default, int audioStreamIndex = 0)
    {
        if (!File.Exists(sourcePath))
            return null;

        var fullPath = Path.GetFullPath(sourcePath);
        trimStartSeconds = Math.Max(0, trimStartSeconds);
        if (trimEndSeconds is { } end && end <= trimStartSeconds + 0.01)
            trimEndSeconds = null;

        var sourceInfo = new FileInfo(fullPath);
        var cachePath = GetCachePath(fullPath, trimStartSeconds, trimEndSeconds, audioStreamIndex);
        var cached = await TryReadCacheAsync(
                cachePath,
                sourceInfo,
                trimStartSeconds,
                trimEndSeconds,
                cancellationToken)
            .ConfigureAwait(false);
        if (cached is not null)
            return cached;

        var ffmpegPath = await _locator.GetFFmpegPathAsync(
                progress: null,
                cancellationToken)
            .ConfigureAwait(false);
        var analysis = await AnalyzeWithFfmpegAsync(
                ffmpegPath,
                fullPath,
                trimStartSeconds,
                trimEndSeconds,
                cancellationToken, audioStreamIndex)
            .ConfigureAwait(false);
        if (analysis is null)
            return null;

        await TryWriteCacheAsync(
                cachePath,
                sourceInfo,
                trimStartSeconds,
                trimEndSeconds,
                analysis,
                cancellationToken)
            .ConfigureAwait(false);
        return analysis;
    }

    private string GetCachePath(string sourcePath, double trimStartSeconds, double? trimEndSeconds, int audioStreamIndex)
    {
        var keySource = string.Join(
            '|',
            sourcePath.ToUpperInvariant(),
            audioStreamIndex.ToString(CultureInfo.InvariantCulture),
            trimStartSeconds.ToString("0.######", CultureInfo.InvariantCulture),
            trimEndSeconds?.ToString("0.######", CultureInfo.InvariantCulture) ?? "end");
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(keySource)))[..20];
        return Path.Combine(_cacheDirectory, $"{hash}.json");
    }

    private static async Task<AudioSignalAnalysis?> TryReadCacheAsync(
        string cachePath,
        FileInfo sourceInfo,
        double trimStartSeconds,
        double? trimEndSeconds,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(cachePath))
            return null;

        try
        {
            await using var stream = new FileStream(
                cachePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var envelope = await JsonSerializer.DeserializeAsync<AudioSignalCacheEnvelope>(
                    stream,
                    JsonOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            if (envelope is null ||
                envelope.Version != CacheVersion ||
                envelope.SourceLength != sourceInfo.Length ||
                envelope.SourceLastWriteUtcTicks != sourceInfo.LastWriteTimeUtc.Ticks ||
                Math.Abs(envelope.TrimStartSeconds - trimStartSeconds) > 0.0001 ||
                !NullableNearlyEquals(envelope.TrimEndSeconds, trimEndSeconds) ||
                envelope.Analysis is null ||
                envelope.Analysis.Frames.Count == 0)
            {
                return null;
            }

            try { File.SetLastAccessTimeUtc(cachePath, DateTime.UtcNow); } catch { }
            return envelope.Analysis;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task TryWriteCacheAsync(
        string cachePath,
        FileInfo sourceInfo,
        double trimStartSeconds,
        double? trimEndSeconds,
        AudioSignalAnalysis analysis,
        CancellationToken cancellationToken)
    {
        var temporaryPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var envelope = new AudioSignalCacheEnvelope
            {
                Version = CacheVersion,
                SourceLength = sourceInfo.Length,
                SourceLastWriteUtcTicks = sourceInfo.LastWriteTimeUtc.Ticks,
                TrimStartSeconds = trimStartSeconds,
                TrimEndSeconds = trimEndSeconds,
                Analysis = analysis
            };
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             32 * 1024,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static async Task<AudioSignalAnalysis?> AnalyzeWithFfmpegAsync(
        string ffmpegPath,
        string sourcePath,
        double trimStartSeconds,
        double? trimEndSeconds,
        CancellationToken cancellationToken, int audioStreamIndex)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        if (trimStartSeconds > 0.001)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(F(trimStartSeconds));
        }
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(sourcePath);
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add($"0:a:{Math.Max(0, audioStreamIndex)}");
        if (trimEndSeconds is { } end)
        {
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(F(end - trimStartSeconds));
        }
        psi.ArgumentList.Add("-vn");
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(AnalysisSampleRate.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("pipe:1");

        using var process = Process.Start(psi);
        if (process is null)
            return null;
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var frames = new List<AudioSignalFrame>();
        var windowFrameCount = Math.Max(1, (int)Math.Round(AnalysisSampleRate * WindowSeconds));
        var buffer = new byte[64 * 1024 + 4];
        var carryBytes = 0;
        var framesInWindow = 0;
        var totalSampleFrames = 0L;
        var peakLeft = 0d;
        var peakRight = 0d;
        var squareLeft = 0d;
        var squareRight = 0d;

        try
        {
            while (true)
            {
                var bytesRead = await process.StandardOutput.BaseStream.ReadAsync(
                        buffer.AsMemory(carryBytes, buffer.Length - carryBytes),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0)
                    break;

                var totalBytes = carryBytes + bytesRead;
                var completeBytes = totalBytes - totalBytes % 4;
                for (var offset = 0; offset < completeBytes; offset += 4)
                {
                    var left = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset, 2)) / 32768d;
                    var right = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset + 2, 2)) / 32768d;
                    peakLeft = Math.Max(peakLeft, Math.Abs(left));
                    peakRight = Math.Max(peakRight, Math.Abs(right));
                    squareLeft += left * left;
                    squareRight += right * right;
                    framesInWindow++;
                    totalSampleFrames++;

                    if (framesInWindow >= windowFrameCount)
                    {
                        AppendFrame(
                            frames,
                            totalSampleFrames,
                            framesInWindow,
                            peakLeft,
                            peakRight,
                            squareLeft,
                            squareRight);
                        framesInWindow = 0;
                        peakLeft = 0;
                        peakRight = 0;
                        squareLeft = 0;
                        squareRight = 0;
                    }
                }

                carryBytes = totalBytes - completeBytes;
                if (carryBytes > 0)
                    Buffer.BlockCopy(buffer, completeBytes, buffer, 0, carryBytes);
            }

            if (framesInWindow > 0)
            {
                AppendFrame(
                    frames,
                    totalSampleFrames,
                    framesInWindow,
                    peakLeft,
                    peakRight,
                    squareLeft,
                    squareRight);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await stderrTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (process.ExitCode != 0 || frames.Count == 0)
            return null;

        return new AudioSignalAnalysis
        {
            WindowSeconds = WindowSeconds,
            DurationSeconds = totalSampleFrames / (double)AnalysisSampleRate,
            Frames = frames,
            BeatSeconds = DetectBeats(frames)
        };
    }

    private static void AppendFrame(
        ICollection<AudioSignalFrame> frames,
        long totalSampleFrames,
        int framesInWindow,
        double peakLeft,
        double peakRight,
        double squareLeft,
        double squareRight)
    {
        var endSeconds = totalSampleFrames / (double)AnalysisSampleRate;
        frames.Add(new AudioSignalFrame
        {
            TimeSeconds = Math.Max(0, endSeconds - framesInWindow / (double)AnalysisSampleRate / 2),
            PeakLeft = Math.Clamp(peakLeft, 0, 1),
            PeakRight = Math.Clamp(peakRight, 0, 1),
            RmsLeft = Math.Clamp(Math.Sqrt(squareLeft / Math.Max(1, framesInWindow)), 0, 1),
            RmsRight = Math.Clamp(Math.Sqrt(squareRight / Math.Max(1, framesInWindow)), 0, 1)
        });
    }

    internal static List<double> DetectBeats(IReadOnlyList<AudioSignalFrame> frames)
    {
        var beats = new List<(double Time, double Energy)>();
        const int lookbackFrames = 20;
        const double minimumGapSeconds = 0.22;
        for (var index = 2; index < frames.Count - 1; index++)
        {
            var frame = frames[index];
            var energy = (frame.RmsLeft + frame.RmsRight) / 2;
            var previousEnergy = (frames[index - 1].RmsLeft + frames[index - 1].RmsRight) / 2;
            var nextEnergy = (frames[index + 1].RmsLeft + frames[index + 1].RmsRight) / 2;
            var start = Math.Max(0, index - lookbackFrames);
            var localAverage = 0d;
            for (var sample = start; sample < index; sample++)
                localAverage += (frames[sample].RmsLeft + frames[sample].RmsRight) / 2;
            localAverage /= Math.Max(1, index - start);

            var peak = Math.Max(frame.PeakLeft, frame.PeakRight);
            var onset = energy - previousEnergy;
            var threshold = Math.Max(0.025, localAverage * 1.38);
            if (peak < 0.08 || energy < threshold || onset < Math.Max(0.008, localAverage * 0.12) || energy < nextEnergy)
                continue;

            if (beats.Count > 0 && frame.TimeSeconds - beats[^1].Time < minimumGapSeconds)
            {
                if (energy > beats[^1].Energy)
                    beats[^1] = (frame.TimeSeconds, energy);
                continue;
            }

            beats.Add((frame.TimeSeconds, energy));
        }

        return beats.Select(beat => beat.Time).ToList();
    }

    private static bool NullableNearlyEquals(double? left, double? right) =>
        left is null && right is null ||
        left is not null && right is not null && Math.Abs(left.Value - right.Value) <= 0.0001;

    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private sealed class AudioSignalCacheEnvelope
    {
        public int Version { get; set; }
        public long SourceLength { get; set; }
        public long SourceLastWriteUtcTicks { get; set; }
        public double TrimStartSeconds { get; set; }
        public double? TrimEndSeconds { get; set; }
        public AudioSignalAnalysis Analysis { get; set; } = new();
    }
}

public sealed class AudioSignalAnalysis
{
    public double WindowSeconds { get; set; } = 0.05;
    public double DurationSeconds { get; set; }
    public List<AudioSignalFrame> Frames { get; set; } = [];
    public List<double> BeatSeconds { get; set; } = [];

    public AudioSignalFrame? GetFrame(double seconds)
    {
        if (Frames.Count == 0 || !double.IsFinite(seconds) || seconds < 0 || seconds > DurationSeconds + WindowSeconds)
            return null;

        var index = Math.Clamp(
            (int)Math.Round(seconds / Math.Max(0.001, WindowSeconds)),
            0,
            Frames.Count - 1);
        return Frames[index];
    }
}

public sealed class AudioSignalFrame
{
    public double TimeSeconds { get; set; }
    public double PeakLeft { get; set; }
    public double PeakRight { get; set; }
    public double RmsLeft { get; set; }
    public double RmsRight { get; set; }
}
