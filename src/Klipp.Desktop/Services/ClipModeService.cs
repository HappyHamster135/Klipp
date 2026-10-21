using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Capture.Models;
using Klipp.Capture.Video;
using Klipp.Core.Models;
using Klipp.Encoding.Audio;
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
    // 5s-segment ger bra granularitet. 36 segment = 3 minuters buffer (täcker
    // den längsta klipplängden 2 min med god marginal).
    private const int SegmentDurationSeconds = 2;
    private const int MaxSegments = 90; // 2s * 90 = 3 min buffer

    private readonly FFmpegLocator _ffmpegLocator = new();
    private readonly ClipExtractor _extractor;
    private readonly string _segmentDirectory;
    private readonly object _lock = new();

    private IVideoCaptureSource? _captureSource;
    private FFmpegSegmentRecorder? _recorder;
    private MixedAudioCapture? _audioCapture;
    private long _videoStartTimestamp;
    private long _audioFramesToSkip;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private DateTime _monitorStartTime;
    private RecordingSettings? _activeSettings;
    private string? _captureFailureMessage;
    private bool _isMonitoring;
    private bool _disposed;
    private bool _replayPreservationPending;
    public CaptureTarget? ActiveTarget { get; private set; }
    public string CaptureBackendName => (_captureSource as RoutedVideoCaptureSource)?.BackendName ?? "Windows capture";
    public string? GameCaptureFallbackReason => (_captureSource as RoutedVideoCaptureSource)?.FallbackReason;
    public event EventHandler? CaptureTargetClosed;
    public System.Collections.Generic.IReadOnlyList<CaptureAudioDeviceStatus> AudioDeviceStatuses =>
        _audioCapture?.DeviceStatuses ?? [];
    public System.Collections.Generic.IReadOnlyList<CaptureAudioLevel> AudioLevels => _audioCapture?.AudioLevels ?? [];
    public string? EncoderFallbackReason => _recorder?.EncoderFallbackReason;

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

    public CaptureRuntimeHealth GetHealthSnapshot()
    {
        var source = _captureSource;
        var recorder = _recorder;
        var statistics = source?.GetStatistics() ?? default;
        var audioSignal = _audioCapture?.HasRecentData == true;
        var settings = _activeSettings;
        return new CaptureRuntimeHealth(
            IsMonitoring,
            statistics.IsCapturing,
            recorder?.IsRunning ?? false,
            recorder?.IsAudioEnabled ?? false,
            audioSignal,
            statistics.CapturedFrames,
            statistics.DeliveredFrames,
            statistics.DroppedFrames,
            statistics.InvalidFrames,
            recorder?.FramesWritten ?? 0,
            MonitoredSeconds,
            statistics.LastFrameUtc,
            statistics.FrameWidth,
            statistics.FrameHeight,
            settings?.FrameRate ?? 0,
            recorder?.EncoderDisplayName ?? "Preparing encoder",
            _captureFailureMessage ?? _audioCapture?.FailureMessage);
    }

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
    public async Task StartMonitoringAsync(CaptureOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        options ??= new CaptureOptions();

        lock (_lock)
        {
            if (_isMonitoring) throw new InvalidOperationException("Background recording is already running.");
        }

        try
        {
            // An emergency archive that could not finish must be retried before
            // the segment recorder is allowed to overwrite the previous buffer.
            if (_replayPreservationPending || File.Exists(Path.Combine(_segmentDirectory, "preserve-replay.flag")))
            {
                await new RecordingRecoveryService(locator: _ffmpegLocator).PreserveReplayBufferAsync(
                    _segmentDirectory, 0, options.FrameRate).ConfigureAwait(false);
                _replayPreservationPending = false;
            }
            var targets = new CaptureTargetService();
            var target = options.Target ?? targets.GetPrimaryMonitor().Target;
            var (sourceWidth, sourceHeight) = targets.GetCaptureSize(target);
            ActiveTarget = target;
            var settings = CreateSettingsForMonitor(sourceWidth, sourceHeight, options);
            _activeSettings = settings;
            _captureFailureMessage = null;
            Interlocked.Exchange(ref _videoStartTimestamp, 0);
            _audioFramesToSkip = -1;

            _recorder = new FFmpegSegmentRecorder(_ffmpegLocator)
            {
                SegmentDurationSeconds = SegmentDurationSeconds,
                MaxSegments = MaxSegments
            };
            _recorder.SetSegmentDirectory(_segmentDirectory);
            var audioOptions = options.CreateAudioOptions(target);
            var audioEnabled = audioOptions.IsEnabled;
            if (audioEnabled) _recorder.EnableAudio(48000, audioOptions.OutputChannels,
                audioOptions.AudioTrackName, audioOptions.TrackNames);
            await _recorder.InitializeAsync(settings);

            _captureSource = VideoCaptureSourceFactory.Create(target, settings.FrameRate, sourceWidth, sourceHeight, options);
            _captureSource.TargetClosed += OnCaptureTargetClosed;
            _captureCts = new CancellationTokenSource();
            await _captureSource.StartAsync();

            // Keep any game-hook attachment delay outside the bounded audio
            // prebuffer and its first-video alignment calculation.
            if (audioEnabled)
            {
                _audioCapture = new MixedAudioCapture();
                _audioCapture.Start(audioOptions, WriteAudioSamplesAsync);
            }
            if (!_captureSource.IsCapturing)
                throw new InvalidOperationException(_captureSource.FailureMessage ?? "The selected capture source closed before recording started.");

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
                throw new InvalidOperationException("Start background recording before saving a clip.");
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

        if (_audioCapture is not null)
            await _audioCapture.StopAsync().ConfigureAwait(false);

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
                await _recorder.AbortAsync().ConfigureAwait(false);
                _recorder = null;
            }
            catch { /* ignorera */ }
        }

        await CleanupAsync();
    }

    /// <summary>Stops a failed emergency save and retains the entire available buffer for Recovery.</summary>
    public async Task StopAndPreserveBufferAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var seconds = MonitoredSeconds;
        var frameRate = _activeSettings?.FrameRate ?? 24;
        lock (_lock)
        {
            if (!_isMonitoring) return;
            _isMonitoring = false;
        }
        _replayPreservationPending = true;
        // The small marker prevents a subsequent start from deleting the buffer
        // if moving its directory fails (for example, temporary access denial).
        try
        {
            using var marker = new FileStream(Path.Combine(_segmentDirectory, "preserve-replay.flag"),
                FileMode.Create, FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough);
            marker.WriteByte(1);
            marker.Flush(flushToDisk: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        string? failure = null;
        try
        {
            _captureCts?.Cancel();
            if (_audioCapture is not null)
            {
                try { await _audioCapture.StopAsync().ConfigureAwait(false); }
                catch (Exception exception) { failure = exception.Message; }
            }
            if (_captureTask is not null)
            {
                try { await _captureTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception exception) { failure = exception.Message; }
            }
            if (_recorder is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                try { await foreach (var _ in _recorder.FlushAsync(timeout.Token).ConfigureAwait(false)) { } }
                catch (Exception exception) { failure = exception.Message; }
                try { await _recorder.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception) { failure = exception.Message; }
                _recorder = null;
            }
            await new RecordingRecoveryService(locator: _ffmpegLocator).PreserveReplayBufferAsync(
                _segmentDirectory, Math.Min(seconds, SegmentDurationSeconds * MaxSegments), frameRate, failure).ConfigureAwait(false);
            _replayPreservationPending = false;
        }
        finally { await CleanupAsync().ConfigureAwait(false); }
    }

    private void OnCaptureTargetClosed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _captureSource)) return;
        _captureFailureMessage = _captureSource?.FailureMessage ?? "The selected capture source was closed.";
        CaptureTargetClosed?.Invoke(this, EventArgs.Empty);
    }

    private static RecordingSettings CreateSettingsForMonitor(int width, int height, CaptureOptions options)
    {
        var (outputWidth, outputHeight) = options.GetOutputSize(width, height);

        return RecordingSettings.Default1080p60 with
        {
            Width = outputWidth,
            Height = outputHeight,
            FrameRate = options.FrameRate,
            VideoBitrate = options.VideoBitrate,
            EncoderPreference = options.EncoderMode
        };
    }

    private async Task WriteAudioSamplesAsync(byte[] buffer, int count, CancellationToken cancellationToken)
    {
        var recorder = _recorder;
        if (recorder is null || count <= 0) return;
        // Audio starts alongside capture, but the video encoder creates its
        // second input only when the first video frame arrives. Retain the first
        // chunk until that input is ready instead of losing the opening sound.
        while (!recorder.IsRunning || Interlocked.Read(ref _videoStartTimestamp) == 0)
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        if (_audioFramesToSkip < 0)
        {
            var audioStart = _audioCapture?.FirstSampleTimestamp ?? Interlocked.Read(ref _videoStartTimestamp);
            _audioFramesToSkip = Math.Max(0, (long)Math.Round(
                (Interlocked.Read(ref _videoStartTimestamp) - audioStart) / (double)Stopwatch.Frequency * 48000));
        }
        if (_audioFramesToSkip > 0)
        {
            var blockAlign = (_audioCapture?.WaveFormat.Channels ?? 2) * sizeof(float);
            var skippedFrames = Math.Min(_audioFramesToSkip, count / blockAlign);
            var skippedBytes = (int)skippedFrames * blockAlign;
            _audioFramesToSkip -= skippedFrames;
            count -= skippedBytes;
            if (count == 0) return;
            Buffer.BlockCopy(buffer, skippedBytes, buffer, 0, count);
        }
        await recorder.WriteAudioAsync(buffer, count, cancellationToken).ConfigureAwait(false);
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        if (_captureSource is null || _recorder is null) return;

        try
        {
            await foreach (var frame in _captureSource.ReadSamplesAsync(cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested) break;

                Interlocked.CompareExchange(ref _videoStartTimestamp, Stopwatch.GetTimestamp(), 0);

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
            _captureFailureMessage = ex.Message;
            System.Diagnostics.Debug.WriteLine($"[Klipp] CaptureLoop fel: {ex.Message}");
        }
    }

    private async Task CleanupAsync()
    {
        if (_captureSource is not null)
        {
            _captureSource.TargetClosed -= OnCaptureTargetClosed;
            try { await _captureSource.StopAsync(); } catch { }
            await _captureSource.DisposeAsync();
            _captureSource = null;
        }

        if (_audioCapture is not null)
        {
            await _audioCapture.DisposeAsync().ConfigureAwait(false);
            _audioCapture = null;
        }

        if (_recorder is not null)
        {
            await _recorder.DisposeAsync();
            _recorder = null;
        }

        _captureCts?.Dispose();
        _captureCts = null;
        _captureTask = null;
        _activeSettings = null;
        ActiveTarget = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (IsMonitoring) await StopMonitoringAsync().ConfigureAwait(false);
        else await CleanupAsync().ConfigureAwait(false);
        _disposed = true;
    }
}
