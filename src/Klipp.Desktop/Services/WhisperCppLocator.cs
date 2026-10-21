using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Klipp.Desktop.Services;

public sealed record WhisperCppInstallation(string CliPath, string ModelPath);

public sealed record WhisperTranscriptionProgress(double Value, string Status);

public sealed class WhisperCppLocator
{
    public const string WhisperVersion = "1.9.1";
    public const string ModelName = "base";
    public const long ModelLength = 147_951_465;

    private const string ModelSha256 = "60ED5BC3DD14EEA856493D334349B405782DDCAF0028D4B5DF4088345FBA2EFE";
    private const string CliDownloadUrl =
        "https://github.com/ggml-org/whisper.cpp/releases/download/v1.9.1/whisper-bin-x64.zip";
    private const string ModelDownloadUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";
    private static readonly SemaphoreSlim InstallLock = new(1, 1);

    public WhisperCppLocator(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Klipp",
            "transcription");
        BinDirectory = Path.Combine(RootDirectory, $"whisper-{WhisperVersion}");
        ModelDirectory = Path.Combine(RootDirectory, "models");
        Directory.CreateDirectory(RootDirectory);
    }

    public string RootDirectory { get; }
    public string BinDirectory { get; }
    public string ModelDirectory { get; }
    public string CliPath => Path.Combine(BinDirectory, "whisper-cli.exe");
    public string ModelPath => Path.Combine(ModelDirectory, "ggml-base.bin");
    private string ModelMarkerPath => Path.Combine(ModelDirectory, "ggml-base.sha256");

    public bool IsReady => IsCliInstalled() && HasVerifiedModelMarker();

    public async Task<WhisperCppInstallation> EnsureInstalledAsync(
        IProgress<WhisperTranscriptionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InstallLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsCliInstalled())
                await InstallCliAsync(progress, cancellationToken).ConfigureAwait(false);

            await EnsureModelAsync(progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new WhisperTranscriptionProgress(0.62, "Local transcription ready"));
            return new WhisperCppInstallation(CliPath, ModelPath);
        }
        finally
        {
            InstallLock.Release();
        }
    }

    private bool IsCliInstalled()
    {
        return File.Exists(CliPath) &&
               File.Exists(Path.Combine(BinDirectory, "whisper.dll")) &&
               File.Exists(Path.Combine(BinDirectory, "ggml.dll")) &&
               Directory.Exists(BinDirectory) &&
               Directory.EnumerateFiles(BinDirectory, "ggml-cpu-*.dll").Any();
    }

    private bool HasVerifiedModelMarker()
    {
        if (!File.Exists(ModelPath) || !File.Exists(ModelMarkerPath))
            return false;

        try
        {
            return new FileInfo(ModelPath).Length == ModelLength &&
                   string.Equals(File.ReadAllText(ModelMarkerPath).Trim(), ModelSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task InstallCliAsync(
        IProgress<WhisperTranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new WhisperTranscriptionProgress(0.01, "Downloading transcription engine"));
        var archivePath = Path.Combine(Path.GetTempPath(), $"klipp-whisper-{Guid.NewGuid():N}.zip");
        var stagingDirectory = Path.Combine(Path.GetTempPath(), $"klipp-whisper-{Guid.NewGuid():N}");
        try
        {
            await DownloadFileAsync(
                    CliDownloadUrl,
                    archivePath,
                    0.01,
                    0.12,
                    "Downloading transcription engine",
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            Directory.CreateDirectory(stagingDirectory);
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                foreach (var entry in archive.Entries.Where(IsRequiredCliEntry))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entry.ExtractToFile(Path.Combine(stagingDirectory, entry.Name), overwrite: true);
                }
            }

            if (!File.Exists(Path.Combine(stagingDirectory, "whisper-cli.exe")) ||
                !File.Exists(Path.Combine(stagingDirectory, "whisper.dll")) ||
                !File.Exists(Path.Combine(stagingDirectory, "ggml.dll")) ||
                !Directory.EnumerateFiles(stagingDirectory, "ggml-cpu-*.dll").Any())
            {
                throw new InvalidDataException("The official whisper.cpp package did not contain the expected files.");
            }

            Directory.CreateDirectory(BinDirectory);
            foreach (var sourcePath in Directory.EnumerateFiles(stagingDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(sourcePath, Path.Combine(BinDirectory, Path.GetFileName(sourcePath)), overwrite: true);
            }
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private async Task EnsureModelAsync(
        IProgress<WhisperTranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (HasVerifiedModelMarker())
            return;

        Directory.CreateDirectory(ModelDirectory);
        if (File.Exists(ModelPath) && new FileInfo(ModelPath).Length == ModelLength)
        {
            progress?.Report(new WhisperTranscriptionProgress(0.16, "Verifying speech model"));
            if (await HasExpectedHashAsync(ModelPath, cancellationToken).ConfigureAwait(false))
            {
                await WriteModelMarkerAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        var temporaryPath = Path.Combine(ModelDirectory, $"ggml-base-{Guid.NewGuid():N}.download");
        try
        {
            await DownloadFileAsync(
                    ModelDownloadUrl,
                    temporaryPath,
                    0.14,
                    0.55,
                    "Downloading multilingual speech model",
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            if (new FileInfo(temporaryPath).Length != ModelLength)
                throw new InvalidDataException("The downloaded speech model has an unexpected size.");

            progress?.Report(new WhisperTranscriptionProgress(0.56, "Verifying speech model"));
            if (!await HasExpectedHashAsync(temporaryPath, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("The downloaded speech model failed integrity verification.");

            File.Move(temporaryPath, ModelPath, overwrite: true);
            await WriteModelMarkerAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private async Task WriteModelMarkerAsync(CancellationToken cancellationToken)
    {
        var temporaryPath = $"{ModelMarkerPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, ModelSha256, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, ModelMarkerPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static bool IsRequiredCliEntry(ZipArchiveEntry entry)
    {
        if (!entry.FullName.StartsWith("Release/", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(entry.Name))
        {
            return false;
        }

        return string.Equals(entry.Name, "whisper-cli.exe", StringComparison.OrdinalIgnoreCase) ||
               entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
               (entry.Name.StartsWith("whisper", StringComparison.OrdinalIgnoreCase) ||
                entry.Name.StartsWith("ggml", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task DownloadFileAsync(
        string url,
        string destinationPath,
        double startProgress,
        double endProgress,
        string status,
        IProgress<WhisperTranscriptionProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Klipp/1.0");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        var downloadedBytes = 0L;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            downloadedBytes += read;
            if (totalBytes > 0)
            {
                var fraction = Math.Clamp((double)downloadedBytes / totalBytes, 0, 1);
                progress?.Report(new WhisperTranscriptionProgress(
                    startProgress + (endProgress - startProgress) * fraction,
                    status));
            }
        }
    }

    private static async Task<bool> HasExpectedHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexString(hash), ModelSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
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
}
