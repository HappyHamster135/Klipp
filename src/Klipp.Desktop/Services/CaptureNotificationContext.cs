using System;
using System.Runtime.InteropServices;

namespace Klipp.Desktop.Services;

/// <summary>Presentation context only; reading it never changes another window.</summary>
internal readonly record struct CaptureNotificationContext(
    bool Passive, bool FullScreen, nint ForegroundWindow, nint Monitor,
    uint Dpi, int MonitorLeft, int MonitorTop, int MonitorRight, int MonitorBottom,
    int WorkLeft, int WorkTop, int WorkRight, int WorkBottom)
{
    public static CaptureNotificationContext Read()
    {
        var foreground = GetForegroundWindow();
        var monitor = MonitorFromWindow(foreground, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref info);
        GetWindowThreadProcessId(foreground, out var process);
        var bounds = default(NativeRect);
        var hasBounds = foreground != 0 && !IsIconic(foreground) &&
            (DwmGetWindowAttribute(foreground, 9, out bounds, Marshal.SizeOf<NativeRect>()) >= 0 ||
                GetWindowRect(foreground, out bounds));
        // Covering the display is sufficient to choose passive placement. It
        // deliberately makes no claim about a game's DXGI exclusive mode.
        var fullScreen = hasBounds && info.Monitor.Right > info.Monitor.Left &&
            bounds.Left <= info.Monitor.Left + 2 && bounds.Top <= info.Monitor.Top + 2 &&
            bounds.Right >= info.Monitor.Right - 2 && bounds.Bottom >= info.Monitor.Bottom - 2;
        return new CaptureNotificationContext(
            fullScreen || process != (uint)Environment.ProcessId, fullScreen, foreground, monitor,
            foreground != 0 ? GetDpiForWindow(foreground) : 96,
            info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom,
            info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out NativeRect value, int size);
}
