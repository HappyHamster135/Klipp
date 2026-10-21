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
    private CaptureVideoEncoderSelection? _encoderSelection;

    private Process? _ffmpegProcess;
    private Stream? _stdin;
    private int _inputWidth;
    private int _inputHeight;

    private Audio.AudioPipeServer? _audioPipe;
    private int _audioSampleRate;
    private int _audioChannels;
    private bool _audioEnabled;
    private string _audioTrackName = Audio.CaptureAudioLayout.SystemName;
    private IReadOnlyList<string>? _audioTrackNames;

    // Frame pacing
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

    public FFmpegSegmentRecorder(FFmpegLocator? locator = null)
    {
        _locator = locator ?? new FFmpegLocator();
    }

    public long FramesWritten => Interlocked.Read(ref _framesWritten);
    public bool IsRunning
    {
        get
        {
            try { return _ffmpegStarted && _ffmpegProcess is { HasExited: false }; }
            catch { return false; }
        }
    }
    public bool IsAudioEnabled => _audioEnabled;
    public int InputWidth => _inputWidth;
    public int InputHeight => _inputHeight;
    public string EncoderDisplayName => _encoderSelection?.DisplayName ?? "Preparing encoder";
    public string? EncoderFallbackReason => _encoderSelection?.FallbackReason;
    public bool IsHardwareEncoder => _encoderSelection?.IsHardware == true;

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
            if (value < 1) throw new ArgumentOutOfRangeException(nameof(value), "Must be at least 1 second.");
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
            if (value < 2) throw new ArgumentOutOfRangeException(nameof(value), "Must be at least 2.");
            _maxSegments = value;
        }
    }

    /// <summary>
    /// Aktiverar ljud i segmenten. Ljudet matas via <see cref="WriteAudioAsync"/>.
    /// </summary>
    public void EnableAudio(int sampleRate, int channels, string audioTrackName = Audio.CaptureAudioLayout.SystemName,
        IReadOnlyList<string>? audioTrackNames = null)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        _audioEnabled = true;
        _audioSampleRate = sampleRate;
        _audioChannels = channels;
        _audioTrackName = audioTrackName;
        _audioTrackNames = audioTrackNames?.ToArray();
    }

    /// <summary>
    /// Skriver PCM float-ljud till FFmpeg om audio är aktiverat och pipen är ansluten.
    /// </summary>
    public async Task WriteAudioAsync(byte[] buffer, int count, CancellationToken cancellationToken = default)
    {
        if (_audioPipe is not null)
            await _audioPipe.WriteAsync(buffer, count, cancellationToken).ConfigureAwait(false);
    }

    public async Task InitializeAsync(RecordingSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) throw new InvalidOperationException("The recorder is already initialized.");
        if (string.IsNullOrWhiteSpace(_segmentDirectory))
            throw new InvalidOperationException("Call SetSegmentDirectory() before InitializeAsync().");

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

        // Städa gamla segment innan vi börjar
        CleanupOldSegments();

        string? audioPipePath = null;
        if (_audioEnabled)
        {
            _audioPipe = new Audio.AudioPipeServer();
            _audioPipe.Start();
            audioPipePath = _audioPipe.PipePath;
        }

        var args = BuildFFmpegArgs(_settings!, _segmentDirectory!, width, height, audioPipePath);

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
    private string BuildFFmpegArgs(
        RecordingSettings settings,
        string segmentDir,
        int width,
        int height,
        string? audioPipePath)
    {
        var pattern = Path.Combine(segmentDir, "seg_%03d.mp4");
        var scaleFilter = settings.Width != width || settings.Height != height
            ? $"-vf scale={settings.Width}:{settings.Height}:flags=fast_bilinear "
            : string.Empty;

        var args =
            $"-hide_banner -loglevel warning -filter_threads 2 -filter_complex_threads 1 -f rawvideo -pix_fmt bgra " +
            $"-s {width}x{height} -r {settings.FrameRate} " +
            $"-i - ";

        if (audioPipePath is not null)
        {
            args +=
                $"-thread_queue_size 512 -f f32le -ar {_audioSampleRate} -ac {_audioChannels} " +
                $"-i \"{audioPipePath}\" " +
                $"-map 0:v:0 " +
                string.Join(" ", Audio.CaptureAudioLayout.OutputArguments("[1:a]", _audioChannels,
                    "aresample=async=1:first_pts=0", _audioTrackName, _audioTrackNames).Select(argument => $"\"{argument}\"")) + " ";
        }

        args +=
            string.Join(" ", FFmpegCaptureEncoderSelector.VideoArguments(settings, _encoderSelection!, settings.FrameRate)) + " " +
            scaleFilter;

        if (audioPipePath is not null)
        {
            args +=
                $"-c:a aac -b:a {settings.AudioBitrate / 1000}k ";
        }

        args +=
            $"-f segment " +
            $"-segment_time {_segmentDurationSeconds} " +
            $"-segment_wrap {_maxSegments} " +
            $"-reset_timestamps 1 " +
            $"-y " +
            $"\"{pattern}\"";

        return args;
    }

    private static void TrySetFFmpegPriority(Process process)
    {
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch { }
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
            lock (_frameLock)
            {
                frameData = _latestFrameData;
            }

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
                    try
                    {
                        await Task.Delay(msToWait, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
    }

    public IAsyncEnumerable<EncodedSample> EncodeAsync(
        VideoFrame frame,
        CancellationToken cancellationToken = default)
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

        if (_audioPipe is not null)
        {
            await _audioPipe.DisposeAsync().ConfigureAwait(false);
            _audioPipe = null;
        }

        if (_ffmpegProcess is not null && !_ffmpegProcess.HasExited)
        {
            await _ffmpegProcess.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_ffmpegProcess is { ExitCode: not 0 })
            throw new InvalidOperationException("The video encoder stopped before finalizing replay capture. Completed segments have been kept.");

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

            if (_audioPipe is not null)
            {
                await _audioPipe.DisposeAsync().ConfigureAwait(false);
                _audioPipe = null;
            }
        }
    }

    /// <summary>
    /// Stoppar segment-recordern snabbt utan att försöka färdigställa sista segmentet.
    /// Används när replay-bufferten stängs eller byter läge och sista halvskrivna
    /// segmentet ändå inte ska sparas.
    /// </summary>
    public async Task AbortAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _pacingCts?.Cancel();
            if (_pacingTask is not null)
            {
                try
                {
                    await _pacingTask.WaitAsync(TimeSpan.FromMilliseconds(500))
                        .ConfigureAwait(false);
                }
                catch { }
            }
            _pacingCts?.Dispose();
        }
        catch { }

        try
        {
            if (_stdin is not null)
            {
                try { _stdin.Close(); } catch { }
                _stdin = null;
            }

            if (_audioPipe is not null)
            {
                await _audioPipe.DisposeAsync().ConfigureAwait(false);
                _audioPipe = null;
            }

            if (_ffmpegProcess is not null && !_ffmpegProcess.HasExited)
            {
                try { _ffmpegProcess.Kill(entireProcessTree: true); }
                catch { try { _ffmpegProcess.Kill(); } catch { } }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await _ffmpegProcess.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
                catch { }
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
