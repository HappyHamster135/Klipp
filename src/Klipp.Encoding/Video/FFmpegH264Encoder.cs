using System.Diagnostics;
using System.Runtime.CompilerServices;
using Klipp.Core.Abstractions;
using Klipp.Core.Models;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Encoding.Video;

/// <summary>
/// H.264 encoder via FFmpeg. Pipear raw BGRA-frames till FFmpegs stdin och
/// producerar en video-only MP4-fil. Ljud muxas separat efter inspelningen.
/// </summary>
public sealed class FFmpegH264Encoder : IVideoEncoder
{
    private readonly FFmpegLocator _locator;
    private string? _outputPath;
    private string? _ffmpegPath;
    private RecordingSettings? _settings;
    private CaptureVideoEncoderSelection? _encoderSelection;

    private Process? _ffmpegProcess;
    private Stream? _stdin;
    private int _inputWidth;
    private int _inputHeight;

    // Pacing: håll senaste framen + en timer som pipear den i constant rate.
    private readonly Lock _frameLock = new();
    private ReadOnlyMemory<byte> _latestFrameData;
    private CancellationTokenSource? _pacingCts;
    private Task? _pacingTask;
    private long _framesWritten;
    private long _pacingStartTimestamp;
    private long _stopTargetFrames = -1;
    private volatile bool _stopRequested;

    private bool _initialized;
    private bool _ffmpegStarted;
    private bool _disposed;

    public FFmpegH264Encoder(FFmpegLocator? locator = null)
    {
        _locator = locator ?? new FFmpegLocator();
    }

    public long FramesWritten => Interlocked.Read(ref _framesWritten);
    public long PacingStartTimestamp => Interlocked.Read(ref _pacingStartTimestamp);
    public bool IsRunning
    {
        get
        {
            try { return _ffmpegStarted && _ffmpegProcess is { HasExited: false }; }
            catch { return false; }
        }
    }
    public int InputWidth => _inputWidth;
    public int InputHeight => _inputHeight;
    public string EncoderDisplayName => _encoderSelection?.DisplayName ?? "Preparing encoder";
    public string? EncoderFallbackReason => _encoderSelection?.FallbackReason;
    public bool IsHardwareEncoder => _encoderSelection?.IsHardware == true;

    /// <summary>Sätter MP4-output-sökväg. Måste anropas innan InitializeAsync().</summary>
    public void SetOutputPath(string path) => _outputPath = path;

    public async Task InitializeAsync(RecordingSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) throw new InvalidOperationException("The encoder is already initialized.");
        if (string.IsNullOrWhiteSpace(_outputPath))
            throw new InvalidOperationException("Call SetOutputPath() before InitializeAsync().");

        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        _settings = settings;

        _ffmpegPath = await _locator.GetFFmpegPathAsync(progress: null, cancellationToken)
            .ConfigureAwait(false);
        _encoderSelection = await FFmpegCaptureEncoderSelector.SelectAsync(_ffmpegPath, settings, cancellationToken)
            .ConfigureAwait(false);

        _initialized = true;
    }

    private void StartFFmpeg(int width, int height)
    {
        _inputWidth = width;
        _inputHeight = height;

        var args = BuildFFmpegArgs(_settings!, _encoderSelection!, _outputPath!, width, height);

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
            ?? throw new InvalidOperationException("Could not start ffmpeg.exe.");

        TrySetFFmpegPriority(_ffmpegProcess);
        _stdin = _ffmpegProcess.StandardInput.BaseStream;

        // Konsumera stderr i bakgrunden så processen inte hänger på full pipe.
        _ = Task.Run(async () =>
        {
            try
            {
                var stderr = _ffmpegProcess.StandardError;
                while (await stderr.ReadLineAsync().ConfigureAwait(false) is not null) { }
            }
            catch { }
        });

        _pacingCts = new CancellationTokenSource();
        _pacingTask = Task.Run(() => PacingLoopAsync(_pacingCts.Token));

        _ffmpegStarted = true;
    }

    private async Task PacingLoopAsync(CancellationToken cancellationToken)
    {
        var targetFps = _settings!.FrameRate;
        var ticksPerStopwatchSecond = Stopwatch.Frequency;
        var frameIntervalStopwatch = ticksPerStopwatchSecond / targetFps;

        // Starta klockan först när första riktiga frame finns.
        while (!cancellationToken.IsCancellationRequested)
        {
            bool hasFrame;
            lock (_frameLock) { hasFrame = !_latestFrameData.IsEmpty; }
            if (hasFrame) break;

            try { await Task.Delay(5, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        var startTick = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _pacingStartTimestamp, startTick);
        var nextTick = startTick;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_stopRequested &&
                Interlocked.Read(ref _framesWritten) >= Interlocked.Read(ref _stopTargetFrames))
            {
                break;
            }

            ReadOnlyMemory<byte> frameData;
            lock (_frameLock) { frameData = _latestFrameData; }

            if (!frameData.IsEmpty && _stdin is not null)
            {
                try
                {
                    await _stdin.WriteAsync(frameData, cancellationToken).ConfigureAwait(false);
                    Interlocked.Increment(ref _framesWritten);
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
                    try { await Task.Delay(msToWait, cancellationToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
    }

    private static string BuildFFmpegArgs(RecordingSettings settings, CaptureVideoEncoderSelection selection, string outputPath, int width, int height)
    {
        var keyframeInterval = settings.FrameRate * settings.KeyframeIntervalSeconds;
        var scaleFilter = settings.Width != width || settings.Height != height
            ? $"-vf scale={settings.Width}:{settings.Height}:flags=fast_bilinear "
            : string.Empty;

        return $"-hide_banner -loglevel warning -filter_threads 2 -f rawvideo -pix_fmt bgra " +
               $"-s {width}x{height} -r {settings.FrameRate} -i - " +
               string.Join(" ", FFmpegCaptureEncoderSelector.VideoArguments(settings, selection, keyframeInterval)) + " " +
               scaleFilter +
               // A completed moov is written before media; each one-second
               // fragment remains readable if capture or Windows stops early.
               $"-vsync cfr -movflags +frag_keyframe+empty_moov+default_base_moof " +
               $"-frag_duration 1000000 -flush_packets 1 -n \"{outputPath}\"";
    }

    private static void TrySetFFmpegPriority(Process process)
    {
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch { }
    }

    public IAsyncEnumerable<EncodedSample> EncodeAsync(VideoFrame frame, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException("Call InitializeAsync first.");

        if (!_ffmpegStarted)
        {
            Interlocked.Exchange(ref _framesWritten, 0);
            Interlocked.Exchange(ref _pacingStartTimestamp, 0);
            Interlocked.Exchange(ref _stopTargetFrames, -1);
            _stopRequested = false;
            StartFFmpeg(frame.Width, frame.Height);
        }
        else if (frame.Width != _inputWidth || frame.Height != _inputHeight)
        {
            return EmptyAsync();
        }

        lock (_frameLock) { _latestFrameData = frame.PixelData; }
        return EmptyAsync();
    }

    public async IAsyncEnumerable<EncodedSample> FlushAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (_pacingCts is not null)
        {
            if (_pacingTask is not null)
            {
                RequestPacingStop();
                try
                {
                    await _pacingTask.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _pacingCts.Cancel();
                    try { await _pacingTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }
                catch (OperationCanceledException)
                {
                    _pacingCts.Cancel();
                    throw;
                }
            }
            else
            {
                _pacingCts.Cancel();
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

        if (_ffmpegProcess is { ExitCode: not 0 })
            throw new InvalidOperationException("The video encoder stopped before finalizing. The captured video has been kept for recovery.");

        yield break;
    }

    private void RequestPacingStop()
    {
        var startTimestamp = Interlocked.Read(ref _pacingStartTimestamp);
        if (startTimestamp == 0 || _settings is null)
        {
            _pacingCts?.Cancel();
            return;
        }

        var elapsedSeconds = (Stopwatch.GetTimestamp() - startTimestamp) / (double)Stopwatch.Frequency;
        var targetFrames = Math.Max(
            Interlocked.Read(ref _framesWritten),
            (long)Math.Ceiling(elapsedSeconds * _settings.FrameRate));

        Interlocked.Exchange(ref _stopTargetFrames, targetFrames);
        _stopRequested = true;
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
                catch { }
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
                try { await _ffmpegProcess.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { try { _ffmpegProcess.Kill(); } catch { } }
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
