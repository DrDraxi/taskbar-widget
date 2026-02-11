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
/// Information about a window in the taskbar.
/// </summary>
public readonly struct TaskbarChildInfo
{
    public IntPtr Handle { get; init; }
    public string ClassName { get; init; }
    public Bounds Bounds { get; init; }
    public bool IsVisible { get; init; }
    public bool IsSystemWindow { get; init; }
    public int RightEdge { get; init; }
    public int LeftEdge { get; init; }
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
        "TrayInputIndicatorWClass", "NotifyIconOverflowWindow", "SysPager", "SystemTray_Main",
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
    public IntPtr TrayNotifyHandle => _hwndTrayNotify;
    public double DpiScale => _dpiScale;
    public Bounds TaskbarBounds => Bounds.FromNative(_taskbarRect);

    /// <summary>
    /// Get all child windows of the taskbar.
    /// </summary>
    public List<TaskbarChildInfo> GetChildWindows()
    {
        var children = new List<TaskbarChildInfo>();
        if (_hwndTaskbar == IntPtr.Zero) return children;

        Native.EnumChildWindows(_hwndTaskbar, (hwnd, lParam) =>
        {
            if (Native.GetParent(hwnd) != _hwndTaskbar) return true;

            var className = Native.GetClassName(hwnd);
            var isVisible = Native.IsWindowVisible(hwnd);
            Native.GetWindowRect(hwnd, out var rect);

            var leftEdge = rect.Left - _taskbarRect.Left;
            var rightEdge = rect.Right - _taskbarRect.Left;

            children.Add(new TaskbarChildInfo
            {
                Handle = hwnd,
                ClassName = className,
                Bounds = Bounds.FromNative(rect),
                IsVisible = isVisible,
                IsSystemWindow = IsSystemClass(className),
                LeftEdge = leftEdge,
                RightEdge = rightEdge
            });

            return true;
        }, IntPtr.Zero);

        return children;
    }

    /// <summary>
    /// Get only injected (non-system) visible windows in the taskbar.
    /// Finds both child windows and top-level TaskbarWidget windows positioned over the taskbar.
    /// </summary>
    public List<TaskbarChildInfo> GetInjectedWindows()
    {
        var injected = new List<TaskbarChildInfo>();

        // Find child windows of taskbar that aren't system windows
        foreach (var child in GetChildWindows())
        {
            if (child.IsVisible && !child.IsSystemWindow)
                injected.Add(child);
        }

        // Also find top-level TaskbarWidget windows (not parented to taskbar)
        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;

            var className = Native.GetClassName(hwnd);
            if (!className.StartsWith("TaskbarWidget_", StringComparison.Ordinal)) return true;

            // Check if already found as a child
            foreach (var existing in injected)
                if (existing.Handle == hwnd) return true;

            Native.GetWindowRect(hwnd, out var rect);
            var leftEdge = rect.Left - _taskbarRect.Left;
            var rightEdge = rect.Right - _taskbarRect.Left;

            injected.Add(new TaskbarChildInfo
            {
                Handle = hwnd,
                ClassName = className,
                Bounds = Bounds.FromNative(rect),
                IsVisible = true,
                IsSystemWindow = false,
                LeftEdge = leftEdge,
                RightEdge = rightEdge
            });

            return true;
        }, IntPtr.Zero);

        return injected.OrderBy(c => c.LeftEdge).ToList();
    }

    /// <summary>
    /// Finds an available slot for a widget of the specified width.
    /// </summary>
    /// <param name="widgetWidth">Width of the widget in pixels.</param>
    /// <param name="ownHandle">Handle of the widget's own window (to exclude from collision detection).</param>
    /// <param name="margin">Margin between widgets in pixels.</param>
    /// <param name="log">Optional logging callback.</param>
    /// <returns>A TaskbarSlot indicating where the widget should be positioned.</returns>
    public TaskbarSlot FindSlot(int widgetWidth, IntPtr ownHandle = default, int margin = 4, Action<string>? log = null)
    {
        return FindSlot(widgetWidth, ownHandle, margin, orderIndex: -1, log);
    }

    /// <summary>
    /// Finds an available slot with order-awareness.
    /// When orderIndex >= 0, positions widgets left-to-right by order (index 0 = rightmost).
    /// </summary>
    public TaskbarSlot FindSlot(int widgetWidth, IntPtr ownHandle, int margin, int orderIndex, Action<string>? log = null)
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

        var injectedWindows = GetInjectedWindows()
            .Where(w => w.Handle != ownHandle)
            .OrderByDescending(w => w.RightEdge)
            .ToList();

        int slotX = rightBoundary - widgetWidth;
        int slotRight = rightBoundary;

        if (orderIndex >= 0 && injectedWindows.Count > 0)
        {
            // Order-aware: position by index from right edge.
            // Each widget at a lower index is further right.
            // Skip past 'orderIndex' worth of existing widgets from the right.
            int offset = 0;
            var sorted = injectedWindows.OrderByDescending(w => w.RightEdge).ToList();
            int widgetsToSkip = orderIndex;
            for (int i = 0; i < sorted.Count && i < widgetsToSkip; i++)
            {
                offset += sorted[i].Bounds.Width + margin;
            }
            slotRight = rightBoundary - offset;
            slotX = slotRight - widgetWidth;
        }

        // Collision detection with remaining windows
        foreach (var widget in injectedWindows)
        {
            bool overlaps = !(slotRight <= widget.LeftEdge || slotX >= widget.RightEdge);
            if (overlaps)
            {
                slotRight = widget.LeftEdge - margin;
                slotX = slotRight - widgetWidth;
                log?.Invoke($"Collision with {widget.ClassName} at [{widget.LeftEdge}-{widget.RightEdge}], moving to {slotX}");
            }
        }

        var minX = _taskbarRect.Width / 3;
        slotX = Math.Max(slotX, minX);

        var availableWidth = slotRight - slotX;

        log?.Invoke($"Slot found: X={slotX}, Width={widgetWidth}, RightBoundary={rightBoundary}, Widgets={injectedWindows.Count}, Order={orderIndex}");

        return new TaskbarSlot
        {
            X = slotX,
            Y = 0,
            AvailableWidth = availableWidth,
            Height = _taskbarRect.Height,
            IsValid = availableWidth >= widgetWidth
        };
    }

    /// <summary>
    /// Register a custom class name as a system window (won't be treated as injected widget).
    /// </summary>
    public static void RegisterSystemClass(string className)
    {
        SystemWindowClasses.Add(className);
    }

    /// <summary>
    /// Check if a class name is a known system window.
    /// </summary>
    public static bool IsSystemClass(string className)
    {
        if (string.IsNullOrEmpty(className)) return false;
        if (SystemWindowClasses.Contains(className)) return true;
        if (className.StartsWith("Windows.UI.", StringComparison.OrdinalIgnoreCase)) return true;
        if (className.StartsWith("Shell_", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Debug: Log all taskbar children.
    /// </summary>
    public void LogAllChildren(Action<string>? log = null)
    {
        if (log == null) return;

        var children = GetChildWindows();
        log($"Found {children.Count} direct children of taskbar:");

        foreach (var child in children.OrderBy(c => c.LeftEdge))
        {
            var type = child.IsSystemWindow ? "System" : "INJECTED";
            var visibility = child.IsVisible ? "Visible" : "Hidden";
            log($"  [{type}] {child.ClassName}: Left={child.LeftEdge}, Right={child.RightEdge}, {visibility}");
        }
    }
}
