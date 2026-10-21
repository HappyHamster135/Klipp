using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Klipp.Core.Enums;
using Klipp.Core.Models;
using Klipp.Encoding.Audio;
using Klipp.Encoding.Clipping;
using Klipp.Encoding.FFmpeg;
using Klipp.Encoding.Muxing;
using Klipp.Encoding.Video;
using NAudio.Wave;

internal static class SeparateAudioChecks
{
    private static int _checks;
    private static string _ffmpeg = "";
    private static string _ffprobe = "";

    internal static async Task RunAsync()
    {
        var locator = new FFmpegLocator();
        if (!locator.IsInstalled) throw new InvalidOperationException("Install FFmpeg before running --tracks checks.");
        _ffmpeg = locator.FFmpegPath;
        _ffprobe = locator.FFprobePath;
        var directory = Path.Combine(Path.GetTempPath(), $"Klipp-SeparateAudio-QA-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var video = Path.Combine(directory, "video.mp4");
        await RunTextAsync(_ffmpeg, ["-v", "error", "-f", "lavfi", "-i", "color=c=black:s=64x64:r=20:d=3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-an", "-y", video]);
        var wav = Path.Combine(directory, "stems.wav");
        using (var writer = new WaveFileWriter(wav, WaveFormat.CreateIeeeFloatWaveFormat(48000, 4)))
        {
            var samples = ToneBlock(0, 48000 * 3);
            writer.WriteSamples(samples, 0, samples.Length);
        }
        var epoch = Stopwatch.Frequency * 10;
        foreach (var offset in new[] { -.15, 0, .1 })
        {
            var output = Path.Combine(directory, $"recording-{offset.ToString("0.00", CultureInfo.InvariantCulture)}.mp4");
            var result = await new FFmpegMp4Muxer(locator).MuxAsync(video, wav, output, epoch,
                epoch + (long)(offset * Stopwatch.Frequency), audioChannels: 4);
            Require(result.HasAudio, "Manual capture lost all audio.");
            await CheckStreamsAsync(output);
            await CheckTonesAsync(output);
            if (offset > 0) await CheckLeadingSilenceAsync(output, .05);
        }
        var microphone = Path.Combine(directory, "microphone.wav");
        using (var writer = new WaveFileWriter(microphone, WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)))
        {
            var samples = Enumerable.Repeat(.1f, 48000 * 3 * 2).ToArray();
            writer.WriteSamples(samples, 0, samples.Length);
        }
        var microphoneOutput = Path.Combine(directory, "microphone-only.mp4");
        await new FFmpegMp4Muxer(locator).MuxAsync(video, microphone, microphoneOutput, epoch, epoch,
            audioTrackName: CaptureAudioLayout.MicrophoneName);
        using (var probe = await ProbeAsync(microphoneOutput))
        {
            var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            Require(streams.Length == 1 && streams[0].GetProperty("tags").GetProperty("handler_name").GetString() == CaptureAudioLayout.MicrophoneName,
                "Microphone-only recordings were mislabeled or created empty extra streams.");
        }
        var silent = Path.Combine(directory, "video-only.mp4");
        var silentResult = await new FFmpegMp4Muxer(locator).MuxAsync(video, null, silent, epoch, null);
        var silentProbe = JsonDocument.Parse(await RunTextAsync(_ffprobe,
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", silent]));
        using (silentProbe)
        {
            var silentStreams = silentProbe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var silentDuration = double.Parse(silentProbe.RootElement.GetProperty("format").GetProperty("duration").GetString()!, CultureInfo.InvariantCulture);
            Require(!silentResult.HasAudio && silentStreams.Length == 1 &&
                silentStreams[0].GetProperty("codec_type").GetString() == "video" && Math.Abs(silentDuration - 3) < .1,
                "Turning off both sources stopped video-only recording from working.");
        }

        // A just-opened MP4 may still contain only its header. It must not cause
        // the replay extractor to discard the last completed segment as well.
        var freshnessSegments = Path.Combine(directory, "freshness-segments");
        Directory.CreateDirectory(freshnessSegments);
        var completedSource = Path.Combine(directory, "recording-0.00.mp4");
        var freshnessEpoch = DateTime.UtcNow;
        for (var index = 0; index < 2; index++)
        {
            var segment = Path.Combine(freshnessSegments, $"seg_{index:000}.mp4");
            File.Copy(completedSource, segment);
            File.SetLastWriteTimeUtc(segment, freshnessEpoch.AddSeconds(index - 3));
        }
        var openSegment = Path.Combine(freshnessSegments, "seg_002.mp4");
        await File.WriteAllBytesAsync(openSegment, new byte[48]);
        File.SetLastWriteTimeUtc(openSegment, freshnessEpoch);
        var freshReplay = Path.Combine(directory, "freshness-replay.mp4");
        var freshResult = await new ClipExtractor(locator).SaveLastSecondsAsync(freshnessSegments, freshReplay, 6, 3);
        Require(freshResult.SegmentsUsed == 2,
            "A 48-byte open segment caused the newest completed replay segment to be skipped.");
        var freshDuration = double.Parse(await RunTextAsync(_ffprobe,
            ["-v", "error", "-show_entries", "format=duration", "-of", "default=nokey=1:noprint_wrappers=1", freshReplay]), CultureInfo.InvariantCulture);
        Require(freshDuration >= 5.9 && freshDuration < 6.2,
            "The freshest completed replay segment was missing from the actual concatenated video.");
        await CheckStreamsAsync(freshReplay);

        // Exercise the real named-pipe segment encoder and ring rotation with
        // generated pixels/tones. This opens no capture device or UI surface.
        var segments = Path.Combine(directory, "segments");
        var replay = Path.Combine(directory, "instant-clip.mp4");
        await using (var recorder = new FFmpegSegmentRecorder(locator) { SegmentDurationSeconds = 1, MaxSegments = 4 })
        {
            recorder.SetSegmentDirectory(segments);
            recorder.EnableAudio(48000, 4);
            await recorder.InitializeAsync(RecordingSettings.Default1080p60 with { Width = 64, Height = 64, FrameRate = 20, VideoBitrate = 200_000 });
            await foreach (var _ in recorder.EncodeAsync(new VideoFrame(64, 64, PixelFormat.Bgra8, 0, new byte[64 * 64 * 4]))) { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var pump = Task.Run(async () =>
            {
                for (var index = 0; index < 300; index++)
                {
                    var samples = ToneBlock(index * 960, 960);
                    var bytes = new byte[samples.Length * sizeof(float)];
                    Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                    await recorder.WriteAudioAsync(bytes, bytes.Length, timeout.Token);
                    await Task.Delay(20, timeout.Token);
                }
            });
            await pump;
            // Match the app: save completed segments while monitoring, then
            // abort the unfinished last segment when monitoring stops.
            await new ClipExtractor(locator).SaveLastSecondsAsync(segments, replay, 2, 1, timeout.Token);
            await recorder.AbortAsync();
        }
        Require(Directory.GetFiles(segments, "seg_*.mp4").Length <= 4,
            "Separate audio streams made replay storage grow beyond the ring limit.");
        await CheckStreamsAsync(replay);
        await CheckTonesAsync(replay);
        await CheckFourSourcesAsync(locator, directory, video, epoch);
        Console.WriteLine($"PASS: {_checks} separate-audio integration checks; manual offsets, source isolation, summed mix, dispositions, single-source/video-only compatibility, real segment pipe, bounded ring and replay extraction. Artifacts: {directory}");
    }

    private static async Task CheckFourSourcesAsync(FFmpegLocator locator, string directory, string video, long epoch)
    {
        var names = new[] { "Game audio", "Microphone", "Discord", "Music" };
        var frequencies = new[] { 330, 660, 990, 1320 };
        float[] FourTones(int firstFrame, int frames)
        {
            var samples = new float[frames * 8];
            for (var frame = 0; frame < frames; frame++)
                for (var source = 0; source < 4; source++)
                    samples[frame * 8 + source * 2] = samples[frame * 8 + source * 2 + 1] =
                        (float)(.1 * Math.Sin(2 * Math.PI * frequencies[source] * (frame + firstFrame) / 48000));
            return samples;
        }
        async Task CheckOutput(string output)
        {
            using var probe = await ProbeAsync(output);
            var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            Require(streams.Length == 5 && streams.Select(stream => stream.GetProperty("tags").GetProperty("handler_name").GetString())
                .SequenceEqual(new[] { "Playback mix" }.Concat(names)), "Four-source recording lost its named default mix or independent stems.");
            Require(streams.All(stream => stream.GetProperty("channels").GetInt32() == 2) &&
                streams.Select((stream, index) => stream.GetProperty("disposition").GetProperty("default").GetInt32() == (index == 0 ? 1 : 0)).All(value => value),
                "Four-source MP4 did not expose stereo stems with only the mix selected by default.");
            for (var source = 0; source < 4; source++)
            {
                var data = await RunBytesAsync(_ffmpeg, ["-v", "error", "-i", output, "-ss", "0.2", "-t", "0.7", "-map", $"0:a:{source + 1}", "-ac", "1", "-f", "f32le", "-"]);
                var samples = new float[data.Length / 4]; Buffer.BlockCopy(data, 0, samples, 0, data.Length);
                Require(Amplitude(samples, frequencies[source]) > .07, $"Four-source {names[source]} stem lost its generated audio.");
                foreach (var other in frequencies.Where(frequency => frequency != frequencies[source]))
                    Require(Amplitude(samples, other) < .01, $"Four-source {names[source]} stem leaked unrelated audio.");
            }
        }
        var wav = Path.Combine(directory, "four-stems.wav");
        using (var writer = new WaveFileWriter(wav, WaveFormat.CreateIeeeFloatWaveFormat(48000, 8)))
        {
            var samples = FourTones(0, 48000 * 3); writer.WriteSamples(samples, 0, samples.Length);
        }
        var manual = Path.Combine(directory, "four-stems-recording.mp4");
        await new FFmpegMp4Muxer(locator).MuxAsync(video, wav, manual, epoch, epoch, audioChannels: 8, audioTrackNames: names);
        await CheckOutput(manual);
        var segments = Path.Combine(directory, "four-stem-segments");
        var replay = Path.Combine(directory, "four-stem-replay.mp4");
        await using (var recorder = new FFmpegSegmentRecorder(locator) { SegmentDurationSeconds = 1, MaxSegments = 4 })
        {
            recorder.SetSegmentDirectory(segments);
            recorder.EnableAudio(48000, 8, audioTrackNames: names);
            await recorder.InitializeAsync(RecordingSettings.Default1080p60 with { Width = 64, Height = 64, FrameRate = 20, VideoBitrate = 200_000 });
            await foreach (var _ in recorder.EncodeAsync(new VideoFrame(64, 64, PixelFormat.Bgra8, 0, new byte[64 * 64 * 4]))) { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            for (var index = 0; index < 300; index++)
            {
                var samples = FourTones(index * 960, 960); var bytes = new byte[samples.Length * sizeof(float)];
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                await recorder.WriteAudioAsync(bytes, bytes.Length, timeout.Token);
                await Task.Delay(20, timeout.Token);
            }
            await new ClipExtractor(locator).SaveLastSecondsAsync(segments, replay, 2, 1, timeout.Token);
            Console.WriteLine($"Four-stem replay encoder: {recorder.EncoderDisplayName}");
            await recorder.AbortAsync();
        }
        await CheckOutput(replay);
        Require(Directory.GetFiles(segments, "seg_*.mp4").Length <= 4, "Four-stem replay exceeded its bounded ring storage.");
    }

    private static float[] ToneBlock(int firstFrame, int frames)
    {
        var samples = new float[frames * 4];
        for (var frame = 0; frame < frames; frame++)
        {
            var time = (firstFrame + frame) / 48000d;
            samples[frame * 4] = samples[frame * 4 + 1] = (float)(.2 * Math.Sin(2 * Math.PI * 440 * time));
            samples[frame * 4 + 2] = samples[frame * 4 + 3] = (float)(.15 * Math.Sin(2 * Math.PI * 880 * time));
        }
        return samples;
    }

    private static async Task CheckStreamsAsync(string path)
    {
        using var probe = await ProbeAsync(path);
        var streams = probe.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        Require(streams.Length == 3, "The MP4 did not retain playback mix plus both independent stems.");
        var names = new[] { CaptureAudioLayout.MixName, CaptureAudioLayout.SystemName, CaptureAudioLayout.MicrophoneName };
        for (var index = 0; index < streams.Length; index++)
        {
            Require(streams[index].GetProperty("tags").GetProperty("handler_name").GetString() == names[index],
                "A recorded audio stream lost its stable editor-visible name.");
            Require(streams[index].GetProperty("disposition").GetProperty("default").GetInt32() == (index == 0 ? 1 : 0),
                "A non-mixed stem became the default playback audio.");
            Require(streams[index].GetProperty("channels").GetInt32() == 2, "The PCM pairs were incorrectly saved as surround audio.");
        }
    }

    private static async Task CheckTonesAsync(string path)
    {
        var levels = new List<(double Game, double Mic)>();
        for (var stream = 0; stream < 3; stream++)
        {
            var bytes = await RunBytesAsync(_ffmpeg, ["-v", "error", "-i", path, "-ss", "0.5", "-t", "1", "-map", $"0:a:{stream}", "-ac", "1", "-ar", "48000", "-f", "f32le", "-"]);
            var samples = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
            levels.Add((Amplitude(samples, 440), Amplitude(samples, 880)));
        }
        Require(levels[1].Game > .17 && levels[1].Mic < .005 && levels[2].Mic > .12 && levels[2].Game < .005,
            "Microphone/game content leaked into the other editable stem.");
        Require(Math.Abs(levels[0].Game - levels[1].Game) < .02 && Math.Abs(levels[0].Mic - levels[2].Mic) < .02,
            "Playback mix averaged or doubled the sources instead of summing their actual levels.");
    }

    private static async Task CheckLeadingSilenceAsync(string path, double seconds)
    {
        for (var stream = 0; stream < 3; stream++)
        {
            var bytes = await RunBytesAsync(_ffmpeg, ["-v", "error", "-i", path, "-t", seconds.ToString(CultureInfo.InvariantCulture), "-map", $"0:a:{stream}", "-f", "f32le", "-"]);
            var max = 0f;
            for (var index = 0; index < bytes.Length; index += 4) max = Math.Max(max, Math.Abs(BitConverter.ToSingle(bytes, index)));
            Require(max < .005, "A delayed source was not aligned with the video start.");
        }
    }

    private static double Amplitude(float[] samples, int frequency)
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

    private static async Task<JsonDocument> ProbeAsync(string path) => JsonDocument.Parse(await RunTextAsync(_ffprobe,
        ["-v", "error", "-select_streams", "a", "-show_streams", "-of", "json", path]));

    private static async Task<string> RunTextAsync(string executable, string[] arguments) =>
        System.Text.Encoding.UTF8.GetString(await RunBytesAsync(executable, arguments));

    private static async Task<byte[]> RunBytesAsync(string executable, string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start FFmpeg.");
        using var bytes = new MemoryStream();
        var read = process.StandardOutput.BaseStream.CopyToAsync(bytes);
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);
        await read;
        var error = await errors;
        if (process.ExitCode != 0) throw new InvalidOperationException(error);
        return bytes.ToArray();
    }

    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
        _checks++;
    }
}
