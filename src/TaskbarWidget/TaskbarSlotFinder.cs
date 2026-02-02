namespace TaskbarWidget;

/// <summary>
/// Represents a rectangular region.
/// </summary>
public readonly struct Bounds
{
    public int Left { get; init; }
    public int Top { get; init; }
    public int Right { get; init; }
    public int Bottom { get; init; }
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    internal static Bounds FromNative(Native.RECT rect) => new()
    {
        Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom
    };
}

/// <summary>
/// Represents an available slot in the taskbar for a widget.
/// </summary>
public readonly struct TaskbarSlot
{
    public int X { get; init; }
    public int Y { get; init; }
    public int AvailableWidth { get; init; }
    public int Height { get; init; }
    public bool IsValid { get; init; }
}

/// <summary>
/// Finds available slots in the Windows taskbar for widget injection.
/// Handles collision detection with other injected widgets.
/// </summary>
public sealed class TaskbarSlotFinder
{
    private static readonly HashSet<string> SystemWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "TrayNotifyWnd", "ReBarWindow32", "MSTaskSwWClass",
        "MSTaskListWClass", "ToolbarWindow32", "TrayClockWClass", "TrayShowDesktopButtonWClass",
        "Button", "TrayButton", "TrayDummySearchControl", "Start",
        "Windows.UI.Composition.DesktopWindowContentBridge",
        "Windows.UI.Input.InputSite.WindowClass", "Xaml_WindowedPopupClass"
    };

    private readonly IntPtr _hwndTaskbar;
    private readonly IntPtr _hwndTrayNotify;
    private readonly Native.RECT _taskbarRect;
    private readonly double _dpiScale;

    public TaskbarSlotFinder()
    {
        _hwndTaskbar = Native.FindTaskbar();
        if (_hwndTaskbar != IntPtr.Zero)
        {
            _hwndTrayNotify = Native.FindTrayNotifyWnd(_hwndTaskbar);
            Native.GetWindowRect(_hwndTaskbar, out _taskbarRect);
            _dpiScale = Native.GetScaleFactor(_hwndTaskbar);
        }
    }

    public bool IsTaskbarFound => _hwndTaskbar != IntPtr.Zero;
    public IntPtr TaskbarHandle => _hwndTaskbar;
    public double DpiScale => _dpiScale;
    public Bounds TaskbarBounds => Bounds.FromNative(_taskbarRect);

    /// <summary>
    /// Finds an available slot for a widget of the specified width.
    /// </summary>
    /// <param name="widgetWidth">Width of the widget in pixels.</param>
    /// <param name="ownHandle">Handle of the widget's own window (to exclude from collision detection).</param>
    /// <param name="margin">Margin between widgets in pixels.</param>
    /// <returns>A TaskbarSlot indicating where the widget should be positioned.</returns>
    public TaskbarSlot FindSlot(int widgetWidth, IntPtr ownHandle = default, int margin = 4)
    {
        if (_hwndTaskbar == IntPtr.Zero)
            return new TaskbarSlot { IsValid = false };

        int rightBoundary;
        if (_hwndTrayNotify != IntPtr.Zero)
        {
            Native.GetWindowRect(_hwndTrayNotify, out var trayRect);
            rightBoundary = trayRect.Left - _taskbarRect.Left - margin;
        }
        else
        {
            rightBoundary = _taskbarRect.Width - 100;
        }

        // Get injected windows sorted by right edge (rightmost first)
        var injectedWindows = GetInjectedWindows()
            .Where(w => w.Handle != ownHandle)
            .OrderByDescending(w => w.RightEdge)
            .ToList();

        // Start from rightmost position and find non-overlapping slot
        int slotX = rightBoundary - widgetWidth;
        int slotRight = rightBoundary;

        foreach (var widget in injectedWindows)
        {
            bool overlaps = !(slotRight <= widget.LeftEdge || slotX >= widget.RightEdge);
            if (overlaps)
            {
                slotRight = widget.LeftEdge - margin;
                slotX = slotRight - widgetWidth;
            }
        }

        // Don't go past 1/3 of taskbar (leave room for start menu and task buttons)
        var minX = _taskbarRect.Width / 3;
        slotX = Math.Max(slotX, minX);

        return new TaskbarSlot
        {
            X = slotX,
            Y = 0,
            AvailableWidth = slotRight - slotX,
            Height = _taskbarRect.Height,
            IsValid = (slotRight - slotX) >= widgetWidth
        };
    }

    private List<(IntPtr Handle, int LeftEdge, int RightEdge)> GetInjectedWindows()
    {
        var result = new List<(IntPtr, int, int)>();

        Native.EnumChildWindows(_hwndTaskbar, (hwnd, lParam) =>
        {
            if (Native.GetParent(hwnd) != _hwndTaskbar) return true;

            var className = Native.GetClassName(hwnd);
            if (!Native.IsWindowVisible(hwnd) || IsSystemClass(className)) return true;

            Native.GetWindowRect(hwnd, out var rect);
            result.Add((hwnd, rect.Left - _taskbarRect.Left, rect.Right - _taskbarRect.Left));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    private static bool IsSystemClass(string className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        if (SystemWindowClasses.Contains(className)) return true;
        if (className.StartsWith("Windows.UI.", StringComparison.OrdinalIgnoreCase)) return true;
        if (className.StartsWith("Shell_", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
