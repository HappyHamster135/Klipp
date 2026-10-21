using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Core.Models;
using Klipp.Encoding.Audio;
using Klipp.Encoding.FFmpeg;
using Klipp.Encoding.Muxing;

namespace Klipp.Desktop.Services;

public sealed record RecoverableRecording(string Id, DateTimeOffset StartedUtc, string SuggestedFileName,
    long Bytes, double DurationSeconds, bool CanRecover, string? FailureReason);
public sealed record RecoveredRecording(string OutputPath, double DurationSeconds, IReadOnlyList<RecordingSessionMarker> Markers);
public sealed record RecordingStartedEventArgs(string SessionId, string OutputPath, DateTimeOffset StartedUtc);

/// <summary>Journals capture sessions and publishes verified files without replacing an existing recording.</summary>
public sealed class RecordingRecoveryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly FFmpegLocator _locator;
    private readonly RecordingBookmarkService _bookmarks;
    public string RootDirectory { get; }

    public RecordingRecoveryService(string? rootDirectory = null, FFmpegLocator? locator = null,
        RecordingBookmarkService? bookmarks = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Klipp", "temp", "recordings"));
        _locator = locator ?? new FFmpegLocator();
        _bookmarks = bookmarks ?? new RecordingBookmarkService();
        Directory.CreateDirectory(RootDirectory);
    }

    internal RecordingSessionHandle CreateSession(string outputPath, int frameRate, bool audioExpected,
        int audioChannels, string audioTrackName, IReadOnlyList<string>? audioTrackNames = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var directory = SessionDirectory(id);
        Directory.CreateDirectory(directory);
        var lease = AcquireLease(directory);
        var manifest = new RecordingSessionManifest
        {
            Id = id, OutputPath = Path.GetFullPath(outputPath), SuggestedFileName = Path.GetFileName(outputPath),
            FrameRate = frameRate, AudioExpected = audioExpected, AudioChannels = audioChannels, AudioTrackName = audioTrackName,
            AudioTrackNames = audioTrackNames?.ToList() ?? []
        };
        var session = new RecordingSessionHandle(directory, manifest, lease);
        try { SaveSession(session); return session; }
        catch { session.Dispose(); DeleteKnownSession(directory); throw; }
    }

    internal void SaveSession(RecordingSessionHandle session) => SaveManifest(session.Directory, session.Manifest);

    /// <summary>Moves a stopped rolling buffer as one directory, without another media-sized copy.</summary>
    internal Task<string?> PreserveReplayBufferAsync(string segmentDirectory, double durationSeconds,
        int frameRate, string? failureReason = null, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.GetFullPath(segmentDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(source) || GetReplaySegments(source).Count == 0) return null;
        if (IsLink(source) || source.Equals(RootDirectory, StringComparison.OrdinalIgnoreCase) ||
            RootDirectory.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(RootDirectory), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The instant clip buffer must remain on its original local capture volume.");
        var id = Guid.NewGuid().ToString("N");
        var destination = SessionDirectory(id);
        // Source and destination are validated absolute directories on the same
        // volume. Rename preserves all buffered media atomically, even if Windows
        // stops before the small journal can be written. Discovery also recognizes
        // numbered MP4 segments without a journal.
        Directory.Move(source, destination);
        var manifest = new RecordingSessionManifest
        {
            Id = id, IsReplayBuffer = true, ReplayDurationSeconds = Math.Min(86400,
                double.IsFinite(durationSeconds) ? Math.Max(0, durationSeconds) : 0),
            FrameRate = Math.Clamp(frameRate, 1, 240), State = "needs-recovery",
            SuggestedFileName = $"Recovered instant clip {DateTime.Now:yyyyMMdd_HHmmss}.mp4",
            FailureReason = "An instant clip could not be saved. The available rolling buffer was kept for recovery." +
                (string.IsNullOrWhiteSpace(failureReason) ? "" : " " + failureReason)
        };
        try
        {
            using var lease = AcquireLease(destination);
            SaveManifest(destination, manifest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Replay journal could not be written; numbered segments remain discoverable: {exception}");
        }
        return id;
    }, cancellationToken);

    private static void SaveManifest(string directory, RecordingSessionManifest manifest)
    {
        var temporary = Path.Combine(directory, "session.json.tmp");
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(file, manifest, JsonOptions);
            file.Flush(flushToDisk: true);
        }
        File.Move(temporary, Path.Combine(directory, "session.json"), overwrite: true);
    }

    public Task<IReadOnlyList<RecoverableRecording>> GetRecoverableAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<RecoverableRecording>>(() =>
        {
            var found = new List<RecoverableRecording>();
            foreach (var directory in Directory.EnumerateDirectories(RootDirectory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = Path.GetFileName(directory);
                if (!Guid.TryParseExact(id, "N", out _) || IsLink(directory)) continue;
                using var lease = TryAcquireLease(directory);
                if (lease is null) continue;
                var manifest = ReadManifest(directory, id);
                if (manifest.State == "complete") continue;
                var replay = GetReplaySegments(directory);
                if (manifest.IsReplayBuffer || replay.Count > 0)
                {
                    if (replay.Any(path => !CanReadQuiescentFile(path))) continue;
                    var replayBytes = replay.Sum(path => SafeLength(path));
                    if (replayBytes > 0 || manifest.PublishedOutputPath is not null)
                        found.Add(new(id, manifest.StartedUtc, SafeFileName(manifest.SuggestedFileName), replayBytes,
                            manifest.DurationSeconds, replay.Any(HasFinalizedMp4Index) || manifest.PublishedOutputPath is not null,
                            manifest.FailureReason ?? "The available instant clip buffer was kept. Recovery includes complete segments."));
                    continue;
                }
                var bytes = SafeLength(Path.Combine(directory, "video.mp4")) + SafeLength(Path.Combine(directory, "audio.wav"));
                var videoBytes = SafeLength(Path.Combine(directory, "video.mp4"));
                if (bytes == 0 && manifest.PublishedOutputPath is null) continue;
                // Old versions did not hold a lease. Exclude a file still open
                // for writing before presenting an older session as interrupted.
                if (!CanReadQuiescentFile(Path.Combine(directory, "video.mp4"))) continue;
                found.Add(new(id, manifest.StartedUtc, SafeFileName(manifest.SuggestedFileName), bytes,
                    manifest.DurationSeconds, videoBytes > 0 || manifest.PublishedOutputPath is not null,
                    manifest.FailureReason ?? (videoBytes == 0 ? "No complete video frames were found." : null)));
            }
            return found.OrderByDescending(item => item.StartedUtc).ToArray();
        }, cancellationToken);

    public async Task<RecoveredRecording> RecoverAsync(string sessionId, string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        var directory = SessionDirectory(sessionId);
        var lease = AcquireLease(directory);
        using var session = new RecordingSessionHandle(directory, ReadManifest(directory, sessionId), lease);
        try
        {
            var result = await PublishAsync(session, Path.GetFullPath(destinationDirectory), recoverInterrupted: true,
                cancellationToken).ConfigureAwait(false);
            CompleteCleanup(session);
            return result;
        }
        catch (Exception exception)
        {
            SaveFailure(session, exception);
            throw;
        }
    }

    public Task DiscardAsync(string sessionId, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = SessionDirectory(sessionId);
        var lease = AcquireLease(directory);
        using var session = new RecordingSessionHandle(directory, ReadManifest(directory, sessionId), lease);
        CompleteCleanup(session, throwOnFailure: true);
    }, cancellationToken);

    internal async Task<RecoveredRecording> FinishActiveAsync(RecordingSessionHandle session,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(session.Manifest.OutputPath)
                        ?? throw new InvalidOperationException("Choose a folder to save the recording.");
        return await PublishAsync(session, directory, recoverInterrupted: false, cancellationToken).ConfigureAwait(false);
    }

    internal void CompleteCleanup(RecordingSessionHandle session, bool throwOnFailure = false)
    {
        try { DeleteKnownSession(session.Directory, throwOnFailure, keepLease: true); }
        finally { session.Dispose(); }
        TryDeleteFile(Path.Combine(session.Directory, "active.lock"));
        try { Directory.Delete(session.Directory, recursive: false); }
        catch when (!throwOnFailure) { }
    }

    internal void SaveFailure(RecordingSessionHandle session, Exception exception)
    {
        session.Manifest.State = "needs-recovery";
        session.Manifest.FailureReason = "Saving did not finish. " + exception.Message;
        try { SaveSession(session); } catch { /* The original media still remains recoverable without its journal. */ }
    }

    private async Task<RecoveredRecording> PublishAsync(RecordingSessionHandle session, string destinationDirectory,
        bool recoverInterrupted, CancellationToken cancellationToken)
    {
        var manifest = session.Manifest;
        var existing = await FindPublishedAsync(manifest, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return await FinishPublishedAsync(session, existing, cancellationToken).ConfigureAwait(false);
        if (manifest.IsReplayBuffer || GetReplaySegments(session.Directory).Count > 0)
            return await PublishReplayAsync(session, destinationDirectory, cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(destinationDirectory);
        var sourceVideo = Path.Combine(session.Directory, "video.mp4");
        if (SafeLength(sourceVideo) == 0) throw new InvalidOperationException("This session contains no video frames to recover.");
        var token = Guid.NewGuid().ToString("N");
        var repairedVideo = Path.Combine(session.Directory, $".recovery-{token}.mp4");
        var repairedAudio = Path.Combine(session.Directory, $".recovery-{token}.wav");
        var stagedOutput = Path.Combine(destinationDirectory, $".klipp-recovering-{token}.mp4");
        try
        {
            if (recoverInterrupted)
            {
                await RunFfmpegAsync(["-v", "error", "-fflags", "+discardcorrupt", "-err_detect", "ignore_err",
                    "-i", sourceVideo, "-map", "0:v:0", "-c:v", "copy", "-an", "-movflags", "+faststart", "-n", repairedVideo],
                    cancellationToken).ConfigureAwait(false);
                sourceVideo = repairedVideo;
            }
            string? audio = null;
            var audioPath = Path.Combine(session.Directory, "audio.wav");
            if (IsLink(audioPath)) throw new IOException("The recorded audio cannot be a link.");
            var hasAudio = RecordingWaveRepair.TryCreateCopy(audioPath, repairedAudio, out var channels);
            if (hasAudio)
            {
                if (channels is not (2 or 4 or 6 or 8))
                    throw new InvalidOperationException("The recorded audio layout could not be restored safely. The original files have been kept.");
                audio = repairedAudio;
                manifest.AudioChannels = channels;
            }
            else if (SafeLength(audioPath) > 0 && !(channels is 2 or 4 or 6 or 8 && SafeLength(audioPath) <= 128))
                throw new InvalidOperationException("The recorded audio could not be repaired safely. The original video and audio have been kept.");
            var offset = double.IsFinite(manifest.AudioOffsetSeconds ?? 0) ? manifest.AudioOffsetSeconds ?? 0 : 0;
            var syntheticAudioTimestamp = (long)Math.Round(Math.Clamp(offset, -86400, 86400) * Stopwatch.Frequency);
            await new FFmpegMp4Muxer(_locator).MuxAsync(sourceVideo, audio, stagedOutput, 0,
                audio is null ? null : syntheticAudioTimestamp, cancellationToken,
                manifest.AudioChannels is 2 or 4 or 6 or 8 ? manifest.AudioChannels : 2,
                manifest.AudioTrackName, manifest.AudioTrackNames).ConfigureAwait(false);
            _ = await ProbeVideoAsync(stagedOutput, cancellationToken).ConfigureAwait(false);
            manifest.PublishedLength = new FileInfo(stagedOutput).Length;
            manifest.PublishedSha256 = await HashFileAsync(stagedOutput, cancellationToken).ConfigureAwait(false);
            manifest.State = "publishing";
            for (var attempt = 0; ; attempt++)
            {
                var name = SafeFileName(manifest.SuggestedFileName);
                if (attempt > 0) name = $"{Path.GetFileNameWithoutExtension(name)} ({attempt + 1}).mp4";
                var output = Path.Combine(destinationDirectory, name);
                if (File.Exists(output)) continue;
                manifest.PublishedOutputPath = output;
                SaveSession(session);
                try { File.Move(stagedOutput, output, overwrite: false); break; }
                catch (IOException) when (File.Exists(output)) { }
            }
            manifest.State = "saved";
            SaveSession(session);
            return await FinishPublishedAsync(session, manifest.PublishedOutputPath!, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(repairedVideo);
            TryDeleteFile(repairedAudio);
            TryDeleteFile(stagedOutput);
        }
    }

    private async Task<RecoveredRecording> FinishPublishedAsync(RecordingSessionHandle session, string output,
        CancellationToken cancellationToken)
    {
        var duration = await ProbeVideoAsync(output, cancellationToken).ConfigureAwait(false);
        var markers = (session.Manifest.Markers ?? []).Where(marker => marker is not null &&
                double.IsFinite(marker.TimeSeconds) && marker.TimeSeconds >= 0)
            .Select(marker => marker with { TimeSeconds = Math.Min(marker.TimeSeconds, duration),
                Label = string.IsNullOrWhiteSpace(marker.Label) ? "Highlight" : marker.Label[..Math.Min(80, marker.Label.Length)] })
            .ToArray();
        await _bookmarks.SaveAsync(output, markers, duration, cancellationToken).ConfigureAwait(false);
        session.Manifest.State = "complete";
        session.Manifest.FailureReason = null;
        SaveSession(session);
        return new(output, duration, markers);
    }

    private async Task<RecoveredRecording> PublishReplayAsync(RecordingSessionHandle session,
        string destinationDirectory, CancellationToken cancellationToken)
    {
        var segments = GetReplaySegments(session.Directory).Where(HasFinalizedMp4Index).ToArray();
        if (segments.Length == 0)
            throw new InvalidOperationException("No complete instant clip segments were found. The original buffer files have been kept.");
        Directory.CreateDirectory(destinationDirectory);
        var token = Guid.NewGuid().ToString("N");
        var concatPath = Path.Combine(session.Directory, $".recovery-{token}.concat");
        var stagedOutput = Path.Combine(destinationDirectory, $".klipp-recovering-{token}.mp4");
        try
        {
            var lines = segments.Select(path => $"file '{path.Replace('\\', '/').Replace("'", "'\\''")}'");
            await File.WriteAllLinesAsync(concatPath, lines, cancellationToken).ConfigureAwait(false);
            // Every saved stereo AAC stem already has its name, disposition and
            // sample alignment. Copy all streams without decoding or remixing.
            await RunFfmpegAsync(["-v", "error", "-f", "concat", "-safe", "0", "-i", concatPath,
                "-map", "0:v:0", "-map", "0:a?", "-c", "copy", "-movflags", "+faststart", "-n", stagedOutput],
                cancellationToken).ConfigureAwait(false);
            _ = await ProbeVideoAsync(stagedOutput, cancellationToken).ConfigureAwait(false);
            var manifest = session.Manifest;
            manifest.PublishedLength = new FileInfo(stagedOutput).Length;
            manifest.PublishedSha256 = await HashFileAsync(stagedOutput, cancellationToken).ConfigureAwait(false);
            manifest.State = "publishing";
            for (var attempt = 0; ; attempt++)
            {
                var name = SafeFileName(manifest.SuggestedFileName);
                if (attempt > 0) name = $"{Path.GetFileNameWithoutExtension(name)} ({attempt + 1}).mp4";
                var output = Path.Combine(destinationDirectory, name);
                if (File.Exists(output)) continue;
                manifest.PublishedOutputPath = output;
                SaveSession(session);
                try { File.Move(stagedOutput, output, overwrite: false); break; }
                catch (IOException) when (File.Exists(output)) { }
            }
            manifest.State = "saved";
            SaveSession(session);
            return await FinishPublishedAsync(session, manifest.PublishedOutputPath!, cancellationToken).ConfigureAwait(false);
        }
        finally { TryDeleteFile(concatPath); TryDeleteFile(stagedOutput); }
    }

    private static IReadOnlyList<string> GetReplaySegments(string directory)
    {
        if (!Directory.Exists(directory) || IsLink(directory)) return [];
        return Directory.EnumerateFiles(directory, "seg_*.mp4")
            .Where(path => IsReplaySegmentName(Path.GetFileName(path)) && !IsLink(path))
            .Select(path => new FileInfo(path)).OrderBy(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase).Select(file => file.FullName).Take(1000).ToArray();
    }

    private static bool IsReplaySegmentName(string name) => name.Length == 11 &&
        name.StartsWith("seg_", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) &&
        name.AsSpan(4, 3).ToArray().All(char.IsAsciiDigit);

    private static bool HasFinalizedMp4Index(string path)
    {
        if (IsLink(path)) return false;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> header = stackalloc byte[16];
            for (var atoms = 0; atoms < 10000 && file.Position + 8 <= file.Length; atoms++)
            {
                var start = file.Position;
                file.ReadExactly(header[..8]);
                var length = (long)BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
                var headerLength = 8;
                if (length == 1)
                {
                    if (file.Position + 8 > file.Length) return false;
                    file.ReadExactly(header.Slice(8, 8));
                    var extended = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
                    if (extended > long.MaxValue) return false;
                    length = (long)extended;
                    headerLength = 16;
                }
                else if (length == 0) length = file.Length - start;
                if (length < headerLength || length > file.Length - start) return false;
                if (header.Slice(4, 4).SequenceEqual("moov"u8)) return true;
                file.Position = start + length;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return false;
    }

    private async Task<string?> FindPublishedAsync(RecordingSessionManifest manifest, CancellationToken cancellationToken)
    {
        var path = manifest.PublishedOutputPath;
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(manifest.PublishedSha256) ||
            SafeLength(path) != manifest.PublishedLength || manifest.PublishedLength <= 0) return null;
        return string.Equals(await HashFileAsync(path, cancellationToken).ConfigureAwait(false),
            manifest.PublishedSha256, StringComparison.Ordinal) ? path : null;
    }

    private async Task<double> ProbeVideoAsync(string path, CancellationToken cancellationToken)
    {
        var probe = await _locator.GetFFprobePathAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var output = await RunProcessAsync(probe, ["-v", "error", "-read_intervals", "%+#1", "-count_packets", "-select_streams", "v:0",
            "-show_entries", "stream=nb_read_packets:format=duration", "-of", "json", path], cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(output);
        if (!json.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0 ||
            !streams[0].TryGetProperty("nb_read_packets", out var count) ||
            !long.TryParse(count.GetString(), out var packets) || packets <= 0 ||
            !json.RootElement.TryGetProperty("format", out var format) || !format.TryGetProperty("duration", out var duration) ||
            !double.TryParse(duration.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds <= 0)
            throw new InvalidOperationException("The recovered video could not be verified. Its original capture files have been kept.");
        return seconds;
    }

    private async Task RunFfmpegAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var ffmpeg = await _locator.GetFFmpegPathAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await RunProcessAsync(ffmpeg, arguments, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> RunProcessAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the recording recovery tool.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch { try { process.Kill(entireProcessTree: true); } catch { } throw; }
        var text = await output.ConfigureAwait(false);
        var errorText = await error.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException("Recording recovery could not finish. " +
            (errorText.Length > 1000 ? errorText[^1000..] : errorText.Trim()));
        return text;
    }

    private RecordingSessionManifest ReadManifest(string directory, string id)
    {
        try
        {
            if (!IsLink(Path.Combine(directory, "session.json")))
            {
                var path = Path.Combine(directory, "session.json");
                if (SafeLength(path) > 8 * 1024 * 1024) throw new IOException("The session journal is too large.");
                var manifest = JsonSerializer.Deserialize<RecordingSessionManifest>(File.ReadAllText(path), JsonOptions);
                if (manifest is not null && manifest.Version == 1)
                {
                    manifest.Id = id;
                    manifest.SuggestedFileName = SafeFileName(manifest.SuggestedFileName);
                    return manifest;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
        var replay = GetReplaySegments(directory).Count > 0;
        return new() { Id = id, StartedUtc = Directory.GetCreationTimeUtc(directory), IsReplayBuffer = replay,
            SuggestedFileName = replay ? "Recovered instant clip.mp4" : "Recovered recording.mp4", State = "needs-recovery",
            FailureReason = "An older or incomplete recording was found. Available video and audio will be preserved." };
    }

    private string SessionDirectory(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new ArgumentException("Choose a valid recording recovery session.", nameof(id));
        var directory = Path.GetFullPath(Path.Combine(RootDirectory, id));
        if (!directory.StartsWith(RootDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) || IsLink(directory))
            throw new IOException("The recording recovery location is not a regular session folder.");
        return directory;
    }

    private static FileStream AcquireLease(string directory)
    {
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("The recovery session no longer exists.");
        if (IsLink(Path.Combine(directory, "active.lock"))) throw new IOException("The recovery session lock is invalid.");
        try { return new FileStream(Path.Combine(directory, "active.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) { throw new IOException("This recording is still in use. Stop capture before recovering it.", exception); }
    }

    private static FileStream? TryAcquireLease(string directory)
    {
        try { return AcquireLease(directory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool CanReadQuiescentFile(string path)
    {
        if (!File.Exists(path)) return true;
        if (IsLink(path)) return false;
        try { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsLink(string path) => (File.Exists(path) || Directory.Exists(path)) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static long SafeLength(string path)
    {
        try { return File.Exists(path) && !IsLink(path) ? new FileInfo(path).Length : 0; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return 0; }
    }
    private static string SafeFileName(string? name)
    {
        name = Path.GetFileName(name ?? string.Empty);
        var clean = new string(name.Where(character => !Path.GetInvalidFileNameChars().Contains(character)).ToArray());
        clean = Path.GetFileNameWithoutExtension(clean).Trim().TrimEnd('.');
        return (string.IsNullOrWhiteSpace(clean) ? "Recovered recording" : clean[..Math.Min(150, clean.Length)]) + ".mp4";
    }
    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        return Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false));
    }
    private static void TryDeleteFile(string path) { try { if (!IsLink(path)) File.Delete(path); } catch { } }
    private static void DeleteKnownSession(string directory, bool throwOnFailure = false, bool keepLease = false)
    {
        if (IsLink(directory)) throw new IOException("The recovery session folder cannot be a link.");
        foreach (var name in new[] { "video.mp4", "audio.wav" })
        {
            var path = Path.Combine(directory, name);
            if (IsLink(path)) throw new IOException("A capture file cannot be a link.");
            if (throwOnFailure) File.Delete(path); else TryDeleteFile(path);
        }
        foreach (var path in GetReplaySegments(directory))
        {
            if (throwOnFailure) File.Delete(path); else TryDeleteFile(path);
        }
        if (GetReplaySegments(directory).Count > 0) return;
        if (File.Exists(Path.Combine(directory, "video.mp4")) || File.Exists(Path.Combine(directory, "audio.wav"))) return;
        foreach (var path in Directory.EnumerateFiles(directory, ".recovery-*"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.StartsWith(".recovery-", StringComparison.Ordinal) && Guid.TryParseExact(name[10..], "N", out _) &&
                Path.GetExtension(path) is ".mp4" or ".wav" or ".concat") TryDeleteFile(path);
        }
        foreach (var name in new[] { "session.json", "session.json.tmp", "preserve-replay.flag" }) TryDeleteFile(Path.Combine(directory, name));
        if (keepLease) return;
        TryDeleteFile(Path.Combine(directory, "active.lock"));
        try { Directory.Delete(directory, recursive: false); }
        catch when (!throwOnFailure) { }
    }
}

internal sealed class RecordingSessionHandle(string directory, RecordingSessionManifest manifest, FileStream lease) : IDisposable
{
    private FileStream? _lease = lease;
    internal string Directory { get; } = directory;
    internal RecordingSessionManifest Manifest { get; } = manifest;
    public void Dispose() => Interlocked.Exchange(ref _lease, null)?.Dispose();
}
