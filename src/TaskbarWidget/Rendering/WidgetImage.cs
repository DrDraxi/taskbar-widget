using System.Drawing;
using System.Drawing.Imaging;

namespace TaskbarWidget.Rendering;

/// <summary>
/// Pre-multiplied ARGB pixel buffer loaded from a PNG file.
/// </summary>
public sealed class WidgetImage
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>
    /// Pre-multiplied ARGB pixels, row-major, top-to-bottom.
    /// Format: 0xAARRGGBB (same as DIBSection with negative height).
    /// </summary>
    public uint[] Pixels { get; }

    private WidgetImage(int width, int height, uint[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    /// <summary>
    /// Load an image from a file path. Supports PNG, BMP, JPG, etc.
    /// Returns pre-multiplied ARGB pixels suitable for direct blitting into a DIBSection.
    /// </summary>
    public static WidgetImage FromFile(string path)
    {
        using var bmp = new Bitmap(path);
        return FromBitmap(bmp);
    }

    /// <summary>
    /// Load an image from a stream.
    /// </summary>
    public static WidgetImage FromStream(Stream stream)
    {
        using var bmp = new Bitmap(stream);
        return FromBitmap(bmp);
    }

    /// <summary>
    /// Load an image from an embedded resource.
    /// </summary>
    public static WidgetImage FromResource(System.Reflection.Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource not found: {resourceName}");
        return FromStream(stream);
    }

    private static unsafe WidgetImage FromBitmap(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var pixels = new uint[w * h];
        var lockBits = bmp.LockBits(
            new Rectangle(0, 0, w, h),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);

        try
        {
            var srcPtr = (byte*)lockBits.Scan0;
            for (int y = 0; y < h; y++)
            {
                var row = srcPtr + y * lockBits.Stride;
                for (int x = 0; x < w; x++)
                {
                    byte b = row[x * 4 + 0];
                    byte g = row[x * 4 + 1];
                    byte r = row[x * 4 + 2];
                    byte a = row[x * 4 + 3];

                    // Pre-multiply
                    if (a == 255)
                    {
                        pixels[y * w + x] = 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
                    }
                    else if (a == 0)
                    {
                        pixels[y * w + x] = 0;
                    }
                    else
                    {
                        uint pr = (uint)r * a / 255;
                        uint pg = (uint)g * a / 255;
                        uint pb = (uint)b * a / 255;
                        pixels[y * w + x] = ((uint)a << 24) | (pr << 16) | (pg << 8) | pb;
                    }
                }
            }
        }
        finally
        {
            bmp.UnlockBits(lockBits);
        }

        return new WidgetImage(w, h, pixels);
    }
}
