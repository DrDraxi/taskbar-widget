using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace TaskbarWidget;

/// <summary>
/// Configuration for taskbar widget injection.
/// </summary>
public sealed class TaskbarInjectionConfig
{
    /// <summary>
    /// Window class name for the widget. Must be unique per widget type.
    /// </summary>
    public required string ClassName { get; init; }

    /// <summary>
    /// Window title for the widget window.
    /// </summary>
    public string WindowTitle { get; init; } = "TaskbarWidget";

    /// <summary>
    /// Width of the widget in device-independent pixels.
    /// </summary>
    public int WidthDip { get; init; } = 100;

    /// <summary>
    /// Margin between widgets in pixels.
    /// </summary>
    public int Margin { get; init; } = 4;

    /// <summary>
    /// If true, Initialize() creates the window but does not inject it into the taskbar.
    /// Call Inject() separately after setting up XAML content.
    /// This is required for WinUI apps using DesktopWindowXamlSource.
    /// </summary>
    public bool DeferInjection { get; init; } = false;
}

/// <summary>
/// Result of taskbar injection initialization.
/// </summary>
public sealed class TaskbarInjectionResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public IntPtr WindowHandle { get; init; }
    public AppWindow? AppWindow { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double DpiScale { get; init; }
}

/// <summary>
/// Helper for injecting custom windows into the Windows taskbar.
/// Handles window creation, positioning, and collision detection with other widgets.
/// </summary>
public sealed class TaskbarInjectionHelper : IDisposable
{
    private readonly TaskbarInjectionConfig _config;
    private IntPtr _hwnd;
    private IntPtr _hwndTaskbar;
    private AppWindow? _appWindow;
    private TaskbarSlotFinder? _slotFinder;
    private int _widgetWidth;
    private double _dpiScale = 1.0;
    private bool _isVisible;
    private bool _disposed;
    private bool _classRegistered;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static readonly Dictionary<string, WndProcDelegate> _wndProcDelegates = new();

    public TaskbarInjectionHelper(TaskbarInjectionConfig config)
    {
        _config = config;
    }

    public bool IsVisible => _isVisible;
    public IntPtr WindowHandle => _hwnd;
    public AppWindow? AppWindow => _appWindow;

    /// <summary>
    /// Initializes the widget window and optionally injects it into the taskbar.
    /// </summary>
    public TaskbarInjectionResult Initialize()
    {
        if (_disposed)
            return new TaskbarInjectionResult { Success = false, Error = "Disposed" };

        try
        {
            _slotFinder = new TaskbarSlotFinder();
            if (!_slotFinder.IsTaskbarFound)
                return new TaskbarInjectionResult { Success = false, Error = "Taskbar not found" };

            _hwndTaskbar = _slotFinder.TaskbarHandle;
            _dpiScale = _slotFinder.DpiScale;
            _widgetWidth = (int)Math.Ceiling(_dpiScale * _config.WidthDip);

            _hwnd = CreateHostWindow();
            if (_hwnd == IntPtr.Zero)
                return new TaskbarInjectionResult { Success = false, Error = "Failed to create window" };

            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(_hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _appWindow.IsShownInSwitchers = false;

            var height = _slotFinder.TaskbarBounds.Height;
            _appWindow.ResizeClient(new SizeInt32(_widgetWidth, height));

            if (!_config.DeferInjection)
            {
                if (!InjectIntoTaskbar())
                    return new TaskbarInjectionResult { Success = false, Error = "Injection failed" };
                UpdatePosition();
            }

            return new TaskbarInjectionResult
            {
                Success = true,
                WindowHandle = _hwnd,
                AppWindow = _appWindow,
                Width = _widgetWidth,
                Height = height,
                DpiScale = _dpiScale
            };
        }
        catch (Exception ex)
        {
            return new TaskbarInjectionResult { Success = false, Error = ex.Message };
        }
    }

    private IntPtr CreateHostWindow()
    {
        RegisterWindowClass();
        return Native.CreateWindowExW(
            Native.WS_EX_LAYERED, _config.ClassName, _config.WindowTitle, Native.WS_POPUP,
            0, 0, 0, 0, _hwndTaskbar, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    private void RegisterWindowClass()
    {
        if (_classRegistered) return;

        if (!_wndProcDelegates.ContainsKey(_config.ClassName))
            _wndProcDelegates[_config.ClassName] = WndProc;

        var wndClass = new Native.WNDCLASS
        {
            lpszClassName = _config.ClassName,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegates[_config.ClassName]),
            hInstance = Native.GetModuleHandleW(null)
        };

        _classRegistered = Native.RegisterClassW(ref wndClass) != 0;
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        Native.DefWindowProcW(hwnd, msg, wParam, lParam);

    private bool InjectIntoTaskbar()
    {
        for (int i = 0; i < 3; i++)
        {
            if (Native.SetParent(_hwnd, _hwndTaskbar) != IntPtr.Zero)
                return true;
            Thread.Sleep(500);
        }
        return false;
    }

    /// <summary>
    /// Injects the window into the taskbar. Call this after setting up XAML content
    /// when using DeferInjection = true.
    /// </summary>
    public bool Inject()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return false;
        if (!InjectIntoTaskbar()) return false;
        UpdatePosition();
        return true;
    }

    /// <summary>
    /// Recalculates and updates the widget position based on current taskbar state.
    /// </summary>
    public void UpdatePosition()
    {
        if (_appWindow == null || _hwndTaskbar == IntPtr.Zero) return;

        _slotFinder = new TaskbarSlotFinder();
        var slot = _slotFinder.FindSlot(_widgetWidth, _hwnd, _config.Margin);

        if (!slot.IsValid)
        {
            Native.GetWindowRect(_hwndTaskbar, out var taskbarRect);
            _appWindow.MoveAndResize(new RectInt32(
                taskbarRect.Width - _widgetWidth - 100, 0, _widgetWidth, taskbarRect.Height));
            return;
        }

        _appWindow.MoveAndResize(new RectInt32(slot.X, slot.Y, _widgetWidth, slot.Height));
    }

    /// <summary>
    /// Shows the widget.
    /// </summary>
    public void Show()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        Native.ShowWindow(_hwnd, 5);
        _isVisible = true;
    }

    /// <summary>
    /// Hides the widget.
    /// </summary>
    public void Hide()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.ShowWindow(_hwnd, 0);
        _isVisible = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isVisible = false;

        if (_hwnd != IntPtr.Zero)
        {
            Native.SetParent(_hwnd, IntPtr.Zero);
            Native.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        _appWindow = null;
    }
}
