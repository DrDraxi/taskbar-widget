using System.Runtime.InteropServices;
using TaskbarWidget.Theming;

namespace TaskbarWidget.Rendering;

/// <summary>
/// Renders a layout tree into a DIBSection and composites via UpdateLayeredWindow.
/// </summary>
internal static class GdiRenderer
{
    /// <summary>
    /// Hover overlay configuration drawn before the content tree.
    /// </summary>
    internal sealed class HoverOverlay
    {
        public int MarginLeft { get; init; }
        public int MarginTop { get; init; }
        public int MarginRight { get; init; }
        public int MarginBottom { get; init; }
        public int CornerRadius { get; init; }
        public Color Color { get; init; }
    }

    /// <summary>
    /// Render the layout tree to the given window via UpdateLayeredWindow.
    /// </summary>
    public static unsafe void Render(IntPtr hwnd, LayoutNode root, double dpiScale, HoverOverlay? hover = null)
    {
        int w = root.Width;
        int h = root.Height;
        if (w <= 0 || h <= 0) return;

        var bmi = new Native.BITMAPINFO
        {
            bmiHeader = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = Native.BI_RGB
            }
        };

        var screenDc = Native.GetDC(IntPtr.Zero);
        var memDc = Native.CreateCompatibleDC(screenDc);
        var hBitmap = Native.CreateDIBSection(memDc, ref bmi, Native.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        var oldBitmap = Native.SelectObject(memDc, hBitmap);

        int pixelCount = w * h;
        var px = (uint*)bits;

        // Alpha=1 on all pixels for full-area hit testing (invisible but mouse-responsive)
        for (int i = 0; i < pixelCount; i++)
            px[i] = 0x01000000;

        // Draw hover overlay (inset rounded rect) before content, matching native taskbar style
        if (hover != null)
        {
            int rectLeft = hover.MarginLeft;
            int rectTop = hover.MarginTop;
            int rectRight = w - hover.MarginRight;
            int rectBottom = h - hover.MarginBottom;
            int cr = (int)(hover.CornerRadius * dpiScale);
            uint hoverPixel = hover.Color.ToPremultiplied();

            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (IsInsideRoundedRect(x, y, rectLeft, rectTop, rectRight, rectBottom, cr))
                        px[y * w + x] = hoverPixel;
        }

        // Walk tree and draw
        DrawNode(px, w, h, root, memDc, dpiScale);

        // Composite
        Native.GetWindowRect(hwnd, out var windowRect);
        var ptDst = new Native.POINT { X = windowRect.Left, Y = windowRect.Top };
        var ptSrc = new Native.POINT { X = 0, Y = 0 };
        var size = new Native.SIZE { cx = w, cy = h };
        var blend = new Native.BLENDFUNCTION
        {
            BlendOp = Native.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = Native.AC_SRC_ALPHA
        };

        Native.UpdateLayeredWindow(hwnd, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, Native.ULW_ALPHA);

        Native.SelectObject(memDc, oldBitmap);
        Native.DeleteObject(hBitmap);
        Native.DeleteDC(memDc);
        Native.ReleaseDC(IntPtr.Zero, screenDc);
    }

    private static unsafe void DrawNode(uint* px, int stride, int height, LayoutNode node, IntPtr hdc, double dpiScale)
    {
        int ax = node.AbsX;
        int ay = node.AbsY;

        switch (node.Type)
        {
            case LayoutNodeType.Panel:
                DrawPanel(px, stride, height, node, hdc, dpiScale);
                break;

            case LayoutNodeType.Text:
                DrawText(px, stride, height, node, hdc, dpiScale);
                break;

            case LayoutNodeType.Image:
                DrawImage(px, stride, height, node);
                break;

            case LayoutNodeType.Canvas:
                DrawCanvas(px, stride, height, node, dpiScale);
                break;
        }

        foreach (var child in node.Children)
            DrawNode(px, stride, height, child, hdc, dpiScale);
    }

    private static unsafe void DrawPanel(uint* px, int stride, int height, LayoutNode node, IntPtr hdc, double dpiScale)
    {
        int ax = node.AbsX, ay = node.AbsY;
        int w = node.Width, h = node.Height;
        int cr = (int)(node.CornerRadius * dpiScale);

        // Background fill
        Color? bg = node.IsHovered ? (node.HoverBackground ?? node.Background) : node.Background;
        if (bg.HasValue && bg.Value.A > 0)
        {
            uint pixel = bg.Value.ToPremultiplied();
            for (int y = 0; y < h; y++)
            {
                int py = ay + y;
                if (py < 0 || py >= height) continue;
                for (int x = 0; x < w; x++)
                {
                    int px2 = ax + x;
                    if (px2 < 0 || px2 >= stride) continue;
                    if (cr > 0 && !IsInsideRoundedRect(x, y, 0, 0, w, h, cr)) continue;
                    BlendPixel(px, py * stride + px2, pixel);
                }
            }
        }
    }

    private static unsafe void DrawText(uint* px, int stride, int height, LayoutNode node, IntPtr hdc, double dpiScale)
    {
        var style = node.TextStyle ?? TextStyle.Default;
        var text = node.Text ?? "";
        if (string.IsNullOrEmpty(text)) return;

        var theme = ThemeDetector.CurrentTheme;
        Color textColor = style.Color ?? theme.Text;

        int fontSize = -(int)(style.FontSizeDip * dpiScale);
        var hFont = Native.CreateFontW(
            fontSize, 0, 0, 0, style.FontWeight,
            0, 0, 0, Native.DEFAULT_CHARSET,
            Native.OUT_DEFAULT_PRECIS, Native.CLIP_DEFAULT_PRECIS,
            4 /* ANTIALIASED_QUALITY */, Native.DEFAULT_PITCH,
            style.FontFamily);

        var oldFont = Native.SelectObject(hdc, hFont);
        Native.SetBkMode(hdc, Native.TRANSPARENT);
        // Draw as white first for coverage detection
        Native.SetTextColor(hdc, Native.RGB(255, 255, 255));

        var rect = new Native.RECT
        {
            Left = node.AbsX,
            Top = node.AbsY,
            Right = node.AbsX + node.Width,
            Bottom = node.AbsY + node.Height
        };
        Native.DrawTextW(hdc, text, -1, ref rect,
            (uint)(Native.DT_CENTER | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_NOPREFIX));

        Native.SelectObject(hdc, oldFont);
        Native.DeleteObject(hFont);

        // Alpha fixup: GDI zeroes alpha on glyph pixels only. Use alpha==0 to
        // identify GDI-drawn pixels vs pre-existing background (which has alpha>0).
        int x0 = Math.Max(0, node.AbsX);
        int y0 = Math.Max(0, node.AbsY);
        int x1 = Math.Min(stride, node.AbsX + node.Width);
        int y1 = Math.Min(height, node.AbsY + node.Height);

        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int idx = y * stride + x;
                uint pixel = px[idx];
                byte existingAlpha = (byte)(pixel >> 24);

                // Only process pixels where GDI zeroed the alpha (glyph pixels)
                if (existingAlpha != 0) continue;

                byte b = (byte)(pixel & 0xFF);
                byte g = (byte)((pixel >> 8) & 0xFF);
                byte r = (byte)((pixel >> 16) & 0xFF);

                byte coverage = Math.Max(r, Math.Max(g, b));
                if (coverage > 0)
                {
                    // Apply text color with coverage as alpha
                    uint a = coverage;
                    uint tr = (uint)textColor.R * a / 255;
                    uint tg = (uint)textColor.G * a / 255;
                    uint tb = (uint)textColor.B * a / 255;
                    px[idx] = (a << 24) | (tr << 16) | (tg << 8) | tb;
                }
            }
        }
    }

    private static unsafe void DrawImage(uint* px, int stride, int height, LayoutNode node)
    {
        if (node.Image == null) return;
        var img = node.Image;

        int dstX = node.AbsX;
        int dstY = node.AbsY;
        int dstW = node.Width;
        int dstH = node.Height;

        for (int y = 0; y < dstH; y++)
        {
            int py = dstY + y;
            if (py < 0 || py >= height) continue;
            for (int x = 0; x < dstW; x++)
            {
                int px2 = dstX + x;
                if (px2 < 0 || px2 >= stride) continue;

                uint srcPixel;
                if (img.Width == dstW && img.Height == dstH)
                {
                    srcPixel = img.Pixels[y * img.Width + x];
                }
                else
                {
                    // Box-filter: average all source pixels that map to this dest pixel
                    int sx0 = x * img.Width / dstW;
                    int sx1 = (x + 1) * img.Width / dstW;
                    int sy0 = y * img.Height / dstH;
                    int sy1 = (y + 1) * img.Height / dstH;
                    if (sx1 == sx0) sx1 = sx0 + 1;
                    if (sy1 == sy0) sy1 = sy0 + 1;
                    sx1 = Math.Min(sx1, img.Width);
                    sy1 = Math.Min(sy1, img.Height);

                    uint sumA = 0, sumR = 0, sumG = 0, sumB = 0;
                    int count = 0;
                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            uint p = img.Pixels[sy * img.Width + sx];
                            sumA += p >> 24;
                            sumR += (p >> 16) & 0xFF;
                            sumG += (p >> 8) & 0xFF;
                            sumB += p & 0xFF;
                            count++;
                        }
                    }
                    uint avgA = sumA / (uint)count;
                    uint avgR = sumR / (uint)count;
                    uint avgG = sumG / (uint)count;
                    uint avgB = sumB / (uint)count;
                    srcPixel = (avgA << 24) | (avgR << 16) | (avgG << 8) | avgB;
                }

                if ((srcPixel >> 24) == 0) continue;
                BlendPixel(px, py * stride + px2, srcPixel);
            }
        }
    }

    private static unsafe void DrawCanvas(uint* px, int stride, int height, LayoutNode node, double dpiScale)
    {
        if (node.CanvasCommands == null) return;

        int ox = node.AbsX;
        int oy = node.AbsY;
        int cw = node.Width;
        int ch = node.Height;

        foreach (var cmd in node.CanvasCommands)
        {
            switch (cmd)
            {
                case DrawLineCommand line:
                    CanvasDrawLine(px, stride, height, ox, oy, cw, ch, dpiScale, line);
                    break;
                case DrawCircleCommand circle:
                    CanvasDrawCircle(px, stride, height, ox, oy, cw, ch, dpiScale, circle, false);
                    break;
                case DrawFilledCircleCommand filled:
                    CanvasDrawFilledCircle(px, stride, height, ox, oy, cw, ch, dpiScale, filled);
                    break;
                case DrawFilledRectCommand fr:
                    CanvasDrawFilledRect(px, stride, height, ox, oy, cw, ch, dpiScale, fr);
                    break;
                case DrawFilledRoundedRectCommand frr:
                    CanvasDrawFilledRoundedRect(px, stride, height, ox, oy, cw, ch, dpiScale, frr);
                    break;
                case DrawRectCommand r:
                    CanvasDrawRect(px, stride, height, ox, oy, cw, ch, dpiScale, r);
                    break;
            }
        }
    }

    #region Canvas Drawing Primitives

    private static unsafe void SetPixelSafe(uint* px, int stride, int height, int x, int y, uint color)
    {
        if (x >= 0 && x < stride && y >= 0 && y < height)
            BlendPixel(px, y * stride + x, color);
    }

    private static uint ApplyAlpha(Color color, double alpha)
    {
        byte a = (byte)Math.Clamp((int)(color.A * alpha), 0, 255);
        if (a == 0) return 0;
        byte r = (byte)(color.R * a / 255);
        byte g = (byte)(color.G * a / 255);
        byte b = (byte)(color.B * a / 255);
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    private static unsafe void CanvasDrawLine(uint* px, int stride, int height, int ox, int oy, int cw, int ch, double dpi, DrawLineCommand cmd)
    {
        int x0 = ox + (int)(cmd.X1 * dpi);
        int y0 = oy + (int)(cmd.Y1 * dpi);
        int x1 = ox + (int)(cmd.X2 * dpi);
        int y1 = oy + (int)(cmd.Y2 * dpi);
        int thick = Math.Max(1, (int)(cmd.Thickness * dpi));
        int halfT = thick / 2;

        // Xiaolin Wu's line algorithm with thickness
        bool steep = Math.Abs(y1 - y0) > Math.Abs(x1 - x0);
        if (steep) { (x0, y0) = (y0, x0); (x1, y1) = (y1, x1); }
        if (x0 > x1) { (x0, x1) = (x1, x0); (y0, y1) = (y1, y0); }

        int dx = x1 - x0;
        int dy = y1 - y0;
        double gradient = dx == 0 ? 1.0 : (double)dy / dx;

        // First endpoint
        double xEnd = x0;
        double yEnd = y0 + gradient * (xEnd - x0);
        double xGap = 1.0 - ((x0 + 0.5) - Math.Floor(x0 + 0.5));
        int xpxl1 = (int)xEnd;
        int ypxl1 = (int)Math.Floor(yEnd);

        void PlotThick(int px2, int py2, double brightness)
        {
            uint c = ApplyAlpha(cmd.Color, brightness);
            if (c == 0) return;
            for (int t = -halfT; t <= halfT; t++)
            {
                if (steep)
                    SetPixelSafe(px, stride, height, py2 + t, px2, c);
                else
                    SetPixelSafe(px, stride, height, px2, py2 + t, c);
            }
        }

        PlotThick(xpxl1, ypxl1, (1.0 - (yEnd - Math.Floor(yEnd))) * xGap);
        PlotThick(xpxl1, ypxl1 + 1, (yEnd - Math.Floor(yEnd)) * xGap);
        double intery = yEnd + gradient;

        // Second endpoint
        xEnd = x1;
        yEnd = y1 + gradient * (xEnd - x1);
        xGap = (x1 + 0.5) - Math.Floor(x1 + 0.5);
        int xpxl2 = (int)xEnd;
        int ypxl2 = (int)Math.Floor(yEnd);
        PlotThick(xpxl2, ypxl2, (1.0 - (yEnd - Math.Floor(yEnd))) * xGap);
        PlotThick(xpxl2, ypxl2 + 1, (yEnd - Math.Floor(yEnd)) * xGap);

        // Main loop
        for (int x = xpxl1 + 1; x < xpxl2; x++)
        {
            int ipart = (int)Math.Floor(intery);
            double fpart = intery - ipart;
            PlotThick(x, ipart, 1.0 - fpart);
            PlotThick(x, ipart + 1, fpart);
            intery += gradient;
        }
    }

    private static unsafe void CanvasDrawCircle(uint* px, int stride, int height, int ox, int oy, int cw, int ch, double dpi, DrawCircleCommand cmd, bool _)
    {
        int cx = ox + (int)(cmd.CX * dpi);
        int cy = oy + (int)(cmd.CY * dpi);
        int r = (int)(cmd.Radius * dpi);
        uint color = cmd.Color.ToPremultiplied();

        // Midpoint circle algorithm
        int x = r, y = 0, d = 1 - r;
        while (x >= y)
        {
            SetPixelSafe(px, stride, height, cx + x, cy + y, color);
            SetPixelSafe(px, stride, height, cx - x, cy + y, color);
            SetPixelSafe(px, stride, height, cx + x, cy - y, color);
            SetPixelSafe(px, stride, height, cx - x, cy - y, color);
            SetPixelSafe(px, stride, height, cx + y, cy + x, color);
            SetPixelSafe(px, stride, height, cx - y, cy + x, color);
            SetPixelSafe(px, stride, height, cx + y, cy - x, color);
            SetPixelSafe(px, stride, height, cx - y, cy - x, color);
            y++;
            if (d <= 0) d += 2 * y + 1;
            else { x--; d += 2 * (y - x) + 1; }
        }
    }

    private static unsafe void CanvasDrawFilledCircle(uint* px, int stride, int height, int ox, int oy, int cw, int ch, double dpi, DrawFilledCircleCommand cmd)
    {
        int cx = ox + (int)(cmd.CX * dpi);
        int cy = oy + (int)(cmd.CY * dpi);
        int r = (int)(cmd.Radius * dpi);
        uint color = cmd.Color.ToPremultiplied();

        for (int y = -r; y <= r; y++)
            for (int x = -r; x <= r; x++)
                if (x * x + y * y <= r * r)
                    SetPixelSafe(px, stride, height, cx + x, cy + y, color);
    }

    private static unsafe void CanvasDrawFilledRect(uint* px, int stride, int height, int ox, int oy, int cw, int ch, double dpi, DrawFilledRectCommand cmd)
    {
        int rx = ox + (int)(cmd.X * dpi);
        int ry = oy + (int)(cmd.Y * dpi);
        int rw = (int)(cmd.W * dpi);
        int rh = (int)(cmd.H * dpi);
        uint color = cmd.Color.ToPremultiplied();

        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
                SetPixelSafe(px, stride, height, rx + x, ry + y, color);
    }

    private static unsafe void CanvasDrawFilledRoundedRect(uint* px, int stride, int height, int ox, int oy, int cw, int ch, double dpi, DrawFilledRoundedRectCommand cmd)
    {
        int rx = ox + (int)(cmd.X * dpi);
        int ry = oy + (int)(cmd.Y * dpi);
        int rw = (int)(cmd.W * dpi);
        int rh = (int)(cmd.H * dpi);
        int cr = (int)(cmd.Radius * dpi);
        uint color = cmd.Color.ToPremultiplied();

        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
                if (IsInsideRoundedRect(x, y, 0, 0, rw, rh, cr))
                    SetPixelSafe(px, stride, height, rx + x, ry + y, color);
    }

    private static unsafe void CanvasDrawRect(uint* px, int stride, int height, int ox, int oy, int cw, int ch, double dpi, DrawRectCommand cmd)
    {
        int rx = ox + (int)(cmd.X * dpi);
        int ry = oy + (int)(cmd.Y * dpi);
        int rw = (int)(cmd.W * dpi);
        int rh = (int)(cmd.H * dpi);
        uint color = cmd.Color.ToPremultiplied();

        for (int x = 0; x < rw; x++)
        {
            SetPixelSafe(px, stride, height, rx + x, ry, color);
            SetPixelSafe(px, stride, height, rx + x, ry + rh - 1, color);
        }
        for (int y = 0; y < rh; y++)
        {
            SetPixelSafe(px, stride, height, rx, ry + y, color);
            SetPixelSafe(px, stride, height, rx + rw - 1, ry + y, color);
        }
    }

    #endregion

    #region Helpers

    internal static bool IsInsideRoundedRect(int x, int y, int left, int top, int right, int bottom, int radius)
    {
        if (x < left || x >= right || y < top || y >= bottom)
            return false;

        int innerLeft = left + radius;
        int innerRight = right - radius;
        int innerTop = top + radius;
        int innerBottom = bottom - radius;

        if ((x >= innerLeft && x < innerRight) || (y >= innerTop && y < innerBottom))
            return true;

        int cx, cy;
        if (x < innerLeft && y < innerTop) { cx = innerLeft; cy = innerTop; }
        else if (x >= innerRight && y < innerTop) { cx = innerRight - 1; cy = innerTop; }
        else if (x < innerLeft && y >= innerBottom) { cx = innerLeft; cy = innerBottom - 1; }
        else { cx = innerRight - 1; cy = innerBottom - 1; }

        int dx = x - cx;
        int dy = y - cy;
        return (dx * dx + dy * dy) <= (radius * radius);
    }

    private static unsafe void BlendPixel(uint* px, int idx, uint src)
    {
        uint srcA = src >> 24;
        if (srcA == 255)
        {
            px[idx] = src;
            return;
        }
        if (srcA == 0) return;

        uint dst = px[idx];
        uint dstA = dst >> 24;
        uint dstR = (dst >> 16) & 0xFF;
        uint dstG = (dst >> 8) & 0xFF;
        uint dstB = dst & 0xFF;

        uint srcR = (src >> 16) & 0xFF;
        uint srcG = (src >> 8) & 0xFF;
        uint srcB = src & 0xFF;

        uint invA = 255 - srcA;
        uint outA = srcA + (dstA * invA / 255);
        uint outR = srcR + (dstR * invA / 255);
        uint outG = srcG + (dstG * invA / 255);
        uint outB = srcB + (dstB * invA / 255);

        px[idx] = (outA << 24) | (outR << 16) | (outG << 8) | outB;
    }

    #endregion
}
