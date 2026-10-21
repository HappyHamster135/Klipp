using System.Text.Json;
using System.Text.Json.Serialization;

namespace Klipp.Core.Branding;

public sealed class EditorBrandLogo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Logo";
    public string StoredPath { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsMissing => !string.IsNullOrWhiteSpace(StoredPath) && !File.Exists(StoredPath);

    public EditorBrandLogo Clone() => new()
    {
        Id = Id,
        Name = Name,
        StoredPath = StoredPath,
        SourceFileName = SourceFileName,
        UpdatedAt = UpdatedAt
    };

    internal void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        Name = EditorBrandKitService.NormalizeName(Name, "Logo", 48);
        StoredPath = StoredPath?.Trim() ?? string.Empty;
        SourceFileName = string.IsNullOrWhiteSpace(SourceFileName)
            ? Path.GetFileName(StoredPath)
            : Path.GetFileName(SourceFileName.Trim());
    }
}

public sealed class EditorBrandFont
{
    public string StoredPath { get; set; } = string.Empty;
    public string FamilyName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string SourceFileName { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsMissing => !string.IsNullOrWhiteSpace(StoredPath) && !File.Exists(StoredPath);

    public EditorBrandFont Clone() => new()
    {
        StoredPath = StoredPath,
        FamilyName = FamilyName,
        FullName = FullName,
        SourceFileName = SourceFileName,
        UpdatedAt = UpdatedAt
    };

    internal void Normalize()
    {
        StoredPath = StoredPath?.Trim() ?? string.Empty;
        FamilyName = EditorBrandKitService.NormalizeName(FamilyName, "Imported font", 80);
        FullName = EditorBrandKitService.NormalizeName(FullName, FamilyName, 100);
        SourceFileName = string.IsNullOrWhiteSpace(SourceFileName)
            ? Path.GetFileName(StoredPath)
            : Path.GetFileName(SourceFileName.Trim());
    }
}

public sealed class EditorBrandKit
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Brand kit";
    public string FontFamily { get; set; } = "segoeui";
    public string PrimaryColor { get; set; } = "#A970FF";
    public string SecondaryColor { get; set; } = "#4CEBFF";
    public string AccentColor { get; set; } = "#B0FF50";
    public string TextColor { get; set; } = "#FFFFFF";
    public EditorBrandFont? FontAsset { get; set; }
    public List<EditorBrandLogo> Logos { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public string LogoCountLabel => Logos.Count == 1 ? "1 logo" : $"{Logos.Count} logos";

    [JsonIgnore]
    public string FontDisplayName => FontAsset?.FamilyName ?? EditorBrandKitService.DisplayFontFamily(FontFamily);

    public EditorBrandKit Clone() => new()
    {
        Id = Id,
        Name = Name,
        FontFamily = FontFamily,
        PrimaryColor = PrimaryColor,
        SecondaryColor = SecondaryColor,
        AccentColor = AccentColor,
        TextColor = TextColor,
        FontAsset = FontAsset?.Clone(),
        Logos = Logos.Select(logo => logo.Clone()).ToList(),
        UpdatedAt = UpdatedAt
    };

    internal void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        Name = EditorBrandKitService.NormalizeName(Name, "Brand kit", 48);
        FontFamily = EditorBrandKitService.NormalizeFontFamily(FontFamily);
        PrimaryColor = EditorBrandKitService.NormalizeColor(PrimaryColor, "#A970FF");
        SecondaryColor = EditorBrandKitService.NormalizeColor(SecondaryColor, "#4CEBFF");
        AccentColor = EditorBrandKitService.NormalizeColor(AccentColor, "#B0FF50");
        TextColor = EditorBrandKitService.NormalizeColor(TextColor, "#FFFFFF");
        FontAsset?.Normalize();
        if (FontAsset is { StoredPath.Length: 0 })
            FontAsset = null;
        Logos ??= [];
        foreach (var logo in Logos)
            logo.Normalize();
        Logos = Logos
            .GroupBy(logo => logo.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderByDescending(logo => logo.UpdatedAt)
            .Take(EditorBrandKitService.MaximumLogosPerKit)
            .ToList();
    }
}

public sealed class EditorBrandKitService
{
    private sealed class BrandKitDocument
    {
        public int Version { get; set; } = CurrentVersion;
        public List<EditorBrandKit> Kits { get; set; } = [];
    }

    private const int CurrentVersion = 2;
    public const int MaximumKits = 12;
    public const int MaximumLogosPerKit = 24;
    private const long MaximumLogoBytes = 32L * 1024 * 1024;
    private const long MaximumFontBytes = 20L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> SupportedLogoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".webp", ".bmp"
    };
    private static readonly HashSet<string> SupportedFontExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ttf", ".otf"
    };
    private static readonly HashSet<string> SupportedFontFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "segoeui", "arial", "georgia", "times", "consolas", "impact", "trebuchet"
    };

    private readonly string _storagePath;
    private readonly string _assetsRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<EditorBrandKit> _kits = [];
    private bool _loaded;

    public EditorBrandKitService(string? storagePath = null, string? assetsRoot = null)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            var appDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Klipp");
            _storagePath = Path.Combine(appDirectory, "editor-brand-kits.json");
            _assetsRoot = Path.Combine(appDirectory, "brand-assets");
        }
        else
        {
            _storagePath = Path.GetFullPath(storagePath);
            _assetsRoot = string.IsNullOrWhiteSpace(assetsRoot)
                ? Path.Combine(Path.GetDirectoryName(_storagePath)!, "brand-assets")
                : Path.GetFullPath(assetsRoot);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_storagePath)!);
        Directory.CreateDirectory(_assetsRoot);
    }

    public async Task<IReadOnlyList<EditorBrandKit>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return _kits
                .OrderByDescending(kit => kit.UpdatedAt)
                .ThenBy(kit => kit.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(kit => kit.Clone())
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditorBrandKit> SaveAsync(
        EditorBrandKit draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var normalized = draft.Clone();
            normalized.Normalize();
            var duplicateName = _kits.Any(kit =>
                !string.Equals(kit.Id, normalized.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(kit.Name, normalized.Name, StringComparison.CurrentCultureIgnoreCase));
            if (duplicateName)
                throw new InvalidOperationException($"A brand kit named '{normalized.Name}' already exists.");

            var index = _kits.FindIndex(kit =>
                string.Equals(kit.Id, normalized.Id, StringComparison.OrdinalIgnoreCase));
            normalized.UpdatedAt = DateTimeOffset.UtcNow;
            if (index >= 0)
            {
                _kits[index] = normalized;
            }
            else
            {
                if (_kits.Count >= MaximumKits)
                    throw new InvalidOperationException($"You can save up to {MaximumKits} brand kits.");
                _kits.Add(normalized);
            }

            await SaveDocumentAsync(cancellationToken).ConfigureAwait(false);
            return normalized.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string kitId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var removed = _kits.RemoveAll(kit =>
                string.Equals(kit.Id, kitId, StringComparison.OrdinalIgnoreCase)) > 0;
            if (!removed)
                return false;

            if (_kits.Count == 0)
                _kits.Add(CreateDefaultKit());
            await SaveDocumentAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditorBrandLogo> ImportLogoAsync(
        string kitId,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("The selected logo could not be found.", sourcePath);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!SupportedLogoExtensions.Contains(extension))
            throw new InvalidOperationException("Choose a PNG, JPG, WebP or BMP image.");
        if (new FileInfo(sourcePath).Length > MaximumLogoBytes)
            throw new InvalidOperationException("Logo files must be 32 MB or smaller.");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var kit = _kits.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, kitId, StringComparison.OrdinalIgnoreCase)) ??
                throw new InvalidOperationException("Select a brand kit before importing a logo.");
            if (kit.Logos.Count >= MaximumLogosPerKit)
                throw new InvalidOperationException($"Each brand kit can contain up to {MaximumLogosPerKit} logos.");

            var logoId = Guid.NewGuid().ToString("N");
            var kitDirectory = Path.Combine(_assetsRoot, kit.Id);
            Directory.CreateDirectory(kitDirectory);
            var destinationPath = Path.Combine(kitDirectory, logoId + extension);
            var temporaryPath = destinationPath + ".tmp";
            try
            {
                await using (var source = new FileStream(
                                 sourcePath,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.ReadWrite | FileShare.Delete,
                                 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var destination = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                File.Move(temporaryPath, destinationPath);
            }
            finally
            {
                try { File.Delete(temporaryPath); } catch { }
            }

            var logo = new EditorBrandLogo
            {
                Id = logoId,
                Name = NormalizeName(Path.GetFileNameWithoutExtension(sourcePath), "Logo", 48),
                StoredPath = destinationPath,
                SourceFileName = Path.GetFileName(sourcePath),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            kit.Logos.Insert(0, logo);
            kit.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveDocumentAsync(cancellationToken).ConfigureAwait(false);
            return logo.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveLogoAsync(
        string kitId,
        string logoId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var kit = _kits.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, kitId, StringComparison.OrdinalIgnoreCase));
            if (kit is null)
                return false;
            var removed = kit.Logos.RemoveAll(logo =>
                string.Equals(logo.Id, logoId, StringComparison.OrdinalIgnoreCase)) > 0;
            if (!removed)
                return false;

            // Keep the stored file: projects may already reference this stable asset path.
            kit.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveDocumentAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditorBrandFont> ImportFontAsync(
        string kitId,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("The selected font could not be found.", sourcePath);
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!SupportedFontExtensions.Contains(extension))
            throw new InvalidOperationException("Choose an individual TTF or OTF font file.");
        if (new FileInfo(sourcePath).Length > MaximumFontBytes)
            throw new InvalidOperationException("Font files must be 20 MB or smaller.");
        var fontInfo = await OpenTypeFontInspector.InspectAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var kit = _kits.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, kitId, StringComparison.OrdinalIgnoreCase)) ??
                throw new InvalidOperationException("Select a brand kit before importing a font.");
            var kitDirectory = Path.Combine(_assetsRoot, kit.Id);
            Directory.CreateDirectory(kitDirectory);
            var destinationPath = Path.Combine(kitDirectory, $"font-{Guid.NewGuid():N}{extension}");
            var temporaryPath = destinationPath + ".tmp";
            try
            {
                await using (var source = new FileStream(
                                 sourcePath,
                                 FileMode.Open,
                                 FileAccess.Read,
                                 FileShare.ReadWrite | FileShare.Delete,
                                 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                await using (var destination = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                File.Move(temporaryPath, destinationPath);
            }
            finally
            {
                try { File.Delete(temporaryPath); } catch { }
            }

            var font = new EditorBrandFont
            {
                StoredPath = destinationPath,
                FamilyName = fontInfo.FamilyName,
                FullName = fontInfo.FullName,
                SourceFileName = Path.GetFileName(sourcePath),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            kit.FontAsset = font;
            kit.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveDocumentAsync(cancellationToken).ConfigureAwait(false);
            return font.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RemoveFontAsync(string kitId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var kit = _kits.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, kitId, StringComparison.OrdinalIgnoreCase));
            if (kit?.FontAsset is null)
                return false;

            // Keep the stored file: projects may already reference this stable asset path.
            kit.FontAsset = null;
            kit.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveDocumentAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string NormalizeFontFamily(string? fontFamily)
    {
        var normalized = fontFamily?.Trim().ToLowerInvariant() ?? "segoeui";
        return SupportedFontFamilies.Contains(normalized) ? normalized : "segoeui";
    }

    public static string DisplayFontFamily(string? fontFamily) => NormalizeFontFamily(fontFamily) switch
    {
        "segoeui" => "Segoe UI",
        "arial" => "Arial",
        "georgia" => "Georgia",
        "times" => "Times New Roman",
        "consolas" => "Consolas",
        "impact" => "Impact",
        "trebuchet" => "Trebuchet MS",
        _ => "Segoe UI"
    };

    public static bool TryNormalizeColor(string? color, out string normalized)
    {
        normalized = color?.Trim() ?? string.Empty;
        if (normalized.Length == 6)
            normalized = "#" + normalized;
        if (normalized.Length != 7 || normalized[0] != '#')
            return false;
        for (var index = 1; index < normalized.Length; index++)
        {
            if (!Uri.IsHexDigit(normalized[index]))
                return false;
        }
        normalized = normalized.ToUpperInvariant();
        return true;
    }

    internal static string NormalizeColor(string? color, string fallback) =>
        TryNormalizeColor(color, out var normalized) ? normalized : fallback;

    internal static string NormalizeName(string? name, string fallback, int maximumLength)
    {
        var normalized = string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
        return normalized.Length > maximumLength ? normalized[..maximumLength] : normalized;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
            return;

        _loaded = true;
        if (!File.Exists(_storagePath))
        {
            _kits = [CreateDefaultKit()];
            await SaveDocumentAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await using var stream = new FileStream(
                _storagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var document = await JsonSerializer.DeserializeAsync<BrandKitDocument>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            _kits = document?.Kits ?? [];
            foreach (var kit in _kits)
                kit.Normalize();
            _kits = _kits
                .GroupBy(kit => kit.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .Take(MaximumKits)
                .ToList();
            if (_kits.Count == 0)
                _kits.Add(CreateDefaultKit());
        }
        catch (OperationCanceledException)
        {
            _loaded = false;
            throw;
        }
        catch
        {
            _kits = [CreateDefaultKit()];
        }
    }

    private async Task SaveDocumentAsync(CancellationToken cancellationToken)
    {
        var temporaryPath = _storagePath + ".tmp";
        try
        {
            var document = new BrandKitDocument
            {
                Kits = _kits.Select(kit => kit.Clone()).ToList()
            };
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, _storagePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static EditorBrandKit CreateDefaultKit() => new()
    {
        Name = "Klipp default",
        FontFamily = "segoeui",
        PrimaryColor = "#A970FF",
        SecondaryColor = "#4CEBFF",
        AccentColor = "#B0FF50",
        TextColor = "#FFFFFF"
    };
}
