using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Capture.Models;
using Klipp.Capture.Video;
using Klipp.Core.Models;
using Klipp.Encoding.Muxing;
using Klipp.Encoding.Video;
using Klipp.Storage.RingBuffer;

namespace Klipp.Desktop.Services;

/// <summary>
/// Hanterar live-inspelning. Äger pipelinen (capture, encoder, ring buffer) och
/// exponerar en enkel API: <see cref="StartAsync"/>, <see cref="StopAsync"/>,
/// <see cref="SaveLastSecondsAsync"/>.
/// </summary>
/// <remarks>
/// För närvarande spelar tjänsten in primary monitor som default. När vi senare
/// lägger till fönster-/spel-detektering kan capture target göras dynamisk.
/// </remarks>
public sealed class RecordingService : IAsyncDisposable
{
    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    private readonly object _lock = new();
    private WgcCaptureSource? _captureSource;
    private RawSampleEncoder? _encoder;
    private RingBufferClipBuffer? _buffer;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private bool _isRecording;
    private bool _disposed;

    /// <summary>True om inspelning är aktiv.</summary>
    public bool IsRecording
    {
        get { lock (_lock) return _isRecording; }
    }

    /// <summary>Antal sekunder som finns i bufferten just nu (0 om ej recording).</summary>
    public double BufferedSeconds => _buffer?.BufferedSeconds ?? 0;

    /// <summary>
    /// Startar inspelning av primary monitor. Frames samlas kontinuerligt i ring-bufferten
    /// tills <see cref="StopAsync"/> eller <see cref="SaveLastSecondsAsync"/> anropas.
    /// </summary>
    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (_isRecording) throw new InvalidOperationException("Inspelning körs redan.");
            _isRecording = true;
        }

        try
        {
            // Hitta primary monitor
            var hMonitor = MonitorFromPoint(new POINT { x = 0, y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            var target = new CaptureTarget
            {
                Kind = CaptureTargetKind.Monitor,
                Handle = hMonitor,
                DisplayName = "Primary Monitor"
            };

            // Settings — 30 FPS för rimlig disk-användning
            var settings = RecordingSettings.Default1080p60 with { FrameRate = 30, RingBufferSeconds = 30 };

            _encoder = new RawSampleEncoder();
            await _encoder.InitializeAsync(settings);

            _buffer = new RingBufferClipBuffer(() => new RawFileWriter(), settings);
            _captureSource = new WgcCaptureSource(target);
            _captureCts = new CancellationTokenSource();

            await _captureSource.StartAsync();

            // Kör capture-loopen i bakgrunden
            _captureTask = Task.Run(() => CaptureLoopAsync(_captureCts.Token));
        }
        catch
        {
            lock (_lock) _isRecording = false;
            await CleanupAsync();
            throw;
        }
    }

    /// <summary>
    /// Stoppar inspelning och frigör resurser.
    /// </summary>
    public async Task StopAsync()
    {
        bool wasRecording;
        lock (_lock)
        {
            wasRecording = _isRecording;
            _isRecording = false;
        }

        if (!wasRecording) return;

        _captureCts?.Cancel();

        if (_captureTask is not null)
        {
            try { await _captureTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* förväntat */ }
            catch { /* ignorera fel vid stop */ }
        }

        await CleanupAsync();
    }

    /// <summary>
    /// Sparar de senaste N sekunderna till en fil. Returnerar faktisk sparad varaktighet.
    /// </summary>
    /// <param name="seconds">Antal sekunder att spara (begränsat av buffert-storlek).</param>
    /// <param name="outputPath">Sökväg att skriva till.</param>
    public async Task<double> SaveLastSecondsAsync(int seconds, string outputPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsRecording || _buffer is null)
            throw new InvalidOperationException("Ingen aktiv inspelning att spara från.");

        // Säkerställ att output-mappen finns
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        return await _buffer.FlushLastSecondsAsync(seconds, outputPath);
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        if (_captureSource is null || _encoder is null || _buffer is null) return;

        try
        {
            await foreach (var frame in _captureSource.ReadSamplesAsync(cancellationToken))
            {
                await foreach (var sample in _encoder.EncodeAsync(frame, cancellationToken))
                {
                    await _buffer.AppendAsync(sample, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Förväntat när StopAsync anropas
        }
    }

    private async Task CleanupAsync()
    {
        if (_captureSource is not null)
        {
            try { await _captureSource.StopAsync(); } catch { }
            await _captureSource.DisposeAsync();
            _captureSource = null;
        }

        if (_buffer is not null)
        {
            await _buffer.DisposeAsync();
            _buffer = null;
        }

        if (_encoder is not null)
        {
            await _encoder.DisposeAsync();
            _encoder = null;
        }

        _captureCts?.Dispose();
        _captureCts = null;
        _captureTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
    }
}
