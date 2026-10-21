using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Klipp.Core.Models;
using Klipp.Desktop.Controls;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Klipp.Desktop;

/// <summary>A small desktop notification that never activates the capture app.</summary>
internal sealed class CaptureNotificationWindow : Window
{
    private readonly Grid _root;
    private readonly Border _card;
    private readonly Border _iconBadge;
    private readonly TextBlock _brand;
    private readonly KlippBrandMark _brandMark;
    private readonly TextBlock _title;
    private readonly TextBlock _detail;
    private readonly FontIcon _icon;
    private readonly FontIcon _closeIcon;
    private readonly Button _close;
    private readonly AccessibilitySettings _accessibility = new();
    private readonly UISettings _uiSettings = new();
    private readonly AppWindow _appWindow;
    private readonly nint _hwnd;
    private readonly SubclassProc _subclass;
    private readonly bool _subclassInstalled;
    private bool _accessibilitySubscribed;
    private bool _closed;
    private bool _passive;
    private bool _fullScreen;
    private bool _showing;
    private CaptureNotificationOverlay? _overlay;
    private CaptureNotificationMessage? _message;
    private CaptureNotificationKind _kind;

    public event EventHandler? DismissRequested;
    internal bool IsExcludedFromCapture => _overlay?.IsExcludedFromCapture == true;
    internal nint VisibleHandle => _overlay?.Handle ?? 0;
    internal bool IsPassive => _passive;
    internal byte[]? PassiveRenderedPixels => _overlay?.RenderedPixels;
    internal int PassiveRenderedWidth => _overlay?.RenderedWidth ?? 0;
    internal int PassiveRenderedHeight => _overlay?.RenderedHeight ?? 0;
    internal double CurrentOpacity => _overlay?.CurrentOpacity ?? 0;
    internal bool IsFadingOut => _overlay?.IsFadingOut == true;
    internal bool AnimationsEnabled => _overlay?.AnimationsEnabled == true;

    public CaptureNotificationWindow()
    {
        Title = "Klipp capture notification";
        ExtendsContentIntoTitleBar = true;
        _brand = new TextBlock
        {
            Text = "KLIPP", FontSize = 10, CharacterSpacing = 70,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        _brandMark = new KlippBrandMark { Width = 16, Height = 16 };
        _title = new TextBlock
        {
            FontSize = 14, LineHeight = 19, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1
        };
        _detail = new TextBlock
        {
            FontSize = 12, LineHeight = 16, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2
        };
        _icon = new FontIcon { FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 17 };
        _iconBadge = new Border
        {
            Width = 36, Height = 36, CornerRadius = new CornerRadius(8), Child = _icon,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0)
        };
        _closeIcon = new FontIcon { FontFamily = new FontFamily("Segoe MDL2 Assets"), Glyph = "\uE711", FontSize = 10 };
        _close = new Button
        {
            Content = _closeIcon, Width = 28, Height = 28, MinWidth = 28, MinHeight = 28,
            Padding = new Thickness(0), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Colors.Transparent), VerticalAlignment = VerticalAlignment.Top,
            IsTabStop = false, AllowFocusOnInteraction = false
        };
        _close.Click += (_, _) => DismissRequested?.Invoke(this, EventArgs.Empty);
        AutomationProperties.SetName(_close, "Dismiss notification");
        AutomationProperties.SetLiveSetting(_title, AutomationLiveSetting.Polite);
        ToolTipService.SetToolTip(_close, "Dismiss notification");
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        brand.Children.Add(_brandMark);
        brand.Children.Add(_brand);
        var text = new StackPanel { Spacing = 3 };
        text.Children.Add(brand);
        text.Children.Add(_title);
        text.Children.Add(_detail);
        var layout = new Grid { ColumnSpacing = 12 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        layout.Children.Add(_iconBadge);
        Grid.SetColumn(text, 1); layout.Children.Add(text);
        Grid.SetColumn(_close, 2); layout.Children.Add(_close);
        _card = new Border
        {
            Padding = new Thickness(15), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Child = layout
        };
        // This hidden XAML tree owns the message's automation live region.
        // A single native visible host keeps normal/game cards equally
        // transparent without WinUI's opaque layered-window fallback.
        _root = new Grid();
        _root.Children.Add(_card);
        Content = _root;
        ApplyTheme();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsAlwaysOnTop = true;
        _appWindow.SetPresenter(presenter);
        _appWindow.IsShownInSwitchers = false;

        _subclass = WindowProcedure;
        _subclassInstalled = SetWindowSubclass(_hwnd, _subclass, 1, 0);
        if (!_subclassInstalled)
        {
            // A throwing constructor never transfers this window to the
            // notification service, so release the hidden HWND here.
            try { Close(); }
            catch (Exception exception) { Debug.WriteLine($"Capture notification construction cleanup failed: {exception}"); }
            throw new InvalidOperationException("The notification could not establish its nonactivating window frame.");
        }
        ApplyNativeFrame();
        ApplyTheme();
        try
        {
            _accessibility.HighContrastChanged += OnHighContrastChanged;
            _accessibilitySubscribed = true;
        }
        catch (COMException) { }
        Closed += OnClosed;
    }

    public void ShowNotification(CaptureNotificationMessage message, bool passive = false, bool fullScreen = false)
    {
        if (_closed) return;
        _message = message;
        _kind = message.Kind;
        ApplyTheme();
        _title.Text = message.Title;
        _detail.Text = message.Detail;
        _icon.Glyph = message.Kind switch
        {
            CaptureNotificationKind.ClipSaved => "\uE73E",
            CaptureNotificationKind.RecordingStarted => "\uE7C8",
            CaptureNotificationKind.RecordingStopped => "\uE71A",
            CaptureNotificationKind.AutomaticSourceChange => "\uE7F4",
            CaptureNotificationKind.RecordingBookmark => "\uE7C1",
            CaptureNotificationKind.AudioDeviceChanged => "\uE720",
            _ => "\uE946"
        };
        AutomationProperties.SetName(_title, $"{message.Title}. {message.Detail}");
        _showing = true;
        ConfigurePresentation(passive, fullScreen, refreshContent: true);
        var peer = FrameworkElementAutomationPeer.FromElement(_title) ??
            FrameworkElementAutomationPeer.CreatePeerForElement(_title);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    public void HideNotification(bool immediate = false)
    {
        _showing = false;
        if (!_closed) _appWindow.Hide();
        _overlay?.Hide(immediate);
    }

    internal void UpdatePresentation(bool passive, bool fullScreen) =>
        ConfigurePresentation(passive, fullScreen, refreshContent: false);

    private void ConfigurePresentation(bool passive, bool fullScreen, bool refreshContent)
    {
        if (_closed) return;
        var changed = _passive != passive;
        _passive = passive;
        _fullScreen = fullScreen;
        if (!_showing || _message is null) return;
        var (bounds, scale) = CalculatePlacement();
        var width = bounds.Right - bounds.Left;
        var height = bounds.Bottom - bounds.Top;
        _appWindow.Hide();
        if (_overlay is null)
        {
            _overlay = new CaptureNotificationOverlay();
            _overlay.DismissRequested += (_, _) => DismissRequested?.Invoke(this, EventArgs.Empty);
        }
        if (refreshContent || changed || !_overlay.IsVisible)
            _overlay.Show(_message, bounds.Left, bounds.Top, width, height, scale, passive);
        else
            _overlay.UpdatePlacement(bounds.Left, bounds.Top, width, height, scale);
    }

    private (NativeRect Bounds, double Scale) CalculatePlacement()
    {
        var monitor = MonitorFromWindow(GetForegroundWindow(), 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
            throw new InvalidOperationException("The notification monitor is unavailable.");
        var work = _fullScreen ? info.Monitor : info.Work;
        // Ask the target monitor directly, avoiding a visible intermediate
        // move when the active game is on a differently scaled monitor.
        var dpi = GetDpiForMonitor(monitor, 0, out var monitorDpi, out _) == 0
            ? monitorDpi : GetDpiForWindow(_hwnd);
        var scale = dpi > 0 ? dpi / 96d : 1;
        var placement = CaptureNotificationPlacement.UpperLeftEdge(work.Left, work.Top,
            work.Right - work.Left, work.Bottom - work.Top, scale);
        var bounds = new NativeRect { Left = placement.Left, Top = placement.Top,
            Right = placement.Left + placement.Width, Bottom = placement.Top + placement.Height };
        return (bounds, scale);
    }

    private void ApplyTheme()
    {
        var highContrast = false;
        try { highContrast = _accessibility.HighContrast; } catch (COMException) { }
        _card.RequestedTheme = highContrast ? ElementTheme.Default : ElementTheme.Dark;
        var background = highContrast ? _uiSettings.GetColorValue(UIColorType.Background) : Windows.UI.Color.FromArgb(255, 27, 30, 35);
        var foreground = highContrast ? _uiSettings.GetColorValue(UIColorType.Foreground) : Windows.UI.Color.FromArgb(255, 230, 232, 235);
        var muted = highContrast ? foreground : Windows.UI.Color.FromArgb(255, 167, 173, 181);
        _root.Background = _card.Background = new SolidColorBrush(background);
        _card.BorderBrush = new SolidColorBrush(highContrast ? foreground : Windows.UI.Color.FromArgb(255, 58, 63, 70));
        _title.Foreground = new SolidColorBrush(foreground);
        _brand.Foreground = _detail.Foreground = _close.Foreground = _closeIcon.Foreground = new SolidColorBrush(muted);
        _brandMark.Fill = new SolidColorBrush(highContrast ? foreground : Windows.UI.Color.FromArgb(255, 143, 175, 210));
        var accent = highContrast ? foreground : _kind switch
        {
            CaptureNotificationKind.ClipSaved => Windows.UI.Color.FromArgb(255, 136, 198, 168),
            CaptureNotificationKind.RecordingStarted => Windows.UI.Color.FromArgb(255, 231, 146, 150),
            _ => Windows.UI.Color.FromArgb(255, 147, 180, 218)
        };
        _icon.Foreground = new SolidColorBrush(accent);
        _iconBadge.Background = new SolidColorBrush(highContrast ? background :
            Windows.UI.Color.FromArgb(255, (byte)(background.R + (accent.R - background.R) * .10),
                (byte)(background.G + (accent.G - background.G) * .10),
                (byte)(background.B + (accent.B - background.B) * .10)));
        _iconBadge.BorderThickness = highContrast ? new Thickness(1) : new Thickness(0);
        _iconBadge.BorderBrush = new SolidColorBrush(foreground);
        if (_hwnd != 0)
        {
            var dark = highContrast ? 0 : 1;
            DwmSetWindowAttribute(_hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            var caption = background.R | background.G << 8 | background.B << 16;
            DwmSetWindowAttribute(_hwnd, 35, ref caption, sizeof(int)); // DWMWA_CAPTION_COLOR
        }
    }

    private void ApplyNativeFrame()
    {
        const long caption = 0x00C00000, thickFrame = 0x00040000, systemMenu = 0x00080000,
            minimizeBox = 0x00020000, maximizeBox = 0x00010000, popup = 0x80000000;
        var style = GetWindowLongPtr(_hwnd, -16).ToInt64();
        SetWindowLongPtr(_hwnd, -16, new nint((style | popup) &
            ~(caption | thickFrame | systemMenu | minimizeBox | maximizeBox)));
        const long toolWindow = 0x80, noActivate = 0x08000000, appWindow = 0x00040000,
            dialogFrame = 0x1, windowEdge = 0x100, clientEdge = 0x200, staticEdge = 0x20000;
        var extended = GetWindowLongPtr(_hwnd, -20).ToInt64();
        SetWindowLongPtr(_hwnd, -20, new nint((extended | toolWindow | noActivate) &
            ~(appWindow | dialogFrame | windowEdge | clientEdge | staticEdge)));
        // Windows 11 otherwise supplies a light inactive outline even for a
        // presenter without a title bar. The card owns its subtle border.
        var noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(_hwnd, 34, ref noBorder, sizeof(int));
        var rounded = 2; // DWMWCP_ROUND, matches the card's 8px corner.
        DwmSetWindowAttribute(_hwnd, 33, ref rounded, sizeof(int));
        var noBackdrop = 1; // DWMSBT_NONE; the popup is a fully opaque surface.
        DwmSetWindowAttribute(_hwnd, 38, ref noBackdrop, sizeof(int));
        var margins = new NativeMargins();
        DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        SetWindowPos(_hwnd, new nint(-1), 0, 0, 0, 0, 0x0010 | 0x0001 | 0x0002 | 0x0020);
    }

    private void OnHighContrastChanged(AccessibilitySettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closed) return;
        try
        {
            ApplyTheme();
            if (_showing) ConfigurePresentation(_passive, _fullScreen, refreshContent: true);
        }
        catch (Exception exception)
        {
            HideNotification();
            Debug.WriteLine($"Capture notification theme update failed: {exception}");
        }
    });

    private nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        if (message == 0x007C && wParam == new nint(-16) && lParam != 0) // WM_STYLECHANGING / GWL_STYLE
        {
            // The presenter can restore overlapped-window chrome on later
            // visibility changes too. Keep this notification's HWND a popup
            // before the style update is committed, avoiding a light frame.
            var result = DefSubclassProc(hwnd, message, wParam, lParam);
            var styles = Marshal.PtrToStructure<NativeStyleChange>(lParam);
            const uint frame = 0x00C00000 | 0x00040000 | 0x00080000 | 0x00020000 | 0x00010000;
            styles.NewStyle = (styles.NewStyle | 0x80000000) & ~frame;
            Marshal.StructureToPtr(styles, lParam, false);
            return result;
        }
        return message switch
        {
            0x0021 => new nint(3), // WM_MOUSEACTIVATE: MA_NOACTIVATE
            0x0084 when _passive => new nint(-1), // WM_NCHITTEST: HTTRANSPARENT
            0x0083 => 0, // WM_NCCALCSIZE: the entire window is the XAML client area.
            _ => DefSubclassProc(hwnd, message, wParam, lParam)
        };
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _overlay?.Dispose();
        _overlay = null;
        if (_accessibilitySubscribed)
        {
            try { _accessibility.HighContrastChanged -= OnHighContrastChanged; } catch (COMException) { }
        }
        if (_subclassInstalled) RemoveWindowSubclass(_hwnd, _subclass, 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMargins { public int Left, Right, Top, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStyleChange { public uint OldStyle, NewStyle; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint data);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowDisplayAffinity(nint hwnd, out uint affinity);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref NativeMargins margins);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowSubclass(nint hwnd, SubclassProc procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProc procedure, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}
