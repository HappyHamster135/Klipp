using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Desktop.Services;

public sealed class MediaImportService
{
    private const string IndexFileName = ".klipp-import-index.json";
    private const int CopyBufferSize = 1024 * 1024;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v", ".ts", ".mts", ".m2ts"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly FFmpegLocator _locator;
    private readonly MediaProbeService _probe;
    private readonly SemaphoreSlim _importGate = new(1, 1);

    public MediaImportService(FFmpegLocator? locator = null, MediaProbeService? probe = null)
    {
        _locator = locator ?? new FFmpegLocator();
        _probe = probe ?? new MediaProbeService(_locator);
    }

    public async Task<ClipImportResult> ImportAsync(
        string sourcePath,
        string targetDirectory,
        IProgress<ClipImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("The clip no longer exists.", sourcePath);

        var extension = Path.GetExtension(sourcePath);
        if (!SupportedExtensions.Contains(extension))
            throw new InvalidOperationException($"The {extension} video format cannot be imported.");

        var fullSourcePath = Path.GetFullPath(sourcePath);
        var fullTargetDirectory = Path.GetFullPath(targetDirectory);
        Directory.CreateDirectory(fullTargetDirectory);
        if (string.Equals(
                Path.GetDirectoryName(fullSourcePath),
                fullTargetDirectory,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return new ClipImportResult(fullSourcePath, ClipImportKind.AlreadyInLibrary);
        }

        await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            progress?.Report(new ClipImportProgress("Checking for duplicates", null));
            var sourceInfo = new FileInfo(fullSourcePath);
            var sourceFingerprint = await ComputeFingerprintAsync(
                    fullSourcePath,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            var indexPath = Path.Combine(fullTargetDirectory, IndexFileName);
            var importIndex = await LoadIndexAsync(indexPath, cancellationToken).ConfigureAwait(false);

            var indexedDuplicate = await FindIndexedDuplicateAsync(
                    importIndex,
                    sourceFingerprint,
                    fullTargetDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
            if (indexedDuplicate is not null)
            {
                await SaveIndexAsync(indexPath, importIndex, cancellationToken).ConfigureAwait(false);
                return new ClipImportResult(indexedDuplicate, ClipImportKind.Duplicate);
            }

            var byteDuplicate = await FindByteIdenticalLibraryFileAsync(
                    fullTargetDirectory,
                    fullSourcePath,
                    sourceInfo.Length,
                    sourceFingerprint,
                    cancellationToken)
                .ConfigureAwait(false);
            if (byteDuplicate is not null)
            {
                await RememberImportAsync(
                        importIndex,
                        indexPath,
                        sourceFingerprint,
                        sourceInfo,
                        byteDuplicate,
                        ClipImportKind.Duplicate,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new ClipImportResult(byteDuplicate, ClipImportKind.Duplicate);
            }

            progress?.Report(new ClipImportProgress("Analyzing video", null));
            var mediaInfo = await _probe.GetMediaInfoAsync(fullSourcePath, cancellationToken)
                .ConfigureAwait(false);
            if (mediaInfo is null || mediaInfo.Duration <= TimeSpan.FromSeconds(0.1))
                throw new InvalidOperationException("The video's codec or duration could not be read.");

            var targetPath = GetUniqueTargetPath(fullTargetDirectory, fullSourcePath);
            temporaryPath = Path.Combine(fullTargetDirectory, $".klipp-import-{Guid.NewGuid():N}.mp4");
            ClipImportKind importKind;
            if (mediaInfo.IsNativeEditorFormat &&
                string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(new ClipImportProgress("Copying video", 0));
                await CopyFileAsync(fullSourcePath, temporaryPath, progress, cancellationToken)
                    .ConfigureAwait(false);
                importKind = ClipImportKind.Copied;
            }
            else if (mediaInfo.CanRemuxToMp4)
            {
                progress?.Report(new ClipImportProgress("Repackaging as MP4", 0));
                var remuxed = await RunFFmpegAsync(
                        fullSourcePath,
                        temporaryPath,
                        mediaInfo.Duration,
                        transcode: false,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (remuxed)
                {
                    importKind = ClipImportKind.Remuxed;
                }
                else
                {
                    TryDelete(temporaryPath);
                    progress?.Report(new ClipImportProgress("Converting to H.264", 0));
                    await RequireFFmpegSuccessAsync(
                            fullSourcePath,
                            temporaryPath,
                            mediaInfo.Duration,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);
                    importKind = ClipImportKind.Transcoded;
                }
            }
            else
            {
                progress?.Report(new ClipImportProgress("Converting to H.264", 0));
                await RequireFFmpegSuccessAsync(
                        fullSourcePath,
                        temporaryPath,
                        mediaInfo.Duration,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                importKind = ClipImportKind.Transcoded;
            }

            progress?.Report(new ClipImportProgress("Verifying import", null));
            var importedInfo = await _probe.GetMediaInfoAsync(temporaryPath, cancellationToken)
                .ConfigureAwait(false);
            if (importedInfo is null || !importedInfo.IsNativeEditorFormat ||
                importedInfo.Duration <= TimeSpan.FromSeconds(0.1))
            {
                throw new InvalidOperationException("The imported video could not be verified as H.264/AAC MP4.");
            }

            File.Move(temporaryPath, targetPath);
            temporaryPath = null;
            try
            {
                await RememberImportAsync(
                        importIndex,
                        indexPath,
                        sourceFingerprint,
                        sourceInfo,
                        targetPath,
                        importKind,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                TryDelete(targetPath);
                throw;
            }
            progress?.Report(new ClipImportProgress("Import complete", 1));
            return new ClipImportResult(targetPath, importKind);
        }
        finally
        {
            if (temporaryPath is not null)
                TryDelete(temporaryPath);
            _importGate.Release();
        }
    }

    private async Task RequireFFmpegSuccessAsync(
        string sourcePath,
        string outputPath,
        TimeSpan duration,
        IProgress<ClipImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var succeeded = await RunFFmpegAsync(
                sourcePath,
                outputPath,
                duration,
                transcode: true,
                progress,
                cancellationToken)
            .ConfigureAwait(false);
        if (!succeeded)
            throw new InvalidOperationException("FFmpeg could not convert the video to an editable format.");
    }

    private async Task<bool> RunFFmpegAsync(
        string sourcePath,
        string outputPath,
        TimeSpan duration,
        bool transcode,
        IProgress<ClipImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = await _locator.GetFFmpegPathAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
                     "-i", sourcePath,
                     "-map", "0:v:0", "-map", "0:a?"
                 })
        {
            psi.ArgumentList.Add(argument);
        }

        if (transcode)
        {
            foreach (var argument in new[]
                     {
                         "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2",
                         "-c:v", "libx264", "-preset", "medium", "-crf", "18",
                         "-pix_fmt", "yuv420p",
                         "-c:a", "aac", "-b:a", "192k", "-ar", "48000"
                     })
            {
                psi.ArgumentList.Add(argument);
            }
        }
        else
        {
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("copy");
        }

        foreach (var argument in new[]
                 {
                     "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", outputPath
                 })
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi);
        if (process is null)
            return false;

        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) ||
                    !long.TryParse(line[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
                {
                    continue;
                }

                var fraction = duration.TotalSeconds <= 0
                    ? 0
                    : Math.Clamp(microseconds / 1_000_000.0 / duration.TotalSeconds, 0, 0.99);
                progress?.Report(new ClipImportProgress(
                    transcode ? "Converting to H.264" : "Repackaging as MP4",
                    fraction));
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            return process.ExitCode == 0 && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string targetPath,
        IProgress<ClipImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var target = new FileStream(
            targetPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long copied = 0;
            var lastReportedFraction = -1.0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                var fraction = source.Length == 0 ? 1 : Math.Clamp((double)copied / source.Length, 0, 1);
                if (fraction >= 1 || fraction - lastReportedFraction >= 0.01)
                {
                    progress?.Report(new ClipImportProgress("Copying video", fraction));
                    lastReportedFraction = fraction;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<string> ComputeFingerprintAsync(
        string path,
        IProgress<ClipImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long consumed = 0;
            var lastReportedFraction = -1.0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                       .ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                consumed += read;
                var fraction = stream.Length == 0 ? 1 : Math.Clamp((double)consumed / stream.Length, 0, 1);
                if (fraction >= 1 || fraction - lastReportedFraction >= 0.01)
                {
                    progress?.Report(new ClipImportProgress("Checking for duplicates", fraction));
                    lastReportedFraction = fraction;
                }
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<string?> FindByteIdenticalLibraryFileAsync(
        string targetDirectory,
        string sourcePath,
        long sourceLength,
        string sourceFingerprint,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in Directory.EnumerateFiles(targetDirectory, "*.mp4"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(candidate, sourcePath, StringComparison.OrdinalIgnoreCase) ||
                new FileInfo(candidate).Length != sourceLength)
            {
                continue;
            }

            var candidateFingerprint = await ComputeFingerprintAsync(candidate, null, cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(candidateFingerprint, sourceFingerprint, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }

    private static async Task<string?> FindIndexedDuplicateAsync(
        ImportIndex index,
        string sourceFingerprint,
        string targetDirectory,
        CancellationToken cancellationToken)
    {
        if (!index.Entries.TryGetValue(sourceFingerprint, out var entry))
            return null;

        var expectedPath = Path.Combine(targetDirectory, entry.TargetFileName);
        if (await IsMatchingTargetAsync(expectedPath, entry, cancellationToken).ConfigureAwait(false))
            return expectedPath;

        foreach (var candidate in Directory.EnumerateFiles(targetDirectory, "*.mp4"))
        {
            if (string.Equals(candidate, expectedPath, StringComparison.OrdinalIgnoreCase) ||
                new FileInfo(candidate).Length != entry.TargetLength)
            {
                continue;
            }

            if (await IsMatchingTargetAsync(candidate, entry, cancellationToken).ConfigureAwait(false))
            {
                entry.TargetFileName = Path.GetFileName(candidate);
                return candidate;
            }
        }

        index.Entries.Remove(sourceFingerprint);
        return null;
    }

    private static async Task<bool> IsMatchingTargetAsync(
        string path,
        ImportIndexEntry entry,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != entry.TargetLength)
            return false;
        var fingerprint = await ComputeFingerprintAsync(path, null, cancellationToken).ConfigureAwait(false);
        return string.Equals(fingerprint, entry.TargetFingerprint, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RememberImportAsync(
        ImportIndex index,
        string indexPath,
        string sourceFingerprint,
        FileInfo sourceInfo,
        string targetPath,
        ClipImportKind importKind,
        CancellationToken cancellationToken)
    {
        var targetInfo = new FileInfo(targetPath);
        var targetFingerprint = string.Equals(sourceInfo.FullName, targetInfo.FullName, StringComparison.OrdinalIgnoreCase) ||
                                (sourceInfo.Length == targetInfo.Length &&
                                 string.Equals(sourceFingerprint, await ComputeFingerprintAsync(targetPath, null, cancellationToken)
                                     .ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
            ? sourceFingerprint
            : await ComputeFingerprintAsync(targetPath, null, cancellationToken).ConfigureAwait(false);
        index.Entries[sourceFingerprint] = new ImportIndexEntry
        {
            SourceFileName = sourceInfo.Name,
            SourceLength = sourceInfo.Length,
            TargetFileName = targetInfo.Name,
            TargetLength = targetInfo.Length,
            TargetFingerprint = targetFingerprint,
            ImportKind = importKind.ToString(),
            ImportedUtc = DateTime.UtcNow
        };
        await SaveIndexAsync(indexPath, index, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ImportIndex> LoadIndexAsync(string indexPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(indexPath))
            return new ImportIndex();

        try
        {
            await using var stream = File.OpenRead(indexPath);
            var loaded = await JsonSerializer.DeserializeAsync<ImportIndex>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return loaded is null
                ? new ImportIndex()
                : new ImportIndex
                {
                    Entries = new Dictionary<string, ImportIndexEntry>(
                        loaded.Entries ?? [],
                        StringComparer.OrdinalIgnoreCase)
                };
        }
        catch (JsonException)
        {
            return new ImportIndex();
        }
        catch (IOException)
        {
            return new ImportIndex();
        }
    }

    private static async Task SaveIndexAsync(
        string indexPath,
        ImportIndex index,
        CancellationToken cancellationToken)
    {
        var temporaryPath = indexPath + ".tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         16 * 1024,
                         FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, index, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, indexPath, overwrite: true);
    }

    private static string GetUniqueTargetPath(string targetDirectory, string sourcePath)
    {
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(name))
            name = $"import_{DateTime.Now:yyyyMMdd_HHmmss}";
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        var candidate = Path.Combine(targetDirectory, $"{name}.mp4");
        var suffix = 2;
        while (File.Exists(candidate))
            candidate = Path.Combine(targetDirectory, $"{name} ({suffix++}).mp4");
        return candidate;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed class ImportIndex
    {
        public Dictionary<string, ImportIndexEntry> Entries { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ImportIndexEntry
    {
        public string SourceFileName { get; set; } = string.Empty;
        public long SourceLength { get; set; }
        public string TargetFileName { get; set; } = string.Empty;
        public long TargetLength { get; set; }
        public string TargetFingerprint { get; set; } = string.Empty;
        public string ImportKind { get; set; } = string.Empty;
        public DateTime ImportedUtc { get; set; }
    }
}

public enum ClipImportKind
{
    AlreadyInLibrary,
    Duplicate,
    Copied,
    Remuxed,
    Transcoded
}

public sealed record ClipImportProgress(string Stage, double? Fraction);

public sealed record ClipImportResult(string FilePath, ClipImportKind Kind)
{
    public bool WasDuplicate => Kind is ClipImportKind.AlreadyInLibrary or ClipImportKind.Duplicate;

    public string StatusText => Kind switch
    {
        ClipImportKind.AlreadyInLibrary => "The clip is already in your library",
        ClipImportKind.Duplicate => "Duplicate found — using the existing clip",
        ClipImportKind.Remuxed => "Clip repackaged as MP4",
        ClipImportKind.Transcoded => "Clip converted to H.264",
        _ => "Clip imported"
    };
}
