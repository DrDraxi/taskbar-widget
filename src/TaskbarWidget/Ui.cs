namespace TaskbarWidget;

/// <summary>
/// Builder passed to a widget's build callback. Elements are laid out left-to-right and centred
/// vertically in the taskbar. The callback runs again on every <see cref="Widget.Invalidate"/>,
/// so just describe what the widget should look like *now*.
/// </summary>
public sealed class Ui
{
    internal readonly List<Element> Elements = new();
    internal string? TooltipTitle, TooltipBody;
    internal Action? Click, RightClick;

    internal Ui(float scale, Theme theme) { Scale = scale; Theme = theme; }

    public float Scale { get; }
    public Theme Theme { get; }

    /// <summary>Space between consecutive elements, in DIPs. Default 6.</summary>
    public float Spacing { get; set; } = 6f;

    /// <summary>Adds explicit horizontal space (replaces the default spacing on both sides).</summary>
    public void Gap(float width) => Elements.Add(new Element(width, 0, null) { IsSpacer = true });

    /// <summary>Single-line text.</summary>
    public Element Text(string text, TextStyle? style = null, Color? color = null)
    {
        style ??= TextStyle.Body;
        var (w, h) = Graphics.Measure(text, style, Scale);
        return Add(new Element(w, h, (g, _) => g.Text(text, 0, 0, style, color)));
    }

    /// <summary>An image drawn at the given size (DIPs). Resampled with high quality at the exact pixel size.</summary>
    public Element Image(WidgetImage image, float width, float height, float opacity = 1f) =>
        Add(new Element(width, height, (g, _) => g.Image(image, 0, 0, width, height, opacity)));

    /// <summary>A custom-drawn element of a fixed size.</summary>
    public Element Custom(float width, float height, Action<Graphics, ElementState> draw) =>
        Add(new Element(width, height, draw));

    /// <summary>Measures single-line text in DIPs (useful for sizing <see cref="Custom"/> elements).</summary>
    public (float Width, float Height) MeasureText(string text, TextStyle style) => Graphics.Measure(text, style, Scale);

    /// <summary>Tooltip for the whole widget (elements can override it with their own).</summary>
    public void Tooltip(string title, string? body = null) { TooltipTitle = title; TooltipBody = body; }

    /// <summary>Left-click anywhere not handled by an element.</summary>
    public void OnClick(Action handler) => Click = handler;

    /// <summary>Right-click anywhere not handled by an element. Shift+right-click always opens the widget menu.</summary>
    public void OnRightClick(Action handler) => RightClick = handler;

    Element Add(Element e) { Elements.Add(e); return e; }
}

/// <summary>A laid-out piece of a widget. Chain handlers onto it.</summary>
public sealed class Element
{
    internal readonly float Width, Height;
    internal readonly Action<Graphics, ElementState>? Draw;
    internal bool IsSpacer;
    internal Action? Click, RightClick;
    internal string? TipTitle, TipBody;
    internal float X; // assigned by layout (DIPs from content origin)

    internal Element(float w, float h, Action<Graphics, ElementState>? draw) { Width = w; Height = h; Draw = draw; }

    internal bool Interactive => Click != null || RightClick != null || TipTitle != null || TipBody != null;

    public Element OnClick(Action handler) { Click = handler; return this; }
    public Element OnRightClick(Action handler) { RightClick = handler; return this; }
    public Element Tooltip(string title, string? body = null) { TipTitle = title; TipBody = body; return this; }
}

/// <summary>Builder for the widget's right-click menu (native Win32 menu, follows dark mode).</summary>
public sealed class MenuBuilder
{
    internal readonly List<(string? Text, Action? Action, bool Checked, bool Enabled)> Items = new();

    public MenuBuilder Item(string text, Action action, bool enabled = true) { Items.Add((text, action, false, enabled)); return this; }
    public MenuBuilder Check(string text, bool isChecked, Action action) { Items.Add((text, action, isChecked, true)); return this; }
    public MenuBuilder Separator() { Items.Add((null, null, false, true)); return this; }
}
