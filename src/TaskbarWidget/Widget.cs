using System.Runtime.InteropServices;
using TaskbarWidget.Interaction;
using TaskbarWidget.Ordering;
using TaskbarWidget.Rendering;
using TaskbarWidget.Theming;
using TaskbarWidget.Timing;

namespace TaskbarWidget;

/// <summary>
/// Main entry point for creating a taskbar widget.
/// Orchestrates rendering, interaction, tooltips, timers, and positioning.
/// </summary>
public sealed class Widget : IDisposable
{
    private const uint WM_INVALIDATE = Native.WM_USER + 1;

    // Hover styling defaults (match native taskbar icon hover)
    private const int HoverMarginTop = 4;
    private const int HoverMarginBottom = 4;
    private const int HoverMarginLeft = 4;
    private const int HoverMarginRight = 4;
    private const int HoverCornerRadius = 4;
    private const int ContentPaddingLeft = 6;
    private const int ContentPaddingRight = 6;

    // Resize animation constants
    private const int ResizeAnimTimerId = 9998;
    private const int ResizeAnimIntervalMs = 16; // ~60fps
    private const double ResizeAnimLerp = 0.25;
    private const double ResizeAnimSnap = 0.5;

    // Fullscreen detection constants
    private const int FullscreenCheckTimerId = 9997;
    private const int FullscreenCheckIntervalMs = 500;

    private readonly string _name;
    private readonly Action<RenderContext> _render;
    private readonly WidgetOptions _options;
    private readonly WndProcDelegate _wndProc;

    private TaskbarInjectionHelper? _helper;
    private readonly MouseTracker _mouseTracker = new();
    private readonly TooltipManager _tooltipManager = new();
    private readonly TimerManager _timerManager = new();
    private readonly DropTarget _dropTarget = new();
    private readonly DragManager _dragManager = new();

    private int _pendingClickX;
    private int _pendingClickY;

    private IntPtr _hwnd;
    private int _width;
    private int _height;
    private double _dpiScale = 1.0;
    private bool _isHovering;
    private bool _hiddenForFullscreen;
    private bool _disposed;
    private uint _repositionMsg;

    // Current render state
    private LayoutNode? _rootNode;
    private string? _widgetTooltipTitle;
    private string? _widgetTooltipBody;

    // Resize animation state: this widget drives smooth repositioning
    // of ALL widgets when its own size changes.
    private Dictionary<IntPtr, double>? _resizeAnimX;
    private Dictionary<IntPtr, int>? _resizeTargetX;
    private int _resizeAnimY;

    /// <summary>
    /// Create a new widget.
    /// </summary>
    /// <param name="name">Unique name for this widget (used for ordering).</param>
    /// <param name="render">Render callback that builds the widget's visual tree.</param>
    /// <param name="options">Optional configuration.</param>
    public Widget(string name, Action<RenderContext> render, WidgetOptions? options = null)
    {
        _name = name;
        _render = render;
        _options = options ?? new WidgetOptions();
        _wndProc = WndProc;
    }

    public IntPtr WindowHandle => _hwnd;
    public double DpiScale => _dpiScale;

    /// <summary>
    /// Initialize and show the widget in the taskbar.
    /// </summary>
    public void Show()
    {
        if (_disposed) return;

        // Register with ordering system
        WidgetOrderManager.Register(_name);
        _repositionMsg = WidgetOrderManager.RepositionMessage;

        var config = new TaskbarInjectionConfig
        {
            ClassName = $"TaskbarWidget_{_name}",
            WindowTitle = _name,
            WidthDip = 40, // initial, will be resized
            Margin = _options.Margin,
            DeferInjection = true,
            WndProc = _wndProc,
            ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST
        };

        _helper = new TaskbarInjectionHelper(config, _options.Log);
        var result = _helper.Initialize();
        if (!result.Success || result.WindowHandle == IntPtr.Zero)
        {
            _options.Log?.Invoke($"Widget '{_name}' initialization failed: {result.Error}");
            return;
        }

        _hwnd = result.WindowHandle;
        _height = result.Height;
        _dpiScale = result.DpiScale;

        _timerManager.SetHwnd(_hwnd);
        _tooltipManager.EnsureWindow();

        // Enable file drop
        _dropTarget.EnableFileDrop(_hwnd);

        // Build initial tree and render
        RebuildAndRender();

        // Position and show
        PositionOverTaskbar();
        _helper.Show();

        // Start fullscreen detection
        Native.SetTimer(_hwnd, (IntPtr)FullscreenCheckTimerId, FullscreenCheckIntervalMs, IntPtr.Zero);

        // Final render at correct screen position
        RenderToScreen();
    }

    /// <summary>
    /// Request a re-render. Thread-safe - posts a message if called from another thread.
    /// </summary>
    public void Invalidate()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.PostMessageW(_hwnd, WM_INVALIDATE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Set a repeating interval timer.
    /// </summary>
    public int SetInterval(TimeSpan interval, Action callback)
    {
        return _timerManager.SetInterval(interval, callback);
    }

    /// <summary>
    /// Set a one-shot timeout timer.
    /// </summary>
    public int SetTimeout(TimeSpan delay, Action callback)
    {
        return _timerManager.SetTimeout(delay, callback);
    }

    /// <summary>
    /// Cancel a timer.
    /// </summary>
    public void ClearTimer(int handle)
    {
        _timerManager.ClearTimer(handle);
    }

    /// <summary>
    /// Set widget-level file drop handler.
    /// </summary>
    public void OnFileDrop(Action<string[]> handler)
    {
        _dropTarget.SetFileDropHandler(handler);
    }

    /// <summary>
    /// Set widget-level text drop handler.
    /// </summary>
    public void OnTextDrop(Action<string> handler)
    {
        _dropTarget.SetTextDropHandler(handler);
    }

    /// <summary>
    /// Run the Win32 message loop. Call after Show().
    /// </summary>
    public static void RunMessageLoop()
    {
        // Initialize common controls
        var icc = new Native.INITCOMMONCONTROLSEX
        {
            dwSize = Marshal.SizeOf<Native.INITCOMMONCONTROLSEX>(),
            dwICC = Native.ICC_WIN95_CLASSES
        };
        Native.InitCommonControlsEx(ref icc);

        while (Native.GetMessageW(out var msg, IntPtr.Zero, 0, 0))
        {
            Native.TranslateMessage(ref msg);
            Native.DispatchMessageW(ref msg);
        }
    }

    private void RebuildAndRender()
    {
        var root = new LayoutNode { Type = LayoutNodeType.Root };
        var ctx = new RenderContext(root, _dpiScale);
        _render(ctx);

        _widgetTooltipTitle = ctx.TooltipTitle;
        _widgetTooltipBody = ctx.TooltipBody;

        // Measure
        var screenDc = Native.GetDC(IntPtr.Zero);
        LayoutEngine.Measure(root, _dpiScale, screenDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);

        // Force root height to taskbar height
        root.Height = _height;

        // Add padding so content fits inside the hover overlay with breathing room
        root.Width += HoverMarginLeft + HoverMarginRight + ContentPaddingLeft + ContentPaddingRight;

        // Arrange
        LayoutEngine.Arrange(root);

        _rootNode = root;
        _mouseTracker.SetRoot(root, _dpiScale);

        // Track new width — actual resize deferred to PositionOverTaskbar()
        // so resize + reposition happen atomically in one SetWindowPos call.
        int newWidth = root.Width;
        if (newWidth > 0)
            _width = newWidth;
    }

    private void RenderToScreen()
    {
        if (_rootNode == null || _hwnd == IntPtr.Zero) return;

        GdiRenderer.HoverOverlay? hover = null;
        if (_isHovering)
        {
            var theme = ThemeDetector.CurrentTheme;
            hover = new GdiRenderer.HoverOverlay
            {
                MarginLeft = HoverMarginLeft,
                MarginTop = HoverMarginTop,
                MarginRight = HoverMarginRight,
                MarginBottom = HoverMarginBottom,
                CornerRadius = HoverCornerRadius,
                Color = theme.HoverBackground
            };
        }

        GdiRenderer.Render(_hwnd, _rootNode, _dpiScale, hover);
    }

    private void PositionOverTaskbar()
    {
        if (_helper == null) return;

        var taskbarHandle = _helper.TaskbarHandle;
        Native.GetWindowRect(taskbarHandle, out var taskbarRect);

        int orderIndex = WidgetOrderManager.GetOrderIndex(_name);
        var slotFinder = new TaskbarSlotFinder();
        var slot = slotFinder.FindSlot(_width, _hwnd, _options.Margin, orderIndex, _options.Log);

        int screenX = taskbarRect.Left + slot.X;
        int screenY = taskbarRect.Top + slot.Y;

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, screenX, screenY, _width, _height,
            Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Start a smooth resize animation that lerps all widgets to their
    /// new positions over several frames, instead of snapping instantly.
    /// </summary>
    private void StartResizeAnimation()
    {
        var taskbar = Native.FindTaskbar();
        if (taskbar == IntPtr.Zero) { PositionOverTaskbar(); return; }

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

        // Get order and enumerate all widget windows
        var order = WidgetOrderManager.GetOrder();
        var windows = new Dictionary<string, (IntPtr Hwnd, int Width, int CurrentX)>();

        Native.EnumWindows((hwnd, _) =>
        {
            if (!Native.IsWindowVisible(hwnd)) return true;
            var className = Native.GetClassName(hwnd);
            if (!className.StartsWith("TaskbarWidget_", StringComparison.Ordinal)) return true;

            string name = className.Substring("TaskbarWidget_".Length);
            Native.GetWindowRect(hwnd, out var rect);
            // Use our new width for self (on-screen rect still has old size)
            int width = (hwnd == _hwnd) ? _width : rect.Width;
            windows[name] = (hwnd, width, rect.Left);
            return true;
        }, IntPtr.Zero);

        if (windows.Count < 2)
        {
            // Only one widget — just position directly, no animation needed
            PositionOverTaskbar();
            return;
        }

        // Calculate target positions right-to-left following saved order
        _resizeAnimX ??= new Dictionary<IntPtr, double>();
        _resizeTargetX = new Dictionary<IntPtr, int>();
        _resizeAnimY = taskbarRect.Top;

        int currentRight = rightBoundary;
        foreach (var name in order)
        {
            if (!windows.TryGetValue(name, out var w)) continue;
            int targetX = currentRight - w.Width;

            // Initialize animated position from current screen position if new
            if (!_resizeAnimX.ContainsKey(w.Hwnd))
                _resizeAnimX[w.Hwnd] = w.CurrentX;

            _resizeTargetX[w.Hwnd] = targetX;
            currentRight = targetX - margin;
        }

        // Start the animation timer
        Native.SetTimer(_hwnd, (IntPtr)ResizeAnimTimerId, ResizeAnimIntervalMs, IntPtr.Zero);
    }

    /// <summary>
    /// Called on each animation timer tick. Lerps all widgets toward their
    /// target positions and stops when everyone has arrived.
    /// </summary>
    private void OnResizeAnimTick()
    {
        if (_resizeAnimX == null || _resizeTargetX == null)
        {
            Native.KillTimer(_hwnd, (IntPtr)ResizeAnimTimerId);
            return;
        }

        bool allDone = true;

        foreach (var hwnd in _resizeTargetX.Keys)
        {
            int target = _resizeTargetX[hwnd];

            if (!_resizeAnimX.TryGetValue(hwnd, out double current))
                current = target;

            double newX = current + (target - current) * ResizeAnimLerp;

            if (Math.Abs(newX - target) < ResizeAnimSnap)
                newX = target;
            else
                allDone = false;

            _resizeAnimX[hwnd] = newX;

            if (hwnd == _hwnd)
            {
                // Self: set both position and size
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST,
                    (int)Math.Round(newX), _resizeAnimY, _width, _height,
                    Native.SWP_NOACTIVATE);
            }
            else
            {
                // Other widgets: position only
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST,
                    (int)Math.Round(newX), _resizeAnimY, 0, 0,
                    Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }

        if (allDone)
        {
            Native.KillTimer(_hwnd, (IntPtr)ResizeAnimTimerId);
            _resizeAnimX = null;
            _resizeTargetX = null;

            // Broadcast so all widgets re-render at their final positions
            WidgetOrderManager.BroadcastReposition();
        }
    }

    private void CheckFullscreen()
    {
        bool fullscreen = IsFullscreenAppOnWidgetMonitor();

        if (fullscreen && !_hiddenForFullscreen)
        {
            _hiddenForFullscreen = true;
            _tooltipManager.Hide(_hwnd);
            Native.ShowWindow(_hwnd, Native.SW_HIDE);
        }
        else if (!fullscreen && _hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            Native.ShowWindow(_hwnd, Native.SW_SHOW);
            PositionOverTaskbar();
            RenderToScreen();
        }
    }

    private bool IsFullscreenAppOnWidgetMonitor()
    {
        var fgWnd = Native.GetForegroundWindow();
        if (fgWnd == IntPtr.Zero) return false;

        // Ignore desktop and shell windows
        if (fgWnd == Native.GetDesktopWindow()) return false;
        if (fgWnd == Native.GetShellWindow()) return false;

        // Only care about fullscreen on the same monitor as the widget
        var widgetMonitor = Native.MonitorFromWindow(_hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var fgMonitor = Native.MonitorFromWindow(fgWnd, Native.MONITOR_DEFAULTTONEAREST);
        if (widgetMonitor != fgMonitor) return false;

        // Check if the foreground window covers the entire monitor
        Native.GetWindowRect(fgWnd, out var fgRect);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfoW(widgetMonitor, ref mi);

        return fgRect.Left <= mi.rcMonitor.Left && fgRect.Top <= mi.rcMonitor.Top &&
               fgRect.Right >= mi.rcMonitor.Right && fgRect.Bottom >= mi.rcMonitor.Bottom;
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Check for reposition broadcast
        if (msg == _repositionMsg && _repositionMsg != 0)
        {
            // Skip if we're driving a resize animation (we handle positioning)
            if (_resizeAnimX == null)
            {
                PositionOverTaskbar();
                RenderToScreen();
            }
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case Native.WM_MOUSEMOVE:
            {
                // During drag, move widget and skip hover/tooltip
                if (_dragManager.IsMouseDown)
                {
                    Native.GetCursorPos(out var pt);
                    if (_dragManager.OnMouseMove(hwnd, pt.X, pt.Y))
                    {
                        _tooltipManager.Hide(_hwnd);
                        return IntPtr.Zero;
                    }
                }

                int x = (short)(lParam.ToInt64() & 0xFFFF);
                int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

                bool wasHovering = _isHovering;
                _isHovering = true;
                bool panelChanged = _mouseTracker.OnMouseMove(hwnd, x, y);

                if (!wasHovering || panelChanged)
                {
                    RenderToScreen();

                    // Tooltip handling
                    var (panelTitle, panelBody) = _mouseTracker.GetHoveredTooltip();
                    string? title = panelTitle ?? _widgetTooltipTitle;
                    string? body = panelBody ?? _widgetTooltipBody;

                    if (title != null || body != null)
                        _tooltipManager.RequestShow(_hwnd, title, body);
                }
                return IntPtr.Zero;
            }

            case Native.WM_MOUSELEAVE:
            {
                _isHovering = false;
                _mouseTracker.OnMouseLeave();
                RenderToScreen();
                _tooltipManager.Hide(_hwnd);
                return IntPtr.Zero;
            }

            case Native.WM_LBUTTONDOWN:
            {
                int x = (short)(lParam.ToInt64() & 0xFFFF);
                int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

                // Save local coords for deferred click dispatch
                _pendingClickX = x;
                _pendingClickY = y;

                // Start drag tracking (captures mouse)
                Native.GetCursorPos(out var pt);
                _dragManager.OnLeftButtonDown(hwnd, pt.X, pt.Y);

                return IntPtr.Zero;
            }

            case Native.WM_RBUTTONDOWN:
            case Native.WM_RBUTTONDBLCLK:
            {
                int x = (short)(lParam.ToInt64() & 0xFFFF);
                int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                _mouseTracker.OnRightButtonDown(x, y);
                return IntPtr.Zero;
            }

            case Native.WM_LBUTTONUP:
            {
                bool wasDragging = _dragManager.OnLeftButtonUp(hwnd, _name);
                if (!wasDragging)
                {
                    // Was a click, not a drag — dispatch the deferred click
                    _mouseTracker.OnLeftButtonDown(_pendingClickX, _pendingClickY);
                }
                return IntPtr.Zero;
            }

            case Native.WM_LBUTTONDBLCLK:
            {
                int x = (short)(lParam.ToInt64() & 0xFFFF);
                int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                _mouseTracker.OnDoubleClick(x, y);
                return IntPtr.Zero;
            }

            case Native.WM_SETCURSOR:
            {
                if (_dragManager.IsDragging)
                {
                    Native.SetCursor(Native.LoadCursorW(IntPtr.Zero, Native.IDC_SIZEALL));
                    return (IntPtr)1;
                }
                break;
            }

            case Native.WM_CAPTURECHANGED:
            {
                if (_dragManager.WasCommitted)
                {
                    // Drag completed — widget already at target slot, just re-render
                    RenderToScreen();
                }
                else
                {
                    // Drag cancelled — snap everything back
                    _dragManager.CancelDrag(hwnd);
                    WidgetOrderManager.BroadcastReposition();
                }
                return IntPtr.Zero;
            }

            case Native.WM_TIMER:
            {
                int timerId = (int)wParam;
                if (timerId == FullscreenCheckTimerId)
                {
                    CheckFullscreen();
                    return IntPtr.Zero;
                }
                if (timerId == ResizeAnimTimerId)
                {
                    OnResizeAnimTick();
                    return IntPtr.Zero;
                }
                if (timerId == TooltipManager.ShowTimerId)
                {
                    _tooltipManager.OnShowTimer(_hwnd, _dpiScale);
                    return IntPtr.Zero;
                }
                if (timerId == TooltipManager.FadeTimerId)
                {
                    _tooltipManager.OnFadeTimer(_hwnd);
                    return IntPtr.Zero;
                }
                if (_timerManager.OnTimer(wParam))
                    return IntPtr.Zero;
                break;
            }

            case Native.WM_SETTINGCHANGE:
            {
                ThemeDetector.OnSettingChange();
                RebuildAndRender();
                RenderToScreen();
                return IntPtr.Zero;
            }

            case Native.WM_DROPFILES:
            {
                _dropTarget.OnDropFiles(wParam);
                return IntPtr.Zero;
            }

            case WM_INVALIDATE:
            {
                int oldWidth = _width;
                RebuildAndRender();

                if (_width != oldWidth)
                {
                    // Width changed — smoothly animate all widgets to new positions
                    StartResizeAnimation();
                }
                else
                {
                    // No size change — just reposition normally
                    PositionOverTaskbar();
                }

                RenderToScreen();
                return IntPtr.Zero;
            }
        }

        return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hwnd != IntPtr.Zero)
            Native.KillTimer(_hwnd, (IntPtr)FullscreenCheckTimerId);

        _timerManager.Dispose();
        _tooltipManager.Dispose();
        _helper?.Dispose();
    }
}
