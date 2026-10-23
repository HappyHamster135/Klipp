using System.Net.Http;
using System.Text.Json;

namespace Klipp.Core.Community;

public sealed partial class CommunityAccountService
{
    private const int OperationsPageSize = 20;

    public Task<CommunityOperationsCapabilities> GetOperationsCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        RunMediaAsync(async (generation, token) =>
        {
            using var result = await MediaRpcAsync("community_operations_capabilities", new { }, generation, token);
            return ParseOperations(result.RootElement, root =>
            {
                if (root.GetProperty("version").GetInt32() != 1) return CommunityOperationsCapabilities.Unavailable;
                var enabled = root.GetProperty("enabled").GetBoolean();
                return new CommunityOperationsCapabilities(enabled, enabled && root.GetProperty("account_deletion").GetBoolean());
            });
        }, cancellationToken);

    public Task<CommunityUsage> GetUsageAsync(CancellationToken cancellationToken = default) =>
        RunMediaAsync(async (generation, token) =>
        {
            using var result = await MediaRpcAsync("community_usage", new { }, generation, token);
            return ParseOperations(result.RootElement, root =>
            {
                var me = root.GetProperty("me"); var beta = root.GetProperty("beta"); var limits = root.GetProperty("limits");
                var own = new CommunityOwnUsage(Count(me, "slots_used"), Count(me, "slots_limit", true), Count(me, "published"),
                    Count(me, "uploading"), Count(me, "deleting"), Count(me, "stored_bytes"));
                var total = new CommunityBetaUsage(Count(beta, "slots_used"), Count(beta, "slots_limit", true), Count(beta, "reserved_bytes"),
                    Count(beta, "storage_bytes"), Count(beta, "storage_limit_bytes", true), Count(beta, "unknown_storage_objects"));
                var isOperator = root.GetProperty("operator").GetBoolean();
                CommunityOperatorUsage? operations = null;
                if (root.TryGetProperty("operations", out var op) && op.ValueKind != JsonValueKind.Null)
                {
                    if (!isOperator) throw Unavailable();
                    var metrics = op.GetProperty("provider_usage");
                    var provider = new Dictionary<string, CommunityProviderUsage>();
                    foreach (var name in new[] { "cached_egress", "uncached_egress", "monthly_active_users", "email_daily", "email_monthly" })
                    {
                        var metric = metrics.GetProperty(name);
                        provider[name] = new(metric.GetProperty("used").ValueKind == JsonValueKind.Null ? null : Count(metric, "used"),
                            Count(metric, "limit", true), OptionalTime(metric, "period_start"), OptionalTime(metric, "period_end"),
                            OptionalTime(metric, "observed_at"), OptionalOperationsText(metric, "source", 120));
                    }
                    operations = new(Count(op, "registered_users"), Count(op, "database_bytes"), Count(op, "database_limit_bytes", true),
                        Count(op, "open_reports"), Count(op, "open_support"), provider);
                }
                if (isOperator != (operations is not null)) throw Unavailable();
                var warning = (int)Count(limits, "warning_percent", true);
                var duration = limits.GetProperty("max_duration_seconds").GetDouble();
                if (warning is < 1 or > 100 || !double.IsFinite(duration) || duration <= 0) throw Unavailable();
                return new CommunityUsage(own, total, Count(limits, "max_video_bytes", true), duration, warning, isOperator,
                    ReadTime(root, "checked_at"), operations);
            });
        }, cancellationToken);

    public Task<IReadOnlyList<CommunitySupportRequest>> GetSupportRequestsAsync(CancellationToken cancellationToken = default) =>
        ReadSupportAsync("community_support_requests", new { }, false, cancellationToken);

    public Task<CommunitySupportRequest> SubmitSupportRequestAsync(string category, string body, CancellationToken cancellationToken = default)
    {
        if (category is not ("help" or "privacy" or "deletion")) throw InvalidOperations();
        body = ValidateText(body, 10, 2000, "invalid_request", "Describe your request in 10–2,000 characters.", true);
        return RunMediaAsync(async (generation, token) =>
        {
            using var result = await MediaRpcAsync("community_submit_support", new { p_category = category, p_body = body }, generation, token);
            var saved = ParseOperations(result.RootElement, root => ParseSupport(root, false));
            if (saved.Category != category || saved.Body != body || saved.Status != "open") throw Unavailable();
            return saved;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<CommunitySupportRequest>> GetOperatorSupportAsync(CancellationToken cancellationToken = default) =>
        ReadSupportAsync("community_operator_support", new { p_limit = 25, p_before = (string?)null, p_before_id = (Guid?)null }, true, cancellationToken);

    private Task<IReadOnlyList<CommunitySupportRequest>> ReadSupportAsync(string rpc, object body, bool isOperator, CancellationToken cancellationToken) =>
        RunMediaAsync<IReadOnlyList<CommunitySupportRequest>>(async (generation, token) =>
        {
            using var result = await MediaRpcAsync(rpc, body, generation, token);
            return ParseOperations(result.RootElement, root =>
            {
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > 100) throw Unavailable();
                return root.EnumerateArray().Select(row => ParseSupport(row, isOperator)).ToArray();
            });
        }, cancellationToken);

    public Task<CommunitySupportRequest> ReplyToSupportAsync(Guid requestId, string reply, bool resolved, CancellationToken cancellationToken = default)
    {
        ValidateId(requestId);
        reply = ValidateText(reply, 1, 2000, "invalid_request", "Write a reply of 2,000 characters or fewer.", true);
        return RunMediaAsync(async (generation, token) =>
        {
            using var result = await MediaRpcAsync("community_operator_reply", new { p_request_id = requestId, p_reply = reply,
                p_status = resolved ? "resolved" : "open" }, generation, token);
            var saved = ParseOperations(result.RootElement, root => ParseSupport(root, true));
            if (saved.Id != requestId || saved.Reply != reply || saved.Status != (resolved ? "resolved" : "open")) throw Unavailable();
            return saved;
        }, cancellationToken);
    }

    public Task<IReadOnlyList<CommunityOperatorReport>> GetOperatorReportsAsync(CancellationToken cancellationToken = default) =>
        RunMediaAsync<IReadOnlyList<CommunityOperatorReport>>(async (generation, token) =>
        {
            using var result = await MediaRpcAsync("community_operator_reports", new { p_limit = 25, p_before = (string?)null, p_before_id = (Guid?)null }, generation, token);
            return ParseOperations(result.RootElement, root =>
            {
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > 25) throw Unavailable();
                return root.EnumerateArray().Select(ParseReport).ToArray();
            });
        }, cancellationToken);

    public Task<CommunitySupportPage> GetSupportRequestsPageAsync(CommunityFeedCursor? cursor = null,
        bool isOperator = false, CancellationToken cancellationToken = default)
    {
        ValidateCursor(cursor);
        return RunMediaAsync(async (generation, token) =>
        {
            using var result = await MediaRpcAsync(isOperator ? "community_operator_support" : "community_support_requests",
                new { p_limit = OperationsPageSize + 1, p_before = CursorTime(cursor), p_before_id = cursor?.Id }, generation, token);
            return ParseOperations(result.RootElement, root =>
            {
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > OperationsPageSize + 1) throw Unavailable();
                var rows = root.EnumerateArray().Select(row => ParseSupport(row, isOperator)).ToArray();
                ValidateDescending(rows.Select(row => new CommunityFeedCursor(row.CreatedAt, row.Id)), cursor);
                var items = rows.Take(OperationsPageSize).ToArray();
                return new CommunitySupportPage(items, rows.Length > items.Length
                    ? new CommunityFeedCursor(items[^1].CreatedAt, items[^1].Id) : null);
            });
        }, cancellationToken);
    }

    public Task<CommunityReportsPage> GetOperatorReportsPageAsync(CommunityFeedCursor? cursor = null,
        CancellationToken cancellationToken = default)
    {
        ValidateCursor(cursor);
        return RunMediaAsync(async (generation, token) =>
        {
            using var result = await MediaRpcAsync("community_operator_reports",
                new { p_limit = OperationsPageSize + 1, p_before = CursorTime(cursor), p_before_id = cursor?.Id }, generation, token);
            return ParseOperations(result.RootElement, root =>
            {
                if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > OperationsPageSize + 1) throw Unavailable();
                var rows = root.EnumerateArray().Select(ParseReport).ToArray();
                ValidateDescending(rows.Select(row => new CommunityFeedCursor(row.CreatedAt, row.Id)), cursor);
                var items = rows.Take(OperationsPageSize).ToArray();
                return new CommunityReportsPage(items, rows.Length > items.Length
                    ? new CommunityFeedCursor(items[^1].CreatedAt, items[^1].Id) : null);
            });
        }, cancellationToken);
    }

    public Task ReviewReportedClipAsync(Guid clipId, bool hide, string note, CancellationToken cancellationToken = default)
    {
        ValidateId(clipId);
        note = ValidateText(note, 1, 1000, "invalid_review", "Explain your review in 1,000 characters or fewer.", true);
        return RunMediaAsync(async (generation, token) =>
        {
            using var result = await MediaRpcAsync("community_operator_review", new { p_clip_id = clipId, p_action = hide ? "hide" : "keep", p_note = note }, generation, token);
            RequireTrue(result.RootElement, "reviewed");
            return true;
        }, cancellationToken);
    }

    public Task DeleteAccountAsync(string confirmation, CancellationToken cancellationToken = default)
    {
        var expected = string.IsNullOrWhiteSpace(Profile?.Username) ? "DELETE" : Profile.Username;
        if (confirmation != expected) throw new CommunityAccountException("confirmation_required", "Type your username exactly to confirm account deletion.");
        return RunMediaAsync(async (generation, token) =>
        {
            var session = await EnsureSessionAsync(generation, token);
            // Re-read the self profile while holding the account gate; a stale draft
            // or a changed username must never confirm a destructive request.
            using var profileResult = await SendAuthenticatedAsync(HttpMethod.Get,
                $"rest/v1/profiles?select={ProfileFields}&id=eq.{session.User!.Id:D}&limit=1", session, null, token);
            var currentProfile = ParseProfileList(profileResult.RootElement, session.User.Id);
            EnsureCurrent(generation);
            Profile = currentProfile;
            var currentExpected = string.IsNullOrWhiteSpace(currentProfile.Username) ? "DELETE" : currentProfile.Username;
            if (!string.Equals(confirmation, currentExpected, StringComparison.Ordinal))
                throw new CommunityAccountException("confirmation_required", "Your username changed. Type your current username exactly to confirm account deletion.");
            using var result = await SendAuthenticatedAsync(HttpMethod.Post, "functions/v1/community-delete-account", session, new { confirmation }, token);
            RequireTrue(result.RootElement, "deleted");
            EnsureCurrent(generation);
            // Acknowledged deletion clears protected credentials even when the view has since closed.
            await ClearSessionAsync(CancellationToken.None);
            return true;
        }, cancellationToken);
    }

    private static long Count(JsonElement row, string name, bool positive = false)
    {
        var value = row.GetProperty(name).GetInt64();
        if (value < (positive ? 1 : 0)) throw Unavailable();
        return value;
    }
    private static DateTimeOffset? OptionalTime(JsonElement row, string name) =>
        !row.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null : ReadTime(row, name);
    private static T ParseOperations<T>(JsonElement root, Func<JsonElement, T> parse)
    {
        try { return parse(root); }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException) { throw Unavailable(); }
    }
    private static CommunitySupportRequest ParseSupport(JsonElement row, bool isOperator)
    {
        var category = ReadText(row, "category", 20); var status = ReadText(row, "status", 20);
        if (category is not ("help" or "privacy" or "deletion") || status is not ("open" or "resolved")) throw Unavailable();
        return new(ReadId(row, "id"), category, ReadText(row, "body", 2000), status, ReadTime(row, "created_at"),
            OptionalOperationsText(row, "reply", 2000) ?? "", ReadTime(row, "updated_at"), isOperator ? ReadId(row, "owner_id") : null,
            isOperator ? OptionalOperationsText(row, "username", 24) : null);
    }
    private static CommunityOperatorReport ParseReport(JsonElement row)
    {
        var status = ReadText(row, "status", 20);
        if (status is not ("open" or "resolved")) throw Unavailable();
        return new(ReadId(row, "id"), ReadId(row, "clip_id"), ReadId(row, "reporter_id"), ReadText(row, "reason", 500), status,
            ReadTime(row, "created_at"), ReadText(row, "title", 120), OptionalOperationsText(row, "owner_username", 24) ?? "",
            OptionalOperationsText(row, "reporter_username", 24) ?? "", row.GetProperty("hidden").GetBoolean(),
            OptionalOperationsText(row, "review_note", 1000) ?? "", OptionalTime(row, "reviewed_at"));
    }
    private static string? OptionalOperationsText(JsonElement row, string field, int maximum) =>
        !row.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null ? null : ReadText(row, field, maximum, allowEmpty: true);
    private static CommunityAccountException InvalidOperations() => new("invalid_request", "Check your request and try again.");
}
