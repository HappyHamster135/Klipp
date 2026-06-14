using Klipp.Capture.Models;

using Vortice.Direct3D11;

using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Klipp.Capture.Video;

/// <summary>
/// Fångar EN frame från ett <see cref="CaptureTarget"/> och sparar som PNG.
/// Används som "smoke test" innan vi bygger kontinuerlig capture.
/// </summary>
public sealed class SingleFrameGrabber : IDisposable
{
    private readonly D3D11Device _d3d;
    private readonly FrameReader _reader;
    private bool _disposed;

    public SingleFrameGrabber()
    {
        _d3d = new D3D11Device();
        _reader = new FrameReader(_d3d);
    }

    /// <summary>
    /// Tar en frame från <paramref name="target"/> och sparar till <paramref name="outputPath"/>.
    /// </summary>
    public async Task GrabFrameAsync(CaptureTarget target, string outputPath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var item = target.Kind switch
        {
            CaptureTargetKind.Window => GraphicsCaptureItemFactory.CreateForWindow(target.Handle),
            CaptureTargetKind.Monitor => GraphicsCaptureItemFactory.CreateForMonitor(target.Handle),
            _ => throw new NotSupportedException("Okänd target-typ: " + target.Kind)
        };

        using var framePool = Direct3D11CaptureFramePool.Create(
            _d3d.WinRTDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            numberOfBuffers: 1,
            item.Size);

        using var session = framePool.CreateCaptureSession(item);
        session.StartCapture();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        Direct3D11CaptureFrame? capturedFrame = null;
        while (!cts.Token.IsCancellationRequested)
        {
            capturedFrame = framePool.TryGetNextFrame();
            if (capturedFrame is not null) break;
            await Task.Delay(10, cts.Token).ConfigureAwait(false);
        }

        if (capturedFrame is null)
            throw new TimeoutException("Ingen frame mottagen inom 5 sekunder. Är fönstret minimerat?");

        using (capturedFrame)
        {
            var (pixelBytes, width, height) = _reader.ReadPixels(capturedFrame.Surface);
            await WritePngAsync(outputPath, (uint)width, (uint)height, pixelBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WritePngAsync(string path, uint width, uint height, byte[] bgraPixels, CancellationToken cancellationToken)
    {
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask().ConfigureAwait(false);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite).AsTask().ConfigureAwait(false);

        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask().ConfigureAwait(false);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            width,
            height,
            dpiX: 96.0,
            dpiY: 96.0,
            pixels: bgraPixels);

        await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _d3d.Dispose();
    }
}