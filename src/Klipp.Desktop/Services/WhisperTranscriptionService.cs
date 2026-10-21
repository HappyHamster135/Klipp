using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Subtitles;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Desktop.Services;

public sealed record WhisperTranscriptionResult(IReadOnlyList<SubtitleCue> Cues, string Language);

public sealed partial class WhisperTranscriptionService
{
    private readonly WhisperCppLocator _whisperLocator;
    private readonly FFmpegLocator _ffmpegLocator;

    public WhisperTranscriptionService(
        WhisperCppLocator? whisperLocator = null,
        FFmpegLocator? ffmpegLocator = null)
    {
        _whisperLocator = whisperLocator ?? new WhisperCppLocator();
        _ffmpegLocator = ffmpegLocator ?? new FFmpegLocator();
    }

    public bool IsReady => _whisperLocator.IsReady;

    public async Task<WhisperTranscriptionResult> TranscribeAsync(
        string sourcePath,
        double trimStartSeconds,
        double trimEndSeconds,
        string? language,
        IProgress<WhisperTranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("The selected clip could not be found.", sourcePath);

        trimStartSeconds = Math.Max(0, trimStartSeconds);
        trimEndSeconds = Math.Max(trimStartSeconds + 0.1, trimEndSeconds);
        language = NormalizeLanguage(language);
        progress?.Report(new WhisperTranscriptionProgress(0, "Preparing local transcription"));

        var installation = await _whisperLocator.EnsureInstalledAsync(progress, cancellationToken)
            .ConfigureAwait(false);
        var ffmpegPath = await _ffmpegLocator.GetFFmpegPathAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "Klipp",
            "transcription",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var wavPath = Path.Combine(temporaryDirectory, "clip.wav");
        var outputPrefix = Path.Combine(temporaryDirectory, "captions");

        try
        {
            progress?.Report(new WhisperTranscriptionProgress(0.64, "Preparing clip audio"));
            await ExtractAudioAsync(
                    ffmpegPath,
                    sourcePath,
                    wavPath,
                    trimStartSeconds,
                    trimEndSeconds - trimStartSeconds,
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new WhisperTranscriptionProgress(0.70, "Transcribing clip locally"));
            await RunWhisperAsync(
                    installation,
                    wavPath,
                    outputPrefix,
                    language,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            var srtPath = $"{outputPrefix}.srt";
            if (!File.Exists(srtPath))
                throw new InvalidOperationException("The transcription engine did not create subtitle output.");

            var content = await File.ReadAllTextAsync(srtPath, cancellationToken).ConfigureAwait(false);
            var cues = SrtSubtitleService.Parse(content)
                .Select(cue => new SubtitleCue(cue.Start, cue.End, NormalizeCueText(cue.Text)))
                .Where(cue => !string.IsNullOrWhiteSpace(cue.Text) && cue.End > cue.Start)
                .ToList();
            progress?.Report(new WhisperTranscriptionProgress(1, "Transcription complete"));
            return new WhisperTranscriptionResult(cues, language);
        }
        finally
        {
            TryDeleteDirectory(temporaryDirectory);
        }
    }

    public static IReadOnlyList<SubtitleCue> MapCuesToTimeline(
        IEnumerable<SubtitleCue> cues,
        double clipTimelineStartSeconds,
        double playbackSpeed,
        double clipTimelineDurationSeconds,
        double projectDurationSeconds)
    {
        ArgumentNullException.ThrowIfNull(cues);
        clipTimelineStartSeconds = Math.Max(0, clipTimelineStartSeconds);
        playbackSpeed = Math.Clamp(playbackSpeed, 0.25, 4);
        var clipTimelineEnd = Math.Min(
            Math.Max(0.1, projectDurationSeconds),
            clipTimelineStartSeconds + Math.Max(0.1, clipTimelineDurationSeconds));
        var mapped = new List<SubtitleCue>();

        foreach (var cue in cues.OrderBy(item => item.Start))
        {
            var text = NormalizeCueText(cue.Text);
            var start = clipTimelineStartSeconds + Math.Max(0, cue.Start.TotalSeconds) / playbackSpeed;
            var end = clipTimelineStartSeconds + Math.Max(cue.Start.TotalSeconds + 0.1, cue.End.TotalSeconds) / playbackSpeed;
            if (string.IsNullOrWhiteSpace(text) || start >= clipTimelineEnd - 0.01)
                continue;

            start = Math.Clamp(start, clipTimelineStartSeconds, clipTimelineEnd - 0.01);
            end = Math.Clamp(end, start + 0.01, clipTimelineEnd);
            mapped.Add(new SubtitleCue(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), text));
        }

        return mapped;
    }

    private static async Task ExtractAudioAsync(
        string ffmpegPath,
        string sourcePath,
        string outputPath,
        double trimStartSeconds,
        double durationSeconds,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateProcessStartInfo(ffmpegPath);
        AddArguments(startInfo,
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-ss", F(trimStartSeconds), "-i", sourcePath,
            "-t", F(durationSeconds), "-vn", "-ac", "1", "-ar", "16000",
            "-c:a", "pcm_s16le", outputPath);
        var error = await RunProcessAsync(startInfo, null, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 44)
            throw new InvalidOperationException($"Could not prepare clip audio. {error}".Trim());
    }

    private static async Task RunWhisperAsync(
        WhisperCppInstallation installation,
        string wavPath,
        string outputPrefix,
        string language,
        IProgress<WhisperTranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = CreateProcessStartInfo(installation.CliPath);
        startInfo.WorkingDirectory = Path.GetDirectoryName(installation.CliPath)!;
        var threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
        AddArguments(startInfo,
            "-m", installation.ModelPath,
            "-f", wavPath,
            "-l", language,
            "-t", threads.ToString(CultureInfo.InvariantCulture),
            "-ml", "72",
            "-sow",
            "-sns",
            "-osrt",
            "-pp",
            "-of", outputPrefix);

        var error = await RunProcessAsync(
                startInfo,
                percent => progress?.Report(new WhisperTranscriptionProgress(
                    0.70 + Math.Clamp(percent, 0, 100) / 100.0 * 0.29,
                    $"Transcribing clip locally · {percent}%")),
                cancellationToken)
            .ConfigureAwait(false);
        if (!File.Exists($"{outputPrefix}.srt"))
            throw new InvalidOperationException($"Local transcription failed. {error}".Trim());
    }

    private static async Task<string> RunProcessAsync(
        ProcessStartInfo startInfo,
        Action<int>? progress,
        CancellationToken cancellationToken)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(startInfo.FileName)}.");
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

        var errorLines = new Queue<string>();
        using var registration = cancellationToken.Register(() => TryKillProcess(process));
        var stdoutTask = DrainAsync(process.StandardOutput, null, null, cancellationToken);
        var stderrTask = DrainAsync(process.StandardError, progress, errorLines, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }

        var error = string.Join(" ", errorLines);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(startInfo.FileName)} exited with code {process.ExitCode}. {error}".Trim());
        return error;
    }

    private static async Task DrainAsync(
        StreamReader reader,
        Action<int>? progress,
        Queue<string>? errorLines,
        CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var match = ProgressRegex().Match(line);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var percent))
                progress?.Invoke(percent);

            if (errorLines is null || string.IsNullOrWhiteSpace(line))
                continue;
            errorLines.Enqueue(line.Trim());
            while (errorLines.Count > 8)
                errorLines.Dequeue();
        }
    }

    private static ProcessStartInfo CreateProcessStartInfo(string executablePath)
    {
        return new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private static void AddArguments(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
    }

    private static string NormalizeLanguage(string? language)
    {
        language = language?.Trim().ToLowerInvariant();
        return language is "sv" or "en" or "da" or "no" or "de" or "fr" or "es" ? language : "auto";
    }

    private static string NormalizeCueText(string? text)
    {
        return WhitespaceRegex().Replace(text?.Trim() ?? string.Empty, " ");
    }

    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch { }
    }

    [GeneratedRegex(@"progress\s*=\s*(\d{1,3})%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
