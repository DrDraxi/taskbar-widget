using TaskbarWidget.Ordering;

namespace TaskbarWidget.Interaction;

/// <summary>
/// Handles drag-to-reorder of taskbar widgets.
/// 5px horizontal dead zone discriminates clicks from drags.
/// During drag, other widgets slide smoothly to show the new order.
/// </summary>
internal sealed class DragManager
{
    private const int DragThreshold = 5;
    private const int Margin = 4;
    private const double LerpFactor = 0.25;
    private const double SnapThreshold = 0.5;

    private bool _isDragging;
    private bool _mouseDown;
    private bool _committed;
    private int _startScreenX;
    private int _startScreenY;
    private int _offsetX; // cursor offset from window left edge
    private string[]? _lastPreviewOrder;

    // Target slot for dragged widget, calculated during LiveReorder
    private int _targetScreenX;
    private int _targetScreenY;

    // Smooth animation: tracks current animated X position per widget handle
    private readonly Dictionary<IntPtr, double> _animatedX = new();

    public bool IsDragging => _isDragging;
    public bool IsMouseDown => _mouseDown;

    /// <summary>
    /// True if the drag completed with a commit (not cancelled).
    /// Checked by WM_CAPTURECHANGED to avoid snapping back.
    /// Resets on read.
    /// </summary>
    public bool WasCommitted
    {
        get
        {
            bool v = _committed;
            _committed = false;
            return v;
        }
    }

    /// <summary>
    /// Call on WM_LBUTTONDOWN. Captures mouse and records start position.
    /// </summary>
    public void OnLeftButtonDown(IntPtr hwnd, int screenX, int screenY)
    {
        _mouseDown = true;
        _isDragging = false;
        _committed = false;
        _startScreenX = screenX;
        _startScreenY = screenY;
        _lastPreviewOrder = null;
        _animatedX.Clear();

        Native.GetWindowRect(hwnd, out var rect);
        _offsetX = screenX - rect.Left;

        Native.SetCapture(hwnd);
    }

    /// <summary>
    /// Call on WM_MOUSEMOVE. Returns true if dragging (caller should skip hover/tooltip).
    /// Moves the widget horizontally and live-reorders other widgets.
    /// </summary>
    public bool OnMouseMove(IntPtr hwnd, int screenX, int screenY)
    {
        if (!_mouseDown) return false;

        if (!_isDragging)
        {
            int dx = Math.Abs(screenX - _startScreenX);
            if (dx < DragThreshold) return false;
            _isDragging = true;
        }

        // Move dragged widget horizontally, keep Y fixed
        Native.GetWindowRect(hwnd, out var rect);
        int newX = screenX - _offsetX;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, newX, rect.Top, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);

        // Live-reorder: position other widgets based on current drag position
        LiveReorder(hwnd);

        return true;
    }

    /// <summary>
    /// Call on WM_LBUTTONUP. Commits the order and releases capture.
    /// Returns true if this was a drag (caller should NOT dispatch click).
    /// </summary>
    public bool OnLeftButtonUp(IntPtr hwnd, string widgetName)
    {
        if (!_mouseDown) return false;

        bool wasDragging = _isDragging;
        _mouseDown = false;
        _isDragging = false;

        if (wasDragging && _lastPreviewOrder != null)
        {
            // Save the final order
            WidgetOrderManager.SaveOrder(_lastPreviewOrder);
            _committed = true;

            // Snap dragged widget to its target slot before releasing capture
            Native.GetWindowRect(hwnd, out var rect);
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST,
                _targetScreenX, _targetScreenY, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        _lastPreviewOrder = null;
        _animatedX.Clear();
        Native.ReleaseCapture();

        if (wasDragging)
        {
            // Broadcast so all widgets re-render at their final positions
            WidgetOrderManager.BroadcastReposition();
        }

        return wasDragging;
    }

    /// <summary>
    /// Call on WM_CAPTURECHANGED. Resets drag state.
    /// </summary>
    public void CancelDrag(IntPtr hwnd)
    {
        if (!_mouseDown) return;

        _mouseDown = false;
        _isDragging = false;
        _lastPreviewOrder = null;
        _animatedX.Clear();
    }

    /// <summary>
    /// During drag, enumerate all widgets, determine order from positions,
    /// and smoothly slide non-dragged widgets toward their correct slots.
    /// Also calculates the target slot for the dragged widget.
    /// </summary>
    private void LiveReorder(IntPtr draggedHwnd)
    {
        var widgets = new List<(string Name, IntPtr Hwnd, int Width, int ScreenX)>();

        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;

            var className = Native.GetClassName(hwnd);
            if (!className.StartsWith("TaskbarWidget_", StringComparison.Ordinal)) return true;

            string name = className.Substring("TaskbarWidget_".Length);
            Native.GetWindowRect(hwnd, out var rect);
            widgets.Add((name, hwnd, rect.Width, rect.Left));

            return true;
        }, IntPtr.Zero);

        if (widgets.Count < 2) return;

        // Determine new order: sort by center X descending (rightmost = index 0)
        widgets.Sort((a, b) =>
        {
            int centerA = a.ScreenX + a.Width / 2;
            int centerB = b.ScreenX + b.Width / 2;
            return centerB.CompareTo(centerA);
        });

        _lastPreviewOrder = widgets.Select(w => w.Name).ToArray();

        // Calculate target positions
        var taskbar = Native.FindTaskbar();
        if (taskbar == IntPtr.Zero) return;

        Native.GetWindowRect(taskbar, out var taskbarRect);
        var trayNotify = Native.FindTrayNotifyWnd(taskbar);

        int rightBoundary;
        if (trayNotify != IntPtr.Zero)
        {
            Native.GetWindowRect(trayNotify, out var trayRect);
            rightBoundary = trayRect.Left - Margin;
        }
        else
        {
            rightBoundary = taskbarRect.Right - 100;
        }

        // Position right-to-left, lerping non-dragged widgets toward targets
        int currentRight = rightBoundary;
        foreach (var w in widgets)
        {
            int targetX = currentRight - w.Width;

            if (w.Hwnd == draggedHwnd)
            {
                // Store where the dragged widget WOULD go (its target slot)
                _targetScreenX = targetX;
                _targetScreenY = taskbarRect.Top;
                currentRight -= w.Width + Margin;
                continue;
            }

            // Initialize animated position from current screen position if new
            if (!_animatedX.TryGetValue(w.Hwnd, out double currentX))
            {
                currentX = w.ScreenX;
                _animatedX[w.Hwnd] = currentX;
            }

            // Lerp toward target
            double newX = currentX + (targetX - currentX) * LerpFactor;

            // Snap when close enough to avoid sub-pixel jitter
            if (Math.Abs(newX - targetX) < SnapThreshold)
                newX = targetX;

            _animatedX[w.Hwnd] = newX;

            Native.SetWindowPos(w.Hwnd, Native.HWND_TOPMOST, (int)Math.Round(newX), taskbarRect.Top, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            currentRight -= w.Width + Margin;
        }
    }
}
