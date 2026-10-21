using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Klipp.Capture.Models;
using Klipp.Capture.Video;
using Klipp.Core.Models;
using Klipp.Encoding.Audio;
using Klipp.Encoding.FFmpeg;
using Klipp.Encoding.Video;

namespace Klipp.Desktop.Services;

/// <summary>
/// Hanterar manuell start/stopp-inspelning. Video och ljud spelas in separat
/// och muxas ihop efter stopp för stabil A/V-synk.
/// </summary>
public sealed class RecordingService : IAsyncDisposable
{
    private const int AudioWarmupMilliseconds = 150;

    private readonly FFmpegLocator _ffmpegLocator = new();
    private readonly object _lock = new();
    private readonly object _journalLock = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    private IVideoCaptureSource? _captureSource;
    private FFmpegH264Encoder? _encoder;
    private WasapiLoopbackWavRecorder? _audioRecorder;
    private CancellationTokenSource? _captureCts;
    private Task? _captureTask;
    private string? _activeOutputPath;
    private RecordingSessionHandle? _session;
    private string? _tempVideoPath;
    private string? _tempAudioPath;
    private long _lastJournalTimestamp;
    private long? _videoStartTimestamp;
    private RecordingSettings? _activeSettings;
    private string? _captureFailureMessage;
    private bool _isRecording;
    private bool _disposed;
    public CaptureTarget? ActiveTarget { get; private set; }
    public string CaptureBackendName => (_captureSource as RoutedVideoCaptureSource)?.BackendName ?? "Windows capture";
    public string? GameCaptureFallbackReason => (_captureSource as RoutedVideoCaptureSource)?.FallbackReason;
    public event EventHandler? CaptureTargetClosed;
    public event EventHandler<RecordingStartedEventArgs>? RecordingStarted;
    public event EventHandler<RecoveredRecording>? RecordingCompleted;
    public RecordingRecoveryService Recovery { get; }
    public IReadOnlyList<CaptureAudioDeviceStatus> AudioDeviceStatuses => _audioRecorder?.DeviceStatuses ?? [];
    public IReadOnlyList<CaptureAudioLevel> AudioLevels => _audioRecorder?.AudioLevels ?? [];
    public string? EncoderFallbackReason => _encoder?.EncoderFallbackReason;
    public (long VideoBytes, long AudioBytes) GetPendingMediaSizes()
    {
        static long Size(string? path)
        {
            try { return path is not null && File.Exists(path) ? new FileInfo(path).Length : 0; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return 0; }
        }
        return (Size(_tempVideoPath), Size(_tempAudioPath));
    }
    public string? ActiveOutputPath { get { lock (_lock) return _activeOutputPath; } }

    public RecordingService()
    {
        Recovery = new RecordingRecoveryService(locator: _ffmpegLocator);
    }

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
                return (_encoder?.FramesWritten ?? 0) / (double)Math.Max(1, _activeSettings?.FrameRate ?? 1);
            }
        }
    }

    /// <summary>True om FFmpeg och FFprobe är installerade och redo.</summary>
    public bool IsFFmpegInstalled => _ffmpegLocator.IsInstalled;

    public CaptureRuntimeHealth GetHealthSnapshot()
    {
        var source = _captureSource;
        var encoder = _encoder;
        var audio = _audioRecorder;
        var statistics = source?.GetStatistics() ?? default;
        var settings = _activeSettings;
        return new CaptureRuntimeHealth(
            IsRecording,
            statistics.IsCapturing,
            encoder?.IsRunning ?? false,
            audio is not null,
            audio?.HasRecentData ?? false,
            statistics.CapturedFrames,
            statistics.DeliveredFrames,
            statistics.DroppedFrames,
            statistics.InvalidFrames,
            encoder?.FramesWritten ?? 0,
            RecordedSeconds,
            statistics.LastFrameUtc,
            statistics.FrameWidth,
            statistics.FrameHeight,
            settings?.FrameRate ?? 0,
            encoder?.EncoderDisplayName ?? "Preparing encoder",
            _captureFailureMessage ?? audio?.FailureMessage);
    }

    /// <summary>
    /// Säkerställer att FFmpeg/FFprobe är installerat. Laddar ner om saknas.
    /// </summary>
    public async Task EnsureFFmpegInstalledAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _ffmpegLocator.GetFFmpegPathAsync(progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Startar inspelning till given slutfil. Själva MP4:an skrivs klart vid StopAsync.
    /// </summary>
    public async Task StartAsync(string outputPath, CaptureOptions? options = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        options ??= new CaptureOptions();

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lock)
                if (_isRecording) throw new InvalidOperationException("Recording is already running.");
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            _videoStartTimestamp = null;
            _lastJournalTimestamp = 0;

            var targets = new CaptureTargetService();
            var target = options.Target ?? targets.GetPrimaryMonitor().Target;
            var (sourceWidth, sourceHeight) = targets.GetCaptureSize(target);
            ActiveTarget = target;
            var settings = CreateSettingsForMonitor(sourceWidth, sourceHeight, options);
            _activeSettings = settings;
            _captureFailureMessage = null;
            var audioOptions = options.CreateAudioOptions(target);
            _session = Recovery.CreateSession(outputPath, settings.FrameRate,
                audioOptions.IsEnabled, audioOptions.OutputChannels, audioOptions.AudioTrackName, audioOptions.TrackNames);
            _tempVideoPath = Path.Combine(_session.Directory, "video.mp4");
            _tempAudioPath = Path.Combine(_session.Directory, "audio.wav");

            _encoder = new FFmpegH264Encoder(_ffmpegLocator);
            _encoder.SetOutputPath(_tempVideoPath);
            await _encoder.InitializeAsync(settings).ConfigureAwait(false);

            _captureSource = VideoCaptureSourceFactory.Create(target, settings.FrameRate, sourceWidth, sourceHeight, options);
            _captureSource.TargetClosed += OnCaptureTargetClosed;
            _captureCts = new CancellationTokenSource();
            await _captureSource.StartAsync().ConfigureAwait(false);

            // Game capture can take time to attach. Open audio only after a
            // usable video source is ready, then retain the existing warmup.
            _audioRecorder = audioOptions.IsEnabled ? StartAudioRecorder(_tempAudioPath, audioOptions) : null;
            if (_audioRecorder is not null)
                await Task.Delay(AudioWarmupMilliseconds).ConfigureAwait(false);
            if (!_captureSource.IsCapturing)
                throw new InvalidOperationException(_captureSource.FailureMessage ?? "The selected capture source closed before recording started.");

            lock (_lock)
            {
                _activeOutputPath = _session.Manifest.OutputPath;
                _isRecording = true;
            }

            _captureTask = Task.Run(() => CaptureLoopAsync(_captureCts.Token));
            RecordingStarted?.Invoke(this, new(_session.Manifest.Id, _activeOutputPath, _session.Manifest.StartedUtc));
        }
        catch (Exception exception)
        {
            // A second Start call must never clean up a capture already in use.
            if (IsRecording) throw;
            lock (_lock) _isRecording = false;
            if (_session is not null) Recovery.SaveFailure(_session, exception);
            await CleanupAsync(deleteTempFiles: false).ConfigureAwait(false);
            throw;
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>
    /// Stoppar inspelning och returnerar sökvägen till den producerade MP4-filen.
    /// Returnerar null om ingen inspelning körde.
    /// </summary>
    public async Task<string?> StopAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        var completed = false;
        var noFrames = false;
        RecoveredRecording? result = null;
        try
        {
            lock (_lock)
            {
                if (!_isRecording) return null;
                _isRecording = false;
            }

            _captureCts?.Cancel();

            if (_captureTask is not null)
            {
                try { await _captureTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }

            // Stop at the video stop epoch and drain the short native audio
            // prebuffer now. Video-only encoder finalization can take longer,
            // and should not keep the microphone open or extend the WAV.
            _audioRecorder?.Stop();
            CheckpointSession("finalizing");

            if (_encoder is not null)
            {
                await foreach (var _ in _encoder.FlushAsync())
                {
                    // FFmpeg skriver klart video-only MP4.
                }
            }

            noFrames = (_encoder?.FramesWritten ?? 0) == 0;
            if (noFrames) return null;
            CheckpointSession("finalizing");
            result = await Recovery.FinishActiveAsync(_session ??
                throw new InvalidOperationException("The recording session is missing.")).ConfigureAwait(false);
            completed = true;
        }
        catch (Exception exception)
        {
            if (_session is not null)
            {
                try { CheckpointSession("needs-recovery"); } catch { }
                Recovery.SaveFailure(_session, exception);
            }
            throw;
        }
        finally
        {
            try { await CleanupAsync(deleteTempFiles: completed || noFrames).ConfigureAwait(false); }
            finally { _lifecycle.Release(); }
        }
        if (result is not null) RecordingCompleted?.Invoke(this, result);
        return result?.OutputPath;
    }

    private static WasapiLoopbackWavRecorder StartAudioRecorder(string audioPath, CaptureAudioOptions options)
    {
        WasapiLoopbackWavRecorder? recorder = null;

        try
        {
            recorder = new WasapiLoopbackWavRecorder();
            recorder.Start(audioPath, options);
            return recorder;
        }
        catch
        {
            recorder?.Dispose();
            throw;
        }
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

    public RecordingSessionMarker? TryAddMarker(string label = "Highlight")
    {
        lock (_journalLock)
        {
            if (!IsRecording || _session is null || _encoder is null || _encoder.FramesWritten <= 0) return null;
            var cleanLabel = string.IsNullOrWhiteSpace(label) ? "Highlight" : label.Trim().Replace('\r', ' ').Replace('\n', ' ');
            var marker = new RecordingSessionMarker(Guid.NewGuid().ToString("N"),
                _encoder.FramesWritten / (double)Math.Max(1, _activeSettings?.FrameRate ?? 1),
                cleanLabel[..Math.Min(80, cleanLabel.Length)]);
            _session.Manifest.Markers.Add(marker);
            try { CheckpointSession("recording"); }
            catch { _session.Manifest.Markers.Remove(marker); throw; }
            return marker;
        }
    }

    private void CheckpointSession(string state)
    {
        lock (_journalLock)
        {
            if (_session is null) return;
            var manifest = _session.Manifest;
            manifest.State = state;
            manifest.FramesWritten = _encoder?.FramesWritten ?? manifest.FramesWritten;
            var videoStart = _encoder?.PacingStartTimestamp is > 0 ? _encoder.PacingStartTimestamp : _videoStartTimestamp;
            if (videoStart.HasValue && _audioRecorder?.FirstAudioStartTimestamp is long audioStart)
                manifest.AudioOffsetSeconds = (audioStart - videoStart.Value) / (double)Stopwatch.Frequency;
            _lastJournalTimestamp = Stopwatch.GetTimestamp();
            Recovery.SaveSession(_session);
        }
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        if (_captureSource is null || _encoder is null) return;

        try
        {
            await foreach (var frame in _captureSource.ReadSamplesAsync(cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested) break;

                _videoStartTimestamp ??= Stopwatch.GetTimestamp();

                await foreach (var _ in _encoder.EncodeAsync(frame, cancellationToken))
                {
                    // FFmpeg encodar video-only direkt.
                }
                if (Stopwatch.GetTimestamp() - _lastJournalTimestamp >= Stopwatch.Frequency)
                {
                    try { CheckpointSession("recording"); }
                    catch (Exception exception) { Debug.WriteLine($"[Klipp] Could not update recording recovery journal: {exception}"); }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Förväntat när StopAsync anropas.
        }
        catch (Exception ex)
        {
            _captureFailureMessage = ex.Message;
            Debug.WriteLine($"[Klipp] Recording capture loop failed: {ex}");
        }
    }

    private async Task CleanupAsync(bool deleteTempFiles)
    {
        if (_captureSource is not null)
        {
            _captureSource.TargetClosed -= OnCaptureTargetClosed;
            try { await _captureSource.StopAsync().ConfigureAwait(false); } catch { }
            try { await _captureSource.DisposeAsync().ConfigureAwait(false); } catch { }
            _captureSource = null;
        }

        if (_audioRecorder is not null)
        {
            try { _audioRecorder.Stop(); } catch { }
            try { _audioRecorder.Dispose(); } catch { }
            _audioRecorder = null;
        }

        if (_encoder is not null)
        {
            try { await _encoder.DisposeAsync().ConfigureAwait(false); } catch { }
            _encoder = null;
        }

        _captureCts?.Dispose();
        _captureCts = null;
        _captureTask = null;
        _activeOutputPath = null;
        _tempVideoPath = null;
        _tempAudioPath = null;
        _videoStartTimestamp = null;
        _activeSettings = null;
        ActiveTarget = null;

        lock (_journalLock)
        {
            if (_session is not null)
            {
                if (deleteTempFiles)
                {
                    try { Recovery.CompleteCleanup(_session); }
                    catch (Exception exception) { Debug.WriteLine($"[Klipp] Recording cleanup deferred: {exception}"); }
                }
                else _session.Dispose();
            }
            _session = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        if (IsRecording)
            await StopAsync().ConfigureAwait(false);
        else
            await CleanupAsync(deleteTempFiles: false).ConfigureAwait(false);

        _disposed = true;
    }
}
