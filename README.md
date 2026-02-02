# TaskbarWidget for WinUI 3

A library for injecting custom WinUI 3 widgets into the Windows 11 taskbar.

![Multiple widgets stacked in taskbar](docs/widgets.png)

## Features

- Inject custom XAML content directly into the Windows taskbar
- Automatic collision detection - multiple widgets stack without overlapping
- DPI-aware positioning
- Simple API for show/hide/update operations

## Quick Start

```csharp
using TaskbarWidget;

// Create the helper with configuration
var helper = new TaskbarInjectionHelper(new TaskbarInjectionConfig
{
    ClassName = "MyWidgetClass",  // Unique class name for your widget
    WindowTitle = "MyWidget",
    WidthDip = 100,               // Width in device-independent pixels
    DeferInjection = true         // Required for WinUI XAML content
});

// Initialize (creates the host window)
var result = helper.Initialize();
if (!result.Success) return;

// Set up your XAML content using DesktopWindowXamlSource
var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(result.WindowHandle);
var xamlSource = new DesktopWindowXamlSource();
xamlSource.Initialize(windowId);
xamlSource.Content = new TextBlock { Text = "Hello!" };

// Inject into taskbar and show
helper.Inject();
helper.Show();
```

## Project Structure

```
taskbar-widget-winui/
├── src/
│   └── TaskbarWidget/          # Core library
│       ├── Native.cs           # Win32 P/Invoke declarations
│       ├── TaskbarSlotFinder.cs    # Collision detection logic
│       └── TaskbarInjectionHelper.cs # Main API
├── samples/
│   └── HelloWorld/             # Simple example widget
└── TaskbarWidget.sln
```

## Building

```bash
dotnet build
```

## Running the Sample

```bash
dotnet run --project samples/HelloWorld/HelloWorld.csproj
```

You can run multiple instances - they will automatically stack without overlapping.

## API Reference

### TaskbarInjectionConfig

| Property | Type | Description |
|----------|------|-------------|
| `ClassName` | string | Window class name (must be unique per widget type) |
| `WindowTitle` | string | Window title for the host window |
| `WidthDip` | int | Width in device-independent pixels (default: 100) |
| `Margin` | int | Margin between widgets in pixels (default: 4) |
| `DeferInjection` | bool | If true, call `Inject()` after setting up XAML (default: false) |

### TaskbarInjectionHelper

| Method | Description |
|--------|-------------|
| `Initialize()` | Creates the host window, returns `TaskbarInjectionResult` |
| `Inject()` | Injects window into taskbar (call after XAML setup if `DeferInjection=true`) |
| `UpdatePosition()` | Recalculates widget position |
| `Show()` | Shows the widget |
| `Hide()` | Hides the widget |
| `Dispose()` | Cleans up resources |

### TaskbarSlotFinder

For advanced scenarios, use `TaskbarSlotFinder` directly to query available slots:

```csharp
var finder = new TaskbarSlotFinder();
var slot = finder.FindSlot(widgetWidth: 100, ownHandle: myHandle);
if (slot.IsValid)
{
    // Position at slot.X, slot.Y with slot.Height
}
```

## Requirements

- Windows 10 version 1809 (build 17763) or later
- .NET 8.0
- Windows App SDK 1.6+

## License

MIT
