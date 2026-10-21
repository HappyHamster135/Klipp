using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Klipp.Core.Enums;
using Klipp.Core.Models;
using Klipp.Desktop.Services;
using Klipp.Encoding.FFmpeg;
using Klipp.Encoding.Video;
using NAudio.Wave;

// All sessions, recordings and bookmark metadata created here are disposable.
// No real captures, audio endpoints, windows, or user recovery sessions are opened.
var root = Path.Combine(Path.GetTempPath(), "Klipp-Recovery-QA-" + Guid.NewGuid().ToString("N"));
var captureRoot = Path.Combine(root, "sessions");
var outputRoot = Path.Combine(root, "outputs");
var bookmarkRoot = Path.Combine(root, "bookmarks");
Directory.CreateDirectory(root);
Directory.CreateDirectory(outputRoot);
var locator = new FFmpegLocator();
if (!locator.IsInstalled) throw new InvalidOperationException("Install Klipp's FFmpeg before running this harness.");
var bookmarks = new RecordingBookmarkService(bookmarkRoot);
var recovery = new RecordingRecoveryService(captureRoot, locator, bookmarks);
var checks = 0;
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
Console.WriteLine("Disposable recovery fixtures: " + root);

var template = Path.Combine(root, "template.mp4");
await Run(locator.FFmpegPath, ["-v", "error", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=20:duration=3",
    "-c:v", "libx264", "-preset", "ultrafast", "-g", "20", "-bf", "0", "-pix_fmt", "yuv420p",
    "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "1000000", "-n", template]);

RecordingSessionHandle Session(string name = "recording.mp4", bool audio = false)
{
    var session = recovery.CreateSession(Path.Combine(outputRoot, name), 20, audio, audio ? 4 : 2, "Game audio");
    File.Copy(template, Path.Combine(session.Directory, "video.mp4"));
    session.Manifest.FramesWritten = 60;
    return session;
}
void Wave(string path)
{
    using var writer = new WaveFileWriter(path, WaveFormat.CreateIeeeFloatWaveFormat(48000, 4));
    var samples = new float[48000 * 3 * 4];
    for (var frame = 0; frame < 48000 * 3; frame++)
    {
        var game = (float)(.12 * Math.Sin(2 * Math.PI * 440 * frame / 48000));
        var mic = frame < 24000 ? 0 : (float)(.12 * Math.Sin(2 * Math.PI * 880 * frame / 48000));
        samples[frame * 4] = samples[frame * 4 + 1] = game;
        samples[frame * 4 + 2] = samples[frame * 4 + 3] = mic;
    }
    writer.WriteSamples(samples, 0, samples.Length);
}
async Task<JsonDocument> Probe(string path) => JsonDocument.Parse(await Run(locator.FFprobePath,
    ["-v", "error", "-show_streams", "-show_format", "-of", "json", path]));
async Task AssertPlayable(string path, bool audio)
{
    using var probe = await Probe(path);
    var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
    Require(streams.Count(stream => stream.GetProperty("codec_type").GetString() == "video") == 1, "A recovered recording lost its video stream.");
    Require(streams.Count(stream => stream.GetProperty("codec_type").GetString() == "audio") == (audio ? 3 : 0), "Separate game/microphone audio streams changed.");
    var duration = double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
    Require(duration > .8 && duration < 3.2, "The recovered duration is outside the recorded interval.");
    await Run(locator.FFmpegPath, ["-v", "error", "-i", path, "-f", "null", "-"]);
    checks++;
}

// Live leases exclude a running capture, including a second recovery service.
var active = Session("active.mp4");
Require((await recovery.GetRecoverableAsync()).Count == 0, "An active recording was offered for recovery.");
var refusedActive = false;
try { await recovery.RecoverAsync(active.Manifest.Id, outputRoot); }
catch (IOException) { refusedActive = true; }
Require(refusedActive, "Recovery stole an active capture session.");
active.Dispose();
Require((await recovery.GetRecoverableAsync()).Count == 1, "An interrupted session was not discovered.");
var activeResult = await recovery.RecoverAsync(active.Manifest.Id, outputRoot);
await AssertPlayable(activeResult.OutputPath, audio: false);
Require(!Directory.Exists(active.Directory), "Verified recovery did not clean its own temporary session.");

// Simulate a crash while FFmpeg is actively writing fragmented video. A kill
// leaves the last fragment incomplete; all complete fragments must still save.
var crashed = recovery.CreateSession(Path.Combine(outputRoot, "crashed.mp4"), 20, true, 4, "Game audio");
var crashedVideo = Path.Combine(crashed.Directory, "video.mp4");
using (var child = Start(locator.FFmpegPath, ["-v", "error", "-re", "-f", "lavfi", "-i", "testsrc2=size=64x64:rate=20",
    "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-g", "40", "-bf", "0",
    "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "1000000", "-flush_packets", "1", "-n", crashedVideo]))
{
    var err = child.StandardError.ReadToEndAsync();
    var stdout = child.StandardOutput.ReadToEndAsync();
    await Task.Delay(2450);
    child.Kill(entireProcessTree: true);
    await child.WaitForExitAsync();
    await err; await stdout;
}
var crashedWave = Path.Combine(crashed.Directory, "audio.wav");
Wave(crashedWave);
// NAudio's crash case: valid format/data contents with stale RIFF/data sizes.
var waveBytes = await File.ReadAllBytesAsync(crashedWave);
BinaryPrimitives.WriteUInt32LittleEndian(waveBytes.AsSpan(4, 4), 0);
for (var index = 12; index < waveBytes.Length - 8;)
{
    var size = BinaryPrimitives.ReadUInt32LittleEndian(waveBytes.AsSpan(index + 4, 4));
    if (waveBytes.AsSpan(index, 4).SequenceEqual("data"u8))
    { BinaryPrimitives.WriteUInt32LittleEndian(waveBytes.AsSpan(index + 4, 4), 0); break; }
    index += 8 + (int)size + (int)(size & 1);
}
await File.WriteAllBytesAsync(crashedWave, waveBytes);
crashed.Manifest.AudioOffsetSeconds = -.15;
crashed.Manifest.Markers.Add(new("crash-marker", 1.25, "Before the crash"));
recovery.SaveSession(crashed);
crashed.Dispose();
var originalVideoHash = Hash(crashedVideo);
var originalWaveHash = Hash(crashedWave);
// An invalid destination models a full/removed/unwritable save target without
// writing or changing any real recording location.
var blocked = Path.Combine(root, "destination-is-a-file");
await File.WriteAllTextAsync(blocked, "keep");
var failed = false;
try { await recovery.RecoverAsync(crashed.Manifest.Id, blocked); }
catch (IOException) { failed = true; }
Require(failed && File.Exists(crashedVideo) && File.Exists(crashedWave), "A failed save deleted raw recording media.");
Require(Hash(crashedVideo) == originalVideoHash && Hash(crashedWave) == originalWaveHash, "Recovery mutated original capture files.");
var crashResult = await recovery.RecoverAsync(crashed.Manifest.Id, outputRoot);
await AssertPlayable(crashResult.OutputPath, audio: true);
var loaded = await bookmarks.LoadAsync(crashResult.OutputPath);
Require(loaded.Count == 1 && loaded[0].Id == "crash-marker", "A recovered highlight was lost before temporary files were removed.");
using (var probe = await Probe(crashResult.OutputPath))
{
    var audio = probe.RootElement.GetProperty("streams").EnumerateArray()
        .Where(stream => stream.GetProperty("codec_type").GetString() == "audio").ToArray();
    Require(audio.Select(stream => stream.GetProperty("tags").GetProperty("handler_name").GetString())
        .SequenceEqual(new[] { "Playback mix", "Game audio", "Microphone" }), "Recovered audio streams lost their labels.");
    Require(audio.All(stream => stream.GetProperty("channels").GetInt32() == 2), "Recovered stems became surround audio.");
    var videoDuration = double.Parse(probe.RootElement.GetProperty("streams")[0].GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
    Require(audio.All(stream => Math.Abs(double.Parse(stream.GetProperty("duration").GetString()!, CultureInfo.InvariantCulture) - videoDuration) < .08),
        "Recovered audio duration drifted away from the video.");
}
var gameSamples = await Decode(crashResult.OutputPath, 1, "game");
var micSamples = await Decode(crashResult.OutputPath, 2, "microphone");
var gameTone = Amplitude(gameSamples.AsSpan(24000, 12000), 440);
var micTone = Amplitude(micSamples.AsSpan(24000, 12000), 880);
Require(gameTone > .09 && Amplitude(gameSamples.AsSpan(24000, 12000), 880) < .005 &&
    micTone > .09 && Amplitude(micSamples.AsSpan(24000, 12000), 440) < .005,
    "Recovery mixed the game and microphone content into the wrong editable stems.");
Require(Amplitude(micSamples.AsSpan(10000, 4000), 880) < .005 &&
    Amplitude(micSamples.AsSpan(22000, 4000), 880) > .09,
    "Recovery did not restore the journaled negative audio/video offset.");

// Corrupt/older manifests are ignored safely; the WAV format, rather than a
// guessed journal value, determines the four-channel stem layout.
var legacy = Session("legacy.mp4", audio: true);
Wave(Path.Combine(legacy.Directory, "audio.wav"));
await File.WriteAllTextAsync(Path.Combine(legacy.Directory, "session.json"), "{truncated");
legacy.Dispose();
var legacyOffer = (await recovery.GetRecoverableAsync()).Single();
Require(legacyOffer.CanRecover && legacyOffer.FailureReason is not null, "An older session was silently lost or claimed to have complete timing metadata.");
var legacyResult = await recovery.RecoverAsync(legacyOffer.Id, outputRoot);
await AssertPlayable(legacyResult.OutputPath, audio: true);

// Publication never overwrites an existing recording with the requested name.
var existingPath = Path.Combine(outputRoot, "duplicate.mp4");
await File.WriteAllTextAsync(existingPath, "user content");
var duplicate = Session("duplicate.mp4");
duplicate.Dispose();
var duplicateResult = await recovery.RecoverAsync(duplicate.Manifest.Id, outputRoot);
Require(await File.ReadAllTextAsync(existingPath) == "user content" && duplicateResult.OutputPath != existingPath,
    "Recovery overwrote an existing user recording.");

var delayed = Session("delayed-audio.mp4", audio: true);
Wave(Path.Combine(delayed.Directory, "audio.wav"));
delayed.Manifest.AudioOffsetSeconds = .15;
recovery.SaveSession(delayed);
delayed.Dispose();
var delayedResult = await recovery.RecoverAsync(delayed.Manifest.Id, outputRoot);
var delayedGame = await Decode(delayedResult.OutputPath, 1, "delayed-game");
Require(Amplitude(delayedGame.AsSpan(0, 4000), 440) < .005 && Amplitude(delayedGame.AsSpan(12000, 4000), 440) > .09,
    "Recovery did not restore a positive audio/video offset.");

// If bookmark storage fails after MP4 publication, retain raw files and the
// file fingerprint. Retry must finish the same recording, never create copies.
var metadata = Session("metadata-retry.mp4");
metadata.Manifest.Markers.Add(new("persistent-marker", .75, "Keep me"));
recovery.SaveSession(metadata);
metadata.Dispose();
var brokenBookmarkRoot = Path.Combine(root, "broken-bookmark-folder");
await File.WriteAllTextAsync(brokenBookmarkRoot, "blocked");
var retry = new RecordingRecoveryService(captureRoot, locator, new RecordingBookmarkService(brokenBookmarkRoot));
failed = false;
try { await retry.RecoverAsync(metadata.Manifest.Id, outputRoot); }
catch (IOException) { failed = true; }
Require(failed && File.Exists(Path.Combine(metadata.Directory, "video.mp4")), "Metadata-save failure discarded recovery media.");
var published = Directory.GetFiles(outputRoot, "metadata-retry*.mp4");
Require(published.Length == 1, "The retry fixture did not publish exactly one verified MP4.");
File.Delete(brokenBookmarkRoot);
var metadataResult = await retry.RecoverAsync(metadata.Manifest.Id, outputRoot);
Require(metadataResult.OutputPath == published[0] && Directory.GetFiles(outputRoot, "metadata-retry*.mp4").Length == 1,
    "Retry duplicated a recording that had already been verified and published.");
Require((await new RecordingBookmarkService(brokenBookmarkRoot).LoadAsync(metadataResult.OutputPath)).Count == 1,
    "Retry failed to persist its original highlight.");

await ReplayRecoveryChecks.RunAsync(locator, root);
await FourStemRecoveryChecks.RunAsync(recovery, locator, root, template, outputRoot);

// The real encoder's output, not only a fixture, must use crash-readable
// fragments and finish as an ordinary playable MP4 in normal stop flow.
var encoded = recovery.CreateSession(Path.Combine(outputRoot, "encoder.mp4"), 20, false, 2, "Game audio");
await using (var encoder = new FFmpegH264Encoder(locator))
{
    encoder.SetOutputPath(Path.Combine(encoded.Directory, "video.mp4"));
    await encoder.InitializeAsync(RecordingSettings.Default1080p60 with { Width = 64, Height = 64, FrameRate = 20, VideoBitrate = 150_000 });
    await foreach (var _ in encoder.EncodeAsync(new VideoFrame(64, 64, PixelFormat.Bgra8, 0, new byte[64 * 64 * 4]))) { }
    await Task.Delay(1300);
    await foreach (var _ in encoder.FlushAsync()) { }
    encoded.Manifest.FramesWritten = encoder.FramesWritten;
    Require(encoder.FramesWritten >= 20 && encoder.PacingStartTimestamp > 0, "Live recording duration was not measured from encoded frames.");
}
var encodedResult = await recovery.FinishActiveAsync(encoded);
recovery.CompleteCleanup(encoded);
await AssertPlayable(encodedResult.OutputPath, audio: false);

// Discard is exercised only on a test-owned session. Unknown adjacent files
// and sibling folders must never be recursively removed.
var discarded = Session("discard.mp4");
var sentinel = Path.Combine(discarded.Directory, "user-note.txt");
await File.WriteAllTextAsync(sentinel, "keep");
discarded.Dispose();
try { await recovery.DiscardAsync(discarded.Manifest.Id); } catch (IOException) { }
Require(File.Exists(sentinel) && !File.Exists(Path.Combine(discarded.Directory, "video.mp4")), "Discard removed unknown data or failed to remove its own capture.");
failed = false;
try { await recovery.DiscardAsync("../outputs"); } catch (ArgumentException) { failed = true; }
Require(failed && File.Exists(existingPath), "An invalid session identifier escaped the recovery root.");

Console.WriteLine($"PASS: {checks} recording recovery checks. Test files remain at {root}");
return;

async Task<float[]> Decode(string path, int stream, string label)
{
    var file = Path.Combine(root, label + ".f32");
    await Run(locator.FFmpegPath, ["-v", "error", "-i", path, "-map", $"0:a:{stream}", "-ac", "1", "-ar", "48000",
        "-t", "1", "-f", "f32le", "-n", file]);
    var bytes = await File.ReadAllBytesAsync(file);
    var samples = new float[bytes.Length / sizeof(float)];
    Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
    return samples;
}

static double Amplitude(ReadOnlySpan<float> samples, int frequency)
{
    double sine = 0, cosine = 0;
    for (var index = 0; index < samples.Length; index++)
    {
        var phase = 2 * Math.PI * frequency * index / 48000;
        sine += samples[index] * Math.Sin(phase);
        cosine += samples[index] * Math.Cos(phase);
    }
    return 2 * Math.Sqrt(sine * sine + cosine * cosine) / samples.Length;
}

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static Process Start(string executable, IReadOnlyList<string> arguments)
{
    var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    return Process.Start(info) ?? throw new InvalidOperationException("Could not start FFmpeg.");
}
static async Task<string> Run(string executable, IReadOnlyList<string> arguments)
{
    using var process = Start(executable, arguments);
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch { try { process.Kill(entireProcessTree: true); } catch { } throw; }
    var output = await stdout; var error = await stderr;
    if (process.ExitCode != 0) throw new InvalidOperationException(error);
    return output;
}
