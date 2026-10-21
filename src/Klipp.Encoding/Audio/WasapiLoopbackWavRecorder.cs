using System.Diagnostics;
using Klipp.Encoding.Muxing;

namespace Klipp.Encoding.Audio;

/// <summary>
/// Spelar in systemljud via WASAPI loopback till en WAV-fil och mäter när första
/// ljudsamplen faktiskt började i samma Stopwatch-tidsbas som video-pipelinen.
/// </summary>
public sealed class WasapiLoopbackWavRecorder : IDisposable
{
    private readonly object _lock = new();

    private MixedAudioCapture? _capture;
    private Rf64WaveFileWriter? _writer;
    private string? _outputPath;
    private long? _firstAudioStartTimestamp;
    private long _bytesWritten;
    private bool _isRecording;
    private bool _disposed;

    public string? OutputPath => _outputPath;
    public long? FirstAudioStartTimestamp => _firstAudioStartTimestamp;
    public long BytesWritten => Interlocked.Read(ref _bytesWritten);
    public bool HasAudio => FirstAudioStartTimestamp.HasValue && BytesWritten > 0;
    public bool IsRecording => _isRecording;
    public bool HasRecentData => _capture?.HasRecentData == true;
    public string? FailureMessage => _capture?.FailureMessage;
    public IReadOnlyList<CaptureAudioDeviceStatus> DeviceStatuses => _capture?.DeviceStatuses ?? [];
    public IReadOnlyList<CaptureAudioLevel> AudioLevels => _capture?.AudioLevels ?? [];
    public event EventHandler<CaptureAudioDeviceStatus>? DeviceStatusChanged;
    public int AudioChannels { get; private set; } = 2;
    public string AudioTrackName { get; private set; } = CaptureAudioLayout.SystemName;
    public IReadOnlyList<string> AudioTrackNames { get; private set; } = [];

    /// <summary>
    /// Startar inspelning till given WAV-sökväg. Filen skrivs färdigt när Stop anropas.
    /// </summary>
    public void Start(string outputPath, CaptureAudioOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (_isRecording) throw new InvalidOperationException("Audio recording is already running.");

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        options ??= new CaptureAudioOptions();
        AudioChannels = options.OutputChannels;
        AudioTrackName = options.AudioTrackName;
        AudioTrackNames = options.TrackNames;
        _capture = new MixedAudioCapture();
        _capture.DeviceStatusChanged += OnDeviceStatusChanged;
        _writer = new Rf64WaveFileWriter(outputPath, 48000, AudioChannels);
        _outputPath = outputPath;
        _firstAudioStartTimestamp = null;
        Interlocked.Exchange(ref _bytesWritten, 0);

        _isRecording = true;
        try { _capture.Start(options, WriteSamplesAsync); }
        catch
        {
            _isRecording = false;
            _capture.Dispose();
            _capture = null;
            FinishWriter();
            throw;
        }
    }

    private void OnDeviceStatusChanged(object? sender, CaptureAudioDeviceStatus status) =>
        DeviceStatusChanged?.Invoke(this, status);

    public void Stop()
    {
        if (!_isRecording) return;

        _capture?.StopAsync(drainTail: true).GetAwaiter().GetResult();
        FinishWriter();
        _isRecording = false;
    }

    private Task WriteSamplesAsync(byte[] buffer, int count, CancellationToken cancellationToken)
    {
        if (count <= 0 || _capture is null) return Task.CompletedTask;
        _firstAudioStartTimestamp ??= _capture.FirstSampleTimestamp ?? Stopwatch.GetTimestamp();

        lock (_lock)
        {
            if (_writer is null) return Task.CompletedTask;
            _writer.Write(buffer, 0, count);
            Interlocked.Add(ref _bytesWritten, count);
        }
        return Task.CompletedTask;
    }

    private void FinishWriter()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { Stop(); } catch { }
        _capture?.Dispose();
        _capture = null;
        FinishWriter();
    }
}
