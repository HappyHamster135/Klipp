using System;
using System.Collections.Generic;
using System.IO;

namespace Klipp.Desktop.Models;

public static class EditorTextTypography
{
    public const string Automatic = "auto";

    private static readonly HashSet<string> FontIds = new(StringComparer.OrdinalIgnoreCase)
    {
        Automatic,
        "segoeui",
        "arial",
        "georgia",
        "times",
        "consolas",
        "impact",
        "trebuchet"
    };

    public static string NormalizeFontFamily(string? fontFamily)
    {
        var normalized = fontFamily?.Trim().ToLowerInvariant() ?? Automatic;
        return FontIds.Contains(normalized) ? normalized : Automatic;
    }

    public static string NormalizeColor(string? color)
    {
        var normalized = color?.Trim() ?? Automatic;
        if (string.Equals(normalized, Automatic, StringComparison.OrdinalIgnoreCase))
            return Automatic;
        if (normalized.Length == 6)
            normalized = $"#{normalized}";
        if (normalized.Length != 7 || normalized[0] != '#')
            return Automatic;

        for (var index = 1; index < normalized.Length; index++)
        {
            if (!Uri.IsHexDigit(normalized[index]))
                return Automatic;
        }
        return normalized.ToUpperInvariant();
    }

    public static string NormalizeFontFilePath(string? fontFilePath)
    {
        if (string.IsNullOrWhiteSpace(fontFilePath))
            return string.Empty;
        var extension = Path.GetExtension(fontFilePath);
        if (!string.Equals(extension, ".ttf", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".otf", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }
        try
        {
            return Path.GetFullPath(fontFilePath.Trim());
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string NormalizeCustomFontFamilyName(string? familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName))
            return string.Empty;
        var value = familyName.Trim();
        var cleaned = new char[Math.Min(value.Length, 80)];
        var count = 0;
        foreach (var character in value)
        {
            if (count >= cleaned.Length)
                break;
            if (!char.IsControl(character))
                cleaned[count++] = character;
        }
        return new string(cleaned, 0, count).Trim();
    }

    public static string ResolvePreviewFontFamily(
        string? fontFamily,
        string? style,
        string? fontFilePath = null,
        string? customFamilyName = null)
    {
        var normalizedPath = NormalizeFontFilePath(fontFilePath);
        var normalizedCustomFamily = NormalizeCustomFontFamilyName(customFamilyName);
        if (!string.IsNullOrWhiteSpace(normalizedPath) &&
            !string.IsNullOrWhiteSpace(normalizedCustomFamily) &&
            File.Exists(normalizedPath))
        {
            return $"{new Uri(normalizedPath, UriKind.Absolute).AbsoluteUri}#{normalizedCustomFamily}";
        }

        return NormalizeFontFamily(fontFamily) switch
        {
            "segoeui" => "Segoe UI Variable",
            "arial" => "Arial",
            "georgia" => "Georgia",
            "times" => "Times New Roman",
            "consolas" => "Consolas",
            "impact" => "Impact",
            "trebuchet" => "Trebuchet MS",
            _ => style?.Trim().ToLowerInvariant() switch
            {
                "pixel" or "typewriter" => "Consolas",
                "impact" => "Impact",
                _ => "Segoe UI Variable"
            }
        };
    }

    public static string ResolveColor(string? color, string? style)
    {
        var normalized = NormalizeColor(color);
        if (!string.Equals(normalized, Automatic, StringComparison.Ordinal))
            return normalized;
        return style?.Trim().ToLowerInvariant() switch
        {
            "highlight" => "#000000",
            "pixel" => "#B0FF50",
            "neon" => "#4CEBFF",
            _ => "#FFFFFF"
        };
    }
}
