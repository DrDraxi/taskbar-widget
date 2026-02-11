namespace TaskbarWidget.Rendering;

/// <summary>
/// Recorded drawing command for CanvasContext.
/// All coordinates are in DIP (will be scaled by DPI).
/// </summary>
internal abstract class CanvasCommand;

internal sealed class DrawLineCommand : CanvasCommand
{
    public int X1 { get; init; }
    public int Y1 { get; init; }
    public int X2 { get; init; }
    public int Y2 { get; init; }
    public int Thickness { get; init; }
    public Color Color { get; init; }
}

internal sealed class DrawCircleCommand : CanvasCommand
{
    public int CX { get; init; }
    public int CY { get; init; }
    public int Radius { get; init; }
    public Color Color { get; init; }
}

internal sealed class DrawFilledCircleCommand : CanvasCommand
{
    public int CX { get; init; }
    public int CY { get; init; }
    public int Radius { get; init; }
    public Color Color { get; init; }
}

internal sealed class DrawRectCommand : CanvasCommand
{
    public int X { get; init; }
    public int Y { get; init; }
    public int W { get; init; }
    public int H { get; init; }
    public Color Color { get; init; }
}

internal sealed class DrawFilledRectCommand : CanvasCommand
{
    public int X { get; init; }
    public int Y { get; init; }
    public int W { get; init; }
    public int H { get; init; }
    public Color Color { get; init; }
}
