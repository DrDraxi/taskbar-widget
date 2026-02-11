# TaskbarWidget

An immediate-mode Win32 GDI widget toolkit for the Windows 11 taskbar. Create taskbar widgets with text, images, canvas drawing, tooltips, click handlers, and cross-process ordering — all with minimal boilerplate.

## Quick Start

### Text Widget

```csharp
using TaskbarWidget;
using TaskbarWidget.Rendering;

var widget = new Widget("MyWidget", render: ctx =>
{
    ctx.DrawText("Hello!", new TextStyle { FontSizeDip = 13, FontWeight = 700 });
    ctx.Tooltip("My Widget", "This is a taskbar widget.");
});

widget.Show();
Widget.RunMessageLoop();
```

### Clickable Panels

```csharp
int count = 3;
Widget? widget = null;
widget = new Widget("Counter", render: ctx =>
{
    ctx.Horizontal(2, h =>
    {
        for (int i = 0; i < count; i++)
        {
            h.Panel(12, 20, p =>
            {
                p.Background(Color.FromArgb(180, 0, 120, 212));
                p.CornerRadius(2);
                p.DrawText($"{i + 1}");
                p.OnClick(() => { count--; widget!.Invalidate(); });
                p.OnRightClick(() => { count++; widget!.Invalidate(); });
                p.Tooltip($"Item {i + 1}");
            });
        }
    });
});
widget.Show();
Widget.RunMessageLoop();
```

### Canvas Drawing

```csharp
var widget = new Widget("TramTracker", render: ctx =>
{
    ctx.Horizontal(4, h =>
    {
        h.Canvas(36, 20, c =>
        {
            c.DrawLine(6, 10, 30, 10, 2, Color.Gray);
            c.DrawFilledCircle(6, 10, 2, Color.Gray);
            c.DrawFilledCircle(18, 10, 3, Color.Green);
        });
        h.Panel(p =>
        {
            p.Background(Color.Yellow);
            p.CornerRadius(2);
            p.DrawText("12", new TextStyle { Color = Color.Black, FontWeight = 700 });
        });
        h.DrawText("5m", new TextStyle { FontSizeDip = 12, FontWeight = 600 });
    });
    ctx.Tooltip("Line 12", "Arrives in: 5 min");
});
widget.Show();
Widget.RunMessageLoop();
```

## API Reference

### Widget

The main entry point. Creates a window, injects it into the taskbar, and manages the render loop.

| Method | Description |
|--------|-------------|
| `Widget(name, render, options?)` | Create a widget with a render callback |
| `Show()` | Initialize and show the widget in the taskbar |
| `Invalidate()` | Request a re-render (thread-safe) |
| `SetInterval(interval, callback)` | Repeating timer, returns handle |
| `SetTimeout(delay, callback)` | One-shot timer, returns handle |
| `ClearTimer(handle)` | Cancel a timer |
| `OnFileDrop(handler)` | Handle files dropped onto the widget |
| `OnTextDrop(handler)` | Handle text dropped onto the widget |
| `RunMessageLoop()` | Static - run the Win32 message loop |
| `Dispose()` | Clean up resources |

### RenderContext

Passed to the render callback. Build the widget's visual tree by calling methods on this context.

| Method | Description |
|--------|-------------|
| `DrawText(text, style?)` | Draw text |
| `DrawImage(image, w?, h?)` | Draw a pre-loaded image |
| `Canvas(w, h, build)` | Custom pixel-level drawing |
| `Horizontal(spacing, build)` | Horizontal layout container |
| `Vertical(spacing, build)` | Vertical layout container |
| `Panel(build)` | Interactive panel (click, tooltip, etc.) |
| `Panel(w, h, build)` | Fixed-size interactive panel |
| `Tooltip(body)` | Widget-level tooltip |
| `Tooltip(title, body)` | Widget-level tooltip with title |
| `IsDarkMode` | Current system theme |
| `DpiScale` | Current DPI scale factor |

### PanelContext

Panels are the interactive building blocks. Only panels can have click handlers, tooltips, and hover effects.

| Method | Description |
|--------|-------------|
| `DrawText(text, style?)` | Draw text inside the panel |
| `DrawImage(image, w?, h?)` | Draw an image |
| `Canvas(w, h, build)` | Nested canvas drawing |
| `Horizontal/Vertical(spacing, build)` | Nested layouts |
| `OnClick(action)` | Left-click handler |
| `OnRightClick(action)` | Right-click handler |
| `OnDoubleClick(action)` | Double-click handler |
| `Tooltip(body)` / `Tooltip(title, body)` | Per-panel tooltip |
| `Background(color)` | Panel background color |
| `HoverBackground(color)` | Background on hover |
| `CornerRadius(radius)` | Rounded corners (DIP) |
| `Blink(durationMs?)` | Blink animation |

### CanvasContext

For pixel-level drawing. All coordinates are in DIP, scaled by DPI at render time.

| Method | Description |
|--------|-------------|
| `DrawLine(x1, y1, x2, y2, thickness, color)` | Line with thickness |
| `DrawCircle(x, y, radius, color)` | Circle outline |
| `DrawFilledCircle(x, y, radius, color)` | Filled circle |
| `DrawRect(x, y, w, h, color)` | Rectangle outline |
| `DrawFilledRect(x, y, w, h, color)` | Filled rectangle |

### TextStyle

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `FontFamily` | string | "Segoe UI" | Font name |
| `FontSizeDip` | int | 12 | Font size in DIP |
| `FontWeight` | int | 400 | Weight (400=normal, 700=bold) |
| `Color` | Color? | theme text | Text color |

### WidgetImage

Load images for use with `DrawImage()`.

```csharp
var img = WidgetImage.FromFile("icon.png");
var img = WidgetImage.FromStream(stream);
var img = WidgetImage.FromResource(assembly, "MyApp.Resources.icon.png");
```

### Color

Simple RGBA color struct with presets: `White`, `Black`, `Gray`, `Transparent`, `Red`, `Green`, `Blue`, `Yellow`.

```csharp
var c = Color.FromRgb(255, 128, 0);
var c = Color.FromArgb(128, 255, 255, 255);
```

## Widget Ordering

Widgets from separate processes coordinate positioning via a shared order file at `%LOCALAPPDATA%\TaskbarWidget\widget-order.json`. Widgets are ordered right-to-left (index 0 = rightmost).

## Theme Detection

Widgets automatically detect the system theme (dark/light) via `ShouldSystemUseDarkMode()` and update on `WM_SETTINGCHANGE`. Use `ctx.IsDarkMode` in your render callback for conditional styling.

## Timer Helpers

```csharp
// Refresh data every hour
widget.SetInterval(TimeSpan.FromHours(1), () =>
{
    FetchData();
    widget.Invalidate();
});

// One-shot delay
widget.SetTimeout(TimeSpan.FromSeconds(5), () => { /* ... */ });
```

## Project Structure

```
taskbar-widget/
├── src/TaskbarWidget/
│   ├── Widget.cs                    # Main entry point
│   ├── WidgetOptions.cs             # Configuration
│   ├── Color.cs                     # RGBA color struct
│   ├── Native.cs                    # Win32 P/Invoke
│   ├── TaskbarInjectionHelper.cs    # Window creation & injection
│   ├── TaskbarSlotFinder.cs         # Collision detection
│   ├── Rendering/                   # Layout, GDI, contexts
│   ├── Theming/                     # Dark/light theme
│   ├── Interaction/                 # Mouse, tooltips, drop
│   ├── Ordering/                    # Cross-process ordering
│   └── Timing/                      # SetInterval/SetTimeout
├── samples/
│   ├── HelloWorld/                  # Minimal text widget
│   ├── ImageWidget/                 # Clickable panels
│   └── CanvasWidget/                # Canvas drawing
└── TaskbarWidget.sln
```

## Building

```bash
dotnet build lib/taskbar-widget/TaskbarWidget.sln -p:Platform=x64
```

## Running Samples

```bash
dotnet run --project samples/HelloWorld -p:Platform=x64
dotnet run --project samples/ImageWidget -p:Platform=x64
dotnet run --project samples/CanvasWidget -p:Platform=x64
```

Run multiple samples simultaneously — they stack without overlapping.

## Requirements

- Windows 10 version 1809 (build 17763) or later
- .NET 8.0

## License

MIT
