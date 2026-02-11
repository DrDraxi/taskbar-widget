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
    /// Logging callback for debug output.
    /// </summary>
    public Action<string>? Log { get; init; }
}
