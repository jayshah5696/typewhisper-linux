using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TypeWhisper.Linux.Services;

/// <summary>Keep pointer-operated overlay buttons from becoming the keyboard input target.</summary>
internal static partial class X11NonActivatingWindow
{
    public static void Apply(nuint window, bool floatingControl)
    {
        if (window == 0)
            return;

        try
        {
            var display = XOpenDisplay(null);
            if (display == IntPtr.Zero)
                return;
            try
            {
                var pointer = XGetWMHints(display, window);
                try
                {
                    var hints = pointer == IntPtr.Zero ? default : Marshal.PtrToStructure<WindowHints>(pointer);
                    hints.Flags |= 1; // ICCCM InputHint. Preserve every other window-manager hint.
                    hints.Input = floatingControl ? 0 : 1;
                    XSetWMHints(display, window, ref hints);
                    XFlush(display);
                }
                finally
                {
                    if (pointer != IntPtr.Zero)
                        XFree(pointer);
                }
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[X11NonActivatingWindow] {ex.GetType().Name}: {ex.Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowHints
    {
        public nint Flags;
        public int Input;
        public int InitialState;
        public nuint IconPixmap;
        public nuint IconWindow;
        public int IconX;
        public int IconY;
        public nuint IconMask;
        public nuint WindowGroup;
    }

    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr XOpenDisplay(string? name);
    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(IntPtr display);
    [LibraryImport("libX11.so.6")]
    private static partial IntPtr XGetWMHints(IntPtr display, nuint window);
    [LibraryImport("libX11.so.6")]
    private static partial int XSetWMHints(IntPtr display, nuint window, ref WindowHints hints);
    [LibraryImport("libX11.so.6")]
    private static partial int XFree(IntPtr pointer);
    [LibraryImport("libX11.so.6")]
    private static partial int XFlush(IntPtr display);
}
