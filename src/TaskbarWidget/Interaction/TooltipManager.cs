using System.Runtime.InteropServices;
using TaskbarWidget.Rendering;
using TaskbarWidget.Theming;

namespace TaskbarWidget.Interaction;

/// <summary>
/// Custom layered tooltip window with fade animation.
/// Extracted/generalized from Menu500Widget.
/// </summary>
internal sealed class TooltipManager : IDisposable
{
    private const string TooltipClassName = "TaskbarWidgetTooltip";
    private const int ShowDelayMs = 400;
    private const int FadeIntervalMs = 16;
    private const int FadeInStep = 30;
    private const int FadeOutStep = 45;
    private const int MaxWidthDip = 360;
    private const int PaddingDip = 16;
    private const int CornerRadiusDip = 4;
    private const int GapDip = 8;
    private const int TitleBodyGapDip = 6;

    // Timer IDs (high to avoid collision)
    internal const int ShowTimerId = 50;
    internal const int FadeTimerId = 51;

    private IntPtr _hwndTooltip;
    private WndProcDelegate? _tooltipWndProc;
    private bool _classRegistered;

    // Tooltip content
    private string? _title;
    private string? _body;

    // Render state
    private IntPtr _memDc;
    private IntPtr _bitmap;
    private IntPtr _oldBitmap;
    private int _posX, _posY;
    private int _renderW, _renderH;
    private byte _alpha;
    private byte _targetAlpha;
    private bool _visible;

    public bool IsVisible => _visible;

    public void EnsureWindow()
    {
        if (_hwndTooltip != IntPtr.Zero) return;

        _tooltipWndProc = (hwnd, msg, wParam, lParam) =>
            Native.DefWindowProcW(hwnd, msg, wParam, lParam);

        if (!_classRegistered)
        {
            var wndClass = new Native.WNDCLASS
            {
                lpszClassName = TooltipClassName,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_tooltipWndProc),
                hInstance = Native.GetModuleHandleW(null)
            };
            Native.RegisterClassW(ref wndClass);
            _classRegistered = true;
        }

        _hwndTooltip = Native.CreateWindowExW(
            Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE,
            TooltipClassName, string.Empty, Native.WS_POPUP,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandleW(null), IntPtr.Zero);
    }

    /// <summary>
    /// Request showing a tooltip. Starts the delay timer on the widget HWND.
    /// </summary>
    public void RequestShow(IntPtr widgetHwnd, string? title, string? body)
    {
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(body)) return;
        _title = title;
        _body = body;

        if (_visible && _targetAlpha == 0)
        {
            // Fading out - reverse
            _targetAlpha = 255;
            Native.SetTimer(widgetHwnd, (IntPtr)FadeTimerId, FadeIntervalMs, IntPtr.Zero);
        }
        else if (!_visible)
        {
            Native.SetTimer(widgetHwnd, (IntPtr)ShowTimerId, ShowDelayMs, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Called when the show-delay timer fires.
    /// </summary>
    public void OnShowTimer(IntPtr widgetHwnd, double dpiScale)
    {
        Native.KillTimer(widgetHwnd, (IntPtr)ShowTimerId);
        Show(widgetHwnd, dpiScale);
    }

    /// <summary>
    /// Immediately show the tooltip.
    /// </summary>
    public void Show(IntPtr widgetHwnd, double dpiScale)
    {
        EnsureWindow();
        if (_hwndTooltip == IntPtr.Zero) return;
        if (string.IsNullOrEmpty(_title) && string.IsNullOrEmpty(_body)) return;

        RenderBitmap(widgetHwnd, dpiScale);

        if (!_visible)
        {
            _alpha = 0;
            Native.ShowWindow(_hwndTooltip, Native.SW_SHOW);
            _visible = true;
        }

        _targetAlpha = 255;
        ApplyAlpha();

        if (_alpha < 255)
            Native.SetTimer(widgetHwnd, (IntPtr)FadeTimerId, FadeIntervalMs, IntPtr.Zero);
    }

    /// <summary>
    /// Start hiding the tooltip (fade out).
    /// </summary>
    public void Hide(IntPtr widgetHwnd)
    {
        Native.KillTimer(widgetHwnd, (IntPtr)ShowTimerId);
        if (_visible)
        {
            _targetAlpha = 0;
            Native.SetTimer(widgetHwnd, (IntPtr)FadeTimerId, FadeIntervalMs, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Called on each fade timer tick. Returns true if still animating.
    /// </summary>
    public bool OnFadeTimer(IntPtr widgetHwnd)
    {
        if (_alpha < _targetAlpha)
            _alpha = (byte)Math.Min(255, _alpha + FadeInStep);
        else if (_alpha > _targetAlpha)
            _alpha = (byte)Math.Max(0, _alpha - FadeOutStep);

        ApplyAlpha();

        if (_alpha == _targetAlpha)
        {
            Native.KillTimer(widgetHwnd, (IntPtr)FadeTimerId);
            if (_alpha == 0)
            {
                Native.ShowWindow(_hwndTooltip, Native.SW_HIDE);
                _visible = false;
                FreeBitmap();
            }
            return false;
        }
        return true;
    }

    /// <summary>
    /// Update tooltip content while visible.
    /// </summary>
    public void UpdateContent(IntPtr widgetHwnd, string? title, string? body, double dpiScale)
    {
        _title = title;
        _body = body;
        if (_visible)
            Show(widgetHwnd, dpiScale);
    }

    private unsafe void RenderBitmap(IntPtr widgetHwnd, double dpiScale)
    {
        FreeBitmap();

        var theme = ThemeDetector.CurrentTheme;
        int padding = (int)(PaddingDip * dpiScale);
        int maxWidth = (int)(MaxWidthDip * dpiScale);
        int cr = (int)(CornerRadiusDip * dpiScale);
        int titleBodyGap = (int)(TitleBodyGapDip * dpiScale);

        int titleFontSize = -(int)(13 * dpiScale);
        int bodyFontSize = -(int)(12 * dpiScale);

        var hTitleFont = Native.CreateFontW(
            titleFontSize, 0, 0, 0, 600, 0, 0, 0, Native.DEFAULT_CHARSET,
            Native.OUT_DEFAULT_PRECIS, Native.CLIP_DEFAULT_PRECIS,
            Native.CLEARTYPE_QUALITY, Native.DEFAULT_PITCH, "Segoe UI");

        var hBodyFont = Native.CreateFontW(
            bodyFontSize, 0, 0, 0, Native.FW_NORMAL, 0, 0, 0, Native.DEFAULT_CHARSET,
            Native.OUT_DEFAULT_PRECIS, Native.CLIP_DEFAULT_PRECIS,
            Native.CLEARTYPE_QUALITY, Native.DEFAULT_PITCH, "Segoe UI");

        var screenDc = Native.GetDC(IntPtr.Zero);
        int maxTextWidth = maxWidth - 2 * padding;

        int titleWidth = 0, titleHeight = 0;
        if (!string.IsNullOrEmpty(_title))
        {
            var oldF = Native.SelectObject(screenDc, hTitleFont);
            var rect = new Native.RECT { Right = maxTextWidth };
            Native.DrawTextW(screenDc, _title, -1, ref rect,
                (uint)(Native.DT_CALCRECT | Native.DT_NOPREFIX | Native.DT_WORDBREAK));
            titleWidth = rect.Right;
            titleHeight = rect.Bottom;
            Native.SelectObject(screenDc, oldF);
        }

        int bodyWidth = 0, bodyHeight = 0;
        if (!string.IsNullOrEmpty(_body))
        {
            var oldF = Native.SelectObject(screenDc, hBodyFont);
            var rect = new Native.RECT { Right = maxTextWidth };
            Native.DrawTextW(screenDc, _body, -1, ref rect,
                (uint)(Native.DT_CALCRECT | Native.DT_NOPREFIX | Native.DT_WORDBREAK));
            bodyWidth = rect.Right;
            bodyHeight = rect.Bottom;
            Native.SelectObject(screenDc, oldF);
        }

        int gap = (titleHeight > 0 && bodyHeight > 0) ? titleBodyGap : 0;
        int contentWidth = Math.Max(titleWidth, bodyWidth);
        int contentHeight = titleHeight + gap + bodyHeight;
        int tw = contentWidth + 2 * padding;
        int th = contentHeight + 2 * padding;

        // Position above widget, centered, clamped to screen
        Native.GetWindowRect(widgetHwnd, out var widgetRect);
        _posX = widgetRect.Left + (widgetRect.Width - tw) / 2;
        _posY = widgetRect.Top - th - (int)(GapDip * dpiScale);

        // Clamp to monitor work area
        var hMon = Native.MonitorFromWindow(widgetHwnd, Native.MONITOR_DEFAULTTONEAREST);
        var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (Native.GetMonitorInfoW(hMon, ref mi))
        {
            if (_posX < mi.rcWork.Left) _posX = mi.rcWork.Left;
            if (_posX + tw > mi.rcWork.Right) _posX = mi.rcWork.Right - tw;
            if (_posY < mi.rcWork.Top) _posY = mi.rcWork.Top;
        }

        _renderW = tw;
        _renderH = th;

        // Create DIB
        var bmi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = tw, biHeight = -th, biPlanes = 1,
                biBitCount = 32, biCompression = Native.BI_RGB
            }
        };

        var memDc = Native.CreateCompatibleDC(screenDc);
        var hBmp = Native.CreateDIBSection(memDc, ref bmi, Native.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        var oldBmp = Native.SelectObject(memDc, hBmp);

        int pixelCount = tw * th;
        var pxPtr = (uint*)bits;
        for (int i = 0; i < pixelCount; i++)
            pxPtr[i] = 0x00000000;

        // Fill rounded rect background
        uint bgColor = theme.TooltipBackground.ToPremultiplied() | 0xFF000000;
        for (int i = 0; i < pixelCount; i++)
        {
            int x = i % tw, y = i / tw;
            if (GdiRenderer.IsInsideRoundedRect(x, y, 0, 0, tw, th, cr))
                pxPtr[i] = bgColor;
        }

        // Draw text
        Native.SetBkMode(memDc, Native.TRANSPARENT);
        var oldFont = Native.SelectObject(memDc, hTitleFont);

        if (!string.IsNullOrEmpty(_title))
        {
            Native.SetTextColor(memDc, theme.TooltipTitle.ToColorRef());
            var drawRect = new Native.RECT
            {
                Left = padding, Top = padding,
                Right = padding + contentWidth, Bottom = padding + titleHeight
            };
            Native.DrawTextW(memDc, _title, -1, ref drawRect,
                (uint)(Native.DT_NOPREFIX | Native.DT_WORDBREAK));
        }

        if (!string.IsNullOrEmpty(_body))
        {
            Native.SelectObject(memDc, hBodyFont);
            Native.SetTextColor(memDc, theme.TooltipBody.ToColorRef());
            int bodyTop = padding + titleHeight + gap;
            var drawRect = new Native.RECT
            {
                Left = padding, Top = bodyTop,
                Right = padding + contentWidth, Bottom = bodyTop + bodyHeight
            };
            Native.DrawTextW(memDc, _body, -1, ref drawRect,
                (uint)(Native.DT_NOPREFIX | Native.DT_WORDBREAK));
        }

        Native.SelectObject(memDc, oldFont);
        Native.DeleteObject(hTitleFont);
        Native.DeleteObject(hBodyFont);

        // Fix alpha: force opaque inside, draw 1px border
        uint borderColor = theme.TooltipBorder.ToPremultiplied() | 0xFF000000;
        for (int i = 0; i < pixelCount; i++)
        {
            int x = i % tw, y = i / tw;
            if (!GdiRenderer.IsInsideRoundedRect(x, y, 0, 0, tw, th, cr))
                continue;
            if (!GdiRenderer.IsInsideRoundedRect(x, y, 1, 1, tw - 1, th - 1, Math.Max(cr - 1, 0)))
                pxPtr[i] = borderColor;
            else
                pxPtr[i] |= 0xFF000000;
        }

        _memDc = memDc;
        _bitmap = hBmp;
        _oldBitmap = oldBmp;
        Native.ReleaseDC(IntPtr.Zero, screenDc);
    }

    private void ApplyAlpha()
    {
        if (_memDc == IntPtr.Zero) return;

        var screenDc = Native.GetDC(IntPtr.Zero);
        var ptDst = new Native.POINT { X = _posX, Y = _posY };
        var ptSrc = new Native.POINT { X = 0, Y = 0 };
        var size = new Native.SIZE { cx = _renderW, cy = _renderH };
        var blend = new Native.BLENDFUNCTION
        {
            BlendOp = Native.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = _alpha,
            AlphaFormat = Native.AC_SRC_ALPHA
        };

        Native.UpdateLayeredWindow(_hwndTooltip, screenDc, ref ptDst, ref size,
            _memDc, ref ptSrc, 0, ref blend, Native.ULW_ALPHA);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
    }

    private void FreeBitmap()
    {
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero)
                Native.SelectObject(_memDc, _oldBitmap);
            Native.DeleteDC(_memDc);
            _memDc = IntPtr.Zero;
            _oldBitmap = IntPtr.Zero;
        }
        if (_bitmap != IntPtr.Zero)
        {
            Native.DeleteObject(_bitmap);
            _bitmap = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        FreeBitmap();
        if (_hwndTooltip != IntPtr.Zero)
        {
            Native.DestroyWindow(_hwndTooltip);
            _hwndTooltip = IntPtr.Zero;
        }
    }
}
