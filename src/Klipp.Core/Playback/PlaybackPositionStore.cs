using System.Text.Json;

namespace Klipp.Core.Playback;

public sealed record PlaybackMediaIdentity(string Path, long Length, long LastWriteUtcTicks, long CreationUtcTicks)
{
    public static PlaybackMediaIdentity? TryCreate(string path)
    {
        try
        {
            var file = new FileInfo(System.IO.Path.GetFullPath(path));
            return file.Exists ? new(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return null; }
    }
}

/// <summary>Small, bounded resume history. A replaced or re-exported file starts at the beginning.</summary>
public sealed class PlaybackPositionStore
{
    public const int MaximumEntries = 500;
    private const int MaximumFileBytes = 512 * 1024;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Entry(PlaybackMediaIdentity Media, double PositionSeconds, long UpdatedUtcTicks);

    public PlaybackPositionStore(string path)
    {
        _path = path;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaximumFileBytes) return;
            var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path)) ?? [];
            foreach (var entry in entries.Where(entry => entry?.Media is not null &&
                         !string.IsNullOrWhiteSpace(entry.Media.Path) && double.IsFinite(entry.PositionSeconds) &&
                         entry.PositionSeconds >= 0).OrderByDescending(entry => entry.UpdatedUtcTicks).Take(MaximumEntries))
                _entries.TryAdd(entry.Media.Path, entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        { /* A damaged or unavailable history must never prevent playback. */ }
    }

    public double GetPosition(PlaybackMediaIdentity? media, double durationSeconds)
    {
        if (media is null) return 0;
        lock (_gate)
            return _entries.TryGetValue(media.Path, out var entry) &&
                string.Equals(entry.Media.Path, media.Path, StringComparison.OrdinalIgnoreCase) &&
                entry.Media.Length == media.Length && entry.Media.LastWriteUtcTicks == media.LastWriteUtcTicks &&
                entry.Media.CreationUtcTicks == media.CreationUtcTicks
                ? NormalizePosition(entry.PositionSeconds, durationSeconds) : 0;
    }

    public void Remember(PlaybackMediaIdentity? media, double positionSeconds, double durationSeconds)
    {
        if (media is null || !double.IsFinite(durationSeconds) || durationSeconds <= 0) return;
        lock (_gate)
        {
            _entries[media.Path] = new(media, NormalizePosition(positionSeconds, durationSeconds), DateTime.UtcNow.Ticks);
            foreach (var old in _entries.Values.OrderByDescending(entry => entry.UpdatedUtcTicks).Skip(MaximumEntries).ToArray())
                _entries.Remove(old.Media.Path);
            var temporary = _path + ".tmp";
            try
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(temporary, JsonSerializer.Serialize(_entries.Values.ToArray()));
                File.Move(temporary, _path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            { /* Keep the in-memory position if persistence is temporarily unavailable. */ }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public static double NormalizePosition(double positionSeconds, double durationSeconds)
    {
        if (!double.IsFinite(positionSeconds) || !double.IsFinite(durationSeconds) || durationSeconds <= 0) return 0;
        var position = Math.Clamp(positionSeconds, 0, durationSeconds);
        // Completed clips play again from the start; brief clips keep the same relative tolerance.
        return position >= durationSeconds - Math.Min(1, durationSeconds * .05) ? 0 : position;
    }
}
