using System.Runtime.InteropServices;
using Klipp.Core.Models;
using Klipp.Desktop;
using Klipp.Desktop.Services;
using Microsoft.UI.Dispatching;

internal static class CaptureNotificationChecks
{
    public static async Task RunAsync(bool includeFullscreen = false)
    {
        var foreground = GetForegroundWindow();
        var window = new CaptureNotificationWindow();
        using var service = new CaptureNotificationService(DispatcherQueue.GetForCurrentThread());
        try
        {
            window.ShowNotification(Message(CaptureNotificationKind.Preview, "Notification test"));
            await Task.Delay(260);
            var hwnd = window.VisibleHandle;
            var styles = GetWindowLongPtr(hwnd, -20).ToInt64();
            Require(IsWindowVisible(hwnd), "The native notification was not visible.");
            Require((styles & 0x08000088) == 0x08000088 && (styles & 0x00040000) == 0,
                "The notification is missing its no-activate/tool-window styles.");
            Require(window.PassiveRenderedPixels is { Length: > 400 },
                "The nonactivating window did not render its notification content.");
            Require(GetForegroundWindow() == foreground, "Showing a notification changed foreground focus.");
            Require(SendMessage(hwnd, 0x0021, 0, 0) == new nint(3), "Mouse interaction would activate the popup.");
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                Require(GetWindowDisplayAffinity(hwnd, out var affinity) && affinity == 0x11,
                    "The notification was not excluded from graphics capture.");
            window.HideNotification();
            await Task.Delay(260);
            for (var index = 0; index < 40; index++)
                service.Show(Message(CaptureNotificationKind.AutomaticSourceChange, "Automatic source changed", $"Target {index}"));
            await Task.Delay(100);
            Require(VisiblePopups() == 1, "Rapid capture events created multiple popup windows.");
            Require(GetForegroundWindow() == foreground, "A repeated notification changed foreground focus.");
            await Task.Delay(2300);
            Require(VisiblePopups() == 0, "The notification did not auto-dismiss.");
            service.Show(Message(CaptureNotificationKind.RecordingStarted, "Recording started"));
            service.Show(Message(CaptureNotificationKind.RecordingStopped, "Recording saved"));
            await Task.Delay(100);
            Require(VisiblePopups() == 1, "Recording status changes did not share one popup.");
            await Task.Delay(2300);
            Require(VisiblePopups() == 0, "Recording status remained visible after its duration.");
            Require(GetForegroundWindow() == foreground, "Dismissing a notification changed foreground focus.");
            Console.WriteLine("PASS: native notification focus, topmost/tool styles, capture exclusion, coalescing, and dismissal");
            await CaptureNotificationFullscreenChecks.CheckNormalPresentationAsync(window);
            await CheckFadeLifecycleAsync(window, foreground);
            if (includeFullscreen) await CaptureNotificationFullscreenChecks.RunAsync(window);
        }
        finally
        {
            service.Dispose();
            window.Close();
        }
    }

    private static CaptureNotificationMessage Message(CaptureNotificationKind kind, string title, string detail = "Disposable native QA popup") =>
        CaptureNotificationMessage.Create(kind, title, detail, DateTimeOffset.UtcNow, 2);

    private static async Task CheckFadeLifecycleAsync(CaptureNotificationWindow window, nint foreground)
    {
        window.ShowNotification(Message(CaptureNotificationKind.Preview, "Fade lifecycle check"));
        await Task.Delay(260);
        var hwnd = window.VisibleHandle;
        Require(IsWindowVisible(hwnd) && ReadOpacity(window) >= .99,
            "The notification did not settle at full presentation opacity.");

        // Replace an exiting notification in the same dispatcher turn. A stale
        // completion callback must not hide the new message at the old deadline.
        window.HideNotification();
        if (ReadBoolean(window, "AnimationsEnabled"))
            Require(ReadBoolean(window, "IsFadingOut") && IsWindowVisible(hwnd) && ReadOpacity(window) > 0,
                "An animated dismissal hid the popup immediately instead of fading out.");
        window.ShowNotification(Message(CaptureNotificationKind.ClipSaved, "Replacement during fade"));
        await Task.Delay(260);
        Require(IsWindowVisible(window.VisibleHandle) && ReadOpacity(window) >= .99,
            "An old fade-out hid or dimmed a replacement notification.");
        Require(GetForegroundWindow() == foreground, "Retargeting a notification fade changed focus.");

        window.HideNotification();
        await Task.Delay(260);
        Require(!IsWindowVisible(window.VisibleHandle) && ReadOpacity(window) <= .01,
            "The notification stayed visible after its fade-out completed.");
        Require(GetForegroundWindow() == foreground, "The notification fade-out changed focus.");
        Console.WriteLine("PASS: notification fades settle, hide fully, and survive replacement during exit without taking focus");
    }

    private static double ReadOpacity(CaptureNotificationWindow window)
    {
        var value = typeof(CaptureNotificationWindow).GetProperty("CurrentOpacity",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(window);
        Require(value is double opacity && double.IsFinite(opacity) && opacity is >= 0 and <= 1,
            "The notification opacity diagnostic was missing or invalid.");
        return (double)value!;
    }

    private static bool ReadBoolean(CaptureNotificationWindow window, string property)
    {
        var value = typeof(CaptureNotificationWindow).GetProperty(property,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(window);
        Require(value is bool, $"The notification {property} diagnostic was missing.");
        return (bool)value!;
    }

    private static int VisiblePopups()
    {
        var count = 0;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var process);
            if (process == Environment.ProcessId && IsWindowVisible(hwnd)) count++;
            return true;
        }, 0);
        return count;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private delegate bool EnumWindowProc(nint hwnd, nint data);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint hwnd, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowProc procedure, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
}
