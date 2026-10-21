using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Klipp.Desktop.Services;

internal enum TrayCommand { Open, SaveClip, StopCapture, Exit }

internal readonly record struct TrayMenuState(
    string Status, string SaveClipLabel, bool CanSaveClip, bool CanStopCapture, bool CanExit = true);

/// <summary>Owns one native notification-area icon without replacing the window's existing message hooks.</summary>
internal sealed class TrayIconService : IDisposable
{
    internal const uint CallbackMessage = 0x8000 + 0x4B2;
    internal const uint DeferredMinimizeMessage = CallbackMessage + 1;
    internal const uint IconId = 1;
    internal static readonly Guid AppIconGuid = new("E710A86E-5DFD-4AFB-9533-6762DB22A1C4");
    private const nuint SubclassId = 0x4B4C4950;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetFocus = 3, NimSetVersion = 4;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifGuid = 0x20, NifShowTip = 0x80;
    private readonly nint _window;
    private readonly nint _icon;
    private readonly Guid _identity;
    private readonly SubclassProc _procedure;
    private readonly Func<bool> _minimizeRequested;
    private readonly Func<TrayMenuState> _getMenuState;
    private readonly Action<TrayCommand> _command;
    private readonly Action _iconUnavailable;
    private bool _subclassInstalled;
    private bool _version4;
    private bool _menuOpen;
    private bool _disposed;
    private string _tooltip = "Klipp · Ready";

    internal bool IsRegistered { get; private set; }
    internal uint TaskbarCreatedMessage { get; }

    internal TrayIconService(nint window, string iconPath, Func<bool> minimizeRequested,
        Func<TrayMenuState> getMenuState, Action<TrayCommand> command, Action iconUnavailable,
        Guid? identity = null)
    {
        _window = window;
        _identity = identity ?? AppIconGuid;
        _minimizeRequested = minimizeRequested;
        _getMenuState = getMenuState;
        _command = command;
        _iconUnavailable = iconUnavailable;
        _procedure = WindowProcedure;
        TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        if (File.Exists(iconPath))
            _icon = LoadImage(0, iconPath, 1, 32, 32, 0x0010); // IMAGE_ICON, LR_LOADFROMFILE; owned HICON.
        _subclassInstalled = SetWindowSubclass(_window, _procedure, SubclassId, 0);
        if (!_subclassInstalled)
        {
            if (_icon != 0) DestroyIcon(_icon);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach the Klipp tray icon.");
        }
        EnsureRegistered();
    }

    internal bool EnsureRegistered()
    {
        if (_disposed || _icon == 0) return false;
        if (IsRegistered) return true;
        var data = CreateIdentityData();
        data.Flags |= NifMessage | NifIcon | NifTip | NifShowTip;
        data.CallbackMessage = CallbackMessage;
        data.Icon = _icon;
        data.Tip = _tooltip;
        // Modify handles a redundant TaskbarCreated message without duplicating
        // an existing icon; after Explorer restarts, Add supplies a fresh icon.
        IsRegistered = ShellNotifyIcon(NimAdd, ref data) || ShellNotifyIcon(NimModify, ref data);
        if (IsRegistered)
        {
            data.TimeoutOrVersion = 4;
            _version4 = ShellNotifyIcon(NimSetVersion, ref data);
        }
        return IsRegistered;
    }

    internal void UpdateTooltip(string text)
    {
        if (_disposed) return;
        text = text.Length > 127 ? text[..127] : text;
        if (_tooltip == text) return;
        _tooltip = text;
        if (!IsRegistered) return;
        var data = CreateIdentityData();
        data.Flags |= NifTip | NifShowTip;
        data.Tip = _tooltip;
        if (ShellNotifyIcon(NimModify, ref data)) return;
        IsRegistered = false;
        if (!EnsureRegistered()) _iconUnavailable();
    }

    internal NotifyIconData CreateIdentityData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(), Window = _window, Id = IconId,
        Flags = NifGuid, Guid = _identity, Tip = string.Empty, Info = string.Empty, InfoTitle = string.Empty
    };

    internal bool HasShellIcon()
    {
        var identifier = new NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = _window, Id = IconId, Guid = _identity
        };
        return ShellNotifyIconGetRect(ref identifier, out _) >= 0;
    }

    private nint WindowProcedure(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        try
        {
            if (!_disposed)
            {
                if (message == 0x0112 && ((uint)wParam & 0xFFF0) == 0xF020 && _minimizeRequested())
                    return 0; // SC_MINIMIZE: hide before Windows changes the maximized/restored state.
                if (message == DeferredMinimizeMessage)
                {
                    if (IsIconic(_window)) _minimizeRequested();
                    return 0;
                }
                if (message == TaskbarCreatedMessage && TaskbarCreatedMessage != 0)
                {
                    IsRegistered = false;
                    if (!EnsureRegistered()) _iconUnavailable();
                    return 0;
                }
                if (message == CallbackMessage)
                {
                    var callback = _version4 ? (uint)lParam & 0xFFFF : (uint)lParam;
                    var iconId = _version4 ? ((uint)lParam >> 16) & 0xFFFF : (uint)wParam;
                    if (iconId == IconId)
                    {
                        if (callback is 0x0400 or 0x0401 || (!_version4 && callback == 0x0202))
                            _command(TrayCommand.Open); // NIN_SELECT / NIN_KEYSELECT / legacy left up.
                        else if (callback == 0x007B || (!_version4 && callback == 0x0205))
                            ShowContextMenu(wParam); // WM_CONTEXTMENU / legacy right up.
                    }
                    return 0;
                }
            }
        }
        catch (Exception exception)
        {
            // A shell callback must never unwind through native window dispatch.
            Debug.WriteLine($"[Klipp] Tray callback failed: {exception}");
        }

        var result = DefSubclassProc(window, message, wParam, lParam);
        if (message == 0x0005 && wParam == 1 && !_disposed) // Programmatic SIZE_MINIMIZED.
            PostMessage(_window, DeferredMinimizeMessage, 0, 0);
        if (message == 0x0082) Dispose(); // WM_NCDESTROY, also covers construction/shutdown failure.
        return result;
    }

    internal static (int X, int Y)? ReadVersion4Anchor(nint packedAnchor)
    {
        // Shell callback coordinates use signed 16-bit words, including on
        // monitors to the left of or above the primary display.
        var x = unchecked((short)((long)packedAnchor & 0xFFFF));
        var y = unchecked((short)(((long)packedAnchor >> 16) & 0xFFFF));
        return x == -1 && y == -1 ? null : (x, y);
    }

    internal (int X, int Y)? GetContextMenuAnchor(nint packedAnchor)
    {
        if (_version4)
        {
            if (ReadVersion4Anchor(packedAnchor) is { } supplied) return supplied;
            var identifier = new NotifyIconIdentifier
            {
                Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), Window = _window,
                Id = IconId, Guid = _identity
            };
            if (ShellNotifyIconGetRect(ref identifier, out var rect) >= 0)
                return (rect.Left + (rect.Right - rect.Left) / 2, rect.Top + (rect.Bottom - rect.Top) / 2);
            return null;
        }
        return GetCursorPos(out var point) ? (point.X, point.Y) : null;
    }

    private void ShowContextMenu(nint packedAnchor)
    {
        if (_menuOpen || _disposed) return;
        // Keyboard invocation belongs beside the focused tray icon, regardless
        // of where the mouse was last used. A missing icon must remain recoverable.
        if (GetContextMenuAnchor(packedAnchor) is not { } point)
        {
            _iconUnavailable();
            return;
        }
        _menuOpen = true;
        var menu = CreatePopupMenu();
        try
        {
            if (menu == 0) return;
            var state = _getMenuState();
            AppendMenu(menu, 0, 10, "Open Klipp");
            SetMenuDefaultItem(menu, 10, false);
            AppendMenu(menu, 0x0800, 0, null);
            AppendMenu(menu, 0x0001, 0, state.Status.Replace("&", "&&"));
            AppendMenu(menu, 0x0800, 0, null);
            AppendMenu(menu, state.CanSaveClip ? 0u : 1u, 20, state.SaveClipLabel);
            AppendMenu(menu, state.CanStopCapture ? 0u : 1u, 30, "Stop capture");
            AppendMenu(menu, 0x0800, 0, null);
            AppendMenu(menu, state.CanExit ? 0u : 1u, 40, "Exit Klipp");
            SetForegroundWindow(_window);
            // Native menus supply Windows' keyboard navigation, accessibility,
            // monitor-edge placement and DPI scaling while the main UI is hidden.
            var selected = TrackPopupMenu(menu, 0x0100 | 0x0080 | 0x0002,
                point.X, point.Y, 0, _window, 0); // RETURNCMD | NONOTIFY | RIGHTBUTTON.
            PostMessage(_window, 0, 0, 0); // Complete the menu's foreground task switch.
            if (selected == 0)
            {
                var identity = CreateIdentityData();
                ShellNotifyIcon(NimSetFocus, ref identity);
            }
            else if (selected is 10 or 20 or 30 or 40)
                _command(selected switch { 10 => TrayCommand.Open, 20 => TrayCommand.SaveClip,
                    30 => TrayCommand.StopCapture, _ => TrayCommand.Exit });
        }
        finally
        {
            if (menu != 0) DestroyMenu(menu);
            _menuOpen = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (IsRegistered)
        {
            var data = CreateIdentityData();
            ShellNotifyIcon(NimDelete, ref data);
            IsRegistered = false;
        }
        if (_subclassInstalled)
        {
            RemoveWindowSubclass(_window, _procedure, SubclassId);
            _subclassInstalled = false;
        }
        if (_icon != 0) DestroyIcon(_icon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NotifyIconData
    {
        public uint Size; public nint Window; public uint Id, Flags, CallbackMessage; public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier { public uint Size; public nint Window; public uint Id; public Guid Guid; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    private delegate nint SubclassProc(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data);

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShellNotifyIcon(uint operation, ref NotifyIconData data);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconGetRect")]
    private static extern int ShellNotifyIconGetRect(ref NotifyIconIdentifier identifier, out NativeRect rect);
    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint window, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint window, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "LoadImageW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImage(nint instance, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(nint menu, uint flags, nuint id, string? text);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetMenuDefaultItem(nint menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint window, nint rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
}
