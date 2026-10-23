using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Klipp.MediaServer;

public sealed record MediaIdentity(Guid UserId, string AccessToken);
public sealed record GatewayClip(Guid Id, Guid OwnerId, string Status, long VideoBytes, double DurationSeconds, JsonElement Json)
{
    public static GatewayClip Parse(JsonElement value)
    {
        try
        {
            var clip = new GatewayClip(value.GetProperty("id").GetGuid(), value.GetProperty("owner_id").GetGuid(),
                value.GetProperty("status").GetString()!, value.GetProperty("byte_size").GetInt64(),
                value.GetProperty("duration_seconds").GetDouble(), value.Clone());
            if (clip.Id == Guid.Empty || clip.OwnerId == Guid.Empty || clip.Status is not ("uploading" or "published" or "deleting")
                || clip.VideoBytes is <= 0 or > MediaOptions.MaximumVideoBytes || !double.IsFinite(clip.DurationSeconds)
                || clip.DurationSeconds <= 0 || clip.DurationSeconds > MediaOptions.MaximumDurationSeconds
                || value.GetProperty("storage_provider").GetString() != "home") throw MediaException.Unavailable();
            return clip;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw MediaException.Unavailable(); }
    }
}

public sealed record MediaEvidence(long VideoBytes, long PosterBytes, double DurationSeconds, string VideoSha256, string PosterSha256);

public interface ICommunityGateway
{
    Task<MediaIdentity> AuthenticateAsync(string token, CancellationToken cancellation);
    Task<GatewayClip> GetClipAsync(MediaIdentity identity, Guid id, CancellationToken cancellation);
    Task<JsonElement> PublishAsync(MediaIdentity identity, Guid id, MediaEvidence evidence, CancellationToken cancellation);
    Task<JsonElement> BeginDeleteAsync(MediaIdentity identity, Guid id, CancellationToken cancellation);
    Task<JsonElement> FinishDeleteAsync(MediaIdentity identity, Guid id, CancellationToken cancellation);
}

/// <summary>Uses the caller's JWT and public key; no service-role credential exists on this host.</summary>
public sealed class SupabaseCommunityGateway(HttpClient http, MediaOptions options) : ICommunityGateway
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

    public Task<JsonElement> PublishAsync(MediaIdentity identity, Guid id, MediaEvidence evidence, CancellationToken cancellation) =>
        RpcAsync("community_home_publish", new { p_clip_id = id, p_video_bytes = evidence.VideoBytes,
            p_poster_bytes = evidence.PosterBytes, p_duration_seconds = evidence.DurationSeconds,
            p_video_sha256 = evidence.VideoSha256, p_poster_sha256 = evidence.PosterSha256 }, identity, true, cancellation);

    public Task<JsonElement> BeginDeleteAsync(MediaIdentity identity, Guid id, CancellationToken cancellation) =>
        RpcAsync("community_remove_clip", new { p_clip_id = id }, identity, false, cancellation);

    public Task<JsonElement> FinishDeleteAsync(MediaIdentity identity, Guid id, CancellationToken cancellation) =>
        RpcAsync("community_home_finish_delete", new { p_clip_id = id }, identity, true, cancellation);

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
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                throw MediaException.Unavailable();
            throw new MediaException(503, "community_unavailable", "Accounts are temporarily unavailable. Try again later.");
        }
        try { return JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 20 }); }
        catch (JsonException) { throw MediaException.StorageUnavailable(); }
    }

    private static MediaException Unauthorized() => new(401, "session_expired", "Sign in again to continue.");
}
