using System;
using System.IO;
using System.Text.Json;
using Klipp.Core.Community;

namespace Klipp.Desktop.Services;

/// <summary>Loads public deployment settings only. Never accepts an administrative Supabase key.</summary>
public static class CommunityConfigurationService
{
    public static CommunityConfiguration Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "community.json");
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024) return new();
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            return new CommunityConfiguration(
                root.TryGetProperty("projectUrl", out var url) ? url.GetString() ?? "" : "",
                root.TryGetProperty("publishableKey", out var key) ? key.GetString() ?? "" : "",
                root.TryGetProperty("mediaApiUrl", out var mediaUrl) ? mediaUrl.GetString() ?? "" : "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            // Invalid optional online configuration must never prevent the local recorder from opening.
            return new();
        }
    }
}
