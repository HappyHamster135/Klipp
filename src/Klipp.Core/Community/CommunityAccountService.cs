using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Klipp.Core.Community;

/// <summary>
/// Optional online identity. Nothing here starts work until a caller explicitly invokes an operation.
/// Requests and persisted credential writes are serialized; signing out fences every pending operation.
/// </summary>
public sealed partial class CommunityAccountService : IDisposable
{
    private readonly CommunityConfiguration _configuration;
    private readonly HttpClient _http;
    private readonly ICommunitySessionStore _store;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CommunitySession? _session;
    private bool _restored;
    private int _generation;
    private bool _disposed;
    private bool _signOutPending;
    private CancellationTokenSource _mediaCancellation = new();
    private const string ProfileFields = "id,username,display_name,bio";

    public CommunityAccountService(CommunityConfiguration configuration, HttpClient httpClient,
        ICommunitySessionStore sessionStore, TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _http = httpClient;
        _store = sessionStore;
        _time = timeProvider ?? TimeProvider.System;
    }

    public bool IsConfigured => _configuration.IsConfigured;
    public bool IsSignedIn => _session?.User is not null;
    public bool HasPendingSignOut => _signOutPending;
    public CommunityUser? User => _session?.User;
    public CommunityProfile? Profile { get; private set; }

    public async Task SendEmailCodeAsync(string email, bool createAccount, CancellationToken cancellationToken = default)
    {
        email = CommunityProfileValidation.NormalizeEmail(email);
        var generation = Volatile.Read(ref _generation);
        await RunAsync(generation, cancellationToken, async () =>
        {
            // Supabase intentionally uses a generic result rather than revealing whether the address exists.
            using var result = await SendAsync(HttpMethod.Post, "auth/v1/otp", null,
                new { email, create_user = createAccount }, cancellationToken);
            EnsureCurrent(generation);
        });
    }

    public async Task VerifyEmailCodeAsync(string email, string code, CancellationToken cancellationToken = default)
    {
        email = CommunityProfileValidation.NormalizeEmail(email);
        code = code.Trim();
        if (code.Length is < 6 or > 10 || code.Any(c => c is < '0' or > '9'))
            throw new CommunityAccountException("invalid_code", "Enter the code from your email.");
        var generation = Volatile.Read(ref _generation);
        await RunAsync(generation, cancellationToken, async () =>
        {
            using var result = await SendAsync(HttpMethod.Post, "auth/v1/verify", null,
                new { email, token = code, type = "email" }, cancellationToken);
            var session = ParseSession(result.RootElement);
            EnsureCurrent(generation);
            await SaveSessionAsync(session, cancellationToken);
            EnsureCurrent(generation);
            if (cancellationToken.IsCancellationRequested)
            {
                // Cancelling the sign-in screen during the atomic write must not leave a hidden sign-in behind.
                await ClearSessionAsync(CancellationToken.None);
                cancellationToken.ThrowIfCancellationRequested();
            }
            _session = session;
            Profile = null;
            _restored = true;
        });
    }

    public async Task RestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        var generation = Volatile.Read(ref _generation);
        await RunAsync(generation, cancellationToken, async () =>
        {
            if (_restored) return;
            CommunitySession? cached;
            try { cached = await _store.LoadAsync(cancellationToken); }
            catch (OperationCanceledException) { throw; }
            catch { throw new CommunityAccountException("session_storage", "Your saved sign-in could not be opened. Try signing in again."); }
            EnsureCurrent(generation);
            if (cached is null)
            {
                _restored = true;
                return;
            }
            if (!IsValidSession(cached))
            {
                await ClearSessionAsync(cancellationToken);
                _restored = true;
                return;
            }

            try
            {
                var verified = cached.ExpiresAt <= _time.GetUtcNow().AddSeconds(60)
                    ? await RefreshAsync(cached, cancellationToken) : cached;
                using var result = await SendAsync(HttpMethod.Get, "auth/v1/user", verified.AccessToken, null, cancellationToken);
                var user = ParseUser(result.RootElement);
                if (user.Id != cached.User!.Id)
                    throw new CommunityAccountException("session_expired", "Please sign in again to use Media.");
                verified = new CommunitySession
                {
                    AccessToken = verified.AccessToken, RefreshToken = verified.RefreshToken,
                    ExpiresAt = verified.ExpiresAt, User = user
                };
                EnsureCurrent(generation);
                await SaveSessionAsync(verified, cancellationToken);
                EnsureCurrent(generation);
                _session = verified;
                _restored = true;
            }
            catch (CommunityAccountException ex) when (ex.Code == "session_expired")
            {
                EnsureCurrent(generation);
                await ClearSessionAsync(cancellationToken);
                _restored = true;
                // A revoked or expired saved sign-in becomes the normal signed-out screen.
            }
        });
    }

    public async Task<CommunityProfile?> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        var generation = Volatile.Read(ref _generation);
        CommunityProfile? profile = null;
        await RunAsync(generation, cancellationToken, async () =>
        {
            var session = await EnsureSessionAsync(generation, cancellationToken);
            using var result = await SendAuthenticatedAsync(HttpMethod.Get,
                $"rest/v1/profiles?select={ProfileFields}&id=eq.{session.User!.Id:D}&limit=1", session, null, cancellationToken);
            profile = ParseProfileList(result.RootElement, session.User.Id);
            EnsureCurrent(generation);
            Profile = profile;
        });
        return profile;
    }

    public async Task<CommunityProfile> SaveProfileAsync(string username, string displayName, string bio,
        CancellationToken cancellationToken = default)
    {
        var fields = CommunityProfileValidation.Validate(username, displayName, bio);
        var generation = Volatile.Read(ref _generation);
        CommunityProfile? profile = null;
        await RunAsync(generation, cancellationToken, async () =>
        {
            var session = await EnsureSessionAsync(generation, cancellationToken);
            using var result = await SendAuthenticatedAsync(HttpMethod.Patch,
                $"rest/v1/profiles?select={ProfileFields}&id=eq.{session.User!.Id:D}", session,
                new { username = fields.Username, display_name = fields.DisplayName, bio = fields.Bio },
                cancellationToken, "return=representation");
            profile = ParseProfileList(result.RootElement, session.User.Id);
            EnsureCurrent(generation);
            Profile = profile;
        });
        return profile!;
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        var previous = _session;
        Interlocked.Increment(ref _generation);
        Interlocked.Exchange(ref _mediaCancellation, new CancellationTokenSource()).Cancel();
        _session = null;
        Profile = null;
        _restored = true;
        _signOutPending = true;
        // Clearing locally never depends on a reachable server or a non-cancelled UI request.
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            await ClearSessionAsync(CancellationToken.None);
            if (previous is not null && IsConfigured && !cancellationToken.IsCancellationRequested)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                try
                {
                    using var result = await SendAsync(HttpMethod.Post, "auth/v1/logout?scope=local",
                        previous.AccessToken, null, timeout.Token);
                }
                catch (Exception ex) when (ex is CommunityAccountException or OperationCanceledException) { }
            }
        }
        finally { _gate.Release(); }
    }

    private async Task RunAsync(int generation, CancellationToken cancellationToken, Func<Task> operation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsConfigured)
            throw new CommunityAccountException("not_configured", "Online Media is not connected yet. Recording and editing are ready to use.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureCurrent(generation);
            if (_signOutPending)
                throw new CommunityAccountException("signout_pending", "Finish signing out before using Media. Your saved sign-in still needs to be removed from this PC.");
            await operation();
        }
        finally { _gate.Release(); }
    }

    private void EnsureCurrent(int generation)
    {
        if (_disposed || generation != Volatile.Read(ref _generation)) throw new OperationCanceledException();
    }

    private async Task<CommunitySession> EnsureSessionAsync(int generation, CancellationToken cancellationToken)
    {
        var session = _session;
        if (session is null)
            throw new CommunityAccountException("signed_out", "Sign in to use Media.");
        if (session.ExpiresAt <= _time.GetUtcNow().AddSeconds(60))
        {
            try
            {
                session = await RefreshAsync(session, cancellationToken);
                EnsureCurrent(generation);
                await SaveSessionAsync(session, cancellationToken);
                EnsureCurrent(generation);
                _session = session;
            }
            catch (CommunityAccountException ex) when (ex.Code == "session_expired")
            {
                EnsureCurrent(generation);
                await ClearSessionAsync(cancellationToken);
                throw;
            }
        }
        return session;
    }

    private async Task<CommunitySession> RefreshAsync(CommunitySession previous, CancellationToken cancellationToken)
    {
        using var result = await SendAsync(HttpMethod.Post, "auth/v1/token?grant_type=refresh_token", null,
            new { refresh_token = previous.RefreshToken }, cancellationToken);
        var session = ParseSession(result.RootElement);
        if (session.User!.Id != previous.User!.Id)
            throw new CommunityAccountException("session_expired", "Please sign in again to use Media.");
        return session;
    }

    private async Task<JsonDocument> SendAuthenticatedAsync(HttpMethod method, string path, CommunitySession session,
        object? body, CancellationToken cancellationToken, string? prefer = null, int maximumResponseBytes = 128 * 1024)
    {
        try { return await SendAsync(method, path, session.AccessToken, body, cancellationToken, prefer, maximumResponseBytes); }
        catch (CommunityAccountException ex) when (ex.Code == "session_expired")
        {
            await ClearSessionAsync(cancellationToken);
            throw;
        }
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, string? accessToken,
        object? body, CancellationToken cancellationToken, string? prefer = null, int maximumResponseBytes = 128 * 1024)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(new Uri(_configuration.ProjectUrl + "/"), path));
            request.Headers.Add("apikey", _configuration.PublishableKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (accessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (prefer is not null) request.Headers.Add("Prefer", prefer);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var bytes = await ReadBoundedAsync(response.Content, timeout.Token, maximumResponseBytes);
            if (!response.IsSuccessStatusCode) throw MakeSafeError(response.StatusCode, bytes, path);
            if (bytes.Length == 0) return JsonDocument.Parse("{}");
            try { return JsonDocument.Parse(bytes); }
            catch (JsonException) { throw Unavailable(); }
        }
        catch (HttpRequestException) { throw new CommunityAccountException("offline", "Could not connect to Media. Check your connection and try again."); }
        catch (IOException) { throw new CommunityAccountException("offline", "Could not connect to Media. Check your connection and try again."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new CommunityAccountException("timeout", "Media took too long to respond. Please try again."); }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken,
        int maximum = 128 * 1024)
    {
        if (content.Headers.ContentLength > maximum) throw Unavailable();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(block, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maximum) throw Unavailable();
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }

    private static CommunityAccountException MakeSafeError(HttpStatusCode status, byte[] bytes, string path)
    {
        string? code = null;
        try
        {
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.ValueKind == JsonValueKind.Object
                && (json.RootElement.TryGetProperty("code", out var value) || json.RootElement.TryGetProperty("error_code", out value)))
                code = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { }
        if (path.StartsWith("rest/v1/rpc/community_", StringComparison.Ordinal)
            || path.StartsWith("functions/v1/community-delete-account", StringComparison.Ordinal))
        {
            if (code == "KO001") return new("operator_required", "Only a Klipp operator can open this area.");
            if (code == "KO002") return new("invalid_request", "Check the request details and try again.");
            if (code == "KO003") return new("request_limit", "The beta request limit has been reached. Please try again later.");
            if (code == "KO004") return new("deletion_pending", "Account cleanup is in progress. Try deleting again to finish removing the remaining files.");
        }
        if (path.StartsWith("rest/v1/rpc/community_", StringComparison.Ordinal) && MediaError(code, path) is { } mediaError)
            return mediaError;
        if (status == HttpStatusCode.TooManyRequests)
            return new("rate_limited", "Too many attempts. Wait a minute before trying again.");
        if (code == "23505") return new("username_taken", "That username is already taken. Choose another one.");
        if (status == HttpStatusCode.Unauthorized || code is "refresh_token_not_found" or "refresh_token_already_used"
            or "bad_jwt" or "session_not_found" or "user_not_found"
            || path.StartsWith("auth/v1/token", StringComparison.Ordinal) && status == HttpStatusCode.BadRequest)
            return new("session_expired", "Please sign in again to use Media.");
        if (path.StartsWith("auth/v1/verify", StringComparison.Ordinal) && (int)status is >= 400 and < 500)
            return new("invalid_code", "That code is invalid or expired. Request a new code and try again.");
        if (code == "signup_disabled") return new("signup_disabled", "New accounts are not available yet. Please try again later.");
        if (code is "otp_disabled" or "email_provider_disabled") return new("email_disabled", "Email sign-in is not available yet. Please try again later.");
        if (status == HttpStatusCode.Forbidden) return new("access_denied", "Your account cannot access this Media feature.");
        if (code is "23514" or "22001") return new("invalid_profile", "Check your profile details and try again.");
        return Unavailable();
    }

    private CommunitySession ParseSession(JsonElement root)
    {
        try
        {
            var access = root.GetProperty("access_token").GetString();
            var refresh = root.GetProperty("refresh_token").GetString();
            var expiry = root.TryGetProperty("expires_at", out var expiresAt)
                ? DateTimeOffset.FromUnixTimeSeconds(expiresAt.GetInt64())
                : _time.GetUtcNow().AddSeconds(root.GetProperty("expires_in").GetInt32());
            var session = new CommunitySession
            {
                AccessToken = access ?? string.Empty, RefreshToken = refresh ?? string.Empty,
                ExpiresAt = expiry, User = ParseUser(root.GetProperty("user"))
            };
            if (!IsValidSession(session) || expiry <= _time.GetUtcNow()) throw Unavailable();
            return session;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or ArgumentOutOfRangeException)
        { throw Unavailable(); }
    }

    private static CommunityUser ParseUser(JsonElement root)
    {
        try
        {
            var id = root.GetProperty("id").GetGuid();
            var email = root.GetProperty("email").GetString();
            if (id == Guid.Empty || string.IsNullOrWhiteSpace(email)) throw Unavailable();
            return new(id, email);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw Unavailable(); }
    }

    private static CommunityProfile ParseProfileList(JsonElement root, Guid expectedId)
    {
        try
        {
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 1)
                throw new CommunityAccountException("profile_unavailable", "Your profile is not ready yet. Please try refreshing Media.");
            var row = root[0];
            var profile = new CommunityProfile(row.GetProperty("id").GetGuid(), row.GetProperty("username").GetString(),
                row.GetProperty("display_name").GetString() ?? "Player", row.GetProperty("bio").GetString() ?? string.Empty);
            if (profile.Id != expectedId) throw Unavailable();
            return profile;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw Unavailable(); }
    }

    private static bool IsValidSession(CommunitySession session) => session.User is { Id: var id } && id != Guid.Empty
        && !string.IsNullOrWhiteSpace(session.User.Email) && IsSafeToken(session.AccessToken) && IsSafeToken(session.RefreshToken);

    private static bool IsSafeToken(string token) => !string.IsNullOrEmpty(token) && token.Length <= 16 * 1024
        && token.All(c => c is > ' ' and < '\x7f');

    private async Task SaveSessionAsync(CommunitySession session, CancellationToken cancellationToken)
    {
        try { await _store.SaveAsync(session, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch { throw new CommunityAccountException("session_storage", "Windows could not securely save your sign-in. Please try again."); }
    }

    private async Task ClearSessionAsync(CancellationToken cancellationToken)
    {
        _session = null;
        Profile = null;
        try { await _store.ClearAsync(cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch
        {
            _signOutPending = true;
            throw new CommunityAccountException("session_storage", "Windows could not remove your saved sign-in. Please try signing out again.");
        }
        _signOutPending = false;
    }

    private static CommunityAccountException Unavailable() => new("unavailable", "Media is unavailable right now. Please try again later.");

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _generation);
        _mediaCancellation.Cancel();
        _session = null;
        Profile = null;
        // HttpClient belongs to the caller. Pending operations release the semaphore themselves.
    }
}
