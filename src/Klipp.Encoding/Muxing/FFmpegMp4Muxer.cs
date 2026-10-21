using System.Diagnostics;
using System.Globalization;
using Klipp.Encoding.FFmpeg;
using Klipp.Encoding.Audio;

namespace Klipp.Encoding.Muxing;

/// <summary>
/// Slår ihop en video-only MP4 och en WAV-fil till en slutlig MP4 med AAC-ljud.
/// Audio trimmas eller fördröjs utifrån uppmätta starttider.
/// </summary>
public sealed class FFmpegMp4Muxer
{
    private readonly FFmpegLocator _locator;

    public FFmpegMp4Muxer(FFmpegLocator? locator = null)
    {
        _locator = locator ?? new FFmpegLocator();
    }

    public async Task<MuxResult> MuxAsync(
        string videoPath,
        string? audioPath,
        string outputPath,
        long videoStartTimestamp,
        long? audioStartTimestamp,
        CancellationToken cancellationToken = default,
        int audioChannels = 2,
        string audioTrackName = CaptureAudioLayout.SystemName,
        IReadOnlyList<string>? audioTrackNames = null)
    {
        if (!File.Exists(videoPath))
            throw new FileNotFoundException("The video file does not exist.", videoPath);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var hasAudio = HasUsableAudio(audioPath, audioStartTimestamp);

        var ffmpegPath = await _locator.GetFFmpegPathAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var audioOffsetSeconds = hasAudio ? (audioStartTimestamp!.Value - videoStartTimestamp) /
                                 (double)Stopwatch.Frequency : 0;

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-filter_complex_threads");
        psi.ArgumentList.Add("1");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(videoPath);
        if (hasAudio)
        {
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(audioPath!);
        }
        psi.ArgumentList.Add("-map");
        psi.ArgumentList.Add("0:v:0");
        if (hasAudio)
        {
            foreach (var argument in CaptureAudioLayout.OutputArguments("[1:a]", audioChannels,
                BuildAudioFilter(audioOffsetSeconds), audioTrackName, audioTrackNames))
                psi.ArgumentList.Add(argument);
        }
        else psi.ArgumentList.Add("-an");
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("copy");
        if (hasAudio)
        {
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add("aac");
            psi.ArgumentList.Add("-b:a");
            psi.ArgumentList.Add("192k");
            psi.ArgumentList.Add("-shortest");
        }
        psi.ArgumentList.Add("-movflags");
        psi.ArgumentList.Add("+faststart");
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(outputPath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start ffmpeg.exe.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch { try { process.Kill(entireProcessTree: true); } catch { } throw; }
        var stderr = await stderrTask.ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"FFmpeg muxing failed (exit code {process.ExitCode}). Stderr:\n{stderr}");

        if (!File.Exists(outputPath))
            throw new InvalidOperationException("FFmpeg did not create a muxed output file.");

        return new MuxResult(outputPath, HasAudio: hasAudio, AudioOffsetSeconds: hasAudio ? audioOffsetSeconds : null);
    }

    private static bool HasUsableAudio(string? audioPath, long? audioStartTimestamp)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !audioStartTimestamp.HasValue)
            return false;
        if (!File.Exists(audioPath))
            return false;

        // En tom WAV-fil består i praktiken bara av headern.
        return new FileInfo(audioPath).Length > 44;
    }

    private static string BuildAudioFilter(double audioOffsetSeconds)
    {
        const double epsilonSeconds = 0.001;

        if (audioOffsetSeconds < -epsilonSeconds)
        {
            var trimSeconds = (-audioOffsetSeconds).ToString("0.######", CultureInfo.InvariantCulture);
            return $"atrim=start={trimSeconds},asetpts=PTS-STARTPTS,aresample=async=1:first_pts=0,apad";
        }

        if (audioOffsetSeconds > epsilonSeconds)
        {
            var delayMilliseconds = Math.Max(0, (int)Math.Round(audioOffsetSeconds * 1000.0));
            return $"adelay={delayMilliseconds}:all=1,aresample=async=1:first_pts=0,apad";
        }

        return "aresample=async=1:first_pts=0,apad";
    }
}

public sealed record MuxResult(
    string OutputPath,
    bool HasAudio,
    double? AudioOffsetSeconds);
