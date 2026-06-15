using System.IO.Compression;
using System.Net.Http;

namespace Klipp.Encoding.FFmpeg;

/// <summary>
/// Hittar eller laddar ner FFmpeg vid behov. FFmpeg distribueras inte med appen —
/// vi laddar ner officiella Windows-builds från gyan.dev vid första körningen
/// och cachar i %LocalAppData%\Klipp\bin\.
/// </summary>
/// <remarks>
/// Detta är samma mönster som OBS, Streamlabs och andra appar använder för
/// att slippa packa 50+ MB FFmpeg-binärer med installern.
/// </remarks>
public sealed class FFmpegLocator
{
    // gyan.dev:s "essentials" build innehåller bara binärerna vi behöver (~50 MB)
    private const string FFmpegDownloadUrl =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    /// <summary>Mapp där vi cachar ffmpeg.exe och ffprobe.exe.</summary>
    public string BinDirectory { get; }

    /// <summary>Förväntad sökväg till ffmpeg.exe (existerar inte nödvändigtvis).</summary>
    public string FFmpegPath => Path.Combine(BinDirectory, "ffmpeg.exe");

    /// <summary>Returnerar true om ffmpeg.exe redan finns lokalt.</summary>
    public bool IsInstalled => File.Exists(FFmpegPath);

    public FFmpegLocator()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        BinDirectory = Path.Combine(localAppData, "Klipp", "bin");
        Directory.CreateDirectory(BinDirectory);
    }

    /// <summary>
    /// Returnerar sökvägen till ffmpeg.exe. Laddar ner och packar upp om filen saknas.
    /// </summary>
    /// <param name="progress">Anropas med nedladdnings-progress (0-1) under download.</param>
    /// <param name="cancellationToken">För att avbryta nedladdningen.</param>
    public async Task<string> GetFFmpegPathAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (IsInstalled) return FFmpegPath;

        await DownloadAndExtractAsync(progress, cancellationToken).ConfigureAwait(false);

        if (!IsInstalled)
            throw new InvalidOperationException(
                $"FFmpeg installerades inte korrekt. Förväntad sökväg: {FFmpegPath}");

        return FFmpegPath;
    }

    private async Task DownloadAndExtractAsync(
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        // Ladda ner zip till tempfil
        var tempZip = Path.Combine(Path.GetTempPath(), $"klipp-ffmpeg-{Guid.NewGuid():N}.zip");

        try
        {
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
            using (var response = await http.GetAsync(
                FFmpegDownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();

                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                var downloadedBytes = 0L;

                using var httpStream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                using var fileStream = new FileStream(
                    tempZip, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 81920, useAsync: true);

                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = await httpStream
                    .ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                        .ConfigureAwait(false);
                    downloadedBytes += bytesRead;

                    if (totalBytes > 0)
                        progress?.Report((double)downloadedBytes / totalBytes);
                }
            }

            // Packa upp — vi vill bara ha ffmpeg.exe och ffprobe.exe ur arkivets bin/-mapp
            ExtractFFmpegBinaries(tempZip);
        }
        finally
        {
            try { File.Delete(tempZip); } catch { /* ignorera städ-fel */ }
        }
    }

    private void ExtractFFmpegBinaries(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            // gyan.dev:s zip har struktur: ffmpeg-release-essentials/bin/ffmpeg.exe
            // Vi extraherar bara filer från /bin/ och struntar i resten.
            var name = entry.Name; // bara filnamn, ingen path
            if (name is not "ffmpeg.exe" and not "ffprobe.exe")
                continue;

            // Verifiera att den är från bin/-mappen (inte t.ex. doc/ eller liknande)
            if (!entry.FullName.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
                continue;

            var destPath = Path.Combine(BinDirectory, name);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }
}
