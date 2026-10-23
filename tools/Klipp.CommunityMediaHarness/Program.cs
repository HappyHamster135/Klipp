using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Klipp.Core.Community;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;

// Default mode is entirely local. --live explicitly uses the configured backend and the owner's
// existing DPAPI session. Only a generated test video is uploaded; credentials/URLs are never logged.
const string testTitle = "Klipp Media verification";
const string testDescription = "Generated test video. Safe to remove.";
var qaRoot = Path.GetFullPath(Path.Combine("artifacts", "community-media-qa"));
var locator = new FFmpegLocator();
var source = Path.Combine(qaRoot, "generated-game-and-microphone.mp4");
var mode = args.FirstOrDefault() ?? "--local";
try
{
    if (mode == "--export-legacy-self-test")
    {
        await LegacyMediaExporter.RunChecksAsync();
        return;
    }
    if (mode is "--local" or "--live")
    {
        Directory.CreateDirectory(qaRoot);
        if (!locator.IsInstalled) throw new InvalidOperationException("Use the app's existing FFmpeg installation before running this check.");
        await GenerateAsync();
        if (mode == "--local")
        {
            var originalHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
            var probe = new MediaProbeService(locator);
            var input = await probe.GetMediaInfoAsync(source) ?? throw new Exception("Fixture probe failed.");
            Check(input.AudioStreams.Count == 2, "fixture_has_separate_audio");
            foreach (var selection in new[] { new[] { 0 }, new[] { 0, 1 }, Array.Empty<int>() })
            {
                string preparedPath;
                using (var share = await new CommunityClipPreparationService(locator).PrepareAsync(source, 0.25, 1.5, selection))
                {
                    preparedPath = share.VideoPath;
                    var actual = await probe.GetMediaInfoAsync(share.VideoPath) ?? throw new Exception("Output probe failed.");
                    Check(actual.VideoCodec == "h264" && actual.Width <= 1280 && actual.Height <= 720
                        && actual.Duration.TotalSeconds <= 1.75 && actual.AudioStreams.Count == (selection.Length == 0 ? 0 : 1),
                        "prepared_codec_range_and_audio");
                    Check(new FileInfo(share.VideoPath).Length <= CommunityMediaLimits.MaximumVideoBytes
                        && new FileInfo(share.ThumbnailPath).Length <= CommunityMediaLimits.MaximumPosterBytes,
                        "prepared_files_fit_free_limits");
                }
                Check(!File.Exists(preparedPath), "temporary_share_copy_cleaned");
            }
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            try
            {
                using var unexpected = await new CommunityClipPreparationService(locator).PrepareAsync(source, 0, 1, [0], cancellationToken: cancelled.Token);
                throw new Exception("Cancellation was ignored.");
            }
            catch (OperationCanceledException) { Console.WriteLine("PASS preparation_cancellation"); }
            var finalHash = SHA256.HashData(await File.ReadAllBytesAsync(source));
            Check(originalHash.SequenceEqual(finalHash), "original_unchanged");
            Console.WriteLine("LOCAL_COMPLETE");
            return;
        }
    }

    if (mode is not ("--live" or "--cleanup" or "--export-legacy" or "--operations-status"))
        throw new Exception("Use --local, --live, --cleanup, --export-legacy, or --operations-status.");
    var configPath = ValueAfter("--config") ?? "backend/community.json";
    var config = CommunityConfigurationService.Load(configPath);
    if (!config.IsConfigured) throw new Exception("Public configuration missing.");
    var http2 = args.Contains("--http2", StringComparer.Ordinal);
    var verifyOperations = args.Contains("--operations", StringComparer.Ordinal);
    var transport = new VerificationTransport(http2, mode == "--live", config.MediaApiUrl,
        verifyOperations ? config.ProjectUrl : null);
    using var client = new HttpClient(transport) { Timeout = Timeout.InfiniteTimeSpan };
    using var accounts = new CommunityAccountService(config, client, new CommunitySessionStore(config));
    await accounts.RestoreSessionAsync();
    Check(accounts.IsSignedIn, "existing_protected_session_restored");
    var own = await accounts.GetProfileAsync();
    Check(own?.IsComplete == true, "complete_profile");
    if (mode == "--operations-status")
    {
        var capabilities = await accounts.GetOperationsCapabilitiesAsync();
        Check(!verifyOperations || transport.DeleteRequestGuardVerified, "edge_runtime_invalid_request_guard");
        Console.WriteLine($"OPERATIONS_STATUS enabled={capabilities.Enabled} deletion={capabilities.AccountDeletionEnabled}");
        if (capabilities.Enabled)
        {
            var usage = await accounts.GetUsageAsync();
            Check(usage.IsOperator && usage.Operations is not null, "private_operator_usage_available");
            Check((await accounts.GetOperatorReportsPageAsync()).Items is not null, "operator_report_queue_readable");
            Check((await accounts.GetSupportRequestsPageAsync()).Items is not null, "private_requests_readable");
        }
        return;
    }
    if (mode == "--export-legacy")
    {
        var exportDirectory = ValueAfter("--export-directory") ?? throw new InvalidOperationException("An explicit export directory is required.");
        await LegacyMediaExporter.ExportAsync(accounts, config, client, exportDirectory);
        return;
    }
    if (mode == "--cleanup")
    {
        var id = Guid.Parse(await File.ReadAllTextAsync(Path.Combine(qaRoot, "test-clip-id.txt")));
        var page = await accounts.GetMediaFeedAsync(mine: true);
        var clip = page.Clips.SingleOrDefault(clip => clip.Id == id);
        if (clip is not null)
        {
            Check(clip.Title == testTitle && clip.Description == testDescription, "cleanup_generated_fixture_only");
            await accounts.DeleteClipAsync(id);
        }
        Check(!(await accounts.GetMediaFeedAsync(mine: true)).Clips.Any(clip => clip.Id == id), "clip_and_reservation_removed");
        Console.WriteLine("CLEANUP_COMPLETE");
        return;
    }

    Guid? uploadedId = null;
    Uri? uploadedVideoUri = null;
    var leave = args.Contains("--leave-test-clip", StringComparer.Ordinal);
    try
    {
        using var share = await new CommunityClipPreparationService(locator).PrepareAsync(source, 0, 2, [0]);
        var uploaded = await accounts.UploadClipAsync(new(share.VideoPath, share.ThumbnailPath, testTitle, testDescription, share.DurationSeconds));
        uploadedId = uploaded.Id;
        Check(transport.BrowserUploadDenied, "browser_upload_denied");
        Check(uploaded.IsPublished && uploaded.OwnerId == accounts.User!.Id, "upload_published_for_correct_owner");
        Check((await accounts.GetMediaFeedAsync()).Clips.Any(clip => clip.Id == uploaded.Id), "published_clip_in_feed");
        uploadedVideoUri = await accounts.GetClipPlaybackUriAsync(uploaded.Id);
        foreach (var (uri, original, limit, label) in new[]
        {
            (uploadedVideoUri, share.VideoPath, CommunityMediaLimits.MaximumVideoBytes, "video"),
            (await accounts.GetClipPosterUriAsync(uploaded.Id), share.ThumbnailPath, CommunityMediaLimits.MaximumPosterBytes, "poster")
        })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var expectedLength = new FileInfo(original).Length;
            Check(response.IsSuccessStatusCode && response.Content.Headers.ContentLength == expectedLength
                && expectedLength > 0 && expectedLength <= limit, "private_media_download");
            if (http2) Check(response.Version.Major == 2, "http2_" + label + "_playback");
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[32 * 1024];
            long downloaded = 0;
            int count;
            while ((count = await body.ReadAsync(buffer, deadline.Token)) != 0)
            {
                downloaded += count;
                if (downloaded > expectedLength || downloaded > limit)
                    throw new InvalidOperationException("Media download exceeded its reviewed size.");
                hash.AppendData(buffer, 0, count);
            }
            await using var expected = File.OpenRead(original);
            var expectedHash = await SHA256.HashDataAsync(expected, deadline.Token);
            Check(downloaded == expectedLength && hash.GetHashAndReset().SequenceEqual(expectedHash),
                "complete_" + label + "_matches_uploaded_bytes");
        }
        using (var seek = new HttpRequestMessage(HttpMethod.Get, uploadedVideoUri))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            seek.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
            using var response = await client.SendAsync(seek, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            Check(response.StatusCode == System.Net.HttpStatusCode.PartialContent
                && response.Content.Headers.ContentRange?.From == 0
                && response.Content.Headers.ContentRange?.To == 1023
                && response.Content.Headers.ContentLength == 1024, "range_seeking_headers");
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            var buffer = new byte[1025];
            var downloaded = 0;
            int count;
            while (downloaded < buffer.Length
                && (count = await body.ReadAsync(buffer.AsMemory(downloaded), deadline.Token)) != 0)
                downloaded += count;
            Check(downloaded == 1024, "range_seeking_supported");
            if (http2) Check(response.Version.Major == 2, "http2_range_seeking");
        }
        using (var anonymous = new HttpClient(new VerificationTransport(http2)) { Timeout = TimeSpan.FromSeconds(30) })
        {
            anonymous.DefaultRequestHeaders.Add("apikey", config.PublishableKey);
            using var denied = await anonymous.PostAsync(config.ProjectUrl + "/rest/v1/rpc/community_feed",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            Check(denied.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden,
                "anonymous_feed_denied");
        }
        var liked = await accounts.SetClipLikedAsync(uploaded.Id, true);
        Check(liked.IsLikedByMe && liked.LikeCount == 1, "like_persisted");
        Check(!(await accounts.SetClipLikedAsync(uploaded.Id, false)).IsLikedByMe, "unlike_persisted");
        var comment = await accounts.AddClipCommentAsync(uploaded.Id, "Generated comment to verify the Media feed.");
        Check((await accounts.GetClipCommentsPageAsync(uploaded.Id)).Items.Any(item => item.Id == comment.Id), "comment_persisted");
        await accounts.DeleteClipCommentAsync(comment.Id);
        Check(!(await accounts.GetClipCommentsPageAsync(uploaded.Id)).Items.Any(item => item.Id == comment.Id), "own_comment_removed");
        if (verifyOperations)
        {
            var capabilities = await accounts.GetOperationsCapabilitiesAsync();
            Check(capabilities.Enabled && capabilities.AccountDeletionEnabled, "operations_capabilities_enabled");
            Check(transport.DeleteRequestGuardVerified, "edge_runtime_rejects_invalid_deletion_without_mutation");
            var usage = await accounts.GetUsageAsync();
            Check(usage.IsOperator && usage.Operations is not null && usage.Me.Published >= 1
                && usage.Beta.SlotsUsed >= usage.Me.SlotsUsed, "usage_and_private_operator_role");
            Check(usage.Operations!.ProviderUsage.Values.All(metric => metric.Used is null), "unknown_provider_usage_is_not_zero");
            await accounts.ReportClipAsync(uploaded.Id, "Generated moderation verification; this clip is disposable.");
            Check((await accounts.GetOperatorReportsPageAsync()).Items.Any(report => report.ClipId == uploaded.Id), "operator_report_queue");
            await accounts.ReviewReportedClipAsync(uploaded.Id, true, "Hide generated test clip to verify playback revocation.");
            Check(!(await accounts.GetMediaFeedAsync()).Clips.Any(clip => clip.Id == uploaded.Id), "hidden_clip_removed_from_feed");
            Check((await accounts.GetMediaFeedAsync(mine: true)).Clips.Single(clip => clip.Id == uploaded.Id).ModerationHidden,
                "owner_can_find_hidden_clip_for_cleanup");
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            using (var hidden = await client.GetAsync(uploadedVideoUri, HttpCompletionOption.ResponseHeadersRead, deadline.Token))
                Check(hidden.StatusCode == System.Net.HttpStatusCode.NotFound, "previous_home_link_revoked_by_moderation");
            await accounts.ReviewReportedClipAsync(uploaded.Id, false, "Restore generated test clip after successful moderation verification.");
            Check((await accounts.GetMediaFeedAsync()).Clips.Any(clip => clip.Id == uploaded.Id), "reviewed_clip_can_be_restored");
            var request = await accounts.SubmitSupportRequestAsync("help", "Generated private support verification. No action is needed.");
            Check((await accounts.GetSupportRequestsPageAsync()).Items.Any(item => item.Id == request.Id), "private_request_saved");
            var reply = await accounts.ReplyToSupportAsync(request.Id, "Generated verification completed successfully.", true);
            Check(reply.Status == "resolved" && (await accounts.GetSupportRequestsPageAsync()).Items
                .Any(item => item.Id == request.Id && item.Reply == reply.Reply), "private_reply_and_resolution_saved");
        }
        await File.WriteAllTextAsync(Path.Combine(qaRoot, "test-clip-id.txt"), uploaded.Id.ToString());
        Console.WriteLine("LIVE_COMPLETE upload=true feed=true playback=true likes=true comments=true anonymous_denied=true");
    }
    finally
    {
        if (uploadedId is { } id && !leave)
        {
            await accounts.DeleteClipAsync(id);
            Check(!(await accounts.GetMediaFeedAsync(mine: true)).Clips.Any(clip => clip.Id == id), "uploaded_clip_and_reservation_removed");
            if (uploadedVideoUri is not null)
            {
                using var removed = await client.GetAsync(uploadedVideoUri);
                Check(removed.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone, "deleted_media_capability_denied");
            }
        }
    }
}
catch (CommunityAccountException error)
{
    Console.WriteLine("FAIL safe_code=" + error.Code);
    Environment.ExitCode = 1;
}
catch (Exception error)
{
    Console.WriteLine("FAIL type=" + error.GetType().Name);
    Environment.ExitCode = 1;
}

string? ValueAfter(string option)
{
    var index = Array.IndexOf(args, option);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
void Check(bool condition, string label)
{
    if (!condition) { Console.WriteLine("FAIL check=" + label); throw new InvalidOperationException(label); }
    Console.WriteLine("PASS " + label);
}
async Task GenerateAsync()
{
    var start = new ProcessStartInfo(locator.FFmpegPath) { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24",
        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-f", "lavfi", "-i", "sine=frequency=660:sample_rate=48000",
        "-t", "3", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-c:v", "libx264", "-preset", "ultrafast",
        "-threads", "2", "-pix_fmt", "yuv420p", "-c:a", "aac", "-metadata:s:a:0", "title=Game audio",
        "-metadata:s:a:0", "handler_name=Game audio", "-metadata:s:a:1", "title=Microphone",
        "-metadata:s:a:1", "handler_name=Microphone", "-y", source }) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new Exception("Could not generate fixture.");
    var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
    await Task.WhenAll(process.WaitForExitAsync(), output, errors);
    if (process.ExitCode != 0) throw new Exception("Could not generate fixture.");
}

// Explicit requests created by the account service must use the selected protocol
// too; HttpClient.DefaultRequestVersion alone applies only to its convenience methods.
sealed class VerificationTransport(bool http2, bool verifyBrowserUpload = false, string? mediaApiUrl = null,
    string? operationsProjectUrl = null)
    : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
{
    private int _browserProbeStarted;
    private int _browserUploadDenied;
    private int _deleteProbeStarted;
    private int _deleteGuardVerified;
    public bool BrowserUploadDenied => Volatile.Read(ref _browserUploadDenied) == 1;
    public bool DeleteRequestGuardVerified => Volatile.Read(ref _deleteGuardVerified) == 1;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Version = http2 ? System.Net.HttpVersion.Version20 : System.Net.HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        if (operationsProjectUrl is not null && request.Method == HttpMethod.Post
            && request.RequestUri?.AbsoluteUri == operationsProjectUrl.TrimEnd('/') + "/rest/v1/rpc/community_operations_capabilities"
            && Interlocked.CompareExchange(ref _deleteProbeStarted, 1, 0) == 0)
            await CheckDeleteGuardAsync(request, cancellationToken);
        if (verifyBrowserUpload && IsHomeVideoUpload(request)
            && Interlocked.CompareExchange(ref _browserProbeStarted, 1, 0) == 0)
            await CheckBrowserUploadDeniedAsync(request, cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }

    private async Task CheckDeleteGuardAsync(HttpRequestMessage original, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        // This body can never confirm any account: it deliberately violates both
        // the confirmation alphabet and the exact single-field request schema.
        using var probe = new HttpRequestMessage(HttpMethod.Post,
            operationsProjectUrl!.TrimEnd('/') + "/functions/v1/community-delete-account")
        {
            Version = original.Version, VersionPolicy = original.VersionPolicy,
            Content = new StringContent("{\"confirmation\":\"!\",\"invalid_probe\":true}", Encoding.UTF8, "application/json")
        };
        probe.Headers.Authorization = original.Headers.Authorization;
        foreach (var value in original.Headers.GetValues("apikey")) probe.Headers.Add("apikey", value);
        using var response = await base.SendAsync(probe, deadline.Token);
        if (response.StatusCode != System.Net.HttpStatusCode.BadRequest || response.Content.Headers.ContentLength > 512)
            throw new InvalidOperationException("Account deletion guard verification failed.");
        await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
        var buffer = new byte[513];
        var count = 0;
        int read;
        while (count < buffer.Length && (read = await body.ReadAsync(buffer.AsMemory(count), deadline.Token)) != 0) count += read;
        if (count > 512) throw new InvalidOperationException("Account deletion guard response exceeded its reviewed size.");
        using var json = JsonDocument.Parse(buffer.AsMemory(0, count));
        if (json.RootElement.GetProperty("code").GetString() != "KO002")
            throw new InvalidOperationException("Account deletion guard verification failed.");
        Volatile.Write(ref _deleteGuardVerified, 1);
    }

    private bool IsHomeVideoUpload(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Put || request.RequestUri is not { } uri
            || !Uri.TryCreate(mediaApiUrl, UriKind.Absolute, out var configured)
            || configured.Scheme != "https" || configured.UserInfo.Length != 0 || configured.AbsolutePath != "/"
            || configured.Query.Length != 0 || configured.Fragment.Length != 0
            || uri.Scheme != "https" || uri.IdnHost != configured.IdnHost || uri.Port != configured.Port
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || request.Headers.Authorization?.Scheme != "Bearer") return false;
        var segments = uri.AbsolutePath.Split('/');
        return segments.Length == 5 && segments[1] == "v1" && segments[2] == "clips" && segments[4] == "video"
            && Guid.TryParseExact(segments[3], "D", out var clip) && clip != Guid.Empty;
    }

    private async Task CheckBrowserUploadDeniedAsync(HttpRequestMessage original, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        // Clone only routing/authentication metadata. The real upload content is
        // neither buffered, read nor reused by this one-byte browser probe.
        using var probe = new HttpRequestMessage(HttpMethod.Put, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy,
            Content = new ByteArrayContent([0])
        };
        probe.Headers.Authorization = original.Headers.Authorization;
        probe.Headers.Add("Origin", "https://happyhamster135.github.io");
        probe.Headers.Add("Sec-Fetch-Mode", "cors");
        probe.Headers.Add("Sec-Fetch-Site", "cross-site");
        probe.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        // Call the inner handler directly so this probe cannot intercept itself.
        using var response = await base.SendAsync(probe, deadline.Token);
        if (response.StatusCode != System.Net.HttpStatusCode.Forbidden
            || response.Version != original.Version || response.Content.Headers.ContentLength > 4096)
            throw new InvalidOperationException("Browser upload guard verification failed.");
        await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var bytes = new MemoryStream();
        var buffer = new byte[1024];
        int count;
        while ((count = await body.ReadAsync(buffer, deadline.Token)) != 0)
        {
            if (bytes.Length + count > 4096)
                throw new InvalidOperationException("Browser upload guard response exceeded its reviewed size.");
            bytes.Write(buffer, 0, count);
        }
        using var json = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)));
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("code", out var code)
            || code.ValueKind != JsonValueKind.String || code.GetString() != "desktop_upload_required")
            throw new InvalidOperationException("Browser upload guard verification failed.");
        deadline.Token.ThrowIfCancellationRequested();
        Volatile.Write(ref _browserUploadDenied, 1);
    }
}
