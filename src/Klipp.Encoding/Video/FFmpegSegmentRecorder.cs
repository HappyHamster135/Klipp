using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Klipp.Core.Abstractions;
using Klipp.Core.Models;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Encoding.Video;

/// <summary>
/// Kontinuerlig segment-inspelare för "spara senaste N sekunder"-funktionen.
/// </summary>
/// <remarks>
/// Skillnad mot <see cref="FFmpegH264Encoder"/>:
/// - Producerar många små MP4-segment istället för en enda fil
/// - FFmpeg roterar segmenten automatiskt (ringbuffer på disk)
/// - "Klipp" extraheras genom att concatena senaste N segment via <see cref="Clipping.ClipExtractor"/>
///
/// Detta är samma pattern som OBS:s Replay Buffer, NVIDIA ShadowPlay och AMD ReLive använder.
/// </remarks>
public sealed class FFmpegSegmentRecorder : IVideoEncoder
{
    private readonly FFmpegLocator _locator;
    private string? _segmentDirectory;
    private int _segmentDurationSeconds = 10;
    private int _maxSegments = 60; // Default: 10 min buffer (60 * 10s)

    private string? _ffmpegPath;
    private RecordingSettings? _settings;

    private Process? _ffmpegProcess;
    private Stream? _stdin;
    private int _inputWidth;
    private int _inputHeight;

    // Frame pacing
    private readonly Lock _frameLock = new();
    private ReadOnlyMemory<byte> _latestFrameData;
    private CancellationTokenSource? _pacingCts;
    private Task? _pacingTask;

    private bool _initialized;
    private bool _ffmpegStarted;
    private bool _disposed;

    public FFmpegSegmentRecorder(FFmpegLocator? locator = null)
    {
        _locator = locator ?? new FFmpegLocator();
    }

    /// <summary>
    /// Mappen där segment-filerna sparas. Skapas om den inte finns.
    /// Filerna får namn enligt mönstret seg_000.mp4, seg_001.mp4, ...
    /// </summary>
    public void SetSegmentDirectory(string directory)
    {
        _segmentDirectory = directory;
        Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// Hur lång varje segment ska vara. Default: 10 sekunder.
    /// </summary>
    public int SegmentDurationSeconds
    {
        get => _segmentDurationSeconds;
        set
        {
            if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), "Måste vara minst 1 sekund.");
            _segmentDurationSeconds = value;
        }
    }

    /// <summary>
    /// Max antal segment som behålls samtidigt. FFmpeg roterar äldre filer.
    /// Default: 60 (= 10 minuters buffer med 10s segment).
    /// </summary>
    public int MaxSegments
    {
        get => _maxSegments;
        set
        {
            if (value < 2) throw new ArgumentOutOfRangeException(nameof(value), "Måste vara minst 2.");
            _maxSegments = value;
        }
    }

    public async Task InitializeAsync(RecordingSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) throw new InvalidOperationException("Recorder är redan initialiserad.");
        if (string.IsNullOrWhiteSpace(_segmentDirectory))
            throw new InvalidOperationException("Anropa SetSegmentDirectory() innan InitializeAsync().");

        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _settings = settings;

        _ffmpegPath = await _locator.GetFFmpegPathAsync(progress: null, cancellationToken)
            .ConfigureAwait(false);

        _initialized = true;
    }

    private void StartFFmpeg(int width, int height)
    {
        _inputWidth = width;
        _inputHeight = height;

        // Städa gamla segment innan vi börjar
        CleanupOldSegments();

        var args = BuildFFmpegArgs(_settings!, _segmentDirectory!, width, height);

        var psi = new ProcessStartInfo
        {
            FileName = _ffmpegPath!,
            Arguments = args,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _ffmpegProcess = Process.Start(psi)
            ?? throw new InvalidOperationException("Kunde inte starta ffmpeg.exe");

        _stdin = _ffmpegProcess.StandardInput.BaseStream;

        // Konsumera stderr så pipen inte fylls
        _ = Task.Run(async () =>
        {
            try
            {
                var stderr = _ffmpegProcess.StandardError;
                while (await stderr.ReadLineAsync().ConfigureAwait(false) is not null)
                {
                    // Ignorera output
                }
            }
            catch { /* processen kan vara död */ }
        });

        // Frame pacing
        _pacingCts = new CancellationTokenSource();
        _pacingTask = Task.Run(() => PacingLoopAsync(_pacingCts.Token));

        _ffmpegStarted = true;
    }

    /// <summary>
    /// Bygger FFmpeg-argument för segment-mode.
    /// Nyckelflaggor:
    ///   -f segment              skriv som flera filer
    ///   -segment_time N         varje segment är N sekunder
    ///   -segment_wrap N         max N filer, sen wrap (seg_000 skrivs över av seg_N+1)
    ///   -reset_timestamps 1     varje segment har egen tidsbas (krävs för korrekt concat)
    /// </summary>
    private string BuildFFmpegArgs(RecordingSettings settings, string segmentDir, int width, int height)
    {
        var pattern = Path.Combine(segmentDir, "seg_%03d.mp4");

        return $"-f rawvideo -pix_fmt bgra " +
               $"-s {width}x{height} -r {settings.FrameRate} " +
               $"-i - " +
               $"-c:v libx264 -preset veryfast " +
               $"-b:v {settings.VideoBitrate / 1000}k " +
               $"-pix_fmt yuv420p " +
               $"-g {settings.FrameRate} " +              // keyframe varje sekund (= varje GOP)
               $"-f segment " +
               $"-segment_time {_segmentDurationSeconds} " +
               $"-segment_wrap {_maxSegments} " +
               $"-reset_timestamps 1 " +
               $"-y " +
               $"\"{pattern}\"";
    }

    private void CleanupOldSegments()
    {
        try
        {
            if (string.IsNullOrEmpty(_segmentDirectory) || !Directory.Exists(_segmentDirectory))
                return;

            foreach (var file in Directory.GetFiles(_segmentDirectory, "seg_*.mp4"))
            {
                try { File.Delete(file); } catch { /* ignorera */ }
            }
        }
        catch { /* ignorera */ }
    }

    private async Task PacingLoopAsync(CancellationToken cancellationToken)
    {
        var targetFps = _settings!.FrameRate;
        var ticksPerStopwatchSecond = Stopwatch.Frequency;
        var frameIntervalStopwatch = ticksPerStopwatchSecond / targetFps;
        var nextTick = Stopwatch.GetTimestamp();

        while (!cancellationToken.IsCancellationRequested)
        {
            ReadOnlyMemory<byte> frameData;
            lock (_frameLock)
            {
                frameData = _latestFrameData;
            }

            if (!frameData.IsEmpty && _stdin is not null)
            {
                try
                {
                    await _stdin.WriteAsync(frameData, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; }
            }

            nextTick += frameIntervalStopwatch;
            var now = Stopwatch.GetTimestamp();
            var ticksToWait = nextTick - now;

            if (ticksToWait > 0)
            {
                var msToWait = (int)(ticksToWait * 1000.0 / ticksPerStopwatchSecond);
                if (msToWait > 0)
                {
                    try
                    {
                        await Task.Delay(msToWait, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                }
            }
            else
            {
                nextTick = now;
            }
        }
    }

    public IAsyncEnumerable<EncodedSample> EncodeAsync(
        VideoFrame frame,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException("Anropa InitializeAsync först.");

        if (!_ffmpegStarted)
        {
            StartFFmpeg(frame.Width, frame.Height);
        }
        else if (frame.Width != _inputWidth || frame.Height != _inputHeight)
        {
            return EmptyAsync();
        }

        lock (_frameLock)
        {
            _latestFrameData = frame.PixelData;
        }

        return EmptyAsync();
    }

    public async IAsyncEnumerable<EncodedSample> FlushAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_pacingCts is not null)
        {
            _pacingCts.Cancel();
            if (_pacingTask is not null)
            {
                try { await _pacingTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* förväntat */ }
            }
        }

        if (_stdin is not null)
        {
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
            _stdin.Close();
            _stdin = null;
        }

        if (_ffmpegProcess is not null && !_ffmpegProcess.HasExited)
        {
            await _ffmpegProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        yield break;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _pacingCts?.Cancel();
            if (_pacingTask is not null)
            {
                try { await _pacingTask.ConfigureAwait(false); }
                catch { /* ignorera vid dispose */ }
            }
            _pacingCts?.Dispose();
        }
        catch { }

        try
        {
            if (_stdin is not null)
            {
                _stdin.Close();
                _stdin = null;
            }

            if (_ffmpegProcess is not null && !_ffmpegProcess.HasExited)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await _ffmpegProcess.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { _ffmpegProcess.Kill(); } catch { }
                }
            }
        }
        finally
        {
            _ffmpegProcess?.Dispose();
            _ffmpegProcess = null;
        }
    }

    private static async IAsyncEnumerable<EncodedSample> EmptyAsync()
    {
        await Task.CompletedTask;
        yield break;
    }
}
