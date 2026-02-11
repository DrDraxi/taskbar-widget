using TaskbarWidget.Theming;

namespace TaskbarWidget.Rendering;

/// <summary>
/// Context for configuring a panel - the interactive building block.
/// Panels can have click handlers, tooltips, background, blink, and nested content.
/// </summary>
public sealed class PanelContext
{
    internal LayoutNode Node { get; }

    internal PanelContext(LayoutNode node)
    {
        Node = node;
    }

    public void DrawText(string text, TextStyle? style = null)
    {
        var child = new LayoutNode
        {
            Type = LayoutNodeType.Text,
            Text = text,
            TextStyle = style ?? TextStyle.Default
        };
        Node.Children.Add(child);
    }

    public void DrawImage(WidgetImage image, int? widthDip = null, int? heightDip = null)
    {
        var child = new LayoutNode
        {
            Type = LayoutNodeType.Image,
            Image = image,
            RequestedWidthDip = widthDip,
            RequestedHeightDip = heightDip
        };
        Node.Children.Add(child);
    }

    public void Canvas(int widthDip, int heightDip, Action<CanvasContext> build)
    {
        var ctx = new CanvasContext();
        build(ctx);
        var child = new LayoutNode
        {
            Type = LayoutNodeType.Canvas,
            RequestedWidthDip = widthDip,
            RequestedHeightDip = heightDip,
            CanvasCommands = ctx.Commands
        };
        Node.Children.Add(child);
    }

    public void Horizontal(int spacing, Action<HorizontalContext> build)
    {
        var child = new LayoutNode { Type = LayoutNodeType.Horizontal, Spacing = spacing };
        var ctx = new HorizontalContext(child);
        build(ctx);
        Node.Children.Add(child);
    }

    public void Vertical(int spacing, Action<VerticalContext> build)
    {
        var child = new LayoutNode { Type = LayoutNodeType.Vertical, Spacing = spacing };
        var ctx = new VerticalContext(child);
        build(ctx);
        Node.Children.Add(child);
    }

    public void OnClick(Action handler) => Node.OnClick = handler;
    public void OnRightClick(Action handler) => Node.OnRightClick = handler;
    public void OnDoubleClick(Action handler) => Node.OnDoubleClick = handler;

    public void Tooltip(string body) => Node.TooltipBody = body;
    public void Tooltip(string title, string body)
    {
        Node.TooltipTitle = title;
        Node.TooltipBody = body;
    }

    public void Blink(int durationMs = 500)
    {
        Node.Blink = true;
        Node.BlinkDurationMs = durationMs;
    }

    public void Background(Color color) => Node.Background = color;
    public void HoverBackground(Color color) => Node.HoverBackground = color;
    public void CornerRadius(int radius) => Node.CornerRadius = radius;

    public void OnFileDrop(Action<string[]> handler) => Node.OnFileDrop = handler;
    public void OnTextDrop(Action<string> handler) => Node.OnTextDrop = handler;
}
