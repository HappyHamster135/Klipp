using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Klipp.Core.Community;

public sealed partial class CommunityAccountService
{
    private Uri RequireHomeMediaUri() => _configuration.TryGetMediaBaseUri(out var uri) ? uri!
        : throw new CommunityAccountException("media_not_configured", "Clip hosting has not been connected for this build. Please update Klipp.");

    private async Task<JsonDocument> HomeMediaJsonAsync(HttpMethod method, string path, object? body,
        int generation, CancellationToken token)
    {
        EnsureCurrent(generation);
        var baseUri = RequireHomeMediaUri();
        var session = await EnsureSessionAsync(generation, token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(baseUri, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var bytes = await ReadBoundedAsync(response.Content, deadline.Token, MaximumMediaResponseBytes);
            EnsureCurrent(generation);
            if (!response.IsSuccessStatusCode) throw HomeMediaError(response.StatusCode, bytes);
            deadline.Token.ThrowIfCancellationRequested();
            try { return JsonDocument.Parse(bytes.Length == 0 ? "{}"u8.ToArray() : bytes); }
            catch (JsonException) { throw Unavailable(); }
        }
        catch (CommunityAccountException ex) when (ex.Code == "session_expired")
        { EnsureCurrent(generation); await ClearSessionAsync(token); throw; }
        catch (HttpRequestException) { throw HomeMediaOffline(); }
        catch (IOException) { throw HomeMediaOffline(); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CommunityAccountException("timeout", "Clip hosting took too long to respond. Please try again."); }
    }

    private async Task UploadHomeAssetAsync(Guid clipId, bool poster, Stream source, string mimeType,
        int generation, CancellationToken token, Action<long> progress)
    {
        EnsureCurrent(generation);
        var baseUri = RequireHomeMediaUri();
        var session = await EnsureSessionAsync(generation, token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put,
                new Uri(baseUri, $"v1/clips/{clipId:D}/{(poster ? "poster" : "video")}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Content = new UploadContent(source, mimeType, progress);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            var bytes = await ReadBoundedAsync(response.Content, token);
            EnsureCurrent(generation);
            if (!response.IsSuccessStatusCode) throw HomeMediaError(response.StatusCode, bytes);
            token.ThrowIfCancellationRequested();
        }
        catch (CommunityAccountException ex) when (ex.Code == "session_expired")
        { EnsureCurrent(generation); await ClearSessionAsync(token); throw; }
        catch (HttpRequestException) { throw HomeMediaOffline(); }
        catch (IOException) { throw HomeMediaOffline(); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CommunityAccountException("timeout", "The upload took too long. Try again on a steadier connection."); }
    }

    private Uri ParseHomeAssetUri(JsonElement row, Guid ownerId, Guid clipId, bool poster)
    {
        var origin = RequireHomeMediaUri();
        var value = ReadText(row, poster ? "poster_url" : "video_url", 4096);
        var now = _time.GetUtcNow();
        var responseExpiry = ReadTime(row, "expires_at");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != origin.Host || uri.Port != origin.Port
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != $"/v1/assets/{ownerId:D}/{clipId:D}/{(poster ? "poster" : "video") }"
            || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            || responseExpiry <= now || responseExpiry > now.AddMinutes(6)) throw Unavailable();
        var pieces = uri.Query.TrimStart('?').Split('&');
        if (pieces.Length != 2) throw Unavailable();
        var expires = pieces.FirstOrDefault(p => p.StartsWith("expires=", StringComparison.Ordinal));
        var signature = pieces.FirstOrDefault(p => p.StartsWith("signature=", StringComparison.Ordinal));
        if (expires is null || signature is null || !long.TryParse(expires.AsSpan(8), NumberStyles.None,
                CultureInfo.InvariantCulture, out var expiry)
            || expiry <= now.ToUnixTimeSeconds() || expiry > now.AddMinutes(6).ToUnixTimeSeconds()
            || signature.Length != 74 || !signature.AsSpan(10).ToString().All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw Unavailable();
        return uri;
    }

    private static CommunityAccountException HomeMediaOffline() =>
        new("offline", "Clip hosting is temporarily unavailable. Please try again shortly.");

    private static CommunityAccountException HomeMediaError(HttpStatusCode status, byte[] bytes)
    {
        string? code = null;
        try
        {
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("code", out var value)
                && value.ValueKind == JsonValueKind.String) code = value.GetString();
        }
        catch (JsonException) { }
        var known = code switch
        {
            "uploads_paused" => new CommunityAccountException("hosting_unavailable", "Uploads are temporarily paused. Please try again later."),
            "backup_required" => new CommunityAccountException("hosting_unavailable", "Uploads are temporarily paused until the server backup and recovery checks pass. Please try again later."),
            "upload_expired" => new("upload_expired", "This unfinished upload expired. Choose the clip again to start a new upload."),
            "server_busy" or "playback_busy" => new("rate_limited", "Clip hosting is busy. Wait a moment and try again."),
            "upload_capacity" => new("upload_limit", "The available upload slots are in use. Remove a shared clip and try again."),
            "profile_required" => new("profile_required", "Choose a username in your profile before sharing clips."),
            "upload_incomplete" or "upload_closed" => new("upload_not_ready", "The upload did not finish. Please upload the clip again."),
            "invalid_media" or "invalid_size" or "invalid_duration" or "length_required" => new("invalid_media", "The clip could not be validated. Review the clip and try uploading it again."),
            "desktop_upload_required" => new("desktop_upload_required", "Upload clips from Klipp on your PC."),
            "cleanup_required" => new("cleanup_pending", "This clip still needs cleanup. Retry removing it from My uploads."),
            _ => null
        };
        if (known is not null) return known;
        if (status == HttpStatusCode.ServiceUnavailable || status == HttpStatusCode.BadGateway
            || status == HttpStatusCode.GatewayTimeout) return HomeMediaOffline();
        if (status == HttpStatusCode.NotFound) return new("clip_unavailable", "This clip is no longer available.");
        if (status == HttpStatusCode.InsufficientStorage) return new("storage_full", "Clip storage is full. Please try again later.");
        return MakeSafeError(status, bytes, "rest/v1/rpc/community_home_media");
    }
}
