using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;

internal static class ReplayRecoveryChecks
{
    public static async Task RunAsync(FFmpegLocator locator, string root)
    {
        var qa = Path.Combine(root, "replay-recovery");
        var template = Path.Combine(qa, "template");
        Directory.CreateDirectory(template);
        var output = Path.Combine(qa, "outputs");
        Directory.CreateDirectory(output);
        var recovery = new RecordingRecoveryService(Path.Combine(qa, "sessions"), locator,
            new RecordingBookmarkService(Path.Combine(qa, "bookmarks")));
        var checks = 0;
        void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); checks++; }
        var args = new List<string> { "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=20:duration=2.2" };
        foreach (var frequency in new[] { 440, 880, 1320, 1760 })
            args.AddRange(["-f", "lavfi", "-i", $"sine=frequency={frequency}:sample_rate=48000:duration=2.2"]);
        args.AddRange(["-filter_complex", "[1:a][2:a][3:a][4:a]amix=inputs=4:normalize=0[mix]",
            "-map", "0:v:0", "-map", "[mix]", "-map", "1:a", "-map", "2:a", "-map", "3:a", "-map", "4:a",
            "-c:v", "libx264", "-preset", "ultrafast", "-g", "20", "-bf", "0", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "64k", "-ac", "2"]);
        var expectedNames = new[] { "Playback mix", "Game audio", "Microphone", "Discord", "Music" };
        for (var index = 0; index < expectedNames.Length; index++)
            args.AddRange([$"-metadata:s:a:{index}", $"handler_name={expectedNames[index]}",
                $"-disposition:a:{index}", index == 0 ? "default" : "0"]);
        args.AddRange(["-f", "segment", "-segment_time", "1", "-reset_timestamps", "1", "-n", Path.Combine(template, "seg_%03d.mp4")]);
        await Run(locator.FFmpegPath, args);
        var templateFiles = Directory.GetFiles(template, "seg_*.mp4").Order().ToArray();
        Require(templateFiles.Length >= 2, "The replay fixture did not create completed segments.");

        string Buffer(string name)
        {
            var directory = Path.Combine(qa, name);
            Directory.CreateDirectory(directory);
            for (var index = 0; index < templateFiles.Length; index++)
            {
                var path = Path.Combine(directory, Path.GetFileName(templateFiles[index]));
                File.Copy(templateFiles[index], path);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(-10 + index));
            }
            return directory;
        }

        var buffer = Buffer("normal-buffer");
        // Incomplete last segment and unrelated data are retained during archive.
        await File.WriteAllBytesAsync(Path.Combine(buffer, "seg_099.mp4"), new byte[] { 0, 0, 0, 8, 102, 116, 121, 112 });
        await File.WriteAllTextAsync(Path.Combine(buffer, "user-note.txt"), "unknown file stays available");
        var sourceHashes = Directory.GetFiles(buffer, "seg_*.mp4").ToDictionary(path => Path.GetFileName(path)!, Hash);
        var id = await recovery.PreserveReplayBufferAsync(buffer, 2.2, 20, "Disposable output-drive failure");
        Require(id is not null && !Directory.Exists(buffer), "Failed replay buffer was not moved atomically into recovery.");
        var session = Path.Combine(recovery.RootDirectory, id!);
        Require(sourceHashes.All(pair => File.Exists(Path.Combine(session, pair.Key!)) && Hash(Path.Combine(session, pair.Key!)) == pair.Value),
            "Archiving changed or omitted source segments.");
        var listed = await recovery.GetRecoverableAsync();
        Require(listed.Count == 1 && listed[0].CanRecover && listed[0].DurationSeconds == 2.2,
            "The archived replay buffer was not offered by recording recovery.");
        var blockedOutput = Path.Combine(qa, "blocked-output");
        await File.WriteAllTextAsync(blockedOutput, "not a folder");
        var failed = false;
        try { await recovery.RecoverAsync(id!, blockedOutput); }
        catch (IOException) { failed = true; }
        Require(failed && sourceHashes.All(pair => Hash(Path.Combine(session, pair.Key!)) == pair.Value),
            "A failed replay publication discarded its only buffered media.");
        // Emulate Windows stopping after the atomic directory move, before the
        // journal write. Fixed numbered segments must still be discoverable.
        File.Delete(Path.Combine(session, "session.json"));
        listed = await recovery.GetRecoverableAsync();
        Require(listed.Count == 1 && listed[0].CanRecover, "A replay buffer without a completed journal became invisible.");
        var result = await recovery.RecoverAsync(id!, output);
        using (var probe = JsonDocument.Parse(await Run(locator.FFprobePath,
                   ["-v", "error", "-show_streams", "-show_format", "-of", "json", result.OutputPath])))
        {
            var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var audio = streams.Where(stream => stream.GetProperty("codec_type").GetString() == "audio").ToArray();
            Require(audio.Length == 5 && audio.All(stream => stream.GetProperty("codec_name").GetString() == "aac" && stream.GetProperty("channels").GetInt32() == 2),
                "Replay recovery lost a named stereo AAC stem or its playback mix.");
            Require(audio.Select(stream => stream.GetProperty("tags").GetProperty("handler_name").GetString()).SequenceEqual(expectedNames),
                "Replay recovery changed the saved game/mic/Discord/music names.");
            var seconds = double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
            Require(seconds > 2 && seconds < 2.6, "Replay concat changed the buffered time interval.");
        }
        await Run(locator.FFmpegPath, ["-v", "error", "-i", result.OutputPath, "-f", "null", "-"]);
        checks++;
        Require(File.Exists(Path.Combine(session, "user-note.txt")) && !Directory.EnumerateFiles(session, "seg_*.mp4").Any(),
            "Verified replay cleanup removed unrelated data or retained its own source media.");

        var canceledBuffer = Buffer("canceled-buffer");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            var canceled = false;
            try { await recovery.PreserveReplayBufferAsync(canceledBuffer, 2.2, 20, cancellationToken: cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Require(canceled && Directory.GetFiles(canceledBuffer, "seg_*.mp4").Length == templateFiles.Length,
                "Canceled preservation changed its original buffer.");
        }
        var activeBuffer = Buffer("active-buffer");
        using (var active = new FileStream(Path.Combine(activeBuffer, "seg_000.mp4"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            failed = false;
            try { await recovery.PreserveReplayBufferAsync(activeBuffer, 2.2, 20); }
            catch (IOException) { failed = true; }
            // A directory rename may be permitted by a filesystem even while a
            // child is held. It must never make that active media recoverable.
            Require(failed || (await recovery.GetRecoverableAsync()).All(item => !item.CanRecover),
                "A still-open replay segment was exposed as recoverable media.");
        }
        Console.WriteLine($"PASS: {checks} replay-buffer archive/recovery checks (all five AAC streams, missing journal, failed destination, incomplete tail, cancellation).");
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static async Task<string> Run(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { try { process.Kill(entireProcessTree: true); } catch { } throw; }
        if (process.ExitCode != 0) throw new InvalidOperationException(await error);
        return await output;
    }
}
