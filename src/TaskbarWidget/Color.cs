namespace TaskbarWidget;

/// <summary>
/// Simple RGBA color struct for widget rendering.
/// </summary>
public readonly struct Color : IEquatable<Color>
{
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }
    public byte A { get; }

    public Color(byte r, byte g, byte b, byte a = 255)
    {
        R = r; G = g; B = b; A = a;
    }

    public static Color FromRgb(byte r, byte g, byte b) => new(r, g, b, 255);
    public static Color FromArgb(byte a, byte r, byte g, byte b) => new(r, g, b, a);

    /// <summary>
    /// Returns pre-multiplied ARGB uint for direct pixel buffer writes (BGRA in memory).
    /// </summary>
    public uint ToPremultiplied()
    {
        if (A == 255)
            return 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
        if (A == 0)
            return 0;
        uint a = A;
        uint r = (uint)R * a / 255;
        uint g = (uint)G * a / 255;
        uint b = (uint)B * a / 255;
        return (a << 24) | (r << 16) | (g << 8) | b;
    }

    /// <summary>
    /// Returns COLORREF (0x00BBGGRR) for GDI SetTextColor.
    /// </summary>
    public uint ToColorRef() => (uint)(R | (G << 8) | (B << 16));

    // Common presets
    public static readonly Color White = new(255, 255, 255);
    public static readonly Color Black = new(0, 0, 0);
    public static readonly Color Gray = new(128, 128, 128);
    public static readonly Color Transparent = new(0, 0, 0, 0);
    public static readonly Color Red = new(255, 0, 0);
    public static readonly Color Green = new(0, 255, 0);
    public static readonly Color Blue = new(0, 0, 255);
    public static readonly Color Yellow = new(255, 255, 0);

    public bool Equals(Color other) => R == other.R && G == other.G && B == other.B && A == other.A;
    public override bool Equals(object? obj) => obj is Color c && Equals(c);
    public override int GetHashCode() => HashCode.Combine(R, G, B, A);
    public static bool operator ==(Color left, Color right) => left.Equals(right);
    public static bool operator !=(Color left, Color right) => !left.Equals(right);
    public override string ToString() => $"Color({R}, {G}, {B}, {A})";
}
