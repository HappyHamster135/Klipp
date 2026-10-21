using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Klipp.Capture.GameCapture;

/// <summary>Reads private GPU textures on the game's adapter. No game-thread CPU readback.</summary>
internal sealed class SharedGameFrames : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D[] _textures = new ID3D11Texture2D[3];
    private readonly IDXGIKeyedMutex[] _mutexes = new IDXGIKeyedMutex[3];
    private ID3D11Texture2D? _staging;
    private readonly ulong[] _seen = new ulong[3];
    public uint Generation { get; }

    public SharedGameFrames(GameHookAttachment attachment, GameHookProtocol.FrameInfo frame)
    {
        Generation = frame.Generation;
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory4>();
        using var adapter = factory.EnumAdapterByLuid<IDXGIAdapter1>((Luid)(((long)frame.AdapterHigh << 32) | frame.AdapterLow));
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out _device, out _context).CheckError();
        try
        {
            using var device1 = _device.QueryInterface<ID3D11Device1>();
            for (var i = 0; i < 3; i++)
            {
                using var handle = attachment.DuplicateTexture(frame.Handles[i]);
                _textures[i] = device1.OpenSharedResource1<ID3D11Texture2D>(handle.DangerousGetHandle());
                var desc = _textures[i].Description;
                if (desc.Width != frame.Width || desc.Height != frame.Height || (uint)desc.Format != frame.Format || desc.SampleDescription.Count != 1)
                    throw new InvalidDataException("The private game texture does not match its description.");
                _mutexes[i] = _textures[i].QueryInterface<IDXGIKeyedMutex>();
            }
            _staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)frame.Width, Height = (uint)frame.Height, MipLevels = 1, ArraySize = 1,
                Format = (Format)frame.Format, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read
            });
        }
        catch { Dispose(); throw; }
    }

    public (byte[] Pixels, long Timestamp, ulong Sequence)? ReadLatest(GameHookProtocol.FrameInfo frame)
    {
        var newest = -1;
        for (var i = 0; i < 3; i++) if (frame.Sequence[i] > _seen[i] && (newest < 0 || frame.Sequence[i] > frame.Sequence[newest])) newest = i;
        // Release superseded slots without a GPU readback so rendering can keep moving.
        for (var i = 0; i < 3; i++)
        {
            if (i == newest || frame.Sequence[i] == 0 || frame.Sequence[i] <= _seen[i]) continue;
            if (!TryAcquire(_mutexes[i], frame.Sequence[i])) continue;
            _seen[i] = frame.Sequence[i]; _mutexes[i].ReleaseSync(0);
        }
        if (newest < 0 || !TryAcquire(_mutexes[newest], frame.Sequence[newest])) return null;
        try
        {
            _context.CopyResource(_staging!, _textures[newest]);
            var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var stride = checked(frame.Width * 4);
                if (mapped.DataPointer == 0 || mapped.RowPitch < stride || mapped.RowPitch > int.MaxValue)
                    throw new InvalidDataException("The game texture could not be read safely.");
                var pixels = new byte[checked(stride * frame.Height)];
                for (var row = 0; row < frame.Height; row++)
                    Marshal.Copy(nint.Add(mapped.DataPointer, checked(row * (int)mapped.RowPitch)), pixels, row * stride, stride);
                if (frame.Format == 28)
                    for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                _seen[newest] = frame.Sequence[newest];
                return (pixels, frame.Timestamp[newest], frame.Sequence[newest]);
            }
            finally { _context.Unmap(_staging!, 0); }
        }
        finally { _mutexes[newest].ReleaseSync(0); }
    }

    private static bool TryAcquire(IDXGIKeyedMutex mutex, ulong key)
    {
        // Vortice's void projection drops positive WAIT_TIMEOUT/WAIT_ABANDONED results.
        // Read the native HRESULT: only S_OK grants ownership of this exact frame.
        var table = Marshal.ReadIntPtr(mutex.NativePointer);
        var acquire = Marshal.GetDelegateForFunctionPointer<AcquireSync>(Marshal.ReadIntPtr(table, 8 * nint.Size));
        var result = acquire(mutex.NativePointer, key, 0);
        if (result < 0) Marshal.ThrowExceptionForHR(result);
        return result == 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AcquireSync(nint self, ulong key, uint milliseconds);

    public void Dispose()
    {
        _staging?.Dispose(); _staging = null;
        for (var i = 0; i < 3; i++) { _mutexes[i]?.Dispose(); _textures[i]?.Dispose(); }
        _context?.Dispose(); _device?.Dispose();
    }
}
