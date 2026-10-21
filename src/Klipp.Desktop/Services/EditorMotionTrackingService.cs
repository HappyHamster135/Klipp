using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Editing;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Desktop.Services;

public sealed record EditorMotionReferenceFrame(byte[] Png, int Width, int Height);
public sealed record EditorMotionTrackSample(double SourceOffsetSeconds, double CenterX, double CenterY, double Confidence);
public sealed record EditorMotionTrackingResult(IReadOnlyList<EditorMotionTrackSample> Samples,
    bool LostSubject, double RequestedDurationSeconds);
public sealed record EditorMotionTrackingProgress(double Fraction, double SourceOffsetSeconds, double Confidence);

public sealed class EditorMotionTrackingService
{
    private readonly FFmpegLocator _locator;
    public const int FramesPerSecond = 12;

    public EditorMotionTrackingService(FFmpegLocator? locator = null) => _locator = locator ?? new FFmpegLocator();

    public async Task<EditorMotionReferenceFrame> GetReferenceFrameAsync(string sourcePath, double sourceSeconds,
        CancellationToken cancellationToken = default)
    {
        var executable = await _locator.GetFFmpegPathAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var start = CreateStart(executable, sourcePath, sourceSeconds);
        Add(start, "-an", "-sn", "-dn", "-frames:v", "1", "-vf",
            "scale=384:240:force_original_aspect_ratio=decrease,setsar=1", "-c:v", "png", "-f", "image2pipe", "pipe:1");
        using var process = Process.Start(start) ?? throw new IOException("The source frame decoder could not start.");
        using var registration = cancellationToken.Register(() => Stop(process));
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var output = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        var bytes = output.ToArray();
        if (process.ExitCode != 0 || bytes.Length < 24 || bytes[0] != 137 || bytes[1] != 80)
            throw new IOException("A frame could not be read at this position. " + error.Trim());
        return new(bytes, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)),
            BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
    }

    public Task<EditorMotionTrackingResult> TrackAsync(string sourcePath, double sourceStartSeconds,
        double sourceDurationSeconds, int width, int height, EditorTrackingRegion region,
        IProgress<EditorMotionTrackingProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => TrackCoreAsync(sourcePath, sourceStartSeconds, sourceDurationSeconds, width, height,
            region, progress, cancellationToken), cancellationToken);

    private async Task<EditorMotionTrackingResult> TrackCoreAsync(string sourcePath, double sourceStartSeconds,
        double sourceDurationSeconds, int width, int height, EditorTrackingRegion region,
        IProgress<EditorMotionTrackingProgress>? progress, CancellationToken cancellationToken)
    {
        if (width < 16 || height < 16 || width > 512 || height > 512)
            throw new ArgumentException("The tracking frame size is invalid.");
        var duration = Math.Clamp(sourceDurationSeconds, 0.1, 480);
        var executable = await _locator.GetFFmpegPathAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var start = CreateStart(executable, sourcePath, sourceStartSeconds);
        Add(start, "-t", Number(duration), "-an", "-sn", "-dn", "-vf",
            $"fps={FramesPerSecond}:start_time=0,scale={width}:{height},format=gray",
            "-c:v", "rawvideo", "-pix_fmt", "gray", "-f", "rawvideo", "pipe:1");
        using var process = Process.Start(start) ?? throw new IOException("The motion tracking decoder could not start.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        using var registration = cancellationToken.Register(() => Stop(process));
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        var buffer = new byte[checked(width * height)];
        var samples = new List<EditorMotionTrackSample>();
        EditorMotionTemplateTracker? tracker = null;
        var lost = false;
        try
        {
            while (await ReadFrameAsync(process.StandardOutput.BaseStream, buffer, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                EditorTrackingMatch match;
                if (tracker is null)
                {
                    tracker = new EditorMotionTemplateTracker(buffer, width, height, region,
                        searchRadius: Math.Max(16, width / 12));
                    match = tracker.InitialMatch;
                }
                else if (!tracker.TryTrack(buffer, out match))
                {
                    lost = true;
                    Stop(process);
                    break;
                }
                var seconds = samples.Count / (double)FramesPerSecond;
                samples.Add(new(seconds, match.CenterX, match.CenterY, match.Confidence));
                if (samples.Count % 3 == 1)
                    progress?.Report(new(Math.Clamp(seconds / duration, 0, 1), seconds, match.Confidence));
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var error = await errors.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!lost && process.ExitCode != 0)
                throw new IOException("The video could not be decoded for tracking. " + error.Trim());
            if (samples.Count < 2)
                throw new InvalidOperationException(lost ? "The subject could not be followed. Choose a smaller, textured region."
                    : "This range has too few frames to track.");
            progress?.Report(new(1, samples[^1].SourceOffsetSeconds, samples[^1].Confidence));
            return new(samples, lost, duration);
        }
        finally
        {
            Stop(process);
            try { await errors.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private static ProcessStartInfo CreateStart(string executable, string sourcePath, double sourceSeconds)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Relink the source video before tracking.", sourcePath);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        Add(start, "-nostdin", "-hide_banner", "-loglevel", "error", "-threads", "1", "-filter_threads", "1",
            "-ss", Number(Math.Max(0, sourceSeconds)), "-i", sourcePath);
        return start;
    }

    private static void Add(ProcessStartInfo start, params string[] arguments)
    {
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
    }

    private static string Number(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    private static async Task<bool> ReadFrameAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (offset != 0) throw new IOException("The tracking decoder returned an incomplete frame.");
                return false;
            }
            offset += read;
        }
        return true;
    }

    private static void Stop(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
