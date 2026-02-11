using TaskbarWidget;
using TaskbarWidget.Rendering;

// Clickable counter widget - demonstrates panels, click handlers, and dynamic resizing.
// Left-click a panel to decrement, right-click to increment.

int count = 3;
Widget? widget = null;

widget = new Widget("ImageWidget", render: ctx =>
{
    ctx.Horizontal(2, h =>
    {
        for (int i = 0; i < count; i++)
        {
            int idx = i; // capture for closure
            h.Panel(12, 20, p =>
            {
                p.Background(Color.FromArgb(180, 0, 120, 212));
                p.HoverBackground(Color.FromArgb(220, 0, 140, 240));
                p.CornerRadius(2);
                p.DrawText($"{idx + 1}", new TextStyle
                {
                    FontSizeDip = 10,
                    FontWeight = 700,
                    Color = Color.White
                });
                p.OnClick(() => { if (count > 1) { count--; widget!.Invalidate(); } });
                p.OnRightClick(() => { count++; widget!.Invalidate(); });
                p.Tooltip($"Item {idx + 1} of {count}\nLeft-click: remove\nRight-click: add");
            });
        }
    });
    ctx.Tooltip("Image Widget", $"Count: {count}");
});

widget.Show();
Widget.RunMessageLoop();
