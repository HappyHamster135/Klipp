namespace Klipp.Core.Community;

public sealed record CommunityOperationsCapabilities(bool Enabled, bool AccountDeletionEnabled)
{
    public static CommunityOperationsCapabilities Unavailable { get; } = new(false, false);
}
public sealed record CommunitySupportPage(IReadOnlyList<CommunitySupportRequest> Items, CommunityFeedCursor? NextCursor);
public sealed record CommunityReportsPage(IReadOnlyList<CommunityOperatorReport> Items, CommunityFeedCursor? NextCursor);

public sealed record CommunityOwnUsage(long SlotsUsed, long SlotsLimit, long Published, long Uploading, long Deleting, long StoredBytes);
public sealed record CommunityBetaUsage(long SlotsUsed, long SlotsLimit, long ReservedBytes, long StorageBytes, long StorageLimitBytes, long UnknownStorageObjects);
public sealed record CommunityProviderUsage(long? Used, long Limit, DateTimeOffset? PeriodStart, DateTimeOffset? PeriodEnd,
    DateTimeOffset? ObservedAt, string? Source)
{
    public bool IsCurrent(DateTimeOffset now) => Used is not null && ObservedAt is not null && PeriodStart <= now
        && PeriodEnd > now && ObservedAt <= now.AddMinutes(5) && now - ObservedAt <= TimeSpan.FromHours(24);
}
public sealed record CommunityOperatorUsage(long RegisteredUsers, long DatabaseBytes, long DatabaseLimitBytes,
    long OpenReports, long OpenSupport, IReadOnlyDictionary<string, CommunityProviderUsage> ProviderUsage);
public sealed record CommunityUsage(CommunityOwnUsage Me, CommunityBetaUsage Beta, long MaximumVideoBytes,
    double MaximumDurationSeconds, int WarningPercent, bool IsOperator, DateTimeOffset CheckedAt, CommunityOperatorUsage? Operations)
{
    public bool UploadsFull => Me.SlotsUsed >= Me.SlotsLimit || Beta.SlotsUsed >= Beta.SlotsLimit;
    public bool NearLimit => Me.SlotsUsed * 100m >= Me.SlotsLimit * WarningPercent || Beta.SlotsUsed * 100m >= Beta.SlotsLimit * WarningPercent
        || Beta.StorageBytes * 100m >= Beta.StorageLimitBytes * WarningPercent;
    public string UploadWarning => Me.SlotsUsed >= Me.SlotsLimit
        ? "Your upload slots are full. Delete a shared clip from My uploads to make room. Your local recordings stay in Library."
        : Beta.SlotsUsed >= Beta.SlotsLimit ? "The free beta is full. Uploads will resume when a slot becomes available."
        : NearLimit ? "The free beta is nearing its clip limit. Check Usage before sharing more clips." : string.Empty;
}
public sealed record CommunitySupportRequest(Guid Id, string Category, string Body, string Status,
    DateTimeOffset CreatedAt, string Reply, DateTimeOffset UpdatedAt, Guid? OwnerId = null, string? Username = null);
public sealed record CommunityOperatorReport(Guid Id, Guid ClipId, Guid ReporterId, string Reason, string Status,
    DateTimeOffset CreatedAt, string Title, string OwnerUsername, string ReporterUsername, bool Hidden,
    string ReviewNote, DateTimeOffset? ReviewedAt);
