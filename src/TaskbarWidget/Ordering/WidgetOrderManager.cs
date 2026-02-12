using System.Text.Json;

namespace TaskbarWidget.Ordering;

/// <summary>
/// Manages widget ordering across processes via a shared JSON file.
/// </summary>
public static class WidgetOrderManager
{
    private static readonly string OrderFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TaskbarWidget", "widget-order.json");

    private static uint _repositionMessage;

    /// <summary>
    /// Get the registered Windows message for cross-process reposition signaling.
    /// </summary>
    public static uint RepositionMessage
    {
        get
        {
            if (_repositionMessage == 0)
                _repositionMessage = Native.RegisterWindowMessageW("TaskbarWidget_Reposition");
            return _repositionMessage;
        }
    }

    /// <summary>
    /// Register a widget name in the order file if not already present.
    /// </summary>
    public static void Register(string widgetName)
    {
        var order = ReadOrder();
        if (!order.Contains(widgetName))
        {
            order.Add(widgetName);
            WriteOrder(order);
        }
    }

    /// <summary>
    /// Get the current order as a name→index mapping.
    /// Index 0 = rightmost position.
    /// </summary>
    public static Dictionary<string, int> GetOrderMap()
    {
        var order = ReadOrder();
        var map = new Dictionary<string, int>();
        for (int i = 0; i < order.Count; i++)
            map[order[i]] = i;
        return map;
    }

    /// <summary>
    /// Get the current order list.
    /// </summary>
    public static List<string> GetOrder() => ReadOrder();

    /// <summary>
    /// Get the order index for a widget (-1 if not found).
    /// </summary>
    public static int GetOrderIndex(string widgetName)
    {
        var order = ReadOrder();
        return order.IndexOf(widgetName);
    }

    /// <summary>
    /// Save a new order.
    /// </summary>
    public static void SaveOrder(string[] names)
    {
        WriteOrder(new List<string>(names));
    }

    /// <summary>
    /// Broadcast the reposition message to all top-level windows.
    /// </summary>
    public static void BroadcastReposition()
    {
        Native.PostMessageW(Native.HWND_BROADCAST, RepositionMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Position all widgets atomically based on the saved order.
    /// Avoids chicken-and-egg issues where individual PositionOverTaskbar calls
    /// collide with neighbors that haven't moved yet.
    /// </summary>
    /// <param name="callerName">Name of the widget whose size just changed (optional).</param>
    /// <param name="callerWidth">The new width the caller will have (used instead of stale on-screen size).</param>
    public static void RepositionAll(string? callerName = null, int callerWidth = 0)
    {
        var order = ReadOrder();
        if (order.Count == 0) return;

        // Enumerate all TaskbarWidget_* windows and map to order
        var windows = new Dictionary<string, (IntPtr Hwnd, int Width)>();
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;

            var className = Native.GetClassName(hwnd);
            if (!className.StartsWith("TaskbarWidget_", StringComparison.Ordinal)) return true;

            string name = className.Substring("TaskbarWidget_".Length);
            Native.GetWindowRect(hwnd, out var rect);
            // Use caller-provided width if this is the widget that just changed size,
            // since its on-screen rect is still the old size.
            int width = (callerName != null && name == callerName && callerWidth > 0)
                ? callerWidth
                : rect.Width;
            windows[name] = (hwnd, width);

            return true;
        }, IntPtr.Zero);

        if (windows.Count < 2) return;

        var taskbar = Native.FindTaskbar();
        if (taskbar == IntPtr.Zero) return;

        Native.GetWindowRect(taskbar, out var taskbarRect);
        var trayNotify = Native.FindTrayNotifyWnd(taskbar);

        const int margin = 4;
        int rightBoundary;
        if (trayNotify != IntPtr.Zero)
        {
            Native.GetWindowRect(trayNotify, out var trayRect);
            rightBoundary = trayRect.Left - margin;
        }
        else
        {
            rightBoundary = taskbarRect.Right - 100;
        }

        // Position right-to-left following the saved order
        int currentRight = rightBoundary;
        foreach (var name in order)
        {
            if (!windows.TryGetValue(name, out var w)) continue;

            int screenX = currentRight - w.Width;
            Native.SetWindowPos(w.Hwnd, Native.HWND_TOPMOST, screenX, taskbarRect.Top, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            currentRight = screenX - margin;
        }

        // Broadcast so widgets re-render at their new positions
        BroadcastReposition();
    }

    private static List<string> ReadOrder()
    {
        try
        {
            if (!File.Exists(OrderFilePath))
                return new List<string>();

            var json = File.ReadAllText(OrderFilePath);
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static void WriteOrder(List<string> order)
    {
        try
        {
            var dir = Path.GetDirectoryName(OrderFilePath)!;
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(order, new JsonSerializerOptions { WriteIndented = true });
            // Write to temp file then move for atomicity
            var temp = OrderFilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, OrderFilePath, overwrite: true);
        }
        catch
        {
            // Silently ignore write failures
        }
    }
}
