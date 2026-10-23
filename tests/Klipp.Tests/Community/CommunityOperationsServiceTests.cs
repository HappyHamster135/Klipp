using System.Net;
using System.Text;
using System.Text.Json;
using Klipp.Core.Community;

namespace Klipp.Tests.Community;

public sealed class CommunityOperationsServiceTests
{
    private static readonly Guid Owner = Guid.Parse("df9cbe32-3354-4101-bb0e-4b994a0fe087");
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public async Task Capabilities_are_obtained_from_authenticated_backend_and_unknown_versions_remain_disabled()
    {
        using var fixture = await Fixture.SignedIn();
        fixture.Handler.Reply = _ => Json("{\"enabled\":true,\"version\":1,\"account_deletion\":true}");
        var capabilities = await fixture.Service.GetOperationsCapabilitiesAsync();
        capabilities.Enabled.ShouldBeTrue(); capabilities.AccountDeletionEnabled.ShouldBeTrue();
        var request = fixture.Handler.Requests.Last();
        request.Path.ShouldBe("/rest/v1/rpc/community_operations_capabilities");
        request.Authorization.ShouldBe("Bearer access-test");
        request.ApiKey.ShouldBe("sb_publishable_testpublickey123456789");
        request.Body.ShouldNotContain("refresh-test");
        fixture.Handler.Reply = _ => Json("{\"enabled\":true,\"version\":2,\"account_deletion\":true}");
        (await fixture.Service.GetOperationsCapabilitiesAsync()).Enabled.ShouldBeFalse();
    }

    [Fact]
    public async Task An_operator_flag_without_verified_operations_data_is_rejected()
    {
        using var fixture = await Fixture.SignedIn();
        fixture.Handler.Reply = _ => Json(Usage(isOperator: true));
        (await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetUsageAsync())).Code.ShouldBe("unavailable");
    }

    [Fact]
    public async Task Member_usage_shows_the_actual_capacity_without_granting_operator_controls()
    {
        using var fixture = await Fixture.SignedIn();
        fixture.Handler.Reply = _ => Json(Usage());
        var usage = await fixture.Service.GetUsageAsync();
        usage.Me.SlotsLimit.ShouldBe(5); usage.Beta.SlotsLimit.ShouldBe(40);
        usage.UploadsFull.ShouldBeTrue(); usage.NearLimit.ShouldBeTrue();
        usage.UploadWarning.ShouldContain("My uploads");
        usage.IsOperator.ShouldBeFalse(); usage.Operations.ShouldBeNull();
    }

    [Fact]
    public void Provider_measurement_is_unknown_when_missing_stale_or_outside_its_reporting_period()
    {
        var measurement = new CommunityProviderUsage(null, 100, Now.AddDays(-1), Now.AddDays(1), Now, "provider");
        measurement.IsCurrent(Now).ShouldBeFalse();
        (measurement with { Used = 0 }).IsCurrent(Now).ShouldBeTrue();
        (measurement with { Used = 50, ObservedAt = Now.AddHours(-25) }).IsCurrent(Now).ShouldBeFalse();
        (measurement with { Used = 50, PeriodEnd = Now }).IsCurrent(Now).ShouldBeFalse();
    }

    [Fact]
    public async Task Report_paging_uses_an_exclusive_tuple_cursor_without_losing_the_boundary_row()
    {
        using var fixture = await Fixture.SignedIn();
        var rows = Enumerable.Range(0, 21).Select(index => Report(index, Now.AddMinutes(-index))).ToArray();
        fixture.Handler.Reply = _ => Json("[" + string.Join(',', rows) + "]");
        var page = await fixture.Service.GetOperatorReportsPageAsync();
        page.Items.Count.ShouldBe(20); page.NextCursor.ShouldNotBeNull();
        page.NextCursor!.Id.ShouldBe(page.Items[^1].Id);
        fixture.Handler.Reply = request =>
        {
            using var body = JsonDocument.Parse(request.Body);
            body.RootElement.GetProperty("p_limit").GetInt32().ShouldBe(21);
            body.RootElement.GetProperty("p_before_id").GetGuid().ShouldBe(page.Items[^1].Id);
            return Json("[" + rows[^1] + "]");
        };
        var older = await fixture.Service.GetOperatorReportsPageAsync(page.NextCursor);
        older.Items.Single().CreatedAt.ShouldBe(Now.AddMinutes(-20)); older.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Duplicate_or_reordered_operator_reports_are_rejected()
    {
        using var fixture = await Fixture.SignedIn();
        var row = Report(0, Now);
        fixture.Handler.Reply = _ => Json($"[{row},{row}]");
        (await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetOperatorReportsPageAsync())).Code.ShouldBe("unavailable");
    }

    [Fact]
    public async Task Self_support_is_paged_and_rejects_unknown_categories()
    {
        using var fixture = await Fixture.SignedIn();
        fixture.Handler.Reply = request =>
        {
            request.Path.ShouldBe("/rest/v1/rpc/community_support_requests");
            using var body = JsonDocument.Parse(request.Body); body.RootElement.GetProperty("p_limit").GetInt32().ShouldBe(21);
            return Json("[" + Support() + "]");
        };
        (await fixture.Service.GetSupportRequestsPageAsync()).Items.Single().Category.ShouldBe("help");
        fixture.Handler.Reply = _ => Json("[" + Support(category: "arbitrary") + "]");
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetSupportRequestsPageAsync());
    }

    [Fact]
    public async Task Support_validation_prevents_empty_short_and_invalid_topics_before_network()
    {
        using var fixture = await Fixture.SignedIn();
        var before = fixture.Handler.Requests.Count;
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.SubmitSupportRequestAsync("help", "short"));
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.SubmitSupportRequestAsync("arbitrary", "A long enough request"));
        fixture.Handler.Requests.Count.ShouldBe(before);
    }

    [Fact]
    public async Task A_support_response_for_different_submitted_text_is_rejected()
    {
        using var fixture = await Fixture.SignedIn();
        fixture.Handler.Reply = _ => Json(Support(body: "Other request text"));
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.SubmitSupportRequestAsync("help", "My request details"));
    }

    [Fact]
    public async Task A_reply_for_another_request_cannot_replace_the_selected_request()
    {
        using var fixture = await Fixture.SignedIn();
        fixture.Handler.Reply = _ => Json(Support());
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.ReplyToSupportAsync(Guid.NewGuid(), "A private reply", true));
    }

    [Fact]
    public async Task Sign_out_cancels_an_inflight_operations_page_before_exposing_its_results()
    {
        using var fixture = await Fixture.SignedIn();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.AsyncReply = async (request, token) =>
        {
            if (request.Path == "/auth/v1/logout?scope=local") return Json("{}");
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("[]");
        };
        var page = fixture.Service.GetOperatorReportsPageAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Service.SignOutAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await page);
        fixture.Store.Session.ShouldBeNull(); fixture.Service.IsSignedIn.ShouldBeFalse();
    }

    [Fact]
    public async Task Account_deletion_requires_exact_confirmation_and_fresh_self_profile_before_edge_call()
    {
        using var fixture = await Fixture.SignedIn(profile: true);
        var before = fixture.Handler.Requests.Count;
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.DeleteAccountAsync("PLAYER"));
        fixture.Handler.Requests.Count.ShouldBe(before);
        fixture.Handler.Reply = request => request.Path.StartsWith("/rest/v1/profiles", StringComparison.Ordinal)
            ? Json(Profile("new_name")) : throw new InvalidOperationException("Edge deletion must not be called");
        var error = await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.DeleteAccountAsync("player"));
        error.Code.ShouldBe("confirmation_required"); fixture.Service.IsSignedIn.ShouldBeTrue();
        fixture.Store.Clears.ShouldBe(0);
    }

    [Theory]
    [InlineData("{\"deleted\":false}", 200, "unavailable")]
    [InlineData("{\"code\":\"KO004\"}", 409, "deletion_pending")]
    [InlineData("{\"message\":\"private failure\"}", 503, "unavailable")]
    public async Task Failed_or_pending_account_deletion_preserves_credentials_for_retry(string response, int status, string expectedCode)
    {
        using var fixture = await Fixture.SignedIn(profile: true);
        fixture.Handler.Reply = request => request.Path.StartsWith("/rest/v1/profiles", StringComparison.Ordinal)
            ? Json(Profile()) : Json(response, (HttpStatusCode)status);
        var error = await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.DeleteAccountAsync("player"));
        error.Code.ShouldBe(expectedCode); error.Message.ShouldNotContain("private failure");
        fixture.Service.IsSignedIn.ShouldBeTrue(); fixture.Store.Session.ShouldNotBeNull(); fixture.Store.Clears.ShouldBe(0);
    }

    [Fact]
    public async Task Only_acknowledged_account_deletion_clears_the_saved_session()
    {
        using var fixture = await Fixture.SignedIn(profile: true);
        fixture.Handler.Reply = request =>
        {
            if (request.Path.StartsWith("/rest/v1/profiles", StringComparison.Ordinal)) return Json(Profile());
            request.Path.ShouldBe("/functions/v1/community-delete-account");
            using var body = JsonDocument.Parse(request.Body); body.RootElement.EnumerateObject().Count().ShouldBe(1);
            body.RootElement.GetProperty("confirmation").GetString().ShouldBe("player");
            return Json("{\"deleted\":true}");
        };
        await fixture.Service.DeleteAccountAsync("player");
        fixture.Service.IsSignedIn.ShouldBeFalse(); fixture.Service.Profile.ShouldBeNull();
        fixture.Store.Session.ShouldBeNull(); fixture.Store.Clears.ShouldBe(1);
    }

    [Fact]
    public async Task Fresh_deletion_profile_for_another_user_never_reaches_the_edge()
    {
        using var fixture = await Fixture.SignedIn(profile: true);
        fixture.Handler.Reply = request => request.Path.StartsWith("/rest/v1/profiles", StringComparison.Ordinal)
            ? Json(Profile(id: Guid.NewGuid())) : throw new InvalidOperationException("Must not reach Edge");
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.DeleteAccountAsync("player"));
        fixture.Store.Clears.ShouldBe(0);
    }

    private static string Profile(string username = "player", Guid? id = null) => JsonSerializer.Serialize(new[]
    { new { id = id ?? Owner, username, display_name = "Player", bio = "" } });
    private static string Usage(bool isOperator = false) => JsonSerializer.Serialize(new
    {
        me = new { slots_used = 5, slots_limit = 5, published = 4, uploading = 0, deleting = 1, stored_bytes = 4096 },
        beta = new { slots_used = 35, slots_limit = 40, reserved_bytes = 10000, storage_bytes = 8000, storage_limit_bytes = 100000, unknown_storage_objects = 0 },
        limits = new { warning_percent = 80, max_video_bytes = 16 * 1024 * 1024, max_duration_seconds = 60 },
        @operator = isOperator, operations = (object?)null, checked_at = Now
    });
    private static string Report(int index, DateTimeOffset created) => JsonSerializer.Serialize(new
    {
        id = Guid.Parse($"10000000-0000-0000-0000-{index + 1:000000000000}"), clip_id = Guid.NewGuid(), reporter_id = Owner,
        reason = "Please review this clip", status = "open", created_at = created, title = "Shared clip",
        owner_username = "member", reporter_username = "player", hidden = false, review_note = "", reviewed_at = (DateTimeOffset?)null
    });
    private static string Support(string category = "help", string body = "My request details") => JsonSerializer.Serialize(new
    {
        id = Guid.NewGuid(), category, body, status = "open", created_at = Now, updated_at = Now, reply = ""
    });
    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed record Request(string Path, string Body, string? Authorization, string? ApiKey);
    private sealed class Handler : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        public Func<Request, HttpResponseMessage> Reply { get; set; } = _ => throw new InvalidOperationException("Unexpected request");
        public Func<Request, CancellationToken, Task<HttpResponseMessage>>? AsyncReply { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
        {
            var request = new Request(message.RequestUri!.PathAndQuery,
                message.Content is null ? "" : await message.Content.ReadAsStringAsync(cancellationToken),
                message.Headers.Authorization?.ToString(), message.Headers.TryGetValues("apikey", out var keys) ? keys.Single() : null);
            Requests.Add(request); return AsyncReply is null ? Reply(request) : await AsyncReply(request, cancellationToken);
        }
    }
    private sealed class Store : ICommunitySessionStore
    {
        public CommunitySession? Session { get; private set; }
        public int Clears { get; private set; }
        public Task<CommunitySession?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Session);
        public Task SaveAsync(CommunitySession session, CancellationToken cancellationToken = default) { Session = session; return Task.CompletedTask; }
        public Task ClearAsync(CancellationToken cancellationToken = default) { Clears++; Session = null; return Task.CompletedTask; }
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Fixture : IDisposable
    {
        public Handler Handler { get; } = new(); public Store Store { get; } = new();
        public CommunityAccountService Service { get; }
        private readonly HttpClient _http;
        private Fixture()
        {
            _http = new(Handler);
            Service = new(new("https://community.example.com", "sb_publishable_testpublickey123456789"), _http, Store, new Clock());
        }
        public static async Task<Fixture> SignedIn(bool profile = false)
        {
            var fixture = new Fixture();
            fixture.Handler.Reply = _ => Json(JsonSerializer.Serialize(new
            {
                access_token = "access-test", refresh_token = "refresh-test", expires_at = Now.AddHours(1).ToUnixTimeSeconds(),
                user = new { id = Owner, email = "player@example.com" }
            }));
            await fixture.Service.VerifyEmailCodeAsync("player@example.com", "123456");
            if (profile) { fixture.Handler.Reply = _ => Json(Profile()); await fixture.Service.GetProfileAsync(); }
            return fixture;
        }
        public void Dispose() { Service.Dispose(); _http.Dispose(); }
    }
}
