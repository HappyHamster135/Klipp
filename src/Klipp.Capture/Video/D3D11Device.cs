using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;
using Windows.Graphics.DirectX.Direct3D11;

namespace Klipp.Capture.Video;

/// <summary>
/// Hanterar livscykeln för en D3D11-device och dess WinRT-motsvarighet
/// (<see cref="IDirect3DDevice"/>) som WGC kräver.
/// </summary>
/// <remarks>
/// WGC är en WinRT-API, så den vill ha en IDirect3DDevice — inte en
/// vanlig ID3D11Device. Vi bygger båda från samma underliggande DXGI device,
/// så de delar GPU-resurser och kan kommunicera med varandra.
/// </remarks>
internal sealed class D3D11Device : IDisposable
{
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDirect3DDevice WinRTDevice { get; }

    private bool _disposed;

    public D3D11Device()
    {
        var featureLevels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };

        var result = D3D11.D3D11CreateDevice(
            adapter: null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            featureLevels,
            out ID3D11Device device,
            out ID3D11DeviceContext context);

        if (result.Failure)
            throw new InvalidOperationException("D3D11CreateDevice misslyckades.");

        Device = device;
        Context = context;
        WinRTDevice = CreateWinRTDevice(device);
    }

    private static IDirect3DDevice CreateWinRTDevice(ID3D11Device d3dDevice)
    {
        using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var graphicsDevicePtr);
        if (hr < 0)
            throw new InvalidOperationException("CreateDirect3D11DeviceFromDXGIDevice misslyckades.");

        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(graphicsDevicePtr);
        }
        finally
        {
            Marshal.Release(graphicsDevicePtr);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WinRTDevice.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}
