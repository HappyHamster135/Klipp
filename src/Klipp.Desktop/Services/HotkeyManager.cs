using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Linq;

namespace Klipp.Desktop.Services;

/// <summary>
/// Hanterar globala hotkeys via Win32 RegisterHotKey. Globala hotkeys fångas av
/// Windows oavsett vilket fönster som har fokus — så användaren kan spara ett klipp
/// mitt i ett fullskärmsspel utan att alt-tabba.
/// </summary>
/// <remarks>
/// Tekniken: vi subclassar fönstrets window procedure (WndProc) för att fånga
/// WM_HOTKEY-meddelanden. WinUI 3 exponerar inte WndProc direkt, så vi använder
/// Win32-interop med fönstrets HWND.
/// </remarks>
public sealed class HotkeyManager : IDisposable
{
    // Win32-konstanter
    private const int WM_HOTKEY = 0x0312;
    private const int GWLP_WNDPROC = -4;

    // Modifier-flaggor för RegisterHotKey
    [Flags]
    public enum Modifiers : uint
    {
        None = 0x0000,
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
        NoRepeat = 0x4000
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint msg, nint wParam, nint lParam);

    // Delegat för vår egen WndProc
    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    private readonly nint _hwnd;
    private readonly int _hotkeyId;
    private WndProcDelegate? _newWndProc;
    private nint _originalWndProc;
    private readonly Dictionary<int, (uint Modifiers, uint Key)> _registrations = [];
    private bool _disposed;

    /// <summary>Triggas när den registrerade hotkeyen trycks.</summary>
    public event EventHandler? HotkeyPressed;
    public event Action<int>? AdditionalHotkeyPressed;

    /// <summary>
    /// Skapar en HotkeyManager för ett fönster.
    /// </summary>
    /// <param name="windowHandle">Fönstrets HWND (från WinRT.Interop.WindowNative).</param>
    /// <param name="hotkeyId">Unikt ID för denna hotkey (godtyckligt, t.ex. 1).</param>
    public HotkeyManager(nint windowHandle, int hotkeyId = 1)
    {
        _hwnd = windowHandle;
        _hotkeyId = hotkeyId;

        // Subclassa fönstrets WndProc så vi kan fånga WM_HOTKEY
        _newWndProc = CustomWndProc;
        var newProcPtr = Marshal.GetFunctionPointerForDelegate(_newWndProc);
        _originalWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, newProcPtr);
    }

    /// <summary>
    /// Registrerar en hotkey. Avregistrerar tidigare om en redan finns.
    /// </summary>
    /// <param name="modifiers">Ctrl/Shift/Alt/Win-kombination.</param>
    /// <param name="virtualKey">Virtual key code (t.ex. 0x53 för 'S').</param>
    /// <returns>True om registreringen lyckades.</returns>
    public bool Register(Modifiers modifiers, uint virtualKey)
        => RegisterCore(_hotkeyId, modifiers, virtualKey);

    public bool RegisterAdditional(int id, Modifiers modifiers, uint virtualKey)
    {
        if (id == _hotkeyId || id < 1) throw new ArgumentOutOfRangeException(nameof(id));
        return RegisterCore(id, modifiers, virtualKey);
    }

    private bool RegisterCore(int id, Modifiers modifiers, uint virtualKey)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var mods = (uint)(modifiers | Modifiers.NoRepeat);
        if (_registrations.TryGetValue(id, out var old) && old == (mods, virtualKey)) return true;
        if (_registrations.Any(pair => pair.Key != id && pair.Value == (mods, virtualKey))) return false;
        var hadOld = _registrations.Remove(id);
        if (hadOld) UnregisterHotKey(_hwnd, id);
        if (RegisterHotKey(_hwnd, id, mods, virtualKey))
        {
            _registrations[id] = (mods, virtualKey);
            return true;
        }
        if (hadOld && RegisterHotKey(_hwnd, id, old.Modifiers, old.Key)) _registrations[id] = old;
        return false;
    }

    private nint CustomWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_HOTKEY && _registrations.ContainsKey(wParam.ToInt32()))
        {
            if (wParam.ToInt32() == _hotkeyId) HotkeyPressed?.Invoke(this, EventArgs.Empty);
            else AdditionalHotkeyPressed?.Invoke(wParam.ToInt32());
        }

        // Skicka vidare till original-WndProc så fönstret fungerar normalt
        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var id in _registrations.Keys) UnregisterHotKey(_hwnd, id);
        _registrations.Clear();

        // Återställ original-WndProc
        if (_originalWndProc != nint.Zero)
        {
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _originalWndProc);
            _originalWndProc = nint.Zero;
        }

        _newWndProc = null;
    }
}
