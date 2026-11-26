using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace TaskbarWidget;

/// <summary>Options for a <see cref="Widget"/>.</summary>
public sealed class WidgetOptions
{
    /// <summary>Allow dragging the widget to reorder it among other taskbar widgets. Default true.</summary>
    public bool AllowReorder { get; init; } = true;

    /// <summary>Exit the process if another instance of this widget is already running. Default true.</summary>
    public bool SingleInstance { get; init; } = true;

    /// <summary>
    /// Extra drawing room above the taskbar, in DIPs (default 0). Elements may draw into it (negative Y);
    /// it stays click-through except where something is drawn. Useful for pop-out hover effects.
    /// </summary>
    public float OverflowTop { get; init; }

    /// <summary>Modifier that makes right-click open the menu even when a right-click handler exists. Default Shift.</summary>
    public MenuModifier MenuModifier { get; init; } = MenuModifier.Shift;

    /// <summary>Items for the widget's context menu. "Exit" is always appended.</summary>
    public Action<MenuBuilder>? Menu { get; init; }
}

/// <summary>Which modifier + right-click always opens the widget menu.</summary>
public enum MenuModifier { Shift, Ctrl, Alt, None }

/// <summary>Current keyboard modifier state (useful inside click handlers).</summary>
public static class Input
{
    public static bool Shift => Native.GetKeyState(Native.VK_SHIFT) < 0;
    public static bool Ctrl => Native.GetKeyState(Native.VK_CONTROL) < 0;
    public static bool Alt => Native.GetKeyState(Native.VK_MENU) < 0;
}

/// <summary>
/// A widget living on the Windows 11 taskbar, rendered with Direct2D into a per-pixel-alpha layered window.
/// </summary>
/// <example>
/// <code>
/// var w = new Widget("Hello", ui => ui.Text("Hello!"));
/// w.Show();
/// Widget.Run();
/// </code>
/// </example>
public sealed unsafe class Widget : IDisposable
{
    // Layout of the widget box (DIPs). The hover pill is inset like native taskbar buttons.
    const float InsetX = 2, InsetY = 4, Padding = 8, HoverRadius = 5;

    const uint WM_INVALIDATE = Native.WM_APP + 1, WM_RELAYOUT = Native.WM_APP + 2, WM_INVOKE = Native.WM_APP + 3;
    const uint WM_THREAD_RECREATE = Native.WM_APP + 9; // posted to the thread when a window dies unexpectedly
    const int TimerAnim = 1, TimerSafety = 2, TimerTooltip = 3, TimerUserBase = 100;
    const int SafetyIntervalMs = 1000, TooltipDelayMs = 450;

    static readonly List<Widget> All = new();
    static IntPtr _fgHook, _locHook;
    static uint _locHookPid, _taskbarCreatedMsg, _uiThreadId;
    static Mutex? _instanceMutex;

    readonly string _name;
    readonly Action<Ui> _build;
    readonly WidgetOptions _options;
    readonly Surface _surface = new();
    readonly Tooltip _tooltip = new();
    readonly ConcurrentQueue<Action> _invokeQueue = new();
    readonly Dictionary<int, (Action Callback, uint Ms, bool Once)> _timers = new();
    int _nextTimer = TimerUserBase;

    IntPtr _hwnd, _owner;
    bool _closing, _disposed;
    int _invalidatePending, _relayoutPending;

    // Content
    Ui? _ui;
    List<Element> _elements = new();
    float[] _elemHover = Array.Empty<float>();
    float _scale = 1;
    int _width, _height;      // _height = taskbar band; the window is _overflow px taller
    int _overflow;
    bool _frameRequested;

    // Placement
    TaskbarInfo _tb;
    double _x;
    int _targetX, _y;
    bool _placed, _visible, _hiddenForFullscreen;

    // Interaction
    bool _mouseInside, _mouseDown, _dragging;
    int _hoverIndex = -1, _pressIndex = -1;
    float _hoverAnim, _pressAnim;
    int _downScreenX, _dragOffset;
    List<string>? _dragOriginalOrder;
    List<string>? _lastPreview;
    long _lastTick;

    public Widget(string name, Action<Ui> build, WidgetOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new ArgumentException("Widget name must be non-empty and contain only letters, digits, '_' or '-'.", nameof(name));
        _name = name;
        _build = build;
        _options = options ?? new WidgetOptions();
    }

    public string Name => _name;
    public IntPtr Handle => _hwnd;
    public float Scale => _scale;
    public Theme Theme => Theme.Current;

    /// <summary>Raised on the UI thread after the PC resumes from sleep (good moment to refresh data).</summary>
    public event Action? Resumed;

    // ── Public API ──────────────────────────────────────────────────────

    /// <summary>Creates the widget window and places it on the taskbar. Call on the thread that will run <see cref="Run"/>.</summary>
    public void Show()
    {
        if (_disposed || _hwnd != IntPtr.Zero) return;
        Log.Init(_name);

        if (_options.SingleInstance)
        {
            _instanceMutex = new Mutex(true, $@"Local\TaskbarWidget.Instance.{_name}", out bool created);
            if (!created) { Log.Write("Another instance is already running; exiting."); Environment.Exit(0); }
        }

        EnsureProcessInit();
        All.Add(this);
        WidgetOrder.EnsureRegistered(_name);

        var tb = TaskbarLayout.Query();
        CreateWindow(tb.Valid ? tb.Handle : IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("Could not create the widget window");
        Log.Write($"Started. {tb}");

        DrainInvokeQueue();
        Relayout();
        WidgetOrder.Broadcast(); // let other widgets make room
    }

    /// <summary>Rebuilds and repaints the widget. Thread-safe; multiple calls are coalesced.</summary>
    public void Invalidate()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (Interlocked.Exchange(ref _invalidatePending, 1) == 0)
            Native.PostMessageW(_hwnd, WM_INVALIDATE, 0, 0);
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread. Thread-safe.</summary>
    public void Post(Action action)
    {
        _invokeQueue.Enqueue(action);
        if (_hwnd != IntPtr.Zero) Native.PostMessageW(_hwnd, WM_INVOKE, 0, 0);
    }

    /// <summary>Repeating timer on the UI thread. Returns a handle for <see cref="ClearTimer"/>.</summary>
    public int SetInterval(TimeSpan interval, Action callback) => AddTimer(interval, callback, once: false);

    /// <summary>One-shot timer on the UI thread.</summary>
    public int SetTimeout(TimeSpan delay, Action callback) => AddTimer(delay, callback, once: true);

    public void ClearTimer(int handle)
    {
        if (_timers.Remove(handle) && _hwnd != IntPtr.Zero) Native.KillTimer(_hwnd, handle);
    }

    /// <summary>Opens the widget's context menu at the cursor.</summary>
    public void ShowMenu()
    {
        var mb = new MenuBuilder();
        _options.Menu?.Invoke(mb);
        if (mb.Items.Count > 0) mb.Separator();
        mb.Item("Exit", Exit);

        var menu = Native.CreatePopupMenu();
        for (int i = 0; i < mb.Items.Count; i++)
        {
            var it = mb.Items[i];
            uint flags = it.Text == null ? Native.MF_SEPARATOR
                : Native.MF_STRING | (it.Checked ? Native.MF_CHECKED : 0) | (it.Enabled ? 0 : Native.MF_GRAYED);
            Native.AppendMenuW(menu, flags, i + 1, it.Text);
        }

        _tooltip.Hide();
        Native.GetCursorPos(out var pt);
        bool fg = Native.SetForegroundWindow(_hwnd); // required so the menu closes when clicking elsewhere
        int cmd = Native.TrackPopupMenuEx(menu,
            Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_BOTTOMALIGN | Native.TPM_NONOTIFY,
            pt.X, pt.Y, _hwnd, IntPtr.Zero);
        Log.Debug($"menu fg={fg} cmd={cmd} err={Marshal.GetLastWin32Error()}");
        Native.DestroyMenu(menu);
        Native.PostMessageW(_hwnd, 0 /* WM_NULL */, 0, 0);

        if (cmd > 0 && cmd <= mb.Items.Count) mb.Items[cmd - 1].Action?.Invoke();
    }

    /// <summary>Runs the message loop until <see cref="Exit"/> is called or all widgets are closed.</summary>
    public static void Run()
    {
        Native.MSG msg;
        while (Native.GetMessageW(&msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.hwnd == IntPtr.Zero && msg.message == WM_THREAD_RECREATE)
            {
                foreach (var w in All.ToArray()) w.RecreateIfLost();
                continue;
            }
            Native.TranslateMessage(&msg);
            Native.DispatchMessageW(&msg);
        }
    }

    /// <summary>Closes every widget in this process and ends <see cref="Run"/>.</summary>
    public static void Exit()
    {
        foreach (var w in All.ToArray()) w.Dispose();
        Native.PostQuitMessage(0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _closing = true;
        _tooltip.Dispose();
        if (_hwnd != IntPtr.Zero && Native.IsWindow(_hwnd)) Native.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _surface.Dispose();
        All.Remove(this);
        WidgetOrder.Broadcast(); // neighbours close the gap
        if (All.Count == 0) RemoveHooks();
    }

    // ── Setup ───────────────────────────────────────────────────────────

    static void EnsureProcessInit()
    {
        if (_uiThreadId != 0) return;
        _uiThreadId = GetCurrentThreadId();
        Native.SetThreadDpiAwarenessContext(-4); // per-monitor v2 even if the app manifest forgot it
        Native.CoInitializeEx(IntPtr.Zero, 2);   // STA, for WIC
        _taskbarCreatedMsg = Native.RegisterWindowMessageW("TaskbarCreated");
        try { Native.SetPreferredAppMode(1); Native.FlushMenuThemes(); } catch { /* older Windows */ }
        _fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero,
            &WinEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        // Watchdog: a window-less timer with a TIMERPROC is dispatched even inside modal loops
        // (menus, message boxes), so a widget whose window died is always brought back.
        SetTimer(IntPtr.Zero, IntPtr.Zero, 2000, &WatchdogProc);
    }

    [DllImport("user32.dll")] static extern IntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, delegate* unmanaged<IntPtr, uint, IntPtr, uint, void> proc);

    [UnmanagedCallersOnly]
    static void WatchdogProc(IntPtr hwnd, uint msg, IntPtr id, uint time)
    {
        try { foreach (var w in All.ToArray()) w.RecreateIfLost(); } catch { }
    }

    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool PostThreadMessageW(uint thread, uint msg, IntPtr w, IntPtr l);

    /// <summary>Taskbar/tray geometry changes come from explorer; watch just that process (cheap, event driven).</summary>
    static void EnsureExplorerHook(IntPtr taskbar)
    {
        if (taskbar == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(taskbar, out uint pid);
        if (pid == 0 || pid == _locHookPid) return;
        if (_locHook != IntPtr.Zero) Native.UnhookWinEvent(_locHook);
        _locHook = Native.SetWinEventHook(Native.EVENT_OBJECT_LOCATIONCHANGE, Native.EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero,
            &WinEventProc, pid, 0, Native.WINEVENT_OUTOFCONTEXT);
        _locHookPid = pid;
    }

    static void RemoveHooks()
    {
        if (_fgHook != IntPtr.Zero) Native.UnhookWinEvent(_fgHook);
        if (_locHook != IntPtr.Zero) Native.UnhookWinEvent(_locHook);
        _fgHook = _locHook = IntPtr.Zero; _locHookPid = 0;
    }

    [UnmanagedCallersOnly]
    static void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (ev == Native.EVENT_OBJECT_LOCATIONCHANGE)
            {
                if (idObject != 0 || hwnd == IntPtr.Zero) return; // OBJID_WINDOW only (skips cursor/caret noise)
                foreach (var w in All)
                    if (hwnd == w._tb.Handle || hwnd == w._tb.TrayHandle) w.RequestRelayout();
            }
            else
            {
                foreach (var w in All) w.RequestRelayout();
            }
        }
        catch { }
    }

    void CreateWindow(IntPtr owner)
    {
        _owner = owner;
        _hwnd = Win32Window.Create(TaskbarLayout.ClassPrefix + _name,
            Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE,
            owner, WndProc);
        if (_hwnd == IntPtr.Zero && owner != IntPtr.Zero)
        {
            // The owner may be a taskbar that is being torn down; try unowned, re-own later.
            _owner = IntPtr.Zero;
            _hwnd = Win32Window.Create(TaskbarLayout.ClassPrefix + _name,
                Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE,
                IntPtr.Zero, WndProc);
        }
        if (_hwnd == IntPtr.Zero) { Log.Write("CreateWindowEx failed; will retry."); return; }

        // Fresh window: forget per-window state. Messages posted to a dead window were lost, so
        // clear the coalescing flags and re-post anything outstanding.
        _placed = _visible = false;
        _mouseInside = _mouseDown = _dragging = false;
        _pressIndex = _hoverIndex = -1;
        _invalidatePending = _relayoutPending = 0;
        Native.SetTimer(_hwnd, TimerSafety, SafetyIntervalMs, IntPtr.Zero);
        foreach (var (id, t) in _timers) Native.SetTimer(_hwnd, id, t.Ms, IntPtr.Zero);
        EnsureExplorerHook(_owner);
        if (!_invokeQueue.IsEmpty) Native.PostMessageW(_hwnd, WM_INVOKE, 0, 0);
    }

    /// <summary>Our window can be destroyed along with its owner when explorer crashes. Bring it back.</summary>
    void RecreateIfLost()
    {
        if (_closing || _disposed || (_hwnd != IntPtr.Zero && Native.IsWindow(_hwnd))) return;
        Log.Write("Widget window was destroyed (explorer restart?); recreating.");
        var tb = TaskbarLayout.Query();
        CreateWindow(tb.Valid ? tb.Handle : IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return; // watchdog retries
        Rebuild();
        Relayout();
    }

    int AddTimer(TimeSpan t, Action cb, bool once)
    {
        int id = _nextTimer++;
        uint ms = (uint)Math.Clamp(t.TotalMilliseconds, 10, int.MaxValue);
        _timers[id] = (cb, ms, once);
        if (_hwnd != IntPtr.Zero) Native.SetTimer(_hwnd, id, ms, IntPtr.Zero);
        return id;
    }

    void DrainInvokeQueue()
    {
        while (_invokeQueue.TryDequeue(out var a))
        {
            try { a(); } catch (Exception ex) { Log.Write($"Posted action failed: {ex}"); }
        }
    }

    // ── Build / paint ───────────────────────────────────────────────────

    void Rebuild()
    {
        var ui = new Ui(_scale, Theme.Current);
        try { _build(ui); }
        catch (Exception ex) { Log.Write($"Build callback failed: {ex}"); }

        float x = 0;
        bool first = true, prevSpacer = false;
        foreach (var e in ui.Elements)
        {
            if (e.IsSpacer) { x += e.Width; prevSpacer = true; continue; }
            if (!first && !prevSpacer) x += ui.Spacing;
            e.X = x;
            x += e.Width;
            first = false; prevSpacer = false;
        }

        _ui = ui;
        _elements = ui.Elements;
        if (_elemHover.Length != _elements.Count) Array.Resize(ref _elemHover, _elements.Count);
        if (_hoverIndex >= _elements.Count) _hoverIndex = -1;
        _width = (int)MathF.Ceiling((Math.Max(x, 8) + 2 * (InsetX + Padding)) * _scale);
    }

    void Paint()
    {
        if (_hwnd == IntPtr.Zero || _height <= 0 || _width <= 0) return;
        var theme = Theme.Current;
        float s = _scale;
        float top = _overflow;

        _surface.EnsureSize(_width, _height + _overflow);
        _surface.Begin(hitAlpha: false);
        // The taskbar band gets a near-invisible fill so all of it receives mouse input (fully transparent
        // layered pixels are click-through). The overflow area above stays click-through unless drawn on.
        _surface.FillRect(new RectF(0, top, _width, top + _height), new Color(1, 0, 0, 0));

        // Hover pill, like native taskbar buttons.
        float hv = Ease(_hoverAnim);
        if (hv > 0.001f)
        {
            var box = new RectF(InsetX * s, top + InsetY * s, _width - InsetX * s, top + _height - InsetY * s);
            var fill = Color.Lerp(theme.HoverFill, theme.PressedFill, Ease(_pressAnim));
            _surface.FillRoundRect(box, HoverRadius * s, fill.WithOpacity(hv));
            float hb = 0.5f * MathF.Max(1, MathF.Round(s));
            _surface.StrokeRoundRect(new RectF(box.L + hb, box.T + hb, box.R - hb, box.B - hb), HoverRadius * s,
                theme.HoverStroke.WithOpacity(hv), hb * 2);
        }

        _frameRequested = false;
        var g = new Graphics(s, theme) { Surface = _surface, FrameRequested = () => _frameRequested = true };
        float ox = (InsetX + Padding) * s;
        // The hovered element is drawn last so pop-out effects sit on top of their neighbours.
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = 0; i < _elements.Count; i++)
            {
                if ((i == _hoverIndex) != (pass == 1)) continue;
                var e = _elements[i];
                if (e.IsSpacer || e.Draw == null) continue;
                g.OriginX = MathF.Round(ox + e.X * s);
                g.OriginY = MathF.Round(top + (_height - e.Height * s) / 2f);
                var state = new ElementState(Ease(_elemHover[i]), i == _pressIndex ? Ease(_pressAnim) : 0);
                try { e.Draw(g, state); }
                catch (Exception ex) { Log.Write($"Draw failed: {ex.Message}"); }
            }
        }

        _surface.End();
        Present();
        if (_frameRequested) StartAnim(); // an element is animating: keep the frame clock running
    }

    void Present()
    {
        var pos = new Native.POINT { X = (int)Math.Round(_x), Y = _y };
        var size = new Native.SIZE { cx = _surface.Width, cy = _surface.Height };
        var src = new Native.POINT();
        var blend = new Native.BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = 1 };
        Native.UpdateLayeredWindow(_hwnd, IntPtr.Zero, &pos, &size, _surface.Hdc, &src, 0, &blend, Native.ULW_ALPHA);
    }

    static float Ease(float t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - 2 * t);

    // ── Placement ───────────────────────────────────────────────────────

    void RequestRelayout()
    {
        if (_hwnd != IntPtr.Zero && Interlocked.Exchange(ref _relayoutPending, 1) == 0)
            Native.PostMessageW(_hwnd, WM_RELAYOUT, 0, 0);
    }

    void Relayout()
    {
        _relayoutPending = 0;
        if (_hwnd == IntPtr.Zero || _closing || _dragging) return;

        var tb = TaskbarLayout.Query();
        if (!tb.Valid || !tb.OnScreen)
        {
            // Never guess a position: with no usable taskbar the old code fell back to (x, 0) — the
            // "widget jumped to the top of the screen" bug. Hide and wait for the next valid geometry.
            if (_tb.Valid != tb.Valid || _tb.OnScreen != tb.OnScreen) Log.Write($"Hiding: {tb}");
            _tb = tb;
            SetVisible(false);
            return;
        }

        if (tb.Handle != _owner)
        {
            // Explorer restarted: re-own the window to the new taskbar so it stays above it in z-order.
            Log.Write($"Taskbar changed ({_owner:X} -> {tb.Handle:X}); re-owning.");
            Native.SetWindowLongPtrW(_hwnd, -8 /* GWLP_HWNDPARENT */, tb.Handle);
            _owner = tb.Handle;
            // The new taskbar was created topmost above us; re-owning alone doesn't change z-order.
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            EnsureExplorerHook(tb.Handle);
            _placed = false;
        }

        bool geometryChanged = MathF.Abs(tb.Scale - _scale) > 0.001f || tb.Rect.Height != _height || _ui == null;
        if (!_tb.Valid || !_tb.OnScreen || tb.Rect.Top != _tb.Rect.Top) _placed = false;
        _tb = tb;
        if (geometryChanged)
        {
            int oldWidth = _width;
            _scale = tb.Scale;
            _height = tb.Rect.Height;
            _overflow = (int)MathF.Ceiling(_options.OverflowTop * _scale);
            Rebuild();
            if (_width != oldWidth) WidgetOrder.Broadcast(); // neighbours must use our new width
        }

        if (TaskbarLayout.IsFullscreenAppActive(tb))
        {
            if (!_hiddenForFullscreen) Log.Debug("fullscreen app → hide");
            _hiddenForFullscreen = true;
            SetVisible(false);
            return;
        }
        _hiddenForFullscreen = false;

        int x = TaskbarLayout.ComputeX(tb, _name, _hwnd, _width);
        _targetX = x;
        _y = tb.Rect.Top - _overflow;

        if (!_placed || !_visible)
        {
            _x = x;          // snap: first placement / coming back from hidden
            _placed = true;
            Paint();
            SetVisible(true);
        }
        else if (geometryChanged)
        {
            Paint();
            if ((int)Math.Round(_x) != x) StartAnim();
        }
        else if ((int)Math.Round(_x) != x)
        {
            StartAnim();     // glide to the new slot
        }
    }

    void SetVisible(bool visible)
    {
        if (visible == _visible) return;
        _visible = visible;
        if (visible)
        {
            Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        else
        {
            _tooltip.Hide();
            Native.ShowWindow(_hwnd, Native.SW_HIDE);
        }
        WidgetOrder.Broadcast(); // others include/exclude us
    }

    void StartAnim()
    {
        _lastTick = Environment.TickCount64;
        Native.SetTimer(_hwnd, TimerAnim, 15, IntPtr.Zero);
    }

    void AnimTick()
    {
        long now = Environment.TickCount64;
        float dt = Math.Clamp(now - _lastTick, 1, 50) / 1000f;
        _lastTick = now;
        bool active = false, repaint = _frameRequested, moved = false;

        // Position: exponential glide (time-constant based, so frame drops don't change the speed).
        if (!_dragging && Math.Abs(_targetX - _x) > 0.01)
        {
            double k = 1 - Math.Exp(-dt / 0.055);
            _x += (_targetX - _x) * k;
            if (Math.Abs(_targetX - _x) < 0.5) _x = _targetX; else active = true;
            moved = true;
        }

        repaint |= Step(ref _hoverAnim, _mouseInside && !_dragging ? 1 : 0, dt / 0.12f, ref active);
        repaint |= Step(ref _pressAnim, _mouseDown && !_dragging ? 1 : 0, dt / 0.08f, ref active);
        for (int i = 0; i < _elemHover.Length; i++)
            repaint |= Step(ref _elemHover[i], i == _hoverIndex && !_dragging ? 1 : 0, dt / 0.14f, ref active);

        if (repaint) Paint(); // also presents at the new position
        if (_frameRequested) active = true;
        else if (moved)
            Native.SetWindowPos(_hwnd, IntPtr.Zero, (int)Math.Round(_x), _y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        if (!active) Native.KillTimer(_hwnd, TimerAnim);
    }

    static bool Step(ref float v, float target, float amount, ref bool active)
    {
        if (v == target) return false;
        v = target > v ? Math.Min(target, v + amount) : Math.Max(target, v - amount);
        if (v != target) active = true;
        return true;
    }

    // ── Input ───────────────────────────────────────────────────────────

    int HitTest(int px)
    {
        float s = _scale, ox = (InsetX + Padding) * s;
        float half = (_ui?.Spacing ?? 0) * s / 2;
        for (int i = 0; i < _elements.Count; i++)
        {
            var e = _elements[i];
            if (e.IsSpacer || !e.Interactive) continue;
            float l = ox + e.X * s - half, r = ox + (e.X + e.Width) * s + half;
            if (px >= l && px < r) return i;
        }
        return -1;
    }

    (string? Title, string? Body) CurrentTooltip()
    {
        if (_hoverIndex >= 0 && _hoverIndex < _elements.Count)
        {
            var e = _elements[_hoverIndex];
            if (e.TipTitle != null || e.TipBody != null) return (e.TipTitle, e.TipBody);
        }
        return (_ui?.TooltipTitle, _ui?.TooltipBody);
    }

    void ShowTooltip()
    {
        if (!_mouseInside || _dragging || _mouseDown || !_visible) return;
        var (title, body) = CurrentTooltip();
        float s = _scale;
        var anchor = new Native.RECT
        {
            Left = (int)Math.Round(_x + InsetX * s), Right = (int)Math.Round(_x + _width - InsetX * s),
            Top = _overflow > 0 ? _y : _y + (int)(InsetY * s), Bottom = _y + _overflow + _height,
        };
        _tooltip.Show(_hwnd, anchor, title, body, s, Theme.Current);
    }

    void ArmLeaveTracking()
    {
        var tme = new Native.TRACKMOUSEEVENT { cbSize = (uint)sizeof(Native.TRACKMOUSEEVENT), dwFlags = Native.TME_LEAVE, hwndTrack = _hwnd };
        Native.TrackMouseEvent(&tme);
    }

    void OnMouseMove(int x)
    {
        // Re-armed on every move: capture (click/drag) can swallow the pending leave notification.
        if (!_mouseDown) ArmLeaveTracking();
        if (!_mouseInside)
        {
            _mouseInside = true;
            Native.SetTimer(_hwnd, TimerTooltip, TooltipDelayMs, IntPtr.Zero);
            StartAnim();
        }

        if (_mouseDown && !_dragging && _options.AllowReorder)
        {
            Native.GetCursorPos(out var p);
            if (Math.Abs(p.X - _downScreenX) > 5 * _scale) BeginDrag();
        }
        if (_dragging)
        {
            Native.GetCursorPos(out var p);
            DragTo(p.X);
            return;
        }

        int idx = HitTest(x);
        if (idx != _hoverIndex)
        {
            _hoverIndex = idx;
            StartAnim();
            if (_tooltip.IsVisible) ShowTooltip(); // update in place
        }
    }

    void OnMouseLeave()
    {
        if (_dragging || _mouseDown) return; // captured; we'll get a real leave after release
        _mouseInside = false;
        _hoverIndex = -1;
        Native.KillTimer(_hwnd, TimerTooltip);
        _tooltip.Hide();
        StartAnim();
    }

    void OnLeftDown(int x)
    {
        Log.Debug($"down x={x} hit={HitTest(x)}");
        _mouseDown = true;
        _pressIndex = HitTest(x);
        Native.GetCursorPos(out var p);
        _downScreenX = p.X;
        Native.SetCapture(_hwnd);
        Native.KillTimer(_hwnd, TimerTooltip);
        _tooltip.Hide();
        StartAnim();
    }

    void OnLeftUp(int x, int y)
    {
        Log.Debug($"up x={x} y={y} down={_mouseDown} drag={_dragging} press={_pressIndex} hit={HitTest(x)}");
        if (!_mouseDown) return;
        _mouseDown = false;
        bool wasDragging = _dragging;
        if (wasDragging) EndDrag(commit: true);
        Native.ReleaseCapture();
        StartAnim();

        bool inside = x >= 0 && y >= 0 && x < _width && y < _height + _overflow;
        if (!inside) { OnMouseLeave(); return; }
        ArmLeaveTracking();
        if (wasDragging) return;

        int idx = HitTest(x);
        if (idx != _pressIndex) return;
        var handler = idx >= 0 ? _elements[idx].Click : null;
        (handler ?? _ui?.Click)?.Invoke();
    }

    void OnRightUp(int x)
    {
        int idx = HitTest(x);
        var handler = idx >= 0 ? _elements[idx].RightClick : null;
        handler ??= _ui?.RightClick;
        bool forceMenu = _options.MenuModifier switch
        {
            MenuModifier.Shift => Input.Shift,
            MenuModifier.Ctrl => Input.Ctrl,
            MenuModifier.Alt => Input.Alt,
            _ => false,
        };
        if (handler == null || forceMenu) ShowMenu();
        else handler();
    }

    // ── Drag to reorder ─────────────────────────────────────────────────

    void BeginDrag()
    {
        _dragging = true;
        _dragOriginalOrder = new List<string>(WidgetOrder.Read());
        _dragOffset = _downScreenX - (int)Math.Round(_x);
        _lastPreview = null;
        _pressIndex = -1;
        Native.KillTimer(_hwnd, TimerTooltip);
        _tooltip.Hide();
        StartAnim();
    }

    void DragTo(int cursorX)
    {
        var tb = _tb;
        int x = cursorX - _dragOffset;
        if (!tb.Valid) return;
        int minX = tb.Rect.Left + tb.Rect.Width / 3, maxX = tb.RightEdge - _width;
        x = Math.Clamp(x, Math.Min(minX, maxX), maxX);
        _x = _targetX = x;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, _y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);

        // Pick the insertion slot whose centre is nearest ours. Slots come from the deterministic
        // layout (not from where neighbours currently are mid-animation), so this can't flicker.
        var order = WidgetOrder.Read();
        var others = TaskbarLayout.RunningWidgets().Where(w => w.Hwnd != _hwnd)
            .OrderBy(w => IndexOrMax(order, w.Name)).ThenBy(w => w.Name, StringComparer.Ordinal).ToList();

        float myCenter = x + _width / 2f;
        int best = 0; float bestDist = float.MaxValue, right = tb.RightEdge;
        for (int k = 0; k <= others.Count; k++)
        {
            float d = MathF.Abs(right - _width / 2f - myCenter);
            if (d < bestDist) { bestDist = d; best = k; }
            if (k < others.Count) right -= others[k].Rect.Width;
        }

        var preview = others.Select(w => w.Name).ToList();
        preview.Insert(best, _name);
        if (_lastPreview == null || !_lastPreview.SequenceEqual(preview))
        {
            _lastPreview = preview;
            WidgetOrder.Save(preview);
            WidgetOrder.Broadcast();
        }
    }

    static int IndexOrMax(List<string> order, string name) { int i = order.IndexOf(name); return i < 0 ? int.MaxValue : i; }

    void EndDrag(bool commit)
    {
        if (!_dragging) return;
        _dragging = false;
        if (!commit && _dragOriginalOrder != null) WidgetOrder.Save(_dragOriginalOrder);
        _dragOriginalOrder = null;
        _lastPreview = null;
        WidgetOrder.Broadcast();
        Relayout(); // glide into the final slot
    }

    // ── Window procedure ────────────────────────────────────────────────

    IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == WidgetOrder.RepositionMessage || (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0))
        {
            RequestRelayout();
            return 0;
        }

        switch (msg)
        {
            case WM_INVALIDATE:
            {
                _invalidatePending = 0;
                int oldWidth = _width;
                Rebuild();
                if (_width != oldWidth && _placed && !_dragging)
                {
                    _x += oldWidth - _width;   // grow/shrink to the left, keeping the right edge put
                    _targetX += oldWidth - _width;
                }
                Paint();
                if (_width != oldWidth) { Relayout(); WidgetOrder.Broadcast(); }
                if (_tooltip.IsVisible) ShowTooltip();
                return 0;
            }
            case WM_RELAYOUT: Relayout(); return 0;
            case WM_INVOKE: DrainInvokeQueue(); return 0;

            case Native.WM_TIMER:
            {
                int id = (int)w;
                if (id == TimerAnim) AnimTick();
                else if (id == TimerSafety) Relayout(); // self-heal anything a missed event left behind
                else if (id == TimerTooltip) { Native.KillTimer(hwnd, TimerTooltip); ShowTooltip(); }
                else if (_timers.TryGetValue(id, out var t))
                {
                    if (t.Once) ClearTimer(id);
                    try { t.Callback(); } catch (Exception ex) { Log.Write($"Timer callback failed: {ex}"); }
                }
                return 0;
            }

            case Native.WM_MOUSEMOVE: OnMouseMove(Native.LoWord(l)); return 0;
            case Native.WM_MOUSELEAVE: OnMouseLeave(); return 0;
            case Native.WM_LBUTTONDOWN: OnLeftDown(Native.LoWord(l)); return 0;
            case Native.WM_LBUTTONUP: OnLeftUp(Native.LoWord(l), Native.HiWord(l)); return 0;
            case Native.WM_RBUTTONUP: OnRightUp(Native.LoWord(l)); return 0;
            case Native.WM_RBUTTONDOWN: return 0;
            case Native.WM_MOUSEACTIVATE: return Native.MA_NOACTIVATE;
            case Native.WM_CAPTURECHANGED:
                if (l != hwnd)
                {
                    if (_dragging) EndDrag(commit: false);
                    if (_mouseDown) { _mouseDown = false; _pressIndex = -1; StartAnim(); }
                    if (_mouseInside) ArmLeaveTracking();
                }
                return 0;
            case Native.WM_SETCURSOR:
                Native.SetCursor(Native.LoadCursorW(IntPtr.Zero, _dragging ? Native.IDC_SIZEWE : Native.IDC_ARROW));
                return 1;

            case Native.WM_SETTINGCHANGE:
            {
                var area = l != IntPtr.Zero ? new string((char*)l) : null;
                if (area == "ImmersiveColorSet")
                {
                    Theme.Refresh();
                    try { Native.FlushMenuThemes(); } catch { }
                    Rebuild();
                    Paint();
                }
                RequestRelayout();
                return 0;
            }
            case Native.WM_DISPLAYCHANGE: RequestRelayout(); return 0;
            case Native.WM_DPICHANGED: RequestRelayout(); return 0; // DPI follows the taskbar, not the suggested rect
            case Native.WM_POWERBROADCAST:
                if ((int)w == Native.PBT_APMRESUMEAUTOMATIC)
                {
                    RequestRelayout();
                    try { Resumed?.Invoke(); } catch (Exception ex) { Log.Write($"Resumed handler failed: {ex}"); }
                }
                return 1;

            case Native.WM_DESTROY:
                if (hwnd == _hwnd)
                {
                    _hwnd = IntPtr.Zero;
                    _visible = false;
                    if (!_closing) PostThreadMessageW(_uiThreadId, WM_THREAD_RECREATE, 0, 0);
                }
                return 0;
        }
        return Native.DefWindowProcW(hwnd, msg, w, l);
    }
}
