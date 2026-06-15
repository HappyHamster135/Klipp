using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Capture.Models;
using Klipp.Capture.Video;
using Klipp.Core.Models;
using Klipp.Encoding.FFmpeg;
using Klipp.Encoding.Video;

namespace Klipp.Desktop.Services;

/// <summary>
/// Hanterar live-inspelning till en MP4-fil. Äger pipelinen
/// (capture, FFmpeg encoder) och exponerar ett enkelt API.
/// </summary>
/// <remarks>
/// Omgång 1 av FFmpeg-integration: start/stop = klipp. När användaren trycker
/// "Spela in" startar vi WGC + FFmpeg och pipear frames. När de trycker "Spara klipp"
/// stoppar vi allt och får en MP4-fil från start till stopp.
///
/// Omgång 2 (framtida): segmenterad MP4 + ring buffer för "spara senaste N sekunder".
/// </remarks>
public sealed class RecordingService : IAsyncDisposable
{
    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    private readonly FFmpegLocator _ffmpegLocator = new();
    private readonly object _lock = new();

    private WgcCaptureSource? _captureSource;
    private FFmpegH264Encoder? _encoder;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private string? _activeOutputPath;
    private DateTime _recordingStartTime;
    private bool _isRecording;
    private bool _disposed;

    /// <summary>True om inspelning är aktiv.</summary>
    public bool IsRecording
    {
        get { lock (_lock) return _isRecording; }
    }

    /// <summary>Hur länge nuvarande inspelning har körts (sekunder).</summary>
    public double RecordedSeconds
    {
        get
        {
            lock (_lock)
            {
                if (!_isRecording) return 0;
                return (DateTime.UtcNow - _recordingStartTime).TotalSeconds;
            }
        }
    }

    /// <summary>True om FFmpeg-binären är installerad och redo.</summary>
    public bool IsFFmpegInstalled => _ffmpegLocator.IsInstalled;

    /// <summary>
    /// Säkerställer att FFmpeg är installerat. Laddar ner om saknas (~50 MB).
    /// </summary>
    public async Task EnsureFFmpegInstalledAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _ffmpegLocator.GetFFmpegPathAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Startar inspelning till given fil. Frames pipeas live till FFmpeg.
    /// </summary>
    public async Task StartAsync(string outputPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        lock (_lock)
        {
            if (_isRecording) throw new InvalidOperationException("Inspelning körs redan.");
        }

        try
        {
            // Säkerställ att output-mappen finns
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Hitta primary monitor
            var hMonitor = MonitorFromPoint(new POINT { x = 0, y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            var target = new CaptureTarget
            {
                Kind = CaptureTargetKind.Monitor,
                Handle = hMonitor,
                DisplayName = "Primary Monitor"
            };

            var settings = RecordingSettings.Default1080p60 with { FrameRate = 30 };

            // Initialisera FFmpeg-encodern
            _encoder = new FFmpegH264Encoder(_ffmpegLocator);
            _encoder.SetOutputPath(outputPath);
            await _encoder.InitializeAsync(settings);

            // Starta capture
            _captureSource = new WgcCaptureSource(target);
            _captureCts = new CancellationTokenSource();
            await _captureSource.StartAsync();

            lock (_lock)
            {
                _activeOutputPath = outputPath;
                _recordingStartTime = DateTime.UtcNow;
                _isRecording = true;
            }

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
    /// Stoppar inspelning och returnerar sökvägen till den producerade MP4-filen.
    /// Returnerar null om ingen inspelning körde.
    /// </summary>
    public async Task<string?> StopAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? outputPath;
        lock (_lock)
        {
            if (!_isRecording) return null;
            _isRecording = false;
            outputPath = _activeOutputPath;
        }

        _captureCts?.Cancel();

        // Vänta på att capture-loopen avslutas
        if (_captureTask is not null)
        {
            try { await _captureTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* förväntat */ }
            catch { /* ignorera fel vid stop */ }
        }

        // Flusha encodern — väntar på att FFmpeg muxar färdigt MP4-filen
        if (_encoder is not null)
        {
            try
            {
                await foreach (var _ in _encoder.FlushAsync())
                {
                    // FFmpeg muxar färdigt
                }
            }
            catch { /* ignorera fel vid flush */ }
        }

        await CleanupAsync();

        return outputPath;
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        if (_captureSource is null || _encoder is null) return;

        try
        {
            await foreach (var frame in _captureSource.ReadSamplesAsync(cancellationToken))
            {
                await foreach (var _ in _encoder.EncodeAsync(frame, cancellationToken))
                {
                    // FFmpeg muxar direkt — inga samples att samla
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

        if (_encoder is not null)
        {
            await _encoder.DisposeAsync();
            _encoder = null;
        }

        _captureCts?.Dispose();
        _captureCts = null;
        _captureTask = null;
        _activeOutputPath = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
    }
}
