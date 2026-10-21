using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Klipp.Desktop.Services;

public sealed record ClipLibraryMetadata(
    bool IsFavorite,
    IReadOnlyList<string> Tags,
    string Collection)
{
    public static ClipLibraryMetadata Empty { get; } = new(false, [], string.Empty);
}

public sealed class ClipLibraryMetadataService
{
    private const int CurrentVersion = 1;
    private readonly object _gate = new();
    private readonly string _metadataPath;
    private readonly Dictionary<string, ClipLibraryMetadataEntry> _items =
        new(StringComparer.OrdinalIgnoreCase);

    public ClipLibraryMetadataService(string? metadataPath = null)
    {
        _metadataPath = string.IsNullOrWhiteSpace(metadataPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Klipp",
                "library.metadata.json")
            : Path.GetFullPath(metadataPath);
        Load();
    }

    public string MetadataPath => _metadataPath;

    public ClipLibraryMetadata Get(string filePath)
    {
        var key = NormalizePath(filePath);
        lock (_gate)
        {
            return _items.TryGetValue(key, out var entry)
                ? ToMetadata(entry)
                : ClipLibraryMetadata.Empty;
        }
    }

    public ClipLibraryMetadata Update(
        string filePath,
        bool isFavorite,
        IEnumerable<string>? tags,
        string? collection)
    {
        var key = NormalizePath(filePath);
        var normalized = new ClipLibraryMetadataEntry
        {
            IsFavorite = isFavorite,
            Tags = NormalizeTags(tags),
            Collection = NormalizeCollection(collection)
        };

        lock (_gate)
        {
            if (HasData(normalized))
                _items[key] = normalized;
            else
                _items.Remove(key);
            SaveLocked();
        }

        return ToMetadata(normalized);
    }

    public ClipLibraryMetadata ToggleFavorite(string filePath)
    {
        var current = Get(filePath);
        return Update(filePath, !current.IsFavorite, current.Tags, current.Collection);
    }

    public void Move(string oldPath, string newPath)
    {
        var oldKey = NormalizePath(oldPath);
        var newKey = NormalizePath(newPath);
        if (string.Equals(oldKey, newKey, StringComparison.OrdinalIgnoreCase))
            return;

        lock (_gate)
        {
            if (!_items.Remove(oldKey, out var source))
                return;

            if (_items.TryGetValue(newKey, out var destination))
            {
                source.IsFavorite |= destination.IsFavorite;
                source.Tags = NormalizeTags(source.Tags.Concat(destination.Tags));
                if (string.IsNullOrWhiteSpace(source.Collection))
                    source.Collection = destination.Collection;
            }

            _items[newKey] = source;
            SaveLocked();
        }
    }

    public void Remove(string filePath)
    {
        var key = NormalizePath(filePath);
        lock (_gate)
        {
            if (!_items.Remove(key))
                return;
            SaveLocked();
        }
    }

    public IReadOnlyList<string> GetCollections()
    {
        lock (_gate)
        {
            return _items.Values
                .Select(entry => entry.Collection)
                .Where(collection => !string.IsNullOrWhiteSpace(collection))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(collection => collection, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    private void Load()
    {
        lock (_gate)
        {
            _items.Clear();
            if (!File.Exists(_metadataPath))
                return;

            try
            {
                var json = File.ReadAllText(_metadataPath);
                var document = JsonSerializer.Deserialize<ClipLibraryMetadataDocument>(json);
                if (document?.Items is null)
                    return;

                foreach (var pair in document.Items)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                        continue;

                    try
                    {
                        var entry = new ClipLibraryMetadataEntry
                        {
                            IsFavorite = pair.Value.IsFavorite,
                            Tags = NormalizeTags(pair.Value.Tags),
                            Collection = NormalizeCollection(pair.Value.Collection)
                        };
                        if (HasData(entry))
                            _items[NormalizePath(pair.Key)] = entry;
                    }
                    catch (ArgumentException)
                    {
                    }
                    catch (NotSupportedException)
                    {
                    }
                }
            }
            catch (JsonException)
            {
                // Keep the app usable if the metadata file was interrupted or edited manually.
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void SaveLocked()
    {
        var directory = Path.GetDirectoryName(_metadataPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var document = new ClipLibraryMetadataDocument
        {
            Version = CurrentVersion,
            Items = _items.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.OrdinalIgnoreCase)
        };
        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        var temporaryPath = _metadataPath + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, _metadataPath, overwrite: true);
    }

    private static string NormalizePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A clip path is required.", nameof(filePath));
        return Path.GetFullPath(filePath.Trim());
    }

    private static List<string> NormalizeTags(IEnumerable<string>? tags)
    {
        return tags?
            .Select(tag => (tag ?? string.Empty).Trim())
            .Where(tag => tag.Length > 0)
            .Select(tag => tag.Length > 32 ? tag[..32].Trim() : tag)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Take(12)
            .ToList() ?? [];
    }

    private static string NormalizeCollection(string? collection)
    {
        var normalized = collection?.Trim() ?? string.Empty;
        return normalized.Length > 48 ? normalized[..48].Trim() : normalized;
    }

    private static bool HasData(ClipLibraryMetadataEntry entry) =>
        entry.IsFavorite || entry.Tags.Count > 0 || !string.IsNullOrWhiteSpace(entry.Collection);

    private static ClipLibraryMetadata ToMetadata(ClipLibraryMetadataEntry entry) =>
        new(entry.IsFavorite, entry.Tags.ToList(), entry.Collection);

    private sealed class ClipLibraryMetadataDocument
    {
        public int Version { get; set; } = CurrentVersion;
        public Dictionary<string, ClipLibraryMetadataEntry> Items { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ClipLibraryMetadataEntry
    {
        public bool IsFavorite { get; set; }
        public List<string> Tags { get; set; } = [];
        public string Collection { get; set; } = string.Empty;

        public ClipLibraryMetadataEntry Clone() => new()
        {
            IsFavorite = IsFavorite,
            Tags = Tags.ToList(),
            Collection = Collection
        };
    }
}
