namespace TaskbarWidget;

/// <summary>
/// Configuration options for a Widget.
/// </summary>
public sealed class WidgetOptions
{
    /// <summary>
    /// Margin between widgets in pixels.
    /// </summary>
    public int Margin { get; init; } = 4;

    /// <summary>
    /// Whether hovering over empty space (the root area) shows the hover overlay.
    /// When false, only child panels with explicit hover backgrounds trigger hover.
    /// Default is true.
    /// </summary>
    public bool RootHover { get; init; } = true;

    /// <summary>
    /// Logging callback for debug output.
    /// </summary>
    public Action<string>? Log { get; init; }
}
