using System.Runtime.InteropServices;
using Klipp.Desktop.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

/// <summary>Exercises an isolated test window and tray icon; never loads user settings, projects or media.</summary>
internal static class TrayIconChecks
{
    internal static async Task RunAsync()
    {
        var iconPath = FindIcon();

        var window = new Window { Title = "Klipp disposable tray check", Content = new Grid() };
        window.Activate();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.Resize(new Windows.Graphics.SizeInt32(800, 520));
        var hidden = false;
        var restored = 0;
        var iconLost = 0;
        var hotkeys = 0;
        var additionalHotkeys = new List<int>();
        var commands = new List<TrayCommand>();
        TrayIconService? tray = null;
        // Match MainWindow's installation order: legacy hotkey WndProc first,
        // then the independently removable comctl32 tray subclass.
        using var hotkey = new HotkeyManager(hwnd, 377);
        hotkey.HotkeyPressed += (_, _) => hotkeys++;
        hotkey.AdditionalHotkeyPressed += additionalHotkeys.Add;
        bool Minimize()
        {
            if (tray?.EnsureRegistered() != true) return false;
            hidden = true;
            appWindow.IsShownInSwitchers = false;
            appWindow.Hide();
            return true;
        }
        void Restore()
        {
            hidden = false;
            appWindow.IsShownInSwitchers = true;
            if (IsIconic(hwnd)) ShowWindow(hwnd, 9);
            appWindow.Show();
            restored++;
        }
        try
        {
            SendMessage(hwnd, 0x0312, 377, 0);
            SendMessage(hwnd, 0x0312, 378, 0);
            Require(hotkeys == 0 && additionalHotkeys.Count == 0,
                "Unregistered WM_HOTKEY IDs dispatched capture commands.");
            const HotkeyManager.Modifiers modifiers = HotkeyManager.Modifiers.Control |
                HotkeyManager.Modifiers.Alt | HotkeyManager.Modifiers.Shift;
            // Prefer Ctrl+Alt+Shift+F24..F13, then fall back to lower function
            // keys if another application has reserved one of those shortcuts.
            var candidates = Enumerable.Range(0x7C, 12).Reverse()
                .Concat(Enumerable.Range(0x77, 5).Reverse()).Select(key => (uint)key).ToArray();
            var primaryKey = candidates.FirstOrDefault(key => hotkey.Register(modifiers, key));
            Require(primaryKey != 0, "No free function-key shortcut was available for the disposable primary hotkey.");
            var secondKey = candidates.Where(key => key != primaryKey)
                .FirstOrDefault(key => hotkey.RegisterAdditional(378, modifiers, key));
            Require(secondKey != 0, "No free function-key shortcut was available for the additional hotkey.");
            Require(!hotkey.RegisterAdditional(379, modifiers, primaryKey) && !hotkey.Register(modifiers, secondKey),
                "Two capture commands in one manager were allowed to share a shortcut.");
            // Reserve a distinct shortcut on the current thread rather than
            // this HWND, forcing the native registration failure/rollback path.
            const int blockerId = 0x4B50;
            var blockedKey = candidates.Where(key => key != primaryKey && key != secondKey)
                .FirstOrDefault(key => RegisterHotKey(0, blockerId, (uint)(modifiers | HotkeyManager.Modifiers.NoRepeat), key));
            Require(blockedKey != 0, "No free shortcut was available for the native rebind-conflict check.");
            try
            {
                Require(!hotkey.Register(modifiers, blockedKey),
                    "Rebinding a capture shortcut to a separately reserved hotkey unexpectedly succeeded.");
            }
            finally { UnregisterHotKey(0, blockerId); }

            tray = new TrayIconService(hwnd, iconPath, Minimize,
                () => new TrayMenuState("Test capture paused", "Save last 30 seconds", false, false),
                command => { commands.Add(command); if (command == TrayCommand.Open) Restore(); },
                () => { iconLost++; Restore(); }, Guid.NewGuid());
            Require(tray.IsRegistered, "The shell did not register the disposable tray icon.");
            Require(tray.HasShellIcon(), "The shell cannot find the registered tray icon.");
            Require(TrayIconService.ReadVersion4Anchor(Pack(-1840, -1180)) == (-1840, -1180),
                "Tray menu coordinates lost their sign on a negative-coordinate monitor.");
            Require(TrayIconService.ReadVersion4Anchor(Pack(24, 1040)) == (24, 1040),
                "Tray menu coordinates did not preserve the supplied shell anchor.");
            Require(TrayIconService.ReadVersion4Anchor(Pack(-1, -1)) is null &&
                tray.GetContextMenuAnchor(Pack(-1, -1)) is not null,
                "A keyboard context menu did not fall back to the actual shell icon rectangle.");
            tray.UpdateTooltip("Klipp · Disposable tray check");
            SendMessage(hwnd, 0x0312, 377, 0);
            SendMessage(hwnd, 0x0312, 378, 0);
            SendMessage(hwnd, 0x0312, 379, 0);
            SendMessage(hwnd, 0x0312, 999, 0);
            Require(hotkeys == 1 && additionalHotkeys.SequenceEqual([378]),
                "The tray subclass lost registered hotkeys, dispatched unknown IDs, or a failed rebind discarded the primary shortcut.");

            SendMessage(hwnd, 0x0112, 0xF020, 0);
            await Task.Delay(150);
            Require(hidden && !IsWindowVisible(hwnd) && !appWindow.IsShownInSwitchers,
                "Minimize did not hide the window from both desktop and taskbar switchers.");
            SendMessage(hwnd, TrayIconService.CallbackMessage, 0,
                (nint)((TrayIconService.IconId << 16) | 0x0400));
            await Task.Delay(150);
            Require(!hidden && IsWindowVisible(hwnd) && appWindow.IsShownInSwitchers && restored == 1,
                "NIN_SELECT did not restore the main window and taskbar entry.");

            var presenter = (OverlappedPresenter)appWindow.Presenter;
            presenter.Maximize();
            await Task.Delay(200);
            Require(presenter.State == OverlappedPresenterState.Maximized, "The test window did not maximize.");
            SendMessage(hwnd, 0x0112, 0xF020, 0);
            await Task.Delay(100);
            SendMessage(hwnd, TrayIconService.CallbackMessage, 0,
                (nint)((TrayIconService.IconId << 16) | 0x0401));
            await Task.Delay(150);
            Require(!hidden && presenter.State == OverlappedPresenterState.Maximized,
                "Keyboard tray activation lost the window's maximized state.");
            presenter.Minimize();
            await Task.Delay(150);
            Require(hidden && !IsWindowVisible(hwnd) && !appWindow.IsShownInSwitchers,
                "Programmatic minimization was not transferred to the tray.");
            SendMessage(hwnd, TrayIconService.CallbackMessage, 0,
                (nint)((TrayIconService.IconId << 16) | 0x0400));
            await Task.Delay(150);
            Require(!hidden && IsWindowVisible(hwnd), "A programmatically minimized window did not restore.");

            // Delete only this harness's own GUID, then simulate Explorer's
            // registered broadcast. No Explorer process is stopped or modified.
            var identity = tray.CreateIdentityData();
            TrayIconService.ShellNotifyIcon(2, ref identity);
            Require(!tray.HasShellIcon(), "The disposable icon did not delete before Explorer recovery.");
            SendMessage(hwnd, tray.TaskbarCreatedMessage, 0, 0);
            Require(tray.IsRegistered && tray.HasShellIcon() && iconLost == 0,
                "The icon did not recover after the simulated Explorer restart.");
            SendMessage(hwnd, tray.TaskbarCreatedMessage, 0, 0);
            Require(tray.IsRegistered && iconLost == 0,
                "A repeated TaskbarCreated broadcast incorrectly lost the existing icon.");
            tray.Dispose();
            Require(!tray.IsRegistered && !tray.HasShellIcon(), "Disposal left the disposable tray icon behind.");
            SendMessage(hwnd, 0x0312, 377, 0);
            SendMessage(hwnd, 0x0312, 378, 0);
            SendMessage(hwnd, 0x0312, 379, 0);
            Require(hotkeys == 2 && additionalHotkeys.SequenceEqual([378, 378]),
                "Removing the tray subclass broke multi-command hotkey dispatch or activated a rejected duplicate registration.");

            using var unavailable = new TrayIconService(hwnd, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ico"),
                () => false, () => default, _ => { }, () => { }, Guid.NewGuid());
            Require(!unavailable.IsRegistered && !unavailable.EnsureRegistered(),
                "A missing icon was reported as a usable background entry.");
            appWindow.IsShownInSwitchers = true;
            SendMessage(hwnd, 0x0112, 0xF020, 0);
            await Task.Delay(150);
            Require(IsIconic(hwnd) && IsWindowVisible(hwnd) && appWindow.IsShownInSwitchers,
                "Failed tray registration stranded the normal minimized taskbar window.");
            ShowWindow(hwnd, 9);
            Require(commands.Count == 3 && commands.All(command => command == TrayCommand.Open),
                "Unexpected tray callback command dispatch.");
            Console.WriteLine("PASS: tray add/delete, multi-command hotkey chaining, collision/rebind rollback, unregistered-ID filtering, hidden taskbar state, click/keyboard restore, maximized state, programmatic minimize, Explorer recovery and failed-icon fallback");
        }
        finally
        {
            tray?.Dispose();
            hotkey.Dispose();
            window.Close();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string FindIcon()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "Klipp.Desktop", "Assets", "Klipp.ico");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("The tray check needs the repository's Klipp icon.");
    }

    private static nint Pack(int x, int y) => (nint)((uint)unchecked((ushort)x) | ((uint)unchecked((ushort)y) << 16));

    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
}
