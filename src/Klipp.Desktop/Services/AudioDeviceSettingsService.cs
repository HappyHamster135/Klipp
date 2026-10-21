using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Klipp.Desktop.Services;

internal sealed record AudioDeviceChoice(string Id, string DisplayName, bool IsAvailable = true)
{
    public override string ToString() => DisplayName;
}

internal sealed record AudioDeviceInventory(
    IReadOnlyList<AudioDeviceChoice> Outputs, IReadOnlyList<AudioDeviceChoice> Inputs,
    string DefaultOutputName, string DefaultInputName);

/// <summary>App-local device selection and short, explicitly started audio tests.</summary>
internal sealed class AudioDeviceSettingsService : IDisposable
{
    private readonly object _gate = new();
    private WasapiCapture? _capture;
    private MMDevice? _captureDevice;
    private WasapiOut? _output;
    private MMDevice? _outputDevice;
    private string? _testError;
    private float _peak;
    private long _lastAudioTimestamp;
    private string? _toneError;
    private bool _disposed;
    private int _toneVersion;

    public float Peak => (Stopwatch.GetTimestamp() - Interlocked.Read(ref _lastAudioTimestamp)) /
        (double)Stopwatch.Frequency <= .25 ? Volatile.Read(ref _peak) : 0;
    public string? TestError => Volatile.Read(ref _testError);

    public static AudioDeviceInventory GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        static string DefaultName(MMDeviceEnumerator source, DataFlow flow, Role role)
        {
            try { using var device = source.GetDefaultAudioEndpoint(flow, role); return device.FriendlyName; }
            catch { return string.Empty; }
        }
        static AudioDeviceChoice[] Enumerate(MMDeviceEnumerator source, DataFlow flow)
        {
            var choices = new List<AudioDeviceChoice>();
            foreach (var device in source.EnumerateAudioEndPoints(flow, DeviceState.Active))
                using (device) choices.Add(new AudioDeviceChoice(device.ID, device.FriendlyName));
            return choices.OrderBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        return new AudioDeviceInventory(Enumerate(enumerator, DataFlow.Render), Enumerate(enumerator, DataFlow.Capture),
            DefaultName(enumerator, DataFlow.Render, Role.Console), DefaultName(enumerator, DataFlow.Capture, Role.Communications));
    }

    public void StartLevelTest(bool microphone, string deviceId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            StopLevelTestCore();
            Volatile.Write(ref _testError, null);
            try
            {
                _captureDevice = OpenDevice(deviceId, microphone ? DataFlow.Capture : DataFlow.Render);
                _capture = microphone ? new WasapiCapture(_captureDevice) : new WasapiLoopbackCapture(_captureDevice);
                cancellationToken.ThrowIfCancellationRequested();
                _capture.DataAvailable += OnAudioData;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();
            }
            catch { StopLevelTestCore(); throw; }
        }
    }

    public async Task PlayTestToneAsync(string deviceId, CancellationToken cancellationToken)
    {
        int version;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopToneCore();
            version = _toneVersion;
            Volatile.Write(ref _toneError, null);
            try
            {
                _outputDevice = OpenDevice(deviceId, DataFlow.Render);
                _output = new WasapiOut(_outputDevice, AudioClientShareMode.Shared, true, 60);
                _output.PlaybackStopped += OnPlaybackStopped;
                _output.Init(new TestToneProvider());
                _output.Play();
            }
            catch { StopToneCore(); throw; }
        }
        try
        {
            await Task.Delay(1100, cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref _toneError) is { } error) throw new InvalidOperationException(error);
        }
        finally
        {
            lock (_gate)
                if (version == _toneVersion) StopToneCore();
        }
    }

    public void StopLevelTest()
    {
        lock (_gate) StopLevelTestCore();
    }

    public void Stop()
    {
        lock (_gate) { StopLevelTestCore(); StopToneCore(); }
    }

    private static MMDevice OpenDevice(string? id, DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var device = string.IsNullOrWhiteSpace(id)
            ? enumerator.GetDefaultAudioEndpoint(flow, flow == DataFlow.Capture ? Role.Communications : Role.Console)
            : enumerator.GetDevice(id);
        if (device.State != DeviceState.Active || device.DataFlow != flow)
        {
            device.Dispose();
            throw new InvalidOperationException("The selected audio device is disconnected. Choose a connected device and try again.");
        }
        return device;
    }

    private void OnAudioData(object? sender, WaveInEventArgs args)
    {
        if (sender is not WasapiCapture capture || !ReferenceEquals(sender, _capture)) return;
        var format = capture.WaveFormat;
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            format is WaveFormatExtensible extensible && extensible.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71");
        var bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample is < 1 or > 4) return;
        var data = args.Buffer.AsSpan(0, args.BytesRecorded);
        var peak = 0f;
        for (var offset = 0; offset + bytesPerSample <= data.Length; offset += bytesPerSample)
        {
            var sample = bytesPerSample switch
            {
                4 when isFloat => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data[offset..])),
                4 => BinaryPrimitives.ReadInt32LittleEndian(data[offset..]) / 2147483648f,
                3 => ((data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16) << 8 >> 8) / 8388608f,
                2 => BinaryPrimitives.ReadInt16LittleEndian(data[offset..]) / 32768f,
                _ => (data[offset] - 128) / 128f
            };
            if (float.IsFinite(sample)) peak = Math.Max(peak, Math.Abs(sample));
        }
        Volatile.Write(ref _peak, Math.Clamp(peak, 0, 1));
        Interlocked.Exchange(ref _lastAudioTimestamp, Stopwatch.GetTimestamp());
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null) Volatile.Write(ref _testError, args.Exception.Message);
        Volatile.Write(ref _peak, 0);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null) Volatile.Write(ref _toneError, args.Exception.Message);
    }

    private void StopLevelTestCore()
    {
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            capture.DataAvailable -= OnAudioData;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); } catch { }
            capture.Dispose();
        }
        _captureDevice?.Dispose();
        _captureDevice = null;
        Volatile.Write(ref _peak, 0);
    }

    private void StopToneCore()
    {
        _toneVersion++;
        var output = _output;
        _output = null;
        if (output is not null)
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            try { output.Stop(); } catch { }
            output.Dispose();
        }
        _outputDevice?.Dispose();
        _outputDevice = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopLevelTestCore(); StopToneCore();
        }
    }

    private sealed class TestToneProvider : IWaveProvider
    {
        private int _position;
        public WaveFormat WaveFormat { get; } = new(48000, 16, 2);
        public int Read(byte[] buffer, int offset, int count)
        {
            var frames = Math.Min(count / 4, 48000 - _position);
            for (var index = 0; index < frames; index++)
            {
                var time = _position / 48000d;
                var envelope = Math.Min(1, Math.Min(time / .02, (1 - time) / .10));
                var frequency = time < .45 ? 440d : 660d;
                var sample = (short)(Math.Sin(2 * Math.PI * frequency * time) * .12 * Math.Max(0, envelope) * short.MaxValue);
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset + index * 4, 2), sample);
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset + index * 4 + 2, 2), sample);
                _position++;
            }
            return frames * 4;
        }
    }
}
