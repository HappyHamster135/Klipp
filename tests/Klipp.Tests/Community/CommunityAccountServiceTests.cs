using System.Net;
using System.Text;
using System.Text.Json;
using Klipp.Core.Community;

namespace Klipp.Tests.Community;

public sealed class CommunityAccountServiceTests
{
    private static readonly Guid UserId = Guid.Parse("161cd682-c82e-4eac-8776-4ecdf5ef13a0");
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private static CommunityConfiguration Configuration => new("https://community.example.com", "sb_publishable_testpublickey123456789");

    [Fact]
    public void Construction_never_reads_credentials_or_starts_network_work()
    {
        var fixture = new Fixture();
        fixture.Handler.Requests.Count.ShouldBe(0);
        fixture.Store.Reads.ShouldBe(0);
        fixture.Service.IsSignedIn.ShouldBeFalse();
    }

    [Fact]
    public async Task Unconfigured_media_never_sends_a_request()
    {
        var fixture = new Fixture(configuration: new());
        var error = await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.RestoreSessionAsync());
        error.Code.ShouldBe("not_configured");
        fixture.Handler.Requests.ShouldBeEmpty();
        fixture.Store.Reads.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Email_flow_preserves_create_account_choice_without_sending_credentials(bool create)
    {
        var fixture = new Fixture();
        fixture.Handler.Replies.Enqueue(_ => Json("{}"));
        await fixture.Service.SendEmailCodeAsync(" player@example.com ", create);
        var request = fixture.Handler.Requests.Single();
        request.Path.ShouldBe("/auth/v1/otp");
        request.Authorization.ShouldBeNull();
        request.ApiKey.ShouldBe(Configuration.PublishableKey);
        using var body = JsonDocument.Parse(request.Body!);
        body.RootElement.GetProperty("email").GetString().ShouldBe("player@example.com");
        body.RootElement.GetProperty("create_user").GetBoolean().ShouldBe(create);
    }

    [Fact]
    public async Task Verification_saves_a_session_and_only_sends_the_otp_to_the_auth_endpoint()
    {
        var fixture = new Fixture();
        await fixture.SignInAsync();
        fixture.Service.User!.Id.ShouldBe(UserId);
        fixture.Service.IsSignedIn.ShouldBeTrue();
        fixture.Store.Session!.RefreshToken.ShouldBe("refresh-secret");
        fixture.Store.Session.ToString().ShouldBe("CommunitySession");
        using var body = JsonDocument.Parse(fixture.Handler.Requests.Single().Body!);
        body.RootElement.GetProperty("type").GetString().ShouldBe("email");
        body.RootElement.GetProperty("token").GetString().ShouldBe("123456");
    }

    [Fact]
    public async Task Restore_checks_the_server_before_exposing_a_cached_user()
    {
        var fixture = new Fixture();
        fixture.Store.Session = Session();
        fixture.Handler.Replies.Enqueue(_ => Json(UserJson()));
        await fixture.Service.RestoreSessionAsync();
        var request = fixture.Handler.Requests.Single();
        request.Path.ShouldBe("/auth/v1/user");
        request.Authorization.ShouldBe("Bearer access-secret");
        fixture.Service.IsSignedIn.ShouldBeTrue();
    }

    [Fact]
    public async Task Offline_restore_does_not_trust_cached_identity_or_delete_retryable_credentials()
    {
        var fixture = new Fixture();
        fixture.Store.Session = Session();
        fixture.Handler.Replies.Enqueue(_ => throw new HttpRequestException("private host error"));
        var error = await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.RestoreSessionAsync());
        error.Code.ShouldBe("offline");
        error.Message.ShouldNotContain("private host");
        fixture.Service.IsSignedIn.ShouldBeFalse();
        fixture.Store.Session.ShouldNotBeNull();
        fixture.Handler.Replies.Enqueue(_ => Json(UserJson()));
        await fixture.Service.RestoreSessionAsync();
        fixture.Service.IsSignedIn.ShouldBeTrue();
    }

    [Fact]
    public async Task Expiring_session_refreshes_once_and_rotates_the_persisted_refresh_token()
    {
        var fixture = new Fixture();
        fixture.Store.Session = Session(expires: Now.AddSeconds(10));
        fixture.Handler.Replies.Enqueue(_ => Json(SessionJson("access-new", "refresh-new")));
        fixture.Handler.Replies.Enqueue(_ => Json(UserJson()));
        await fixture.Service.RestoreSessionAsync();
        var refresh = fixture.Handler.Requests[0];
        refresh.Path.ShouldBe("/auth/v1/token?grant_type=refresh_token");
        refresh.Authorization.ShouldBeNull();
        using var body = JsonDocument.Parse(refresh.Body!);
        body.RootElement.GetProperty("refresh_token").GetString().ShouldBe("refresh-secret");
        fixture.Handler.Requests[1].Authorization.ShouldBe("Bearer access-new");
        fixture.Store.Session!.RefreshToken.ShouldBe("refresh-new");
    }

    [Fact]
    public async Task Revoked_saved_session_becomes_signed_out_without_blocking_local_use()
    {
        var fixture = new Fixture();
        fixture.Store.Session = Session();
        fixture.Handler.Replies.Enqueue(_ => Json("{\"message\":\"private error\"}", HttpStatusCode.Unauthorized));
        await fixture.Service.RestoreSessionAsync();
        fixture.Service.IsSignedIn.ShouldBeFalse();
        fixture.Store.Session.ShouldBeNull();
    }

    [Fact]
    public async Task Profile_reads_and_updates_use_user_token_owner_filter_and_an_explicit_field_list()
    {
        var fixture = new Fixture();
        await fixture.SignInAsync();
        fixture.Handler.Replies.Enqueue(_ => Json(ProfileJson(username: null)));
        (await fixture.Service.GetProfileAsync())!.IsComplete.ShouldBeFalse();
        fixture.Handler.Replies.Enqueue(_ => Json(ProfileJson()));
        var saved = await fixture.Service.SaveProfileAsync(" PLAYER_1 ", " Player ", " Hello ");
        saved.Username.ShouldBe("player_1");
        var get = fixture.Handler.Requests[1];
        get.Path.ShouldBe($"/rest/v1/profiles?select=id,username,display_name,bio&id=eq.{UserId:D}&limit=1");
        get.Authorization.ShouldBe("Bearer access-secret");
        var patch = fixture.Handler.Requests[2];
        patch.Method.ShouldBe("PATCH");
        patch.Path.ShouldNotContain("limit=");
        patch.Prefer.ShouldBe("return=representation");
        using var body = JsonDocument.Parse(patch.Body!);
        body.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["username", "display_name", "bio"]);
        body.RootElement.GetProperty("username").GetString().ShouldBe("player_1");
        body.RootElement.GetProperty("display_name").GetString().ShouldBe("Player");
        body.RootElement.GetProperty("bio").GetString().ShouldBe("Hello");
    }

    [Fact]
    public async Task A_missing_profile_does_not_grant_a_client_insert_fallback()
    {
        var fixture = new Fixture();
        await fixture.SignInAsync();
        fixture.Handler.Replies.Enqueue(_ => Json("[]"));
        var error = await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetProfileAsync());
        error.Code.ShouldBe("profile_unavailable");
        fixture.Handler.Requests.Count.ShouldBe(2);
        fixture.Service.Profile.ShouldBeNull();
    }

    [Fact]
    public async Task A_profile_for_another_identity_is_rejected()
    {
        var fixture = new Fixture();
        await fixture.SignInAsync();
        fixture.Handler.Replies.Enqueue(_ => Json(ProfileJson(id: Guid.NewGuid())));
        await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetProfileAsync());
        fixture.Service.Profile.ShouldBeNull();
    }

    [Fact]
    public async Task Username_conflicts_are_safe_and_do_not_expose_server_details()
    {
        var fixture = new Fixture();
        await fixture.SignInAsync();
        fixture.Handler.Replies.Enqueue(_ => Json("{\"code\":\"23505\",\"details\":\"access-secret private database table\"}", HttpStatusCode.Conflict));
        var error = await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.SaveProfileAsync("player", "Player", ""));
        error.Code.ShouldBe("username_taken");
        error.Message.ShouldNotContain("private");
        error.Message.ShouldNotContain("secret");
    }

    [Fact]
    public async Task Signout_clears_current_and_saved_identity_even_when_remote_revocation_fails()
    {
        var fixture = new Fixture();
        await fixture.SignInAsync();
        fixture.Handler.Replies.Enqueue(_ => throw new HttpRequestException());
        await fixture.Service.SignOutAsync();
        fixture.Service.IsSignedIn.ShouldBeFalse();
        fixture.Service.Profile.ShouldBeNull();
        fixture.Store.Session.ShouldBeNull();
        fixture.Handler.Requests.Last().Path.ShouldBe("/auth/v1/logout?scope=local");
    }

    [Fact]
    public async Task Late_verification_cannot_resurrect_a_signed_out_account()
    {
        var fixture = new Fixture();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.AsyncReply = async (_, _) => { started.SetResult(); return await reply.Task; };
        var verification = fixture.Service.VerifyEmailCodeAsync("player@example.com", "123456");
        await started.Task;
        var signout = fixture.Service.SignOutAsync();
        fixture.Service.IsSignedIn.ShouldBeFalse();
        reply.SetResult(Json(SessionJson()));
        await Should.ThrowAsync<OperationCanceledException>(() => verification);
        await signout;
        fixture.Store.Session.ShouldBeNull();
        fixture.Service.IsSignedIn.ShouldBeFalse();
    }

    [Fact]
    public async Task Failed_signout_cleanup_remains_recoverable_and_blocks_media_until_retried()
    {
        var fixture = new Fixture();
        await fixture.SignInAsync();
        fixture.Store.FailClear = true;
        var error = await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.SignOutAsync());
        error.Code.ShouldBe("session_storage");
        fixture.Service.IsSignedIn.ShouldBeFalse();
        fixture.Service.HasPendingSignOut.ShouldBeTrue();
        fixture.Store.Session.ShouldNotBeNull();
        var blocked = await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetProfileAsync());
        blocked.Code.ShouldBe("signout_pending");
        fixture.Handler.Requests.Count.ShouldBe(1);
        fixture.Store.FailClear = false;
        await fixture.Service.SignOutAsync();
        fixture.Service.HasPendingSignOut.ShouldBeFalse();
        fixture.Store.Session.ShouldBeNull();
        await fixture.Service.RestoreSessionAsync();
        fixture.Service.IsSignedIn.ShouldBeFalse();
    }

    [Fact]
    public async Task Cancelling_verification_during_the_credential_write_does_not_leave_a_hidden_signin()
    {
        var fixture = new Fixture();
        using var source = new CancellationTokenSource();
        fixture.Store.OnSaved = source.Cancel;
        fixture.Handler.Replies.Enqueue(_ => Json(SessionJson()));
        await Should.ThrowAsync<OperationCanceledException>(() => fixture.Service.VerifyEmailCodeAsync("player@example.com", "123456", source.Token));
        fixture.Service.IsSignedIn.ShouldBeFalse();
        fixture.Store.Session.ShouldBeNull();
    }

    [Fact]
    public async Task Rate_limits_and_malformed_responses_do_not_copy_server_payloads_to_the_ui()
    {
        var fixture = new Fixture();
        fixture.Handler.Replies.Enqueue(_ => Json("private secret upstream response", HttpStatusCode.TooManyRequests));
        (await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.SendEmailCodeAsync("player@example.com", false)))
            .Code.ShouldBe("rate_limited");
        fixture.Handler.Replies.Enqueue(_ => Json("not json private secret"));
        var error = await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.VerifyEmailCodeAsync("player@example.com", "123456"));
        error.Message.ShouldNotContain("secret");
        fixture.Service.IsSignedIn.ShouldBeFalse();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"private server detail\"")]
    public async Task Valid_json_error_payloads_with_the_wrong_shape_remain_safe(string response)
    {
        var fixture = new Fixture();
        fixture.Handler.Replies.Enqueue(_ => Json(response, HttpStatusCode.BadGateway));
        var error = await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.SendEmailCodeAsync("player@example.com", false));
        error.Code.ShouldBe("unavailable");
        error.Message.ShouldNotContain("private");
    }

    [Fact]
    public async Task Malformed_token_headers_are_rejected_before_persistence_or_authenticated_requests()
    {
        var fixture = new Fixture();
        fixture.Handler.Replies.Enqueue(_ => Json(SessionJson().Replace("access-secret", "access-secret\\r\\nforged-header")));
        var error = await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.VerifyEmailCodeAsync("player@example.com", "123456"));
        error.Code.ShouldBe("unavailable");
        fixture.Store.Session.ShouldBeNull();
    }

    [Fact]
    public async Task Signed_out_users_cannot_fetch_or_save_profiles()
    {
        var fixture = new Fixture();
        (await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.GetProfileAsync())).Code.ShouldBe("signed_out");
        (await Should.ThrowAsync<CommunityAccountException>(() => fixture.Service.SaveProfileAsync("player", "Player", ""))).Code.ShouldBe("signed_out");
        fixture.Handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancellation_preserves_credentials_without_exposing_a_cached_identity()
    {
        var fixture = new Fixture();
        fixture.Store.Session = Session();
        using var cancellation = new CancellationTokenSource();
        fixture.Handler.AsyncReply = async (_, token) => { cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); return Json("{}"); };
        await Should.ThrowAsync<OperationCanceledException>(() => fixture.Service.RestoreSessionAsync(cancellation.Token));
        fixture.Service.IsSignedIn.ShouldBeFalse();
        fixture.Store.Session.ShouldNotBeNull();
    }

    private static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };

    private static string UserJson(Guid? id = null) => JsonSerializer.Serialize(new { id = id ?? UserId, email = "player@example.com" });
    private static string SessionJson(string access = "access-secret", string refresh = "refresh-secret") =>
        $"{{\"access_token\":\"{access}\",\"refresh_token\":\"{refresh}\",\"expires_at\":{Now.AddHours(1).ToUnixTimeSeconds()},\"user\":{UserJson()}}}";
    private static CommunitySession Session(DateTimeOffset? expires = null) => new()
    {
        AccessToken = "access-secret", RefreshToken = "refresh-secret", ExpiresAt = expires ?? Now.AddHours(1),
        User = new CommunityUser(UserId, "player@example.com")
    };
    private static string ProfileJson(string? username = "player_1", Guid? id = null) =>
        JsonSerializer.Serialize(new[] { new { id = id ?? UserId, username, display_name = "Player", bio = "Hello" } });

    private sealed class Fixture
    {
        public Handler Handler { get; } = new();
        public Store Store { get; } = new();
        public CommunityAccountService Service { get; }
        public Fixture(CommunityConfiguration? configuration = null) =>
            Service = new(configuration ?? Configuration, new HttpClient(Handler), Store, new Clock());
        public Task SignInAsync()
        {
            Handler.Replies.Enqueue(_ => Json(SessionJson()));
            return Service.VerifyEmailCodeAsync("player@example.com", "123456");
        }
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Store : ICommunitySessionStore
    {
        public CommunitySession? Session { get; set; }
        public int Reads { get; private set; }
        public bool FailClear { get; set; }
        public Action? OnSaved { get; set; }
        public Task<CommunitySession?> LoadAsync(CancellationToken cancellationToken = default) { Reads++; return Task.FromResult(Session); }
        public Task SaveAsync(CommunitySession session, CancellationToken cancellationToken = default)
        {
            Session = session;
            OnSaved?.Invoke();
            return Task.CompletedTask;
        }
        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            if (FailClear) throw new IOException("private storage path");
            Session = null;
            return Task.CompletedTask;
        }
    }
    private sealed record Request(string Method, string Path, string? Authorization, string? ApiKey, string? Prefer, string? Body);
    private sealed class Handler : HttpMessageHandler
    {
        public List<Request> Requests { get; } = [];
        public Queue<Func<Request, HttpResponseMessage>> Replies { get; } = new();
        public Func<Request, CancellationToken, Task<HttpResponseMessage>>? AsyncReply { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var snapshot = new Request(request.Method.Method, request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString(), request.Headers.TryGetValues("apikey", out var keys) ? keys.Single() : null,
                request.Headers.TryGetValues("Prefer", out var prefer) ? prefer.Single() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(snapshot);
            return AsyncReply is null ? Replies.Dequeue()(snapshot) : await AsyncReply(snapshot, cancellationToken);
        }
    }
}
