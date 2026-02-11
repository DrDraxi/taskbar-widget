namespace TaskbarWidget.Rendering;

/// <summary>
/// Context for building a horizontal layout container.
/// </summary>
public sealed class HorizontalContext
{
    internal LayoutNode Node { get; }

    internal HorizontalContext(LayoutNode node)
    {
        Node = node;
    }

    public void DrawText(string text, TextStyle? style = null)
    {
        Node.Children.Add(new LayoutNode
        {
            Type = LayoutNodeType.Text,
            Text = text,
            TextStyle = style ?? TextStyle.Default
        });
    }

    public void DrawImage(WidgetImage image, int? widthDip = null, int? heightDip = null)
    {
        Node.Children.Add(new LayoutNode
        {
            Type = LayoutNodeType.Image,
            Image = image,
            RequestedWidthDip = widthDip,
            RequestedHeightDip = heightDip
        });
    }

    public void Canvas(int widthDip, int heightDip, Action<CanvasContext> build)
    {
        var ctx = new CanvasContext();
        build(ctx);
        Node.Children.Add(new LayoutNode
        {
            Type = LayoutNodeType.Canvas,
            RequestedWidthDip = widthDip,
            RequestedHeightDip = heightDip,
            CanvasCommands = ctx.Commands
        });
    }

    public void Panel(Action<PanelContext> build)
    {
        var child = new LayoutNode { Type = LayoutNodeType.Panel };
        var ctx = new PanelContext(child);
        build(ctx);
        Node.Children.Add(child);
    }

    public void Panel(int widthDip, int heightDip, Action<PanelContext> build)
    {
        var child = new LayoutNode
        {
            Type = LayoutNodeType.Panel,
            RequestedWidthDip = widthDip,
            RequestedHeightDip = heightDip
        };
        var ctx = new PanelContext(child);
        build(ctx);
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
}
