using System.Runtime.InteropServices;

namespace TaskbarWidget;

/// <summary>Creates windows whose messages are routed to a managed handler (AOT-safe, no delegates marshalled).</summary>
internal static unsafe class Win32Window
{
    public delegate IntPtr Handler(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    static readonly HashSet<string> Registered = new();

    public static IntPtr Create(string className, uint exStyle, IntPtr owner, Handler handler)
    {
        if (Registered.Add(className))
        {
            fixed (char* cls = className)
            {
                var wc = new Native.WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                    // No CS_DBLCLKS: fast repeated clicks must arrive as separate button-down messages.
                    lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&Proc,
                    hInstance = Native.GetModuleHandleW(IntPtr.Zero),
                    hCursor = Native.LoadCursorW(IntPtr.Zero, Native.IDC_ARROW),
                    lpszClassName = cls,
                };
                Native.RegisterClassExW(&wc);
            }
        }

        var gch = GCHandle.Alloc(handler);
        var hwnd = Native.CreateWindowExW(exStyle, className, className, Native.WS_POPUP,
            0, 0, 0, 0, owner, IntPtr.Zero, Native.GetModuleHandleW(IntPtr.Zero), GCHandle.ToIntPtr(gch));
        if (hwnd == IntPtr.Zero) gch.Free();
        return hwnd;
    }

    [UnmanagedCallersOnly]
    static IntPtr Proc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Native.WM_NCCREATE)
            Native.SetWindowLongPtrW(hwnd, Native.GWLP_USERDATA, ((Native.CREATESTRUCTW*)lParam)->lpCreateParams);

        var ptr = Native.GetWindowLongPtrW(hwnd, Native.GWLP_USERDATA);
        if (ptr == IntPtr.Zero) return Native.DefWindowProcW(hwnd, msg, wParam, lParam);

        var gch = GCHandle.FromIntPtr(ptr);
        IntPtr result;
        try
        {
            result = ((Handler)gch.Target!)(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            Log.Write($"Unhandled exception in message 0x{msg:X4}: {ex}");
            result = Native.DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        if (msg == Native.WM_NCDESTROY)
        {
            Native.SetWindowLongPtrW(hwnd, Native.GWLP_USERDATA, IntPtr.Zero);
            gch.Free();
        }
        return result;
    }
}
