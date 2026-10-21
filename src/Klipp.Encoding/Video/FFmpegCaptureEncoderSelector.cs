using System.Collections.Concurrent;
using System.Diagnostics;
using Klipp.Core.Models;

namespace Klipp.Encoding.Video;

public sealed record CaptureVideoEncoderSelection(string EncoderName, string DisplayName,
    bool IsHardware, string? FallbackReason);

/// <summary>
/// Tests the actual encoder before capture opens. Listing an encoder only says
/// FFmpeg was built with it; a short synthetic encode checks the driver and GPU.
/// Selections are shared by recording/replay and never probed per segment/frame.
/// </summary>
public static class FFmpegCaptureEncoderSelector
{
    private sealed record CacheEntry(DateTimeOffset Created, Lazy<Task<CaptureVideoEncoderSelection>> Selection);
    private readonly record struct CacheKey(string Executable, long ModifiedTicks, long Length,
        int Width, int Height, int FrameRate, int Bitrate, string Preference);
    private static readonly ConcurrentDictionary<CacheKey, CacheEntry> Cache = new();
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly string[] HardwareEncoders = ["h264_nvenc", "h264_qsv", "h264_amf"];

    public static string NormalizePreference(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "cpu" => "cpu",
        "nvenc" or "h264_nvenc" => "nvenc",
        "qsv" or "h264_qsv" => "qsv",
        "amf" or "h264_amf" => "amf",
        _ => "auto"
    };

    public static async Task<CaptureVideoEncoderSelection> SelectAsync(string ffmpegPath,
        RecordingSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var preference = NormalizePreference(settings.EncoderPreference);
        if (preference == "cpu") return Software();
        var file = new FileInfo(Path.GetFullPath(ffmpegPath));
        var key = new CacheKey(file.FullName.ToUpperInvariant(), file.LastWriteTimeUtc.Ticks, file.Length,
            settings.Width, settings.Height, settings.FrameRate, settings.VideoBitrate, preference);
        var now = DateTimeOffset.UtcNow;
        // Keep settings changes from retaining an unbounded set of probe tasks.
        if (Cache.Count > 64)
            foreach (var entry in Cache.OrderBy(entry => entry.Value.Created).Take(Cache.Count - 32))
                Cache.TryRemove(entry.Key, out _);
        var cached = Cache.AddOrUpdate(key,
            _ => NewEntry(), (_, old) => now - old.Created > CacheLifetime ? NewEntry() : old);
        return await cached.Selection.Value.WaitAsync(cancellationToken).ConfigureAwait(false);

        CacheEntry NewEntry() => new(now, new Lazy<Task<CaptureVideoEncoderSelection>>(
            () => SelectCoreAsync(file.FullName, settings, preference), LazyThreadSafetyMode.ExecutionAndPublication));
    }

    private static async Task<CaptureVideoEncoderSelection> SelectCoreAsync(string ffmpegPath,
        RecordingSettings settings, string preference)
    {
        var candidates = preference == "auto" ? HardwareEncoders : ["h264_" + preference];
        foreach (var encoder in candidates)
            if (await ProbeAsync(ffmpegPath, settings, encoder).ConfigureAwait(false))
                return new(encoder, DisplayName(encoder), true, null);
        return Software(preference == "auto"
            ? "No compatible GPU encoder is available for these recording settings. Using CPU encoding."
            : $"{DisplayName(candidates[0])} is unavailable for these recording settings. Using CPU encoding.");
    }

    private static CaptureVideoEncoderSelection Software(string? reason = null) =>
        new("libx264", "CPU · x264", false, reason);

    private static string DisplayName(string encoder) => encoder switch
    {
        "h264_nvenc" => "GPU · NVIDIA NVENC",
        "h264_qsv" => "GPU · Intel Quick Sync",
        "h264_amf" => "GPU · AMD AMF",
        _ => "CPU · x264"
    };

    internal static IReadOnlyList<string> VideoArguments(RecordingSettings settings,
        CaptureVideoEncoderSelection selection, int keyframeInterval)
    {
        var args = new List<string> { "-c:v", selection.EncoderName };
        switch (selection.EncoderName)
        {
            case "h264_nvenc":
                args.AddRange(["-preset", "p2", "-tune", "ll", "-rc", "vbr",
                    "-rc-lookahead", "0", "-zerolatency", "1"]);
                break;
            case "h264_qsv":
                args.AddRange(["-preset", "veryfast", "-async_depth", "1", "-look_ahead", "0"]);
                break;
            case "h264_amf":
                args.AddRange(["-usage", "ultralowlatency", "-quality", "speed", "-rc", "cbr"]);
                break;
            default:
                args.AddRange(["-preset", "ultrafast", "-threads", EncoderThreads(settings).ToString(),
                    "-tune", "zerolatency"]);
                break;
        }
        args.AddRange(["-bf", "0", "-b:v", (settings.VideoBitrate / 1000).ToString() + "k",
            "-g", keyframeInterval.ToString(), "-pix_fmt", selection.IsHardware ? "nv12" : "yuv420p"]);
        return args;
    }

    internal static int EncoderThreads(RecordingSettings settings)
    {
        var threadCap = Math.Min(4, Math.Max(2, Environment.ProcessorCount / 2));
        if (settings.FrameRate <= 15 || settings.Height <= 540) return 1;
        return settings.FrameRate <= 24 || settings.Height <= 720 ? Math.Min(2, threadCap) : threadCap;
    }

    private static async Task<bool> ProbeAsync(string executable, RecordingSettings settings, string encoder)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var selection = new CaptureVideoEncoderSelection(encoder, DisplayName(encoder), true, null);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-filter_threads", "2",
                     "-f", "lavfi", "-i", $"color=c=black:s={settings.Width}x{settings.Height}:r={settings.FrameRate},format=bgra",
                     "-frames:v", "3", "-an" }) start.ArgumentList.Add(argument);
        foreach (var argument in VideoArguments(settings, selection, settings.FrameRate)) start.ArgumentList.Add(argument);
        foreach (var argument in new[] { "-f", "null", "-" }) start.ArgumentList.Add(argument);
        Process? process = null;
        try
        {
            process = Process.Start(start);
            if (process is null) return false;
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(error, output).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
                   OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                process.Dispose();
            }
        }
    }
}
