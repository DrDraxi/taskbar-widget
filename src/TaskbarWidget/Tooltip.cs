namespace TaskbarWidget;

/// <summary>Windows 11 style tooltip: rounded, bordered, soft shadow, fades and slides in above the taskbar.</summary>
internal sealed unsafe class Tooltip : IDisposable
{
    const string ClassName = "TaskbarWidget.Tooltip"; // must NOT use the widget class prefix
    const int AnimTimer = 1;
    const float MaxTextWidth = 340, PadX = 11, PadY = 8, Shadow = 12, Radius = 7, Gap = 10, TitleGap = 3, Slide = 6;
    const float FadeInMs = 150, FadeOutMs = 110;

    IntPtr _hwnd, _owner;
    readonly Surface _surface = new();
    string? _title, _body;
    float _scale = 1;
    int _x, _y; // final (settled) window position
    float _progress; // 0 hidden … 1 shown
    bool _showing;
    long _lastTick;

    public bool IsVisible => _hwnd != IntPtr.Zero && _progress > 0;

    /// <summary>Shows (or updates in place) the tooltip above <paramref name="anchor"/> (screen px).</summary>
    public void Show(IntPtr owner, Native.RECT anchor, string? title, string? body, float scale, Theme theme)
    {
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(body)) { Hide(); return; }
        EnsureWindow(owner);
        if (_hwnd == IntPtr.Zero) return;

        bool contentChanged = title != _title || body != _body || scale != _scale;
        _title = title; _body = body; _scale = scale;
        if (contentChanged || !_showing || _progress == 0) Render(anchor, theme);

        if (!_showing)
        {
            _showing = true;
            if (_progress == 0)
            {
                Native.ShowWindow(_hwnd, Native.SW_SHOWNOACTIVATE);
                Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            StartAnim();
        }
        else Present();
    }

    public void Hide()
    {
        if (!_showing) return;
        _showing = false;
        StartAnim();
    }

    void EnsureWindow(IntPtr owner)
    {
        if (_hwnd != IntPtr.Zero && Native.IsWindow(_hwnd) && owner == _owner) return;
        if (_hwnd != IntPtr.Zero && Native.IsWindow(_hwnd)) Native.DestroyWindow(_hwnd);
        _owner = owner;
        _progress = 0; _showing = false;
        _hwnd = Win32Window.Create(ClassName,
            Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE,
            owner, WndProc);
    }

    void Render(Native.RECT anchor, Theme theme)
    {
        float s = _scale;
        float maxW = MaxTextWidth * s;
        IntPtr titleLayout = 0, bodyLayout = 0;
        try
        {
            float textW = 0, titleH = 0, bodyH = 0;
            if (!string.IsNullOrEmpty(_title))
            {
                titleLayout = D2D.CreateLayout(_title, D2D.TextFormat(12.5f * s, 600), maxW, wrap: true);
                var m = D2D.Metrics(titleLayout);
                textW = Math.Max(textW, m.Width); titleH = m.Height;
            }
            if (!string.IsNullOrEmpty(_body))
            {
                bodyLayout = D2D.CreateLayout(_body, D2D.TextFormat(12f * s, 400), maxW, wrap: true);
                var m = D2D.Metrics(bodyLayout);
                textW = Math.Max(textW, m.Width); bodyH = m.Height;
            }
            float gap = titleLayout != 0 && bodyLayout != 0 ? TitleGap * s : 0;

            float boxW = MathF.Ceiling(textW + 2 * PadX * s);
            float boxH = MathF.Ceiling(titleH + gap + bodyH + 2 * PadY * s);
            float sh = Shadow * s;
            int w = (int)MathF.Ceiling(boxW + 2 * sh), h = (int)MathF.Ceiling(boxH + 2 * sh);

            // Centre above the anchor, clamped to the monitor.
            var mon = Native.MonitorFromRect(&anchor, Native.MONITOR_DEFAULTTONEAREST);
            var mi = new Native.MONITORINFO { cbSize = (uint)sizeof(Native.MONITORINFO) };
            Native.GetMonitorInfoW(mon, &mi);
            float boxX = (anchor.Left + anchor.Right) / 2f - boxW / 2f;
            float margin = 8 * s;
            boxX = Math.Clamp(boxX, mi.rcMonitor.Left + margin, Math.Max(mi.rcMonitor.Left + margin, mi.rcMonitor.Right - margin - boxW));
            float boxY = anchor.Top - Gap * s - boxH;
            _x = (int)MathF.Round(boxX - sh);
            _y = (int)MathF.Round(boxY - sh);

            _surface.EnsureSize(w, h);
            _surface.Begin(hitAlpha: false);

            var box = new RectF(sh, sh, sh + boxW, sh + boxH);
            float r = Radius * s;

            // Soft shadow: stacked, increasingly transparent rounded rects, offset slightly downwards.
            float strength = theme.IsDark ? 0.075f : 0.035f;
            int steps = (int)MathF.Max(4, MathF.Round(sh * 0.8f));
            for (int i = steps; i >= 1; i--)
            {
                float k = i / (float)steps;               // 1 = outermost
                float grow = k * sh * 0.85f;
                float a = strength * (1 - k) * (1 - k) * 2.2f;
                _surface.FillRoundRect(new RectF(box.L - grow, box.T - grow + 2 * s, box.R + grow, box.B + grow + 2 * s),
                    r + grow, Color.Black.WithOpacity(Math.Min(a, 1)));
            }

            _surface.FillRoundRect(box, r, theme.TooltipBackground);
            float hb = 0.5f * MathF.Max(1, MathF.Round(s));
            _surface.StrokeRoundRect(new RectF(box.L + hb, box.T + hb, box.R - hb, box.B - hb), r, theme.TooltipBorder, hb * 2);

            float tx = MathF.Round(box.L + PadX * s), ty = MathF.Round(box.T + PadY * s);
            if (titleLayout != 0) _surface.DrawLayout(titleLayout, tx, ty, theme.Text);
            if (bodyLayout != 0)
                _surface.DrawLayout(bodyLayout, tx, MathF.Round(ty + titleH + gap), titleLayout != 0 ? theme.TextSecondary : theme.Text);

            _surface.End();
        }
        finally
        {
            Com.Release(ref titleLayout);
            Com.Release(ref bodyLayout);
        }
    }

    void Present()
    {
        float p = _progress;
        float eased = 1 - (1 - p) * (1 - p) * (1 - p); // ease-out cubic
        var pos = new Native.POINT { X = _x, Y = _y + (int)MathF.Round((1 - eased) * Slide * _scale) };
        var size = new Native.SIZE { cx = _surface.Width, cy = _surface.Height };
        var src = new Native.POINT();
        var blend = new Native.BLENDFUNCTION { SourceConstantAlpha = (byte)MathF.Round(eased * 255), AlphaFormat = 1 };
        Native.UpdateLayeredWindow(_hwnd, IntPtr.Zero, &pos, &size, _surface.Hdc, &src, 0, &blend, Native.ULW_ALPHA);
    }

    void StartAnim()
    {
        _lastTick = Environment.TickCount64;
        Native.SetTimer(_hwnd, AnimTimer, 15, IntPtr.Zero);
    }

    void Tick()
    {
        long now = Environment.TickCount64;
        float dt = Math.Clamp(now - _lastTick, 1, 50);
        _lastTick = now;

        _progress = _showing ? Math.Min(1, _progress + dt / FadeInMs) : Math.Max(0, _progress - dt / FadeOutMs);
        Present();

        if (_showing ? _progress >= 1 : _progress <= 0)
        {
            Native.KillTimer(_hwnd, AnimTimer);
            if (!_showing) Native.ShowWindow(_hwnd, Native.SW_HIDE);
        }
    }

    IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr w, IntPtr l)
    {
        if (msg == Native.WM_TIMER && w == AnimTimer) { Tick(); return 0; }
        if (msg == Native.WM_MOUSEACTIVATE) return Native.MA_NOACTIVATE;
        if (msg == Native.WM_DESTROY && hwnd == _hwnd) { _hwnd = IntPtr.Zero; _progress = 0; _showing = false; }
        return Native.DefWindowProcW(hwnd, msg, w, l);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero && Native.IsWindow(_hwnd)) Native.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _surface.Dispose();
    }
}
