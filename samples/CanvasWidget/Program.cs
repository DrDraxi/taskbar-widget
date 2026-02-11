using TaskbarWidget;
using TaskbarWidget.Rendering;

// Tram tracker style widget - demonstrates canvas drawing, line badges, and text.

int vehicleX = 18;
var statusColor = Color.FromRgb(76, 175, 80); // green
var lineColor = Color.Yellow;

var widget = new Widget("CanvasWidget", render: ctx =>
{
    ctx.Horizontal(4, h =>
    {
        // Route visualization canvas
        h.Canvas(36, 20, c =>
        {
            // Track line (gray background)
            c.DrawLine(6, 10, 30, 10, 2, Color.Gray);
            // Progress line (colored)
            c.DrawLine(6, 10, vehicleX, 10, 2, statusColor);
            // Stop circles
            c.DrawFilledCircle(6, 10, 2, Color.Gray);
            c.DrawFilledCircle(18, 10, 2, Color.Gray);
            c.DrawFilledCircle(30, 10, 2, Color.White);
            // Vehicle position
            c.DrawFilledCircle(vehicleX, 10, 3, statusColor);
        });

        // Line badge
        h.Panel(p =>
        {
            p.Background(lineColor);
            p.CornerRadius(2);
            p.DrawText("12", new TextStyle
            {
                FontSizeDip = 10,
                FontWeight = 700,
                Color = Color.Black
            });
        });

        // Arrival time
        h.DrawText("5m", new TextStyle { FontSizeDip = 12, FontWeight = 600 });
    });

    ctx.Tooltip("Line 12 to Lehovec", "Arrives in: 5 min\nNext stop: Florenc\nDelay: +2 min");
});

widget.Show();
Widget.RunMessageLoop();
