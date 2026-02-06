using System.Runtime.InteropServices;

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
    /// Window title for the host window.
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
    /// Number of injection retry attempts.
    /// </summary>
    public int RetryAttempts { get; init; } = 3;

    /// <summary>
    /// Delay between retry attempts in milliseconds.
    /// </summary>
    public int RetryDelayMs { get; init; } = 500;

    /// <summary>
    /// If true, Initialize() creates the window but does not inject it into the taskbar.
    /// Call Inject() separately after setting up content.
    /// </summary>
    public bool DeferInjection { get; init; } = false;

    /// <summary>
    /// Custom WndProc callback. If null, DefWindowProcW is used.
    /// </summary>
    public WndProcDelegate? WndProc { get; init; }

    /// <summary>
    /// Extended window style flags for the host window.
    /// Defaults to WS_EX_LAYERED for compatibility.
    /// Set to 0 for standard GDI rendering.
    /// </summary>
    public int ExStyle { get; init; } = Native.WS_EX_LAYERED;
}

/// <summary>
/// Delegate for custom window procedures.
/// </summary>
public delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

/// <summary>
/// Result of taskbar injection initialization.
/// </summary>
public sealed class TaskbarInjectionResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public IntPtr WindowHandle { get; init; }
    public IntPtr TaskbarHandle { get; init; }
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
    private readonly Action<string>? _log;

    private IntPtr _hwnd;
    private IntPtr _hwndTaskbar;
    private TaskbarSlotFinder? _slotFinder;
    private int _widgetWidth;
    private int _widgetHeight;
    private double _dpiScale = 1.0;
    private bool _isVisible;
    private bool _disposed;
    private bool _classRegistered;

    // Must keep a reference to prevent GC collection of the delegate
    private WndProcDelegate? _wndProcDelegate;

    /// <summary>
    /// Create a new taskbar injection helper.
    /// </summary>
    public TaskbarInjectionHelper(TaskbarInjectionConfig config, Action<string>? log = null)
    {
        _config = config;
        _log = log;
    }

    public bool IsVisible => _isVisible;
    public IntPtr WindowHandle => _hwnd;
    public IntPtr TaskbarHandle => _hwndTaskbar;
    public double DpiScale => _dpiScale;
    public int Width => _widgetWidth;
    public int Height => _widgetHeight;

    /// <summary>
    /// Initializes the widget window and optionally injects it into the taskbar.
    /// </summary>
    public TaskbarInjectionResult Initialize()
    {
        if (_disposed)
            return new TaskbarInjectionResult { Success = false, Error = "Helper has been disposed" };

        try
        {
            _slotFinder = new TaskbarSlotFinder();
            if (!_slotFinder.IsTaskbarFound)
            {
                Log("Failed to find taskbar");
                return new TaskbarInjectionResult { Success = false, Error = "Taskbar not found" };
            }

            _hwndTaskbar = _slotFinder.TaskbarHandle;
            _dpiScale = _slotFinder.DpiScale;
            _widgetWidth = (int)Math.Ceiling(_dpiScale * _config.WidthDip);
            _widgetHeight = _slotFinder.TaskbarBounds.Height;

            Log($"Taskbar: {_hwndTaskbar:X}, DPI scale: {_dpiScale}, Width: {_widgetWidth}");

            _slotFinder.LogAllChildren(_log);

            _hwnd = CreateHostWindow();
            if (_hwnd == IntPtr.Zero)
            {
                Log("Failed to create host window");
                return new TaskbarInjectionResult { Success = false, Error = "Failed to create host window" };
            }

            // Size the window
            Native.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, _widgetWidth, _widgetHeight,
                Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

            if (!_config.DeferInjection)
            {
                if (!InjectIntoTaskbar())
                {
                    Log("Failed to inject into taskbar");
                    Cleanup();
                    return new TaskbarInjectionResult { Success = false, Error = "Failed to inject into taskbar" };
                }
                UpdatePosition();
            }

            Log(_config.DeferInjection ? "Window created (injection deferred)" : "Injection successful");

            return new TaskbarInjectionResult
            {
                Success = true,
                WindowHandle = _hwnd,
                TaskbarHandle = _hwndTaskbar,
                Width = _widgetWidth,
                Height = _widgetHeight,
                DpiScale = _dpiScale
            };
        }
        catch (Exception ex)
        {
            Log($"Initialization failed: {ex.Message}");
            return new TaskbarInjectionResult { Success = false, Error = ex.Message };
        }
    }

    private IntPtr CreateHostWindow()
    {
        RegisterWindowClass();

        var hwnd = Native.CreateWindowExW(
            _config.ExStyle, _config.ClassName, _config.WindowTitle, Native.WS_POPUP,
            0, 0, 0, 0, _hwndTaskbar, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        Log($"Created host window: {hwnd:X}");
        return hwnd;
    }

    private void RegisterWindowClass()
    {
        if (_classRegistered) return;

        _wndProcDelegate = _config.WndProc ?? DefaultWndProc;

        var wndClass = new Native.WNDCLASS
        {
            lpszClassName = _config.ClassName,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = Native.GetModuleHandleW(null)
        };

        var atom = Native.RegisterClassW(ref wndClass);
        _classRegistered = atom != 0;

        Log($"Registered window class: {_classRegistered}, atom: {atom}");
    }

    private static IntPtr DefaultWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam) =>
        Native.DefWindowProcW(hwnd, msg, wParam, lParam);

    private bool InjectIntoTaskbar()
    {
        Log("Attempting to inject into taskbar");

        for (int attempt = 1; attempt <= _config.RetryAttempts; attempt++)
        {
            Log($"Injection attempt #{attempt}");

            if (Native.SetParent(_hwnd, _hwndTaskbar) != IntPtr.Zero)
            {
                Log("Injected successfully");
                return true;
            }

            if (attempt < _config.RetryAttempts)
                Thread.Sleep(_config.RetryDelayMs);
        }

        return false;
    }

    /// <summary>
    /// Injects the window into the taskbar. Call this after setting up content
    /// when using DeferInjection = true.
    /// </summary>
    public bool Inject()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return false;

        if (!InjectIntoTaskbar())
        {
            Log("Deferred injection failed");
            return false;
        }

        UpdatePosition();
        Log("Deferred injection successful");
        return true;
    }

    /// <summary>
    /// Recalculates and updates the widget position based on current taskbar state.
    /// </summary>
    public void UpdatePosition()
    {
        if (_hwnd == IntPtr.Zero || _hwndTaskbar == IntPtr.Zero) return;

        _slotFinder = new TaskbarSlotFinder();
        var slot = _slotFinder.FindSlot(_widgetWidth, _hwnd, _config.Margin, _log);

        if (!slot.IsValid)
        {
            Log("No valid slot found, using fallback position");
            Native.GetWindowRect(_hwndTaskbar, out var taskbarRect);
            Native.SetWindowPos(_hwnd, IntPtr.Zero,
                taskbarRect.Width - _widgetWidth - 100, 0, _widgetWidth, taskbarRect.Height,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            return;
        }

        Native.SetWindowPos(_hwnd, IntPtr.Zero,
            slot.X, slot.Y, _widgetWidth, slot.Height,
            Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        Log($"Positioned at ({slot.X}, {slot.Y}), size ({_widgetWidth}x{slot.Height})");
    }

    /// <summary>
    /// Shows the widget.
    /// </summary>
    public void Show()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        Native.ShowWindow(_hwnd, Native.SW_SHOW);
        _isVisible = true;
        Log("Window shown");
    }

    /// <summary>
    /// Hides the widget.
    /// </summary>
    public void Hide()
    {
        if (_hwnd == IntPtr.Zero) return;
        Native.ShowWindow(_hwnd, Native.SW_HIDE);
        _isVisible = false;
        Log("Window hidden");
    }

    /// <summary>
    /// Re-inject the window after explorer restart.
    /// </summary>
    public bool Reinject()
    {
        if (_disposed) return false;

        Log("Re-injecting after explorer restart");

        _slotFinder = new TaskbarSlotFinder();
        if (!_slotFinder.IsTaskbarFound)
        {
            Log("Taskbar not found during reinject");
            return false;
        }

        _hwndTaskbar = _slotFinder.TaskbarHandle;

        if (_hwnd != IntPtr.Zero)
        {
            if (Native.SetParent(_hwnd, _hwndTaskbar) != IntPtr.Zero)
            {
                UpdatePosition();
                Log("Re-injection successful");
                return true;
            }
        }

        Log("Re-injection failed");
        return false;
    }

    /// <summary>
    /// Resize the widget width.
    /// </summary>
    public void Resize(int widthDip)
    {
        _widgetWidth = (int)Math.Ceiling(_dpiScale * widthDip);

        if (_hwnd != IntPtr.Zero && _slotFinder != null)
        {
            _widgetHeight = _slotFinder.TaskbarBounds.Height;
            Native.SetWindowPos(_hwnd, IntPtr.Zero,
                0, 0, _widgetWidth, _widgetHeight,
                Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            UpdatePosition();
        }
    }

    private void Cleanup()
    {
        if (_hwnd != IntPtr.Zero)
        {
            try
            {
                Native.SetParent(_hwnd, IntPtr.Zero);
                Native.DestroyWindow(_hwnd);
            }
            catch { }
            _hwnd = IntPtr.Zero;
        }
    }

    private void Log(string message) => _log?.Invoke(message);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isVisible = false;
        Cleanup();
        Log("Disposed");
    }
}
