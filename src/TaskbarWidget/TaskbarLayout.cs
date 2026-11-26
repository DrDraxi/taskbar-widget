using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace TaskbarWidget;

/// <summary>Snapshot of the primary taskbar's geometry.</summary>
internal readonly struct TaskbarInfo
{
    /// <summary>A taskbar exists and has sane, horizontal geometry.</summary>
    public bool Valid { get; init; }
    /// <summary>The taskbar is actually on screen (false while auto-hidden or hidden).</summary>
    public bool OnScreen { get; init; }
    public IntPtr Handle { get; init; }
    public IntPtr TrayHandle { get; init; }
    public Native.RECT Rect { get; init; }
    public Native.RECT MonitorRect { get; init; }
    public IntPtr Monitor { get; init; }
    /// <summary>Screen X where widgets must end (left edge of the notification area).</summary>
    public int RightEdge { get; init; }
    public float Scale { get; init; }

    public override string ToString() =>
        Valid ? $"taskbar {Rect} onScreen={OnScreen} right={RightEdge} scale={Scale:0.##}" : "taskbar invalid";
}

/// <summary>
/// Taskbar discovery and the cross-process widget layout.
///
/// Layout model (the fix for the old "random repositioning" bugs): every widget computes the
/// *same* deterministic layout from the same inputs — the shared order file plus each running
/// widget window's current width — and then moves **only itself**. Nobody moves anyone else's
/// window, nothing depends on where windows currently are, and the result can't oscillate.
/// Geometry is validated before use, so a missing/restarting/auto-hidden taskbar can never send a
/// widget to (0,0) at the top of the screen.
/// </summary>
internal static unsafe class TaskbarLayout
{
    public const string ClassPrefix = "TaskbarWidget_";

    // ── Taskbar ─────────────────────────────────────────────────────────

    public static TaskbarInfo Query()
    {
        var tb = Native.FindWindowW("Shell_TrayWnd", null);
        if (tb == IntPtr.Zero || !Native.GetWindowRect(tb, out var r)) return default;
        if (r.Width <= 0 || r.Height <= 0 || r.Height >= r.Width) return default; // vertical taskbars unsupported

        var mon = Native.MonitorFromRect(&r, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = (uint)sizeof(Native.MONITORINFO) };
        if (mon == IntPtr.Zero || !Native.GetMonitorInfoW(mon, &mi)) return default;

        uint dpi = Native.GetDpiForWindow(tb);
        float scale = dpi == 0 ? 1f : dpi / 96f;

        // Fully inside its monitor vertically ⇒ on screen. Auto-hide slides it mostly off-screen.
        bool onScreen = Native.IsWindowVisible(tb)
            && r.Top >= mi.rcMonitor.Top && r.Bottom <= mi.rcMonitor.Bottom
            && r.Height >= (int)(16 * scale);

        var tray = Native.FindWindowExW(tb, IntPtr.Zero, "TrayNotifyWnd", null);
        int right = r.Right - (int)(240 * scale); // conservative fallback if the tray can't be found
        if (tray != IntPtr.Zero && Native.IsWindowVisible(tray) && Native.GetWindowRect(tray, out var t)
            && t.Width > 0 && t.Left > r.Left + r.Width / 3 && t.Left <= r.Right)
            right = t.Left;

        return new TaskbarInfo
        {
            Valid = true, OnScreen = onScreen, Handle = tb, TrayHandle = tray, Rect = r,
            Monitor = mon, MonitorRect = mi.rcMonitor, RightEdge = right, Scale = scale,
        };
    }

    /// <summary>True when the foreground window is a fullscreen app on the taskbar's monitor.</summary>
    public static bool IsFullscreenAppActive(in TaskbarInfo tb)
    {
        if (!tb.Valid) return false;
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == tb.Handle || fg == Native.GetShellWindow() || fg == Native.GetDesktopWindow())
            return false;

        Native.GetWindowThreadProcessId(fg, out uint pid);
        if (pid == (uint)Environment.ProcessId) return false;

        // The desktop (Progman/WorkerW) and shell surfaces cover the whole monitor but aren't "fullscreen apps".
        var cls = Native.GetClassName(fg);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow" or "TopLevelWindowForOverflowXamlIsland")
            return false;

        if (Native.MonitorFromWindow(fg, Native.MONITOR_DEFAULTTONEAREST) != tb.Monitor) return false;

        // A normal maximized window also covers the whole monitor when the taskbar auto-hides.
        // Real fullscreen (games, video, F11) has no caption.
        const long WS_CAPTION = 0x00C00000;
        if (Native.IsZoomed(fg) && ((long)Native.GetWindowLongPtrW(fg, -16 /* GWL_STYLE */) & WS_CAPTION) == WS_CAPTION)
            return false;
        if (!Native.GetWindowRect(fg, out var r) || Native.IsCloaked(fg)) return false;

        var m = tb.MonitorRect;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    // ── Running widgets ─────────────────────────────────────────────────

    internal readonly record struct WidgetWindow(string Name, IntPtr Hwnd, Native.RECT Rect);

    [ThreadStatic] static List<WidgetWindow>? _enumTarget;

    /// <summary>All visible widget windows (from any process, including widgets built on older versions of this library).</summary>
    public static List<WidgetWindow> RunningWidgets()
    {
        var list = new List<WidgetWindow>();
        _enumTarget = list;
        try { Native.EnumWindows(&EnumProc, IntPtr.Zero); }
        finally { _enumTarget = null; }
        return list;
    }

    [UnmanagedCallersOnly]
    static int EnumProc(IntPtr hwnd, IntPtr _)
    {
        try
        {
            if (Native.IsWindowVisible(hwnd) && Native.ClassStartsWith(hwnd, ClassPrefix) && Native.GetWindowRect(hwnd, out var r))
                _enumTarget?.Add(new WidgetWindow(Native.GetClassName(hwnd)[ClassPrefix.Length..], hwnd, r));
        }
        catch { /* never let an exception cross the native boundary */ }
        return 1;
    }

    /// <summary>
    /// Computes the screen X for a widget. Widgets are packed right-to-left from the tray edge in
    /// order-file order (index 0 = rightmost). Unknown names go after known ones, alphabetically,
    /// so two widgets that both missed registration still agree on the result.
    /// </summary>
    public static int ComputeX(in TaskbarInfo tb, string selfName, IntPtr selfHwnd, int selfWidth,
        List<WidgetWindow>? running = null, IReadOnlyList<string>? order = null)
    {
        running ??= RunningWidgets();
        order ??= WidgetOrder.Read();

        var items = new List<(int Rank, string Name, long Tie, int Width, bool Self)>(running.Count + 1);
        items.Add((Rank(order, selfName), selfName, (long)selfHwnd, selfWidth, true));
        foreach (var w in running)
        {
            if (w.Hwnd == selfHwnd) continue;
            items.Add((Rank(order, w.Name), w.Name, (long)w.Hwnd, w.Rect.Width, false));
        }
        items.Sort((a, b) =>
        {
            int c = a.Rank.CompareTo(b.Rank);
            if (c == 0) c = string.CompareOrdinal(a.Name, b.Name);
            if (c == 0) c = a.Tie.CompareTo(b.Tie);
            return c;
        });

        int right = tb.RightEdge;
        foreach (var it in items)
        {
            int x = right - it.Width;
            if (it.Self) return x;
            right = x;
        }
        return right - selfWidth; // unreachable
    }

    static int Rank(IReadOnlyList<string> order, string name)
    {
        for (int i = 0; i < order.Count; i++)
            if (order[i] == name) return i;
        return int.MaxValue;
    }
}

/// <summary>
/// Shared widget order (index 0 = rightmost), persisted at %LOCALAPPDATA%\TaskbarWidget\widget-order.json.
/// The format is unchanged from earlier versions, so existing orders are kept.
/// </summary>
internal static class WidgetOrder
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarWidget", "widget-order.json");

    static uint _repositionMsg;
    static List<string> _cache = new();

    /// <summary>Registered message every widget listens to; same name as earlier versions for compatibility.</summary>
    public static uint RepositionMessage =>
        _repositionMsg != 0 ? _repositionMsg : (_repositionMsg = Native.RegisterWindowMessageW("TaskbarWidget_Reposition"));

    public static void Broadcast() =>
        Native.PostMessageW(Native.HWND_BROADCAST, RepositionMessage, IntPtr.Zero, IntPtr.Zero);

    public static List<string> Read()
    {
        try
        {
            // Re-read every time: it's ~100 bytes, and a timestamp cache can miss two quick
            // same-length reorders during a drag (leaving widgets overlapping).
            if (!File.Exists(FilePath)) return new List<string>();
            using var doc = JsonDocument.Parse(File.ReadAllBytes(FilePath));
            var list = new List<string>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var e in doc.RootElement.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s && !list.Contains(s))
                        list.Add(s);

            _cache = list;
            return list;
        }
        catch { return _cache; } // mid-write or corrupt: keep the last good order
    }

    /// <summary>Adds the name at the end (leftmost) if it isn't known yet.</summary>
    public static void EnsureRegistered(string name) => Update(order =>
    {
        if (order.Contains(name)) return false;
        order.Add(name);
        return true;
    });

    /// <summary>Saves <paramref name="activeOrder"/> first, keeping not-running widgets' relative order after it.</summary>
    public static void Save(IReadOnlyList<string> activeOrder) => Update(order =>
    {
        var merged = new List<string>(activeOrder);
        foreach (var n in order) if (!merged.Contains(n)) merged.Add(n);
        if (merged.SequenceEqual(order)) return false;
        order.Clear(); order.AddRange(merged);
        return true;
    });

    /// <summary>Read-modify-write under a cross-process mutex (widgets usually all start together at login).</summary>
    static void Update(Func<List<string>, bool> mutate)
    {
        try
        {
            using var mutex = new Mutex(false, @"Local\TaskbarWidget.OrderFile");
            bool owned = false;
            try
            {
                try { owned = mutex.WaitOne(2000); } catch (AbandonedMutexException) { owned = true; }
                var order = new List<string>(Read());
                if (!mutate(order)) return;

                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var sb = new StringBuilder("[\n");
                for (int i = 0; i < order.Count; i++)
                    sb.Append("  \"").Append(JsonEncodedText.Encode(order[i]).ToString()).Append(i < order.Count - 1 ? "\",\n" : "\"\n");
                sb.Append(']');

                var tmp = $"{FilePath}.{Environment.ProcessId}.tmp";
                File.WriteAllText(tmp, sb.ToString());
                File.Move(tmp, FilePath, overwrite: true);
                _cache = order;
            }
            finally { if (owned) mutex.ReleaseMutex(); }
        }
        catch (Exception ex) { Log.Write($"order file update failed: {ex.Message}"); }
    }
}

/// <summary>Tiny append-only diagnostics log at %LOCALAPPDATA%\TaskbarWidget\logs\&lt;name&gt;.log.</summary>
internal static class Log
{
    static string? _path;
    static readonly bool Verbose = Environment.GetEnvironmentVariable("TASKBARWIDGET_LOG") == "1";

    public static void Init(string name)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TaskbarWidget", "logs");
        _path = Path.Combine(dir, name + ".log");
        try
        {
            Directory.CreateDirectory(dir);
            if (File.Exists(_path) && new FileInfo(_path).Length > 256 * 1024) File.Delete(_path);
        }
        catch { }
    }

    public static void Debug(string msg, [CallerMemberName] string caller = "")
    {
        if (Verbose) Write($"{caller}: {msg}");
    }

    public static void Write(string msg)
    {
        if (_path == null) return;
        try { File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}\n"); } catch { }
    }
}
