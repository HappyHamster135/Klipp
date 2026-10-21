using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Desktop.Models;

namespace Klipp.Desktop.Services;

public sealed class EditorTextTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Text template";
    public string Style { get; set; } = "plain";
    public string Animation { get; set; } = "none";
    public string FontFamily { get; set; } = EditorTextTypography.Automatic;
    public string FontFilePath { get; set; } = string.Empty;
    public string FontFamilyName { get; set; } = string.Empty;
    public string Color { get; set; } = EditorTextTypography.Automatic;
    public double Size { get; set; } = 64;
    public double BoxWidth { get; set; } = 420;
    public double Opacity { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsBuiltIn { get; set; }

    [JsonIgnore]
    public string KindLabel => IsBuiltIn ? "KLIPP" : "CUSTOM";

    [JsonIgnore]
    public string SampleText => Style switch
    {
        "meme" or "impact" => "TEXT",
        "pixel" => "Pixel",
        "typewriter" => "Type|",
        "caption" => "Caption",
        _ => "Aa"
    };

    [JsonIgnore]
    public string PreviewFontFamily => EditorTextTypography.ResolvePreviewFontFamily(
        FontFamily,
        Style,
        FontFilePath,
        FontFamilyName);

    [JsonIgnore]
    public string PreviewColor => EditorTextTypography.ResolveColor(Color, Style);

    [JsonIgnore]
    public string Summary =>
        $"{DisplayStyle(Style)} · {DisplayFont(FontFamily, FontFamilyName)} · {Math.Round(Size):0}px";

    public EditorTextTemplate Clone() => new()
    {
        Id = Id,
        Name = Name,
        Style = Style,
        Animation = Animation,
        FontFamily = FontFamily,
        FontFilePath = FontFilePath,
        FontFamilyName = FontFamilyName,
        Color = Color,
        Size = Size,
        BoxWidth = BoxWidth,
        Opacity = Opacity,
        UpdatedAt = UpdatedAt,
        IsBuiltIn = IsBuiltIn
    };

    internal void Normalize()
    {
        Id = string.IsNullOrWhiteSpace(Id) ? Guid.NewGuid().ToString("N") : Id.Trim();
        Name = string.IsNullOrWhiteSpace(Name) ? "Text template" : Name.Trim();
        if (Name.Length > 48)
            Name = Name[..48];
        Style = EditorTextTemplateService.NormalizeStyle(Style);
        Animation = EditorTextTemplateService.NormalizeAnimation(Animation);
        FontFamily = EditorTextTypography.NormalizeFontFamily(FontFamily);
        FontFilePath = EditorTextTypography.NormalizeFontFilePath(FontFilePath);
        FontFamilyName = EditorTextTypography.NormalizeCustomFontFamilyName(FontFamilyName);
        Color = EditorTextTypography.NormalizeColor(Color);
        Size = Math.Clamp(Size, 12, 220);
        BoxWidth = Math.Clamp(BoxWidth, 96, 1280);
        Opacity = Math.Clamp(Opacity, 0, 1);
    }

    private static string DisplayStyle(string style) => style switch
    {
        "highlight" => "Highlight",
        "typewriter" => "Typewriter",
        "caption" => "Caption",
        _ => string.IsNullOrWhiteSpace(style)
            ? "Plain"
            : char.ToUpperInvariant(style[0]) + style[1..]
    };

    private static string DisplayAnimation(string animation) => animation switch
    {
        "none" => "Still",
        "slide-up" => "Slide up",
        "slide-left" => "Slide left",
        "type" => "Type",
        _ => string.IsNullOrWhiteSpace(animation)
            ? "Still"
            : char.ToUpperInvariant(animation[0]) + animation[1..]
    };

    private static string DisplayFont(string fontFamily, string customFamilyName)
    {
        var custom = EditorTextTypography.NormalizeCustomFontFamilyName(customFamilyName);
        if (!string.IsNullOrWhiteSpace(custom))
            return custom;
        return EditorTextTypography.NormalizeFontFamily(fontFamily) switch
        {
            "segoeui" => "Segoe UI",
            "arial" => "Arial",
            "georgia" => "Georgia",
            "times" => "Times",
            "consolas" => "Consolas",
            "impact" => "Impact",
            "trebuchet" => "Trebuchet",
            _ => "Preset font"
        };
    }
}

public sealed class EditorTextTemplateService
{
    private sealed class TemplateDocument
    {
        public int Version { get; set; } = 1;
        public List<EditorTextTemplate> Templates { get; set; } = [];
    }

    private const int CurrentVersion = 1;
    private const int MaximumCustomTemplates = 40;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> ValidStyles = new(StringComparer.OrdinalIgnoreCase)
    {
        "plain", "bold", "meme", "highlight", "pixel", "outline",
        "neon", "shadow", "typewriter", "impact", "sticker", "caption"
    };
    private static readonly HashSet<string> ValidAnimations = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "fade", "pop", "slide-up", "slide-left", "type"
    };
    private static readonly IReadOnlyList<EditorTextTemplate> BuiltInTemplates =
    [
        BuiltIn("builtin-clean-title", "Clean title", "bold", "fade", 72, 620, 1),
        BuiltIn("builtin-social-caption", "Social caption", "caption", "pop", 54, 760, 1),
        BuiltIn("builtin-neon-intro", "Neon intro", "neon", "slide-up", 68, 560, 1),
        BuiltIn("builtin-impact-hit", "Impact hit", "impact", "pop", 88, 700, 1),
        BuiltIn("builtin-pixel-quest", "Pixel quest", "pixel", "type", 58, 600, 1),
        BuiltIn("builtin-soft-quote", "Soft quote", "shadow", "fade", 52, 720, 0.92)
    ];

    private readonly string _templatePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<EditorTextTemplate> _customTemplates = [];
    private bool _loaded;

    public EditorTextTemplateService(string? templatePath = null)
    {
        if (!string.IsNullOrWhiteSpace(templatePath))
        {
            _templatePath = Path.GetFullPath(templatePath);
        }
        else
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Klipp");
            _templatePath = Path.Combine(directory, "editor-text-templates.json");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_templatePath)!);
    }

    public async Task<IReadOnlyList<EditorTextTemplate>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            return BuiltInTemplates
                .Select(template => template.Clone())
                .Concat(_customTemplates
                    .OrderByDescending(template => template.UpdatedAt)
                    .ThenBy(template => template.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(template => template.Clone()))
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<EditorTextTemplate> SaveCustomAsync(
        string name,
        string style,
        string animation,
        double size,
        double boxWidth,
        double opacity,
        string? fontFamily = null,
        string? color = null,
        string? fontFilePath = null,
        string? fontFamilyName = null,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var normalizedName = string.IsNullOrWhiteSpace(name) ? "Text template" : name.Trim();
            if (normalizedName.Length > 48)
                normalizedName = normalizedName[..48];
            var template = _customTemplates.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, normalizedName, StringComparison.CurrentCultureIgnoreCase));
            if (template is null)
            {
                if (_customTemplates.Count >= MaximumCustomTemplates)
                    throw new InvalidOperationException($"You can save up to {MaximumCustomTemplates} custom text templates.");
                template = new EditorTextTemplate();
                _customTemplates.Add(template);
            }

            template.Name = normalizedName;
            template.Style = style;
            template.Animation = animation;
            template.FontFamily = fontFamily ?? EditorTextTypography.Automatic;
            template.FontFilePath = fontFilePath ?? string.Empty;
            template.FontFamilyName = fontFamilyName ?? string.Empty;
            template.Color = color ?? EditorTextTypography.Automatic;
            template.Size = size;
            template.BoxWidth = boxWidth;
            template.Opacity = opacity;
            template.UpdatedAt = DateTimeOffset.UtcNow;
            template.IsBuiltIn = false;
            template.Normalize();
            await SaveDocumentAsync(cancellationToken).ConfigureAwait(false);
            return template.Clone();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteCustomAsync(
        string templateId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            var removed = _customTemplates.RemoveAll(template =>
                string.Equals(template.Id, templateId, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
                await SaveDocumentAsync(cancellationToken).ConfigureAwait(false);
            return removed;
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string NormalizeStyle(string? style)
    {
        var normalized = style?.Trim().ToLowerInvariant() ?? "plain";
        return ValidStyles.Contains(normalized) ? normalized : "plain";
    }

    public static string NormalizeAnimation(string? animation)
    {
        var normalized = animation?.Trim().ToLowerInvariant() ?? "none";
        return ValidAnimations.Contains(normalized) ? normalized : "none";
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
            return;

        _loaded = true;
        if (!File.Exists(_templatePath))
            return;

        try
        {
            await using var stream = new FileStream(
                _templatePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: true);
            var document = await JsonSerializer.DeserializeAsync<TemplateDocument>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            _customTemplates = document?.Templates ?? [];
            foreach (var template in _customTemplates)
            {
                template.IsBuiltIn = false;
                template.Normalize();
            }
            _customTemplates = _customTemplates
                .GroupBy(template => template.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .Take(MaximumCustomTemplates)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            _loaded = false;
            throw;
        }
        catch
        {
            _customTemplates = [];
        }
    }

    private async Task SaveDocumentAsync(CancellationToken cancellationToken)
    {
        var temporaryPath = _templatePath + ".tmp";
        try
        {
            var document = new TemplateDocument
            {
                Version = CurrentVersion,
                Templates = _customTemplates.Select(template => template.Clone()).ToList()
            };
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _templatePath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }

    private static EditorTextTemplate BuiltIn(
        string id,
        string name,
        string style,
        string animation,
        double size,
        double boxWidth,
        double opacity) => new()
    {
        Id = id,
        Name = name,
        Style = style,
        Animation = animation,
        Size = size,
        BoxWidth = boxWidth,
        Opacity = opacity,
        IsBuiltIn = true
    };
}
