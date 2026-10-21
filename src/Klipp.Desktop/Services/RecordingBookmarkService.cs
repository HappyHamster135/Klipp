using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Models;

namespace Klipp.Desktop.Services;

/// <summary>Durable recording highlights, kept independently of temporary capture media and editor autosaves.</summary>
public sealed class RecordingBookmarkService
{
    private readonly string _directory;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public RecordingBookmarkService(string? directory = null) => _directory = Path.GetFullPath(directory ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klipp", "RecordingBookmarks"));

    public async Task SaveAsync(string outputPath, IReadOnlyList<RecordingSessionMarker> markers,
        double durationSeconds, CancellationToken cancellationToken = default)
    {
        if (markers.Count == 0) return;
        var normalized = markers.Where(marker => double.IsFinite(marker.TimeSeconds))
            .Select(marker => new RecordingSessionMarker(
                string.IsNullOrWhiteSpace(marker.Id) ? Guid.NewGuid().ToString("N") : marker.Id,
                Math.Clamp(marker.TimeSeconds, 0, double.IsFinite(durationSeconds) ? Math.Max(0, durationSeconds) : double.MaxValue),
                CleanLabel(marker.Label)))
            .GroupBy(marker => marker.Id, StringComparer.Ordinal).Select(group => group.First())
            .OrderBy(marker => marker.TimeSeconds).Take(10000).ToArray();
        if (normalized.Length == 0) return;
        var info = new FileInfo(outputPath);
        if (!info.Exists || info.Length == 0) throw new FileNotFoundException("The saved recording is missing.", outputPath);
        var document = new BookmarkDocument
        {
            Length = info.Length,
            ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks,
            Markers = normalized
        };
        Directory.CreateDirectory(_directory);
        var path = GetPath(outputPath);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task<IReadOnlyList<RecordingSessionMarker>> LoadAsync(string outputPath,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(outputPath);
        if (!File.Exists(path) || !File.Exists(outputPath)) return [];
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 8 * 1024 * 1024) return [];
            var document = await JsonSerializer.DeserializeAsync<BookmarkDocument>(stream, Options, cancellationToken).ConfigureAwait(false);
            var info = new FileInfo(outputPath);
            if (document is null || document.Version != 1 || document.Length != info.Length || document.ModifiedUtcTicks != info.LastWriteTimeUtc.Ticks)
                return [];
            return (document.Markers ?? []).Where(marker => marker is not null && !string.IsNullOrWhiteSpace(marker.Id) &&
                    double.IsFinite(marker.TimeSeconds) && marker.TimeSeconds >= 0)
                .Select(marker => marker with { Label = CleanLabel(marker.Label) })
                .GroupBy(marker => marker.Id, StringComparer.Ordinal).Select(group => group.First())
                .OrderBy(marker => marker.TimeSeconds).Take(10000).ToArray();
        }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    public void Move(string oldPath, string newPath)
    {
        var oldMetadata = GetPath(oldPath);
        if (!File.Exists(oldMetadata)) return;
        var newMetadata = GetPath(newPath);
        if (!string.Equals(oldMetadata, newMetadata, StringComparison.OrdinalIgnoreCase))
            File.Move(oldMetadata, newMetadata, overwrite: true);
    }

    private string GetPath(string outputPath)
    {
        var key = Path.GetFullPath(outputPath).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_directory, hash + ".json");
    }

    private static string CleanLabel(string? label)
    {
        var text = string.IsNullOrWhiteSpace(label) ? "Highlight" : label.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return text.Length > 80 ? text[..80] : text;
    }

    private sealed class BookmarkDocument
    {
        public int Version { get; set; } = 1;
        public long Length { get; set; }
        public long ModifiedUtcTicks { get; set; }
        public RecordingSessionMarker[]? Markers { get; set; }
    }
}
