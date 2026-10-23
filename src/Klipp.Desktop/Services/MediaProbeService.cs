using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Desktop.Services;

/// <summary>
/// Läser metadata (t.ex. varaktighet) ur videofiler med ffprobe, som följer med
/// FFmpeg-nedladdningen.
/// </summary>
public sealed class MediaProbeService
{
    private static readonly JsonSerializerOptions ProbeJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly FFmpegLocator _locator;

    public MediaProbeService(FFmpegLocator? locator = null)
    {
        _locator = locator ?? new FFmpegLocator();
    }

    /// <summary>
    /// Läser videons varaktighet. Returnerar null om filen saknas eller läsningen misslyckas.
    /// </summary>
    public async Task<TimeSpan?> GetDurationAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(videoPath)) return null;

        try
        {
            var ffprobePath = await _locator.GetFFprobePathAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // ffprobe skriver bara varaktigheten i sekunder, t.ex. "12.345000"
            var args = $"-hide_banner -v error -show_entries format=duration " +
                       $"-of default=noprint_wrappers=1:nokey=1 \"{videoPath}\"";

            var psi = new ProcessStartInfo
            {
                FileName = ffprobePath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            using var cancellation = cancellationToken.Register(() => FFmpegProgressReader.Terminate(process));

            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            output = output.Trim();
            if (double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                return TimeSpan.FromSeconds(seconds);

            return null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<MediaProbeInfo?> GetMediaInfoAsync(
        string mediaPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(mediaPath))
            return null;

        try
        {
            var ffprobePath = await _locator.GetFFprobePathAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var psi = new ProcessStartInfo
            {
                FileName = ffprobePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
                     {
                         "-hide_banner", "-v", "error",
                         "-show_entries", "format=format_name,duration:stream=codec_type,codec_name,width,height:stream_tags=handler_name,title",
                         "-of", "json", mediaPath
                     })
            {
                psi.ArgumentList.Add(argument);
            }

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            using var cancellation = cancellationToken.Register(() => FFmpegProgressReader.Terminate(process));

            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                return null;

            var document = JsonSerializer.Deserialize<ProbeDocument>(output, ProbeJsonOptions);
            if (document?.Format is null ||
                !double.TryParse(
                    document.Format.Duration,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var durationSeconds))
            {
                return null;
            }

            var streams = document.Streams ?? [];
            var video = streams.FirstOrDefault(stream =>
                string.Equals(stream.CodecType, "video", StringComparison.OrdinalIgnoreCase));
            if (video is null || string.IsNullOrWhiteSpace(video.CodecName))
                return null;

            var audioCodecs = streams
                .Where(stream => string.Equals(stream.CodecType, "audio", StringComparison.OrdinalIgnoreCase))
                .Select(stream => stream.CodecName?.Trim().ToLowerInvariant())
                .Where(codec => !string.IsNullOrWhiteSpace(codec))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new MediaProbeInfo(
                TimeSpan.FromSeconds(Math.Max(0, durationSeconds)),
                document.Format.FormatName?.Trim().ToLowerInvariant() ?? string.Empty,
                video.CodecName.Trim().ToLowerInvariant(),
                audioCodecs,
                Math.Max(0, video.Width),
                Math.Max(0, video.Height))
            {
                AudioStreams = streams.Where(stream =>
                        string.Equals(stream.CodecType, "audio", StringComparison.OrdinalIgnoreCase))
                    .Select((stream, index) => new MediaAudioStreamInfo(index,
                        stream.Tags?.GetValueOrDefault("handler_name") ?? string.Empty,
                        stream.Tags?.GetValueOrDefault("title") ?? string.Empty)).ToList()
            };
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

    private sealed class ProbeDocument
    {
        public ProbeFormat? Format { get; set; }
        public List<ProbeStream>? Streams { get; set; }
    }

    private sealed class ProbeFormat
    {
        [System.Text.Json.Serialization.JsonPropertyName("format_name")]
        public string? FormatName { get; set; }

        public string? Duration { get; set; }
    }

    private sealed class ProbeStream
    {
        [System.Text.Json.Serialization.JsonPropertyName("codec_type")]
        public string? CodecType { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("codec_name")]
        public string? CodecName { get; set; }

        public int Width { get; set; }
        public int Height { get; set; }
        public Dictionary<string, string>? Tags { get; set; }
    }
}

public sealed record MediaProbeInfo(
    TimeSpan Duration,
    string FormatName,
    string VideoCodec,
    IReadOnlyList<string> AudioCodecs,
    int Width,
    int Height)
{
    public IReadOnlyList<MediaAudioStreamInfo> AudioStreams { get; init; } = [];
    public bool IsMp4Container => FormatName
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Any(format => format is "mov" or "mp4" or "m4a" or "3gp" or "3g2" or "mj2");

    public bool CanRemuxToMp4 =>
        string.Equals(VideoCodec, "h264", StringComparison.OrdinalIgnoreCase) &&
        AudioCodecs.All(codec => string.Equals(codec, "aac", StringComparison.OrdinalIgnoreCase));

    public bool IsNativeEditorFormat => IsMp4Container && CanRemuxToMp4;
}

public sealed record MediaAudioStreamInfo(int Index, string HandlerName, string Title)
{
    public string CaptureRole => new[] { HandlerName, Title }.Select(name => name.Trim().ToLowerInvariant())
        .FirstOrDefault(name => name is "game audio" or "microphone" or "discord" or "music" or "playback mix") ?? string.Empty;
}
