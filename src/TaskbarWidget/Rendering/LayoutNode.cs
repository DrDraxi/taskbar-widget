namespace TaskbarWidget.Rendering;

/// <summary>
/// Internal element tree node used by the layout engine.
/// </summary>
internal sealed class LayoutNode
{
    public LayoutNodeType Type { get; init; }
    public List<LayoutNode> Children { get; } = new();

    // Content
    public string? Text { get; set; }
    public TextStyle? TextStyle { get; set; }
    public WidgetImage? Image { get; set; }
    public List<CanvasCommand>? CanvasCommands { get; set; }

    // Sizing (DIP, before DPI scaling)
    public int? RequestedWidthDip { get; set; }
    public int? RequestedHeightDip { get; set; }
    public int Spacing { get; set; }

    // Panel properties
    public Color? Background { get; set; }
    public Color? HoverBackground { get; set; }
    public int CornerRadius { get; set; }
    public Action? OnClick { get; set; }
    public Action? OnRightClick { get; set; }
    public Action? OnDoubleClick { get; set; }
    public string? TooltipTitle { get; set; }
    public string? TooltipBody { get; set; }
    public bool Blink { get; set; }
    public int BlinkDurationMs { get; set; }
    public Action<string[]>? OnFileDrop { get; set; }
    public Action<string>? OnTextDrop { get; set; }

    // Computed layout (physical pixels)
    public int Width { get; set; }
    public int Height { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    // Absolute position relative to root
    public int AbsX { get; set; }
    public int AbsY { get; set; }

    // Runtime state
    public bool IsHovered { get; set; }
}

internal enum LayoutNodeType
{
    Root,
    Horizontal,
    Vertical,
    Panel,
    Text,
    Image,
    Canvas,
}
