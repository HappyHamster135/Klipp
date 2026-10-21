using System;
using System.Runtime.InteropServices;
using Klipp.Capture.GameCapture;
using Klipp.Core.Models;

namespace Klipp.Desktop.Services;

/// <summary>Shares the desktop card's pixels with a capture-ready game renderer.</summary>
internal sealed class CaptureGameNotificationPresenter : IDisposable
{
    private CaptureNotificationOverlay? _renderer;
    private nint _window;
    private uint _process;

    public bool TryShow(CaptureNotificationMessage message, CaptureNotificationContext context, TimeSpan remaining)
    {
        if (!context.Passive || remaining <= TimeSpan.Zero || context.ForegroundWindow == 0)
            return false;
        GetWindowThreadProcessId(context.ForegroundWindow, out var process);
        if (!D3D11GameCaptureSource.IsNotificationTargetReady(context.ForegroundWindow, process))
            return false;
        if (!GetClientRect(context.ForegroundWindow, out var client) || client.Right <= 0 || client.Bottom <= 0)
            return false;

        var scale = context.Dpi > 0 ? context.Dpi / 96d : 1;
        var placement = CaptureNotificationPlacement.UpperLeftEdge(0, 0, client.Right, client.Bottom, scale);
        _renderer ??= new CaptureNotificationOverlay();
        _renderer.PrepareFrame(message, placement.Left, placement.Top, placement.Width, placement.Height, scale);
        if (_renderer.RenderedPixels is not { } pixels) return false;
        if (_window != 0 && (_window != context.ForegroundWindow || _process != process)) Hide();
        if (!D3D11GameCaptureSource.TryShowNotification(context.ForegroundWindow, process, pixels,
            placement.Width, placement.Height, placement.Left, placement.Top,
            Math.Clamp((int)Math.Ceiling(remaining.TotalMilliseconds), 1, 10000), _renderer.AnimationsEnabled))
            return false;
        _window = context.ForegroundWindow;
        _process = process;
        return true;
    }

    public void Hide()
    {
        if (_window != 0) D3D11GameCaptureSource.HideNotification(_window, _process);
        _window = 0;
        _process = 0;
    }

    public void Dispose()
    {
        Hide();
        _renderer?.Dispose();
        _renderer = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint hwnd, out NativeRect rect);
}
