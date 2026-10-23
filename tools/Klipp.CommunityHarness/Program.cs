using System.Net;
using System.Text;
using System.Text.Json;
using Klipp.Core.Community;
using Klipp.Desktop.Services;

if (args.Contains("--live", StringComparer.Ordinal))
{
    Environment.ExitCode = await LiveCommunityChecks.RunAsync(args);
    return;
}

// Entirely local: Windows DPAPI, temporary files, and an in-memory HTTP handler. No hosted service required.
if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This harness verifies Windows credential protection.");
var parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Klipp.CommunityHarness"));
var root = Path.GetFullPath(Path.Combine(parent, Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var count = 0;
try
{
    var config = new CommunityConfiguration("https://community.example.com", "sb_publishable_test_public_key12345");
    var session = new CommunitySession
    {
        AccessToken = "test-access-never-plaintext-unique", RefreshToken = "test-refresh-never-plaintext-unique",
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), User = new CommunityUser(Guid.NewGuid(), "player@example.com")
    };

    await Check("DPAPI roundtrip protects tokens and identity on disk", async () =>
    {
        var folder = Path.Combine(root, "roundtrip");
        var store = new CommunitySessionStore(config, folder);
        Assert(!Directory.Exists(folder), "Constructing the store must not write credentials.");
        await store.SaveAsync(session);
        var file = Directory.GetFiles(folder).Single();
        var bytes = await File.ReadAllBytesAsync(file);
        var text = Encoding.UTF8.GetString(bytes);
        Assert(!text.Contains(session.AccessToken) && !text.Contains(session.RefreshToken)
            && !text.Contains(session.User.Email), "Credential or email present in plaintext.");
        var restored = await new CommunitySessionStore(config, folder).LoadAsync();
        Assert(restored?.AccessToken == session.AccessToken && restored.RefreshToken == session.RefreshToken
            && restored.User == session.User, "Protected roundtrip lost credentials.");
        Assert(!Directory.GetFiles(folder, "*.tmp").Any(), "Atomic save left a temporary file.");
    });

    await Check("backend project isolates both file names and DPAPI entropy", async () =>
    {
        var folder = Path.Combine(root, "scope");
        var first = new CommunitySessionStore(config, folder);
        var second = new CommunitySessionStore(new("https://another.example.com", config.PublishableKey), folder);
        await first.SaveAsync(session);
        var firstPath = Directory.GetFiles(folder).Single();
        Assert(await second.LoadAsync() is null, "Another project could load the saved session.");
        await second.SaveAsync(session);
        var secondPath = Directory.GetFiles(folder).Single(path => path != firstPath);
        File.Copy(firstPath, secondPath, overwrite: true);
        Assert(await second.LoadAsync() is null, "Copied credentials bypassed project-bound DPAPI entropy.");
        Assert(!File.Exists(secondPath), "Unreadable credentials were retained.");
        Assert((await first.LoadAsync())?.User == session.User, "Foreign read changed the original account.");
    });

    await Check("sign-out clear removes only this project's saved credential", async () =>
    {
        var folder = Path.Combine(root, "clear");
        var first = new CommunitySessionStore(config, folder);
        var second = new CommunitySessionStore(new("https://another.example.com", config.PublishableKey), folder);
        await first.SaveAsync(session);
        await second.SaveAsync(session);
        await first.ClearAsync();
        Assert(await first.LoadAsync() is null, "Cleared identity can still be loaded.");
        Assert((await second.LoadAsync())?.User == session.User, "Sign-out removed another project credential.");
    });

    await Check("corrupt protected files fail closed and are removed", async () =>
    {
        var folder = Path.Combine(root, "corrupt");
        var store = new CommunitySessionStore(config, folder);
        await store.SaveAsync(session);
        var path = Directory.GetFiles(folder).Single();
        await File.WriteAllTextAsync(path, "not a Windows protected credential");
        Assert(await store.LoadAsync() is null && !File.Exists(path), "Corrupt session was trusted or retained.");
    });

    await Check("locked credentials retain sign-out intent across a fresh store instance", async () =>
    {
        var folder = Path.Combine(root, "locked");
        var store = new CommunitySessionStore(config, folder);
        await store.SaveAsync(session);
        var path = Directory.GetFiles(folder).Single();
        await using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            try { await store.ClearAsync(); throw new Exception("Expected a locked-credential error."); }
            catch (IOException) { }
            Assert(File.Exists(path + ".signed-out"), "Failed sign-out did not preserve its intent.");
            Assert(await new CommunitySessionStore(config, folder).LoadAsync() is null, "A fresh process could restore a signed-out credential.");
        }
        await store.ClearAsync();
        Assert(!Directory.GetFiles(folder).Any(), "Sign-out retry did not remove credential and marker.");
    });

    await Check("oversized credentials are not read or trusted", async () =>
    {
        var folder = Path.Combine(root, "oversized");
        var store = new CommunitySessionStore(config, folder);
        await store.SaveAsync(session);
        var path = Directory.GetFiles(folder).Single();
        await File.WriteAllBytesAsync(path, new byte[128 * 1024 + 1]);
        Assert(await store.LoadAsync() is null && !File.Exists(path), "Oversized session was trusted or retained.");
    });

    await Check("cancelled save does not create a directory or plaintext fallback", async () =>
    {
        var folder = Path.Combine(root, "cancelled");
        var store = new CommunitySessionStore(config, folder);
        using var source = new CancellationTokenSource();
        source.Cancel();
        try { await store.SaveAsync(session, source.Token); throw new Exception("Expected cancellation."); }
        catch (OperationCanceledException) { }
        Assert(!Directory.Exists(folder), "Cancelled save wrote credentials.");
    });

    await Check("public deployment configuration loads without touching credentials", async () =>
    {
        var path = Path.Combine(root, "community.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { projectUrl = config.ProjectUrl, publishableKey = config.PublishableKey }));
        var loaded = CommunityConfigurationService.Load(path);
        Assert(loaded.IsConfigured && loaded.ProjectUrl == config.ProjectUrl && loaded.PublishableKey == config.PublishableKey, "Public configuration did not load.");
    });

    await Check("missing malformed oversized and unsafe configuration stays unavailable", async () =>
    {
        var path = Path.Combine(root, "invalid-community.json");
        Assert(!CommunityConfigurationService.Load(path).IsConfigured, "Missing configuration enabled Media.");
        foreach (var contents in new[]
        {
            "not json", "[]", "{\"projectUrl\":123}", new string(' ', 16 * 1024 + 1),
            "{\"projectUrl\":\"https://project.supabase.co\",\"publishableKey\":\"sb_secret_admin123456789\"}",
            "{\"projectUrl\":\"https://project.supabase.co\",\"publishableKey\":\"sb_publishable_YOUR_KEY_HERE\"}",
            "{\"projectUrl\":\"http://project.supabase.co\",\"publishableKey\":\"sb_publishable_test_public_key12345\"}"
        })
        {
            await File.WriteAllTextAsync(path, contents);
            Assert(!CommunityConfigurationService.Load(path).IsConfigured, "Unsafe deployment configuration enabled Media.");
        }
    });

    await Check("credential IO failure is a safe sign-in error without plaintext recovery", async () =>
    {
        var blocker = Path.Combine(root, "not-a-directory");
        await File.WriteAllTextAsync(blocker, "block directory creation");
        var store = new CommunitySessionStore(config, blocker);
        var payload = JsonSerializer.Serialize(new
        {
            access_token = session.AccessToken, refresh_token = session.RefreshToken,
            expires_at = session.ExpiresAt.ToUnixTimeSeconds(),
            user = new { id = session.User.Id, email = session.User.Email }
        });
        using var http = new HttpClient(new LocalHandler(payload));
        using var service = new CommunityAccountService(config, http, store);
        try { await service.VerifyEmailCodeAsync("player@example.com", "123456"); throw new Exception("Expected a safe persistence failure."); }
        catch (CommunityAccountException ex)
        {
            Assert(ex.Code == "session_storage" && !ex.Message.Contains(root) && !ex.Message.Contains(session.AccessToken), "Unsafe storage error.");
        }
        Assert(!service.IsSignedIn, "Sign-in succeeded without protecting its credential.");
        Assert(await File.ReadAllTextAsync(blocker) == "block directory creation", "Plaintext recovery overwrote another file.");
    });

    Console.WriteLine($"Passed {count} local Windows account/configuration checks. No live backend was contacted.");
}
finally
{
    // Only this explicitly created, validated temporary directory can be removed recursively.
    if (root.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        && Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

async Task Check(string name, Func<Task> run)
{
    await run();
    count++;
    Console.WriteLine($"PASS {name}");
}
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

sealed class LocalHandler(string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
}
