using System.Reflection;
using Microsoft.Win32;

namespace TaskbarWidget;

/// <summary>A straight-alpha ARGB colour.</summary>
public readonly record struct Color(byte A, byte R, byte G, byte B)
{
    public static Color Rgb(byte r, byte g, byte b) => new(255, r, g, b);
    public static Color Argb(byte a, byte r, byte g, byte b) => new(a, r, g, b);

    /// <summary>Parses "#RRGGBB" or "#AARRGGBB".</summary>
    public static Color Hex(string hex)
    {
        hex = hex.TrimStart('#');
        uint v = Convert.ToUInt32(hex, 16);
        return hex.Length <= 6
            ? new Color(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new Color((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>Same colour with its alpha multiplied by <paramref name="opacity"/> (0–1).</summary>
    public Color WithOpacity(float opacity) => this with { A = (byte)Math.Clamp(MathF.Round(A * opacity), 0, 255) };

    public static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        byte L(byte x, byte y) => (byte)MathF.Round(x + (y - x) * t);
        return new Color(L(a.A, b.A), L(a.R, b.R), L(a.G, b.G), L(a.B, b.B));
    }

    public static readonly Color Transparent = new(0, 0, 0, 0);
    public static readonly Color White = Rgb(255, 255, 255);
    public static readonly Color Black = Rgb(0, 0, 0);
}

/// <summary>Font settings for text. Sizes are in device-independent pixels.</summary>
public sealed record TextStyle(float Size = 12f, int Weight = 400)
{
    public static readonly TextStyle Body = new(12f);
    public static readonly TextStyle Caption = new(11f);
    public static readonly TextStyle Strong = new(12f, 600);
}

/// <summary>Windows 11 taskbar-matched palette. Follows the *system* (taskbar) light/dark setting.</summary>
public sealed class Theme
{
    public required bool IsDark { get; init; }
    public required Color Text { get; init; }
    public required Color TextSecondary { get; init; }
    public required Color TextTertiary { get; init; }
    public required Color HoverFill { get; init; }
    public required Color PressedFill { get; init; }
    public required Color HoverStroke { get; init; }
    public required Color TooltipBackground { get; init; }
    public required Color TooltipBorder { get; init; }
    public required Color Success { get; init; }
    public required Color Caution { get; init; }
    public required Color Critical { get; init; }
    public required Color Accent { get; init; }

    static Theme? _current;
    public static Theme Current => _current ??= Detect();

    internal static void Refresh() => _current = Detect();

    static Theme Detect()
    {
        bool dark = true;
        var accent = Color.Rgb(0, 120, 212);
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (k?.GetValue("SystemUsesLightTheme") is int light) dark = light == 0;
            using var dwm = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (dwm?.GetValue("AccentColor") is int abgr)
                accent = Color.Rgb((byte)abgr, (byte)(abgr >> 8), (byte)(abgr >> 16));
        }
        catch { /* keep defaults */ }

        return dark
            ? new Theme
            {
                IsDark = true,
                Text = Color.White,
                TextSecondary = Color.White.WithOpacity(0.70f),
                TextTertiary = Color.White.WithOpacity(0.42f),
                HoverFill = Color.White.WithOpacity(0.075f),
                PressedFill = Color.White.WithOpacity(0.045f),
                HoverStroke = Color.White.WithOpacity(0.06f),
                TooltipBackground = Color.Rgb(0x2C, 0x2C, 0x2C),
                TooltipBorder = Color.White.WithOpacity(0.09f),
                Success = Color.Hex("#6CCB5F"),
                Caution = Color.Hex("#FCE100"),
                Critical = Color.Hex("#FF99A4"),
                Accent = accent,
            }
            : new Theme
            {
                IsDark = false,
                Text = Color.Hex("#1B1B1B"),
                TextSecondary = Color.Black.WithOpacity(0.62f),
                TextTertiary = Color.Black.WithOpacity(0.40f),
                HoverFill = Color.White.WithOpacity(0.62f),
                PressedFill = Color.White.WithOpacity(0.38f),
                HoverStroke = Color.Black.WithOpacity(0.06f),
                TooltipBackground = Color.Hex("#F9F9F9"),
                TooltipBorder = Color.Black.WithOpacity(0.08f),
                Success = Color.Hex("#0F7B0F"),
                Caution = Color.Hex("#9D5D00"),
                Critical = Color.Hex("#C42B1C"),
                Accent = accent,
            };
    }
}

/// <summary>An encoded image (PNG/JPEG/…) that is decoded and resampled on demand at the exact pixel size drawn.</summary>
public sealed class WidgetImage
{
    readonly byte[] _encoded;
    readonly Dictionary<(int, int), byte[]> _pixels = new();

    WidgetImage(byte[] encoded) => _encoded = encoded;

    public static WidgetImage FromBytes(byte[] encoded) => new(encoded);
    public static WidgetImage FromFile(string path) => new(File.ReadAllBytes(path));

    public static WidgetImage FromResource(Assembly assembly, string resourceName)
    {
        using var s = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource '{resourceName}' not found");
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        return new WidgetImage(bytes);
    }

    (int, int)? _natural;

    /// <summary>The image's own size in pixels.</summary>
    public (int Width, int Height) NaturalSize
    {
        get
        {
            if (_natural == null) { D2D.Decode(_encoded, 1, 1, out var w, out var h); _natural = (w, h); }
            return _natural.Value;
        }
    }

    /// <summary>
    /// Decodes and resamples the image to exactly <paramref name="w"/>×<paramref name="h"/> premultiplied
    /// BGRA pixels (4 bytes per pixel, row-major). Results are cached. Must be called on the UI thread.
    /// </summary>
    public byte[] GetPixels(int w, int h)
    {
        if (!_pixels.TryGetValue((w, h), out var px))
            _pixels[(w, h)] = px = D2D.Decode(_encoded, w, h, out _, out _);
        return px;
    }
}

/// <summary>
/// A raw pixel buffer you render yourself (e.g. a software 3D renderer) and draw with
/// <see cref="Graphics.Pixels"/>. Pixels are premultiplied 0xAARRGGBB, row-major. Call
/// <see cref="Commit"/> after changing them; the GPU-side copy is only refreshed then.
/// </summary>
public sealed class PixelCanvas
{
    public PixelCanvas(int width, int height) => Resize(width, height);

    public int Width { get; private set; }
    public int Height { get; private set; }
    public uint[] Pixels { get; private set; } = Array.Empty<uint>();
    internal int Version { get; private set; }

    /// <summary>Resizes (clearing the contents) if the size differs.</summary>
    public void Resize(int width, int height)
    {
        width = Math.Max(1, width); height = Math.Max(1, height);
        if (width == Width && height == Height) return;
        Width = width; Height = height;
        Pixels = new uint[width * height];
        Version++;
    }

    public void Clear() { Array.Clear(Pixels); Version++; }
    public void Commit() => Version++;
}

/// <summary>Interaction state handed to custom draw callbacks. Values animate smoothly between 0 and 1.</summary>
public readonly record struct ElementState(float Hover, float Pressed);

/// <summary>
/// Drawing API for custom elements. All coordinates are device-independent pixels relative to the
/// element's top-left corner; scaling to the taskbar's DPI is handled for you.
/// </summary>
public sealed class Graphics
{
    internal Surface Surface = null!;
    internal float OriginX, OriginY;
    internal Action? FrameRequested;

    static readonly long ClockStart = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>Monotonic animation clock in seconds (same clock as <see cref="Time"/>).</summary>
    public static double Now => System.Diagnostics.Stopwatch.GetElapsedTime(ClockStart).TotalSeconds;

    /// <summary>Current animation time in seconds.</summary>
    public double Time => Now;

    /// <summary>Ask for another frame (~60 fps). Call it from a draw callback while something is animating;
    /// when nobody asks, the widget stops repainting (zero idle CPU).</summary>
    public void RequestFrame() => FrameRequested?.Invoke();

    internal Graphics(float scale, Theme theme) { Scale = scale; Theme = theme; }

    /// <summary>DPI scale (1.0 = 96 DPI).</summary>
    public float Scale { get; }
    public Theme Theme { get; }

    float X(float x) => OriginX + x * Scale;
    float Y(float y) => OriginY + y * Scale;

    public void FillRect(float x, float y, float w, float h, Color c) =>
        Surface.FillRect(new RectF(X(x), Y(y), X(x + w), Y(y + h)), c);

    public void FillRoundRect(float x, float y, float w, float h, float radius, Color c) =>
        Surface.FillRoundRect(new RectF(X(x), Y(y), X(x + w), Y(y + h)), radius * Scale, c);

    public void StrokeRoundRect(float x, float y, float w, float h, float radius, Color c, float thickness = 1f)
    {
        // Inset by half the stroke so the outline stays inside the box.
        float t = thickness * Scale, i = t / 2;
        Surface.StrokeRoundRect(new RectF(X(x) + i, Y(y) + i, X(x + w) - i, Y(y + h) - i), radius * Scale, c, t);
    }

    public void FillEllipse(float cx, float cy, float rx, float ry, Color c) =>
        Surface.FillEllipse(X(cx), Y(cy), rx * Scale, ry * Scale, c);

    /// <summary>Draws a <see cref="PixelCanvas"/> 1:1 in device pixels with its top-left at (x, y) DIPs.</summary>
    public void Pixels(PixelCanvas canvas, float x, float y, float opacity = 1f) =>
        Surface.DrawCanvas(canvas, MathF.Round(X(x)), MathF.Round(Y(y)), opacity);

    public void FillCircle(float cx, float cy, float r, Color c) =>
        Surface.FillEllipse(X(cx), Y(cy), r * Scale, r * Scale, c);

    public void StrokeCircle(float cx, float cy, float r, Color c, float thickness = 1f) =>
        Surface.StrokeEllipse(X(cx), Y(cy), r * Scale, r * Scale, c, thickness * Scale);

    public void Line(float x1, float y1, float x2, float y2, Color c, float thickness = 1f) =>
        Surface.Line(X(x1), Y(y1), X(x2), Y(y2), c, thickness * Scale);

    public void Image(WidgetImage img, float x, float y, float w, float h, float opacity = 1f) =>
        Surface.DrawImage(img, new RectF(X(x), Y(y), X(x + w), Y(y + h)), opacity);

    /// <summary>Draws single-line text with its layout box's top-left at (x, y).</summary>
    public void Text(string text, float x, float y, TextStyle style, Color? color = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        var layout = D2D.CreateLayout(text, D2D.TextFormat(style.Size * Scale, style.Weight), 100000f, wrap: false);
        try { Surface.DrawLayout(layout, MathF.Round(X(x)), MathF.Round(Y(y)), color ?? Theme.Text); }
        finally { Com.Release(ref layout); }
    }

    /// <summary>Measures single-line text in DIPs.</summary>
    public (float Width, float Height) MeasureText(string text, TextStyle style) => Measure(text, style, Scale);

    internal static (float Width, float Height) Measure(string text, TextStyle style, float scale)
    {
        var layout = D2D.CreateLayout(string.IsNullOrEmpty(text) ? " " : text,
            D2D.TextFormat(style.Size * scale, style.Weight), 100000f, wrap: false);
        try
        {
            var m = D2D.Metrics(layout);
            return (string.IsNullOrEmpty(text) ? 0 : m.WidthIncludingTrailingWhitespace / scale, m.Height / scale);
        }
        finally { Com.Release(ref layout); }
    }
}
