using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace Klipp.Encoding.Audio;

/// <summary>Device callbacks only wake the session worker; endpoint teardown never runs in a COM callback.</summary>
internal sealed class WindowsAudioCaptureEndpointFactory : IAudioCaptureEndpointFactory
{
    private MMDeviceEnumerator? _notifications;
    private readonly DeviceNotifications _listener;
    public event Action? DevicesChanged;

    internal WindowsAudioCaptureEndpointFactory()
    {
        _listener = new DeviceNotifications(() => DevicesChanged?.Invoke());
        try
        {
            _notifications = new MMDeviceEnumerator();
            _notifications.RegisterEndpointNotificationCallback(_listener);
        }
        catch
        {
            // Polling still provides recovery if endpoint notifications are unavailable.
            _notifications?.Dispose();
            _notifications = null;
        }
    }

    public AudioCaptureEndpointInfo Resolve(string configuredId, CaptureAudioSource source)
    {
        if (AudioApplicationCatalog.IsProcessConfiguration(configuredId))
            return AudioApplicationCatalog.ResolveProcess(configuredId);
        using var device = CaptureAudioOptions.ResolveDevice(configuredId,
            source == CaptureAudioSource.System ? DataFlow.Render : DataFlow.Capture);
        return new(device.ID, device.FriendlyName);
    }

    public IAudioCaptureEndpoint Open(AudioCaptureEndpointInfo info, CaptureAudioSource source)
    {
        if (info.Id.StartsWith("process:", StringComparison.Ordinal))
        {
            var verified = AudioApplicationCatalog.ResolveProcess(info.Id);
            return new WindowsAudioCaptureEndpoint(verified);
        }
        var device = CaptureAudioOptions.ResolveDevice(info.Id,
            source == CaptureAudioSource.System ? DataFlow.Render : DataFlow.Capture);
        try { return new WindowsAudioCaptureEndpoint(device, source); }
        catch { device.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (_notifications is null) return;
        try { _notifications.UnregisterEndpointNotificationCallback(_listener); } catch { }
        _notifications.Dispose();
        _notifications = null;
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class DeviceNotifications(Action changed) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState state) => changed();
        public void OnDeviceAdded(string deviceId) => changed();
        public void OnDeviceRemoved(string deviceId) => changed();
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string deviceId) => changed();
        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }
}

/// <summary>
/// A guarded WASAPI reader. NAudio 2.3's WasapiCapture can throw from its capture
/// thread's final AudioClient.Stop when an endpoint disappears. Keeping that
/// boundary here lets a disconnected endpoint recover without terminating Klipp.
/// </summary>
internal sealed class WindowsAudioCaptureEndpoint : IAudioCaptureEndpoint
{
    private readonly MMDevice? _device;
    private readonly AudioClient _client;
    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _provider;
    private readonly object _bufferGate = new();
    private readonly EventWaitHandle _ready = new(false, EventResetMode.AutoReset);
    private Task? _captureTask;
    private byte[] _packet = [];
    private volatile bool _stopping;
    private int _disposed;
    public string DeviceId { get; }
    public string DeviceName { get; }
    public WaveFormat WaveFormat => _provider.WaveFormat;
    public event Action? DataReceived;
    public event Action<Exception?>? Stopped;

    internal WindowsAudioCaptureEndpoint(MMDevice device, CaptureAudioSource source)
    {
        _device = device;
        DeviceId = device.ID;
        DeviceName = device.FriendlyName;
        _client = device.AudioClient;
        try
        {
            var nativeFormat = _client.MixFormat;
            var flags = AudioClientStreamFlags.EventCallback |
                (source == CaptureAudioSource.System ? AudioClientStreamFlags.Loopback : AudioClientStreamFlags.None);
            _client.Initialize(AudioClientShareMode.Shared, flags, 1_000_000, 0, nativeFormat, Guid.Empty);
            _client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
            var standardFormat = nativeFormat is WaveFormatExtensible extensible
                ? extensible.ToStandardWaveFormat() : nativeFormat;
            _buffer = new BufferedWaveProvider(standardFormat)
            {
                BufferDuration = TimeSpan.FromSeconds(1), ReadFully = true, DiscardOnBufferOverflow = true
            };
            _provider = MixedAudioCapture.PrepareInput(_buffer.ToSampleProvider());
        }
        catch
        {
            _client.Dispose();
            _ready.Dispose();
            throw;
        }
    }

    internal WindowsAudioCaptureEndpoint(AudioCaptureEndpointInfo info)
    {
        DeviceId = info.Id;
        DeviceName = info.Name;
        var pid = int.Parse(info.Id.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
        _client = ProcessLoopbackActivation.Activate(pid);
        try
        {
            // Virtual process loopback has no endpoint mix format. Request a
            // known stereo PCM format, with engine conversion as in Microsoft's sample.
            var format = new WaveFormat(48000, 16, 2);
            var flags = AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.Loopback |
                unchecked((AudioClientStreamFlags)0x80000000); // AUTOCONVERTPCM
            _client.Initialize(AudioClientShareMode.Shared, flags, 0, 0, format, Guid.Empty);
            _client.SetEventHandle(_ready.SafeWaitHandle.DangerousGetHandle());
            _buffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromSeconds(1), ReadFully = true, DiscardOnBufferOverflow = true
            };
            _provider = MixedAudioCapture.PrepareInput(_buffer.ToSampleProvider());
        }
        catch { _client.Dispose(); _ready.Dispose(); throw; }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _client.Start();
        _captureTask = Task.Factory.StartNew(ReadPackets, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void ReadPackets()
    {
        Exception? failure = null;
        try
        {
            var reader = _client.AudioCaptureClient;
            var blockAlign = _buffer.WaveFormat.BlockAlign;
            while (!_stopping)
            {
                _ready.WaitOne(100);
                if (_stopping) break;
                while (reader.GetNextPacketSize() > 0 && !_stopping)
                {
                    var address = reader.GetBuffer(out var frames, out var flags);
                    try
                    {
                        var count = checked(frames * blockAlign);
                        if (_packet.Length < count) _packet = new byte[count];
                        if ((flags & AudioClientBufferFlags.Silent) != 0) Array.Clear(_packet, 0, count);
                        else Marshal.Copy(address, _packet, 0, count);
                        lock (_bufferGate)
                        {
                            if (_buffer.BufferedBytes + count > _buffer.WaveFormat.AverageBytesPerSecond / 2)
                                _buffer.ClearBuffer();
                            var take = Math.Min(count, _buffer.BufferLength);
                            take -= take % blockAlign;
                            if (take > 0) _buffer.AddSamples(_packet, count - take, take);
                        }
                        if (count > 0) DataReceived?.Invoke();
                    }
                    finally { reader.ReleaseBuffer(frames); }
                }
            }
        }
        catch (Exception exception) { if (!_stopping) failure = exception; }
        finally
        {
            // Unplugging a USB headset can also invalidate Stop itself.
            try { _client.Stop(); } catch (Exception exception) { if (!_stopping) failure ??= exception; }
            try { Stopped?.Invoke(failure); } catch { }
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_bufferGate) return _provider.Read(buffer, offset, count);
    }

    public void Stop()
    {
        _stopping = true;
        try { _ready.Set(); } catch (ObjectDisposedException) { }
    }

    public void FinishCapture()
    {
        Stop();
        _captureTask?.GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { FinishCapture(); }
        finally { _client.Dispose(); _device?.Dispose(); _ready.Dispose(); }
    }
}
