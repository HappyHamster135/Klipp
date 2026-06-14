using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Klipp.Capture.Models;
using Klipp.Core.Abstractions;
using Klipp.Core.Enums;
using Klipp.Core.Models;
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
public sealed class WgcCaptureSource : ICaptureSource<VideoFrame>
{
    private readonly CaptureTarget _target;
    private readonly D3D11Device _d3d;
    private readonly FrameReader _reader;
    private readonly Channel<VideoFrame> _channel;

    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private CancellationTokenSource? _captureLoopCts;
    private Task? _captureLoopTask;
    private Stopwatch? _clock;
    private bool _disposed;

    /// <inheritdoc/>
    public bool IsCapturing { get; private set; }

    /// <summary>
    /// Skapar en ny capture-source för givet target.
    /// </summary>
    /// <param name="target">Fönster eller skärm att fånga.</param>
    public WgcCaptureSource(CaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        _target = target;
        _d3d = new D3D11Device();
        _reader = new FrameReader(_d3d);

        // Bounded channel: om konsumenten är långsam, dropp äldsta frame istället för att blockera.
        // Capture > storage > consumer är en typisk producer-consumer-pattern.
        _channel = Channel.CreateBounded<VideoFrame>(new BoundedChannelOptions(capacity: 16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsCapturing) return Task.CompletedTask;

        var item = _target.Kind switch
        {
            CaptureTargetKind.Window => GraphicsCaptureItemFactory.CreateForWindow(_target.Handle),
            CaptureTargetKind.Monitor => GraphicsCaptureItemFactory.CreateForMonitor(_target.Handle),
            _ => throw new NotSupportedException("Okänd target-typ: " + _target.Kind)
        };

        _framePool = Direct3D11CaptureFramePool.Create(
            _d3d.WinRTDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 2,
            item.Size);

        _session = _framePool.CreateCaptureSession(item);
        _clock = Stopwatch.StartNew();

        _captureLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _captureLoopTask = Task.Run(() => CaptureLoopAsync(_captureLoopCts.Token), CancellationToken.None);

        _session.StartCapture();
        IsCapturing = true;

        return Task.CompletedTask;
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        var pool = _framePool!;
        var clock = _clock!;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var frame = pool.TryGetNextFrame();
                if (frame is null)
                {
                    // Ingen frame redo än — vänta en bit. Vid 60 FPS kommer en frame var ~16ms.
                    await Task.Delay(5, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var (pixelBytes, width, height) = _reader.ReadPixels(frame.Surface);

                var videoFrame = new VideoFrame(
                    Width: width,
                    Height: height,
                    Format: PixelFormat.Bgra8,
                    Timestamp: clock.ElapsedTicks * 10_000_000L / Stopwatch.Frequency,
                    PixelData: pixelBytes);

                // TryWrite blockerar aldrig — om channel är full dropps äldsta frame.
                _channel.Writer.TryWrite(videoFrame);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown — ignorera.
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!IsCapturing) return;
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
        _clock?.Stop();
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<VideoFrame> ReadSamplesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await foreach (var frame in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync(CancellationToken.None).ConfigureAwait(false);

        _captureLoopCts?.Dispose();
        _d3d.Dispose();
    }
}
