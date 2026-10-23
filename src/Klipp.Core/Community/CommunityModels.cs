using System.Net.Mail;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Klipp.Core.Community;

/// <summary>Public deployment information. A server or service-role key must never be distributed.</summary>
public sealed class CommunityConfiguration
{
    public CommunityConfiguration(string projectUrl = "", string publishableKey = "", string mediaApiUrl = "")
    {
        ProjectUrl = projectUrl.Trim().TrimEnd('/');
        PublishableKey = publishableKey.Trim();
        MediaApiUrl = mediaApiUrl.Trim().TrimEnd('/');
    }

    public string ProjectUrl { get; }
    public string PublishableKey { get; }
    public string MediaApiUrl { get; }
    public bool HasHomeMedia => MediaApiUrl.Length != 0;
    public bool IsConfigured => TryGetBaseUri(out _) && IsPublicKey(PublishableKey);
    public string UnavailableReason => IsConfigured ? string.Empty : "Online Media has not been connected for this build yet.";

    public bool TryGetBaseUri(out Uri? uri)
    {
        if (!Uri.TryCreate(ProjectUrl + "/", UriKind.Absolute, out uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            && uri.AbsolutePath == "/" && !uri.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase);
    }

    public bool TryGetMediaBaseUri(out Uri? uri)
    {
        if (!Uri.TryCreate(MediaApiUrl + "/", UriKind.Absolute, out uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo)
            && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
            && uri.AbsolutePath == "/" && !uri.Host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPublicKey(string key)
    {
        if (key.Length > 16 * 1024 || key.Any(char.IsWhiteSpace)) return false;
        if (key.StartsWith("sb_publishable_", StringComparison.Ordinal))
            return key.Length > "sb_publishable_".Length + 10 && !key.Any(char.IsWhiteSpace)
                && !key.Contains("YOUR_", StringComparison.OrdinalIgnoreCase);

        // Legacy anon JWTs are also public. Reject secret/service-role credentials even in a misconfigured build.
        try
        {
            var parts = key.Split('.');
            if (parts.Length != 3) return false;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            return json.RootElement.TryGetProperty("role", out var role) && role.GetString() == "anon";
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }
}

public sealed record CommunityUser(Guid Id, string Email);

public sealed record CommunityProfile(Guid Id, string? Username, string DisplayName, string Bio)
{
    public bool IsComplete => !string.IsNullOrWhiteSpace(Username);
}

/// <summary>Credentials are deliberately not records: generated ToString must not expose tokens.</summary>
public sealed class CommunitySession
{
    public string AccessToken { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; init; }
    public CommunityUser? User { get; init; }
    public override string ToString() => nameof(CommunitySession);
}

public interface ICommunitySessionStore
{
    Task<CommunitySession?> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(CommunitySession session, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class CommunityAccountException : Exception
{
    public CommunityAccountException(string code, string message) : base(message) => Code = code;
    public string Code { get; }
}

public static partial class CommunityProfileValidation
{
    [GeneratedRegex("^[a-z][a-z0-9_]{2,23}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    public static string NormalizeEmail(string email)
    {
        var value = email.Trim();
        if (value.Length > 254 || !MailAddress.TryCreate(value, out var parsed)
            || parsed.Address != value || string.IsNullOrWhiteSpace(parsed.Host)
            || !parsed.Host.Contains('.'))
            throw new CommunityAccountException("invalid_email", "Enter a valid email address.");
        return value;
    }

    public static (string Username, string DisplayName, string Bio) Validate(string username, string displayName, string bio)
    {
        username = username.Trim().ToLowerInvariant();
        displayName = displayName.Trim();
        bio = bio.Trim();
        if (!UsernamePattern().IsMatch(username))
            throw new CommunityAccountException("invalid_username", "Use 3–24 letters, numbers, or underscores. Start with a letter.");
        if (displayName.Length is < 1 or > 40 || displayName.Any(char.IsControl))
            throw new CommunityAccountException("invalid_display_name", "Enter a display name between 1 and 40 characters.");
        if (bio.Length > 160 || bio.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            throw new CommunityAccountException("invalid_bio", "Keep your bio to 160 characters or fewer.");
        return (username, displayName, bio);
    }
}
