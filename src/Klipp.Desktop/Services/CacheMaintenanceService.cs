using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Klipp.Desktop.Services;

public sealed record CacheDirectorySnapshot(
    string Name,
    string DirectoryPath,
    int FileCount,
    long SizeBytes);

public sealed record CacheSnapshot(
    long TotalBytes,
    int FileCount,
    IReadOnlyList<CacheDirectorySnapshot> Directories);

public sealed record CacheCleanupResult(
    long BeforeBytes,
    long AfterBytes,
    int DeletedFiles,
    int FailedFiles,
    long ReclaimedBytes);

public sealed class CacheMaintenanceService
{
    private sealed record CacheFile(string Path, long Length, DateTime LastUseUtc, bool IsTemporary);

    private readonly IReadOnlyList<(string Name, string Path)> _directories;

    public CacheMaintenanceService(string? klippDataDirectory = null)
    {
        var root = klippDataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Klipp");
        _directories =
        [
            ("Proxies", Path.Combine(root, "proxies")),
            ("Thumbnails", Path.Combine(root, "thumbnails")),
            ("Effect previews", Path.Combine(root, "effect-previews")),
            ("Waveforms", Path.Combine(root, "waveforms")),
            ("Renders", Path.Combine(root, "render-cache"))
        ];

        foreach (var (_, path) in _directories)
            Directory.CreateDirectory(path);
    }

    public static int NormalizeBudgetMb(int value) => value switch
    {
        512 => 512,
        1024 => 1024,
        2048 => 2048,
        8192 => 8192,
        _ => 4096
    };

    public Task<CacheSnapshot> InspectAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Inspect(cancellationToken), cancellationToken);

    public Task<CacheCleanupResult> EnforceBudgetAsync(
        long budgetBytes,
        TimeSpan? minimumAge = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => Cleanup(
                Math.Max(0, budgetBytes),
                clearAll: false,
                minimumAge ?? TimeSpan.FromMinutes(10),
                cancellationToken),
            cancellationToken);

    public Task<CacheCleanupResult> ClearAsync(
        TimeSpan? minimumAge = null,
        CancellationToken cancellationToken = default) =>
        Task.Run(
            () => Cleanup(
                targetBytes: 0,
                clearAll: true,
                minimumAge ?? TimeSpan.FromMinutes(10),
                cancellationToken),
            cancellationToken);

    private CacheSnapshot Inspect(CancellationToken cancellationToken)
    {
        var snapshots = new List<CacheDirectorySnapshot>(_directories.Count);
        foreach (var (name, path) in _directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = EnumerateFiles(path, cancellationToken);
            snapshots.Add(new CacheDirectorySnapshot(name, path, files.Count, files.Sum(file => file.Length)));
        }

        return new CacheSnapshot(
            snapshots.Sum(snapshot => snapshot.SizeBytes),
            snapshots.Sum(snapshot => snapshot.FileCount),
            snapshots);
    }

    private CacheCleanupResult Cleanup(
        long targetBytes,
        bool clearAll,
        TimeSpan minimumAge,
        CancellationToken cancellationToken)
    {
        var files = _directories
            .SelectMany(directory => EnumerateFiles(directory.Path, cancellationToken))
            .OrderBy(file => file.LastUseUtc)
            .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var beforeBytes = files.Sum(file => file.Length);
        var remainingBytes = beforeBytes;
        var deletedFiles = 0;
        var failedFiles = 0;
        var now = DateTime.UtcNow;

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var abandonedTemporaryFile = file.IsTemporary && now - file.LastUseUtc >= TimeSpan.FromHours(24);
            if (!abandonedTemporaryFile)
            {
                if (!clearAll && remainingBytes <= targetBytes)
                    continue;
                if (now - file.LastUseUtc < minimumAge)
                    continue;
            }

            try
            {
                File.Delete(file.Path);
                remainingBytes = Math.Max(0, remainingBytes - file.Length);
                deletedFiles++;
            }
            catch
            {
                failedFiles++;
            }
        }

        return new CacheCleanupResult(
            beforeBytes,
            remainingBytes,
            deletedFiles,
            failedFiles,
            Math.Max(0, beforeBytes - remainingBytes));
    }

    private static List<CacheFile> EnumerateFiles(string directory, CancellationToken cancellationToken)
    {
        var files = new List<CacheFile>();
        if (!Directory.Exists(directory))
            return files;

        IEnumerable<string> paths;
        try { paths = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories); }
        catch { return files; }

        try
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var info = new FileInfo(path);
                    var lastUse = info.LastAccessTimeUtc > info.LastWriteTimeUtc
                        ? info.LastAccessTimeUtc
                        : info.LastWriteTimeUtc;
                    var name = info.Name;
                    files.Add(new CacheFile(
                        info.FullName,
                        Math.Max(0, info.Length),
                        lastUse,
                        name.StartsWith(".", StringComparison.Ordinal) ||
                        name.Contains(".tmp", StringComparison.OrdinalIgnoreCase)));
                }
                catch
                {
                    // A concurrently removed cache file can be ignored.
                }
            }
        }
        catch
        {
            // A directory may disappear while it is enumerated.
        }

        return files;
    }
}
