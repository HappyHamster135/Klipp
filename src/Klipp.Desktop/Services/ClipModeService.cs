using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Capture.Models;
using Klipp.Capture.Video;
using Klipp.Core.Models;
using Klipp.Encoding.Clipping;
using Klipp.Encoding.FFmpeg;
using Klipp.Encoding.Video;

namespace Klipp.Desktop.Services;

/// <summary>
/// Hanterar "Klipp-läge" — kontinuerlig bakgrundsövervakning med en ring buffer på disk.
/// När användaren trycker "Spara klipp" extraheras de senaste N sekunderna utan att
/// övervakningen stoppas.
/// </summary>
/// <remarks>
/// Detta är kusinen till <see cref="RecordingService"/>. Skillnaden:
/// - RecordingService: start/stop = en MP4 (vanlig skärminspelning)
/// - ClipModeService: kontinuerlig segment-buffer + extract på begäran (Medal-stil)
///
/// Segment-filerna ligger i %LocalAppData%\Klipp\segments\ och roteras av FFmpeg.
/// </remarks>
public sealed class ClipModeService : IAsyncDisposable
{
    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(POINT pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    private const uint MONITOR_DEFAULTTOPRIMARY = 0x00000001;

    // 5s-segment ger bra granularitet. 36 segment = 3 minuters buffer (täcker
    // den längsta klipplängden 2 min med god marginal).
    private const int SegmentDurationSeconds = 2;
    private const int MaxSegments = 90; // 2s * 90 = 3 min buffer

    private readonly FFmpegLocator _ffmpegLocator = new();
    private readonly ClipExtractor _extractor;
    private readonly string _segmentDirectory;
    private readonly object _lock = new();

    private WgcCaptureSource? _captureSource;
    private FFmpegSegmentRecorder? _recorder;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private DateTime _monitorStartTime;
    private bool _isMonitoring;
    private bool _disposed;

    public ClipModeService()
    {
        _extractor = new ClipExtractor(_ffmpegLocator);

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _segmentDirectory = Path.Combine(localAppData, "Klipp", "segments");
        Directory.CreateDirectory(_segmentDirectory);
    }

    /// <summary>True om bakgrundsövervakning är aktiv.</summary>
    public bool IsMonitoring
    {
        get { lock (_lock) return _isMonitoring; }
    }

    /// <summary>Hur länge övervakningen har körts (sekunder).</summary>
    public double MonitoredSeconds
    {
        get
        {
            lock (_lock)
            {
                if (!_isMonitoring) return 0;
                return (DateTime.UtcNow - _monitorStartTime).TotalSeconds;
            }
        }
    }

    /// <summary>True om FFmpeg-binären är installerad.</summary>
    public bool IsFFmpegInstalled => _ffmpegLocator.IsInstalled;

    /// <summary>Säkerställer att FFmpeg är installerat (laddar ner vid behov).</summary>
    public async Task EnsureFFmpegInstalledAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _ffmpegLocator.GetFFmpegPathAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Startar bakgrundsövervakning. Segment börjar skrivas till ring buffern.
    /// </summary>
    public async Task StartMonitoringAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (_isMonitoring) throw new InvalidOperationException("Övervakning körs redan.");
        }

        try
        {
            var hMonitor = MonitorFromPoint(new POINT { x = 0, y = 0 }, MONITOR_DEFAULTTOPRIMARY);
            var target = new CaptureTarget
            {
                Kind = CaptureTargetKind.Monitor,
                Handle = hMonitor,
                DisplayName = "Primary Monitor"
            };

            var settings = RecordingSettings.Default1080p60 with { FrameRate = 30 };

            _recorder = new FFmpegSegmentRecorder(_ffmpegLocator)
            {
                SegmentDurationSeconds = SegmentDurationSeconds,
                MaxSegments = MaxSegments
            };
            _recorder.SetSegmentDirectory(_segmentDirectory);
            await _recorder.InitializeAsync(settings);

            _captureSource = new WgcCaptureSource(target);
            _captureCts = new CancellationTokenSource();
            await _captureSource.StartAsync();

            lock (_lock)
            {
                _monitorStartTime = DateTime.UtcNow;
                _isMonitoring = true;
            }

            _captureTask = Task.Run(() => CaptureLoopAsync(_captureCts.Token));
        }
        catch
        {
            lock (_lock) _isMonitoring = false;
            await CleanupAsync();
            throw;
        }
    }

    /// <summary>
    /// Sparar de senaste <paramref name="seconds"/> sekunderna till en MP4 — utan att
    /// stoppa övervakningen. Returnerar resultatet av extraktionen.
    /// </summary>
    public async Task<ClipExtractionResult> SaveClipAsync(string outputPath, int seconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (!_isMonitoring)
                throw new InvalidOperationException("Ingen övervakning aktiv — starta först.");
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        return await _extractor.SaveLastSecondsAsync(
            segmentDirectory: _segmentDirectory,
            outputPath: outputPath,
            secondsToCapture: seconds,
            segmentDurationSeconds: SegmentDurationSeconds);
    }

    /// <summary>Stoppar bakgrundsövervakningen och städar resurser.</summary>
    public async Task StopMonitoringAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (!_isMonitoring) return;
            _isMonitoring = false;
        }

        _captureCts?.Cancel();

        if (_captureTask is not null)
        {
            try { await _captureTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* förväntat */ }
            catch { /* ignorera */ }
        }

        if (_recorder is not null)
        {
            try
            {
                await foreach (var _ in _recorder.FlushAsync())
                {
                    // FFmpeg avslutar sista segmentet
                }
            }
            catch { /* ignorera */ }
        }

        await CleanupAsync();
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        if (_captureSource is null || _recorder is null) return;

        try
        {
            await foreach (var frame in _captureSource.ReadSamplesAsync(cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested) break;

                await foreach (var _ in _recorder.EncodeAsync(frame, cancellationToken))
                {
                    // FFmpeg skriver segment direkt
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Förväntat vid stop — token avbröts
        }
        catch (Exception ex)
        {
            // Oväntat fel i capture-loopen — logga men krascha inte appen
            System.Diagnostics.Debug.WriteLine($"[Klipp] CaptureLoop fel: {ex.Message}");
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

        if (_recorder is not null)
        {
            await _recorder.DisposeAsync();
            _recorder = null;
        }

        _captureCts?.Dispose();
        _captureCts = null;
        _captureTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopMonitoringAsync();
    }
}
