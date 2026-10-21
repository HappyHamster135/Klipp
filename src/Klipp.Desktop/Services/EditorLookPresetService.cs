using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Desktop.Models;

namespace Klipp.Desktop.Services;

public sealed class EditorLookPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "My look";
    public EditorProjectClip Style { get; set; } = new();
    public override string ToString() => Name;
}

public sealed class EditorLookPresetService
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public EditorLookPresetService(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klipp", "look-presets.json"));
        _gate = Gates.GetOrAdd(_path, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<IReadOnlyList<EditorLookPreset>> LoadAsync()
    {
        await _gate.WaitAsync();
        try { return await ReadAsync(); }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(string name, EditorProjectClip clip)
    {
        name = name.Trim();
        if (name.Length == 0 || name.Length > 64)
            throw new ArgumentException("Use a preset name between 1 and 64 characters.", nameof(name));
        await _gate.WaitAsync();
        try
        {
            var presets = await ReadAsync();
            var preset = presets.FirstOrDefault(item => item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (preset is null)
            {
                preset = new EditorLookPreset { Name = name };
                presets.Add(preset);
            }
            preset.Style = new EditorProjectClip();
            EditorClipAttributesService.CopyLook(clip, preset.Style);
            await WriteAsync(presets);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string id)
    {
        await _gate.WaitAsync();
        try
        {
            var presets = await ReadAsync();
            presets.RemoveAll(preset => preset.Id == id);
            await WriteAsync(presets);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<EditorLookPreset>> ReadAsync()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            await using var stream = File.OpenRead(_path);
            var presets = await JsonSerializer.DeserializeAsync<List<EditorLookPreset>>(stream, Options);
            if (presets is null || presets.Any(preset => preset is null || string.IsNullOrWhiteSpace(preset.Name) || preset.Style is null))
                throw new InvalidDataException("Saved looks contain invalid data. The existing file has been preserved.");
            foreach (var preset in presets) preset.Style.Normalize();
            return presets.OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (JsonException exception) { throw new InvalidDataException("Saved looks could not be read. The existing file has been preserved.", exception); }
    }

    private async Task WriteAsync(List<EditorLookPreset> presets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(presets, Options));
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
