using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Klipp.MediaServer;

public sealed record MediaIdentity(Guid UserId, string AccessToken);
public sealed record GatewayClip(Guid Id, Guid OwnerId, string Status, long VideoBytes, double DurationSeconds, JsonElement Json, DateTimeOffset? UploadExpiresAt, bool ModerationHidden = false)
{
    public static GatewayClip Parse(JsonElement value)
    {
        try
        {
            var clip = new GatewayClip(value.GetProperty("id").GetGuid(), value.GetProperty("owner_id").GetGuid(),
                value.GetProperty("status").GetString()!, value.GetProperty("byte_size").GetInt64(),
                value.GetProperty("duration_seconds").GetDouble(), value.Clone(),
                value.TryGetProperty("upload_expires_at", out var expiry) && expiry.ValueKind == JsonValueKind.String ? expiry.GetDateTimeOffset() : null,
                value.TryGetProperty("moderation_hidden", out var hidden) && hidden.GetBoolean());
            if (clip.Id == Guid.Empty || clip.OwnerId == Guid.Empty || clip.Status is not ("uploading" or "published" or "deleting")
                || clip.VideoBytes is <= 0 or > MediaOptions.MaximumVideoBytes || !double.IsFinite(clip.DurationSeconds)
                || clip.DurationSeconds <= 0 || clip.DurationSeconds > MediaOptions.MaximumDurationSeconds
                || value.GetProperty("storage_provider").GetString() != "home"
                || clip.Status == "uploading" && clip.UploadExpiresAt is null) throw MediaException.Unavailable();
            return clip;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw MediaException.Unavailable(); }
    }
}

public sealed record MediaEvidence(long VideoBytes, long PosterBytes, double DurationSeconds, string VideoSha256, string PosterSha256);
public sealed record ExpiredUpload(Guid Id, Guid OwnerId);

public interface IExpiryGateway
{
    Task<IReadOnlyList<ExpiredUpload>> ClaimExpiredAsync(CancellationToken cancellation);
    Task FinishExpiredAsync(Guid id, CancellationToken cancellation);
}

public interface ICommunityGateway
{
    Task<bool> IsAssetAllowedAsync(Guid id, Guid owner, CancellationToken cancellation);
    Task<MediaIdentity> AuthenticateAsync(string token, CancellationToken cancellation);
    Task<GatewayClip> GetClipAsync(MediaIdentity identity, Guid id, CancellationToken cancellation);
    Task<JsonElement> PublishAsync(MediaIdentity identity, Guid id, MediaEvidence evidence, CancellationToken cancellation);
    Task<JsonElement> BeginDeleteAsync(MediaIdentity identity, Guid id, CancellationToken cancellation);
    Task<JsonElement> FinishDeleteAsync(MediaIdentity identity, Guid id, CancellationToken cancellation);
}

/// <summary>Uses the caller's JWT and public key; no service-role credential exists on this host.</summary>
public sealed class SupabaseCommunityGateway(HttpClient http, MediaOptions options) : ICommunityGateway, IExpiryGateway
{
    public async Task<MediaIdentity> AuthenticateAsync(string token, CancellationToken cancellation)
    {
        using var request = Request(HttpMethod.Get, "auth/v1/user", token);
        using var response = await SendAsync(request, cancellation);
        using var document = await ReadAsync(response, cancellation, authentication: true);
        try
        {
            var user = document.RootElement;
            var id = user.GetProperty("id").GetGuid();
            if (id == Guid.Empty || !user.TryGetProperty("email_confirmed_at", out var confirmed)
                || confirmed.ValueKind != JsonValueKind.String || !DateTimeOffset.TryParse(confirmed.GetString(), out _)
                || user.TryGetProperty("is_anonymous", out var anonymous) && anonymous.ValueKind == JsonValueKind.True)
                throw Unauthorized();
            return new MediaIdentity(id, token);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw Unauthorized(); }
    }

    public async Task<GatewayClip> GetClipAsync(MediaIdentity identity, Guid id, CancellationToken cancellation) =>
        GatewayClip.Parse(await RpcAsync("community_get_clip", new { p_clip_id = id }, identity, false, cancellation));

    public async Task<bool> IsAssetAllowedAsync(Guid id, Guid owner, CancellationToken cancellation)
    {
        var result = await MaintenanceRpcAsync("community_home_asset_allowed", new { p_clip_id = id, p_owner_id = owner }, cancellation);
        if (!result.TryGetProperty("allowed", out var allowed) || allowed.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw MediaException.StorageUnavailable();
        return allowed.GetBoolean();
    }

    public Task<JsonElement> PublishAsync(MediaIdentity identity, Guid id, MediaEvidence evidence, CancellationToken cancellation) =>
        RpcAsync("community_home_publish", new { p_clip_id = id, p_video_bytes = evidence.VideoBytes,
            p_poster_bytes = evidence.PosterBytes, p_duration_seconds = evidence.DurationSeconds,
            p_video_sha256 = evidence.VideoSha256, p_poster_sha256 = evidence.PosterSha256 }, identity, true, cancellation);

    public Task<JsonElement> BeginDeleteAsync(MediaIdentity identity, Guid id, CancellationToken cancellation) =>
        RpcAsync("community_remove_clip", new { p_clip_id = id }, identity, false, cancellation);

    public Task<JsonElement> FinishDeleteAsync(MediaIdentity identity, Guid id, CancellationToken cancellation) =>
        RpcAsync("community_home_finish_delete", new { p_clip_id = id }, identity, true, cancellation);

    public async Task<IReadOnlyList<ExpiredUpload>> ClaimExpiredAsync(CancellationToken cancellation)
    {
        var result = await MaintenanceRpcAsync("community_home_claim_expired_uploads", new { p_limit = 10 }, cancellation);
        if (result.ValueKind != JsonValueKind.Array || result.GetArrayLength() > 10) throw MediaException.StorageUnavailable();
        var claims = new List<ExpiredUpload>();
        foreach (var item in result.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var id) || !id.TryGetGuid(out var clip) || clip == Guid.Empty
                || !item.TryGetProperty("owner_id", out var owner) || !owner.TryGetGuid(out var user) || user == Guid.Empty
                || claims.Any(value => value.Id == clip)) throw MediaException.StorageUnavailable();
            claims.Add(new ExpiredUpload(clip, user));
        }
        return claims;
    }

    public async Task FinishExpiredAsync(Guid id, CancellationToken cancellation)
    {
        var result = await MaintenanceRpcAsync("community_home_finish_expired_upload", new { p_clip_id = id }, cancellation);
        if (!result.TryGetProperty("deleted", out var deleted) || deleted.ValueKind != JsonValueKind.True)
            throw MediaException.StorageUnavailable();
    }

    private async Task<JsonElement> MaintenanceRpcAsync(string name, object body, CancellationToken cancellation)
    {
        // Scoped maintenance/access RPCs use only the gateway proof, never a privileged Supabase role.
        using var request = new HttpRequestMessage(HttpMethod.Post, options.SupabaseProjectUrl.TrimEnd('/') + "/rest/v1/rpc/" + name);
        request.Headers.Add("apikey", options.SupabasePublishableKey);
        request.Headers.Add("x-klipp-media-secret", options.GatewaySecret);
        request.Content = JsonContent.Create(body);
        using var response = await SendAsync(request, cancellation);
        using var result = await ReadAsync(response, cancellation, authentication: false);
        return result.RootElement.Clone();
    }

    private async Task<JsonElement> RpcAsync(string name, object body, MediaIdentity identity, bool attest, CancellationToken cancellation)
    {
        using var request = Request(HttpMethod.Post, "rest/v1/rpc/" + name, identity.AccessToken);
        if (attest) request.Headers.Add("x-klipp-media-secret", options.GatewaySecret);
        request.Content = JsonContent.Create(body);
        using var response = await SendAsync(request, cancellation);
        using var result = await ReadAsync(response, cancellation, authentication: false);
        return result.RootElement.Clone();
    }

    private HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, options.SupabaseProjectUrl.TrimEnd('/') + "/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("apikey", options.SupabasePublishableKey);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        try { return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation); }
        catch (HttpRequestException) { throw new MediaException(503, "community_unavailable", "Accounts are temporarily unavailable. Try again later."); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new MediaException(503, "community_unavailable", "Accounts are temporarily unavailable. Try again later."); }
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken token, bool authentication)
    {
        if (response.Content.Headers.ContentLength > 64 * 1024) throw MediaException.StorageUnavailable();
        await using var input = await response.Content.ReadAsStreamAsync(token);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + read > 64 * 1024) throw MediaException.StorageUnavailable();
            output.Write(buffer, 0, read);
        }
        if (!response.IsSuccessStatusCode)
        {
            if (authentication || response.StatusCode == HttpStatusCode.Unauthorized) throw Unauthorized();
            // Database messages, request headers, paths and credentials never reach callers.
            string? code = null;
            try
            {
                using var error = JsonDocument.Parse(output.ToArray());
                if (error.RootElement.TryGetProperty("code", out var value) && value.ValueKind == JsonValueKind.String) code = value.GetString();
            }
            catch (JsonException) { }
            if (code == "KM001") throw Unauthorized();
            if (code == "KM002") throw new MediaException(409, "profile_required", "Choose a username before uploading clips.");
            if (code is "KM004" or "KM005") throw new MediaException(409, "upload_capacity", "The available upload slots are in use. Remove an old upload and try again.");
            if (code == "KM006") throw new MediaException(400, "invalid_media", "The uploaded files do not match the reservation.");
            if (code == "KM010") throw new MediaException(409, "cleanup_required", "This clip still needs cleanup. Try again shortly.");
            if (code == "KM014") throw new MediaException(403, "desktop_upload_required", "Upload clips from Klipp on your PC.");
            if (code == "KM015") throw MediaException.StorageUnavailable();
            if (code == "KM016") throw new MediaException(503, "uploads_paused", "Uploads are temporarily paused. Try again later.");
            if (code == "KM017") throw new MediaException(410, "upload_expired", "This unfinished upload has expired. Start a new upload.");
            if (code == "KM018") throw new MediaException(409, "desktop_update_required", "Update Klipp on your PC before uploading.");
            if (code == "KM019") throw new MediaException(409, "pending_upload_limit", "Finish or remove an unfinished upload before starting another.");
            if (code == "KM008") throw new MediaException(429, "upload_rate_limited", "Too many upload attempts. Try again later.");
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                throw MediaException.Unavailable();
            throw new MediaException(503, "community_unavailable", "Accounts are temporarily unavailable. Try again later.");
        }
        try { return JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 20 }); }
        catch (JsonException) { throw MediaException.StorageUnavailable(); }
    }

    private static MediaException Unauthorized() => new(401, "session_expired", "Sign in again to continue.");
}
