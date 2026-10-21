using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Klipp.Capture.Models;
using Klipp.Core.Abstractions;
using Klipp.Core.Enums;
using Klipp.Core.Models;
using Klipp.Core.Capture;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Klipp.Capture.Video;

/// <summary>
/// Kontinuerlig capture från ett fönster eller en skärm via Windows Graphics Capture.
/// Producerar <see cref="VideoFrame"/>-objekt via <see cref="ReadSamplesAsync"/>.
/// </summary>
/// <remarks>
/// Capture-loopen och konsumenten är frikopplade via en <see cref="Channel{T}"/>.
/// Om konsumenten är långsam kommer äldre frames droppas istället för att blockera capture.
/// </remarks>
public sealed class WgcCaptureSource : IVideoCaptureSource
{
    private readonly CaptureTarget _target;
    private readonly D3D11Device _d3d;
    private readonly FrameReader _reader;
    private readonly Channel<VideoFrame> _channel;
    private readonly object _channelGate = new();
    private readonly long _captureFrameIntervalTicks;
    private readonly bool _hideCaptureBorder;
    private readonly bool _captureCursor;
    private GraphicsCaptureItem? _captureItem;
    private Windows.Graphics.SizeInt32 _poolSize;
    private int _canvasWidth;
    private int _canvasHeight;
    private int _targetClosed;

    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private CancellationTokenSource? _captureLoopCts;
    private Task? _captureLoopTask;
    private Stopwatch? _clock;
    private long _capturedFrames;
    private long _deliveredFrames;
    private long _droppedFrames;
    private long _invalidFrames;
    private long _lastFrameUtcTicks;
    private int _pendingFrames;
    private int _frameWidth;
    private int _frameHeight;
    private bool _disposed;

    /// <inheritdoc/>
    public bool IsCapturing { get; private set; }
    public CaptureTarget Target => _target;
    public string? FailureMessage { get; private set; }
    public event EventHandler? TargetClosed;

    public CaptureSourceStatistics GetStatistics()
    {
        var lastFrameTicks = Interlocked.Read(ref _lastFrameUtcTicks);
        return new CaptureSourceStatistics(
            IsCapturing,
            Interlocked.Read(ref _capturedFrames),
            Interlocked.Read(ref _deliveredFrames),
            Interlocked.Read(ref _droppedFrames),
            Interlocked.Read(ref _invalidFrames),
            lastFrameTicks > 0 ? new DateTime(lastFrameTicks, DateTimeKind.Utc) : null,
            Volatile.Read(ref _frameWidth),
            Volatile.Read(ref _frameHeight));
    }

    /// <summary>
    /// Skapar en ny capture-source för givet target.
    /// </summary>
    /// <param name="target">Fönster eller skärm att fånga.</param>
    public WgcCaptureSource(CaptureTarget target, int? maxFrameRate = null, bool hideCaptureBorder = true, bool captureCursor = true)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (maxFrameRate is <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFrameRate), "Max FPS must be positive.");

        _target = target;
        _d3d = new D3D11Device();
        _reader = new FrameReader(_d3d);
        _captureFrameIntervalTicks = maxFrameRate is null
            ? 0
            : Math.Max(1, Stopwatch.Frequency / maxFrameRate.Value);
        _hideCaptureBorder = hideCaptureBorder;
        _captureCursor = captureCursor;

        // Håll bara några få fullstora frames i minnet. Vid 2560x1600 är en frame
        // cirka 16 MB, så en djup kö gör appen tung utan att ge bättre output.
        _channel = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(capacity: 1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsCapturing) return;

        var item = _target.Kind switch
        {
            CaptureTargetKind.Window => GraphicsCaptureItemFactory.CreateForWindow(_target.Handle),
            CaptureTargetKind.Monitor => GraphicsCaptureItemFactory.CreateForMonitor(_target.Handle),
            _ => throw new NotSupportedException("Unknown capture target type: " + _target.Kind)
        };
        if (item.Size.Width <= 1 || item.Size.Height <= 1)
            throw new InvalidOperationException("Restore the selected capture source before starting capture.");
        _captureItem = item;
        _captureItem.Closed += OnCaptureItemClosed;
        _poolSize = item.Size;
        _canvasWidth = item.Size.Width;
        _canvasHeight = item.Size.Height;
        Interlocked.Exchange(ref _targetClosed, 0);
        FailureMessage = null;

        _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _d3d.WinRTDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,
            item.Size);

        _session = _framePool.CreateCaptureSession(item);
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            _session.IsCursorCaptureEnabled = _captureCursor;
        if (_hideCaptureBorder)
            await TryDisableCaptureBorderAsync(_session).ConfigureAwait(false);
        _clock = Stopwatch.StartNew();
        Interlocked.Exchange(ref _capturedFrames, 0);
        Interlocked.Exchange(ref _deliveredFrames, 0);
        Interlocked.Exchange(ref _droppedFrames, 0);
        Interlocked.Exchange(ref _invalidFrames, 0);
        Interlocked.Exchange(ref _lastFrameUtcTicks, 0);
        Volatile.Write(ref _pendingFrames, 0);
        Volatile.Write(ref _frameWidth, 0);
        Volatile.Write(ref _frameHeight, 0);

        _captureLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _captureLoopTask = Task.Run(() => CaptureLoopAsync(_captureLoopCts.Token), CancellationToken.None);

        _session.StartCapture();
        if (Volatile.Read(ref _targetClosed) != 0)
            throw new InvalidOperationException("The selected capture source closed before recording started.");
        IsCapturing = true;
    }

    private void OnCaptureItemClosed(GraphicsCaptureItem sender, object args)
    {
        FailureMessage = "The selected capture source was closed.";
        SignalTargetClosed();
    }

    private void SignalTargetClosed()
    {
        if (Interlocked.Exchange(ref _targetClosed, 1) != 0) return;
        IsCapturing = false;
        _captureLoopCts?.Cancel();
        _channel.Writer.TryComplete();
        try { TargetClosed?.Invoke(this, EventArgs.Empty); }
        catch (Exception exception) { Debug.WriteLine($"[Klipp] Capture source close handler failed: {exception.Message}"); }
    }

    private static async Task TryDisableCaptureBorderAsync(GraphicsCaptureSession session)
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
                return;

            var access = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless)
                .AsTask()
                .ConfigureAwait(false);

            if (access.ToString() == "Allowed")
                session.IsBorderRequired = false;
        }
        catch
        {
            // Borderless capture kräver OS-/policy-stöd. Om Windows nekar fortsätter
            // capture normalt med systemets säkerhetsram.
        }
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        var pool = _framePool!;
        var clock = _clock!;
        var nextFrameReadTimestamp = 0L;
        var hasDeliveredFrame = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = pool.TryGetNextFrame();
                if (frame is null)
                {
                    // Ingen frame redo än — vänta en bit. Vid 60 FPS kommer en frame var ~16ms.
                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (frame.ContentSize.Width > 1 && frame.ContentSize.Height > 1 &&
                    (frame.ContentSize.Width != _poolSize.Width || frame.ContentSize.Height != _poolSize.Height))
                {
                    var size = frame.ContentSize;
                    frame.Dispose();
                    pool.Recreate(_d3d.WinRTDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                    _poolSize = size;
                    continue;
                }
                using var capturedFrame = frame;
                if (frame.ContentSize.Width <= 1 || frame.ContentSize.Height <= 1) continue;

                var now = Stopwatch.GetTimestamp();
                if (_captureFrameIntervalTicks > 0 &&
                    hasDeliveredFrame &&
                    now < nextFrameReadTimestamp)
                {
                    continue;
                }

                if (_captureFrameIntervalTicks > 0)
                    nextFrameReadTimestamp = now + _captureFrameIntervalTicks;

                byte[] pixelBytes;
                int width;
                int height;
                try
                {
                    (pixelBytes, width, height) = _reader.ReadPixels(frame.Surface);
                    if (width != _canvasWidth || height != _canvasHeight)
                    {
                        pixelBytes = BgraCaptureFrameFit.Fit(pixelBytes, width, height, _canvasWidth, _canvasHeight);
                        width = _canvasWidth;
                        height = _canvasHeight;
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or OverflowException or OutOfMemoryException)
                {
                    Interlocked.Increment(ref _invalidFrames);
                    Debug.WriteLine($"[Klipp] Droppar ogiltig capture-frame: {ex.Message}");
                    continue;
                }

                var videoFrame = new VideoFrame(
                    Width: width,
                    Height: height,
                    Format: PixelFormat.Bgra8,
                    Timestamp: clock.ElapsedTicks * 10_000_000L / Stopwatch.Frequency,
                    PixelData: pixelBytes);

                Interlocked.Increment(ref _capturedFrames);
                Interlocked.Exchange(ref _lastFrameUtcTicks, DateTime.UtcNow.Ticks);
                Volatile.Write(ref _frameWidth, width);
                Volatile.Write(ref _frameHeight, height);

                // Channel-kapaciteten är ett. Låset gör drop-räknaren exakt samtidigt
                // som DropOldest fortfarande ger editorn den färskaste framen.
                lock (_channelGate)
                {
                    if (_pendingFrames > 0)
                        Interlocked.Increment(ref _droppedFrames);
                    if (_channel.Writer.TryWrite(videoFrame))
                        _pendingFrames = 1;
                    else
                        Interlocked.Increment(ref _droppedFrames);
                }
                hasDeliveredFrame = true;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — ignorera.
        }
        catch (Exception exception)
        {
            FailureMessage = exception.Message;
            Debug.WriteLine($"[Klipp] Capture source stopped: {exception}");
            SignalTargetClosed();
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        IsCapturing = false;

        _captureLoopCts?.Cancel();

        if (_captureLoopTask is not null)
        {
            try
            {
                await _captureLoopTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
        }

        _session?.Dispose();
        _session = null;
        _framePool?.Dispose();
        _framePool = null;
        if (_captureItem is not null) _captureItem.Closed -= OnCaptureItemClosed;
        _captureItem = null;
        _clock?.Stop();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<VideoFrame> ReadSamplesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Vi läser via WaitToReadAsync + TryRead istället för ReadAllAsync.
        // Det låter oss avsluta GRACIÖST vid cancellation utan att kasta en
        // OperationCanceledException (man kan inte try/catch runt yield return,
        // så vi fångar i en hjälpmetod istället).
        while (await WaitToReadSafelyAsync(cancellationToken).ConfigureAwait(false))
        {
            while (true)
            {
                VideoFrame frame;
                lock (_channelGate)
                {
                    if (!_channel.Reader.TryRead(out frame))
                        break;
                    _pendingFrames = 0;
                }

                Interlocked.Increment(ref _deliveredFrames);
                yield return frame;
            }
        }
    }

    /// <summary>
    /// Väntar på att fler frames finns att läsa. Returnerar false (istället för att
    /// kasta) om token avbryts eller kanalen stängs — så att läs-loopen avslutas rent.
    /// </summary>
    private async Task<bool> WaitToReadSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync(CancellationToken.None).ConfigureAwait(false);

        _captureLoopCts?.Dispose();
        _reader.Dispose();
        _d3d.Dispose();
    }
}
