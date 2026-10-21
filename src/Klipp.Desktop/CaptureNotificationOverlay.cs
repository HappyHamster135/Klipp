using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Klipp.Core.Branding;
using Klipp.Core.Models;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace Klipp.Desktop;

/// <summary>A capture-excluded native card with cached pixels and opacity-only presentation.</summary>
internal sealed class CaptureNotificationOverlay : IDisposable
{
    private const string WindowClass = "Klipp.PassiveCaptureNotification";
    private const nuint FadeTimer = 1;
    private const nuint InteractionTimer = 2;
    private static readonly WindowProc Procedure = ProcessMessage;
    private static readonly Dictionary<nint, CaptureNotificationOverlay> Windows = new();
    private static bool _registered;
    private bool _disposed;
    private bool _passive = true;
    private bool _closePressed;
    private NativeRect _bounds;
    private double _scale;
    private CaptureNotificationMessage? _message;
    private nint _memoryDc, _bitmap, _previousBitmap, _bits;
    private long _fadeStarted;
    private double _fadeFrom, _fadeTarget, _fadeMilliseconds;
    private bool _highContrast;

    internal event EventHandler? DismissRequested;
    internal nint Handle { get; private set; }
    internal bool IsVisible { get; private set; }
    internal bool IsExcludedFromCapture { get; private set; }
    internal byte[]? RenderedPixels { get; private set; }
    internal int RenderedWidth { get; private set; }
    internal int RenderedHeight { get; private set; }
    internal double CurrentOpacity { get; private set; }
    internal bool IsFadingOut { get; private set; }
    internal bool AnimationsEnabled { get; private set; }

    internal CaptureNotificationOverlay()
    {
        var instance = GetModuleHandle(null);
        if (!_registered)
        {
            var windowClass = new WindowClassInfo
            {
                Size = (uint)Marshal.SizeOf<WindowClassInfo>(), Instance = instance,
                Procedure = Marshal.GetFunctionPointerForDelegate(Procedure), Name = WindowClass
            };
            if (RegisterClassEx(ref windowClass) == 0 && Marshal.GetLastWin32Error() != 1410)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            _registered = true;
        }
        // LAYERED + TRANSPARENT passes mouse input across process boundaries.
        // A native bitmap also avoids WinUI's opaque compositor fallback.
        Handle = CreateWindowEx(0x080800A8, WindowClass, "Klipp capture notification",
            0x80000000, 0, 0, 1, 1, 0, 0, instance, 0);
        if (Handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        Windows.Add(Handle, this);
        var noBorder = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(Handle, 34, ref noBorder, sizeof(int));
    }

    /// <summary>Prepares identical BGRA pixels for an in-game renderer without showing an external window.</summary>
    internal void PrepareFrame(CaptureNotificationMessage message, int left, int top, int width, int height,
        double scale, bool interactive = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        _message = message;
        _scale = scale;
        _bounds = new NativeRect { Left = left, Top = top, Right = left + width, Bottom = top + height };
        _passive = !interactive;
        ReadAccessibilityPreferences();
        Render(message, width, height, scale, present: false);
    }

    internal void Show(CaptureNotificationMessage message, int left, int top, int width, int height,
        double scale, bool passive = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (!EnsureCaptureExclusion())
        {
            Hide(immediate: true);
            throw new InvalidOperationException("Capture exclusion is unavailable for the notification.");
        }
        var wasVisible = IsVisible;
        _message = message;
        _scale = scale;
        _bounds = new NativeRect { Left = left, Top = top, Right = left + width, Bottom = top + height };
        _passive = passive;
        _closePressed = false;
        RefreshInputMode();
        ReadAccessibilityPreferences();
        if (!wasVisible) CurrentOpacity = AnimationsEnabled ? 0 : 1;
        Render(message, width, height, scale);
        ShowWindow(Handle, 4); // SW_SHOWNOACTIVATE; never take game focus.
        SetWindowPos(Handle, new nint(-1), left, top, width, height, 0x0010);
        IsVisible = true;
        SetTimer(Handle, InteractionTimer, 100, 0);
        // Retarget an interrupted exit from its current alpha. Coalescing an
        // already-visible message does not flash or restart its entrance.
        AnimateTo(1);
    }

    internal void UpdatePlacement(int left, int top, int width, int height, double scale)
    {
        if (_disposed || !IsVisible || IsFadingOut) return;
        if (!EnsureCaptureExclusion())
        {
            Hide(immediate: true);
            throw new InvalidOperationException("Capture exclusion became unavailable for the notification.");
        }
        if (_bounds.Left == left && _bounds.Top == top && _bounds.Right == left + width &&
            _bounds.Bottom == top + height && Math.Abs(_scale - scale) < .001) return;
        var resize = _bounds.Right - _bounds.Left != width || _bounds.Bottom - _bounds.Top != height ||
            Math.Abs(_scale - scale) >= .001;
        _bounds = new NativeRect { Left = left, Top = top, Right = left + width, Bottom = top + height };
        _scale = scale;
        if (resize && _message is not null) Render(_message, width, height, scale);
        SetWindowPos(Handle, new nint(-1), left, top, width, height, 0x0010);
    }

    internal void Hide(bool immediate = false)
    {
        if (_disposed) return;
        KillTimer(Handle, InteractionTimer);
        if (immediate || !IsVisible || !AnimationsEnabled || !HasCaptureExclusion()) HideImmediately();
        else AnimateTo(0);
    }

    private void ReadAccessibilityPreferences()
    {
        _highContrast = false;
        AnimationsEnabled = false;
        try { _highContrast = new AccessibilitySettings().HighContrast; } catch (COMException) { }
        try { AnimationsEnabled = !_highContrast && new UISettings().AnimationsEnabled; } catch (COMException) { }
    }

    private void AnimateTo(double target)
    {
        KillTimer(Handle, FadeTimer);
        IsFadingOut = target == 0;
        _fadeTarget = target;
        if (!AnimationsEnabled || Math.Abs(target - CurrentOpacity) < .001)
        {
            CurrentOpacity = target;
            if (target == 0) HideImmediately();
            else { IsFadingOut = false; PresentPixels(); }
            return;
        }
        _fadeFrom = CurrentOpacity;
        _fadeStarted = Stopwatch.GetTimestamp();
        _fadeMilliseconds = target > CurrentOpacity ? 160 : 140;
        if (SetTimer(Handle, FadeTimer, 16, 0) == 0)
        {
            CurrentOpacity = target;
            if (target == 0) HideImmediately();
            else { IsFadingOut = false; PresentPixels(); }
        }
    }

    private void AdvanceFade()
    {
        // Exclusion failure must hide immediately, including during an exit.
        if (!HasCaptureExclusion()) { HideImmediately(); return; }
        var elapsed = (Stopwatch.GetTimestamp() - _fadeStarted) * 1000d / Stopwatch.Frequency;
        var progress = Math.Clamp(elapsed / _fadeMilliseconds, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        CurrentOpacity = _fadeFrom + (_fadeTarget - _fadeFrom) * eased;
        PresentPixels();
        if (progress < 1) return;
        KillTimer(Handle, FadeTimer);
        if (_fadeTarget == 0) HideImmediately();
        else IsFadingOut = false;
    }

    private void HideImmediately()
    {
        KillTimer(Handle, FadeTimer);
        KillTimer(Handle, InteractionTimer);
        ShowWindow(Handle, 0);
        CurrentOpacity = 0;
        IsFadingOut = false;
        IsVisible = false;
        _closePressed = false;
    }

    private bool EnsureCaptureExclusion()
    {
        IsExcludedFromCapture = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) &&
            SetWindowDisplayAffinity(Handle, 0x11) && HasCaptureExclusion();
        return IsExcludedFromCapture;
    }

    private bool HasCaptureExclusion()
    {
        IsExcludedFromCapture = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041) &&
            DwmIsCompositionEnabled(out var enabled) == 0 && enabled &&
            GetWindowDisplayAffinity(Handle, out var affinity) && affinity == 0x11;
        return IsExcludedFromCapture;
    }

    private void Render(CaptureNotificationMessage message, int width, int height, double scale, bool present = true)
    {
        ReleaseBitmap();
        var screen = GetDC(0);
        _memoryDc = CreateCompatibleDC(screen);
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = width, Height = -height,
            Planes = 1, BitCount = 32
        };
        _bitmap = CreateDIBSection(screen, ref header, 0, out _bits, 0, 0);
        if (screen != 0) ReleaseDC(0, screen);
        if (_memoryDc == 0 || _bitmap == 0 || _bits == 0)
        {
            var error = Marshal.GetLastWin32Error();
            ReleaseBitmap();
            throw new Win32Exception(error);
        }
        _previousBitmap = SelectObject(_memoryDc, _bitmap);
        var ui = new UISettings();
        var background = _highContrast ? ui.GetColorValue(UIColorType.Background) : Color.FromArgb(191, 18, 22, 28);
        var foreground = _highContrast ? ui.GetColorValue(UIColorType.Foreground) : Color.FromArgb(255, 242, 245, 248);
        var muted = _highContrast ? foreground : Color.FromArgb(255, 206, 213, 222);
        var accent = _highContrast ? foreground : message.Kind switch
        {
            CaptureNotificationKind.ClipSaved => Color.FromArgb(255, 151, 217, 183),
            CaptureNotificationKind.RecordingStarted => Color.FromArgb(255, 244, 160, 165),
            _ => Color.FromArgb(255, 169, 202, 239)
        };
        var pixels = new byte[checked(width * height * 4)];
        int P(double value) => (int)Math.Round(value * scale);
        var radius = Math.Min(Math.Min(width, height) / 2d, Math.Max(1, P(12)));
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var coverage = RoundedCoverage(x, y, 0, 0, width, height, radius);
            Composite(pixels, (y * width + x) * 4, background, coverage);
            var inner = RoundedCoverage(x, y, 1, 1, width - 2, height - 2, Math.Max(0, radius - 1));
            var stroke = _highContrast ? foreground : Color.FromArgb(42, 231, 237, 245);
            Composite(pixels, (y * width + x) * 4, stroke, Math.Max(0, coverage - inner));
        }
        var badge = _highContrast ? background : Color.FromArgb(35, accent.R, accent.G, accent.B);
        for (var y = Math.Max(0, P(20)); y < Math.Min(height, P(56)); y++)
        for (var x = Math.Max(0, P(16)); x < Math.Min(width, P(52)); x++)
            Composite(pixels, (y * width + x) * 4, badge,
                RoundedCoverage(x, y, P(16), P(20), P(36), P(36), P(9)));

        // GDI writes antialiased glyph coverage into a reusable black mask.
        // Source-over preserves opaque text over the translucent surface.
        // Output channels remain premultiplied for User32 and game renderers.
        var mask = new byte[pixels.Length];
        void Text(string text, string family, double fontSize, int weight, Color color,
            double left, double top, int right, double bottom, uint format)
        {
            Array.Clear(mask);
            Marshal.Copy(mask, 0, _bits, mask.Length);
            SetBkMode(_memoryDc, 1);
            DrawText(_memoryDc, text, family, P(fontSize), weight, Color.FromArgb(255, 255, 255, 255),
                P(left), P(top), right, P(bottom), format);
            GdiFlush();
            Marshal.Copy(_bits, mask, 0, mask.Length);
            if (!_highContrast)
            {
                var shadowOffset = Math.Max(1, P(1)) * width * 4;
                var shadow = Color.FromArgb(150, 0, 0, 0);
                for (var offset = 0; offset < pixels.Length - shadowOffset; offset += 4)
                    if (mask[offset] != 0) Composite(pixels, offset + shadowOffset, shadow, mask[offset] / 255d);
            }
            for (var offset = 0; offset < pixels.Length; offset += 4)
                if (mask[offset] != 0) Composite(pixels, offset, color, mask[offset] / 255d);
        }
        var textRight = Math.Max(P(64), width - P(_passive ? 18 : 46));
        DrawBrandMark(pixels, width, height, 62 * scale, 14 * scale, 18 * scale,
            _highContrast ? foreground : Color.FromArgb(255, 143, 175, 210));
        Text("KLIPP", "Segoe UI", 10, 600, muted, 82, 16, textRight, 30, 0x20);
        Text(message.Title, "Segoe UI", 14, 600, foreground, 64, 33, textRight, 54, 0x20 | 0x8000);
        Text(message.Detail, "Segoe UI", 12, 400, muted, 64, 60, width - P(18), 99, 0x10 | 0x8000);
        var glyph = message.Kind switch
        {
            CaptureNotificationKind.ClipSaved => "\uE73E",
            CaptureNotificationKind.RecordingStarted => "\uE7C8",
            CaptureNotificationKind.RecordingStopped => "\uE71A",
            CaptureNotificationKind.AutomaticSourceChange => "\uE7F4",
            CaptureNotificationKind.RecordingBookmark => "\uE7C1",
            CaptureNotificationKind.AudioDeviceChanged => "\uE720",
            _ => "\uE946"
        };
        Text(glyph, "Segoe MDL2 Assets", 17, 400, accent, 16, 20, P(52), 56, 0x1 | 0x4 | 0x20);
        if (!_passive)
            Text("\uE711", "Segoe MDL2 Assets", 10, 400, muted,
                width / scale - 40, 12, width - P(12), 40, 0x1 | 0x4 | 0x20);
        Marshal.Copy(pixels, 0, _bits, pixels.Length);
        RenderedPixels = pixels;
        RenderedWidth = width;
        RenderedHeight = height;
        if (present) PresentPixels();
    }

    private static void DrawBrandMark(byte[] pixels, int width, int height,
        double left, double top, double size, Color color)
    {
        // Supersample only this tiny mark. The cached frame is then reused for
        // both the desktop overlay and the exclusive-fullscreen renderer.
        const int samples = 4;
        for (var y = Math.Max(0, (int)Math.Floor(top)); y < Math.Min(height, (int)Math.Ceiling(top + size)); y++)
        for (var x = Math.Max(0, (int)Math.Floor(left)); x < Math.Min(width, (int)Math.Ceiling(left + size)); x++)
        {
            var covered = 0;
            for (var sy = 0; sy < samples; sy++)
            for (var sx = 0; sx < samples; sx++)
            {
                var sourceX = (x + (sx + .5) / samples - left) * KlippBrandGeometry.Size / size;
                var sourceY = (y + (sy + .5) / samples - top) * KlippBrandGeometry.Size / size;
                if (KlippBrandGeometry.Contains(sourceX, sourceY)) covered++;
            }
            if (covered > 0) Composite(pixels, (y * width + x) * 4, color, covered / (double)(samples * samples));
        }
    }

    private static double RoundedCoverage(double x, double y, double left, double top,
        double width, double height, double radius)
    {
        if (width <= 0 || height <= 0 || x < left || y < top || x >= left + width || y >= top + height) return 0;
        radius = Math.Min(radius, Math.Min(width, height) / 2);
        if (radius <= 0) return 1;
        var centerX = Math.Clamp(x + .5, left + radius, left + width - radius);
        var centerY = Math.Clamp(y + .5, top + radius, top + height - radius);
        var dx = x + .5 - centerX;
        var dy = y + .5 - centerY;
        return Math.Clamp(radius + .5 - Math.Sqrt(dx * dx + dy * dy), 0, 1);
    }

    private static void Composite(byte[] pixels, int offset, Color color, double coverage)
    {
        var alpha = Math.Clamp(color.A / 255d * coverage, 0, 1);
        var remaining = 1 - alpha;
        pixels[offset] = (byte)Math.Clamp(Math.Round(color.B * alpha + pixels[offset] * remaining), 0, 255);
        pixels[offset + 1] = (byte)Math.Clamp(Math.Round(color.G * alpha + pixels[offset + 1] * remaining), 0, 255);
        pixels[offset + 2] = (byte)Math.Clamp(Math.Round(color.R * alpha + pixels[offset + 2] * remaining), 0, 255);
        pixels[offset + 3] = (byte)Math.Clamp(Math.Round(255 * alpha + pixels[offset + 3] * remaining), 0, 255);
    }

    private void PresentPixels()
    {
        if (_memoryDc == 0 || _disposed) return;
        var destination = new NativePoint { X = _bounds.Left, Y = _bounds.Top };
        var size = new NativeSize { Width = RenderedWidth, Height = RenderedHeight };
        var source = new NativePoint();
        var blend = new BlendFunction { SourceAlpha = (byte)Math.Round(Math.Clamp(CurrentOpacity, 0, 1) * 255), AlphaFormat = 1 };
        if (!UpdateLayeredWindow(Handle, 0, ref destination, ref size, _memoryDc, ref source, 0, ref blend, 2))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static void DrawText(nint dc, string text, string family, int height, int weight, Color color,
        int left, int top, int right, int bottom, uint format)
    {
        var font = CreateFont(-Math.Max(1, height), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, 4, 0, family);
        var oldFont = SelectObject(dc, font);
        try
        {
            SetTextColor(dc, (uint)(color.R | color.G << 8 | color.B << 16));
            var bounds = new NativeRect { Left = left, Top = top, Right = right, Bottom = bottom };
            DrawTextNative(dc, text, text.Length, ref bounds, format | 0x800);
        }
        finally { SelectObject(dc, oldFont); DeleteObject(font); }
    }

    private bool CloseHit(nint packed)
    {
        var x = unchecked((short)(packed.ToInt64() & 0xffff));
        var y = unchecked((short)((packed.ToInt64() >> 16) & 0xffff));
        var width = _bounds.Right - _bounds.Left;
        return !_passive && IsOwnForeground() && !IsFadingOut && x >= width - 40 * _scale && x < width - 12 * _scale &&
            y >= 12 * _scale && y < 40 * _scale;
    }

    private static bool IsOwnForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var process);
        return process == Environment.ProcessId;
    }

    private void RefreshInputMode()
    {
        var extended = GetWindowLongPtr(Handle, -20).ToInt64();
        var passThrough = _passive || !IsOwnForeground();
        var next = passThrough ? extended | 0x20 : extended & ~0x20L;
        if (next != extended) SetWindowLongPtr(Handle, -20, new nint(next));
    }

    private static nint ProcessMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (!Windows.TryGetValue(hwnd, out var window)) return DefWindowProc(hwnd, message, wParam, lParam);
        try
        {
            switch (message)
            {
                case 0x0021: return new nint(3); // MA_NOACTIVATE
                case 0x0084 when window._passive || !IsOwnForeground():
                    // The context poll may not have observed an Alt-Tab yet.
                    // Become cross-process pass-through before this click.
                    var extended = GetWindowLongPtr(hwnd, -20).ToInt64();
                    if ((extended & 0x20) == 0) SetWindowLongPtr(hwnd, -20, new nint(extended | 0x20));
                    return new nint(-1); // HTTRANSPARENT
                case 0x0113 when (nuint)wParam == FadeTimer: window.AdvanceFade(); return 0;
                case 0x0113 when (nuint)wParam == InteractionTimer:
                    if (!window.HasCaptureExclusion()) window.HideImmediately();
                    else window.RefreshInputMode();
                    return 0;
                case 0x0201: window._closePressed = window.CloseHit(lParam); return 0;
                case 0x0202:
                    var dismiss = window._closePressed && window.CloseHit(lParam);
                    window._closePressed = false;
                    if (dismiss) window.DismissRequested?.Invoke(window, EventArgs.Empty);
                    return 0;
            }
        }
        catch (Exception exception)
        {
            window.HideImmediately();
            Debug.WriteLine($"Capture notification presentation failed: {exception}");
            return 0;
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ReleaseBitmap()
    {
        if (_memoryDc != 0 && _previousBitmap != 0) SelectObject(_memoryDc, _previousBitmap);
        if (_bitmap != 0) DeleteObject(_bitmap);
        if (_memoryDc != 0) DeleteDC(_memoryDc);
        _memoryDc = _bitmap = _previousBitmap = _bits = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        HideImmediately();
        _disposed = true;
        if (Handle != 0) { Windows.Remove(Handle); DestroyWindow(Handle); }
        ReleaseBitmap();
        Handle = 0;
        RenderedPixels = null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassInfo
    {
        public uint Size, Style; public nint Procedure; public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background; public string? Menu, Name; public nint SmallIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte Operation, Flags, SourceAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, ImageSize; public int XPixelsPerMeter, YPixelsPerMeter; public uint ColorsUsed, ColorsImportant;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClassInfo info);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateWindowEx(uint extended, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern nuint SetTimer(nint hwnd, nuint id, uint milliseconds, nint callback);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool KillTimer(nint hwnd, nuint id);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int size);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(nint dc, uint color);
    [DllImport("gdi32.dll", EntryPoint = "CreateFontW", CharSet = CharSet.Unicode)] private static extern nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeout, uint charset, uint precision, uint clip, uint quality, uint pitch, string family);
    [DllImport("user32.dll", EntryPoint = "DrawTextW", CharSet = CharSet.Unicode)] private static extern int DrawTextNative(nint dc, string text, int count, ref NativeRect rect, uint format);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GdiFlush();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateLayeredWindow(nint hwnd, nint destinationDc, ref NativePoint position, ref NativeSize size, nint sourceDc, ref NativePoint source, uint colorKey, ref BlendFunction blend, uint flags);
}
