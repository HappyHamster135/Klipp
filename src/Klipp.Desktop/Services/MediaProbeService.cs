using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    private readonly FFmpegLocator _locator;

    public MediaProbeService(FFmpegLocator? locator = null)
    {
        _locator = locator ?? new FFmpegLocator();
    }

    /// <summary>
    /// Läser videons varaktighet. Returnerar null om filen saknas, ffprobe inte finns,
    /// eller läsningen misslyckas.
    /// </summary>
    public async Task<TimeSpan?> GetDurationAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(videoPath)) return null;

        var ffprobePath = Path.Combine(_locator.BinDirectory, "ffprobe.exe");
        if (!File.Exists(ffprobePath)) return null;

        try
        {
            // ffprobe skriver bara varaktigheten i sekunder, t.ex. "12.345000"
            var args = $"-v error -show_entries format=duration " +
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
}
