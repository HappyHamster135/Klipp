using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Klipp.Core.Community;

// One-time, owner-authorized transfer into the encrypted server backup. No API
// mutator is used here and signed capabilities never appear in logs/manifests.
internal static class LegacyMediaExporter
{
    internal sealed record FileDigest(string sha256, long size);

    internal static async Task ExportAsync(CommunityAccountService accounts, CommunityConfiguration configuration,
        HttpClient client, string outputDirectory)
    {
        var output = RequireOutputDirectory(outputDirectory);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var owner = accounts.User?.Id ?? throw new InvalidOperationException("An existing account is required.");
        var clips = new List<CommunityClip>();
        CommunityFeedCursor? cursor = null;
        do
        {
            var page = await accounts.GetMediaFeedAsync(cursor, mine: true, cancellationToken: deadline.Token);
            if (page.Clips.Any(clip => clip.OwnerId != owner) || clips.Count + page.Clips.Count > 40)
                throw new InvalidOperationException("The export exceeds its account boundary.");
            clips.AddRange(page.Clips);
            cursor = page.NextCursor;
        } while (cursor is not null);
        if (clips.Select(clip => clip.Id).Distinct().Count() != clips.Count
            || clips.Any(clip => clip.StorageProvider is not ("supabase" or "home")))
            throw new InvalidOperationException("Unexpected clip provider or duplicate identifier.");
        var legacy = clips.Where(clip => clip.StorageProvider == "supabase").OrderBy(clip => clip.Id).ToArray();
        if (legacy.Any(clip => !clip.IsPublished)) throw new InvalidOperationException("An unfinished legacy clip requires review.");
        Directory.CreateDirectory(output);
        RejectLinks(output);
        var files = new SortedDictionary<string, FileDigest>(StringComparer.Ordinal);
        foreach (var clip in legacy)
        {
            var directory = Path.Combine(output, clip.Id.ToString("D"));
            Directory.CreateDirectory(directory);
            RejectLinks(directory);
            foreach (var poster in new[] { false, true })
            {
                var uri = poster ? await accounts.GetClipPosterUriAsync(clip.Id, deadline.Token)
                    : await accounts.GetClipPlaybackUriAsync(clip.Id, deadline.Token);
                ValidateCapability(uri, configuration, clip.OwnerId, clip.Id, poster);
                var relative = clip.Id.ToString("D") + (poster ? "/poster.jpg" : "/video.mp4");
                var digest = await DownloadAsync(client, uri, Path.Combine(directory, poster ? "poster.jpg" : "video.mp4"),
                    poster ? CommunityMediaLimits.MaximumPosterBytes : CommunityMediaLimits.MaximumVideoBytes,
                    poster ? "image/jpeg" : "video/mp4", poster ? null : clip.ByteSize, deadline.Token);
                files.Add(relative, digest);
            }
        }
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new { files, clips = legacy.Select(clip => clip.Id.ToString("D")).ToArray() },
            new JsonSerializerOptions { WriteIndented = true });
        var manifestPath = Path.Combine(output, "manifest.json");
        await using (var destination = new FileStream(manifestPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await destination.WriteAsync(manifest, deadline.Token);
            await destination.FlushAsync(deadline.Token);
            destination.Flush(flushToDisk: true);
        }
        Console.WriteLine($"LEGACY_EXPORT_COMPLETE clips={legacy.Length} files={files.Count} bytes={files.Values.Sum(file => file.size)} manifest_sha256={Convert.ToHexStringLower(SHA256.HashData(manifest))}");
    }

    internal static string RequireOutputDirectory(string path)
    {
        var output = Path.GetFullPath(path);
        var artifacts = Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar;
        if (!output.StartsWith(artifacts, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Export into an explicit artifacts subdirectory.");
        RejectLinks(output);
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
            throw new InvalidOperationException("The export directory must be empty.");
        return output;
    }

    internal static void ValidateCapability(Uri uri, CommunityConfiguration configuration, Guid owner, Guid clip, bool poster)
    {
        if (!configuration.TryGetBaseUri(out var configured) || configured is null
            || uri.Scheme != "https" || uri.IdnHost != configured.IdnHost || uri.Port != configured.Port
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != $"/storage/v1/object/sign/{(poster ? "community-posters" : "community-videos")}/{owner:D}/{clip:D}/{(poster ? "poster.jpg" : "video.mp4")}"
            || !uri.Query.StartsWith("?token=", StringComparison.Ordinal) || uri.Query.Length is < 8 or > 4096)
            throw new InvalidOperationException("The media capability is outside the configured clip boundary.");
    }

    internal static async Task<FileDigest> DownloadAsync(HttpClient client, Uri uri, string path, long maximum,
        string contentType, long? expectedBytes, CancellationToken cancellation)
    {
        RejectLinks(path);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(contentType));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        var length = response.Content.Headers.ContentLength;
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != contentType
            || response.Content.Headers.ContentEncoding.Count != 0 || length is <= 0 || length > maximum
            || expectedBytes is <= 0 || expectedBytes > maximum
            || expectedBytes is not null && length is not null && expectedBytes != length)
            throw new InvalidOperationException("The media response failed bounded download checks.");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            long size = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var source = await response.Content.ReadAsStreamAsync(deadline.Token))
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, deadline.Token)) != 0)
                {
                    size += read;
                    if (size > maximum || expectedBytes is not null && size > expectedBytes)
                        throw new InvalidOperationException("The media response exceeded its bounded size.");
                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
                }
                if (size == 0 || length is not null && size != length || expectedBytes is not null && size != expectedBytes)
                    throw new InvalidOperationException("The media response ended before its complete file.");
                await destination.FlushAsync(deadline.Token);
                destination.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
            return new FileDigest(Convert.ToHexStringLower(hash.GetHashAndReset()), size);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Export paths cannot use symbolic links or junctions.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static async Task RunChecksAsync()
    {
        var root = RequireOutputDirectory(Path.Combine("artifacts", "legacy-export-checks-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var checks = 0;
        void Check(bool value) { if (!value) throw new InvalidOperationException("Legacy export check failed."); checks++; }
        void Denied(Action action)
        {
            try { action(); throw new Exception("Expected export boundary rejection."); }
            catch (InvalidOperationException) { checks++; }
        }
        try
        {
            var owner = Guid.NewGuid(); var clip = Guid.NewGuid();
            var config = new CommunityConfiguration("https://example.supabase.co", "sb_publishable_export_fixture");
            var uri = new Uri($"https://example.supabase.co/storage/v1/object/sign/community-videos/{owner:D}/{clip:D}/video.mp4?token=fixture");
            ValidateCapability(uri, config, owner, clip, false); checks++;
            Denied(() => ValidateCapability(new Uri(uri.AbsoluteUri.Replace("example.supabase.co", "attacker.invalid")), config, owner, clip, false));
            Denied(() => ValidateCapability(new Uri(uri.AbsoluteUri.Replace("https://", "http://")), config, owner, clip, false));
            Denied(() => ValidateCapability(uri, config, Guid.NewGuid(), clip, false));
            Denied(() => ValidateCapability(uri, config, owner, clip, true));
            Denied(() => RequireOutputDirectory(Path.GetTempPath()));
            var body = new byte[] { 1, 2, 3, 4 };
            async Task<FileDigest> Download(HttpStatusCode status, string mime, byte[] data, long maximum = 4, long? expected = 4)
            {
                using var http = new HttpClient(new ExportFixtureHandler(request =>
                {
                    Check(request.Method == HttpMethod.Get && request.Headers.Authorization is null && !request.Headers.Contains("apikey"));
                    var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(data) };
                    response.Content.Headers.ContentType = new MediaTypeHeaderValue(mime);
                    return response;
                }));
                return await DownloadAsync(http, uri, Path.Combine(root, Guid.NewGuid().ToString("N")), maximum, "video/mp4", expected, default);
            }
            var digest = await Download(HttpStatusCode.OK, "video/mp4", body);
            Check(digest.size == 4 && digest.sha256 == Convert.ToHexStringLower(SHA256.HashData(body)));
            foreach (var attempt in new Func<Task>[]
            {
                async () => { await Download(HttpStatusCode.Redirect, "video/mp4", body); },
                async () => { await Download(HttpStatusCode.OK, "text/html", body); },
                async () => { await Download(HttpStatusCode.OK, "video/mp4", body, maximum:3); },
                async () => { await Download(HttpStatusCode.OK, "video/mp4", body, expected:3); }
            })
            {
                try { await attempt(); throw new Exception("Expected bounded response rejection."); }
                catch (InvalidOperationException) { checks++; }
            }
            Denied(() => RequireOutputDirectory(root));
            foreach (var (data, maximum, declared) in new[]
            {
                (new byte[5], 4L, (long?)null),
                (new byte[2], 4L, (long?)4)
            })
            {
                using var http = new HttpClient(new ExportFixtureHandler(_ =>
                {
                    var content = new StreamContent(new NonSeekableFixture(new MemoryStream(data)));
                    content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
                    content.Headers.ContentLength = declared;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                }));
                try
                {
                    await DownloadAsync(http, uri, Path.Combine(root, Guid.NewGuid().ToString("N")), maximum, "video/mp4", null, default);
                    throw new Exception("Expected streaming size rejection.");
                }
                catch (InvalidOperationException) { checks++; }
            }
            Check(!Directory.EnumerateFiles(root,"*.partial").Any());
            Console.WriteLine($"LEGACY_EXPORT_CHECKS_COMPLETE checks={checks}");
        }
        finally
        {
            var absolute = Path.GetFullPath(root);
            if (!absolute.StartsWith(Path.GetFullPath("artifacts") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Export test cleanup escaped artifacts.");
            Directory.Delete(absolute, recursive:true);
        }
    }

    private sealed class ExportFixtureHandler(Func<HttpRequestMessage,HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }

    private sealed class NonSeekableFixture(Stream source) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer,int offset,int count) => source.Read(buffer,offset,count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancellationToken=default) => source.ReadAsync(buffer,cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset,SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer,int offset,int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if(disposing) source.Dispose(); base.Dispose(disposing); }
    }
}
