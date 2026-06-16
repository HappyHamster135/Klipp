using System.Diagnostics;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Encoding.Clipping;

/// <summary>
/// Extraherar klipp från segment-buffern producerad av FFmpegSegmentRecorder.
/// </summary>
/// <remarks>
/// Algorithm:
/// 1. Lista alla segment-filer i mappen (sorterade efter modification time)
/// 2. Räkna ut hur många senaste segment som behövs för att täcka begärt antal sekunder
/// 3. Skriv en FFmpeg concat-fil med deras sökvägar
/// 4. Kör ffmpeg -f concat -c copy för att slå ihop dem utan re-encoding (snabbt!)
///
/// Resultatet kan vara något längre än begärt eftersom vi inte kan klippa mitt i ett segment
/// utan re-encoding. Ex: begär 30s, segments är 10s → får 30-40s.
/// </remarks>
public sealed class ClipExtractor
{
    private readonly FFmpegLocator _locator;

    public ClipExtractor(FFmpegLocator? locator = null)
    {
        _locator = locator ?? new FFmpegLocator();
    }

    /// <summary>
    /// Plockar de senaste N sekunderna från segment-mappen och skriver till en MP4.
    /// </summary>
    /// <param name="segmentDirectory">Mappen där FFmpegSegmentRecorder skriver segments.</param>
    /// <param name="outputPath">Var den färdiga MP4-filen ska skrivas.</param>
    /// <param name="secondsToCapture">Hur många sekunder att plocka.</param>
    /// <param name="segmentDurationSeconds">Hur långt varje segment är.</param>
    public async Task<ClipExtractionResult> SaveLastSecondsAsync(
        string segmentDirectory,
        string outputPath,
        int secondsToCapture,
        int segmentDurationSeconds,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(segmentDirectory))
            throw new DirectoryNotFoundException($"Segment-mapp finns inte: {segmentDirectory}");

        var ffmpegPath = await _locator.GetFFmpegPathAsync(progress: null, cancellationToken)
            .ConfigureAwait(false);

        // Hitta alla segment, sortera efter modification time (äldsta först)
        var segments = new DirectoryInfo(segmentDirectory)
            .GetFiles("seg_*.mp4")
            .Where(f => f.Length > 0)
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();

        if (segments.Count == 0)
            throw new InvalidOperationException("Inga segment hittades — har inspelning startats?");

        // Räkna hur många segment vi behöver. Rundat uppåt så vi får MINST det begärda.
        var segmentsNeeded = (int)Math.Ceiling((double)secondsToCapture / segmentDurationSeconds);
        if (segmentsNeeded > segments.Count) segmentsNeeded = segments.Count;

        // Plocka de N senaste
        var selectedSegments = segments.Skip(segments.Count - segmentsNeeded).ToList();

        // Skapa concat-listfil för FFmpeg
        // Format:
        //   file 'path/seg_001.mp4'
        //   file 'path/seg_002.mp4'
        var concatListPath = Path.Combine(Path.GetTempPath(), $"klipp_concat_{Guid.NewGuid():N}.txt");

        try
        {
            // FFmpeg concat-fil måste ha forward slashes och escaped paths
            var lines = selectedSegments.Select(f =>
                $"file '{f.FullName.Replace('\\', '/').Replace("'", "'\\''")}'");
            await File.WriteAllLinesAsync(concatListPath, lines, cancellationToken)
                .ConfigureAwait(false);

            // Kör ffmpeg concat
            var args = $"-f concat -safe 0 -i \"{concatListPath}\" -c copy -y \"{outputPath}\"";

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Kunde inte starta ffmpeg.exe");

            // Konsumera output så pipen inte fylls
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"FFmpeg concat misslyckades (exit code {process.ExitCode}). Stderr:\n{stderr}");

            if (!File.Exists(outputPath))
                throw new InvalidOperationException("FFmpeg skapade ingen output-fil.");

            var outputInfo = new FileInfo(outputPath);

            return new ClipExtractionResult(
                OutputPath: outputPath,
                SegmentsUsed: selectedSegments.Count,
                ApproximateDurationSeconds: selectedSegments.Count * segmentDurationSeconds,
                FileSizeBytes: outputInfo.Length);
        }
        finally
        {
            try { File.Delete(concatListPath); } catch { /* ignorera */ }
        }
    }
}

/// <summary>
/// Resultat från en klipp-extraktion.
/// </summary>
public sealed record ClipExtractionResult(
    string OutputPath,
    int SegmentsUsed,
    int ApproximateDurationSeconds,
    long FileSizeBytes);
