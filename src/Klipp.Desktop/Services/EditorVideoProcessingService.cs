using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Desktop.Services;

public sealed record EditorVideoProcessingOptions(bool RampEnabled, double StartSpeed, double MiddleSpeed,
    double EndSpeed, double StabilizationStrength, int FrameRate = 60)
{
    public EditorVideoProcessingOptions Normalize() => this with
    {
        StartSpeed = Rate(StartSpeed), MiddleSpeed = Rate(MiddleSpeed), EndSpeed = Rate(EndSpeed),
        StabilizationStrength = double.IsFinite(StabilizationStrength) ? Math.Clamp(StabilizationStrength, 0, 1) : 0,
        FrameRate = FrameRate == 30 ? 30 : 60
    };
    private static double Rate(double value) => double.IsFinite(value) ? Math.Clamp(value, 0.25, 4) : 1;
}

public sealed record EditorVideoProcessingProgress(double Fraction, string Stage);
public sealed record EditorVideoProcessingResult(string SourcePath, double DurationSeconds, bool CacheHit);
public sealed record EditorSpeedRampSegment(double SourceOffsetSeconds, double SourceDurationSeconds,
    double PlaybackSpeed, int OutputFrames, double OutputStartSeconds);

/// <summary>Builds immutable, silent processed media while preserving every original file.</summary>
public sealed class EditorVideoProcessingService
{
    private readonly FFmpegLocator _locator;
    private readonly MediaProbeService _probe;
    public string DerivedDirectory { get; }

    public EditorVideoProcessingService(FFmpegLocator? locator = null, string? derivedDirectory = null)
    {
        _locator = locator ?? new FFmpegLocator();
        _probe = new MediaProbeService(_locator);
        DerivedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(derivedDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klipp", "DerivedMedia")));
    }

    public static IReadOnlyList<EditorSpeedRampSegment> BuildRampPlan(double sourceDuration, double baseSpeed,
        EditorVideoProcessingOptions rawOptions)
    {
        if (!double.IsFinite(sourceDuration) || sourceDuration <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceDuration));
        var options = rawOptions.Normalize();
        baseSpeed = double.IsFinite(baseSpeed) ? Math.Clamp(baseSpeed, 0.25, 4) : 1;
        var maximumRate = options.RampEnabled ? Math.Max(options.StartSpeed, Math.Max(options.MiddleSpeed, options.EndSpeed)) : baseSpeed;
        var count = options.RampEnabled ? Math.Clamp((int)(sourceDuration * options.FrameRate / (maximumRate * 3)), 1, 32) : 1;
        if (count > 1) count -= count % 2;
        var result = new List<EditorSpeedRampSegment>(count);
        var exactTime = 0d;
        var frame = 0;
        double Rate(double position) => position <= 0.5
            ? options.StartSpeed + (options.MiddleSpeed - options.StartSpeed) * position * 2
            : options.MiddleSpeed + (options.EndSpeed - options.MiddleSpeed) * (position - 0.5) * 2;
        double Interval(double left, double right)
        {
            if (!options.RampEnabled) return (right - left) * sourceDuration / baseSpeed;
            if (left < 0.5 && right > 0.5) return Interval(left, 0.5) + Interval(0.5, right);
            var a = Rate(left); var b = Rate(right);
            return (right - left) * sourceDuration * (Math.Abs(b - a) < 1e-9 ? 1 / a : Math.Log(b / a) / (b - a));
        }
        for (var index = 0; index < count; index++)
        {
            var left = index / (double)count;
            var right = (index + 1d) / count;
            var duration = sourceDuration * (right - left);
            var outputDuration = Interval(left, right);
            exactTime += outputDuration;
            // Round cumulative boundaries rather than every segment duration,
            // keeping the whole ramp within one output frame of its integral.
            var nextFrame = Math.Max(frame + 1, (int)Math.Round(exactTime * options.FrameRate));
            var outputFrames = nextFrame - frame;
            result.Add(new(left * sourceDuration, duration, duration / outputDuration,
                outputFrames, frame / (double)options.FrameRate));
            frame = nextFrame;
        }
        return result;
    }

    public async Task<EditorVideoProcessingResult> ProcessAsync(string sourcePath, double trimStart,
        double trimEnd, double baseSpeed, EditorVideoProcessingOptions rawOptions,
        IProgress<EditorVideoProcessingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("The original video is missing.", sourcePath);
        if (!double.IsFinite(trimStart) || !double.IsFinite(trimEnd) || trimStart < 0 || trimEnd <= trimStart)
            throw new ArgumentException("Choose a valid video range before processing.");
        var options = rawOptions.Normalize();
        var plan = BuildRampPlan(trimEnd - trimStart, baseSpeed, options);
        var expectedDuration = plan.Sum(segment => segment.OutputFrames) / (double)options.FrameRate;
        // Processed clips play at 1x; the editor requires at least 0.1s of
        // selected source. Reject shorter bakes rather than extending their cut.
        if (expectedDuration < 0.1 - 1e-9)
            throw new ArgumentException("The processed result would be shorter than 0.1 seconds. Choose a longer range or slower speeds.");
        var source = new FileInfo(sourcePath);
        var key = string.Join('|', "processed-video-v1", source.FullName, source.Length, source.LastWriteTimeUtc.Ticks,
            F(trimStart), F(trimEnd), F(baseSpeed), options.RampEnabled, F(options.StartSpeed), F(options.MiddleSpeed),
            F(options.EndSpeed), F(options.StabilizationStrength), options.FrameRate);
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();
        Directory.CreateDirectory(DerivedDirectory);
        var destination = Path.Combine(DerivedDirectory, $"processed-{hash}.mp4");
        cancellationToken.ThrowIfCancellationRequested();
        var cached = await ReadValidAsync(destination, expectedDuration, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (cached is not null)
        {
            progress?.Report(new(1, "Processed video ready"));
            return new(destination, cached.Duration.TotalSeconds, true);
        }
        var ffmpeg = await _locator.GetFFmpegPathAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var workspace = Path.Combine(DerivedDirectory, $".processing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            var rampSource = sourcePath;
            var rampOffset = trimStart;
            var stabilizationWeight = options.StabilizationStrength > 0.001 ? 0.3 : 0;
            if (stabilizationWeight > 0)
            {
                rampSource = Path.Combine(workspace, "stabilized.mp4");
                rampOffset = 0;
                var radius = 16 * Math.Clamp((int)Math.Ceiling(options.StabilizationStrength * 4), 1, 4);
                var contrast = (int)Math.Round(100 - options.StabilizationStrength * 70);
                var seek = Math.Max(0, trimStart - 1);
                var filter = $"trim=start={F(trimStart - seek)}:duration={F(trimEnd - trimStart)},setpts=PTS-STARTPTS," +
                    $"deshake=rx={radius}:ry={radius}:edge=mirror:blocksize=8:contrast={contrast}:search=1," +
                    $"fps={options.FrameRate},pad=ceil(iw/2)*2:ceil(ih/2)*2,format=yuv420p";
                await RunAsync(ffmpeg, ["-ss", F(seek), "-i", sourcePath, "-map", "0:v:0", "-vf", filter,
                    "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "17", "-threads", "2", rampSource],
                    trimEnd - trimStart, value => progress?.Report(new(value * stabilizationWeight, "Stabilizing video")), cancellationToken).ConfigureAwait(false);
            }
            var paths = new List<string>();
            for (var index = 0; index < plan.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segment = plan[index];
                var path = Path.Combine(workspace, $"segment-{index:D3}.mp4");
                paths.Add(path);
                var sourceStart = rampOffset + segment.SourceOffsetSeconds;
                var seek = Math.Max(0, sourceStart - 1);
                var filter = $"trim=start={F(sourceStart - seek)}:duration={F(segment.SourceDurationSeconds)}," +
                    $"setpts=(PTS-STARTPTS)/{F(segment.PlaybackSpeed)},fps={options.FrameRate}," +
                    $"tpad=stop_mode=clone:stop_duration={F(2d / options.FrameRate)}," +
                    "pad=ceil(iw/2)*2:ceil(ih/2)*2,format=yuv420p";
                var segmentIndex = index;
                await RunAsync(ffmpeg, ["-ss", F(seek), "-i", rampSource, "-map", "0:v:0", "-vf", filter,
                    "-frames:v", segment.OutputFrames.ToString(CultureInfo.InvariantCulture), "-an", "-c:v", "libx264",
                    "-preset", "veryfast", "-crf", "18", "-threads", "2", path],
                    segment.OutputFrames / (double)options.FrameRate,
                    value => progress?.Report(new(stabilizationWeight + (0.95 - stabilizationWeight) *
                        (segmentIndex + value) / plan.Count, options.RampEnabled ? "Building speed ramp" : "Preparing processed video")), cancellationToken).ConfigureAwait(false);
            }
            var list = Path.Combine(workspace, "segments.txt");
            await File.WriteAllLinesAsync(list, paths.Select(path => $"file '{path.Replace('\\', '/').Replace("'", "'\\''")}'"), cancellationToken).ConfigureAwait(false);
            var completed = Path.Combine(workspace, "completed.mp4");
            await RunAsync(ffmpeg, ["-f", "concat", "-safe", "0", "-i", list, "-map", "0:v:0", "-an", "-c:v", "copy",
                "-movflags", "+faststart", completed], expectedDuration,
                value => progress?.Report(new(0.95 + value * 0.05, "Finishing processed video")), cancellationToken).ConfigureAwait(false);
            var completedInfo = await ReadValidAsync(completed, expectedDuration, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (completedInfo is null)
                throw new InvalidDataException("The processed video did not match its expected duration.");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(completed, destination, overwrite: true);
            progress?.Report(new(1, "Processed video ready"));
            return new(destination, completedInfo.Duration.TotalSeconds, false);
        }
        finally
        {
            // This unique job folder is the only recursively removed path.
            if (Path.GetDirectoryName(Path.GetFullPath(workspace)) == DerivedDirectory)
                try { Directory.Delete(workspace, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<MediaProbeInfo?> ReadValidAsync(string path, double duration, CancellationToken token)
    {
        var info = await _probe.GetMediaInfoAsync(path, token).ConfigureAwait(false);
        return info is not null && info.VideoCodec == "h264" && info.AudioCodecs.Count == 0 &&
            Math.Abs(info.Duration.TotalSeconds - duration) < 0.08 ? info : null;
    }

    private static async Task RunAsync(string ffmpeg, IReadOnlyList<string> arguments, double duration,
        Action<double> progress, CancellationToken token)
    {
        var info = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y" }) info.ArgumentList.Add(argument);
        FFmpegProgressReader.AddArguments(info);
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("FFmpeg could not start video processing.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        var output = FFmpegProgressReader.ReadAsync(process.StandardOutput, duration, new CallbackProgress(progress), token);
        var error = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) { FFmpegProgressReader.Terminate(process); throw; }
        await output.ConfigureAwait(false);
        var detail = await error.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException($"Video processing failed. {detail}".Trim());
    }
    private sealed class CallbackProgress(Action<double> callback) : IProgress<double> { public void Report(double value) => callback(value); }
    private static string F(double value) => value.ToString("0.#########", CultureInfo.InvariantCulture);
}
