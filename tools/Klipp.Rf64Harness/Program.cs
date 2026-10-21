using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Klipp.Encoding.Muxing;
using Klipp.Encoding.Audio;

var root = Path.Combine(Path.GetTempPath(), "Klipp-RF64-QA-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Require(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}

// Logical streams store only their small header. No multi-gigabyte file is
// allocated, written, copied or traversed to check RF64 boundary arithmetic.
foreach (var channels in new[] { 2, 4, 6, 8 })
{
    using var stream = new HeaderOnlyStream();
    var writer = new Rf64WaveFileWriter(stream, 48000, channels, leaveOpen: true);
    var payload = ((long)uint.MaxValue + 1_000_000) / (channels * 4) * (channels * 4);
    stream.SetLength(Rf64WaveFileWriter.HeaderLength + payload);
    writer.Dispose();
    Require(stream.Bytes.AsSpan(0, 4).SequenceEqual("RF64"u8), "A large recording did not retain RF64 identification.");
    Require(BinaryPrimitives.ReadUInt64LittleEndian(stream.Bytes.AsSpan(20)) == (ulong)(stream.Length - 8), "The RF64 RIFF size overflowed at 4GB.");
    Require(BinaryPrimitives.ReadUInt64LittleEndian(stream.Bytes.AsSpan(28)) == (ulong)payload, "The RF64 data size overflowed at 4GB.");
    Require(BinaryPrimitives.ReadUInt64LittleEndian(stream.Bytes.AsSpan(36)) == (ulong)(payload / (channels * 4)), "RF64 sample count is not frame-aligned.");
    Require(RecordingWaveRepair.TryGetRepairPlan(stream, out var plan) && plan.PayloadLength == payload && plan.Channels == channels, "Large RF64 repair failed to retain the full data extent.");
    BinaryPrimitives.WriteUInt64LittleEndian(stream.Bytes.AsSpan(20), 96);
    BinaryPrimitives.WriteUInt64LittleEndian(stream.Bytes.AsSpan(28), 0);
    Require(RecordingWaveRepair.TryGetRepairPlan(stream, out plan) && plan.PayloadLength == payload, "An interrupted RF64 header lost data beyond its stale length.");
    RecordingWaveRepair.WriteRepairedLengths(stream, plan);
    Require(BinaryPrimitives.ReadUInt64LittleEndian(stream.Bytes.AsSpan(28)) == (ulong)payload, "Repair wrote a truncated RF64 size.");
}

var fourStemPath = Path.Combine(root, "four-stems.wav");
var samples = new byte[48000 * 8 * 4];
for (var frame = 0; frame < 48000; frame++)
    for (var stem = 0; stem < 4; stem++)
    {
        var sample = (float)(.1 * Math.Sin(2 * Math.PI * (440 + stem * 220) * frame / 48000));
        BinaryPrimitives.WriteSingleLittleEndian(samples.AsSpan((frame * 8 + stem * 2) * 4), sample);
        BinaryPrimitives.WriteSingleLittleEndian(samples.AsSpan((frame * 8 + stem * 2 + 1) * 4), sample);
    }
using (var writer = new Rf64WaveFileWriter(fourStemPath, 48000, 8)) writer.Write(samples, 0, samples.Length);
var originalBytes = File.ReadAllBytes(fourStemPath);
Require(originalBytes.Length == samples.Length + Rf64WaveFileWriter.HeaderLength, "The RF64 writer changed the float PCM payload length.");
Require(originalBytes.AsSpan(Rf64WaveFileWriter.HeaderLength).SequenceEqual(samples), "The RF64 writer changed interleaved stereo pairs.");
var interrupted = Path.Combine(root, "interrupted.wav");
var broken = originalBytes.ToArray();
BinaryPrimitives.WriteUInt64LittleEndian(broken.AsSpan(20), 96);
BinaryPrimitives.WriteUInt64LittleEndian(broken.AsSpan(28), 0);
BinaryPrimitives.WriteUInt64LittleEndian(broken.AsSpan(36), 0);
File.WriteAllBytes(interrupted, broken);
File.AppendAllBytes(interrupted, [1, 2, 3]);
var repaired = Path.Combine(root, "repaired.wav");
Require(RecordingWaveRepair.TryCreateCopy(interrupted, repaired, out var recoveredChannels) && recoveredChannels == 8, "Interrupted eight-channel RF64 could not repair.");
var repairedBytes = File.ReadAllBytes(repaired);
Require(repairedBytes.AsSpan(Rf64WaveFileWriter.HeaderLength).SequenceEqual(samples), "RF64 recovery lost or changed complete PCM frames.");
byte[] expectedInterrupted = [.. broken, 1, 2, 3];
Require(File.ReadAllBytes(interrupted).SequenceEqual(expectedInterrupted), "Recovery mutated its original RF64 file.");

var empty = Path.Combine(root, "empty.wav");
using (var writer = new Rf64WaveFileWriter(empty, 48000, 8)) { }
Require(!RecordingWaveRepair.TryCreateCopy(empty, Path.Combine(root, "empty-copy.wav"), out var emptyChannels) && emptyChannels == 8,
    "An empty valid RF64 header did not preserve its channel layout for video-only recovery.");

// A completed data chunk may be followed by metadata. Its bytes must survive
// recovery instead of being misinterpreted as extra audio or discarded.
var metadata = Path.Combine(root, "metadata.wav");
File.WriteAllBytes(metadata, originalBytes);
using (var append = new FileStream(metadata, FileMode.Append)) append.Write("LIST\u0004\0\0\0INFO"u8);
var metadataCopy = Path.Combine(root, "metadata-copy.wav");
Require(RecordingWaveRepair.TryCreateCopy(metadata, metadataCopy), "A WAV with trailing metadata did not repair.");
Require(File.ReadAllBytes(metadataCopy).AsSpan(Rf64WaveFileWriter.HeaderLength).SequenceEqual(File.ReadAllBytes(metadata).AsSpan(Rf64WaveFileWriter.HeaderLength)), "Repair discarded trailing WAV chunks or counted them as audio.");
Require(BinaryPrimitives.ReadUInt64LittleEndian(File.ReadAllBytes(metadataCopy).AsSpan(28)) == (ulong)samples.Length, "Trailing metadata was counted as audio samples.");

// Ordinary RIFF capture files from earlier Klipp versions remain supported.
var legacy = Path.Combine(root, "legacy.wav");
using (var writer = new BinaryWriter(File.Create(legacy)))
{
    writer.Write("RIFF"u8); writer.Write(0); writer.Write("WAVEfmt "u8); writer.Write(16);
    writer.Write((short)3); writer.Write((short)8); writer.Write(48000); writer.Write(48000 * 32);
    writer.Write((short)32); writer.Write((short)32); writer.Write("data"u8); writer.Write(0); writer.Write(samples);
}
var legacyCopy = Path.Combine(root, "legacy-copy.wav");
Require(RecordingWaveRepair.TryCreateCopy(legacy, legacyCopy, out var legacyChannels) && legacyChannels == 8, "Legacy RIFF repair regressed.");
var legacyBytes = File.ReadAllBytes(legacyCopy);
Require(legacyBytes.AsSpan(44).SequenceEqual(samples), "Legacy RIFF repair changed the audio.");
Require(BinaryPrimitives.ReadUInt32LittleEndian(legacyBytes.AsSpan(4)) == legacyBytes.Length - 8 &&
    BinaryPrimitives.ReadUInt32LittleEndian(legacyBytes.AsSpan(40)) == samples.Length, "Legacy RIFF lengths were not restored.");

using (var largeLegacy = new HeaderOnlyStream())
using (var promoted = new HeaderOnlyStream())
{
    legacyBytes.AsSpan(0, 44).CopyTo(largeLegacy.Bytes);
    BinaryPrimitives.WriteUInt32LittleEndian(largeLegacy.Bytes.AsSpan(40), 0);
    var payload = ((long)uint.MaxValue + 10_000_000) / 32 * 32;
    largeLegacy.SetLength(44 + payload);
    Require(RecordingWaveRepair.TryGetRepairPlan(largeLegacy, out var plan) && plan.NeedsRf64Promotion && plan.PayloadLength == payload,
        "An oversized legacy RIFF could not be promoted without truncating its data.");
    "RF64"u8.CopyTo(promoted.Bytes);
    "WAVEds64"u8.CopyTo(promoted.Bytes.AsSpan(8));
    BinaryPrimitives.WriteUInt32LittleEndian(promoted.Bytes.AsSpan(16), 28);
    largeLegacy.Bytes.AsSpan(12, 32).CopyTo(promoted.Bytes.AsSpan(48));
    promoted.SetLength(plan.CopyLength + 36);
    RecordingWaveRepair.WriteRepairedLengths(promoted, plan);
    Require(BinaryPrimitives.ReadUInt64LittleEndian(promoted.Bytes.AsSpan(28)) == (ulong)payload &&
        BinaryPrimitives.ReadUInt32LittleEndian(promoted.Bytes.AsSpan(76)) == uint.MaxValue,
        "Promoted RIFF data size did not retain its 64-bit extent.");
}

if (args.Length == 2 && args[0] == "--ffmpeg")
{
    var probePath = Path.Combine(Path.GetDirectoryName(args[1])!, "ffprobe.exe");
    var probe = await Run(probePath, ["-v", "error", "-show_streams", "-show_format", "-of", "json", repaired]);
    using var json = JsonDocument.Parse(probe);
    var stream = json.RootElement.GetProperty("streams")[0];
    Require(stream.GetProperty("codec_name").GetString() == "pcm_f32le" && stream.GetProperty("channels").GetInt32() == 8, "FFprobe did not recognize the RF64 float audio layout.");
    Require(Math.Abs(double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture) - 1) < .0001, "RF64 duration changed.");
    await Run(args[1], ["-v", "error", "-i", repaired, "-f", "null", "-"]);
    checks++;
    var video = Path.Combine(root, "video.mp4");
    await Run(args[1], ["-v", "error", "-f", "lavfi", "-i", "color=c=blue:s=64x64:r=24:d=1",
        "-c:v", "libx264", "-preset", "ultrafast", "-an", "-n", video]);
    var muxed = Path.Combine(root, "four-stems.mp4");
    var muxArguments = new List<string> { "-v", "error", "-filter_complex_threads", "1", "-i", video, "-i", repaired, "-map", "0:v:0" };
    string[] names = ["Game audio", "Microphone", "Discord", "Music"];
    muxArguments.AddRange(CaptureAudioLayout.OutputArguments("[1:a]", 8, "asetpts=PTS-STARTPTS", "Game audio", names));
    muxArguments.AddRange(["-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-t", "1", "-n", muxed]);
    await Run(args[1], muxArguments.ToArray());
    using var muxProbe = JsonDocument.Parse(await Run(probePath, ["-v", "error", "-show_streams", "-of", "json", muxed]));
    var tracks = muxProbe.RootElement.GetProperty("streams").EnumerateArray()
        .Where(item => item.GetProperty("codec_type").GetString() == "audio").ToArray();
    Require(tracks.Length == 5, "RF64 did not produce a compatibility mix and four editable stems.");
    Require(tracks.All(item => item.GetProperty("channels").GetInt32() == 2), "An RF64 stereo pair became surround audio.");
    Require(tracks.Select(item => item.GetProperty("tags").GetProperty("handler_name").GetString())
        .SequenceEqual(new[] { "Playback mix" }.Concat(names)), "RF64 capture role labels changed.");
    for (var index = 0; index < 4; index++)
    {
        var pcm = await Decode(args[1], muxed, index + 1);
        var amplitude = Amplitude(pcm, 440 + index * 220);
        var unwanted = Enumerable.Range(0, 4).Where(other => other != index)
            .Max(other => Amplitude(pcm, 440 + other * 220));
        Require(amplitude > .085 && unwanted < .008, "RF64 channel order mixed or swapped independent app audio.");
    }
}
Console.WriteLine($"PASS: {checks} RF64 checks, >4GB logical headers, interrupted writer repair, eight-channel PCM, metadata and legacy RIFF");
Console.WriteLine("Disposable RF64 files: " + root);

static async Task<string> Run(string executable, string[] args)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in args) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException(await error);
    return await output;
}

static async Task<float[]> Decode(string executable, string path, int stream)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var arg in new[] { "-v", "error", "-i", path, "-map", "0:a:" + stream, "-ac", "1", "-ar", "48000", "-f", "f32le", "pipe:1" }) start.ArgumentList.Add(arg);
    using var process = Process.Start(start)!;
    var error = process.StandardError.ReadToEndAsync();
    using var bytes = new MemoryStream();
    await process.StandardOutput.BaseStream.CopyToAsync(bytes);
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException(await error);
    var samples = new float[bytes.Length / 4];
    Buffer.BlockCopy(bytes.ToArray(), 0, samples, 0, (int)bytes.Length);
    return samples;
}

static double Amplitude(float[] samples, double frequency)
{
    const int start = 4800, count = 19200;
    var real = 0d; var imaginary = 0d;
    for (var index = 0; index < count; index++)
    {
        var phase = index * 2 * Math.PI * frequency / 48000;
        real += samples[start + index] * Math.Cos(phase);
        imaginary += samples[start + index] * Math.Sin(phase);
    }
    return 2 * Math.Sqrt(real * real + imaginary * imaginary) / count;
}

sealed class HeaderOnlyStream : Stream
{
    public byte[] Bytes { get; } = new byte[256];
    private long _length;
    public override long Position { get; set; }
    public override long Length => _length;
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override void Flush() { }
    public override void SetLength(long value) => _length = value;
    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, _ => _length + offset };
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        var take = (int)Math.Min(buffer.Length, Math.Max(0, _length - Position));
        buffer[..take].Clear();
        if (Position < Bytes.Length) Bytes.AsSpan((int)Position, Math.Min(take, Bytes.Length - (int)Position)).CopyTo(buffer);
        Position += take; return take;
    }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (Position < Bytes.Length) buffer[..Math.Min(buffer.Length, Bytes.Length - (int)Position)].CopyTo(Bytes.AsSpan((int)Position));
        Position += buffer.Length; _length = Math.Max(_length, Position);
    }
}
