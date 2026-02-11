namespace TaskbarWidget.Theming;

/// <summary>
/// Color palette for widget rendering, with dark and light presets.
/// </summary>
public sealed class Theme
{
    public Color Text { get; init; }
    public Color HoverBackground { get; init; }
    public Color TooltipBackground { get; init; }
    public Color TooltipBorder { get; init; }
    public Color TooltipTitle { get; init; }
    public Color TooltipBody { get; init; }

    public static readonly Theme Dark = new()
    {
        Text = Color.White,
        HoverBackground = Color.FromArgb(22, 255, 255, 255),
        TooltipBackground = Color.FromRgb(44, 44, 44),
        TooltipBorder = Color.FromRgb(70, 70, 70),
        TooltipTitle = Color.White,
        TooltipBody = Color.FromRgb(200, 200, 200),
    };

    public static readonly Theme Light = new()
    {
        Text = Color.Black,
        HoverBackground = Color.FromArgb(22, 0, 0, 0),
        TooltipBackground = Color.FromRgb(249, 249, 249),
        TooltipBorder = Color.FromRgb(220, 220, 220),
        TooltipTitle = Color.FromRgb(26, 26, 26),
        TooltipBody = Color.FromRgb(64, 64, 64),
    };
}
