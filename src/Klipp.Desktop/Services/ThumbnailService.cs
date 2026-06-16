using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Desktop.Services;

/// <summary>
/// Genererar och cachar thumbnail-bilder (en stillbild ur videon) för klippkort.
/// Använder FFmpeg för att plocka en frame och spara som JPG i en cache-mapp.
/// </summary>
public sealed class ThumbnailService
{
    private readonly FFmpegLocator _locator;
    private readonly string _thumbnailDirectory;

    public ThumbnailService(FFmpegLocator? locator = null)
    {
        _locator = locator ?? new FFmpegLocator();

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _thumbnailDirectory = Path.Combine(localAppData, "Klipp", "thumbnails");
        Directory.CreateDirectory(_thumbnailDirectory);
    }

    /// <summary>
    /// Returnerar sökväg till en thumbnail för videon. Genererar den om den inte finns.
    /// Returnerar null om generering misslyckas (då visar kortet sin fallback-ikon).
    /// </summary>
    public async Task<string?> GetOrCreateThumbnailAsync(
        string videoPath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(videoPath)) return null;

        var thumbName = Path.GetFileNameWithoutExtension(videoPath) + ".jpg";
        var thumbPath = Path.Combine(_thumbnailDirectory, thumbName);

        // Redan genererad och nyare än videon? Återanvänd.
        if (File.Exists(thumbPath) &&
            File.GetLastWriteTimeUtc(thumbPath) >= File.GetLastWriteTimeUtc(videoPath))
        {
            return thumbPath;
        }

        try
        {
            var ffmpegPath = await _locator.GetFFmpegPathAsync(progress: null, cancellationToken)
                .ConfigureAwait(false);

            // Försök plocka en frame vid 1 sekund. Funkar för de flesta klipp.
            var ok = await ExtractFrameAsync(ffmpegPath, videoPath, thumbPath, "00:00:01", cancellationToken)
                .ConfigureAwait(false);

            // Fallback för väldigt korta klipp: plocka första framen istället.
            if (!ok || !File.Exists(thumbPath))
            {
                ok = await ExtractFrameAsync(ffmpegPath, videoPath, thumbPath, "00:00:00", cancellationToken)
                    .ConfigureAwait(false);
            }

            return File.Exists(thumbPath) ? thumbPath : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> ExtractFrameAsync(
        string ffmpegPath,
        string videoPath,
        string thumbPath,
        string seekTime,
        CancellationToken cancellationToken)
    {
        // -ss FÖRE -i = snabb seek. -vframes 1 = en frame.
        // scale=480:-2 = skala till 480px bredd, behåll proportioner (höjd jämn).
        var args = $"-ss {seekTime} -i \"{videoPath}\" -vframes 1 " +
                   $"-vf scale=480:-2 -q:v 3 -y \"{thumbPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null) return false;

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        _ = process.StandardOutput.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await stderrTask.ConfigureAwait(false);

        return process.ExitCode == 0;
    }
}
