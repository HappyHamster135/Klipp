using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Klipp.Core.Abstractions;
using Klipp.Core.Models;
using Klipp.Encoding.FFmpeg;

namespace Klipp.Encoding.Video;

/// <summary>
/// H.264 encoder som använder FFmpeg via Process.Start. Pipear raw BGRA-frames till
/// FFmpegs stdin, FFmpeg producerar en MP4-fil med H.264-kodning.
/// </summary>
/// <remarks>
/// Encodern är dimensions-agnostisk och har inbyggd frame pacing — den pipear frames
/// till FFmpeg med konstant frekvens (target FPS från RecordingSettings), oberoende av
/// hur ofta caller anropar EncodeAsync. När ingen ny frame kommer från caller pipear
/// vi den senaste igen, vilket ger korrekt timing i den producerade MP4-filen.
///
/// VIKTIGT: Output-sökväg sätts via SetOutputPath() innan InitializeAsync().
/// FFmpeg-processen startar dock först vid första framen, så vi kan lära oss
/// dimensions från capture-källan.
/// </remarks>
public sealed class FFmpegH264Encoder : IVideoEncoder
{
    private readonly FFmpegLocator _locator;
    private string? _outputPath;
    private string? _ffmpegPath;
    private RecordingSettings? _settings;

    private Process? _ffmpegProcess;
    private Stream? _stdin;
    private int _inputWidth;
    private int _inputHeight;

    // Pacing: håll senaste framen + en timer som pipear den i constant rate
    private readonly Lock _frameLock = new();
    private ReadOnlyMemory<byte> _latestFrameData;
    private CancellationTokenSource? _pacingCts;
    private Task? _pacingTask;

    private bool _initialized;
    private bool _ffmpegStarted;
    private bool _disposed;

    public FFmpegH264Encoder(FFmpegLocator? locator = null)
    {
        _locator = locator ?? new FFmpegLocator();
    }

    /// <summary>
    /// Sätter sökvägen där FFmpeg ska skriva MP4-filen. Måste anropas innan
    /// InitializeAsync().
    /// </summary>
    public void SetOutputPath(string path) => _outputPath = path;

    public async Task InitializeAsync(RecordingSettings settings, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) throw new InvalidOperationException("Encoder är redan initialiserad.");
        if (string.IsNullOrWhiteSpace(_outputPath))
            throw new InvalidOperationException("Anropa SetOutputPath() innan InitializeAsync().");

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

        var args = BuildFFmpegArgs(_settings!, _outputPath!, width, height);

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

        // Konsumera stderr i bakgrunden så processen inte hänger på en full pipe
        _ = Task.Run(async () =>
        {
            try
            {
                var stderr = _ffmpegProcess.StandardError;
                while (await stderr.ReadLineAsync().ConfigureAwait(false) is not null)
                {
                    // Ignorera output men håll pipen tom
                }
            }
            catch { /* processen kan vara död */ }
        });

        // Starta pacing-tråden som pipear senaste frame i constant rate
        _pacingCts = new CancellationTokenSource();
        _pacingTask = Task.Run(() => PacingLoopAsync(_pacingCts.Token));

        _ffmpegStarted = true;
    }

    /// <summary>
    /// Pacing-loopen kör i bakgrunden och pipear senaste frame till FFmpeg
    /// med constant frekvens (target FPS från settings). Detta säkerställer att
    /// FFmpeg får exakt rätt antal frames per sekund även om capture-källan
    /// inte levererar frames i jämn takt.
    /// </summary>
    private async Task PacingLoopAsync(CancellationToken cancellationToken)
    {
        var targetFps = _settings!.FrameRate;
        var frameIntervalTicks = TimeSpan.TicksPerSecond / targetFps;
        var nextTick = Stopwatch.GetTimestamp();
        var ticksPerStopwatchSecond = Stopwatch.Frequency;

        // Konvertera frame interval från TimeSpan ticks till stopwatch ticks
        var frameIntervalStopwatch = (long)((double)frameIntervalTicks / TimeSpan.TicksPerSecond * ticksPerStopwatchSecond);

        while (!cancellationToken.IsCancellationRequested)
        {
            // Snapshot av senaste frame
            ReadOnlyMemory<byte> frameData;
            lock (_frameLock)
            {
                frameData = _latestFrameData;
            }

            // Pipea om vi har en frame att skicka
            if (!frameData.IsEmpty && _stdin is not null)
            {
                try
                {
                    await _stdin.WriteAsync(frameData, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { break; } // FFmpeg-processen har troligen avslutats
            }

            // Vänta till nästa tick — använd Stopwatch för precis timing
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
                // Vi ligger efter — hoppa fram så vi inte ackumulerar fördröjning
                nextTick = now;
            }
        }
    }

    private static string BuildFFmpegArgs(RecordingSettings settings, string outputPath, int width, int height)
    {
        return $"-f rawvideo -pix_fmt bgra " +
               $"-s {width}x{height} -r {settings.FrameRate} " +
               $"-i - " +
               $"-c:v libx264 -preset veryfast " +
               $"-b:v {settings.VideoBitrate / 1000}k " +
               $"-pix_fmt yuv420p " +
               $"-y \"{outputPath}\"";
    }

    public IAsyncEnumerable<EncodedSample> EncodeAsync(
        VideoFrame frame,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException("Anropa InitializeAsync först.");

        // Starta FFmpeg vid första framen med de faktiska dimensions
        if (!_ffmpegStarted)
        {
            StartFFmpeg(frame.Width, frame.Height);
        }
        else if (frame.Width != _inputWidth || frame.Height != _inputHeight)
        {
            // Capture-storleken har ändrats mitt i — ignorera framen för nu
            return EmptyAsync();
        }

        // Spara framen — pacing-tråden plockar upp den och pipear i constant rate
        lock (_frameLock)
        {
            _latestFrameData = frame.PixelData;
        }

        return EmptyAsync();
    }

    public async IAsyncEnumerable<EncodedSample> FlushAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Stoppa pacing-tråden — vi vill inte mata in fler duplicerade frames
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

        // Stoppa pacing-tråden först
        try
        {
            _pacingCts?.Cancel();
            if (_pacingTask is not null)
            {
                try { await _pacingTask.ConfigureAwait(false); }
                catch { /* ignorera fel vid dispose */ }
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
