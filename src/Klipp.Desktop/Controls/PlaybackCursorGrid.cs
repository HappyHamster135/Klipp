using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace Klipp.Desktop.Controls;

/// <summary>A cursor override confined to the video surface, including when it is reparented.</summary>
public sealed class PlaybackCursorGrid : Grid
{
    private static InputCursor? _invisibleCursor;
    private bool _cursorHidden;

    public PlaybackCursorGrid()
    {
        Unloaded += (_, _) => SetCursorHidden(false);
    }

    public void SetCursorHidden(bool hidden)
    {
        if (_cursorHidden == hidden) return;
        // A null ProtectedCursor inherits the normal pointer; it does not hide it.
        // A transparent cursor avoids changing Windows' shared ShowCursor counter.
        ProtectedCursor = hidden ? _invisibleCursor ??= CreateInvisibleCursor() : null;
        _cursorHidden = hidden;
    }

    private static unsafe InputCursor CreateInvisibleCursor()
    {
        var andMask = new byte[128];
        Array.Fill(andMask, (byte)0xff);
        var handle = CreateCursor(0, 0, 0, 32, 32, andMask, new byte[128]);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());

        nint className = 0, factory = 0, cursor = 0;
        try
        {
            const string runtimeClass = "Microsoft.UI.Input.InputCursor";
            Marshal.ThrowExceptionForHR(WindowsCreateString(runtimeClass, runtimeClass.Length, out className));
            // IInputCursorStaticsInterop derives from IInspectable (six base slots).
            var iid = new Guid("ac6f5065-90c4-46ce-beb7-05e138e54117");
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, in iid, out factory));
            var create = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(nint**)factory)[6];
            Marshal.ThrowExceptionForHR(create(factory, handle, &cursor));
            // CreateFromHCursor copies the image; both native references can be released.
            return WinRT.MarshalInterface<InputCursor>.FromAbi(cursor);
        }
        finally
        {
            if (cursor != 0) Marshal.Release(cursor);
            if (factory != 0) Marshal.Release(factory);
            if (className != 0) WindowsDeleteString(className);
            DestroyCursor(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateCursor(nint instance, int hotSpotX, int hotSpotY,
        int width, int height, byte[] andMask, byte[] xorMask);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyCursor(nint cursor);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int WindowsCreateString(string value, int length, out nint result);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(nint value);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(nint className, in Guid iid, out nint factory);
}
