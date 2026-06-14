using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;
using Windows.Graphics.DirectX.Direct3D11;

namespace Klipp.Capture.Video;

/// <summary>
/// Läser ut pixel-bytes från en D3D11-textur som ligger i GPU-minne.
/// Återanvänds av både <see cref="SingleFrameGrabber"/> och <see cref="WgcCaptureSource"/>.
/// </summary>
internal sealed class FrameReader
{
    private readonly D3D11Device _d3d;

    public FrameReader(D3D11Device d3d)
    {
        _d3d = d3d;
    }

    /// <summary>
    /// Kopierar pixel-data från en GPU-textur till en tightly-packed byte-array (ingen rad-padding).
    /// Returnerar bytes plus textur-storleken (kan skilja sig från fönsterstorleken).
    /// </summary>
    public (byte[] PixelBytes, int Width, int Height) ReadPixels(IDirect3DSurface surface)
    {
        using var d3dTexture = GetD3D11Texture2D(surface);
        var desc = d3dTexture.Description;

        var stagingDesc = new Texture2DDescription
        {
            Width = desc.Width,
            Height = desc.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None
        };

        using var staging = _d3d.Device.CreateTexture2D(stagingDesc);
        _d3d.Context.CopyResource(staging, d3dTexture);

        var mapped = _d3d.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var width = (int)desc.Width;
            var height = (int)desc.Height;
            var rowPitch = (int)mapped.RowPitch;
            var widthBytes = width * 4;
            var pixelBytes = new byte[widthBytes * height];
            for (int row = 0; row < height; row++)
            {
                var srcPtr = nint.Add(mapped.DataPointer, row * rowPitch);
                Marshal.Copy(srcPtr, pixelBytes, row * widthBytes, widthBytes);
            }
            return (pixelBytes, width, height);
        }
        finally
        {
            _d3d.Context.Unmap(staging, 0);
        }
    }

    private static ID3D11Texture2D GetD3D11Texture2D(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = typeof(ID3D11Texture2D).GUID;
        var ptr = access.GetInterface(ref iid);
        return new ID3D11Texture2D(ptr);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface([In] ref Guid iid);
    }
}
