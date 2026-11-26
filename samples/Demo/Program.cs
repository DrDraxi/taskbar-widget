using TaskbarWidget;

// A counter widget: click to increment, right-click to reset, drag to reorder.
int count = 0;
Widget? widget = null;

widget = new Widget("Demo", ui =>
{
    var t = ui.Theme;
    ui.Custom(18, 18, (g, s) =>
    {
        g.FillCircle(9, 9, 8, t.Accent.WithOpacity(0.25f + 0.35f * s.Hover));
        g.FillCircle(9, 9, 4 + s.Hover, t.Accent);
    }).OnClick(() => { count++; widget!.Invalidate(); })
      .Tooltip("Increment", "Click to add one");

    ui.Text($"{count}", new TextStyle(14, 600));
    ui.Tooltip("Demo widget", $"Count: {count}\nRight-click to reset · drag to reorder");
    ui.OnRightClick(() => { count = 0; widget!.Invalidate(); });
}, new WidgetOptions { Menu = m => m.Item("Reset", () => { count = 0; widget!.Invalidate(); }) });

widget.Show();
Widget.Run();
