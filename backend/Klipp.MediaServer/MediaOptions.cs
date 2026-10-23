namespace Klipp.MediaServer;

public sealed class MediaOptions
{
    public string SupabaseProjectUrl { get; set; } = "";
    public string SupabasePublishableKey { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "";
    public string StorageRoot { get; set; } = "/srv/klipp/media/data";
    public string MountPoint { get; set; } = "/srv/klipp/media";
    public string ExpectedMountSource { get; set; } = "/dev/mapper/ubuntu--vg-klipp--media";
    public string SigningKeyBase64 { get; set; } = "";
    public string GatewaySecret { get; set; } = "";
    public bool UploadsEnabled { get; set; }
    public string BackupStatusPath { get; set; } = "/var/lib/klipp-backup/status.json";
    public string FfprobePath { get; set; } = "/usr/bin/ffprobe";
    public string[] AllowedOrigins { get; set; } = ["https://happyhamster135.github.io"];
    public long MinimumFreeBytes { get; set; } = 1024L * 1024 * 1024;
    public int MaximumVideoStreams { get; set; } = 8;
    public int MaximumVideoStreamsPerClient { get; set; } = 4;
    public int MaximumPlaybackStreams { get; set; } = 64;
    public int MaximumPlaybackStreamsPerClient { get; set; } = 16;
    public int PlaybackBytesPerSecond { get; set; } = 20 * 1024 * 1024;
    public int PlaybackBurstBytes { get; set; } = 4 * 1024 * 1024;
    public int PlaybackStreamTimeoutSeconds { get; set; } = 180;
    public const long MaximumVideoBytes = 16 * 1024 * 1024;
    public const long MaximumPosterBytes = 256 * 1024;
    public const double MaximumDurationSeconds = 60;

    public void Validate()
    {
        ValidateOrigin(SupabaseProjectUrl);
        ValidateOrigin(PublicBaseUrl);
        if (!SupabasePublishableKey.StartsWith("sb_publishable_", StringComparison.Ordinal))
            throw new InvalidOperationException("Configure a Supabase publishable key, not a secret or service-role key.");
        byte[] key;
        try { key = Convert.FromBase64String(SigningKeyBase64); }
        catch (FormatException) { throw new InvalidOperationException("A base64 signing key is required."); }
        if (key.Length != 32) throw new InvalidOperationException("The signing key must contain exactly 32 random bytes.");
        if (GatewaySecret.Length < 32) throw new InvalidOperationException("A separate server attestation secret is required.");
        if (!Path.IsPathFullyQualified(StorageRoot) || !Path.IsPathFullyQualified(MountPoint)
            || !Path.GetFullPath(StorageRoot).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(MountPoint)) + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) || string.IsNullOrWhiteSpace(ExpectedMountSource))
            throw new InvalidOperationException("The private data directory must be inside the required media mount.");
        if (!Path.IsPathFullyQualified(FfprobePath) || MinimumFreeBytes < 256L * 1024 * 1024)
            throw new InvalidOperationException("Configure the installed ffprobe path and a free-space reserve of at least 256 MiB.");
        if (MaximumVideoStreams is < 1 or > 128 || MaximumVideoStreamsPerClient < 1
            || MaximumVideoStreamsPerClient > MaximumVideoStreams || PlaybackBytesPerSecond is < 1048576 or > 268435456
            || MaximumPlaybackStreams is < 1 or > 256 || MaximumPlaybackStreamsPerClient < 1
            || MaximumPlaybackStreamsPerClient > MaximumPlaybackStreams || MaximumVideoStreams > MaximumPlaybackStreams
            || MaximumVideoStreamsPerClient > MaximumPlaybackStreamsPerClient
            || PlaybackBurstBytes is < 65536 or > 16777216 || PlaybackBurstBytes > PlaybackBytesPerSecond
            || PlaybackStreamTimeoutSeconds is < 30 or > 180)
            throw new InvalidOperationException("Configure bounded playback streams and an aggregate byte budget.");
        foreach (var origin in AllowedOrigins) ValidateOrigin(origin);
    }

    private static void ValidateOrigin(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("Service URLs and CORS origins must be HTTPS origins without credentials, paths or queries.");
    }
}

public sealed class MediaException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public static MediaException Unavailable() => new(404, "clip_unavailable", "This clip is not available.");
    public static MediaException StorageUnavailable() => new(503, "storage_unavailable", "Clip storage is temporarily unavailable. Try again later.");
}
