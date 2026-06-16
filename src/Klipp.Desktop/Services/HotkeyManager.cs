using System;
using System.Runtime.InteropServices;

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
    private bool _registered;
    private bool _disposed;

    /// <summary>Triggas när den registrerade hotkeyen trycks.</summary>
    public event EventHandler? HotkeyPressed;

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
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_registered)
        {
            UnregisterHotKey(_hwnd, _hotkeyId);
            _registered = false;
        }

        // Lägg alltid till NoRepeat så att hålla nere tangenten inte spammar events
        var mods = (uint)(modifiers | Modifiers.NoRepeat);
        _registered = RegisterHotKey(_hwnd, _hotkeyId, mods, virtualKey);
        return _registered;
    }

    private nint CustomWndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _hotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
        }

        // Skicka vidare till original-WndProc så fönstret fungerar normalt
        return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_registered)
        {
            UnregisterHotKey(_hwnd, _hotkeyId);
            _registered = false;
        }

        // Återställ original-WndProc
        if (_originalWndProc != nint.Zero)
        {
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _originalWndProc);
            _originalWndProc = nint.Zero;
        }

        _newWndProc = null;
    }
}
