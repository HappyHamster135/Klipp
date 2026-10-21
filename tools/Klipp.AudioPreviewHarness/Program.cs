using Klipp.Desktop.Services;
using System.Diagnostics;
using Windows.Media.Core;
using Windows.Media.Playback;

// Only generated tones are decoded, and native players stay muted. No device
// loopback, microphone, user media, app settings, or project store is opened.
WinRT.ComWrappersSupport.InitializeComWrappers();
Console.WriteLine("Initialized WinRT");
var root = Path.Combine(Path.GetTempPath(), "Klipp-AudioPreview-QA-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var firstPath = Path.Combine(root, "first.wav");
var secondPath = Path.Combine(root, "second.wav");
WriteTone(firstPath, 700, .05);
WriteTone(secondPath, 1100, .1);
Console.WriteLine("Generated disposable tones");
var checks = 0;
void Require(bool condition, string detail)
{
    if (!condition) throw new InvalidOperationException(detail);
    checks++;
}

using var first = new MediaPlayer { AutoPlay = false, IsMuted = true };
Console.WriteLine("Created first muted player");
using var second = new MediaPlayer { AutoPlay = false, IsMuted = true };
Console.WriteLine("Created second muted player");
try
{
    Require(PreviewAudioGainEffect.SetGain(first, 1), "The native gain effect did not register/attach.");
    Console.WriteLine("Attached first gain effect");
    await Open(first, firstPath);
    first.Play();
    await CheckGain(first, 1);
    await CheckGain(first, 2);
    await CheckGain(first, 4);
    if (args.Length == 2 && args[0] == "--ffmpeg")
    {
        foreach (var gain in new[] { 2d, 4d, 16d })
        {
            await CheckGain(first, gain);
            PreviewAudioGainEffect.TryGetStatistics(first, out _, out var previewPeak, out _);
            var exportPeak = await ExportPeak(args[1], firstPath, gain);
            Require(Math.Abs(previewPeak - exportPeak) < .00005, "Preview amplification differs from FFmpeg's exported sample gain.");
            Console.WriteLine($"preview/export gain={gain} previewPeak={previewPeak:0.000000} exportPeak={exportPeak:0.000000}");
        }
    }
    await CheckGain(first, .5);
    await CheckGain(first, 0);
    await CheckGain(first, 16);
    Require(first.IsMuted, "Gain changes altered the player's independent mute state.");
    var before = first.PlaybackSession.Position;
    await CheckGain(first, 2);
    Require(first.PlaybackSession.Position > before, "A live gain change reset or stopped the decoder.");
    Require(PreviewAudioGainEffect.SetGain(second, .5), "A second effect voice could not attach.");
    await Open(second, secondPath);
    second.Play();
    await CheckGain(second, .5);
    await CheckGain(first, 2);
    first.Pause();
    first.PlaybackSession.Position = TimeSpan.FromSeconds(1);
    first.Play();
    await CheckGain(first, 2);
    first.Pause();
    await Open(first, secondPath);
    first.Play();
    await CheckGain(first, 2);
    PreviewAudioGainEffect.Release(first);
    Require(!PreviewAudioGainEffect.TryGetStatistics(first, out _, out _, out _), "A released controller remained registered.");
    Console.WriteLine($"PASS: {checks} native muted-player gain checks, live amplification, independent voices, seek, source replacement and cleanup");
    Console.WriteLine("Disposable generated audio: " + root);
}
finally
{
    first.Pause(); second.Pause();
    PreviewAudioGainEffect.Release(first);
    PreviewAudioGainEffect.Release(second);
}

async Task CheckGain(MediaPlayer player, double gain)
{
    Require(PreviewAudioGainEffect.SetGain(player, gain), "The native gain controller was unavailable.");
    PreviewAudioGainEffect.TryGetStatistics(player, out _, out _, out var initialSamples);
    var deadline = DateTime.UtcNow.AddSeconds(4);
    while (DateTime.UtcNow < deadline)
    {
        await Task.Delay(40);
        if (!PreviewAudioGainEffect.TryGetStatistics(player, out var input, out var output, out var samples) ||
            samples <= initialSamples || input < .001) continue;
        if (Math.Abs(output / input - gain) > .001) continue;
        Require(true, "Decoded samples have the requested linear amplification.");
        Console.WriteLine($"gain={gain:0.###} inputPeak={input:0.000000} outputPeak={output:0.000000} samples={samples}");
        return;
    }
    throw new InvalidOperationException($"The real native player's decoded samples did not reach gain {gain}.");
}

static async Task Open(MediaPlayer player, string path)
{
    var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    void OnOpened(MediaPlayer sender, object args) => opened.TrySetResult();
    void OnFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args) => opened.TrySetException(new InvalidOperationException(args.Error + ": " + args.ErrorMessage));
    player.MediaOpened += OnOpened; player.MediaFailed += OnFailed;
    try
    {
        player.Source = MediaSource.CreateFromUri(new Uri(path));
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(8));
    }
    finally { player.MediaOpened -= OnOpened; player.MediaFailed -= OnFailed; }
}

static void WriteTone(string path, double frequency, double amplitude)
{
    const int rate = 48000, frames = rate * 20;
    using var writer = new BinaryWriter(File.Create(path));
    writer.Write("RIFF"u8); writer.Write(36 + frames * 4); writer.Write("WAVEfmt "u8);
    writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(rate);
    writer.Write(rate * 4); writer.Write((short)4); writer.Write((short)16);
    writer.Write("data"u8); writer.Write(frames * 4);
    for (var frame = 0; frame < frames; frame++)
    {
        var sample = (short)Math.Round(Math.Sin(frame * Math.PI * 2 * frequency / rate) * amplitude * short.MaxValue);
        writer.Write(sample); writer.Write(sample);
    }
}

static async Task<double> ExportPeak(string executable, string path, double gain)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var value in new[] { "-v", "error", "-i", path, "-af", "volume=" + gain.ToString(System.Globalization.CultureInfo.InvariantCulture), "-t", "0.15", "-f", "f32le", "pipe:1" })
        start.ArgumentList.Add(value);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not run the export gain check.");
    var error = process.StandardError.ReadToEndAsync();
    using var output = new MemoryStream();
    await process.StandardOutput.BaseStream.CopyToAsync(output);
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException(await error);
    var bytes = output.ToArray();
    var peak = 0d;
    for (var index = 0; index + 4 <= bytes.Length; index += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(bytes, index)));
    return peak;
}
