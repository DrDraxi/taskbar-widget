using TaskbarWidget;
using TaskbarWidget.Rendering;

var widget = new Widget("HelloWorld", render: ctx =>
{
    ctx.DrawText("Hello!", new TextStyle { FontSizeDip = 13, FontWeight = 700 });
    ctx.Tooltip("Hello World", "This is a sample taskbar widget.");
});

widget.Show();
Widget.RunMessageLoop();
