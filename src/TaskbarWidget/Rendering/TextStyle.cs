namespace TaskbarWidget.Rendering;

/// <summary>
/// Font configuration for text rendering.
/// </summary>
public sealed class TextStyle
{
    public string FontFamily { get; init; } = "Segoe UI";
    public int FontSizeDip { get; init; } = 13;
    public int FontWeight { get; init; } = 400;
    public Color? Color { get; init; }

    internal static readonly TextStyle Default = new();
}
