using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Klipp.Encoding.Audio;
using Klipp.Encoding.Muxing;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

internal static class ProcessAudioChecks
{
    private static int _checks;
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
    }

    internal static async Task RunSyntheticAsync()
    {
        var options = new CaptureAudioOptions
        {
            AudioCaptureMode = "game", CaptureSystemAudio = true, GameAudioProcessName = "Synthetic game",
            CaptureMicrophone = true, CaptureDiscordAudio = true, DiscordProcessName = "Synthetic Discord",
            CaptureMusicAudio = true, MusicProcessName = "Synthetic music", AudioLimiterEnabled = true
        };
        Require(options.OutputChannels == 8 && options.TrackNames.SequenceEqual(new[] { "Game audio", "Microphone", "Discord", "Music" }),
            "Four selected audio roles did not keep fixed named stereo pairs.");
        Require((options with { AudioCaptureMode = "desktop" }).OutputChannels == 4,
            "Desktop audio duplicated Discord/music into extra tracks.");
        var mixer = new AudioSampleMixer(new[] { .1f, .2f, .3f, .4f }.Select(value => ((ISampleProvider)new ConstantSamples(value), 1d)), separateSources: true);
        var buffer = new float[960 * 8];
        mixer.Read(buffer, buffer.Length);
        Require(buffer.Take(8).SequenceEqual(new[] { .1f, .1f, .2f, .2f, .3f, .3f, .4f, .4f }),
            "Four-track mixing merged or reordered the stereo source slots.");
        var limited = new AudioSampleMixer([((ISampleProvider)new ConstantSamples(.8f), 2d)], separateSources: true, limiterEnabled: true);
        limited.Read(buffer, 1920);
        var level = limited.GetLevel(0);
        Require(buffer.Take(1920).All(sample => sample <= .98001 && sample >= -.98001), "Limiter allowed boosted samples beyond its ceiling.");
        Require(level.Peak > 1 && level.IsClipping && level.LimiterActive, "Live meters did not expose pre-limiter clipping and limiting activity.");
        var graph = CaptureAudioLayout.OutputArguments("[0:a]", 8, "anull", "Game audio", options.TrackNames);
        Require(graph.Count(item => item == "-map") == 5 && graph.Contains("title=Discord") && graph.Contains("title=Music"),
            "The MP4 graph lost stems or its default playback mix.");
        await using (var capture = new MixedAudioCapture(() => new FourSourceFactory(), TimeSpan.FromMilliseconds(15)))
        {
            var chunks = new ConcurrentQueue<float[]>();
            capture.Start(options, (bytes, count, _) =>
            {
                var samples = new float[count / 4];
                Buffer.BlockCopy(bytes, 0, samples, 0, count);
                chunks.Enqueue(samples);
                return Task.CompletedTask;
            });
            await UntilAsync(() => capture.DeviceStatuses.Count == 4 && capture.DeviceStatuses.All(status => status.State == CaptureAudioConnectionState.Connected));
            await UntilAsync(() => chunks.Any(chunk => chunk[^1] != 0));
            Require(capture.WaveFormat.Channels == 8 && capture.AudioLevels.Select(level => level.Source).SequenceEqual(Enum.GetValues<CaptureAudioSource>()),
                "Live levels did not retain the source role order.");
            Require(capture.AudioLevels is ICollection<CaptureAudioLevel> { IsReadOnly: true }, "A caller could mutate the audio meter snapshot.");
            var readers = Task.Run(async () => { for (var index = 0; index < 100; index++) { _ = capture.AudioLevels; await Task.Yield(); } });
            await capture.StopAsync();
            await readers;
            Require(capture.AudioLevels.Count == 0, "Stopped capture retained stale live meter activity.");
        }
        Require(AudioApplicationCatalog.ConfigureProcess(0, "Discord.exe") == "app:Discord", "Saved app selection retained an unstable PID or extension.");
        using (var current = Process.GetCurrentProcess())
        {
            var rejected = false;
            try { _ = AudioApplicationCatalog.ResolveProcess($"pid:{current.Id}:{current.StartTime.ToUniversalTime().Ticks - 1}"); }
            catch (Exception exception) when (exception is InvalidOperationException or PlatformNotSupportedException) { rejected = true; }
            Require(rejected, "A reused process ID could capture an unrelated application.");
        }
        await using (var duplicate = new MixedAudioCapture(() => new DuplicateAppFactory(), TimeSpan.FromMilliseconds(15)))
        {
            duplicate.Start(options with { CaptureMicrophone = false, CaptureMusicAudio = false }, (_, _, _) => Task.CompletedTask);
            await UntilAsync(() => duplicate.DeviceStatuses.Any(status => status.State == CaptureAudioConnectionState.Connected));
            await Task.Delay(60);
            Require(duplicate.DeviceStatuses.Count(status => status.State == CaptureAudioConnectionState.Connected) == 1 &&
                duplicate.DeviceStatuses.Single(status => status.Source == CaptureAudioSource.Discord).Message.Contains("already assigned", StringComparison.Ordinal),
                "Assigning one app to two tracks captured it twice instead of keeping the conflicting track silent.");
            await duplicate.StopAsync();
        }
        Console.WriteLine($"PASS: {_checks} app-audio/limiter checks (four stereo roles, desktop duplicate prevention, named mux layout, limiter ceiling, immutable live meters and stop races). All samples synthetic.");
    }

    internal static async Task PlayToneAsync(string[] args)
    {
        var frequency = double.Parse(args[Array.IndexOf(args, "--tone-source") + 1], CultureInfo.InvariantCulture);
        using var output = new WaveOutEvent { DesiredLatency = 70, NumberOfBuffers = 3 };
        output.Init(new SignalGenerator(48000, 2) { Frequency = frequency, Gain = .12, Type = SignalGeneratorType.Sin });
        output.Play();
        Console.WriteLine("READY");
        await Task.Delay(14000);
        output.Stop();
    }

    internal static async Task PlayToneParentAsync(string[] args)
    {
        // The selected parent renders no audio. Only its child produces a tone,
        // matching Electron apps and games with a separate audio subprocess.
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--tone-source");
        start.ArgumentList.Add(args[Array.IndexOf(args, "--tone-parent") + 1]);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the tone child.");
        if (await child.StandardOutput.ReadLineAsync() != "READY") throw new InvalidOperationException("Tone child failed: " + await child.StandardError.ReadToEndAsync());
        Console.WriteLine("READY");
        await child.WaitForExitAsync();
    }

    internal static async Task RunNativeAsync()
    {
        var priorChecks = _checks;
        if (!ProcessAudioCaptureSupport.IsSupported) throw new PlatformNotSupportedException(ProcessAudioCaptureSupport.Description);
        var root = Path.Combine(Path.GetTempPath(), "Klipp-AppAudio-QA-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
        var executable = Path.Combine(AppContext.BaseDirectory, "Klipp.AudioCaptureHarness.exe");
        var processes = new List<Process>();
        try
        {
            foreach (var (label, frequency) in new[] { ("Game", 440), ("Discord", 880), ("Music", 1760) })
            {
                var path = Path.Combine(root, $"Klipp{label}Tone{Guid.NewGuid():N}.exe");
                File.Copy(executable, path);
                var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add("--tone-parent"); start.ArgumentList.Add(frequency.ToString(CultureInfo.InvariantCulture));
                var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the generated-tone app.");
                processes.Add(process);
                using var readyTimeout = new CancellationTokenSource(5000);
                if (await process.StandardOutput.ReadLineAsync(readyTimeout.Token) != "READY")
                    throw new InvalidOperationException("Tone output did not become ready: " + await process.StandardError.ReadToEndAsync());
            }
            var nativeOptions = new CaptureAudioOptions
            {
                AudioCaptureMode = "game", GameAudioProcessId = processes[0].Id, CaptureSystemAudio = true,
                CaptureMicrophone = false, CaptureDiscordAudio = true, DiscordProcessName = processes[1].ProcessName,
                CaptureMusicAudio = true, MusicProcessName = processes[2].ProcessName
            };
            using (var directFactory = new WindowsAudioCaptureEndpointFactory())
            {
                var identity = directFactory.Resolve(AudioApplicationCatalog.ConfigureProcess(processes[0].Id, null), CaptureAudioSource.System);
                using var directEndpoint = directFactory.Open(identity, CaptureAudioSource.System);
                directEndpoint.Start();
                await Task.Delay(80);
                directEndpoint.FinishCapture();
            }
            var chunks = new ConcurrentQueue<float[]>();
            await using var capture = new MixedAudioCapture();
            capture.Start(nativeOptions, (bytes, count, _) =>
            {
                var samples = new float[count / 4]; Buffer.BlockCopy(bytes, 0, samples, 0, count); chunks.Enqueue(samples);
                return Task.CompletedTask;
            });
            await UntilAsync(() => capture.DeviceStatuses.Count == 3 && capture.DeviceStatuses.All(status => status.State == CaptureAudioConnectionState.Connected),
                () => string.Join(" | ", capture.DeviceStatuses.Select(status => status.Message)), 7000);
            await UntilAsync(() => chunks.Count >= 110, timeout: 5000);
            await capture.StopAsync(drainTail: true);
            var pcm = chunks.SelectMany(chunk => chunk).ToArray();
            var frames = pcm.Length / 6;
            Require(frames >= 48000, "Native process capture produced too few frames.");
            foreach (var (source, frequency) in new[] { (0, 440), (1, 880), (2, 1760) })
            {
                var samples = Enumerable.Range(Math.Min(24000, frames / 3), Math.Min(48000, frames / 2))
                    .Select(frame => pcm[frame * 6 + source * 2]).ToArray();
                var desired = Magnitude(samples, frequency);
                Require(desired > .025, $"App audio source {source} did not contain its own generated tone ({desired}).");
                foreach (var other in new[] { 440, 880, 1760 }.Where(other => other != frequency))
                    Require(Magnitude(samples, other) < desired * .08, $"App audio source {source} leaked the unrelated {other}Hz app.");
                Console.WriteLine($"Native role{source}: own {frequency}Hz amplitude={desired:0.0000}; unrelated apps below8%.");
            }
            Require(capture.WaveFormat.Channels == 6, "Three native apps did not retain six separate PCM channels.");
            var wavePath = Path.Combine(root, "native-four-slot-capture.wav");
            using (var recorder = new WasapiLoopbackWavRecorder())
            {
                recorder.Start(wavePath, nativeOptions with
                {
                    CaptureMicrophone = true,
                    // Never open a real microphone: the fourth slot stays silent
                    // while the other three tracks capture only our own tone apps.
                    MicrophoneDeviceId = "Klipp-QA-missing-mic-" + Guid.NewGuid().ToString("N")
                });
                await UntilAsync(() => recorder.DeviceStatuses.Count(status => status.State == CaptureAudioConnectionState.Connected) == 3,
                    () => string.Join(" | ", recorder.DeviceStatuses.Select(status => status.Message)), 7000);
                await UntilAsync(() => recorder.BytesWritten >= 48000 * 8 * sizeof(float) * 1.4, timeout: 5000);
                recorder.Stop();
                Require(recorder.AudioChannels == 8 && recorder.AudioTrackNames.SequenceEqual(new[] { "Game audio", "Microphone", "Discord", "Music" }),
                    "Native RF64 capture changed the four source-slot identities.");
            }
            var wave = await File.ReadAllBytesAsync(wavePath);
            Require(wave.AsSpan(0, 4).SequenceEqual("RF64"u8), "The real WASAPI recorder still used a 32-bit RIFF writer.");
            var wavePcm = new float[(wave.Length - Rf64WaveFileWriter.HeaderLength) / sizeof(float)];
            Buffer.BlockCopy(wave, Rf64WaveFileWriter.HeaderLength, wavePcm, 0, wavePcm.Length * sizeof(float));
            var waveFrames = wavePcm.Length / 8;
            Require(waveFrames > 48000, "Stopping the real RF64 recorder lost or truncated its pending samples.");
            Require(Enumerable.Range(0, waveFrames).All(frame => wavePcm[frame * 8 + 2] == 0 && wavePcm[frame * 8 + 3] == 0),
                "An unavailable explicit microphone captured another device instead of preserving silence.");
            foreach (var (source, frequency) in new[] { (0, 440), (2, 880), (3, 1760) })
            {
                var samples = Enumerable.Range(24000, Math.Min(36000, waveFrames - 24000))
                    .Select(frame => wavePcm[frame * 8 + source * 2]).ToArray();
                var desired = Magnitude(samples, frequency);
                Require(desired > .025, $"The native RF64 source {source} lost its own generated tone.");
                foreach (var other in new[] { 440, 880, 1760 }.Where(other => other != frequency))
                    Require(Magnitude(samples, other) < desired * .08, $"Native RF64 source {source} leaked the unrelated {other}Hz app.");
            }
            Console.WriteLine($"PASS: {_checks - priorChecks} native process/RF64 checks; three generated-tone process trees isolated, four stereo slots preserved with silent missing mic. No microphone, full desktop audio, or personal audio was captured. Disposable fixtures: {root}");
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
            }
        }
    }

    private static double Magnitude(float[] samples, int frequency)
    {
        double real = 0, imaginary = 0;
        for (var index = 0; index < samples.Length; index++)
        {
            var angle = 2 * Math.PI * frequency * index / 48000;
            real += samples[index] * Math.Cos(angle); imaginary += samples[index] * Math.Sin(angle);
        }
        return Math.Sqrt(real * real + imaginary * imaginary) * 2 / samples.Length;
    }
    private static async Task UntilAsync(Func<bool> predicate, Func<string>? detail = null, int timeout = 3000)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            if (clock.ElapsedMilliseconds > timeout) throw new TimeoutException("Audio check timed out. " + detail?.Invoke());
            await Task.Delay(15);
        }
    }
    private sealed class ConstantSamples(float value) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int Read(float[] buffer, int offset, int count) { Array.Fill(buffer, value, offset, count); return count; }
    }
    private sealed class FourSourceFactory : IAudioCaptureEndpointFactory
    {
        public event Action? DevicesChanged { add { } remove { } }
        public AudioCaptureEndpointInfo Resolve(string configuredId, CaptureAudioSource source) => new(source == CaptureAudioSource.Microphone ? "mic" : source.ToString(), source.ToString());
        public IAudioCaptureEndpoint Open(AudioCaptureEndpointInfo info, CaptureAudioSource source) => new Endpoint(info, (int)source * .1f + .1f);
        public void Dispose() { }
    }
    private sealed class DuplicateAppFactory : IAudioCaptureEndpointFactory
    {
        public event Action? DevicesChanged { add { } remove { } }
        public AudioCaptureEndpointInfo Resolve(string configuredId, CaptureAudioSource source) => new("process:123:456", "Same app");
        public IAudioCaptureEndpoint Open(AudioCaptureEndpointInfo info, CaptureAudioSource source) => new Endpoint(info, .1f);
        public void Dispose() { }
    }
    private sealed class Endpoint(AudioCaptureEndpointInfo info, float value) : IAudioCaptureEndpoint
    {
        public string DeviceId => info.Id;
        public string DeviceName => info.Name;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public event Action? DataReceived;
        public event Action<Exception?>? Stopped { add { } remove { } }
        public void Start() { DataReceived?.Invoke(); }
        public void Stop() { }
        public void FinishCapture() { }
        public int Read(float[] buffer, int offset, int count) { Array.Fill(buffer, value, offset, count); return count; }
        public void Dispose() { }
    }
}
