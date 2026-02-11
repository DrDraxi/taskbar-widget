using TaskbarWidget.Theming;

namespace TaskbarWidget.Rendering;

/// <summary>
/// Top-level render context passed to the widget's render callback.
/// Each method call appends a LayoutNode to the root.
/// </summary>
public sealed class RenderContext
{
    internal LayoutNode Root { get; }

    // Widget-level tooltip
    internal string? TooltipTitle { get; private set; }
    internal string? TooltipBody { get; private set; }

    public bool IsDarkMode => ThemeDetector.IsDarkMode;
    public double DpiScale { get; }

    internal RenderContext(LayoutNode root, double dpiScale)
    {
        Root = root;
        DpiScale = dpiScale;
    }

    public void DrawText(string text, TextStyle? style = null)
    {
        Root.Children.Add(new LayoutNode
        {
            Type = LayoutNodeType.Text,
            Text = text,
            TextStyle = style ?? TextStyle.Default
        });
    }

    public void DrawImage(WidgetImage image, int? widthDip = null, int? heightDip = null)
    {
        Root.Children.Add(new LayoutNode
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
        Root.Children.Add(new LayoutNode
        {
            Type = LayoutNodeType.Canvas,
            RequestedWidthDip = widthDip,
            RequestedHeightDip = heightDip,
            CanvasCommands = ctx.Commands
        });
    }

    public void Horizontal(int spacing, Action<HorizontalContext> build)
    {
        var child = new LayoutNode { Type = LayoutNodeType.Horizontal, Spacing = spacing };
        var ctx = new HorizontalContext(child);
        build(ctx);
        Root.Children.Add(child);
    }

    public void Vertical(int spacing, Action<VerticalContext> build)
    {
        var child = new LayoutNode { Type = LayoutNodeType.Vertical, Spacing = spacing };
        var ctx = new VerticalContext(child);
        build(ctx);
        Root.Children.Add(child);
    }

    public void Panel(Action<PanelContext> build)
    {
        var child = new LayoutNode { Type = LayoutNodeType.Panel };
        var ctx = new PanelContext(child);
        build(ctx);
        Root.Children.Add(child);
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
        Root.Children.Add(child);
    }

    /// <summary>
    /// Set widget-level tooltip (shown when hovering the widget and no panel tooltip is active).
    /// </summary>
    public void Tooltip(string body)
    {
        TooltipTitle = null;
        TooltipBody = body;
    }

    /// <summary>
    /// Set widget-level tooltip with title and body.
    /// </summary>
    public void Tooltip(string title, string body)
    {
        TooltipTitle = title;
        TooltipBody = body;
    }
}
