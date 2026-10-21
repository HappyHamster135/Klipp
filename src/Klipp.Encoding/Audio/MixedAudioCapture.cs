using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Klipp.Encoding.Audio;

/// <summary>
/// Clocked system/microphone capture. Both endpoints keep their own native
/// format and are resampled to clock-aligned, independent stereo pairs; quiet sources emit continuous silence.
/// </summary>
public sealed class MixedAudioCapture : IDisposable, IAsyncDisposable
{
    private const int SampleRate = 48000;
    private const int Channels = 2;
    private const int ChunkFrames = 960;
    private const int PrebufferMilliseconds = 150;
    private readonly List<ReconnectSlot> _inputs = [];
    private readonly Func<IAudioCaptureEndpointFactory> _createFactory;
    private readonly TimeSpan _checkInterval;
    private readonly SemaphoreSlim _deviceChanged = new(0, 1);
    private readonly object _sessionGate = new();
    private Task? _endpointWorker;
    private Task? _stopTask;
    private long _generation;
    private volatile bool _running;
    private IReadOnlyList<CaptureAudioDeviceStatus> _statuses = Array.Empty<CaptureAudioDeviceStatus>();
    private CancellationTokenSource? _stop;
    private Task? _pump;
    private AudioSampleMixer? _mixer;
    private MeterState? _meterState;
    private sealed record MeterState(AudioSampleMixer Mixer, CaptureAudioSource[] Sources);
    private Func<byte[], int, CancellationToken, Task>? _sink;
    private long _startedTimestamp;
    private long _framesWritten;
    private bool _disposed;
    private long _lastDataTimestamp;
    private long _firstSampleTimestamp;
    private string? _failureMessage;

    public WaveFormat WaveFormat { get; private set; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
    public long? FirstSampleTimestamp => Interlocked.Read(ref _firstSampleTimestamp) is var value && value != 0 ? value : null;
    public bool HasRecentData => Interlocked.Read(ref _lastDataTimestamp) is var value && value != 0 &&
        (Stopwatch.GetTimestamp() - value) / (double)Stopwatch.Frequency < 2;
    public string? FailureMessage
    {
        get
        {
            if (Volatile.Read(ref _failureMessage) is { } failure) return failure;
            var message = string.Join(" ", DeviceStatuses.Where(status => status.State == CaptureAudioConnectionState.Reconnecting)
                .Select(status => status.Message));
            return message.Length == 0 ? null : message;
        }
    }
    public IReadOnlyList<CaptureAudioDeviceStatus> DeviceStatuses => Volatile.Read(ref _statuses);
    public IReadOnlyList<CaptureAudioLevel> AudioLevels
    {
        get
        {
            var state = Volatile.Read(ref _meterState);
            if (state is null || !_running) return [];
            return Array.AsReadOnly(state.Sources.Select((source, index) =>
                {
                    var level = state.Mixer.GetLevel(index);
                    return new CaptureAudioLevel(source, level.Peak, level.IsClipping, level.LimiterActive);
                }).ToArray());
        }
    }
    public event EventHandler<CaptureAudioDeviceStatus>? DeviceStatusChanged;

    public MixedAudioCapture() : this(() => new WindowsAudioCaptureEndpointFactory(), TimeSpan.FromSeconds(1)) { }

    internal MixedAudioCapture(Func<IAudioCaptureEndpointFactory> createFactory, TimeSpan checkInterval)
    {
        _createFactory = createFactory;
        _checkInterval = checkInterval;
    }

    /// <summary>Normalizes native mono/stereo/surround input without changing its sample values' timing.</summary>
    public static ISampleProvider PrepareInput(ISampleProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (provider.WaveFormat.Channels <= 0 || provider.WaveFormat.SampleRate <= 0)
            throw new ArgumentException("The selected audio format is invalid.", nameof(provider));
        if (provider.WaveFormat.Channels != 2) provider = new StereoDownmix(provider);
        if (provider.WaveFormat.SampleRate != SampleRate)
            provider = new WdlResamplingSampleProvider(provider, SampleRate);
        return provider;
    }

    public void Start(CaptureAudioOptions options, Func<byte[], int, CancellationToken, Task> sink)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(sink);
        if (!options.IsEnabled) throw new ArgumentException("Enable at least one audio source.", nameof(options));
        lock (_sessionGate)
        {
            if (_stop is not null) throw new InvalidOperationException("Audio capture is already running.");
            _stopTask = null;
            Volatile.Write(ref _failureMessage, null);
            Interlocked.Exchange(ref _lastDataTimestamp, 0);
            Interlocked.Exchange(ref _firstSampleTimestamp, 0);
            while (_deviceChanged.Wait(0)) { }
            if (options.CaptureSystemAudio)
                _inputs.Add(new ReconnectSlot(CaptureAudioSource.System, options.AudioCaptureMode == "game"
                    ? AudioApplicationCatalog.ConfigureProcess(options.GameAudioProcessId, options.GameAudioProcessName, options.GameAudioProcessStartTicks)
                    : options.SystemAudioDeviceId, options.SystemAudioGain));
            if (options.CaptureMicrophone)
                _inputs.Add(new ReconnectSlot(CaptureAudioSource.Microphone, options.MicrophoneDeviceId, options.MicrophoneGain));
            // Dedicated app tracks are exclusive to app mode: mixing the full
            // desktop with those same apps would record them twice.
            if (options.CaptureDiscordAudio && options.AudioCaptureMode == "game")
                _inputs.Add(new ReconnectSlot(CaptureAudioSource.Discord,
                    AudioApplicationCatalog.ConfigureProcess(0, options.DiscordProcessName), options.DiscordAudioGain));
            if (options.CaptureMusicAudio && options.AudioCaptureMode == "game")
                _inputs.Add(new ReconnectSlot(CaptureAudioSource.Music,
                    AudioApplicationCatalog.ConfigureProcess(0, options.MusicProcessName), options.MusicAudioGain));
            var started = Stopwatch.GetTimestamp();
            _stop = new CancellationTokenSource();
            var mixer = new AudioSampleMixer(_inputs.Select(input => ((ISampleProvider)input, (double)input.Gain)),
                separateSources: true, limiterEnabled: options.AudioLimiterEnabled);
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, mixer.OutputChannels);
            _mixer = mixer;
            Volatile.Write(ref _meterState, new(mixer, _inputs.Select(slot => slot.Source).ToArray()));
            _sink = sink;
            _startedTimestamp = started;
            Interlocked.Exchange(ref _framesWritten, 0);
            _running = true;
            var generation = Interlocked.Increment(ref _generation);
            PublishStatuses();
            var token = _stop.Token;
            _endpointWorker = Task.Run(() => MaintainEndpointsAsync(generation, token));
            _pump = Task.Run(() => PumpAsync(mixer, sink, started, token));
        }
    }

    private void WakeDeviceWorker()
    {
        if (!_running) return;
        try { _deviceChanged.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task MaintainEndpointsAsync(long generation, CancellationToken token)
    {
        IAudioCaptureEndpointFactory? factory = null;
        var factoryFailures = 0;
        var nextFactoryAttempt = 0d;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                if (factory is null && now >= nextFactoryAttempt)
                {
                    try { factory = _createFactory(); factory.DevicesChanged += WakeDeviceWorker; }
                    catch
                    {
                        nextFactoryAttempt = now + Math.Min(5, .25 * Math.Pow(2, Math.Min(5, factoryFailures++)));
                        foreach (var slot in _inputs) SetReconnecting(slot);
                    }
                }
                if (factory is not null)
                    foreach (var slot in _inputs)
                    {
                        if (token.IsCancellationRequested) break;
                        MaintainEndpoint(factory, slot, generation, token);
                    }
                await _deviceChanged.WaitAsync(_checkInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            if (factory is not null)
            {
                factory.DevicesChanged -= WakeDeviceWorker;
                try { factory.Dispose(); } catch { }
            }
        }
    }

    private void MaintainEndpoint(IAudioCaptureEndpointFactory factory, ReconnectSlot slot, long generation, CancellationToken token)
    {
        AudioCaptureEndpointInfo info;
        try
        {
            info = factory.Resolve(slot.ConfiguredId, slot.Source);
            if (slot.ConfiguredId.Length > 0 && !AudioApplicationCatalog.IsProcessConfiguration(slot.ConfiguredId) &&
                !string.Equals(info.Id, slot.ConfiguredId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected endpoint identity changed.");
            if (info.Id.StartsWith("process:", StringComparison.Ordinal) && _inputs.Any(other =>
                    other != slot && other.Source != CaptureAudioSource.Microphone && other.Endpoint?.DeviceId == info.Id))
                throw new InvalidOperationException("This app is already assigned to another audio track.");
        }
        catch (Exception exception)
        {
            slot.WasAvailable = false;
            slot.RemoveEndpoint();
            SetReconnecting(slot, exception.Message);
            return;
        }
        var endpoint = slot.Endpoint;
        if (endpoint is not null && (slot.Interrupted || !string.Equals(endpoint.DeviceId, info.Id, StringComparison.OrdinalIgnoreCase)))
        {
            slot.RemoveEndpoint();
            SetReconnecting(slot);
            endpoint = null;
        }
        if (endpoint is not null) return;
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        var becameAvailable = !slot.WasAvailable || !string.Equals(slot.ResolvedId, info.Id, StringComparison.OrdinalIgnoreCase);
        slot.WasAvailable = true;
        slot.ResolvedId = info.Id;
        if (!becameAvailable && now < slot.NextAttempt) return;
        slot.NextAttempt = now + Math.Min(5, .25 * Math.Pow(2, Math.Min(5, slot.FailedAttempts)));
        try
        {
            endpoint = factory.Open(info, slot.Source);
            if (!_running || token.IsCancellationRequested || generation != Interlocked.Read(ref _generation))
            {
                endpoint.Dispose();
                return;
            }
            var opened = endpoint;
            endpoint.DataReceived += () =>
            {
                if (_running && generation == Interlocked.Read(ref _generation) && slot.IsCurrent(opened))
                    Interlocked.Exchange(ref _lastDataTimestamp, Stopwatch.GetTimestamp());
            };
            endpoint.Stopped += _ =>
            {
                if (_running && generation == Interlocked.Read(ref _generation) && slot.MarkInterrupted(opened))
                    WakeDeviceWorker();
            };
            slot.Attach(endpoint, Stopwatch.GetTimestamp(), _startedTimestamp);
            endpoint.Start();
            if (slot.Interrupted) throw new InvalidOperationException("The audio endpoint stopped while opening.");
            slot.FailedAttempts = 0;
            SetStatus(slot, new(slot.Source, CaptureAudioConnectionState.Connected,
                endpoint.DeviceId, endpoint.DeviceName, $"{slot.Label} connected."));
        }
        catch (Exception exception)
        {
            slot.FailedAttempts++;
            slot.RemoveEndpoint();
            SetReconnecting(slot, exception.Message);
        }
    }

    private void SetReconnecting(ReconnectSlot slot, string? reason = null) => SetStatus(slot, new(slot.Source,
        CaptureAudioConnectionState.Reconnecting, slot.Status.DeviceId, slot.Status.DeviceName,
        $"{slot.Label} unavailable. Reconnecting automatically; video and other audio continue." +
            (AudioApplicationCatalog.IsProcessConfiguration(slot.ConfiguredId) && !string.IsNullOrWhiteSpace(reason) ? " " + reason : "")));

    private void SetStatus(ReconnectSlot slot, CaptureAudioDeviceStatus status)
    {
        if (!_running || slot.Status == status) return;
        slot.Status = status;
        PublishStatuses();
        try { DeviceStatusChanged?.Invoke(this, status); }
        catch (Exception exception) { Debug.WriteLine($"Audio device status listener failed: {exception}"); }
    }

    private void PublishStatuses() => Volatile.Write(ref _statuses, Array.AsReadOnly(_inputs.Select(input => input.Status).ToArray()));

    private async Task PumpAsync(AudioSampleMixer mixer, Func<byte[], int, CancellationToken, Task> sink,
        long started, CancellationToken cancellationToken)
    {
        var samples = new float[ChunkFrames * mixer.OutputChannels];
        var bytes = new byte[samples.Length * sizeof(float)];
        var sentFrames = 0L;
        try
        {
            await Task.Delay(PrebufferMilliseconds, cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _firstSampleTimestamp, started);
            while (!cancellationToken.IsCancellationRequested)
            {
                mixer.Read(samples, samples.Length);
                Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                // One awaited sink bounds memory and preserves chunk order even
                // when the encoder is briefly busy. The byte buffer is reused.
                await sink(bytes, bytes.Length, cancellationToken).ConfigureAwait(false);
                sentFrames += ChunkFrames;
                Interlocked.Exchange(ref _framesWritten, sentFrames);
                var elapsed = (Stopwatch.GetTimestamp() - started) / (double)Stopwatch.Frequency;
                var next = PrebufferMilliseconds / 1000d + sentFrames / (double)SampleRate;
                var delay = next - elapsed;
                if (delay > 0) await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { Volatile.Write(ref _failureMessage, $"Audio capture stopped: {exception.Message}"); }
    }

    public Task StopAsync(bool drainTail = false)
    {
        lock (_sessionGate)
            return _stop is null ? Task.CompletedTask : _stopTask ??= StopCoreAsync(_stop, drainTail);
    }

    private async Task StopCoreAsync(CancellationTokenSource stop, bool drainTail)
    {
        var stopped = Stopwatch.GetTimestamp();
        _running = false;
        Volatile.Write(ref _meterState, null);
        stop.Cancel();
        foreach (var input in _inputs) input.Stop();
        try
        {
            if (_endpointWorker is not null) await _endpointWorker.ConfigureAwait(false);
            foreach (var input in _inputs) input.Stop();
            if (_pump is not null) await _pump.ConfigureAwait(false);
            if (drainTail && _mixer is not null && _sink is not null)
            {
                // StopRecording only requests a stop. Join each native capture
                // before draining so its last callback cannot race this read.
                foreach (var input in _inputs) input.FinishCapture();
                // Native sources are stopped now. Preserve their pending final
                // samples up to the requested stop epoch, without another real-
                // time prebuffer wait or an unbounded encoder drain.
                Interlocked.CompareExchange(ref _firstSampleTimestamp, _startedTimestamp, 0);
                await AudioTailDrain.DrainAsync(_mixer, _sink, _startedTimestamp,
                    stopped, Interlocked.Read(ref _framesWritten)).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _failureMessage, $"Audio capture cleanup: {exception.Message}");
        }
        finally
        {
            foreach (var input in _inputs)
            {
                try { input.Dispose(); }
                catch (Exception exception) { Volatile.Write(ref _failureMessage, $"Audio device cleanup: {exception.Message}"); }
            }
            _inputs.Clear();
            Volatile.Write(ref _statuses, Array.Empty<CaptureAudioDeviceStatus>());
            stop.Dispose();
            _stop = null;
            _endpointWorker = null;
            _pump = null;
            _mixer = null;
            _sink = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopAsync().GetAwaiter().GetResult();
        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync().ConfigureAwait(false);
        _disposed = true;
    }

    private sealed class ReconnectSlot : ISampleProvider, IDisposable
    {
        private readonly object _gate = new();
        private IAudioCaptureEndpoint? _endpoint;
        private bool _interrupted;
        private long _readFrames;
        private long _sourceStartFrame;
        public CaptureAudioSource Source { get; }
        public string ConfiguredId { get; }
        public string Label => Source switch { CaptureAudioSource.Microphone => "Microphone", CaptureAudioSource.Discord => "Discord", CaptureAudioSource.Music => "Music", _ => "Game audio" };
        public float Gain { get; }
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        public CaptureAudioDeviceStatus Status { get; set; }
        public bool WasAvailable;
        public string? ResolvedId;
        public int FailedAttempts;
        public double NextAttempt;
        public IAudioCaptureEndpoint? Endpoint { get { lock (_gate) return _endpoint; } }
        public bool Interrupted { get { lock (_gate) return _interrupted; } }

        public ReconnectSlot(CaptureAudioSource source, string id, double gain)
        {
            Source = source; ConfiguredId = id?.Trim() ?? string.Empty; Gain = CaptureAudioOptions.SafeGain(gain);
            Status = new(source, CaptureAudioConnectionState.Reconnecting, ConfiguredId,
                ConfiguredId.Length == 0 ? $"Default {Label.ToLowerInvariant()}" : Label, $"Connecting {Label.ToLowerInvariant()}…");
        }

        public void Attach(IAudioCaptureEndpoint endpoint, long opened, long epoch)
        {
            lock (_gate)
            {
                _endpoint = endpoint;
                _interrupted = false;
                // The mixer runs 150ms behind wall time. A newly opened source
                // starts at its own clock position, after silence for the gap;
                // otherwise its fresh samples would jump ahead of the other stem.
                _sourceStartFrame = (long)Math.Round(Math.Max(0, opened - epoch) / (double)Stopwatch.Frequency * SampleRate);
            }
        }
        public bool IsCurrent(IAudioCaptureEndpoint endpoint) { lock (_gate) return ReferenceEquals(endpoint, _endpoint); }
        public bool MarkInterrupted(IAudioCaptureEndpoint endpoint)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(endpoint, _endpoint)) return false;
                _interrupted = true;
                return true;
            }
        }

        public void RemoveEndpoint()
        {
            IAudioCaptureEndpoint? endpoint;
            lock (_gate) { endpoint = _endpoint; _endpoint = null; _interrupted = false; }
            if (endpoint is not null) { try { endpoint.Dispose(); } catch { } }
        }

        public int Read(float[] buffer, int offset, int count)
        {
            lock (_gate)
            {
                Array.Clear(buffer, offset, count);
                var frames = count / Channels;
                var leadingFrames = Math.Clamp(_sourceStartFrame - _readFrames, 0, frames);
                if (_endpoint is not null && !_interrupted && leadingFrames < frames)
                {
                    var padding = (int)leadingFrames * Channels;
                    _endpoint.Read(buffer, offset + padding, count - padding);
                }
                _readFrames += frames;
                return count;
            }
        }
        public void Stop() { lock (_gate) _endpoint?.Stop(); }
        public void FinishCapture() { var endpoint = Endpoint; endpoint?.FinishCapture(); }
        public void Dispose() => RemoveEndpoint();
    }

    private sealed class StereoDownmix(ISampleProvider source) : ISampleProvider
    {
        private float[] _source = [];
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 2);
        public int Read(float[] buffer, int offset, int count)
        {
            var channels = source.WaveFormat.Channels;
            var frames = count / 2;
            var needed = frames * channels;
            if (_source.Length < needed) _source = new float[needed];
            var read = source.Read(_source, 0, needed) / channels;
            for (var frame = 0; frame < read; frame++)
            {
                var index = frame * channels;
                var left = _source[index];
                var right = channels == 1 ? left : _source[index + 1];
                for (var channel = 2; channel < channels; channel++)
                {
                    var sample = _source[index + channel];
                    if (channels == 4)
                    {
                        if (channel == 2) left += sample * .7071f;
                        else right += sample * .7071f;
                    }
                    else if (channels == 5 && channel >= 3)
                    {
                        if (channel == 3) left += sample * .7071f;
                        else right += sample * .7071f;
                    }
                    else if (channel == 2) { left += sample * .7071f; right += sample * .7071f; }
                    else if (channel == 3) { left += sample * .25f; right += sample * .25f; }
                    else if (channels == 7 && channel == 4) { left += sample * .5f; right += sample * .5f; }
                    else if (channels == 7 && channel == 5) left += sample * .7071f;
                    else if (channels == 7 && channel == 6) right += sample * .7071f;
                    else if (channel % 2 == 0) left += sample * .7071f;
                    else right += sample * .7071f;
                }
                buffer[offset + frame * 2] = left;
                buffer[offset + frame * 2 + 1] = right;
            }
            return read * 2;
        }
    }
}
