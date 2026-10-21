using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Klipp.Capture.Models;
using Klipp.Capture.Video;
using Klipp.Core.Models;
using Klipp.Desktop;
using Vortice;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        try { using var game = new FakeGame(args); return game.Run(); }
        catch (Exception error) { Console.WriteLine($"FAIL: {error}"); return 1; }
    }
}

internal sealed class FakeGame : IDisposable
{
    private const string ClassName = "Klipp.DxgiExclusiveFullscreenQA";
    private readonly WindowProc _procedure;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CaptureNotificationOverlay _popup = new();
    private readonly nint _instance = GetModuleHandle(null);
    private nint _window;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11DeviceContext1? _context1;
    private IDXGIFactory1? _factory;
    private IDXGISwapChain? _swapChain;
    private ID3D11RenderTargetView? _view;
    private bool _resizing;
    private bool _transitioning;
    private bool _running = true;
    private bool _disposed;
    private bool _reportedFullscreen;
    private bool _paused;
    private bool _hasPainted;
    private readonly bool _partial, _dirty;
    private int _width = 1100, _height = 680;
    private int _forcedWidth, _forcedHeight;
    private long _frames, _clicks, _keys;
    private double _nextReport;
    private long _popupUntil;
    private CaptureNotificationBounds _popupBounds;
    private WgcCaptureSource? _capture;
    private CancellationTokenSource? _captureCancellation;
    private Task? _captureTask;
    private long _capturedFrames, _blackFrames, _capturePopupFrames, _captureMismatches, _maximumGapMicroseconds;

    public FakeGame(string[] args)
    {
        _partial = args.Contains("--partial"); _dirty = args.Contains("--dirty");
        var effect = args.Contains("--flip-sequential") ? SwapEffect.FlipSequential :
            args.Contains("--sequential") ? SwapEffect.Sequential :
            args.Contains("--flip-discard") ? SwapEffect.FlipDiscard : SwapEffect.Discard;
        SetThreadDpiAwarenessContext(new nint(-4));
        _procedure = Procedure;
        var cls = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = _procedure,
            Instance = _instance, ClassName = ClassName, Cursor = LoadCursor(0, new nint(32512))
        };
        if (RegisterClassEx(ref cls) == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        _window = CreateWindowEx(0x00040000, ClassName,
            "Klipp DXGI fullscreen QA | X: exclusive request | P: popup | G: capture probe | Esc: exit",
            0x00CF0000, 100, 100, _width, _height, 0, 0, _instance, 0);
        if (_window == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        GetClientRect(_window, out var client);
        _width = Math.Max(1, client.Right); _height = Math.Max(1, client.Bottom);
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out _device, out _context).CheckError();
        _context1 = _context.QueryInterface<ID3D11DeviceContext1>();
        _factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        _swapChain = _factory.CreateSwapChain(_device, new SwapChainDescription
        {
            BufferDescription = new ModeDescription((uint)_width, (uint)_height, new Rational(0, 1), Format.B8G8R8A8_UNorm),
            SampleDescription = new SampleDescription(1, 0), BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2, OutputWindow = _window, Windowed = true, SwapEffect = effect,
            Flags = SwapChainFlags.None
        });
        _factory.MakeWindowAssociation(_window, WindowAssociationFlags.IgnoreAll).CheckError();
        CreateView();
        ShowWindow(_window, 4); // No activation; Sky/user activates the fake game explicitly.
        Console.WriteLine("READY: activate 'Klipp DXGI fullscreen QA', then X requests fullscreen, P shows popup, G toggles capture probe, Escape exits.");
        Console.WriteLine("CAUTION: DXGI TRUE may be intercepted by Fullscreen Optimizations. No physical-exclusive/overlay-visibility guarantee is inferred.");
        Console.WriteLine($"Swap effect={effect}; partial repaint={_partial}; dirty Present1={_dirty}. Space pauses/resumes presentation.");
    }

    public int Run()
    {
        while (_running)
        {
            while (PeekMessage(out var message, 0, 0, 0, 1))
            {
                if (message.Message == 0x0012) { _running = false; break; }
                TranslateMessage(ref message); DispatchMessage(ref message);
            }
            if (!_running) break;
            if (_resizing && !_transitioning) Resize();
            if (!IsIconic(_window) && !_paused) Render();
            else Thread.Sleep(25);
            if (_popupUntil != 0 && DateTime.UtcNow.Ticks >= _popupUntil)
            {
                _popup.Hide(); _popupUntil = 0;
                Report("Popup dismissed");
            }
            if (_clock.Elapsed.TotalSeconds >= _nextReport) { _nextReport = _clock.Elapsed.TotalSeconds + 1; Report("Tick"); }
        }
        return 0;
    }

    private void Render()
    {
        if (_view is null || _context is null || _context1 is null || _swapChain is null) return;
        var firstPaint = !_hasPainted;
        if (!_partial || !_hasPainted) _context.ClearRenderTargetView(_view, new Color4(26 / 255f, 45 / 255f, 65 / 255f, 1));
        _hasPainted = true;
        var size = Math.Clamp(_width / 12, 30, 140);
        var x = (int)((Math.Sin(_clock.Elapsed.TotalSeconds * 1.3) + 1) * .5 * Math.Max(1, _width - size));
        var y = (int)(_height * .70);
        _context1.ClearView(_view, new Color4(.3f, .76f, .65f, 1),
            new[] { new RawRect(x, y, x + size, Math.Min(_height, y + size)) });
        // Initialize the complete presentation surface after creation/resize before
        // requesting dirty rectangles, whose unpainted pixels must already exist.
        var result = _dirty && !firstPaint ? DirtyPresent(x, y, size) : _swapChain.Present(1, PresentFlags.None);
        if (result.Failure) { Console.WriteLine($"Present failed: {result}"); _running = false; }
        if (result.Code == 0x087A0001 || GetForegroundWindow() != _window) Thread.Sleep(15); // DXGI_STATUS_OCCLUDED
        _frames++;
    }

    private SharpGen.Runtime.Result DirtyPresent(int x, int y, int size)
    {
        using var chain1 = _swapChain!.QueryInterface<IDXGISwapChain1>();
        var rect = new RawRect(x, y, x + size, Math.Min(_height, y + size));
        return chain1.Present1(1, PresentFlags.None, new PresentParameters { DirtyRectangles = new[] { rect } });
    }

    private void CreateView()
    {
        using var buffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _view = _device!.CreateRenderTargetView(buffer);
        var description = buffer.Description;
        _width = (int)description.Width; _height = (int)description.Height;
    }

    private void Resize()
    {
        _resizing = false;
        if (_swapChain is null || IsIconic(_window)) return;
        GetClientRect(_window, out var client);
        if (client.Right <= 0 || client.Bottom <= 0) return;
        _view?.Dispose(); _view = null;
        _context!.ClearState(); _context.Flush();
        var width = _forcedWidth > 0 ? _forcedWidth : client.Right;
        var height = _forcedHeight > 0 ? _forcedHeight : client.Bottom;
        _swapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        CreateView();
        _hasPainted = false;
    }

    private void ToggleFullscreen()
    {
        if (_swapChain is null || GetForegroundWindow() != _window)
        { Console.WriteLine("Fullscreen request ignored: fake game must own foreground focus."); return; }
        _transitioning = true;
        try
        {
            _swapChain.GetFullscreenState(out var state, out var output).CheckError();
            output?.Dispose();
            var before = GetForegroundWindow();
            var result = _swapChain.SetFullscreenState(!state, null);
            Console.WriteLine($"SetFullscreenState({!state}) => {result}; foreground unchanged={GetForegroundWindow() == before}");
            _swapChain.GetFullscreenState(out var after, out var afterOutput).CheckError();
            afterOutput?.Dispose();
            _reportedFullscreen = after;
            _resizing = true;
            Report("Fullscreen request completed (API state only)");
        }
        finally { _transitioning = false; }
    }

    private void ShowPopup()
    {
        if (GetForegroundWindow() != _window) return;
        var before = GetForegroundWindow();
        var monitor = MonitorFromWindow(_window, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var scale = GetDpiForWindow(_window) / 96d;
        _popupBounds = CaptureNotificationPlacement.UpperLeftEdge(info.Monitor.Left, info.Monitor.Top,
            info.Monitor.Right - info.Monitor.Left, info.Monitor.Bottom - info.Monitor.Top, scale);
        _popup.Show(CaptureNotificationMessage.Create(CaptureNotificationKind.ClipSaved,
            "Exclusive fullscreen diagnostic", "This tests the API path; physical overlay visibility remains unproven.", DateTimeOffset.UtcNow, 6),
            _popupBounds.Left, _popupBounds.Top, _popupBounds.Width, _popupBounds.Height, scale);
        Interlocked.Exchange(ref _popupUntil, DateTime.UtcNow.AddSeconds(6).Ticks);
        Console.WriteLine($"Popup HWND visible={_popup.IsVisible}, capture exclusion={_popup.IsExcludedFromCapture}, " +
            $"game focus retained={GetForegroundWindow() == before}. This is NOT a physical scanout visibility assertion.");
        Report("Popup shown");
    }

    private void ToggleCaptureProbe()
    {
        if (_capture is not null) { StopCaptureProbe(); Report("Capture probe stopped"); return; }
        _capturedFrames = _blackFrames = _capturePopupFrames = _captureMismatches = _maximumGapMicroseconds = 0;
        _captureCancellation = new CancellationTokenSource();
        _capture = new WgcCaptureSource(new CaptureTarget { Kind = CaptureTargetKind.Window,
            Handle = _window, DisplayName = "Disposable DXGI fake game" }, 15, hideCaptureBorder: true, captureCursor: false);
        _capture.StartAsync().GetAwaiter().GetResult();
        var source = _capture; var token = _captureCancellation.Token;
        _captureTask = Task.Run(async () =>
        {
            var previous = Stopwatch.GetTimestamp();
            try
            {
                await foreach (var frame in source.ReadSamplesAsync(token))
                {
                    var now = Stopwatch.GetTimestamp();
                    var gap = (long)((now - previous) * 1_000_000d / Stopwatch.Frequency); previous = now;
                    InterlockedExtensions.Max(ref _maximumGapMicroseconds, gap);
                    Interlocked.Increment(ref _capturedFrames);
                    InspectFrame(frame);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { Console.WriteLine($"Capture probe ended: {error.Message}"); }
        });
        Console.WriteLine("Capture probe started: own fake-game window only, in memory; no media file is saved.");
    }

    private void InspectFrame(VideoFrame frame)
    {
        var bytes = frame.PixelData.Span;
        if (bytes.Length < 4 || frame.Width < 4 || frame.Height < 4) return;
        var offset = (frame.Height / 4 * frame.Width + frame.Width / 4) * 4;
        if (offset + 3 >= bytes.Length) return;
        if (bytes[offset] < 4 && bytes[offset + 1] < 4 && bytes[offset + 2] < 4) Interlocked.Increment(ref _blackFrames);
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _popupUntil))
        {
            Interlocked.Increment(ref _capturePopupFrames);
            if (Math.Abs(bytes[offset] - 65) > 8 || Math.Abs(bytes[offset + 1] - 45) > 8 || Math.Abs(bytes[offset + 2] - 26) > 8)
                Interlocked.Increment(ref _captureMismatches);
        }
    }

    private void StopCaptureProbe()
    {
        _captureCancellation?.Cancel();
        if (_captureTask is not null) _captureTask.GetAwaiter().GetResult();
        _capture?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _captureCancellation?.Dispose(); _captureCancellation = null; _captureTask = null; _capture = null;
    }

    private void Report(string reason)
    {
        if (_swapChain is null) return;
        _swapChain.GetFullscreenState(out var state, out var output);
        output?.Dispose(); _reportedFullscreen = state;
        var query = SHQueryUserNotificationState(out var notificationState);
        var focus = GetForegroundWindow() == _window;
        Console.WriteLine($"{reason}: DXGI fullscreen={_reportedFullscreen}, foreground={focus}, " +
            $"shellState={(query >= 0 ? notificationState : -1)}, frames={_frames}, keys={_keys}, clicks={_clicks}; " +
            $"WGC frames={Interlocked.Read(ref _capturedFrames)}, black={Interlocked.Read(ref _blackFrames)}, " +
            $"maxGapMs={Interlocked.Read(ref _maximumGapMicroseconds) / 1000d:F1}, popupFrames={Interlocked.Read(ref _capturePopupFrames)}, " +
            $"popupBackgroundMismatches={Interlocked.Read(ref _captureMismatches)}.");
        SetWindowText(_window, $"Klipp DXGI fullscreen QA | DXGI={_reportedFullscreen} Focus={focus} Paused={_paused} Frames={_frames} Clicks={_clicks} | X fullscreen P popup G probe Space pause Esc exit");
    }

    private nint Procedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (message == 0x0005) { _resizing = true; return 0; }
            // Private diagnostics, accepted only by this disposable fake game. The
            // separate test host never synthesizes input for another application.
            if (message == 0x8051) { _paused = wParam != 0; Report(_paused ? "Presentation paused" : "Presentation resumed"); return 0; }
            if (message == 0x8052)
            {
                _forcedWidth = Math.Clamp(wParam.ToInt32(), 320, 3840); _forcedHeight = Math.Clamp(lParam.ToInt32(), 180, 2160);
                _resizing = true; Report("Backbuffer resize requested by disposable test host"); return 0;
            }
            if (message == 0x0201) { _clicks++; Report("Game received pointer input"); return 0; }
            if (message == 0x0100 && ((lParam.ToInt64() >> 30) & 1) == 0)
            {
                _keys++;
                switch (wParam.ToInt32())
                {
                    case 0x58: ToggleFullscreen(); break;
                    case 0x50: ShowPopup(); break;
                    case 0x47: ToggleCaptureProbe(); break;
                    case 0x20: _paused = !_paused; Report(_paused ? "Presentation paused" : "Presentation resumed"); break;
                    case 0x1B: _running = false; break;
                }
                return 0;
            }
            if (message == 0x0010) { _running = false; return 0; }
        }
        catch (Exception error) { Console.WriteLine($"Diagnostic action failed safely: {error}"); }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        try { StopCaptureProbe(); }
        catch (Exception error) { Console.WriteLine($"Capture probe cleanup: {error.Message}"); }
        _popup.Dispose();
        try { _swapChain?.SetFullscreenState(false, null); } catch { }
        _view?.Dispose(); _swapChain?.Dispose(); _factory?.Dispose();
        _context1?.Dispose(); _context?.Dispose(); _device?.Dispose();
        if (_window != 0) DestroyWindow(_window);
        UnregisterClass(ClassName, _instance);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    { public uint Size, Style; public WindowProc Procedure; public int ClassExtra, WindowExtra; public nint Instance, Icon, Cursor, Background; public string? MenuName, ClassName; public nint SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeMessage
    { public nint Window; public uint Message; public nuint WParam; public nint LParam; public uint Time; public NativePoint Point; public uint Private; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass cls);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string name, nint instance);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string cls, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] private static extern nint LoadCursor(nint instance, nint name);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "PeekMessageW")] private static extern bool PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out NativeRect bounds);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint awareness);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(nint hwnd, string text);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
}

internal static class InterlockedExtensions
{
    internal static void Max(ref long location, long value)
    {
        var current = Interlocked.Read(ref location);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current) return;
            current = observed;
        }
    }
}
