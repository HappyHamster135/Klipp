using System.Diagnostics;
using System.Text.Json;
using Klipp.Core.Enums;
using Klipp.Core.Models;
using Klipp.Encoding.FFmpeg;
using Klipp.Encoding.Video;

// Synthetic pixels only; this harness never captures a window or opens audio devices.
var root = Path.Combine(Path.GetTempPath(), "Klipp-HardwareEncoder-QA-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var locator = new FFmpegLocator();
var ffmpeg = await locator.GetFFmpegPathAsync();
var ffprobe = await locator.GetFFprobePathAsync();
var checks = 0;
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
var settings = RecordingSettings.Default1080p60 with
{
    Width = 1280, Height = 720, FrameRate = 30, VideoBitrate = 4_000_000
};
var clock = Stopwatch.StartNew();
var automatic = await FFmpegCaptureEncoderSelector.SelectAsync(ffmpeg, settings);
Console.WriteLine($"Auto selection: {automatic.DisplayName}, first probe {clock.Elapsed.TotalMilliseconds:F0}ms");
clock.Restart();
var repeated = await FFmpegCaptureEncoderSelector.SelectAsync(ffmpeg, settings);
Require(ReferenceEquals(automatic, repeated), "A repeated capture configuration did not reuse the cached probe result.");
Console.WriteLine($"Cached selection: {clock.Elapsed.TotalMilliseconds:F2}ms");
Require(automatic.IsHardware == (automatic.EncoderName != "libx264"), "Encoder hardware identity is inconsistent.");
Require(automatic.IsHardware || !string.IsNullOrWhiteSpace(automatic.FallbackReason), "Auto CPU fallback had no explanation.");
var cpu = await FFmpegCaptureEncoderSelector.SelectAsync(ffmpeg, settings with { EncoderPreference = "cpu" });
Require(!cpu.IsHardware && cpu.EncoderName == "libx264" && cpu.FallbackReason is null, "Explicit CPU selection changed or was described as a failure.");
Require(FFmpegCaptureEncoderSelector.NormalizePreference(" H264_QSV ") == "qsv" &&
        FFmpegCaptureEncoderSelector.NormalizePreference("invalid") == "auto", "Encoder preference normalization is unsafe.");
var impossibleHardware = settings with { Width = 2, Height = 2, EncoderPreference = "nvenc" };
var fallback = await FFmpegCaptureEncoderSelector.SelectAsync(ffmpeg, impossibleHardware);
Require(!fallback.IsHardware && fallback.EncoderName == "libx264" && !string.IsNullOrWhiteSpace(fallback.FallbackReason),
    "Unsupported GPU dimensions did not select an explained CPU fallback before capture.");
var concurrentSettings = settings with { VideoBitrate = 4_100_000 };
var concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
    FFmpegCaptureEncoderSelector.SelectAsync(ffmpeg, concurrentSettings)));
Require(concurrent.All(selection => ReferenceEquals(selection, concurrent[0])),
    "Concurrent recording/replay preparations duplicated encoder selection work.");
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    var canceledSettings = settings with { VideoBitrate = 4_200_000 };
    var canceled = false;
    try { await FFmpegCaptureEncoderSelector.SelectAsync(ffmpeg, canceledSettings, cancellation.Token); }
    catch (OperationCanceledException) { canceled = true; }
    Require(canceled, "Capture preparation ignored caller cancellation.");
    var retry = await FFmpegCaptureEncoderSelector.SelectAsync(ffmpeg, canceledSettings);
    Require(retry.IsHardware == automatic.IsHardware, "A canceled caller poisoned the shared encoder probe.");
}

await CheckManual(settings, "automatic.mp4", expectedHardware: automatic.IsHardware);
await CheckManual(settings with { EncoderPreference = "cpu" }, "cpu.mp4", expectedHardware: false);
await CheckManual(impossibleHardware, "unsupported-gpu-fallback.mp4", expectedHardware: false);
await CheckSegments(settings, "automatic-segments", expectedHardware: automatic.IsHardware);
await CheckSegments(settings with { EncoderPreference = "cpu" }, "cpu-segments", expectedHardware: false);
await CheckFailedReplay();

Console.WriteLine($"PASS: {checks} hardware-selection/caching, real manual MP4, GPU-unavailable CPU fallback and replay segment checks");
Console.WriteLine("Synthetic artifacts: " + root);
return;

async Task CheckManual(RecordingSettings configuration, string name, bool expectedHardware)
{
    var path = Path.Combine(root, name);
    await using var encoder = new FFmpegH264Encoder(locator);
    encoder.SetOutputPath(path);
    await encoder.InitializeAsync(configuration);
    Require(encoder.IsHardwareEncoder == expectedHardware, "The manual encoder did not use the preflight selection.");
    await Feed(encoder, configuration, TimeSpan.FromSeconds(1.1));
    await foreach (var _ in encoder.FlushAsync()) { }
    Require(encoder.FramesWritten >= configuration.FrameRate, "The manual recorder lost its paced frame stream.");
    var info = await Probe(path);
    Require(info.Codec == "h264" && info.Width == configuration.Width && info.Height == configuration.Height,
        "The manual MP4 did not keep H264 and the requested canvas dimensions.");
    Require(info.Duration >= 1 && info.Duration < 2, "The manual MP4 duration diverged from capture time.");
    Require(info.Packets == encoder.FramesWritten, "The final manual MP4 did not contain every delivered paced frame.");
    await Run(ffmpeg, ["-v", "error", "-i", path, "-f", "null", "-"]);
    checks++;
    Console.WriteLine($"Manual {name}: {encoder.EncoderDisplayName}, {info.Packets} frames, {info.Duration:F3}s");
}

async Task CheckSegments(RecordingSettings configuration, string name, bool expectedHardware)
{
    var directory = Path.Combine(root, name);
    await using var recorder = new FFmpegSegmentRecorder(locator) { SegmentDurationSeconds = 1, MaxSegments = 4 };
    recorder.SetSegmentDirectory(directory);
    await recorder.InitializeAsync(configuration);
    Require(recorder.IsHardwareEncoder == expectedHardware, "The replay encoder did not share the preflight selection.");
    await Feed(recorder, configuration, TimeSpan.FromSeconds(2.2));
    await foreach (var _ in recorder.FlushAsync()) { }
    var files = Directory.GetFiles(directory, "seg_*.mp4").Order().ToArray();
    Require(files.Length >= 2 && files.Length <= 4, "Replay did not create bounded finalized segments.");
    var totalFrames = 0L;
    foreach (var file in files)
    {
        var info = await Probe(file);
        Require(info.Codec == "h264" && info.Width == configuration.Width && info.Height == configuration.Height,
            "A replay segment did not retain the selected video configuration.");
        totalFrames += info.Packets;
        await Run(ffmpeg, ["-v", "error", "-i", file, "-f", "null", "-"]);
        checks++;
    }
    Require(totalFrames == recorder.FramesWritten, "Replay segment finalization lost or replayed paced frames.");
    Console.WriteLine($"Replay {name}: {recorder.EncoderDisplayName}, {files.Length} segments, {totalFrames} frames");
}

async Task CheckFailedReplay()
{
    var directory = Path.Combine(root, "replay-output-failure");
    await using var recorder = new FFmpegSegmentRecorder(locator) { SegmentDurationSeconds = 1, MaxSegments = 4 };
    recorder.SetSegmentDirectory(directory);
    await recorder.InitializeAsync(settings with { EncoderPreference = "cpu" });
    // Remove only this empty fixture directory and replace it with a file.
    // This forces a genuine FFmpeg output failure without touching user data.
    Directory.Delete(directory, recursive: false);
    await File.WriteAllTextAsync(directory, "Disposable invalid replay destination");
    await Feed(recorder, settings, TimeSpan.FromMilliseconds(300));
    var failed = false;
    try { await foreach (var _ in recorder.FlushAsync()) { } }
    catch (Exception exception) when (exception is IOException or InvalidOperationException) { failed = true; }
    Require(failed, "Replay output failure was silently reported as successful finalization.");
}

async Task Feed(Klipp.Core.Abstractions.IVideoEncoder encoder, RecordingSettings configuration, TimeSpan duration)
{
    var width = configuration.Width == 2 ? 2 : 640;
    var height = configuration.Height == 2 ? 2 : 360;
    var pixels = new byte[width * height * 4];
    for (var i = 0; i < pixels.Length; i += 4)
    { pixels[i] = (byte)((i / 4) % 240); pixels[i + 1] = 140; pixels[i + 2] = 70; pixels[i + 3] = 255; }
    var frame = new VideoFrame(width, height, PixelFormat.Bgra8, 0, pixels);
    var timer = Stopwatch.StartNew();
    do
    {
        await foreach (var _ in encoder.EncodeAsync(frame)) { }
        await Task.Delay(15);
    } while (timer.Elapsed < duration);
}

async Task<(string Codec, int Width, int Height, long Packets, double Duration)> Probe(string path)
{
    var output = await Run(ffprobe, ["-v", "error", "-count_packets", "-select_streams", "v:0", "-show_entries",
        "stream=codec_name,width,height,nb_read_packets:format=duration", "-of", "json", path]);
    using var document = JsonDocument.Parse(output);
    var stream = document.RootElement.GetProperty("streams")[0];
    return (stream.GetProperty("codec_name").GetString()!, stream.GetProperty("width").GetInt32(),
        stream.GetProperty("height").GetInt32(), long.Parse(stream.GetProperty("nb_read_packets").GetString()!),
        double.Parse(document.RootElement.GetProperty("format").GetProperty("duration").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
}

async Task<string> Run(string executable, string[] arguments)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardError = true, RedirectStandardOutput = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch { process.Kill(entireProcessTree: true); throw; }
    if (process.ExitCode != 0) throw new InvalidOperationException(await errors);
    return await output;
}
