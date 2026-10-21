using System.Runtime.InteropServices;
using System.Text;
using Klipp.Capture.Models;
using Klipp.Capture.Video;
using Klipp.Core.Models;
using Klipp.Desktop;
using Klipp.Desktop.Services;
using Microsoft.UI.Dispatching;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;

/// <summary>
/// A temporary, solid-color borderless fullscreen target verifies the real desktop
/// composition/capture path. It deliberately does not claim DXGI exclusive support.
/// No application settings, projects or persistent media are read or written.
/// </summary>
internal static class CaptureNotificationFullscreenChecks
{
    private static readonly string ArtifactDirectory = Path.Combine(Path.GetTempPath(), $"Klipp-popup-fullscreen-{Guid.NewGuid():N}");
    public static async Task RunAsync(CaptureNotificationWindow popup)
    {
        var foreground = GetForegroundWindow();
        var monitor = MonitorFromWindow(foreground, 2);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        Require(GetMonitorInfo(monitor, ref monitorInfo), "Cannot inspect the fullscreen test monitor.");
        using var target = await NativeFullscreenTarget.OpenAsync(monitorInfo.Monitor);
        try
        {
            await Task.Delay(260);
            Require(GetForegroundWindow() == foreground, "The passive fullscreen QA target changed focus.");
            Console.WriteLine($"Fullscreen target geometry: {monitorInfo.Monitor.Left},{monitorInfo.Monitor.Top},{monitorInfo.Monitor.Right},{monitorInfo.Monitor.Bottom}; target DPI={GetDpiForWindow(target.Handle)}.");
            Console.WriteLine("READY: activate the window 'Klipp borderless fullscreen notification QA' to start the focused game simulation.");
            var activationDeadline = DateTime.UtcNow.AddSeconds(60);
            while (GetForegroundWindow() != target.Handle && DateTime.UtcNow < activationDeadline)
                await Task.Delay(100);
            Require(GetForegroundWindow() == target.Handle, "The fullscreen QA target was not activated within 60 seconds.");
            foreground = target.Handle;
            var context = CaptureNotificationContext.Read();
            Require(context.FullScreen && context.Passive && context.ForegroundWindow == target.Handle,
                "Automatic notification context did not recognize the foreground fullscreen target.");
            using var service = new CaptureNotificationService(DispatcherQueue.GetForCurrentThread());
            await using var capture = new WgcCaptureSource(new CaptureTarget
            {
                Kind = CaptureTargetKind.Monitor, Handle = monitor,
                DisplayName = "Disposable notification fullscreen monitor"
            }, maxFrameRate: 12, hideCaptureBorder: false, captureCursor: false);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await capture.StartAsync(timeout.Token);
            await using var frames = capture.ReadSamplesAsync(timeout.Token).GetAsyncEnumerator();
            var before = await ReadFreshFrameAsync(frames);

            // A direct fixture gives the human/Sky input phase time to arrive.
            // Product preferences and Create() continue to clamp to 2–10 seconds.
            service.Show(new CaptureNotificationMessage(CaptureNotificationKind.ClipSaved,
                "Clip saved", "Fullscreen test · 30 seconds · gameplay.mp4", DateTimeOffset.UtcNow, 60));
            await Task.Delay(200);
            var activePopup = typeof(CaptureNotificationService).GetField("_window",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(service)
                as CaptureNotificationWindow;
            Require(activePopup is not null, "The fullscreen event did not create its real service-owned popup.");
            var hwnd = PresentationHandle(activePopup!);
            Require(IsWindowVisible(hwnd), "The passive fullscreen popup HWND was not visible.");
            Require(GetForegroundWindow() == foreground, "The fullscreen popup changed foreground focus.");
            var extended = GetWindowLongPtr(hwnd, -20).ToInt64();
            Require((extended & 0x080800A8) == 0x080800A8,
                "Passive fullscreen popup must be layered, transparent, topmost, tool-only and nonactivating.");
            Require(GetWindowThreadProcessId(target.Handle, out _) != GetWindowThreadProcessId(hwnd, out _),
                "The hit-test target must be on a different native UI thread.");
            Require(GetWindowRect(hwnd, out var bounds), "Cannot inspect fullscreen popup bounds.");
            Require(bounds.Left >= monitorInfo.Monitor.Left && bounds.Top >= monitorInfo.Monitor.Top &&
                bounds.Right <= monitorInfo.Monitor.Right && bounds.Bottom <= monitorInfo.Monitor.Bottom,
                "The fullscreen popup extends outside the game monitor.");
            CheckEdgePlacement(hwnd, bounds, monitorInfo.Monitor);
            CheckClientFrame(hwnd, bounds);
            Require(GetWindowDisplayAffinity(hwnd, out var affinity) && affinity == 0x11,
                "The active passive popup HWND was not excluded from capture.");
            await CheckRenderedContentAsync(activePopup!, "passive-game-popup.png");
            var point = new NativePoint { X = (bounds.Left + bounds.Right) / 2, Y = (bounds.Top + bounds.Bottom) / 2 };
            var hit = WindowFromPoint(point);
            Console.WriteLine($"Fullscreen hit diagnostic: target={target.Handle:X}, popup={hwnd:X}, hit={hit:X}, point={point.X},{point.Y}, popupBounds={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}, monitor={monitorInfo.Monitor.Left},{monitorInfo.Monitor.Top},{monitorInfo.Monitor.Right},{monitorInfo.Monitor.Bottom}, hitClass={WindowClassName(hit)}");
            var during = await ReadFreshFrameAsync(frames);
            Require(IsWindowVisible(hwnd), "The popup disappeared before its WGC capture verification.");
            VerifyUnchangedCapturedPixels(before, during, bounds, monitorInfo.Monitor);
            Require(hit == target.Handle,
                "Hit testing did not pass through to the separate-thread fullscreen target.");
            await capture.StopAsync();
            var targetDpi = Math.Max(96u, GetDpiForWindow(target.Handle));
            var logicalX = (int)Math.Round((point.X - monitorInfo.Monitor.Left) * 96d / targetDpi);
            var logicalY = (int)Math.Round((point.Y - monitorInfo.Monitor.Top) * 96d / targetDpi);
            Console.WriteLine($"READY CLICK (45 seconds): click the fullscreen QA window at logical {logicalX},{logicalY} (physical {point.X - monitorInfo.Monitor.Left},{point.Y - monitorInfo.Monitor.Top}); the excluded passive popup is over that point. This verifies real input reaches the fake game.");
            var clickDeadline = DateTime.UtcNow.AddSeconds(45);
            while (target.ClickCount == 0 && DateTime.UtcNow < clickDeadline)
                await Task.Delay(100);
            Require(target.ClickCount > 0, "The fullscreen QA target did not receive a real click beneath the popup.");
            Require(IsWindowVisible(hwnd) && GetForegroundWindow() == target.Handle,
                "The real click under the popup interrupted game focus or arrived after dismissal.");
            Console.WriteLine("PASS: actual pointer input reached the separate-thread fake game through the visible passive popup");
            service.Show(CaptureNotificationMessage.Create(CaptureNotificationKind.ClipSaved,
                "Clip saved", "Input pass-through confirmed", DateTimeOffset.UtcNow, 2));
            await Task.Delay(2300);
            Require(!IsWindowVisible(hwnd), "The fullscreen notification did not auto-dismiss.");
            Require(GetForegroundWindow() == foreground, "The fullscreen popup or its dismissal stole game focus.");
            Console.WriteLine("PASS: automatic borderless fullscreen context, popup placement, no native frame, cross-thread click-through, game foreground preservation, auto-dismissal, and actual WGC pixels beneath popup");
            service.Dispose();
        }
        finally { popup.HideNotification(); }
    }

    public static async Task CheckNormalPresentationAsync(CaptureNotificationWindow popup)
    {
        var foreground = GetForegroundWindow();
        try
        {
            popup.ShowNotification(CaptureNotificationMessage.Create(CaptureNotificationKind.Preview,
                "Notification preview", "A clean, borderless popup stays out of your recording.", DateTimeOffset.UtcNow, 2));
            await Task.Delay(260);
            var hwnd = PresentationHandle(popup);
            Require(GetWindowRect(hwnd, out var bounds), "Cannot inspect normal popup bounds.");
            CheckClientFrame(hwnd, bounds);
            var monitor = MonitorFromWindow(hwnd, 2);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            Require(GetMonitorInfo(monitor, ref info), "Cannot inspect the popup's display work area.");
            CheckEdgePlacement(hwnd, bounds, info.Work);
            await CheckRenderedContentAsync(popup, "interactive-popup.png");
            Require(GetForegroundWindow() == foreground, "Showing the interactive preview activated Klipp.");
        }
        finally { popup.HideNotification(); await Task.Delay(260); }
    }

    private static nint PresentationHandle(CaptureNotificationWindow window) =>
        (nint?)typeof(CaptureNotificationWindow).GetProperty("VisibleHandle",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(window)
        ?? WinRT.Interop.WindowNative.GetWindowHandle(window);

    private static string WindowClassName(nint hwnd)
    {
        var result = new StringBuilder(256);
        GetClassName(hwnd, result, result.Capacity);
        return result.ToString();
    }

    private static async Task CheckRenderedContentAsync(CaptureNotificationWindow popup, string artifactName)
    {
        var type = typeof(CaptureNotificationWindow);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var pixels = type.GetProperty("PassiveRenderedPixels", flags)?.GetValue(popup) as byte[];
        var width = type.GetProperty("PassiveRenderedWidth", flags)?.GetValue(popup) as int? ?? 0;
        var height = type.GetProperty("PassiveRenderedHeight", flags)?.GetValue(popup) as int? ?? 0;
        Require(pixels is not null && width > 0 && height > 0 && pixels.Length == width * height * 4,
            "The native renderer did not produce its own complete BGRA card.");
        var bright = 0;
        var opaque = 0;
        var translucent = 0;
        var transparent = 0;
        for (var index = 0; index < pixels!.Length; index += 4)
        {
            if (pixels[index + 3] > 240) opaque++;
            if (pixels[index + 3] is >= 187 and <= 195) translucent++;
            if (pixels[index + 3] == 0) transparent++;
            if (pixels[index + 3] > 240 && pixels[index] > 170 && pixels[index + 1] > 170 && pixels[index + 2] > 170) bright++;
            Require(pixels[index] <= pixels[index + 3] && pixels[index + 1] <= pixels[index + 3] && pixels[index + 2] <= pixels[index + 3],
                "The popup bitmap contains invalid, non-premultiplied color channels.");
        }
        var highContrast = new AccessibilitySettings().HighContrast;
        Require(bright > 25 && opaque > 25, "The native card is missing readable, opaque text.");
        Require(highContrast ? opaque > width * height / 2 : translucent > width * height / 2,
            highContrast ? "The high-contrast card did not retain a solid background."
                : "The popup background is not approximately 75 percent opaque.");
        Require(transparent > 0, "The native card's rounded corners are not transparent.");
        var topCenter = (width / 2) * 4;
        Require(highContrast || pixels[topCenter] < 100 && pixels[topCenter + 1] < 100 && pixels[topCenter + 2] < 100,
            "The native card still has a light bar along its top edge.");
        await SaveOwnRenderedPixelsAsync(artifactName, pixels, width, height);
        Console.WriteLine("PASS: 75-percent-opaque card surface, opaque readable text, premultiplied rounded corners and no light title strip");
    }

    private static void CheckEdgePlacement(nint hwnd, NativeRect bounds, NativeRect display)
    {
        var scale = Math.Max(96u, GetDpiForWindow(hwnd)) / 96d;
        var expected = CaptureNotificationPlacement.UpperLeftEdge(display.Left, display.Top,
            display.Right - display.Left, display.Bottom - display.Top, scale);
        Require(bounds.Left == expected.Left && bounds.Top == expected.Top &&
            bounds.Right - bounds.Left == expected.Width && bounds.Bottom - bounds.Top == expected.Height,
            "The notification is not inset consistently from the upper-left display edge.");
    }

    private static void CheckClientFrame(nint hwnd, NativeRect bounds)
    {
        var styles = GetWindowLongPtr(hwnd, -16).ToInt64();
        Require((styles & 0x00C40000) == 0, $"Native caption/frame styles remain around the popup: 0x{styles:X}.");
        Require(GetClientRect(hwnd, out var client), "Cannot inspect popup client area.");
        var origin = new NativePoint();
        Require(ClientToScreen(hwnd, ref origin), "Cannot inspect popup client origin.");
        Require(origin.X == bounds.Left && origin.Y == bounds.Top &&
            client.Right == bounds.Right - bounds.Left && client.Bottom == bounds.Bottom - bounds.Top,
            "The popup has a nonclient title strip or border outside its content.");
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) &&
            DwmGetWindowAttribute(hwnd, 34, out var borderColor, sizeof(uint)) == 0)
            Require(borderColor == 0xFFFFFFFE, "The popup still uses a visible system outline.");
    }

    private static async Task<VideoFrame> ReadFreshFrameAsync(IAsyncEnumerator<VideoFrame> frames)
    {
        VideoFrame frame = default;
        for (var index = 0; index < 3; index++)
        {
            Require(await frames.MoveNextAsync(), "Windows Graphics Capture stopped before a test frame arrived.");
            frame = frames.Current;
        }
        return frame;
    }

    private static void VerifyUnchangedCapturedPixels(VideoFrame before, VideoFrame during,
        NativeRect popup, NativeRect monitor)
    {
        Require(before.Width == during.Width && before.Height == during.Height &&
            during.Width == monitor.Right - monitor.Left && during.Height == monitor.Bottom - monitor.Top,
            "The monitor capture dimensions changed during the popup.");
        var first = before.PixelData.Span;
        var second = during.PixelData.Span;
        var different = 0;
        var checkedPixels = 0;
        // The complete popup interior is compared, including where text/icon/card
        // would be. An excluded window must reveal the original game pixels,
        // rather than a black rectangle or a color-transformed placeholder.
        for (var y = popup.Top - monitor.Top + 6; y < popup.Bottom - monitor.Top - 6; y += 3)
        for (var x = popup.Left - monitor.Left + 6; x < popup.Right - monitor.Left - 6; x += 3)
        {
            var offset = (y * before.Width + x) * 4;
            Require(Math.Abs(first[offset] - 65) <= 4 && Math.Abs(first[offset + 1] - 45) <= 4 &&
                Math.Abs(first[offset + 2] - 26) <= 4, "The fullscreen QA color did not cover the popup capture area.");
            checkedPixels++;
            if (Math.Abs(first[offset] - second[offset]) > 2 ||
                Math.Abs(first[offset + 1] - second[offset + 1]) > 2 ||
                Math.Abs(first[offset + 2] - second[offset + 2]) > 2) different++;
        }
        Require(checkedPixels > 100 && different == 0,
            $"The fullscreen popup affected captured video pixels ({different}/{checkedPixels}).");
    }

    private static async Task SaveOwnRenderedPixelsAsync(string name, byte[] pixels, int width, int height)
    {
        Directory.CreateDirectory(ArtifactDirectory);
        var path = Path.Combine(ArtifactDirectory, name);
        using (File.Create(path)) { }
        var file = await StorageFile.GetFileFromPathAsync(path);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
        Console.WriteLine($"Own-rendered popup design artifact: {path}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>A native target on its own STA thread avoids same-thread hit-test false positives.</summary>
    private sealed class NativeFullscreenTarget : IDisposable
    {
        private readonly Thread _thread;
        private readonly TaskCompletionSource<nint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly NativeRect _bounds;
        private readonly WindowProc _procedure;
        private readonly string _className = $"KlippFullscreenPopupQA-{Guid.NewGuid():N}";
        private int _clicks;
        public nint Handle { get; private set; }
        public int ClickCount => Volatile.Read(ref _clicks);

        private NativeFullscreenTarget(NativeRect bounds)
        {
            _bounds = bounds;
            _procedure = Procedure;
            _thread = new Thread(Run) { IsBackground = true, Name = "Klipp fullscreen notification QA target" };
            _thread.SetApartmentState(ApartmentState.STA);
        }

        public static async Task<NativeFullscreenTarget> OpenAsync(NativeRect bounds)
        {
            var target = new NativeFullscreenTarget(bounds);
            target._thread.Start();
            target.Handle = await target._ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return target;
        }

        private void Run()
        {
            SetThreadDpiAwarenessContext(new nint(-4)); // PerMonitorV2, matching the app's native windows.
            var brush = CreateSolidBrush(26u | (45u << 8) | (65u << 16));
            var instance = GetModuleHandle(null);
            try
            {
                var windowClass = new WindowClass
                {
                    Size = (uint)Marshal.SizeOf<WindowClass>(), Procedure = _procedure,
                    Instance = instance, Background = brush, ClassName = _className
                };
                if (RegisterClassEx(ref windowClass) == 0) throw new InvalidOperationException("Cannot register fullscreen QA target.");
                Handle = CreateWindowEx(0x00040000, _className, "Klipp borderless fullscreen notification QA",
                    0x80000000, _bounds.Left, _bounds.Top, _bounds.Right - _bounds.Left, _bounds.Bottom - _bounds.Top,
                    0, 0, instance, 0);
                if (Handle == 0) throw new InvalidOperationException($"Cannot create fullscreen QA target: {Marshal.GetLastWin32Error()}.");
                SetWindowPos(Handle, nint.Zero, _bounds.Left, _bounds.Top,
                    _bounds.Right - _bounds.Left, _bounds.Bottom - _bounds.Top, 0x0010 | 0x0040);
                UpdateWindow(Handle);
                _ready.TrySetResult(Handle);
                while (GetMessage(out var message, 0, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
            catch (Exception exception) { _ready.TrySetException(exception); }
            finally
            {
                if (Handle != 0 && IsWindow(Handle)) DestroyWindow(Handle);
                UnregisterClass(_className, instance);
                DeleteObject(brush);
            }
        }

        private nint Procedure(nint hwnd, uint message, nint wParam, nint lParam)
        {
            if (message == 0x0002) { PostQuitMessage(0); return 0; }
            if (message == 0x0021) return new nint(3);
            if (message == 0x0201) { Interlocked.Increment(ref _clicks); return 0; }
            return DefWindowProc(hwnd, message, wParam, lParam);
        }

        public void Dispose()
        {
            if (Handle != 0) PostMessage(Handle, 0x0010, 0, 0);
            if (!_thread.Join(TimeSpan.FromSeconds(3))) throw new InvalidOperationException("Fullscreen QA target did not close.");
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Size, Style; public WindowProc Procedure; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage { public nint Window; public uint Message; public nuint WParam; public nint LParam; public uint Time; public NativePoint Point; public uint Private; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ClientToScreen(nint hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out uint value, int size);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint hwnd, StringBuilder result, int capacity);
    [DllImport("gdi32.dll")] private static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint value);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW")] private static extern ushort RegisterClassEx(ref WindowClass windowClass);
    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterClass(string className, nint instance);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateWindow(nint hwnd);
    [DllImport("user32.dll", EntryPoint = "GetMessageW")] private static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")] private static extern nint DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint hwnd);
}
