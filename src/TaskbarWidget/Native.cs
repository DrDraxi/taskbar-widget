using System.Runtime.InteropServices;

namespace TaskbarWidget;

/// <summary>Minimal Win32 surface. Everything is blittable so it is NativeAOT friendly.</summary>
internal static unsafe class Native
{
    // ── Messages ────────────────────────────────────────────────────────
    public const uint WM_CREATE = 0x0001, WM_DESTROY = 0x0002, WM_SETTINGCHANGE = 0x001A,
        WM_SETCURSOR = 0x0020, WM_MOUSEACTIVATE = 0x0021, WM_DISPLAYCHANGE = 0x007E,
        WM_NCCREATE = 0x0081, WM_NCDESTROY = 0x0082, WM_KEYDOWN = 0x0100, WM_TIMER = 0x0113,
        WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203,
        WM_RBUTTONDOWN = 0x0204, WM_RBUTTONUP = 0x0205, WM_RBUTTONDBLCLK = 0x0206,
        WM_MBUTTONUP = 0x0208, WM_CAPTURECHANGED = 0x0215, WM_POWERBROADCAST = 0x0218,
        WM_MOUSELEAVE = 0x02A3, WM_DPICHANGED = 0x02E0, WM_THEMECHANGED = 0x031A,
        WM_APP = 0x8000;

    public const int MA_NOACTIVATE = 3;
    public const int PBT_APMRESUMEAUTOMATIC = 0x12;
    public const int VK_ESCAPE = 0x1B, VK_SHIFT = 0x10;

    // ── Styles ──────────────────────────────────────────────────────────
    public const uint WS_POPUP = 0x80000000;
    public const uint WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
        WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    public const uint CS_DBLCLKS = 0x8;

    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10,
        SWP_SHOWWINDOW = 0x40, SWP_NOOWNERZORDER = 0x200;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;
    public static readonly IntPtr HWND_TOPMOST = -1;
    public static readonly IntPtr HWND_BROADCAST = 0xFFFF;

    public const int GWLP_USERDATA = -21;
    public const uint MONITOR_DEFAULTTONEAREST = 2;
    public const uint ULW_ALPHA = 2;
    public const uint TME_LEAVE = 2;
    public const int IDC_ARROW = 32512, IDC_SIZEWE = 32644, IDC_HAND = 32649;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;

    public const uint MF_STRING = 0, MF_GRAYED = 1, MF_CHECKED = 8, MF_SEPARATOR = 0x800;
    public const uint TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;

    public const int DWMWA_CLOAKED = 14;

    // ── Structs ─────────────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
        public override readonly string ToString() => $"[{Left},{Top} {Width}x{Height}]";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd; public uint message; public IntPtr wParam, lParam;
        public uint time; public POINT pt; public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASSEXW
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CREATESTRUCTW { public IntPtr lpCreateParams; /* rest unused */ }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public uint cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TRACKMOUSEEVENT { public uint cbSize, dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

    // ── user32 ──────────────────────────────────────────────────────────
    const string U = "user32.dll";

    [DllImport(U, CharSet = CharSet.Unicode, ExactSpelling = true)] public static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport(U, CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string className, string? title, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport(U, ExactSpelling = true)] public static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport(U, ExactSpelling = true)] public static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport(U, ExactSpelling = true)] public static extern int GetMessageW(MSG* msg, IntPtr hwnd, uint min, uint max);
    [DllImport(U, ExactSpelling = true)] public static extern bool TranslateMessage(MSG* msg);
    [DllImport(U, ExactSpelling = true)] public static extern IntPtr DispatchMessageW(MSG* msg);
    [DllImport(U, ExactSpelling = true)] public static extern void PostQuitMessage(int code);
    [DllImport(U, ExactSpelling = true)] public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport(U, CharSet = CharSet.Unicode, ExactSpelling = true)] public static extern uint RegisterWindowMessageW(string name);

    [DllImport(U, ExactSpelling = true)] public static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    [DllImport(U, ExactSpelling = true)] public static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport(U, ExactSpelling = true)] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport(U, ExactSpelling = true)] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport(U, ExactSpelling = true)] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport(U, ExactSpelling = true)] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport(U, ExactSpelling = true)] public static extern bool IsZoomed(IntPtr hwnd);
    [DllImport(U, ExactSpelling = true)] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport(U, ExactSpelling = true)] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport(U, CharSet = CharSet.Unicode, ExactSpelling = true)] public static extern IntPtr FindWindowW(string? cls, string? title);
    [DllImport(U, CharSet = CharSet.Unicode, ExactSpelling = true)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport(U, ExactSpelling = true)] public static extern int GetClassNameW(IntPtr hwnd, char* buf, int max);
    [DllImport(U, ExactSpelling = true)] public static extern bool EnumWindows(delegate* unmanaged<IntPtr, IntPtr, int> cb, IntPtr l);
    [DllImport(U, ExactSpelling = true)] public static extern IntPtr GetForegroundWindow();
    [DllImport(U, ExactSpelling = true, SetLastError = true)] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport(U, ExactSpelling = true)] public static extern IntPtr GetShellWindow();
    [DllImport(U, ExactSpelling = true)] public static extern IntPtr GetDesktopWindow();

    [DllImport(U, ExactSpelling = true)] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport(U, ExactSpelling = true)] public static extern IntPtr MonitorFromRect(RECT* rect, uint flags);
    [DllImport(U, ExactSpelling = true)] public static extern bool GetMonitorInfoW(IntPtr mon, MONITORINFO* mi);
    [DllImport(U, ExactSpelling = true)] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport(U, ExactSpelling = true)] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);

    [DllImport(U, ExactSpelling = true)]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, POINT* pptDst, SIZE* psize,
        IntPtr hdcSrc, POINT* pptSrc, uint crKey, BLENDFUNCTION* blend, uint flags);

    [DllImport(U, ExactSpelling = true)] public static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport(U, ExactSpelling = true)] public static extern bool ReleaseCapture();
    [DllImport(U, ExactSpelling = true)] public static extern IntPtr GetCapture();
    [DllImport(U, ExactSpelling = true)] public static extern bool GetCursorPos(out POINT pt);
    [DllImport(U, ExactSpelling = true)] public static extern bool TrackMouseEvent(TRACKMOUSEEVENT* tme);
    [DllImport(U, ExactSpelling = true)] public static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
    [DllImport(U, ExactSpelling = true)] public static extern IntPtr SetCursor(IntPtr cursor);
    [DllImport(U, ExactSpelling = true)] public static extern short GetKeyState(int vk);

    [DllImport(U, ExactSpelling = true)] public static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
    [DllImport(U, ExactSpelling = true)] public static extern bool KillTimer(IntPtr hwnd, IntPtr id);

    [DllImport(U, ExactSpelling = true)] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport(U, ExactSpelling = true)] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport(U, ExactSpelling = true)]
    public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod,
        delegate* unmanaged<IntPtr, uint, IntPtr, int, int, uint, uint, void> proc, uint pid, uint tid, uint flags);
    [DllImport(U, ExactSpelling = true)] public static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport(U, ExactSpelling = true)] public static extern IntPtr CreatePopupMenu();
    [DllImport(U, CharSet = CharSet.Unicode, ExactSpelling = true)] public static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string? text);
    [DllImport(U, ExactSpelling = true, SetLastError = true)] public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport(U, ExactSpelling = true)] public static extern bool DestroyMenu(IntPtr menu);

    // ── gdi32 ───────────────────────────────────────────────────────────
    [DllImport("gdi32.dll", ExactSpelling = true)] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll", ExactSpelling = true)] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll", ExactSpelling = true)] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll", ExactSpelling = true)] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", ExactSpelling = true)]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, BITMAPINFOHEADER* bmi, uint usage, void** bits, IntPtr section, uint offset);

    // ── misc ────────────────────────────────────────────────────────────
    [DllImport("kernel32.dll", ExactSpelling = true)] public static extern IntPtr GetModuleHandleW(IntPtr name);
    [DllImport("ole32.dll", ExactSpelling = true)] public static extern int CoInitializeEx(IntPtr reserved, uint coinit);
    [DllImport("dwmapi.dll", ExactSpelling = true)] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, void* value, int size);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern IntPtr ShellExecuteW(IntPtr hwnd, string? op, string file, string? args, string? dir, int show);

    // Undocumented but stable since 1809: lets Win32 menus follow dark mode.
    [DllImport("uxtheme.dll", EntryPoint = "#135")] public static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] public static extern void FlushMenuThemes();

    // ── helpers ─────────────────────────────────────────────────────────
    public static string GetClassName(IntPtr hwnd)
    {
        char* buf = stackalloc char[128];
        int n = GetClassNameW(hwnd, buf, 128);
        return n > 0 ? new string(buf, 0, n) : string.Empty;
    }

    /// <summary>Cheap check against a known class prefix without allocating for non-matches.</summary>
    public static bool ClassStartsWith(IntPtr hwnd, string prefix)
    {
        char* buf = stackalloc char[128];
        int n = GetClassNameW(hwnd, buf, 128);
        if (n < prefix.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
            if (buf[i] != prefix[i]) return false;
        return true;
    }

    public static bool IsCloaked(IntPtr hwnd)
    {
        int cloaked = 0;
        return DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    public static int LoWord(IntPtr v) => (short)((long)v & 0xFFFF);
    public static int HiWord(IntPtr v) => (short)(((long)v >> 16) & 0xFFFF);
}
