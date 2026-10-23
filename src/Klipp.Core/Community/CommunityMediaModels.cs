namespace Klipp.Core.Community;

public sealed record CommunityClip(Guid Id, Guid OwnerId, string ProfileUsername, string ProfileDisplayName,
    string Title, string Description, double DurationSeconds, long ByteSize, DateTimeOffset CreatedAt,
    int LikeCount, int CommentCount, bool IsLikedByMe, string Status, string? VideoPath = null, string? PosterPath = null,
    string StorageProvider = "supabase", bool ModerationHidden = false)
{
    public bool IsPublished => Status == "published";
    public bool CanPlay => IsPublished && !ModerationHidden;
}

public sealed record CommunityFeedCursor(DateTimeOffset CreatedAt, Guid Id);

public sealed record CommunityFeedPage(IReadOnlyList<CommunityClip> Clips, CommunityFeedCursor? NextCursor)
{
    public IReadOnlyList<CommunityClip> Items => Clips;
}

public sealed record CommunityComment(Guid Id, Guid ClipId, Guid OwnerId, string ProfileUsername,
    string ProfileDisplayName, string Body, DateTimeOffset CreatedAt)
{
    public Guid UserId => OwnerId;
}

public sealed record CommunityCommentsPage(IReadOnlyList<CommunityComment> Items, CommunityFeedCursor? NextCursor);

public sealed record CommunityBlockedUser(Guid UserId, string Username, string DisplayName);

/// <summary>Prepared local files. Paths and file contents are never sent to profile or feed APIs.</summary>
public sealed record CommunityUploadRequest(string VideoPath, string PosterPath, string Title,
    string Description, double DurationSeconds);

public sealed record CommunityUploadProgress(long CompletedBytes, long TotalBytes, string Status)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)CompletedBytes / TotalBytes, 0, 1) : 0;
}

public static class CommunityMediaLimits
{
    public const long MaximumVideoBytes = 16 * 1024 * 1024;
    public const long MaximumPosterBytes = 256 * 1024;
    public const double MaximumDurationSeconds = 60;
    public const int FeedPageSize = 12;
    public const int CommentsPageSize = 30;
}
