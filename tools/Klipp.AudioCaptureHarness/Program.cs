using System.IO.Pipes;
using System.Diagnostics;
using Klipp.Encoding.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

if (args.Contains("--tone-parent"))
{
    await ProcessAudioChecks.PlayToneParentAsync(args);
    return;
}
if (args.Contains("--tone-source"))
{
    await ProcessAudioChecks.PlayToneAsync(args);
    return;
}

var checks = 0;
void Require(bool condition, string detail)
{
    if (!condition) throw new InvalidOperationException(detail);
    checks++;
}
bool Near(float first, float second, float tolerance = .0001f) => Math.Abs(first - second) <= tolerance;

var mono = MixedAudioCapture.PrepareInput(new Samples(48000, 1, [.1f, -.2f, .3f]));
var output = new float[6];
Require(mono.Read(output, 0, output.Length) == 6 && output.SequenceEqual([.1f, .1f, -.2f, -.2f, .3f, .3f]),
    "Mono microphone samples were not duplicated into both stereo channels.");

var center = MixedAudioCapture.PrepareInput(new Samples(48000, 6, [0, 0, .5f, 0, 0, 0]));
center.Read(output, 0, 2);
Require(Near(output[0], .35355f) && Near(output[1], .35355f),
    "Surround center-channel speech did not reach both recorded stereo channels.");
var quad = MixedAudioCapture.PrepareInput(new Samples(48000, 4, [0, 0, 0, .5f]));
quad.Read(output, 0, 2);
Require(Near(output[0], 0) && Near(output[1], .35355f), "Quad rear-right audio was routed to the wrong recorded channel.");

var tone = Enumerable.Range(0, 44100).Select(index => (float)Math.Sin(index * 2 * Math.PI * 1000 / 44100)).ToArray();
var resampled = MixedAudioCapture.PrepareInput(new Samples(44100, 1, tone));
Require(resampled.WaveFormat.SampleRate == 48000 && resampled.WaveFormat.Channels == 2,
    "A 44.1kHz microphone was not normalized to the encoder format.");
var all = new List<float>();
var block = new float[2048];
int read;
while ((read = resampled.Read(block, 0, block.Length)) > 0)
{
    all.AddRange(block.AsSpan(0, read).ToArray());
    if (all.Count > 1000000) throw new InvalidOperationException("Resampling did not finish.");
}
var frames = all.Count / 2;
Require(Math.Abs(frames - 48000) <= 128, $"Resampling changed the duration of a one-second source ({frames} frames).");
var crossings = 0;
double square = 0;
var monoPaired = true;
for (var index = 2; index < all.Count; index += 2)
{
    if (all[index - 2] < 0 && all[index] >= 0) crossings++;
    square += all[index] * all[index];
    monoPaired &= Near(all[index], all[index + 1]);
}
Require(monoPaired, "Resampling separated a mono microphone's stereo pair.");
Require(Math.Abs(crossings - 1000) <= 4, "Resampling changed the tone's pitch.");
Require(Math.Abs(Math.Sqrt(square / frames) - Math.Sqrt(.5)) < .03, "Resampling changed normal microphone loudness.");

var mixer = new AudioSampleMixer([
    (new Samples(48000, 2, [.6f, .2f]), .5),
    (new Samples(48000, 2, [.1f, .3f]), 1d)
]);
mixer.Read(output, 2);
Require(Near(output[0], .4f) && Near(output[1], .4f), "Game/microphone gains were not applied independently.");
new AudioSampleMixer([(new Samples(48000, 2, [.7f, -.8f]), 2d)]).Read(output, 2);
Require(output[0] == 1 && output[1] == -1, "Loud mixed samples exceeded the encoder's safe float PCM range.");
new AudioSampleMixer([(new Samples(48000, 2, [float.NaN, float.PositiveInfinity]), 1d)]).Read(output, 2);
Require(output[0] == 0 && output[1] == 0, "Invalid input samples contaminated encoded audio.");
new AudioSampleMixer([(new Samples(48000, 2, [.5f, -.5f]), 0d)]).Read(output, 2);
Require(output[0] == 0 && output[1] == 0, "A muted source remained audible.");

var stems = new AudioSampleMixer([
    (new Samples(48000, 2, [.6f, .2f, .4f, -.2f]), .5),
    (new Samples(48000, 2, [.1f, .3f]), 1d)
], separateSources: true);
var separated = new float[8];
stems.Read(separated, separated.Length);
Require(stems.OutputChannels == 4 && separated.SequenceEqual([.3f, .1f, .1f, .3f, .2f, -.1f, 0, 0]),
    "Game and microphone samples mixed together, changed stereo order or lost their common clock when one source went quiet.");
Require(new CaptureAudioOptions { CaptureSystemAudio = true, CaptureMicrophone = true }.OutputChannels == 4 &&
    new CaptureAudioOptions { CaptureSystemAudio = false, CaptureMicrophone = true }.OutputChannels == 2,
    "Single-source recording and separate dual-source recording used the wrong PCM layouts.");

foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
{
    var rejected = false;
    try { using var device = CaptureAudioOptions.ResolveDevice("klipp-nonexistent-endpoint-test", flow); }
    catch (InvalidOperationException exception) { rejected = exception.Message.Contains("Settings > Audio"); }
    Require(rejected, "An unavailable selected endpoint silently fell back to a different audio device.");
}
using (var disabled = new MixedAudioCapture())
{
    var rejected = false;
    try { disabled.Start(new CaptureAudioOptions { CaptureSystemAudio = false, CaptureMicrophone = false }, (_, _, _) => Task.CompletedTask); }
    catch (ArgumentException) { rejected = true; }
    Require(rejected, "A disabled audio session attempted to open capture devices.");
}

await using (var pipe = new AudioPipeServer())
{
    pipe.Start();
    var packet = new byte[] { 5, 11, 17, 23 };
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    var write = pipe.WriteAsync(packet, packet.Length, timeout.Token);
    await Task.Delay(100, timeout.Token);
    Require(!write.IsCompleted, "The first audio packet was discarded before FFmpeg connected.");
    var name = pipe.PipePath[(pipe.PipePath.LastIndexOf('\\') + 1)..];
    await using var client = new NamedPipeClientStream(".", name, PipeDirection.In, PipeOptions.Asynchronous);
    await client.ConnectAsync(timeout.Token);
    var received = new byte[packet.Length];
    await client.ReadExactlyAsync(received, timeout.Token);
    await write;
    Require(received.SequenceEqual(packet), "The opening audio packet changed or was lost during encoder connection.");
}
await using (var pipe = new AudioPipeServer())
{
    pipe.Start();
    using var cancel = new CancellationTokenSource(50);
    var canceled = false;
    try { await pipe.WriteAsync(new byte[8], 8, cancel.Token); }
    catch (OperationCanceledException) { canceled = true; }
    Require(canceled, "Stopping capture could not cancel an audio writer waiting for the encoder.");
}
var epoch = Stopwatch.Frequency * 5;
var stopped = epoch + Stopwatch.Frequency * 2;
var priorFrames = 48000 * 2 - 7200;
Require(AudioTailDrain.PendingFrames(epoch, stopped, priorFrames) == 7200,
    "The 150ms prebuffer tail did not end at the manual recording stop epoch.");
using (var tail = new MemoryStream())
{
    var tailMixer = new AudioSampleMixer([(new Samples(48000, 2,
        Enumerable.Repeat(.125f, 7200 * 2).ToArray()), 1d)]);
    var drained = await AudioTailDrain.DrainAsync(tailMixer, (bytes, count, _) =>
    {
        tail.Write(bytes, 0, count);
        return Task.CompletedTask;
    }, epoch, stopped, priorFrames);
    var pcm = tail.ToArray();
    Require(drained == 7200 && pcm.Length == 7200 * 2 * sizeof(float),
        "Manual stop lost or extended the final prebuffered audio samples.");
    Require(BitConverter.ToSingle(pcm, pcm.Length - sizeof(float)) == .125f,
        "The final audible sample was replaced with a silent stop tail.");
}
Require(AudioTailDrain.PendingFrames(epoch, epoch + Stopwatch.Frequency * 30, 0) == 48000,
    "An encoder stall allowed an unbounded manual stop drain.");
Require(AudioTailDrain.PendingFrames(epoch, stopped, 48000 * 2 + 1) == 0,
    "Manual stop appended audio beyond its requested stop time.");
var blockedDrainCanceled = false;
try
{
    await AudioTailDrain.DrainAsync(new AudioSampleMixer([]),
        (_, _, _) => new TaskCompletionSource().Task, epoch, stopped, priorFrames);
}
catch (OperationCanceledException) { blockedDrainCanceled = true; }
Require(blockedDrainCanceled, "A stalled drain sink did not honor the bounded manual stop deadline.");
using (var tail = new MemoryStream())
{
    var separatedTail = new AudioSampleMixer([
        (new Samples(48000, 2, Enumerable.Repeat(.125f, 7200 * 2).ToArray()), 1d),
        (new Samples(48000, 2, Enumerable.Repeat(-.25f, 7200 * 2).ToArray()), 1d)
    ], separateSources: true);
    var drained = await AudioTailDrain.DrainAsync(separatedTail, (bytes, count, _) =>
    {
        tail.Write(bytes, 0, count);
        return Task.CompletedTask;
    }, epoch, stopped, priorFrames);
    var pcm = tail.ToArray();
    Require(drained == 7200 && pcm.Length == 7200 * 4 * sizeof(float) &&
        BitConverter.ToSingle(pcm, pcm.Length - 16) == .125f && BitConverter.ToSingle(pcm, pcm.Length - 4) == -.25f,
        "Stopping a separate-source recording changed either stem's duration, order or final samples.");
}
Console.WriteLine($"PASS: {checks} audio checks (mono/surround routing, 44.1/48kHz duration and pitch, gains/limiting, unavailable device rejection, ordered startup and cancelable pipe writes). No microphone or loopback capture opened.");
await ReconnectChecks.RunAsync();
if (args.Contains("--native-endpoints", StringComparer.OrdinalIgnoreCase)) ReconnectChecks.CheckNativeEndpoints();
if (args.Contains("--tracks", StringComparer.OrdinalIgnoreCase)) await SeparateAudioChecks.RunAsync();
await ProcessAudioChecks.RunSyntheticAsync();
if (args.Contains("--process-audio", StringComparer.OrdinalIgnoreCase)) await ProcessAudioChecks.RunNativeAsync();

sealed class Samples(int sampleRate, int channels, float[] samples) : ISampleProvider
{
    private int _position;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    public int Read(float[] buffer, int offset, int count)
    {
        var read = Math.Min(count, samples.Length - _position);
        Array.Copy(samples, _position, buffer, offset, read);
        _position += read;
        return read;
    }
}
