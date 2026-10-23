using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Klipp.Core.Community;

public sealed partial class CommunityAccountService
{
    private const int MaximumMediaResponseBytes = 512 * 1024;

    public Task<CommunityFeedPage> GetMediaFeedAsync(CommunityFeedCursor? cursor = null, bool mine = false,
        CancellationToken cancellationToken = default)
    {
        ValidateCursor(cursor);
        return RunMediaAsync(async (generation, token) =>
        {
            using var result = await MediaRpcAsync("community_feed", new
            {
                p_limit = CommunityMediaLimits.FeedPageSize + 1,
                p_before = CursorTime(cursor), p_before_id = cursor?.Id, p_mine = mine
            }, generation, token);
            var clips = ParseClips(result.RootElement, CommunityMediaLimits.FeedPageSize + 1);
            ValidateDescending(clips.Select(c => new CommunityFeedCursor(c.CreatedAt, c.Id)), cursor);
            if (mine && clips.Any(c => c.OwnerId != _session!.User!.Id)) throw Unavailable();
            if (!mine && clips.Any(c => !c.CanPlay)) throw Unavailable();
            var items = clips.Take(CommunityMediaLimits.FeedPageSize).ToArray();
            return new CommunityFeedPage(items, clips.Count > items.Length
                ? new CommunityFeedCursor(items[^1].CreatedAt, items[^1].Id) : null);
        }, cancellationToken);
    }

    public Task<CommunityCommentsPage> GetClipCommentsPageAsync(Guid clipId, CommunityFeedCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ValidateId(clipId);
        ValidateCursor(cursor);
        return RunMediaAsync(async (generation, token) =>
        {
            using var result = await MediaRpcAsync("community_comments", new
            {
                p_clip_id = clipId, p_limit = CommunityMediaLimits.CommentsPageSize + 1,
                p_before = CursorTime(cursor), p_before_id = cursor?.Id
            }, generation, token);
            if (result.RootElement.ValueKind != JsonValueKind.Array
                || result.RootElement.GetArrayLength() > CommunityMediaLimits.CommentsPageSize + 1) throw Unavailable();
            var comments = result.RootElement.EnumerateArray().Select(ParseComment).ToArray();
            if (comments.Any(c => c.ClipId != clipId)) throw Unavailable();
            ValidateDescending(comments.Select(c => new CommunityFeedCursor(c.CreatedAt, c.Id)), cursor);
            var items = comments.Take(CommunityMediaLimits.CommentsPageSize).ToArray();
            return new CommunityCommentsPage(items, comments.Length > items.Length
                ? new CommunityFeedCursor(items[^1].CreatedAt, items[^1].Id) : null);
        }, cancellationToken);
    }

    public Task<CommunityCommentsPage> GetCommentsPageAsync(Guid clipId, CommunityFeedCursor? cursor = null,
        CancellationToken cancellationToken = default) => GetClipCommentsPageAsync(clipId, cursor, cancellationToken);

    public async Task<IReadOnlyList<CommunityComment>> GetClipCommentsAsync(Guid clipId,
        CancellationToken cancellationToken = default) =>
        (await GetClipCommentsPageAsync(clipId, cancellationToken: cancellationToken)).Items;

    public Task<CommunityClip> SetClipLikedAsync(Guid clipId, bool liked, CancellationToken cancellationToken = default)
    {
        ValidateId(clipId);
        return RunMediaAsync(async (generation, token) =>
        {
            using var result = await MediaRpcAsync("community_set_like", new { p_clip_id = clipId, p_liked = liked }, generation, token);
            var clip = ParseClip(result.RootElement);
            if (clip.Id != clipId || clip.IsLikedByMe != liked || !clip.IsPublished) throw Unavailable();
            return clip;
        }, cancellationToken);
    }

    public Task<CommunityComment> AddClipCommentAsync(Guid clipId, string body,
        CancellationToken cancellationToken = default)
    {
        ValidateId(clipId);
        body = ValidateText(body, 1, 500, "invalid_comment", "Write a comment of 1–500 characters.", true);
        return RunMediaAsync(async (generation, token) =>
        {
            using var result = await MediaRpcAsync("community_add_comment", new { p_clip_id = clipId, p_body = body }, generation, token);
            var comment = ParseComment(result.RootElement);
            if (comment.ClipId != clipId || comment.OwnerId != _session!.User!.Id) throw Unavailable();
            return comment;
        }, cancellationToken);
    }

    public Task DeleteClipCommentAsync(Guid commentId, CancellationToken cancellationToken = default)
    {
        ValidateId(commentId);
        return RunMediaAsync(async (generation, token) =>
        {
            using var result = await MediaRpcAsync("community_delete_comment", new { p_comment_id = commentId }, generation, token);
            RequireTrue(result.RootElement, "deleted");
            return true;
        }, cancellationToken);
    }

    public Task ReportClipAsync(Guid clipId, string reason, CancellationToken cancellationToken = default)
    {
        ValidateId(clipId);
        reason = ValidateText(reason, 1, 500, "invalid_report", "Describe the issue in 500 characters or fewer.", true);
        return RunMediaAsync(async (generation, token) =>
        {
            using var result = await MediaRpcAsync("community_report_clip", new { p_clip_id = clipId, p_reason = reason }, generation, token);
            RequireTrue(result.RootElement, "reported");
            return true;
        }, cancellationToken);
    }

    public Task BlockUserAsync(Guid userId, CancellationToken cancellationToken = default) =>
        SetUserBlockedAsync(userId, true, cancellationToken);

    public Task<IReadOnlyList<CommunityBlockedUser>> GetBlockedUsersAsync(CancellationToken cancellationToken = default) =>
        RunMediaAsync<IReadOnlyList<CommunityBlockedUser>>(async (generation, token) =>
        {
            using var result = await MediaRpcAsync("community_blocked_members", new { }, generation, token);
            if (result.RootElement.ValueKind != JsonValueKind.Array || result.RootElement.GetArrayLength() > 100) throw Unavailable();
            var users = result.RootElement.EnumerateArray().Select(row => new CommunityBlockedUser(
                ReadId(row, "user_id"), row.TryGetProperty("username", out var username) && username.ValueKind == JsonValueKind.Null
                    ? string.Empty : ReadText(row, "username", 24, allowEmpty: true), ReadText(row, "display_name", 40))).ToArray();
            if (users.Any(u => u.UserId == _session!.User!.Id) || users.Select(u => u.UserId).Distinct().Count() != users.Length)
                throw Unavailable();
            return users;
        }, cancellationToken);

    public Task SetUserBlockedAsync(Guid userId, bool blocked, CancellationToken cancellationToken = default)
    {
        ValidateId(userId);
        return RunMediaAsync(async (generation, token) =>
        {
            if (userId == _session!.User!.Id)
                throw new CommunityAccountException("invalid_user", "You cannot block your own profile.");
            using var result = await MediaRpcAsync("community_block_user", new { p_user_id = userId, p_blocked = blocked }, generation, token);
            if (result.RootElement.ValueKind != JsonValueKind.Object
                || !result.RootElement.TryGetProperty("blocked", out var value)
                || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || value.GetBoolean() != blocked)
                throw Unavailable();
            return true;
        }, cancellationToken);
    }

    public Task<Uri> GetClipPlaybackUriAsync(Guid clipId, CancellationToken cancellationToken = default) =>
        GetClipAssetUriAsync(clipId, poster: false, cancellationToken);

    public Task<Uri> GetClipPosterUriAsync(Guid clipId, CancellationToken cancellationToken = default) =>
        GetClipAssetUriAsync(clipId, poster: true, cancellationToken);

    private Task<Uri> GetClipAssetUriAsync(Guid clipId, bool poster, CancellationToken cancellationToken)
    {
        ValidateId(clipId);
        return RunMediaAsync(async (generation, token) =>
        {
            using var clipResult = await MediaRpcAsync("community_get_clip", new { p_clip_id = clipId }, generation, token);
            var clip = ParseClip(clipResult.RootElement);
            if (clip.Id != clipId || !clip.CanPlay)
                throw new CommunityAccountException("clip_unavailable", "This clip is not available to play.");
            if (clip.StorageProvider == "home")
            {
                using var home = await HomeMediaJsonAsync(HttpMethod.Post, $"v1/clips/{clipId:D}/playback", new { }, generation, token);
                return ParseHomeAssetUri(home.RootElement, clip.OwnerId, clip.Id, poster);
            }
            var bucket = poster ? "community-posters" : "community-videos";
            var path = AssetPath(clip.OwnerId, clip.Id, poster);
            var session = await EnsureSessionAsync(generation, token);
            using var result = await SendAuthenticatedAsync(HttpMethod.Post, $"storage/v1/object/sign/{bucket}/{path}",
                session, new { expiresIn = poster ? 120 : 300 }, token);
            EnsureCurrent(generation);
            var uri = ParseSignedUri(result.RootElement, bucket, path);
            token.ThrowIfCancellationRequested();
            return uri;
        }, cancellationToken);
    }

    public Task DeleteClipAsync(Guid clipId, CancellationToken cancellationToken = default)
    {
        ValidateId(clipId);
        return RunMediaAsync(async (generation, token) =>
        {
            await DeleteClipCoreAsync(clipId, generation, token);
            return true;
        }, cancellationToken);
    }

    public Task<CommunityClip> UploadClipAsync(CommunityUploadRequest upload,
        IProgress<CommunityUploadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        var title = ValidateText(upload.Title, 1, 120, "invalid_title", "Enter a title of 1–120 characters.", false);
        var description = ValidateText(upload.Description, 0, 500, "invalid_description", "Keep the description to 500 characters.", true);
        if (!double.IsFinite(upload.DurationSeconds) || upload.DurationSeconds <= 0
            || upload.DurationSeconds > CommunityMediaLimits.MaximumDurationSeconds)
            throw new CommunityAccountException("invalid_duration", "Upload a clip of 60 seconds or less.");
        return RunMediaAsync(async (generation, token) =>
        {
            await using var video = OpenUploadFile(upload.VideoPath, CommunityMediaLimits.MaximumVideoBytes, ".mp4");
            await using var poster = OpenUploadFile(upload.PosterPath, CommunityMediaLimits.MaximumPosterBytes, ".jpg", ".jpeg");
            var total = video.Length + poster.Length;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromMinutes(3));
            Guid? reservation = null;
            try
            {
                ReportProgress(progress, 0, total, "Preparing upload");
                var homeUpload = _configuration.HasHomeMedia;
                if (homeUpload) RequireHomeMediaUri();
                using var begin = await MediaRpcAsync(homeUpload ? "community_begin_home_upload" : "community_begin_upload", new
                {
                    p_title = title, p_description = description,
                    p_duration_seconds = upload.DurationSeconds, p_byte_size = video.Length
                }, generation, deadline.Token);
                var pending = ParseClip(begin.RootElement);
                if (pending.OwnerId != _session!.User!.Id || pending.Status != "uploading"
                    || pending.StorageProvider != (homeUpload ? "home" : "supabase")) throw Unavailable();
                reservation = pending.Id;
                if (homeUpload)
                {
                    await UploadHomeAssetAsync(pending.Id, false, video, "video/mp4", generation, deadline.Token,
                        value => ReportProgress(progress, value, total, "Uploading video"));
                    await UploadHomeAssetAsync(pending.Id, true, poster, "image/jpeg", generation, deadline.Token,
                        value => ReportProgress(progress, video.Length + value, total, "Uploading thumbnail"));
                }
                else
                {
                    await UploadObjectAsync("community-videos", AssetPath(pending.OwnerId, pending.Id, false), video,
                        "video/mp4", generation, deadline.Token, value => ReportProgress(progress, value, total, "Uploading video"));
                    await UploadObjectAsync("community-posters", AssetPath(pending.OwnerId, pending.Id, true), poster,
                        "image/jpeg", generation, deadline.Token, value => ReportProgress(progress, video.Length + value, total, "Uploading thumbnail"));
                }
                ReportProgress(progress, total, total, "Publishing clip");
                using var published = homeUpload
                    ? await HomeMediaJsonAsync(HttpMethod.Post, $"v1/clips/{pending.Id:D}/publish", new { }, generation, deadline.Token)
                    : await MediaRpcAsync("community_publish_clip", new { p_clip_id = pending.Id }, generation, deadline.Token);
                var clip = ParseClip(published.RootElement);
                if (clip.Id != pending.Id || clip.OwnerId != pending.OwnerId || !clip.IsPublished
                    || clip.StorageProvider != pending.StorageProvider) throw Unavailable();
                EnsureCurrent(generation);
                deadline.Token.ThrowIfCancellationRequested();
                ReportProgress(progress, total, total, "Uploaded");
                return clip;
            }
            catch
            {
                // A failed removal deliberately leaves its reservation visible in My uploads for a later retry.
                if (reservation.HasValue && IsCurrentMediaIdentity(generation))
                {
                    using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(_mediaCancellation.Token);
                    cleanup.CancelAfter(TimeSpan.FromSeconds(20));
                    try { await DeleteClipCoreAsync(reservation.Value, generation, cleanup.Token); }
                    catch (Exception ex) when (ex is CommunityAccountException or OperationCanceledException) { }
                }
                EnsureCurrent(generation);
                if (deadline.IsCancellationRequested && !token.IsCancellationRequested)
                    throw new CommunityAccountException("timeout", "The upload took too long. Try again on a steadier connection.");
                throw;
            }
        }, cancellationToken);
    }

    private async Task<T> RunMediaAsync<T>(Func<int, CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var generation = Volatile.Read(ref _generation);
        var requestedUser = _session?.User?.Id;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _mediaCancellation.Token);
        T? result = default;
        await RunAsync(generation, linked.Token, async () =>
        {
            try
            {
                await EnsureSessionAsync(generation, linked.Token);
                if (_session?.User?.Id != requestedUser) throw new OperationCanceledException();
                result = await operation(generation, linked.Token);
                EnsureCurrent(generation);
                linked.Token.ThrowIfCancellationRequested();
            }
            catch { EnsureCurrent(generation); throw; }
        });
        return result!;
    }

    private bool IsCurrentMediaIdentity(int generation) => !_disposed
        && generation == Volatile.Read(ref _generation) && _session?.User is not null && !_signOutPending;

    private async Task<JsonDocument> MediaRpcAsync(string name, object body, int generation, CancellationToken token)
    {
        EnsureCurrent(generation);
        var session = await EnsureSessionAsync(generation, token);
        var document = await SendAuthenticatedAsync(HttpMethod.Post, "rest/v1/rpc/" + name, session,
            body, token, maximumResponseBytes: MaximumMediaResponseBytes);
        try { EnsureCurrent(generation); token.ThrowIfCancellationRequested(); return document; }
        catch { document.Dispose(); throw; }
    }

    private async Task DeleteClipCoreAsync(Guid clipId, int generation, CancellationToken token)
    {
        using var hidden = await MediaRpcAsync("community_remove_clip", new { p_clip_id = clipId }, generation, token);
        var status = ReadText(hidden.RootElement, "status", 20);
        if (ReadId(hidden.RootElement, "id") != clipId) throw Unavailable();
        if (status == "deleted") return;
        var ownerId = ReadId(hidden.RootElement, "owner_id");
        if (ownerId != _session!.User!.Id || status != "deleting") throw Unavailable();
        ValidateAssetPaths(hidden.RootElement, ownerId, clipId);
        if (ReadStorageProvider(hidden.RootElement) == "home")
        {
            using var deleted = await HomeMediaJsonAsync(HttpMethod.Delete, $"v1/clips/{clipId:D}", null, generation, token);
            if (ReadId(deleted.RootElement, "id") != clipId) throw Unavailable();
            RequireTrue(deleted.RootElement, "deleted");
            return;
        }
        Exception? failure = null;
        foreach (var poster in new[] { false, true })
        {
            try
            {
                var session = await EnsureSessionAsync(generation, token);
                EnsureCurrent(generation);
                var bucket = poster ? "community-posters" : "community-videos";
                using var removed = await SendAuthenticatedAsync(HttpMethod.Delete, $"storage/v1/object/{bucket}",
                    session, new { prefixes = new[] { AssetPath(ownerId, clipId, poster) } }, token);
                EnsureCurrent(generation);
            }
            catch (CommunityAccountException ex) { failure ??= ex; }
        }
        if (failure is not null) throw failure;
        using var finished = await MediaRpcAsync("community_finish_delete", new { p_clip_id = clipId }, generation, token);
        RequireTrue(finished.RootElement, "deleted");
    }

    private async Task UploadObjectAsync(string bucket, string path, Stream source, string mimeType,
        int generation, CancellationToken token, Action<long> progress)
    {
        EnsureCurrent(generation);
        var session = await EnsureSessionAsync(generation, token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_configuration.ProjectUrl + $"/storage/v1/object/{bucket}/{path}"));
            request.Headers.Add("apikey", _configuration.PublishableKey);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            request.Headers.Add("x-upsert", "false");
            request.Content = new UploadContent(source, mimeType, progress);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            var bytes = await ReadBoundedAsync(response.Content, token);
            EnsureCurrent(generation);
            if (!response.IsSuccessStatusCode)
            {
                var error = MakeSafeError(response.StatusCode, bytes, "storage/v1/object/");
                if (error.Code == "session_expired") await ClearSessionAsync(token);
                throw error;
            }
            token.ThrowIfCancellationRequested();
        }
        catch (HttpRequestException) { throw new CommunityAccountException("offline", "Could not upload the clip. Check your connection and try again."); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new CommunityAccountException("timeout", "The upload took too long. Try again on a steadier connection."); }
    }

    private static FileStream OpenUploadFile(string path, long limit, params string[] extensions)
    {
        FileStream? stream = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                throw new CommunityAccountException("invalid_file", "Choose a prepared MP4 video and JPEG thumbnail.");
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > limit)
                throw new CommunityAccountException("file_too_large", "Use a video up to 16 MiB and a thumbnail up to 256 KiB.");
            return stream;
        }
        catch (CommunityAccountException) { stream?.Dispose(); throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { stream?.Dispose(); throw new CommunityAccountException("invalid_file", "The prepared clip could not be opened. Choose the clip again."); }
    }

    private sealed class UploadContent : HttpContent
    {
        private readonly Stream _source;
        private readonly Action<long> _progress;
        public UploadContent(Stream source, string contentType, Action<long> progress)
        {
            _source = source;
            _progress = progress;
            Headers.ContentType = new MediaTypeHeaderValue(contentType);
            Headers.ContentLength = source.Length;
        }
        protected override bool TryComputeLength(out long length) { length = _source.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];
            long sent = 0;
            int read;
            while ((read = await _source.ReadAsync(buffer, cancellationToken)) != 0)
            {
                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                sent += read;
                _progress(sent);
            }
        }
    }

    private Uri ParseSignedUri(JsonElement root, string bucket, string path)
    {
        var value = ReadText(root, "signedURL", 8192);
        if (value.StartsWith("/object/sign/", StringComparison.Ordinal)) value = "/storage/v1" + value;
        if (!Uri.TryCreate(new Uri(_configuration.ProjectUrl + "/"), value, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != new Uri(_configuration.ProjectUrl).Host
            || uri.Port != new Uri(_configuration.ProjectUrl).Port || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != $"/storage/v1/object/sign/{bucket}/{path}"
            || uri.Query.Length is < 8 or > 4096 || !uri.Query.StartsWith("?token=", StringComparison.Ordinal)
            || value.Any(char.IsControl)) throw Unavailable();
        return uri;
    }

    private IReadOnlyList<CommunityClip> ParseClips(JsonElement root, int maximum)
    {
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > maximum) throw Unavailable();
        return root.EnumerateArray().Select(ParseClip).ToArray();
    }

    private CommunityClip ParseClip(JsonElement row)
    {
        try
        {
            var id = ReadId(row, "id");
            var owner = ReadId(row, "owner_id");
            var duration = row.GetProperty("duration_seconds").GetDouble();
            var bytes = row.GetProperty("byte_size").GetInt64();
            var likes = row.GetProperty("like_count").GetInt32();
            var comments = row.GetProperty("comment_count").GetInt32();
            var status = ReadText(row, "status", 20);
            if (!double.IsFinite(duration) || duration <= 0 || duration > CommunityMediaLimits.MaximumDurationSeconds
                || bytes <= 0 || bytes > CommunityMediaLimits.MaximumVideoBytes || likes < 0 || comments < 0
                || status is not ("uploading" or "published" or "deleting")) throw Unavailable();
            var (video, poster) = ValidateAssetPaths(row, owner, id);
            var username = ReadOptionalText(row, "username", 24);
            // The server hides incomplete public profiles, while an owner can still recover their own uploads.
            if (username is null && owner != _session?.User?.Id) throw Unavailable();
            return new(id, owner, username ?? string.Empty, ReadText(row, "display_name", 40),
                ReadText(row, "title", 120), ReadText(row, "description", 500, allowEmpty: true), duration, bytes,
                ReadTime(row, "created_at"), likes, comments, row.GetProperty("liked_by_me").GetBoolean(), status, video, poster,
                ReadStorageProvider(row), row.TryGetProperty("moderation_hidden", out var hidden) && hidden.GetBoolean());
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { throw Unavailable(); }
    }

    private static string ReadStorageProvider(JsonElement row)
    {
        var provider = ReadOptionalText(row, "storage_provider", 20) ?? "supabase";
        return provider is "supabase" or "home" ? provider : throw Unavailable();
    }

    private static (string? Video, string? Poster) ValidateAssetPaths(JsonElement row, Guid owner, Guid id)
    {
        var video = ReadOptionalText(row, "video_path", 120);
        var poster = ReadOptionalText(row, "poster_path", 120);
        if (video is not null && video != AssetPath(owner, id, false)
            || poster is not null && poster != AssetPath(owner, id, true)) throw Unavailable();
        return (video, poster);
    }

    private static CommunityComment ParseComment(JsonElement row)
    {
        try
        {
            return new(ReadId(row, "id"), ReadId(row, "clip_id"), ReadId(row, "owner_id"),
                ReadText(row, "username", 24), ReadText(row, "display_name", 40), ReadText(row, "body", 500), ReadTime(row, "created_at"));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw Unavailable(); }
    }

    private static Guid ReadId(JsonElement row, string field)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(field, out var value)
            || value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var id) || id == Guid.Empty) throw Unavailable();
        return id;
    }
    private static string ReadText(JsonElement row, string field, int maximum, bool allowEmpty = false)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(field, out var value)
            || value.ValueKind != JsonValueKind.String || value.GetString() is not { } text
            || text.EnumerateRunes().Count() > maximum || !allowEmpty && text.Length == 0
            || text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t')) throw Unavailable();
        return text;
    }
    private static string? ReadOptionalText(JsonElement row, string field, int maximum) =>
        !row.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null ? null : ReadText(row, field, maximum);
    private static DateTimeOffset ReadTime(JsonElement row, string field)
    {
        var text = ReadText(row, field, 64);
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            || time < DateTimeOffset.UnixEpoch) throw Unavailable();
        return time;
    }
    private static void RequireTrue(JsonElement row, string field)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.True)
            throw Unavailable();
    }
    private static void ValidateId(Guid id)
    {
        if (id == Guid.Empty) throw new CommunityAccountException("invalid_media", "Choose a clip and try again.");
    }
    private static void ValidateCursor(CommunityFeedCursor? cursor)
    {
        if (cursor is not null && (cursor.Id == Guid.Empty || cursor.CreatedAt < DateTimeOffset.UnixEpoch))
            throw new CommunityAccountException("invalid_cursor", "Refresh Media to load the latest clips.");
    }
    private static string? CursorTime(CommunityFeedCursor? cursor) => cursor?.CreatedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static void ValidateDescending(IEnumerable<CommunityFeedCursor> rows, CommunityFeedCursor? previous)
    {
        foreach (var row in rows)
        {
            // PostgreSQL compares UUIDs by their canonical bytes, not Guid.CompareTo's platform field order.
            if (previous is not null && (row.CreatedAt > previous.CreatedAt
                || row.CreatedAt == previous.CreatedAt && string.CompareOrdinal(row.Id.ToString("N"), previous.Id.ToString("N")) >= 0))
                throw Unavailable();
            previous = row;
        }
    }
    private static string ValidateText(string? text, int minimum, int maximum, string code, string message, bool multiline)
    {
        var value = text?.Trim() ?? string.Empty;
        if (value.Length < minimum || value.Length > maximum
            || value.Any(c => char.IsControl(c) && (!multiline || c is not '\r' and not '\n' and not '\t')))
            throw new CommunityAccountException(code, message);
        return value;
    }
    private static string AssetPath(Guid ownerId, Guid clipId, bool poster) => $"{ownerId:D}/{clipId:D}/{(poster ? "poster.jpg" : "video.mp4")}";
    private static void ReportProgress(IProgress<CommunityUploadProgress>? progress, long done, long total, string status)
    {
        try { progress?.Report(new(done, total, status)); }
        catch { /* UI progress must not leave a half-published upload behind. */ }
    }
    private static CommunityAccountException? MediaError(string? code, string path) => code switch
    {
        "KM001" => new("verified_account_required", "Verify your email address before using Media."),
        "KM002" => new("profile_required", "Choose a username in your profile before using Media."),
        "KM003" => new("invalid_media", "Check the clip details and try again."),
        "KM004" => new("upload_limit", "You can keep five clips in this beta. Remove one from My uploads to upload another."),
        "KM005" => new("storage_full", "The free beta's clip storage is full. Please try again later."),
        "KM006" => new("upload_not_ready", "The clip files are not ready to publish. Try uploading the clip again."),
        "KM007" => new("clip_unavailable", "This clip or profile is no longer available."),
        "KM008" when path.EndsWith("community_begin_home_upload", StringComparison.Ordinal) =>
            new("rate_limited", "The upload limit has been reached. Please try again later."),
        "KM008" => new("rate_limited", "You're doing that too quickly. Wait a moment and try again."),
        "KM009" when path.EndsWith("community_report_clip", StringComparison.Ordinal) =>
            new("report_limit", "The beta's report queue is full. Please try again later."),
        "KM009" when path.EndsWith("community_block_user", StringComparison.Ordinal) =>
            new("block_limit", "Your block list is full. Remove a block before adding another."),
        "KM009" => new("comment_limit", "This clip has reached the beta's comment limit."),
        "KM010" => new("cleanup_pending", "The clip is hidden. Remove it again from My uploads to finish clearing its files."),
        "KM011" => new("access_denied", "Your account cannot change this clip or comment."),
        "KM015" => new("upload_not_ready", "The clip has not finished uploading. Please try again."),
        "KM016" => new("hosting_unavailable", "Clip hosting is getting ready. Please try again shortly."),
        "KM017" => new("upload_expired", "This unfinished upload expired. Choose the clip again to start a new upload."),
        "KM018" => new("legacy_upload_retired", "This upload method has been retired. Update Klipp to upload clips to the new server."),
        "KM019" => new("uploads_busy", "Uploads or clip cleanup are still in progress. Please wait a moment before starting another upload."),
        _ => null
    };
}
