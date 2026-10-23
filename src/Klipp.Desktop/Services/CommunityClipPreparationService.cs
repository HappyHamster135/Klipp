using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Desktop.Services;

/// <summary>Creates an explicitly selected share copy on this device. Originals are never changed.</summary>
public sealed class CommunityClipPreparationService
{
    public const long MaximumVideoBytes = 16 * 1024 * 1024;
    public const long MaximumPosterBytes = 256 * 1024;
    public const double MaximumDurationSeconds = 60;
    private readonly FFmpegLocator _locator;
    private readonly MediaProbeService _probe;

    public CommunityClipPreparationService(FFmpegLocator? locator = null)
    {
        _locator = locator ?? new FFmpegLocator();
        _probe = new MediaProbeService(_locator);
    }

    public async Task<PreparedCommunityClip> PrepareAsync(string sourcePath, double startSeconds,
        double durationSeconds, IReadOnlyList<int> audioStreams, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(sourcePath)) throw new InvalidOperationException("This clip is no longer on this device.");
        if (!double.IsFinite(startSeconds) || startSeconds < 0 || !double.IsFinite(durationSeconds)
            || durationSeconds <= 0 || durationSeconds > MaximumDurationSeconds)
            throw new InvalidOperationException("Choose a section up to 60 seconds long.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        var source = await _probe.GetMediaInfoAsync(sourcePath, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This video could not be read. Try exporting it from the editor first.");
        if (startSeconds + durationSeconds > source.Duration.TotalSeconds + 0.05)
            throw new InvalidOperationException("The selected section extends beyond the end of the video.");
        var selectedAudio = audioStreams.Distinct().Order().ToArray();
        if (selectedAudio.Any(index => index < 0 || index >= source.AudioStreams.Count))
            throw new InvalidOperationException("One of the selected audio tracks is unavailable.");

        var temporaryRoot = Path.Combine(Path.GetTempPath(), "Klipp", "CommunityShare");
        var directory = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var result = new PreparedCommunityClip(temporaryRoot, directory, durationSeconds);
        try
        {
            var ffmpeg = await _locator.GetFFmpegPathAsync(cancellationToken: token).ConfigureAwait(false);
            var arguments = new List<string>
            {
                "-ss", Format(startSeconds), "-i", sourcePath, "-t", Format(durationSeconds),
                "-map", "0:v:0", "-map_metadata", "-1", "-map_chapters", "-1",
                "-vf", "scale=w='min(1280,iw)':h='min(720,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2,setsar=1,fps=30,format=yuv420p",
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "24", "-maxrate", "1600k",
                "-bufsize", "2400k", "-threads", "2", "-filter_threads", "1"
            };
            if (selectedAudio.Length == 0) arguments.Add("-an");
            else
            {
                if (selectedAudio.Length == 1)
                    arguments.AddRange(["-map", $"0:a:{selectedAudio[0]}"]);
                else
                {
                    var inputs = string.Concat(selectedAudio.Select(index => $"[0:a:{index}]"));
                    arguments.AddRange(["-filter_complex_threads", "1", "-filter_complex",
                        $"{inputs}amix=inputs={selectedAudio.Length}:duration=longest:dropout_transition=0:normalize=0,alimiter=limit=0.95:level=false[shared_audio]",
                        "-map", "[shared_audio]"]);
                }
                arguments.AddRange(["-c:a", "aac", "-b:a", "96k", "-ac", "2", "-ar", "48000"]);
            }
            arguments.AddRange(["-movflags", "+faststart", "-y", result.VideoPath]);
            await RunAsync(ffmpeg, arguments, durationSeconds, progress, token).ConfigureAwait(false);
            var videoSize = new FileInfo(result.VideoPath).Length;
            if (videoSize is <= 0 or > MaximumVideoBytes)
                throw new InvalidOperationException("The share copy is too large. Choose a shorter section and try again.");
            var encoded = await _probe.GetMediaInfoAsync(result.VideoPath, token).ConfigureAwait(false);
            if (encoded is null || !encoded.IsNativeEditorFormat || encoded.Width > 1280 || encoded.Height > 720
                || encoded.Duration.TotalSeconds > MaximumDurationSeconds + 0.2)
                throw new InvalidOperationException("The share copy could not be verified. Try exporting the clip first.");
            result.DurationSeconds = Math.Min(MaximumDurationSeconds, encoded.Duration.TotalSeconds);
            await RunAsync(ffmpeg,
                ["-ss", Format(Math.Min(1, result.DurationSeconds / 3)), "-i", result.VideoPath,
                    "-frames:v", "1", "-an", "-vf", "scale=480:270:force_original_aspect_ratio=decrease,setsar=1",
                    "-q:v", "7", "-threads", "1", "-map_metadata", "-1", "-y", result.ThumbnailPath],
                1, null, token).ConfigureAwait(false);
            if (new FileInfo(result.ThumbnailPath).Length is <= 0 or > MaximumPosterBytes)
                throw new InvalidOperationException("The clip thumbnail could not be prepared.");
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    private static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments, double duration,
        IProgress<double>? progress, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error" }) start.ArgumentList.Add(arg);
        FFmpegProgressReader.AddArguments(start);
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start video preparation.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        using var cancellation = token.Register(() => FFmpegProgressReader.Terminate(process));
        var errors = process.StandardError.ReadToEndAsync(token);
        var updates = FFmpegProgressReader.ReadAsync(process.StandardOutput, duration, progress, token);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(token), errors, updates).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("This video could not be prepared for sharing. Try exporting it from the editor first.");
        }
        catch
        {
            FFmpegProgressReader.Terminate(process);
            try { await process.WaitForExitAsync().ConfigureAwait(false); } catch { }
            throw;
        }
    }
}

public sealed class PreparedCommunityClip : IDisposable
{
    private readonly string _root;
    private readonly string _directory;
    internal PreparedCommunityClip(string root, string directory, double durationSeconds)
    {
        _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        _directory = Path.GetFullPath(directory);
        VideoPath = Path.Combine(_directory, "video.mp4");
        ThumbnailPath = Path.Combine(_directory, "poster.jpg");
        DurationSeconds = durationSeconds;
    }
    public string VideoPath { get; }
    public string ThumbnailPath { get; }
    public double DurationSeconds { get; internal set; }
    public void Dispose()
    {
        // Delete only the unique preparation directory created inside our named temporary root.
        if (!_directory.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) return;
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch { }
    }
    public override string ToString() => nameof(PreparedCommunityClip);
}
