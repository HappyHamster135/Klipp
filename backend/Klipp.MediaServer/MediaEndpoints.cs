using System.Security.Cryptography;
using System.Text.Json;

namespace Klipp.MediaServer;

public static class MediaEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Json(new { status = "ok" }));
        app.MapGet("/health/ready", (IStorageGuard guard) => { guard.Verify(); return Results.Json(new { status = "ready" }); });
        var clips = app.MapGroup("/v1/clips").RequireRateLimiting("control");
        clips.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            var authorization = context.Request.Headers.Authorization;
            if (authorization.Count != 1 || authorization[0] is not { } header || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                || header.Length is < 24 or > 8192 || header[7..].Any(char.IsWhiteSpace))
                throw new MediaException(401, "session_expired", "Sign in again to continue.");
            var gateway = context.RequestServices.GetRequiredService<ICommunityGateway>();
            context.Items["media.identity"] = await gateway.AuthenticateAsync(header[7..], context.RequestAborted);
            return await next(invocation);
        });
        clips.MapPut("/{id:guid}/{asset}", UploadAsync);
        clips.MapPost("/{id:guid}/publish", PublishAsync);
        clips.MapPost("/{id:guid}/playback", PlaybackAsync);
        clips.MapDelete("/{id:guid}", DeleteAsync);
        app.MapMethods("/v1/assets/{owner:guid}/{id:guid}/{asset}", ["GET", "HEAD"], Asset)
            .RequireRateLimiting("assets");
    }

    private static async Task<IResult> UploadAsync(Guid id, string asset, HttpContext context, ICommunityGateway gateway,
        PrivateMediaStore store, IMediaValidator validator, IBackupGate backup)
    {
        EnsureDesktop(context);
        backup.EnsureUploadsAllowed();
        if (asset is not ("video" or "poster")) throw MediaException.Unavailable();
        var expectedMime = asset == "video" ? "video/mp4" : "image/jpeg";
        if (!string.Equals(context.Request.ContentType, expectedMime, StringComparison.OrdinalIgnoreCase))
            throw new MediaException(415, "invalid_media", "Upload a prepared Klipp MP4 clip and JPEG thumbnail.");
        if (context.Request.ContentLength is not > 0)
            throw new MediaException(411, "length_required", "The upload must include its file size.");
        using var timeout = Deadline(context);
        var identity = Identity(context);
        await using var clipLock = await store.AcquireClipAsync(id, timeout.Token);
        var clip = await RequireOwnerAsync(gateway, identity, id, timeout.Token);
        if (clip.Status != "uploading") throw new MediaException(409, "upload_closed", "This clip can no longer be uploaded.");
        if (asset == "video" && context.Request.ContentLength != clip.VideoBytes)
            throw new MediaException(400, "invalid_size", "The uploaded file does not match its reservation.");
        await store.StoreUploadAsync(identity.UserId, id, asset, context.Request.Body, context.Request.ContentLength.Value, validator, timeout.Token);
        return Results.Json(new { uploaded = true });
    }

    private static async Task<IResult> PublishAsync(Guid id, HttpContext context, ICommunityGateway gateway,
        PrivateMediaStore store, IMediaValidator validator, IBackupGate backup)
    {
        EnsureDesktop(context);
        backup.EnsureUploadsAllowed();
        using var timeout = Deadline(context);
        var identity = Identity(context);
        await using var clipLock = await store.AcquireClipAsync(id, timeout.Token);
        await using var backupLock = await store.AcquireBackupReadLockAsync(timeout.Token);
        var clip = await RequireOwnerAsync(gateway, identity, id, timeout.Token);
        if (clip.Status == "published") return Results.Json(clip.Json);
        if (clip.Status != "uploading") throw MediaException.Unavailable();
        store.Guard.Verify();
        var videoPath = store.AssetPath(clip.OwnerId, id, "video");
        var posterPath = store.AssetPath(clip.OwnerId, id, "poster");
        if (!File.Exists(videoPath) || !File.Exists(posterPath))
            throw new MediaException(409, "upload_incomplete", "Upload both the video and thumbnail before publishing.");
        var videoBytes = new FileInfo(videoPath).Length;
        var posterBytes = new FileInfo(posterPath).Length;
        if (videoBytes != clip.VideoBytes || posterBytes is <= 0 or > MediaOptions.MaximumPosterBytes)
            throw new MediaException(400, "invalid_size", "The uploaded files do not match the reservation.");
        var duration = await validator.ValidateVideoAsync(videoPath, timeout.Token);
        await validator.ValidatePosterAsync(posterPath, timeout.Token);
        if (Math.Abs(duration - clip.DurationSeconds) > 0.5)
            throw new MediaException(400, "invalid_duration", "The uploaded clip duration does not match its reservation.");
        var evidence = new MediaEvidence(videoBytes, posterBytes, duration,
            await HashFileAsync(videoPath, timeout.Token), await HashFileAsync(posterPath, timeout.Token));
        // Persist recovery metadata before publishing. Final assets remain immutable.
        await store.WriteManifestAsync(clip, evidence, clip.Json, timeout.Token);
        var published = await gateway.PublishAsync(identity, id, evidence, timeout.Token);
        var result = GatewayClip.Parse(published);
        if (result.Id != id || result.OwnerId != identity.UserId || result.Status != "published") throw MediaException.StorageUnavailable();
        await store.WriteManifestAsync(clip, evidence, published, timeout.Token);
        return Results.Json(published);
    }

    private static async Task<IResult> PlaybackAsync(Guid id, HttpContext context, ICommunityGateway gateway,
        PrivateMediaStore store, MediaSigner signer, TimeProvider time)
    {
        var clip = await gateway.GetClipAsync(Identity(context), id, context.RequestAborted);
        if (clip.Id != id || clip.Status != "published") throw MediaException.Unavailable();
        // Confirm both local files exist before handing a player a signed capability.
        using (store.OpenAsset(clip.OwnerId, id, "video")) { }
        using (store.OpenAsset(clip.OwnerId, id, "poster")) { }
        return Results.Json(new { video_url = signer.Create(clip.OwnerId, id, "video", TimeSpan.FromMinutes(5)),
            poster_url = signer.Create(clip.OwnerId, id, "poster", TimeSpan.FromMinutes(2)),
            expires_at = time.GetUtcNow().AddMinutes(2) });
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext context, ICommunityGateway gateway, PrivateMediaStore store)
    {
        using var timeout = Deadline(context);
        var identity = Identity(context);
        await using var clipLock = await store.AcquireClipAsync(id, timeout.Token);
        // Check the provider before marking anything for removal. The home API
        // must never change the lifecycle of a legacy Supabase Storage clip.
        try { await RequireOwnerAsync(gateway, identity, id, timeout.Token); }
        catch (MediaException unavailable) when (unavailable.StatusCode == 404)
        { return Results.Json(new { id, deleted = true }); }
        var hidden = await gateway.BeginDeleteAsync(identity, id, timeout.Token);
        if (hidden.TryGetProperty("deleted", out var deleted) && deleted.ValueKind == JsonValueKind.True)
            return Results.Json(new { id, deleted = true });
        var clip = GatewayClip.Parse(hidden);
        if (clip.Id != id || clip.OwnerId != identity.UserId || clip.Status != "deleting") throw MediaException.Unavailable();
        await using var backupLock = await store.AcquireBackupReadLockAsync(timeout.Token);
        store.DeleteAssets(clip.OwnerId, id);
        var finished = await gateway.FinishDeleteAsync(identity, id, timeout.Token);
        if (!finished.TryGetProperty("deleted", out deleted) || deleted.ValueKind != JsonValueKind.True) throw MediaException.StorageUnavailable();
        return Results.Json(new { id, deleted = true });
    }

    private static IResult Asset(Guid owner, Guid id, string asset, HttpContext context, PrivateMediaStore store, MediaSigner signer)
    {
        if (context.Request.Query.Count != 2 || context.Request.Query["expires"].Count != 1 || context.Request.Query["signature"].Count != 1
            || !signer.Verify(owner, id, asset, context.Request.Query["expires"].ToString(), context.Request.Query["signature"].ToString()))
            throw new MediaException(403, "invalid_link", "This playback link has expired. Open the clip again.");
        return Results.File(store.OpenAsset(owner, id, asset), asset == "video" ? "video/mp4" : "image/jpeg", enableRangeProcessing: true);
    }

    private static async Task<GatewayClip> RequireOwnerAsync(ICommunityGateway gateway, MediaIdentity identity, Guid id, CancellationToken token)
    {
        var clip = await gateway.GetClipAsync(identity, id, token);
        if (clip.Id != id || clip.OwnerId != identity.UserId) throw MediaException.Unavailable();
        return clip;
    }

    private static void EnsureDesktop(HttpContext context)
    {
        if (context.Request.Headers.ContainsKey("Origin") || context.Request.Headers.Keys.Any(key => key.StartsWith("Sec-Fetch-", StringComparison.OrdinalIgnoreCase)))
            throw new MediaException(403, "desktop_upload_required", "Upload clips from Klipp on your PC.");
    }

    private static MediaIdentity Identity(HttpContext context) => (MediaIdentity)context.Items["media.identity"]!;
    private static CancellationTokenSource Deadline(HttpContext context)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        return timeout;
    }
    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var file = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token));
    }
}
