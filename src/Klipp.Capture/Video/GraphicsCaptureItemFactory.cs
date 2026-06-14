using System.Runtime.InteropServices;
using WinRT;
using Windows.Graphics.Capture;

namespace Klipp.Capture.Video;

/// <summary>
/// Factory för att skapa <see cref="GraphicsCaptureItem"/> från HWND eller HMONITOR.
/// </summary>
/// <remarks>
/// WGC exponerar inte detta direkt i C# — vi måste gå via COM-interfacet
/// <c>IGraphicsCaptureItemInterop</c>. Detta är standard Win32-interop som
/// alla WGC-applikationer skriver själva (se Microsofts officiella samples).
/// </remarks>
internal static class GraphicsCaptureItemFactory
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow([In] nint window, [In] ref Guid iid);
        nint CreateForMonitor([In] nint monitor, [In] ref Guid iid);
    }

    /// <summary>Skapar en <see cref="GraphicsCaptureItem"/> för ett fönster (HWND).</summary>
    public static GraphicsCaptureItem CreateForWindow(nint hwnd)
    {
        if (hwnd == nint.Zero)
            throw new ArgumentException("HWND får inte vara noll.", nameof(hwnd));

        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var itemPtr = interop.CreateForWindow(hwnd, ref iid);

        if (itemPtr == nint.Zero)
            throw new InvalidOperationException("CreateForWindow returnerade null.");

        return GraphicsCaptureItem.FromAbi(itemPtr);
    }

    /// <summary>Skapar en <see cref="GraphicsCaptureItem"/> för en skärm (HMONITOR).</summary>
    public static GraphicsCaptureItem CreateForMonitor(nint hmonitor)
    {
        if (hmonitor == nint.Zero)
            throw new ArgumentException("HMONITOR får inte vara noll.", nameof(hmonitor));

        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var itemPtr = interop.CreateForMonitor(hmonitor, ref iid);

        if (itemPtr == nint.Zero)
            throw new InvalidOperationException("CreateForMonitor returnerade null.");

        return GraphicsCaptureItem.FromAbi(itemPtr);
    }
}
