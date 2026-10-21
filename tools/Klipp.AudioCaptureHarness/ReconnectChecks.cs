using System.Collections.Concurrent;
using System.Diagnostics;
using Klipp.Encoding.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

internal static class ReconnectChecks
{
    private static int _checks;

    internal static async Task RunAsync()
    {
        await IndependentStemsAsync();
        await SharedClockAsync();
        await MissingAtStartAsync();
        await StopDuringOpenAsync();
        await RestartGenerationAsync();
        await BoundedRetriesAsync();
        Console.WriteLine($"PASS: {_checks} reconnect checks (independent silence, explicit device identity, Windows default changes, immutable status, stale callbacks, bounded retries and stop races). All audio was synthetic.");
    }

    private static async Task SharedClockAsync()
    {
        using var factory = new FakeFactory { OpenDelayMilliseconds = 40 };
        await using var capture = Session(factory);
        var chunks = new ConcurrentQueue<float[]>();
        capture.Start(new CaptureAudioOptions { CaptureSystemAudio = true, CaptureMicrophone = true, MicrophoneDeviceId = "mic-a" },
            (bytes, count, _) =>
            {
                var samples = new float[count / sizeof(float)];
                Buffer.BlockCopy(bytes, 0, samples, 0, count);
                chunks.Enqueue(samples);
                return Task.CompletedTask;
            });
        await UntilAsync(() => chunks.Any(chunk => chunk[^1] != 0));
        var initial = chunks.SelectMany(chunk => chunk).ToArray();
        var epoch = capture.FirstSampleTimestamp!.Value;
        foreach (var (id, channel) in new[] { ("game-a", 0), ("mic-a", 2) })
        {
            var firstFrame = Enumerable.Range(0, initial.Length / 4).First(frame => initial[frame * 4 + channel] != 0);
            var expected = (factory.LastOpened(id).StartedTimestamp - epoch) / (double)Stopwatch.Frequency;
            Require(Math.Abs(firstFrame / 48000d - expected) < .01,
                "A slow-opening audio source was shifted to the beginning instead of its shared clock position.");
        }
        factory.Available("mic-a", false);
        await UntilAsync(() => IsReconnecting(capture, CaptureAudioSource.Microphone));
        var priorFrames = chunks.Sum(chunk => chunk.Length / 4);
        await Task.Delay(100);
        factory.Available("mic-a", true);
        await UntilAsync(() => capture.DeviceStatuses.All(IsConnected));
        var restartFrame = (factory.LastOpened("mic-a").StartedTimestamp - epoch) / (double)Stopwatch.Frequency * 48000;
        await UntilAsync(() => chunks.Sum(chunk => chunk.Length / 4) > restartFrame + 1920);
        var restored = chunks.SelectMany(chunk => chunk).ToArray();
        var restoredFrame = Enumerable.Range(priorFrames, restored.Length / 4 - priorFrames)
            .First(frame => restored[frame * 4 + 2] != 0);
        Require(Math.Abs(restoredFrame - restartFrame) < 480,
            "Fresh microphone samples jumped ahead of the continuing game/video clock after reconnecting.");
    }

    private static async Task IndependentStemsAsync()
    {
        using var factory = new FakeFactory();
        await using var capture = Session(factory);
        var chunks = new ConcurrentQueue<float[]>();
        var changes = new ConcurrentQueue<CaptureAudioDeviceStatus>();
        capture.DeviceStatusChanged += (_, status) => changes.Enqueue(status);
        capture.Start(new CaptureAudioOptions
        {
            CaptureSystemAudio = true, CaptureMicrophone = true,
            MicrophoneDeviceId = "mic-a", SystemAudioGain = .5, MicrophoneGain = .25
        }, (bytes, count, _) =>
        {
            var samples = new float[count / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, samples, 0, count);
            chunks.Enqueue(samples);
            return Task.CompletedTask;
        });
        await UntilAsync(() => capture.DeviceStatuses.Count == 2 && capture.DeviceStatuses.All(IsConnected));
        await UntilAsync(() => chunks.Any(chunk => HasSamples(chunk, .125f, .125f)));
        Require(capture.WaveFormat.SampleRate == 48000 && capture.WaveFormat.Channels == 4,
            "Initial game/microphone capture did not have fixed independent stereo pairs.");
        var originalEpoch = capture.FirstSampleTimestamp;
        var originalSnapshot = capture.DeviceStatuses;
        Require(originalSnapshot is not CaptureAudioDeviceStatus[] &&
            originalSnapshot is ICollection<CaptureAudioDeviceStatus> { IsReadOnly: true },
            "A caller could mutate a published audio device status snapshot.");
        var oldMic = factory.LastOpened("mic-a");
        factory.Available("mic-a", false);
        oldMic.Interrupt();
        await UntilAsync(() => IsReconnecting(capture, CaptureAudioSource.Microphone));
        chunks.Clear();
        await UntilAsync(() => chunks.Any(chunk => HasSamples(chunk, .125f, 0)));
        Require(capture.DeviceStatuses.Single(status => status.Source == CaptureAudioSource.System).State == CaptureAudioConnectionState.Connected,
            "Losing a microphone stopped the healthy game audio source.");
        Require(originalSnapshot.All(IsConnected), "A reconnect mutated a previously published status snapshot.");
        Require(capture.FailureMessage?.Contains("Reconnecting", StringComparison.OrdinalIgnoreCase) == true,
            "Device loss was not visible in capture health.");
        factory.DefaultMicrophone = "mic-b";
        factory.Notify();
        await Task.Delay(80);
        Require(factory.OpenedCount("mic-b") == 0 && IsReconnecting(capture, CaptureAudioSource.Microphone),
            "An explicit missing microphone silently changed to an unrelated default microphone.");

        factory.Available("mic-a", true);
        await UntilAsync(() => capture.DeviceStatuses.All(IsConnected));
        chunks.Clear();
        await UntilAsync(() => chunks.Any(chunk => HasSamples(chunk, .125f, .125f)));
        Require(factory.LastOpened("mic-a") != oldMic && oldMic.IsDisposed,
            "Reconnection reused or leaked the invalidated microphone endpoint.");
        Require(capture.FailureMessage is null, "A restored device left a stale failure warning behind.");
        Require(capture.FirstSampleTimestamp == originalEpoch && capture.WaveFormat.Channels == 4,
            "Reconnection reset the audio clock or changed the encoder channel layout.");
        Require(changes.Any(status => status.Source == CaptureAudioSource.Microphone && status.State == CaptureAudioConnectionState.Reconnecting) &&
            changes.Count(status => status.Source == CaptureAudioSource.Microphone && IsConnected(status)) == 2,
            "Microphone loss/restoration did not publish the expected status changes.");

        var interruptedMic = factory.LastOpened("mic-a");
        interruptedMic.Interrupt();
        await UntilAsync(() => factory.LastOpened("mic-a") != interruptedMic && capture.DeviceStatuses.All(IsConnected));
        Require(interruptedMic.IsDisposed,
            "A capture stream which stopped while its endpoint stayed Active did not reopen that endpoint.");

        var micOpenCount = factory.OpenedCount("mic-a");
        var oldGame = factory.LastOpened("game-a");
        factory.DefaultSystem = "game-b";
        factory.Notify();
        await UntilAsync(() => capture.DeviceStatuses.Any(status => status.Source == CaptureAudioSource.System && status.DeviceId == "game-b" && IsConnected(status)));
        chunks.Clear();
        await UntilAsync(() => chunks.Any(chunk => HasSamples(chunk, .375f, .125f)));
        Require(oldGame.IsDisposed && factory.OpenedCount("mic-a") == micOpenCount,
            "Following a new Windows playback device disturbed the explicitly selected microphone.");
        var endpointCount = factory.TotalOpened;
        oldGame.Interrupt(); oldMic.Interrupt(); oldMic.SignalData();
        await Task.Delay(80);
        Require(factory.TotalOpened == endpointCount && capture.DeviceStatuses.All(IsConnected),
            "A stale callback from a disposed endpoint interrupted its replacement.");

        await capture.StopAsync(drainTail: true);
        var chunksAtStop = chunks.Count;
        var statusesAtStop = changes.Count;
        factory.Notify(); factory.LastOpened("game-b").Interrupt();
        await Task.Delay(80);
        Require(capture.DeviceStatuses.Count == 0 && factory.TotalOpened == endpointCount &&
            changes.Count == statusesAtStop && chunks.Count == chunksAtStop && factory.AllDisposed,
            "Stopping left audio writes, reconnect attempts, status events or live native endpoints behind.");
    }

    private static async Task MissingAtStartAsync()
    {
        using var factory = new FakeFactory();
        factory.Available("mic-a", false);
        await using var capture = Session(factory);
        var chunks = new ConcurrentQueue<float[]>();
        capture.Start(new CaptureAudioOptions
        {
            CaptureSystemAudio = true, CaptureMicrophone = true, MicrophoneDeviceId = "mic-a"
        }, (bytes, count, _) =>
        {
            var samples = new float[count / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, samples, 0, count);
            chunks.Enqueue(samples);
            return Task.CompletedTask;
        });
        Require(IsReconnecting(capture, CaptureAudioSource.Microphone),
            "A microphone missing at startup had no initial actionable reconnect state.");
        await UntilAsync(() => chunks.Any(chunk => HasSamples(chunk, .25f, 0)));
        Require(capture.WaveFormat.Channels == 4 && factory.OpenedCount("mic-b") == 0,
            "A missing startup microphone broke the stem layout or recorded another microphone.");
        factory.Available("mic-a", true);
        await UntilAsync(() => capture.DeviceStatuses.All(IsConnected));
        await UntilAsync(() => chunks.Any(chunk => HasSamples(chunk, .25f, .5f)));
        Require(true, "Startup reconnect did not recover the originally selected microphone.");
    }

    private static async Task StopDuringOpenAsync()
    {
        using var factory = new FakeFactory { BlockOpen = true };
        await using var capture = Session(factory);
        capture.Start(new CaptureAudioOptions(), (_, _, _) => Task.CompletedTask);
        await factory.OpenEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var stopping = capture.StopAsync();
        Require(ReferenceEquals(stopping, capture.StopAsync()), "Concurrent stop requests did not share one cleanup operation.");
        factory.ReleaseOpen.Set();
        await stopping.WaitAsync(TimeSpan.FromSeconds(3));
        var endpoint = factory.LastOpened("game-a");
        Require(endpoint.StartCount == 0 && endpoint.IsDisposed && capture.DeviceStatuses.Count == 0,
            "An endpoint which finished opening after stop was allowed to start recording.");
        var count = factory.TotalOpened;
        factory.Notify(); endpoint.Interrupt();
        await Task.Delay(80);
        Require(factory.TotalOpened == count && factory.IsDisposed,
            "A stopped capture retained device notifications or reopened an endpoint.");
    }

    private static async Task RestartGenerationAsync()
    {
        var factories = new List<FakeFactory>();
        await using var capture = new MixedAudioCapture(() =>
        {
            var factory = new FakeFactory();
            lock (factories) factories.Add(factory);
            return factory;
        }, TimeSpan.FromMilliseconds(20));
        capture.Start(new CaptureAudioOptions(), (_, _, _) => Task.CompletedTask);
        await UntilAsync(() => capture.DeviceStatuses.Count == 1 && capture.DeviceStatuses.All(IsConnected));
        var oldFactory = factories[0];
        var oldEndpoint = oldFactory.LastOpened("game-a");
        await capture.StopAsync();
        capture.Start(new CaptureAudioOptions { CaptureSystemAudio = false, CaptureMicrophone = true }, (_, _, _) => Task.CompletedTask);
        await UntilAsync(() => capture.DeviceStatuses.Count == 1 && capture.DeviceStatuses.All(IsConnected));
        var currentFactory = factories[1];
        oldEndpoint.Interrupt(); oldEndpoint.SignalData(); oldFactory.Notify();
        await Task.Delay(80);
        Require(currentFactory.TotalOpened == 1 && capture.DeviceStatuses.Single().Source == CaptureAudioSource.Microphone &&
            capture.WaveFormat.Channels == 2,
            "A callback from the prior session changed the new session or retained its old stem layout.");
    }

    private static async Task BoundedRetriesAsync()
    {
        using var factory = new FakeFactory { FailOpens = true };
        await using var capture = Session(factory);
        capture.Start(new CaptureAudioOptions(), (_, _, _) => Task.CompletedTask);
        for (var index = 0; index < 13; index++)
        {
            factory.Notify();
            await Task.Delay(50);
        }
        Require(factory.OpenAttempts is >= 2 and <= 3 && IsReconnecting(capture, CaptureAudioSource.System),
            "Repeated endpoint callbacks bypassed the bounded reconnect backoff.");
        factory.FailOpens = false;
        await UntilAsync(() => capture.DeviceStatuses.All(IsConnected));
        Require(factory.TotalOpened == 1, "A successful retry repeatedly reopened a healthy endpoint.");

        var unavailableAttempts = 0;
        await using var unavailableService = new MixedAudioCapture(() =>
        {
            Interlocked.Increment(ref unavailableAttempts);
            throw new InvalidOperationException("Simulated unavailable Windows audio service");
        }, TimeSpan.FromMilliseconds(20));
        unavailableService.Start(new CaptureAudioOptions(), (_, _, _) => Task.CompletedTask);
        await Task.Delay(650);
        Require(unavailableAttempts is >= 2 and <= 3 && IsReconnecting(unavailableService, CaptureAudioSource.System),
            "A missing Windows audio service caused an unbounded factory creation loop.");
    }

    // Initialization opens no recording stream. It validates the actual machine's
    // native WASAPI format/buffer/event configuration without saving any audio.
    internal static void CheckNativeEndpoints()
    {
        using var factory = new WindowsAudioCaptureEndpointFactory();
        using var enumerator = new MMDeviceEnumerator();
        var count = 0;
        foreach (var source in new[] { CaptureAudioSource.System, CaptureAudioSource.Microphone })
        {
            var flow = source == CaptureAudioSource.System ? DataFlow.Render : DataFlow.Capture;
            var devices = enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
            if (devices.Count == 0) continue;
            using var device = devices[0];
            var info = new AudioCaptureEndpointInfo(device.ID, device.FriendlyName);
            using var endpoint = factory.Open(info, source);
            Require(endpoint.WaveFormat.SampleRate == 48000 && endpoint.WaveFormat.Channels == 2,
                "A real Windows endpoint could not normalize its native format for capture.");
            count++;
        }
        Console.WriteLine($"PASS: initialized {count} available native audio endpoints; no stream was started and no audio was recorded.");
    }

    private static MixedAudioCapture Session(FakeFactory factory) => new(() => factory, TimeSpan.FromMilliseconds(20));
    private static bool IsConnected(CaptureAudioDeviceStatus status) => status.State == CaptureAudioConnectionState.Connected;
    private static bool IsReconnecting(MixedAudioCapture capture, CaptureAudioSource source) =>
        capture.DeviceStatuses.Any(status => status.Source == source && status.State == CaptureAudioConnectionState.Reconnecting);
    private static bool HasSamples(float[] samples, float game, float mic) => samples.Length >= 4 &&
        Enumerable.Range(0, samples.Length / 4).All(frame => samples[frame * 4] == game && samples[frame * 4 + 1] == game &&
            samples[frame * 4 + 2] == mic && samples[frame * 4 + 3] == mic);
    private static async Task UntilAsync(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(4)) throw new TimeoutException("A simulated audio device state did not settle.");
            await Task.Delay(10);
        }
    }
    private static void Require(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
        _checks++;
    }

    private sealed class FakeFactory : IAudioCaptureEndpointFactory
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _available = ["game-a", "game-b", "mic-a", "mic-b"];
        private readonly List<FakeEndpoint> _opened = [];
        private int _attempts;
        public volatile string DefaultSystem = "game-a";
        public volatile string DefaultMicrophone = "mic-a";
        public volatile bool FailOpens;
        public bool BlockOpen;
        public int OpenDelayMilliseconds;
        public readonly TaskCompletionSource OpenEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim ReleaseOpen = new();
        public bool IsDisposed;
        public int OpenAttempts => Volatile.Read(ref _attempts);
        public int TotalOpened { get { lock (_gate) return _opened.Count; } }
        public bool AllDisposed { get { lock (_gate) return _opened.All(endpoint => endpoint.IsDisposed); } }
        public event Action? DevicesChanged;
        public void Notify() => DevicesChanged?.Invoke();
        public void Available(string id, bool available)
        {
            lock (_gate) { if (available) _available.Add(id); else _available.Remove(id); }
            Notify();
        }
        public AudioCaptureEndpointInfo Resolve(string configuredId, CaptureAudioSource source)
        {
            var id = configuredId.Length > 0 ? configuredId : source == CaptureAudioSource.System ? DefaultSystem : DefaultMicrophone;
            lock (_gate)
            {
                if (!_available.Contains(id)) throw new InvalidOperationException("Simulated unavailable endpoint");
                return new(id, id);
            }
        }
        public IAudioCaptureEndpoint Open(AudioCaptureEndpointInfo info, CaptureAudioSource source)
        {
            Interlocked.Increment(ref _attempts);
            if (FailOpens) throw new InvalidOperationException("Simulated endpoint open failure");
            var value = info.Id switch { "game-a" => .25f, "game-b" => .75f, "mic-a" => .5f, _ => -.5f };
            var endpoint = new FakeEndpoint(info, value);
            lock (_gate) _opened.Add(endpoint);
            if (BlockOpen) { OpenEntered.TrySetResult(); ReleaseOpen.Wait(TimeSpan.FromSeconds(5)); }
            if (OpenDelayMilliseconds > 0) Thread.Sleep(OpenDelayMilliseconds);
            return endpoint;
        }
        public FakeEndpoint LastOpened(string id) { lock (_gate) return _opened.Last(endpoint => endpoint.DeviceId == id); }
        public int OpenedCount(string id) { lock (_gate) return _opened.Count(endpoint => endpoint.DeviceId == id); }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeEndpoint(AudioCaptureEndpointInfo info, float value) : IAudioCaptureEndpoint
    {
        public string DeviceId => info.Id;
        public string DeviceName => info.Name;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        public int StartCount;
        public long StartedTimestamp;
        public bool IsDisposed;
        public event Action? DataReceived;
        public event Action<Exception?>? Stopped;
        public int Read(float[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            Array.Fill(buffer, value, offset, count);
            SignalData();
            return count;
        }
        public void SignalData() => DataReceived?.Invoke();
        public void Interrupt() => Stopped?.Invoke(new IOException("Simulated unplug"));
        public void Start() { ObjectDisposedException.ThrowIf(IsDisposed, this); StartedTimestamp = Stopwatch.GetTimestamp(); Interlocked.Increment(ref StartCount); SignalData(); }
        public void Stop() { }
        public void FinishCapture() { }
        public void Dispose() { IsDisposed = true; Stopped?.Invoke(null); }
    }
}
