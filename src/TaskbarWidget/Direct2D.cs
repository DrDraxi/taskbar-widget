using System.Runtime.InteropServices;

namespace TaskbarWidget;

// Hand-rolled vtable interop for the handful of Direct2D / DirectWrite / WIC calls we need.
// No COM wrappers, no reflection: everything is a raw function-pointer call so it compiles
// cleanly with NativeAOT and costs nothing at startup. Vtable slots follow d2d1.h / dwrite.h /
// wincodec.h declaration order.

[StructLayout(LayoutKind.Sequential)] internal struct ColorF { public float R, G, B, A; }
[StructLayout(LayoutKind.Sequential)] internal struct Point2F { public float X, Y; public Point2F(float x, float y) { X = x; Y = y; } }
[StructLayout(LayoutKind.Sequential)] internal struct RectF { public float L, T, R, B; public RectF(float l, float t, float r, float b) { L = l; T = t; R = r; B = b; } }
[StructLayout(LayoutKind.Sequential)] internal struct RoundedRectF { public RectF Rect; public float RX, RY; }
[StructLayout(LayoutKind.Sequential)] internal struct EllipseF { public Point2F Center; public float RX, RY; }
[StructLayout(LayoutKind.Sequential)] internal struct SizeU { public uint W, H; }

[StructLayout(LayoutKind.Sequential)]
internal struct RenderTargetProps { public int Type, Format, AlphaMode; public float DpiX, DpiY; public int Usage, MinLevel; }

[StructLayout(LayoutKind.Sequential)]
internal struct BitmapProps { public int Format, AlphaMode; public float DpiX, DpiY; }

[StructLayout(LayoutKind.Sequential)]
internal struct StrokeStyleProps { public int StartCap, EndCap, DashCap, LineJoin; public float MiterLimit; public int DashStyle; public float DashOffset; }

[StructLayout(LayoutKind.Sequential)]
internal struct TextMetrics
{
    public float Left, Top, Width, WidthIncludingTrailingWhitespace, Height, LayoutWidth, LayoutHeight;
    public uint MaxBidiReorderingDepth, LineCount;
}

internal static unsafe class Com
{
    public static void** Vt(IntPtr p) => *(void***)p;

    public static void Release(ref IntPtr p)
    {
        if (p == IntPtr.Zero) return;
        ((delegate* unmanaged<IntPtr, uint>)Vt(p)[2])(p);
        p = IntPtr.Zero;
    }

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed: 0x{hr:X8}");
    }
}

/// <summary>Process-wide factories. Must only be used from the UI thread.</summary>
internal static unsafe class D2D
{
    const int DXGI_FORMAT_B8G8R8A8_UNORM = 87, D2D1_ALPHA_MODE_PREMULTIPLIED = 1;
    public const int D2DERR_RECREATE_TARGET = unchecked((int)0x8899000C);

    [DllImport("d2d1.dll", ExactSpelling = true)] static extern int D2D1CreateFactory(int type, Guid* riid, void* opts, IntPtr* factory);
    [DllImport("dwrite.dll", ExactSpelling = true)] static extern int DWriteCreateFactory(int type, Guid* riid, IntPtr* factory);
    [DllImport("ole32.dll", ExactSpelling = true)] static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint ctx, Guid* iid, IntPtr* ppv);

    static IntPtr _factory, _dwrite, _wic, _roundStroke;
    static string _family = "Segoe UI";
    static readonly Dictionary<(int, int), IntPtr> _formats = new();

    public static IntPtr Factory { get { Init(); return _factory; } }
    public static IntPtr RoundStroke { get { Init(); return _roundStroke; } }

    static void Init()
    {
        if (_factory != IntPtr.Zero) return;

        var iidD2D = new Guid("06152247-6f50-465a-9245-118bfd3b6007");
        IntPtr f;
        Com.Check(D2D1CreateFactory(0 /* single threaded */, &iidD2D, null, &f), "D2D1CreateFactory");
        _factory = f;

        var iidDW = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
        IntPtr dw;
        Com.Check(DWriteCreateFactory(0 /* shared */, &iidDW, &dw), "DWriteCreateFactory");
        _dwrite = dw;

        // Round-capped stroke style for lines (looks much softer than flat caps at small sizes).
        var sp = new StrokeStyleProps { StartCap = 2, EndCap = 2, DashCap = 2, LineJoin = 2, MiterLimit = 10 };
        IntPtr ss;
        Com.Check(((delegate* unmanaged<IntPtr, StrokeStyleProps*, float*, uint, IntPtr*, int>)Com.Vt(_factory)[11])
            (_factory, &sp, null, 0, &ss), "CreateStrokeStyle");
        _roundStroke = ss;

        _family = HasFont("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
    }

    static bool HasFont(string family)
    {
        IntPtr coll;
        if (((delegate* unmanaged<IntPtr, IntPtr*, int, int>)Com.Vt(_dwrite)[3])(_dwrite, &coll, 0) < 0) return false;
        uint index; int exists = 0;
        fixed (char* name = family)
            ((delegate* unmanaged<IntPtr, char*, uint*, int*, int>)Com.Vt(coll)[5])(coll, name, &index, &exists);
        Com.Release(ref coll);
        return exists != 0;
    }

    public static RenderTargetProps DcTargetProps() => new()
    {
        Type = 1, // software: tiny surfaces, avoids spinning up a GPU device per process
        Format = DXGI_FORMAT_B8G8R8A8_UNORM,
        AlphaMode = D2D1_ALPHA_MODE_PREMULTIPLIED,
        DpiX = 96, DpiY = 96, // we work in physical pixels and scale ourselves
    };

    public static BitmapProps BitmapPropsPremul() => new()
    { Format = DXGI_FORMAT_B8G8R8A8_UNORM, AlphaMode = D2D1_ALPHA_MODE_PREMULTIPLIED, DpiX = 96, DpiY = 96 };

    // ── DirectWrite ─────────────────────────────────────────────────────

    public static IntPtr TextFormat(float sizePx, int weight)
    {
        Init();
        var key = ((int)MathF.Round(sizePx * 4), weight);
        if (_formats.TryGetValue(key, out var fmt)) return fmt;

        IntPtr f;
        fixed (char* fam = _family)
        fixed (char* loc = "en-us")
            Com.Check(((delegate* unmanaged<IntPtr, char*, IntPtr, int, int, int, float, char*, IntPtr*, int>)Com.Vt(_dwrite)[15])
                (_dwrite, fam, IntPtr.Zero, weight, 0, 5, key.Item1 / 4f, loc, &f), "CreateTextFormat");
        _formats[key] = f;
        return f;
    }

    /// <summary>Creates a text layout. Caller owns it and must release it.</summary>
    public static IntPtr CreateLayout(string text, IntPtr format, float maxWidth, bool wrap)
    {
        IntPtr layout;
        fixed (char* s = text)
            Com.Check(((delegate* unmanaged<IntPtr, char*, uint, IntPtr, float, float, IntPtr*, int>)Com.Vt(_dwrite)[18])
                (_dwrite, s, (uint)text.Length, format, maxWidth, 10000f, &layout), "CreateTextLayout");
        // IDWriteTextFormat::SetWordWrapping (slot 5): 0 = wrap, 1 = no wrap
        ((delegate* unmanaged<IntPtr, int, int>)Com.Vt(layout)[5])(layout, wrap ? 0 : 1);
        return layout;
    }

    public static TextMetrics Metrics(IntPtr layout)
    {
        TextMetrics m;
        // IDWriteTextLayout::GetMetrics (slot 60)
        Com.Check(((delegate* unmanaged<IntPtr, TextMetrics*, int>)Com.Vt(layout)[60])(layout, &m), "GetMetrics");
        return m;
    }

    // ── WIC ─────────────────────────────────────────────────────────────

    /// <summary>Decodes an encoded image (PNG etc.) and resamples it to exactly w×h premultiplied BGRA pixels.</summary>
    public static byte[] Decode(byte[] encoded, int w, int h, out int naturalW, out int naturalH)
    {
        if (_wic == IntPtr.Zero)
        {
            var clsid = new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a");
            var iid = new Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
            IntPtr wic;
            Com.Check(CoCreateInstance(&clsid, IntPtr.Zero, 1, &iid, &wic), "CoCreateInstance(WIC)");
            _wic = wic;
        }

        IntPtr stream = 0, decoder = 0, frame = 0, conv = 0, scaler = 0;
        try
        {
            var vt = Com.Vt(_wic);
            Com.Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)vt[14])(_wic, &stream), "CreateStream");
            fixed (byte* data = encoded)
            {
                Com.Check(((delegate* unmanaged<IntPtr, byte*, uint, int>)Com.Vt(stream)[16])(stream, data, (uint)encoded.Length), "InitializeFromMemory");
                Com.Check(((delegate* unmanaged<IntPtr, IntPtr, Guid*, int, IntPtr*, int>)vt[4])(_wic, stream, null, 0, &decoder), "CreateDecoderFromStream");
                Com.Check(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Com.Vt(decoder)[13])(decoder, 0, &frame), "GetFrame");

                uint nw, nh;
                ((delegate* unmanaged<IntPtr, uint*, uint*, int>)Com.Vt(frame)[3])(frame, &nw, &nh);
                naturalW = (int)nw; naturalH = (int)nh;
                if (w <= 0) w = naturalW;
                if (h <= 0) h = naturalH;

                var pbgra = new Guid("6fddc324-4e03-4bfe-b185-3d77768dc910");
                Com.Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)vt[10])(_wic, &conv), "CreateFormatConverter");
                Com.Check(((delegate* unmanaged<IntPtr, IntPtr, Guid*, int, IntPtr, double, int, int>)Com.Vt(conv)[8])
                    (conv, frame, &pbgra, 0, IntPtr.Zero, 0.0, 0), "FormatConverter.Initialize");

                // Fant gives clean box-filtered downscaling (big PNG → 10×26 px can) without aliasing.
                Com.Check(((delegate* unmanaged<IntPtr, IntPtr*, int>)vt[11])(_wic, &scaler), "CreateBitmapScaler");
                Com.Check(((delegate* unmanaged<IntPtr, IntPtr, uint, uint, int, int>)Com.Vt(scaler)[8])
                    (scaler, conv, (uint)w, (uint)h, 3), "BitmapScaler.Initialize");

                var pixels = new byte[w * h * 4];
                fixed (byte* px = pixels)
                    Com.Check(((delegate* unmanaged<IntPtr, void*, uint, uint, byte*, int>)Com.Vt(scaler)[7])
                        (scaler, null, (uint)(w * 4), (uint)pixels.Length, px), "CopyPixels");
                return pixels;
            }
        }
        finally
        {
            Com.Release(ref scaler); Com.Release(ref conv); Com.Release(ref frame);
            Com.Release(ref decoder); Com.Release(ref stream);
        }
    }
}

/// <summary>
/// An offscreen 32-bit premultiplied surface (DIB + memory DC) with a Direct2D DC render target
/// bound to it. The result is pushed to a layered window with UpdateLayeredWindow.
/// All coordinates are physical pixels.
/// </summary>
internal sealed unsafe class Surface : IDisposable
{
    IntPtr _memDc, _dib, _oldBmp, _rt, _brush;
    readonly Dictionary<(WidgetImage, int, int), IntPtr> _bitmaps = new();
    readonly Dictionary<PixelCanvas, (IntPtr Bmp, int W, int H, int Version, long Used)> _canvases = new();
    long _frame;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public IntPtr Hdc => _memDc;

    public void EnsureSize(int w, int h)
    {
        w = Math.Max(1, w); h = Math.Max(1, h);
        if (_memDc != IntPtr.Zero && w == Width && h == Height) return;

        FreeDib();
        _memDc = Native.CreateCompatibleDC(IntPtr.Zero);
        var bih = new Native.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Native.BITMAPINFOHEADER), biWidth = w, biHeight = -h,
            biPlanes = 1, biBitCount = 32,
        };
        void* bits;
        _dib = Native.CreateDIBSection(_memDc, &bih, 0, &bits, IntPtr.Zero, 0);
        _oldBmp = Native.SelectObject(_memDc, _dib);
        Width = w; Height = h;
    }

    void EnsureTarget()
    {
        if (_rt != IntPtr.Zero) return;
        var props = D2D.DcTargetProps();
        IntPtr rt;
        Com.Check(((delegate* unmanaged<IntPtr, RenderTargetProps*, IntPtr*, int>)Com.Vt(D2D.Factory)[16])
            (D2D.Factory, &props, &rt), "CreateDCRenderTarget");
        _rt = rt;

        var c = new ColorF { A = 1 };
        IntPtr brush;
        Com.Check(((delegate* unmanaged<IntPtr, ColorF*, void*, IntPtr*, int>)Com.Vt(_rt)[8])(_rt, &c, null, &brush), "CreateSolidColorBrush");
        _brush = brush;

        ((delegate* unmanaged<IntPtr, int, void>)Com.Vt(_rt)[32])(_rt, 0);  // per-primitive antialiasing
        ((delegate* unmanaged<IntPtr, int, void>)Com.Vt(_rt)[34])(_rt, 2);  // grayscale text AA (needed on transparent surfaces)
    }

    /// <summary>Begins drawing. <paramref name="hitAlpha"/> fills the surface with an almost invisible
    /// alpha so the whole area receives mouse input (fully transparent layered pixels are click-through).</summary>
    public void Begin(bool hitAlpha)
    {
        EnsureTarget();
        var rc = new Native.RECT { Right = Width, Bottom = Height };
        Com.Check(((delegate* unmanaged<IntPtr, IntPtr, Native.RECT*, int>)Com.Vt(_rt)[57])(_rt, _memDc, &rc), "BindDC");
        ((delegate* unmanaged<IntPtr, void>)Com.Vt(_rt)[48])(_rt);
        var clear = new ColorF { A = hitAlpha ? 1f / 255f : 0f };
        ((delegate* unmanaged<IntPtr, ColorF*, void>)Com.Vt(_rt)[47])(_rt, &clear);
    }

    public void End()
    {
        ulong t1, t2;
        int hr = ((delegate* unmanaged<IntPtr, ulong*, ulong*, int>)Com.Vt(_rt)[49])(_rt, &t1, &t2);
        if (hr == D2D.D2DERR_RECREATE_TARGET || hr < 0) { ReleaseTarget(); return; } // recreated lazily next frame

        // Drop GPU copies of canvases that haven't been drawn for a while (e.g. a can that was drunk).
        if (++_frame % 240 == 0)
            foreach (var key in _canvases.Where(kv => _frame - kv.Value.Used > 240).Select(kv => kv.Key).ToList())
            {
                var b = _canvases[key].Bmp; Com.Release(ref b); _canvases.Remove(key);
            }
    }

    IntPtr Brush(Color c)
    {
        var cf = new ColorF { R = c.R / 255f, G = c.G / 255f, B = c.B / 255f, A = c.A / 255f };
        ((delegate* unmanaged<IntPtr, ColorF*, void>)Com.Vt(_brush)[8])(_brush, &cf);
        return _brush;
    }

    public void FillRect(RectF r, Color c)
    {
        if (c.A == 0) return;
        ((delegate* unmanaged<IntPtr, RectF*, IntPtr, void>)Com.Vt(_rt)[17])(_rt, &r, Brush(c));
    }

    public void FillRoundRect(RectF r, float radius, Color c)
    {
        if (c.A == 0) return;
        var rr = new RoundedRectF { Rect = r, RX = radius, RY = radius };
        ((delegate* unmanaged<IntPtr, RoundedRectF*, IntPtr, void>)Com.Vt(_rt)[19])(_rt, &rr, Brush(c));
    }

    public void StrokeRoundRect(RectF r, float radius, Color c, float width)
    {
        if (c.A == 0) return;
        var rr = new RoundedRectF { Rect = r, RX = radius, RY = radius };
        ((delegate* unmanaged<IntPtr, RoundedRectF*, IntPtr, float, IntPtr, void>)Com.Vt(_rt)[18])(_rt, &rr, Brush(c), width, IntPtr.Zero);
    }

    public void FillEllipse(float cx, float cy, float rx, float ry, Color c)
    {
        if (c.A == 0) return;
        var e = new EllipseF { Center = new Point2F(cx, cy), RX = rx, RY = ry };
        ((delegate* unmanaged<IntPtr, EllipseF*, IntPtr, void>)Com.Vt(_rt)[21])(_rt, &e, Brush(c));
    }

    public void StrokeEllipse(float cx, float cy, float rx, float ry, Color c, float width)
    {
        if (c.A == 0) return;
        var e = new EllipseF { Center = new Point2F(cx, cy), RX = rx, RY = ry };
        ((delegate* unmanaged<IntPtr, EllipseF*, IntPtr, float, IntPtr, void>)Com.Vt(_rt)[20])(_rt, &e, Brush(c), width, IntPtr.Zero);
    }

    public void Line(float x1, float y1, float x2, float y2, Color c, float width)
    {
        if (c.A == 0) return;
        ((delegate* unmanaged<IntPtr, Point2F, Point2F, IntPtr, float, IntPtr, void>)Com.Vt(_rt)[15])
            (_rt, new Point2F(x1, y1), new Point2F(x2, y2), Brush(c), width, D2D.RoundStroke);
    }

    public void DrawLayout(IntPtr layout, float x, float y, Color c)
    {
        if (c.A == 0) return;
        // D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT = 4 (emoji render in colour)
        ((delegate* unmanaged<IntPtr, Point2F, IntPtr, IntPtr, int, void>)Com.Vt(_rt)[28])
            (_rt, new Point2F(x, y), layout, Brush(c), 4);
    }

    public void DrawImage(WidgetImage img, RectF dest, float opacity)
    {
        int pw = Math.Max(1, (int)MathF.Round(dest.R - dest.L));
        int ph = Math.Max(1, (int)MathF.Round(dest.B - dest.T));
        var key = (img, pw, ph);
        if (!_bitmaps.TryGetValue(key, out var bmp))
        {
            var pixels = img.GetPixels(pw, ph);
            var size = new SizeU { W = (uint)pw, H = (uint)ph };
            var props = D2D.BitmapPropsPremul();
            fixed (byte* px = pixels)
                Com.Check(((delegate* unmanaged<IntPtr, SizeU, void*, uint, BitmapProps*, IntPtr*, int>)Com.Vt(_rt)[4])
                    (_rt, size, px, (uint)(pw * 4), &props, &bmp), "CreateBitmap");
            _bitmaps[key] = bmp;
        }
        // Pre-scaled to exact pixel size, so draw 1:1 at a whole-pixel origin to stay crisp.
        float x = MathF.Round(dest.L), y = MathF.Round(dest.T);
        var r = new RectF(x, y, x + pw, y + ph);
        ((delegate* unmanaged<IntPtr, IntPtr, RectF*, float, int, RectF*, void>)Com.Vt(_rt)[26])
            (_rt, bmp, &r, Math.Clamp(opacity, 0f, 1f), 1, null);
    }

    public void DrawCanvas(PixelCanvas c, float x, float y, float opacity)
    {
        if (opacity <= 0) return;
        _canvases.TryGetValue(c, out var e);
        fixed (uint* px = c.Pixels)
        {
            if (e.Bmp == IntPtr.Zero || e.W != c.Width || e.H != c.Height)
            {
                if (e.Bmp != IntPtr.Zero) { var old = e.Bmp; Com.Release(ref old); }
                var size = new SizeU { W = (uint)c.Width, H = (uint)c.Height };
                var props = D2D.BitmapPropsPremul();
                IntPtr bmp;
                Com.Check(((delegate* unmanaged<IntPtr, SizeU, void*, uint, BitmapProps*, IntPtr*, int>)Com.Vt(_rt)[4])
                    (_rt, size, px, (uint)(c.Width * 4), &props, &bmp), "CreateBitmap");
                e = (bmp, c.Width, c.Height, c.Version, _frame);
            }
            else if (e.Version != c.Version)
            {
                // ID2D1Bitmap::CopyFromMemory (slot 10)
                ((delegate* unmanaged<IntPtr, void*, void*, uint, int>)Com.Vt(e.Bmp)[10])(e.Bmp, null, px, (uint)(c.Width * 4));
                e.Version = c.Version;
            }
        }
        e.Used = _frame;
        _canvases[c] = e;
        var r = new RectF(x, y, x + c.Width, y + c.Height);
        ((delegate* unmanaged<IntPtr, IntPtr, RectF*, float, int, RectF*, void>)Com.Vt(_rt)[26])
            (_rt, e.Bmp, &r, Math.Clamp(opacity, 0f, 1f), 0 /* nearest: canvas is already pixel-exact */, null);
    }

    void ReleaseTarget()
    {
        foreach (var kv in _bitmaps) { var b = kv.Value; Com.Release(ref b); }
        _bitmaps.Clear();
        foreach (var kv in _canvases) { var b = kv.Value.Bmp; Com.Release(ref b); }
        _canvases.Clear();
        Com.Release(ref _brush);
        Com.Release(ref _rt);
    }

    void FreeDib()
    {
        if (_memDc == IntPtr.Zero) return;
        Native.SelectObject(_memDc, _oldBmp);
        Native.DeleteObject(_dib);
        Native.DeleteDC(_memDc);
        _memDc = _dib = _oldBmp = IntPtr.Zero;
    }

    public void Dispose()
    {
        ReleaseTarget();
        FreeDib();
    }
}
