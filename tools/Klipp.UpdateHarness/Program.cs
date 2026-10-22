using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.Versioning;
using System.Text.Json;
using Klipp.Desktop.Services;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;

if (args.Contains("--installed-root"))
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The native apply check requires Windows.");
    await RunInstalledCheckAsync(args);
    return;
}

var checks = 0;
var root = Path.Combine(Path.GetTempPath(), "Klipp-Update-QA-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);

void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

string State(string name) => Path.Combine(root, name, "check-state.json");

// A raw ZIP/development launch has no updater ownership and never queries the feed.
{
    var client = new FakeClient { IsInstalled = false };
    using var service = new AppUpdateService(client, State("portable"));
    await service.CheckAsync(true, manual: true);
    Assert(service.Snapshot.Status == AppUpdateStatus.InstallerRequired, "Portable build must explain installation.");
    Assert(client.CheckCalls == 0 && client.DownloadCalls == 0, "Portable build must not query/download updates.");
    Assert(!service.PrepareRestart() && client.PrepareCalls == 0, "Portable build cannot arm updates.");
    Assert(!File.Exists(State("portable")), "Portable check must not create an update poll cache.");
}

// Successful checks persist their timestamp and avoid repeated network activity across launches.
{
    var clock = new TestClock();
    var client = new FakeClient();
    using (var service = new AppUpdateService(client, State("throttle"), clock))
    {
        await service.CheckAsync(false, manual: true);
        Assert(service.Snapshot.Status == AppUpdateStatus.UpToDate, "Empty feed should be up to date.");
        Assert(service.Snapshot.LastCheckedUtc == clock.GetUtcNow(), "Successful check timestamp missing.");
        await service.CheckAsync(false, manual: true);
        Assert(client.CheckCalls == 1, "Immediate manual checks should be throttled.");
        clock.Advance(TimeSpan.FromHours(5));
        await service.CheckAsync(false);
        Assert(client.CheckCalls == 1, "Automatic check repeated within six hours.");
    }
    var reopenedClient = new FakeClient();
    using var reopened = new AppUpdateService(reopenedClient, State("throttle"), clock);
    await reopened.CheckAsync(false);
    Assert(reopenedClient.CheckCalls == 0, "Poll throttling must survive app restart.");
    clock.Advance(TimeSpan.FromHours(1));
    await reopened.CheckAsync(false);
    Assert(reopenedClient.CheckCalls == 1, "Automatic checks should resume after six hours.");
    clock.Advance(TimeSpan.FromSeconds(16));
    await reopened.CheckAsync(false, manual: true);
    Assert(reopenedClient.CheckCalls == 2, "Explicit checks should bypass the automatic interval.");
}

// Failed checks have a short retry backoff rather than blocking retries for six hours.
{
    var clock = new TestClock();
    var client = new FakeClient { Check = _ => throw new HttpRequestException("offline") };
    using var service = new AppUpdateService(client, State("offline"), clock);
    await service.CheckAsync(false);
    Assert(service.Snapshot.Status == AppUpdateStatus.Error, "Offline check should produce a recoverable error.");
    Assert(service.Snapshot.Message.Contains("connection") && service.Snapshot.LastCheckedUtc is null, "Offline check must not claim success.");
    clock.Advance(TimeSpan.FromMinutes(14));
    await service.CheckAsync(false);
    Assert(client.CheckCalls == 1, "Failed checks need a retry backoff.");
    clock.Advance(TimeSpan.FromMinutes(1));
    client.Check = _ => Task.FromResult<AppUpdateRelease?>(null);
    await service.CheckAsync(false);
    Assert(client.CheckCalls == 2 && service.Snapshot.Status == AppUpdateStatus.UpToDate, "Offline recovery should succeed.");
}

// A shared operation coalesces overlapping callers instead of doubling GitHub requests.
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource<AppUpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var client = new FakeClient { Check = async token => { entered.TrySetResult(); return await release.Task.WaitAsync(token); } };
    using var service = new AppUpdateService(client, State("coalesce"));
    var first = service.CheckAsync(false, manual: true);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var second = service.CheckAsync(true, manual: true);
    Assert(ReferenceEquals(first, second), "Concurrent checks should share a task.");
    release.SetResult(null);
    await Task.WhenAll(first, second);
    Assert(client.CheckCalls == 1, "Concurrent checks queried twice.");
}

// Available updates can be downloaded later, while idle, without repeating a throttled check.
{
    var client = new FakeClient { Offered = FakeClient.Release("1.0.7") };
    using var service = new AppUpdateService(client, State("available"));
    var progress = new ConcurrentQueue<int>();
    service.StateChanged += (_, _) => { if (service.Snapshot.Status == AppUpdateStatus.Downloading) progress.Enqueue(service.Snapshot.ProgressPercent); };
    await service.CheckAsync(false, manual: true);
    Assert(service.Snapshot.Status == AppUpdateStatus.Available && client.DownloadCalls == 0, "Check-only mode must not download.");
    await service.CheckAsync(true);
    Assert(client.CheckCalls == 1 && client.DownloadCalls == 1, "Idle download should reuse the known release.");
    Assert(service.Snapshot.Status == AppUpdateStatus.Ready && service.Snapshot.AvailableVersion == "1.0.7", "Verified package should become ready.");
    Assert(progress.All(p => p is >= 0 and <= 100), "Progress must remain bounded.");
    Assert(service.PrepareRestart() && client.PrepareCalls == 1, "Ready update should arm graceful restart.");
    Assert(service.PrepareRestart() && client.PrepareCalls == 1, "Double-clicking restart must not spawn a second helper.");
    await service.CheckAsync(true, manual: true);
    Assert(client.CheckCalls == 1, "Checks must stop once restart has been prepared.");
}

// Cancellation is retryable; a canceled download never arms the updater.
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var client = new FakeClient { Offered = FakeClient.Release("1.0.8") };
    client.Download = async (_, _, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
    using var service = new AppUpdateService(client, State("cancel"));
    var operation = service.CheckAsync(true, manual: true);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert(service.Snapshot.Status == AppUpdateStatus.Downloading, "Download should be visible while active.");
    Assert(!service.PrepareRestart(), "An active download must not be installed.");
    service.CancelDownload();
    await operation;
    Assert(service.Snapshot.Status == AppUpdateStatus.Available && service.Snapshot.Message.Contains("paused"), "Canceled download must remain retryable.");
    Assert(!service.PrepareRestart() && client.PrepareCalls == 0, "Canceled package must never be installed.");
    client.Download = null;
    await service.DownloadAsync();
    Assert(service.Snapshot.Status == AppUpdateStatus.Ready && client.DownloadCalls == 2, "Canceled downloads should retry without another check.");
}

// Concurrent manual downloads must share one transfer.
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var client = new FakeClient { Offered = FakeClient.Release("1.0.9") };
    client.Download = async (asset, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); client.Pending = asset; };
    using var service = new AppUpdateService(client, State("download-coalesce"));
    await service.CheckAsync(false, manual: true);
    var first = service.DownloadAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var second = service.DownloadAsync();
    Assert(ReferenceEquals(first, second), "Concurrent downloads should share one task.");
    release.SetResult();
    await Task.WhenAll(first, second);
    Assert(client.DownloadCalls == 1 && service.Snapshot.Status == AppUpdateStatus.Ready, "Concurrent download handling failed.");
}

// Large failed downloads back off automatically, while an explicit retry stays immediate.
{
    var clock = new TestClock();
    var client = new FakeClient { Offered = FakeClient.Release("1.1.0"), Download = (_, _, _) => throw new IOException("disk full") };
    using (var service = new AppUpdateService(client, State("download-backoff"), clock))
    {
        await service.CheckAsync(true);
        Assert(client.DownloadCalls == 1 && service.Snapshot.Status == AppUpdateStatus.Available, "Initial automatic download should fail safely.");
        for (var attempt = 0; attempt < 5; attempt++) await service.DownloadAsync(manual: false);
        Assert(client.DownloadCalls == 1, "Background polling retried a failed package too often.");
        clock.Advance(TimeSpan.FromMinutes(14));
        await service.CheckAsync(true);
        Assert(client.DownloadCalls == 1, "Automatic transfer backoff was too short.");
        clock.Advance(TimeSpan.FromMinutes(1));
        await service.DownloadAsync(manual: false);
        Assert(client.DownloadCalls == 2, "Automatic transfer did not resume after backoff.");
        await service.DownloadAsync();
        Assert(client.DownloadCalls == 3, "Manual retry should bypass transfer backoff.");
    }
    clock.Advance(TimeSpan.FromMinutes(15));
    var reopenedClient = new FakeClient { Offered = FakeClient.Release("1.1.0") };
    using var reopened = new AppUpdateService(reopenedClient, State("download-backoff"), clock);
    await reopened.CheckAsync(true);
    Assert(reopenedClient.CheckCalls == 1 && reopenedClient.DownloadCalls == 1 && reopened.Snapshot.Status == AppUpdateStatus.Ready,
        "Failed transfer retry should survive app restart without waiting six hours.");
}

// Neither checksum failure nor a false 100% progress event may offer Restart.
foreach (var missingPreparedPackage in new[] { false, true })
{
    var client = new FakeClient { Offered = FakeClient.Release("1.1.0") };
    client.Download = (_, progress, _) =>
    {
        progress(100);
        if (!missingPreparedPackage) throw new InvalidDataException("checksum mismatch");
        return Task.CompletedTask;
    };
    using var service = new AppUpdateService(client, State("bad-package-" + missingPreparedPackage));
    await service.CheckAsync(true, manual: true);
    Assert(service.Snapshot.Status == AppUpdateStatus.Available, "Unverified download must remain unavailable to install.");
    Assert(!service.PrepareRestart() && client.PrepareCalls == 0, "Unverified download was armed.");
}

// Prepared updates survive app restarts; recovery must not require network access.
{
    var client = new FakeClient { Pending = FakeClient.Release("1.1.1") };
    using var service = new AppUpdateService(client, State("pending"));
    await service.CheckAsync(true);
    Assert(service.Snapshot.Status == AppUpdateStatus.Ready && client.CheckCalls == 0, "Prepared update should be restored without network.");
    client.PrepareFailure = true;
    Assert(!service.PrepareRestart() && service.Snapshot.Status == AppUpdateStatus.Ready, "Updater launch failure should allow a retry.");
    client.PrepareFailure = false;
    Assert(service.PrepareRestart() && client.PrepareCalls == 2, "Updater launch retry failed.");
}

// A deferred staged package does not pin later checks to that older version.
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var client = new FakeClient { Pending = FakeClient.Release("1.0.9"), Offered = FakeClient.Release("1.0.10") };
    client.Download = async (asset, progress, token) =>
    {
        progress(100);
        entered.TrySetResult();
        await finish.Task.WaitAsync(token);
        client.Pending = asset;
    };
    using var service = new AppUpdateService(client, State("pending-newer-manual"), new TestClock());
    await service.CheckAsync(false);
    Assert(service.Snapshot.Status == AppUpdateStatus.Ready && client.CheckCalls == 0,
        "The older verified package was not restored before querying the network.");
    await service.CheckAsync(false, manual: true);
    Assert(service.Snapshot.Status == AppUpdateStatus.Available && service.Snapshot.AvailableVersion == "1.0.10" && client.DownloadCalls == 0,
        "Checking with an older staged update did not expose the newer stable download.");
    var transfer = service.DownloadAsync();
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert(service.Snapshot.Status == AppUpdateStatus.Downloading && client.Pending?.Version == "1.0.9" && !service.PrepareRestart(),
        "A 100% progress report replaced the verified fallback before the newer download completed.");
    finish.SetResult();
    await transfer;
    Assert(service.Snapshot.Status == AppUpdateStatus.Ready && service.Snapshot.AvailableVersion == "1.0.10",
        "Manual download could not replace an older staged package.");
    Assert(service.PrepareRestart() && client.Prepared?.Version == "1.0.10",
        "Restart did not select the newest verified package.");
}

// Ready restoration is local, but subsequent feed checks keep their normal persisted interval.
{
    var clock = new TestClock();
    var state = State("pending-check-interval");
    var client = new FakeClient { Pending = FakeClient.Release("1.0.9"), Offered = FakeClient.Release("1.0.9") };
    using (var service = new AppUpdateService(client, state, clock))
    {
        await service.CheckAsync(true);
        await service.CheckAsync(true);
        Assert(client.CheckCalls == 1 && client.DownloadCalls == 0 && service.Snapshot.Status == AppUpdateStatus.Ready,
            "A pending update either prevented a later feed check or redownloaded its own version.");
    }
    clock.Advance(TimeSpan.FromHours(5));
    var reopenedClient = new FakeClient { Pending = client.Pending, Offered = FakeClient.Release("1.0.10") };
    using var reopened = new AppUpdateService(reopenedClient, state, clock);
    await reopened.CheckAsync(true);
    await reopened.CheckAsync(true);
    Assert(reopenedClient.CheckCalls == 0 && reopenedClient.DownloadCalls == 0 && reopened.Snapshot.Status == AppUpdateStatus.Ready,
        "Restoring a pending package bypassed the persisted six-hour feed interval.");
    clock.Advance(TimeSpan.FromHours(1));
    await reopened.CheckAsync(true);
    Assert(reopenedClient.CheckCalls == 1 && reopenedClient.DownloadCalls == 1 && reopened.Snapshot.AvailableVersion == "1.0.10",
        "An idle check did not discover and download a newer release after its interval elapsed.");
}

// Offline discovery retains both the old ready status and its usable restart handle.
{
    var client = new FakeClient { Pending = FakeClient.Release("1.0.9"), Check = _ => throw new HttpRequestException("offline") };
    using var service = new AppUpdateService(client, State("pending-offline"), new TestClock());
    await service.CheckAsync(false);
    await service.CheckAsync(false, manual: true);
    Assert(service.Snapshot.Status == AppUpdateStatus.Ready && service.Snapshot.AvailableVersion == "1.0.9" &&
        service.Snapshot.Message.Contains("connection"), "Offline discovery hid the verified fallback.");
    Assert(service.PrepareRestart() && client.Prepared?.Version == "1.0.9", "Offline discovery discarded the verified restart handle.");
}

// Canceled discovery restores the fallback and retains automatic network backoff.
{
    var clock = new TestClock();
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var client = new FakeClient { Pending = FakeClient.Release("1.0.9") };
    client.Check = async token => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return null; };
    using var service = new AppUpdateService(client, State("pending-cancel-check"), clock);
    await service.CheckAsync(false);
    using var cancellation = new CancellationTokenSource();
    var check = service.CheckAsync(false, manual: true, cancellation.Token);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    cancellation.Cancel();
    await check;
    Assert(service.Snapshot.Status == AppUpdateStatus.Ready && service.Snapshot.AvailableVersion == "1.0.9",
        "Canceled discovery hid the verified fallback.");
    client.Check = _ => Task.FromResult<AppUpdateRelease?>(null);
    clock.Advance(TimeSpan.FromMinutes(14));
    await service.CheckAsync(false);
    Assert(client.CheckCalls == 1, "Canceled discovery immediately retried the network.");
    clock.Advance(TimeSpan.FromMinutes(1));
    await service.CheckAsync(false);
    Assert(client.CheckCalls == 2 && service.Snapshot.Status == AppUpdateStatus.Ready,
        "Canceled discovery never resumed or lost its fallback after a later empty feed.");
    Assert(service.PrepareRestart() && client.Prepared?.Version == "1.0.9", "Canceled discovery discarded the verified restart handle.");
}

// Failed or stale verification retains the fallback only while the SDK still has that package.
foreach (var failure in new[] { "disk", "checksum", "stale" })
{
    var client = new FakeClient { Pending = FakeClient.Release("1.0.9"), Offered = FakeClient.Release("1.0.10") };
    client.Download = (_, progress, _) =>
    {
        progress(100);
        if (failure == "disk") throw new IOException("disk full");
        if (failure == "checksum") throw new InvalidDataException("checksum mismatch");
        return Task.CompletedTask;
    };
    using var service = new AppUpdateService(client, State("pending-failure-" + failure));
    await service.CheckAsync(false);
    await service.CheckAsync(true, manual: true);
    Assert(service.Snapshot.Status == AppUpdateStatus.Ready && service.Snapshot.AvailableVersion == "1.0.9",
        "A failed or unverified replacement lost the old ready package: " + failure);
    Assert(service.PrepareRestart() && client.Prepared?.Version == "1.0.9",
        "A failed or mismatched replacement changed the restart target: " + failure);
}

// The real SDK may remove the old package during failed/canceled replacement cleanup.
foreach (var failure in new[] { "disk", "checksum", "missing", "wrong-version", "cancel" })
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var client = new FakeClient { Pending = FakeClient.Release("1.0.9"), Offered = FakeClient.Release("1.0.10") };
    client.Download = async (_, progress, token) =>
    {
        client.Pending = failure == "wrong-version" ? FakeClient.Release("1.0.11") : null;
        progress(100);
        if (failure == "disk") throw new IOException("disk full after cleanup");
        if (failure == "checksum") throw new InvalidDataException("checksum mismatch after cleanup");
        if (failure == "cancel")
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        }
    };
    using var service = new AppUpdateService(client, State("pending-cleaned-" + failure));
    await service.CheckAsync(false);
    var transfer = service.CheckAsync(true, manual: true);
    if (failure == "cancel")
    {
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.CancelDownload();
    }
    await transfer;
    Assert(service.Snapshot.Status == AppUpdateStatus.Available && service.Snapshot.AvailableVersion == "1.0.10" &&
        service.Snapshot.Message.Contains("previous update is no longer prepared"),
        "SDK cleanup left a stale ready card or omitted retry guidance: " + failure);
    Assert(!service.PrepareRestart() && client.PrepareCalls == 0,
        "SDK cleanup left a restart handle to a removed or mismatched package: " + failure);
    client.Download = null;
    await service.DownloadAsync();
    Assert(service.Snapshot.Status == AppUpdateStatus.Ready && service.Snapshot.AvailableVersion == "1.0.10" && client.DownloadCalls == 2,
        "SDK cleanup prevented an explicit replacement retry: " + failure);
    Assert(service.PrepareRestart() && client.Prepared?.Version == "1.0.10",
        "The retry did not prepare the newly verified update after SDK cleanup: " + failure);
}

// Pausing a replacement leaves the fallback ready; a manual retry can still install the newer one.
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var client = new FakeClient { Pending = FakeClient.Release("1.0.9"), Offered = FakeClient.Release("1.0.10") };
    client.Download = async (_, _, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
    using var service = new AppUpdateService(client, State("pending-cancel-download"));
    await service.CheckAsync(false);
    var replacement = service.CheckAsync(true, manual: true);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    service.CancelDownload();
    await replacement;
    Assert(service.Snapshot.Status == AppUpdateStatus.Ready && service.Snapshot.AvailableVersion == "1.0.9" &&
        service.Snapshot.Message.Contains("paused"), "Pausing a replacement hid the older verified package.");
    client.Download = null;
    await service.DownloadAsync();
    Assert(client.DownloadCalls == 2 && service.Snapshot.Status == AppUpdateStatus.Ready && service.Snapshot.AvailableVersion == "1.0.10",
        "Manual replacement retry was blocked by the retained older package.");
    Assert(service.PrepareRestart() && client.Prepared?.Version == "1.0.10", "Replacement retry installed the stale fallback.");
}

// Retaining an old ready package must not bypass failed-transfer backoff.
{
    var clock = new TestClock();
    var client = new FakeClient { Pending = FakeClient.Release("1.0.9"), Offered = FakeClient.Release("1.0.10"),
        Download = (_, _, _) => throw new IOException("disk full") };
    using var service = new AppUpdateService(client, State("pending-download-backoff"), clock);
    await service.CheckAsync(false);
    await service.CheckAsync(true);
    for (var attempt = 0; attempt < 5; attempt++) await service.CheckAsync(true);
    clock.Advance(TimeSpan.FromMinutes(14));
    await service.CheckAsync(true);
    Assert(client.DownloadCalls == 1 && service.Snapshot.Status == AppUpdateStatus.Ready,
        "Replacement retry ignored backoff or hid the verified fallback while waiting.");
    clock.Advance(TimeSpan.FromMinutes(1));
    client.Download = null;
    await service.CheckAsync(true);
    Assert(client.DownloadCalls == 2 && service.Snapshot.AvailableVersion == "1.0.10",
        "Replacement retry did not resume after backoff.");
}

// Compare semantic precedence, not strings; ignore stale/equal/prerelease/malformed offers.
foreach (var offered in new[] { "1.0.8", "1.0.9", "1.0.9.0+rebuilt", "1.0.10-beta.1", "not-a-version" })
{
    var client = new FakeClient { Pending = FakeClient.Release("1.0.9"), Offered = FakeClient.Release(offered) };
    using var service = new AppUpdateService(client, State("pending-ignore-" + Guid.NewGuid()));
    await service.CheckAsync(false);
    await service.CheckAsync(true, manual: true);
    await service.DownloadAsync();
    Assert(client.CheckCalls == 1 && client.DownloadCalls == 0 && service.Snapshot.Status == AppUpdateStatus.Ready &&
        service.Snapshot.AvailableVersion == "1.0.9", "An unsuitable offer replaced or redownloaded the ready package: " + offered);
    Assert(service.PrepareRestart() && client.Prepared?.Version == "1.0.9", "An unsuitable offer changed the restart target: " + offered);
}

// The installed version is also a lower bound when no package is staged.
foreach (var offered in new[] { "1.0.5", "1.0.6", "1.0.7-beta.1", "not-a-version" })
{
    var client = new FakeClient { Offered = FakeClient.Release(offered) };
    using var service = new AppUpdateService(client, State("ignore-" + Guid.NewGuid()));
    await service.CheckAsync(true, manual: true);
    Assert(client.DownloadCalls == 0 && service.Snapshot.Status == AppUpdateStatus.UpToDate && !service.PrepareRestart(),
        "A stale, prerelease or malformed offer bypassed the installed-version guard: " + offered);
}

// Closing Klipp cancels transfers without sending callbacks to a disposed view.
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var client = new FakeClient { Offered = FakeClient.Release("1.1.2") };
    client.Download = async (_, _, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); };
    var service = new AppUpdateService(client, State("dispose"));
    var notifications = 0;
    service.StateChanged += (_, _) => Interlocked.Increment(ref notifications);
    var transfer = service.CheckAsync(true, manual: true);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    service.Dispose();
    var before = notifications;
    await transfer;
    Assert(notifications == before && client.PrepareCalls == 0, "Disposed views received update events.");
    await service.CheckAsync(true, manual: true);
    Assert(client.CheckCalls == 1, "Disposed service must not run new checks.");
}

// Rate limits and an old ZIP-only GitHub release receive useful, nonfatal messages.
foreach (var exception in new Exception[]
         {
             new HttpRequestException("limited", null, HttpStatusCode.Forbidden),
             new HttpRequestException("limited", null, HttpStatusCode.TooManyRequests),
             new ArgumentException("Missing releases.win.json")
         })
{
    var client = new FakeClient { Check = _ => throw exception };
    using var service = new AppUpdateService(client, State(exception.GetType().Name + Guid.NewGuid()));
    await service.CheckAsync(false, manual: true);
    Assert(service.Snapshot.Status == AppUpdateStatus.Error && client.DownloadCalls == 0, "Feed error should remain nonfatal.");
    Assert(exception is ArgumentException ? service.Snapshot.Message.Contains("installer update") : service.Snapshot.Message.Contains("limiting"), "Feed error message is not useful.");
}

// Damaged or impossible future cache timestamps must not disable checking.
foreach (var cache in new[] { "{broken", new string(' ', 4097), "{\"LastAttemptUtc\":\"2099-01-01T00:00:00Z\",\"LastCheckedUtc\":\"2099-01-01T00:00:00Z\"}" })
{
    var statePath = State("cache-" + Guid.NewGuid());
    Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
    await File.WriteAllTextAsync(statePath, cache);
    var client = new FakeClient();
    using var service = new AppUpdateService(client, statePath, new TestClock());
    await service.CheckAsync(false);
    Assert(client.CheckCalls == 1 && service.Snapshot.Status == AppUpdateStatus.UpToDate, "Damaged poll cache blocked updates.");
}

// A read-only/unwritable cache is an optimization failure, not an update failure.
{
    var statePath = Path.Combine(root, "cache-is-directory");
    Directory.CreateDirectory(statePath);
    var client = new FakeClient();
    using var service = new AppUpdateService(client, statePath);
    await service.CheckAsync(false, manual: true);
    Assert(service.Snapshot.Status == AppUpdateStatus.UpToDate, "An unwritable poll cache must not prevent a check.");
}

// The SDK adapter refuses another application's installation ownership.
{
    var packages = Path.Combine(root, "wrong-app-packages");
    Directory.CreateDirectory(packages);
    var locator = new TestVelopackLocator("Unrelated.Application", "1.0.0", packages);
    var manager = new UpdateManager(packages, new UpdateOptions { ExplicitChannel = "win" }, locator);
    var client = new VelopackAppUpdateClient(manager);
    Assert(!client.IsInstalled, "Another application's installation must never be updated.");
}

Console.WriteLine($"PASS: {checks} update checks. Fixtures: {root}");

[SupportedOSPlatform("windows")]
static async Task RunInstalledCheckAsync(string[] arguments)
{
    static string Required(string[] list, string key)
    {
        var index = Array.IndexOf(list, key);
        if (index < 0 || index + 1 >= list.Length) throw new ArgumentException("Missing " + key);
        return list[index + 1];
    }
    var installedRoot = Path.GetFullPath(Required(arguments, "--installed-root"));
    var feed = Path.GetFullPath(Required(arguments, "--feed"));
    var executable = Path.Combine(installedRoot, "current", "Klipp.Desktop.exe");
    var updateExe = Path.Combine(installedRoot, "Update.exe");
    if (!File.Exists(executable) || !File.Exists(updateExe) || !File.Exists(Path.Combine(installedRoot, "current", "sq.version")))
        throw new InvalidOperationException("Expected an actual Velopack installation with current/sq.version and Update.exe.");
    if (!File.Exists(Path.Combine(feed, "releases.win.json"))) throw new InvalidOperationException("Expected a local Windows release feed.");
    var logger = new NullVelopackLogger();
    var locator = new WindowsVelopackLocator(new InstalledProcess(executable, installedRoot), logger);
    if (locator.AppId != AppUpdateService.ApplicationId) throw new InvalidOperationException("Unexpected installed application ID.");
    var manager = new UpdateManager(feed, new UpdateOptions { ExplicitChannel = "win", AllowVersionDowngrade = false }, locator);
    var client = new VelopackAppUpdateClient(manager);
    var state = Path.Combine(Path.GetTempPath(), "Klipp-Native-Update-" + Guid.NewGuid().ToString("N"), "check-state.json");
    using var service = new AppUpdateService(client, state);
    service.StateChanged += (_, _) => Console.WriteLine($"{service.Snapshot.Status}: {service.Snapshot.Message} {service.Snapshot.ProgressPercent}%");
    await service.CheckAsync(downloadAutomatically: true, manual: true);
    var snapshot = service.Snapshot;
    Console.WriteLine(JsonSerializer.Serialize(snapshot));
    if (snapshot.Status is not (AppUpdateStatus.Ready or AppUpdateStatus.UpToDate)) throw new InvalidOperationException("Native SDK update check/download failed.");
    var expectedIndex = Array.IndexOf(arguments, "--expected-version");
    if (expectedIndex >= 0 && snapshot.AvailableVersion != arguments[expectedIndex + 1]) throw new InvalidOperationException("Unexpected prepared version.");
    if (arguments.Contains("--apply"))
    {
        var running = Process.GetProcessesByName("Klipp.Desktop");
        try
        {
            if (running.Length > 0) throw new InvalidOperationException("Close Klipp before running the native apply check.");
        }
        finally { foreach (var process in running) process.Dispose(); }
        if (snapshot.Status != AppUpdateStatus.Ready || !service.PrepareRestart()) throw new InvalidOperationException("Could not arm the native update helper.");
        Console.WriteLine("Native helper is armed and waiting for this harness to exit. Klipp will restart after apply.");
    }
}

sealed class TestClock : TimeProvider
{
    private DateTimeOffset _utc = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _utc;
    public void Advance(TimeSpan duration) => _utc += duration;
}

sealed class FakeClient : IAppUpdateClient
{
    public bool IsInstalled { get; set; } = true;
    public string CurrentVersion => "1.0.6";
    public AppUpdateRelease? Offered { get; set; }
    public AppUpdateRelease? Pending { get; set; }
    public Func<CancellationToken, Task<AppUpdateRelease?>>? Check { get; set; }
    public Func<AppUpdateRelease, Action<int>, CancellationToken, Task>? Download { get; set; }
    public bool PrepareFailure { get; set; }
    public AppUpdateRelease? Prepared { get; private set; }
    public int CheckCalls;
    public int DownloadCalls;
    public int PrepareCalls;
    public static AppUpdateRelease Release(string version) => new(version, new object());
    public AppUpdateRelease? GetPendingRestart() => Pending;
    public Task<AppUpdateRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref CheckCalls);
        return Check?.Invoke(cancellationToken) ?? Task.FromResult(Offered);
    }
    public async Task DownloadAsync(AppUpdateRelease release, Action<int> progress, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref DownloadCalls);
        if (Download is not null) { await Download(release, progress, cancellationToken); return; }
        foreach (var percent in new[] { -10, 20, 20, 50, 100, 120 }) progress(percent);
        Pending = release;
    }
    public void PrepareRestart(AppUpdateRelease release)
    {
        Interlocked.Increment(ref PrepareCalls);
        if (PrepareFailure) throw new IOException("Unable to launch helper");
        Prepared = release;
    }
}

sealed class InstalledProcess(string executable, string installedRoot) : IProcessImpl
{
    public string GetCurrentProcessPath() => executable;
    public uint GetCurrentProcessId() => (uint)Environment.ProcessId;
    public void Exit(int exitCode) => throw new InvalidOperationException("The graceful update test must never force process exit.");
    public void StartProcess(string exePath, IEnumerable<string> arguments, string workDir, bool showWindow)
    {
        var path = Path.GetFullPath(exePath);
        if (!path.Equals(Path.Combine(installedRoot, "Update.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected updater executable.");
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            WorkingDirectory = workDir,
            CreateNoWindow = !showWindow,
            WindowStyle = showWindow ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        _ = Process.Start(start) ?? throw new InvalidOperationException("Updater did not start.");
    }
}
