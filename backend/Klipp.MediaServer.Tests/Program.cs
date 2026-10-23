using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klipp.MediaServer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

var ffprobe = args.Length > 0 ? Path.GetFullPath(args[0]) : "/usr/bin/ffprobe";
var ffmpeg = args.Length > 1 ? Path.GetFullPath(args[1]) : "/usr/bin/ffmpeg";
var directory = Path.Combine(Path.GetTempPath(), "klipp-media-security-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var checks = 0;
void Check(bool passed, string message)
{
    if (!passed) throw new Exception("FAIL: " + message);
    checks++;
    Console.WriteLine("PASS " + message);
}
void Reject(Action action, string code, string description)
{
    try { action(); throw new Exception("Expected rejection: " + description); }
    catch (MediaException error) { Check(error.Code == code, description); }
}
async Task RejectAsync(Func<Task> action, string code, string description)
{
    try { await action(); throw new Exception("Expected rejection: " + description); }
    catch (MediaException error) { Check(error.Code == code, description); }
}
try
{
    var options = new MediaOptions
    {
        SupabaseProjectUrl = "https://example.supabase.co", SupabasePublishableKey = "sb_publishable_fixture",
        PublicBaseUrl = "https://media.example.com", GatewaySecret = new string('x', 64),
        SigningKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        MountPoint = directory, StorageRoot = Path.Combine(directory, "data"), FfprobePath = ffprobe,
        BackupStatusPath = Path.Combine(directory, "backup-status.json"), UploadsEnabled = true
    };
    options.Validate();
    var now = new FixtureTime(DateTimeOffset.UtcNow);
    var owner = Guid.NewGuid();
    var other = Guid.NewGuid();
    var id = Guid.NewGuid();
    var signer = new MediaSigner(options, now);
    var signed = new Uri(signer.Create(owner, id, "video", TimeSpan.FromMinutes(5)));
    var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(signed.Query);
    Check(signer.Verify(owner, id, "video", query["expires"]!, query["signature"]!), "signed capability verifies");
    Check(!signer.Verify(other, id, "video", query["expires"]!, query["signature"]!), "capability cannot change owner");
    Check(!signer.Verify(owner, Guid.NewGuid(), "video", query["expires"]!, query["signature"]!), "capability cannot change clip");
    Check(!signer.Verify(owner, id, "poster", query["expires"]!, query["signature"]!), "capability cannot change asset");
    Check(!signer.Verify(owner, id, "video", query["expires"]!, new string('0', 64)), "forged capability rejected");
    now.Now = now.Now.AddMinutes(5);
    Check(!signer.Verify(owner, id, "video", query["expires"]!, query["signature"]!), "expired capability rejected");
    now.Now = DateTimeOffset.UtcNow;
    const string mount = "41 29 253:1 / /srv/klipp/media rw,nosuid,nodev,noexec,relatime - ext4 /dev/mapper/ubuntu--vg-klipp--media rw";
    Check(LinuxStorageGuard.MatchesMount([mount], "/srv/klipp/media", options.ExpectedMountSource), "exact required media mount recognized");
    Check(!LinuxStorageGuard.MatchesMount([mount.Replace("/srv/klipp/media ", "/srv/klipp/media-wrong ")], "/srv/klipp/media", options.ExpectedMountSource), "prefix lookalike mount rejected");
    Check(!LinuxStorageGuard.MatchesMount([mount.Replace("nodev,", "")], "/srv/klipp/media", options.ExpectedMountSource), "unsafe mount options rejected");
    Check(!LinuxStorageGuard.MatchesMount([mount.Replace(options.ExpectedMountSource, "/dev/wrong")], "/srv/klipp/media", options.ExpectedMountSource), "wrong volume rejected");
    var backup = new BackupGate(options, now);
    Reject(backup.EnsureUploadsAllowed, "backup_required", "missing backup receipt blocks uploads");
    File.WriteAllText(options.BackupStatusPath, JsonSerializer.Serialize(new { successful_at = now.Now, verified_restore_at = now.Now }));
    backup.EnsureUploadsAllowed();
    Check(true, "verified fresh backup enables uploads");
    now.Now = now.Now.AddHours(49);
    Reject(backup.EnsureUploadsAllowed, "backup_required", "stale backup blocks uploads");
    now.Now = DateTimeOffset.UtcNow;
    File.WriteAllText(options.BackupStatusPath, JsonSerializer.Serialize(new { successful_at = now.Now, verified_restore_at = (DateTimeOffset?)null }));
    Reject(backup.EnsureUploadsAllowed, "backup_required", "backup without actual restore drill blocks uploads");
    options.UploadsEnabled = false;
    Reject(backup.EnsureUploadsAllowed, "uploads_paused", "default activation switch blocks uploads");
    options.UploadsEnabled = true;
    File.WriteAllText(options.BackupStatusPath, JsonSerializer.Serialize(new { successful_at = now.Now, verified_restore_at = now.Now }));

    var video = Path.Combine(directory, "fixture.mp4");
    var poster = Path.Combine(directory, "fixture.jpg");
    await RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=size=128x72:rate=10", "-t", "1", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-an", "-threads", "1", "-movflags", "+faststart", "-y", video]);
    await RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=navy:s=128x72", "-frames:v", "1", "-c:v", "mjpeg", "-threads", "1", "-y", poster]);
    var validator = new FfprobeMediaValidator(options);
    Check(await validator.ValidateVideoAsync(video, default) == 1, "actual H264 fast-start MP4 validates");
    await validator.ValidatePosterAsync(poster, default);
    Check(true, "actual JPEG thumbnail validates");
    var invalid = Path.Combine(directory, "invalid.mp4");
    File.WriteAllText(invalid, "not media");
    await RejectAsync(async () => { await validator.ValidateVideoAsync(invalid, default); }, "invalid_media", "disguised media rejected");
    var unprepared = Path.Combine(directory, "tail-moov.mp4");
    await RunAsync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-i", video, "-c", "copy", "-y", unprepared]);
    Check(!FfprobeMediaValidator.HasFastStartMp4Structure(unprepared), "non-fast-start MP4 rejected before parser");
    var videoBytes = await File.ReadAllBytesAsync(video);
    var posterBytes = await File.ReadAllBytesAsync(poster);
    var gateway = new FixtureGateway(owner, other, id, videoBytes.Length);
    var guard = new FixtureGuard();
    await using var app = MediaServerApplication.Build([], builder =>
    {
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.Replace(ServiceDescriptor.Singleton(options));
        builder.Services.Replace(ServiceDescriptor.Singleton<TimeProvider>(now));
        builder.Services.Replace(ServiceDescriptor.Singleton<ICommunityGateway>(gateway));
        builder.Services.Replace(ServiceDescriptor.Singleton<IStorageGuard>(guard));
    });
    await app.StartAsync();
    var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
    async Task<HttpResponseMessage> Send(HttpMethod method, string path, byte[]? bytes = null, string? mime = null, string? token = FixtureGateway.OwnerToken, string? origin = null, string? fetch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (fetch is not null) request.Headers.Add("Sec-Fetch-Mode", fetch);
        if (bytes is not null)
        { request.Content = new ByteArrayContent(bytes); request.Content.Headers.ContentType = new MediaTypeHeaderValue(mime ?? "video/mp4"); }
        return await client.SendAsync(request);
    }
    async Task Status(Task<HttpResponseMessage> task, HttpStatusCode expected, string description)
    {
        using var response = await task;
        var body = await response.Content.ReadAsStringAsync();
        Check(response.StatusCode == expected, description + (response.StatusCode == expected ? "" : $" ({response.StatusCode}: {body})"));
        Check(response.Headers.CacheControl?.NoStore == true, "private response has no-store cache policy");
    }
    var clipPath = "/v1/clips/" + id.ToString("D");
    await Status(Send(HttpMethod.Put, clipPath + "/video", videoBytes, token: null), HttpStatusCode.Unauthorized, "anonymous upload rejected");
    await Status(Send(HttpMethod.Put, clipPath + "/video", videoBytes, origin: "https://happyhamster135.github.io"), HttpStatusCode.Forbidden, "browser Origin upload rejected");
    await Status(Send(HttpMethod.Put, clipPath + "/video", videoBytes, fetch: "cors"), HttpStatusCode.Forbidden, "browser fetch metadata upload rejected");
    await Status(Send(HttpMethod.Put, clipPath + "/video", videoBytes, mime: "text/plain"), HttpStatusCode.UnsupportedMediaType, "incorrect content type rejected");
    await Status(Send(HttpMethod.Put, clipPath + "/video", videoBytes[..^1]), HttpStatusCode.BadRequest, "reservation byte mismatch rejected");
    await Status(Send(HttpMethod.Put, clipPath + "/video", videoBytes, token: FixtureGateway.OtherToken), HttpStatusCode.NotFound, "other member cannot upload into owner's reservation");
    await Status(Send(HttpMethod.Post, clipPath + "/publish"), HttpStatusCode.Conflict, "incomplete upload cannot publish");
    var existingPaths = Directory.Exists(options.StorageRoot) ? Directory.GetFileSystemEntries(options.StorageRoot, "*", SearchOption.AllDirectories) : [];
    guard.Unavailable = true;
    await Status(Send(HttpMethod.Put, clipPath + "/video", videoBytes), HttpStatusCode.ServiceUnavailable, "lost media mount fails closed");
    Check(existingPaths.SequenceEqual(Directory.GetFileSystemEntries(options.StorageRoot, "*", SearchOption.AllDirectories)), "lost mount never creates fallback files or directories");
    guard.Unavailable = false;
    options.UploadsEnabled = false;
    await Status(Send(HttpMethod.Put, clipPath + "/video", videoBytes), HttpStatusCode.ServiceUnavailable, "paused uploads do not write files");
    options.UploadsEnabled = true;
    await Status(Send(HttpMethod.Put, clipPath + "/video", videoBytes), HttpStatusCode.OK, "bounded streamed video uploads");
    await Status(Send(HttpMethod.Put, clipPath + "/poster", posterBytes, "image/jpeg"), HttpStatusCode.OK, "bounded thumbnail uploads");
    await Status(Send(HttpMethod.Put, clipPath + "/video", videoBytes), HttpStatusCode.Conflict, "uploaded assets cannot be overwritten");
    await Status(Send(HttpMethod.Post, clipPath + "/publish", origin: "null"), HttpStatusCode.Forbidden, "browser cannot publish even with null origin");
    await Status(Send(HttpMethod.Post, clipPath + "/publish"), HttpStatusCode.OK, "validated upload publishes");
    Check(gateway.PublishCalls == 1 && gateway.Evidence?.VideoSha256 == Convert.ToHexStringLower(SHA256.HashData(videoBytes)), "server computes SHA256 evidence from stored bytes");
    var manifest = Path.Combine(options.StorageRoot, "clips", owner.ToString("D"), id.ToString("D"), "manifest.json");
    using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(manifest)))
    {
        Check(json.RootElement.GetProperty("video_sha256").GetString() == gateway.Evidence!.VideoSha256, "backup manifest records checked hashes");
        Check(json.RootElement.GetProperty("clip").GetProperty("status").GetString() == "published", "backup manifest records published clip metadata");
    }
    using var playback = await Send(HttpMethod.Post, clipPath + "/playback", token: FixtureGateway.OtherToken, origin: "https://happyhamster135.github.io");
    Check(playback.StatusCode == HttpStatusCode.OK, "signed-in mobile member obtains playback");
    Check(playback.Headers.GetValues("Access-Control-Allow-Origin").Single() == "https://happyhamster135.github.io", "CORS allows configured mobile origin");
    using var playbackJson = JsonDocument.Parse(await playback.Content.ReadAsStringAsync());
    var assetUri = new Uri(playbackJson.RootElement.GetProperty("video_url").GetString()!);
    var assetPath = assetUri.PathAndQuery;
    using (var rangeRequest = new HttpRequestMessage(HttpMethod.Get, assetPath))
    {
        rangeRequest.Headers.Range = new RangeHeaderValue(0, 15);
        using var range = await client.SendAsync(rangeRequest);
        Check(range.StatusCode == HttpStatusCode.PartialContent, "real Kestrel serves HTTP byte ranges");
        Check((await range.Content.ReadAsByteArrayAsync()).SequenceEqual(videoBytes[..16]), "range response contains exact requested bytes");
        Check(range.Content.Headers.ContentRange?.Length == videoBytes.Length, "range total size matches file");
    }
    using (var head = await Send(HttpMethod.Head, assetPath, token: null))
        Check(head.StatusCode == HttpStatusCode.OK && head.Content.Headers.ContentLength == videoBytes.Length && (await head.Content.ReadAsByteArrayAsync()).Length == 0, "HEAD reports size without sending content");
    using (var request = new HttpRequestMessage(HttpMethod.Get, assetPath))
    {
        request.Headers.Range = new RangeHeaderValue(videoBytes.Length + 20, null);
        using var response = await client.SendAsync(request);
        Check(response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable, "out-of-bounds range rejected");
    }
    await Status(Send(HttpMethod.Get, assetPath + "&signature=bad", token: null), HttpStatusCode.Forbidden, "duplicate signed query argument rejected");
    await Status(Send(HttpMethod.Get, assetPath.Replace(owner.ToString("D"), other.ToString("D")), token: null), HttpStatusCode.Forbidden, "HTTP asset owner tampering rejected");
    now.Now = now.Now.AddMinutes(6);
    await Status(Send(HttpMethod.Get, assetPath, token: null), HttpStatusCode.Forbidden, "expired HTTP playback capability rejected");
    now.Now = DateTimeOffset.UtcNow;
    gateway.DenyPlayback = true;
    await Status(Send(HttpMethod.Post, clipPath + "/playback", token: FixtureGateway.OtherToken), HttpStatusCode.NotFound, "database visibility revocation blocks new playback links");
    gateway.DenyPlayback = false;
    options.UploadsEnabled = false;
    await Status(Send(HttpMethod.Delete, clipPath), HttpStatusCode.OK, "owner can remove files while uploads are paused");
    Check(!File.Exists(manifest) && !Directory.EnumerateFiles(options.StorageRoot, "video.mp4", SearchOption.AllDirectories).Any(), "deletion removes assets and recovery manifest");
    await Status(Send(HttpMethod.Delete, clipPath), HttpStatusCode.OK, "completed deletion is idempotent");
    await Status(Send(HttpMethod.Get, assetPath, token: null), HttpStatusCode.NotFound, "previous playback capability no longer opens deleted file");
    await app.StopAsync();
    Console.WriteLine($"All {checks} media security, validation, backup and HTTP checks passed.");
}
finally
{
    if (Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
        && Path.GetFileName(directory).StartsWith("klipp-media-security-", StringComparison.Ordinal)) Directory.Delete(directory, recursive: true);
}

static async Task RunAsync(string executable, string[] arguments)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new Exception("Fixture encoder did not start.");
    var error = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await process.WaitForExitAsync(timeout.Token);
    if (process.ExitCode != 0) throw new Exception("Fixture encoder failed: " + await error);
}

sealed class FixtureTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
sealed class FixtureGuard : IStorageGuard
{
    public bool Unavailable { get; set; }
    public void Verify() { if (Unavailable) throw MediaException.StorageUnavailable(); }
    public void VerifySpace(long requiredBytes) => Verify();
}
sealed class FixtureGateway(Guid owner, Guid other, Guid id, long bytes) : ICommunityGateway
{
    public const string OwnerToken = "fixture-owner-access-token-12345";
    public const string OtherToken = "fixture-other-access-token-12345";
    private string _status = "uploading";
    private bool _deleted;
    public bool DenyPlayback { get; set; }
    public int PublishCalls { get; private set; }
    public MediaEvidence? Evidence { get; private set; }
    private JsonElement Clip() => JsonSerializer.SerializeToElement(new { id, owner_id = owner, status = _status,
        storage_provider = "home", byte_size = bytes, duration_seconds = 1.0, title = "Fixture", description = "",
        username = "fixture", display_name = "Fixture", created_at = DateTimeOffset.UtcNow,
        video_path = $"{owner:D}/{id:D}/video.mp4", poster_path = $"{owner:D}/{id:D}/poster.jpg",
        like_count = 0, comment_count = 0, liked_by_me = false });
    public Task<MediaIdentity> AuthenticateAsync(string token, CancellationToken cancellation) => token switch
    {
        OwnerToken => Task.FromResult(new MediaIdentity(owner, token)),
        OtherToken => Task.FromResult(new MediaIdentity(other, token)),
        _ => throw new MediaException(401, "session_expired", "Sign in again.")
    };
    public Task<GatewayClip> GetClipAsync(MediaIdentity identity, Guid requested, CancellationToken cancellation)
    {
        if (requested != id || _deleted || DenyPlayback && identity.UserId != owner) throw MediaException.Unavailable();
        return Task.FromResult(GatewayClip.Parse(Clip()));
    }
    public Task<JsonElement> PublishAsync(MediaIdentity identity, Guid requested, MediaEvidence evidence, CancellationToken cancellation)
    {
        if (identity.UserId != owner || requested != id || _deleted) throw MediaException.Unavailable();
        PublishCalls++; Evidence = evidence; _status = "published";
        return Task.FromResult(Clip());
    }
    public Task<JsonElement> BeginDeleteAsync(MediaIdentity identity, Guid requested, CancellationToken cancellation)
    {
        if (requested != id || identity.UserId != owner) throw MediaException.Unavailable();
        _status = "deleting";
        return Task.FromResult(Clip());
    }
    public Task<JsonElement> FinishDeleteAsync(MediaIdentity identity, Guid requested, CancellationToken cancellation)
    { _deleted = true; return Task.FromResult(JsonSerializer.SerializeToElement(new { id, deleted = true })); }
}
