namespace TaskbarWidget.Rendering;

/// <summary>
/// Context for recording canvas drawing commands (circles, lines, shapes).
/// All coordinates are in DIP, scaled by DPI at render time.
/// </summary>
public sealed class CanvasContext
{
    internal List<CanvasCommand> Commands { get; } = new();

    public void DrawLine(int x1, int y1, int x2, int y2, int thickness, Color color)
    {
        Commands.Add(new DrawLineCommand { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Thickness = thickness, Color = color });
    }

    public void DrawCircle(int x, int y, int radius, Color color)
    {
        Commands.Add(new DrawCircleCommand { CX = x, CY = y, Radius = radius, Color = color });
    }

    public void DrawFilledCircle(int x, int y, int radius, Color color)
    {
        Commands.Add(new DrawFilledCircleCommand { CX = x, CY = y, Radius = radius, Color = color });
    }

    public void DrawRect(int x, int y, int w, int h, Color color)
    {
        Commands.Add(new DrawRectCommand { X = x, Y = y, W = w, H = h, Color = color });
    }

    public void DrawFilledRect(int x, int y, int w, int h, Color color)
    {
        Commands.Add(new DrawFilledRectCommand { X = x, Y = y, W = w, H = h, Color = color });
    }
}
