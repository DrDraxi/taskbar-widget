# TaskbarWidget

A tiny toolkit for putting your own widgets on the Windows 11 taskbar. Rendering uses Direct2D/DirectWrite into a per-pixel-alpha layered window, and apps compile to a single ~2 MB native exe with .NET NativeAOT.

- **Looks native**: anti-aliased shapes and text, Windows 11 hover pill, rounded tooltips with a soft shadow, dark-mode context menus. It follows the taskbar's light/dark setting live.
- **Lightweight**: no runtime to unpack, no timers while idle, event-driven repositioning. Typical widget: ~7 MB private memory and 0 % CPU at idle.
- **Robust positioning**: several widget processes share the taskbar without fighting. See [How positioning works](#how-positioning-works).
- **Batteries included**: drag-to-reorder, tooltips, timers, right-click menu, WinHTTP client, JSON config helper, start-with-Windows.

## Quick start

```csharp
using TaskbarWidget;

int count = 0;
Widget? widget = null;

widget = new Widget("Counter", ui =>
{
    ui.Text($"{count}", new TextStyle(14, 600));
    ui.Tooltip("Counter", "Click to add one");
    ui.OnClick(() => { count++; widget!.Invalidate(); });
});

widget.Show();
Widget.Run();
```

The build callback runs again on every `Invalidate()`. Describe what the widget should look like *now*; there's no retained tree to update.

### Custom drawing

```csharp
ui.Custom(40, 8, (g, state) =>
{
    // DIP coordinates relative to the element; DPI scaling is automatic.
    g.Line(4, 4, 36, 4, g.Theme.TextTertiary, 2);
    g.FillCircle(4 + 32 * progress, 4, 3 + state.Hover, g.Theme.Success);
})
.OnClick(() => ...)
.Tooltip("Title", "Body");
```

`ElementState.Hover` / `.Pressed` animate smoothly from 0 to 1, so it's easy to do lift or glow effects.

## API

### `Widget`

| Member | |
|---|---|
| `new Widget(name, build, options?)` | `name` identifies the widget in the shared order (letters, digits, `_`, `-`) |
| `Show()` | Create the window and place it on the taskbar |
| `Invalidate()` | Rebuild and repaint (thread-safe, coalesced) |
| `Post(action)` | Run an action on the UI thread (thread-safe) |
| `SetInterval / SetTimeout / ClearTimer` | UI-thread timers |
| `ShowMenu()` | Open the context menu |
| `Resumed` | Event raised after the PC wakes from sleep |
| `Widget.Run()` / `Widget.Exit()` | Message loop / quit |

`WidgetOptions`: `AllowReorder` (default `true`), `SingleInstance` (default `true`), `Menu` (adds items to the right-click menu; **Exit** is always appended).

Right-click runs the element's or widget's `OnRightClick` handler if there is one, and opens the menu otherwise. **Shift+right-click** always opens the menu.

### `Ui` (build callback)

| Member | |
|---|---|
| `Text(text, style?, color?)` | Single-line text |
| `Image(image, w, h, opacity?)` | Image, resampled with high quality at the exact pixel size |
| `Custom(w, h, draw)` | Custom-drawn element |
| `Gap(w)` | Explicit spacing (`Spacing` sets the default, 6 DIP) |
| `MeasureText(text, style)` | Size text before laying it out |
| `Tooltip(title, body?)`, `OnClick`, `OnRightClick` | Widget-level |
| `Theme`, `Scale` | Current palette and DPI scale |

Elements return an `Element` you can chain `.OnClick()`, `.OnRightClick()` and `.Tooltip()` onto.

### `Graphics` (custom draw)

`FillRect`, `FillRoundRect`, `StrokeRoundRect`, `FillCircle`, `StrokeCircle`, `Line` (round caps), `Image`, `Text`, `MeasureText`, `Theme`, `Scale`.

### Helpers

- `Http.Get / PostJson / Send`: blocking WinHTTP client. Call it from a background thread, then `widget.Post(...)` the result back.
- `JsonConfig`: case-insensitive config file access that keeps unknown keys when saving, and never overwrites a file it couldn't parse.
- `AppKit.DataDir`, `AppKit.SetStartWithWindows`, `AppKit.Env` (`.env` lookup), `AppKit.Open`, `AppKit.WriteAllTextAtomic`.

## How positioning works

Widgets are packed right-to-left from the left edge of the notification area. Each widget is its own process, so the hard part is keeping several processes in agreement. Earlier versions were prone to random swaps, gaps and widgets landing at the top of the screen. The rules now:

1. **One deterministic layout.** Every widget computes the *same* layout from the same inputs: the shared order file (`%LOCALAPPDATA%\TaskbarWidget\widget-order.json`, index 0 = rightmost) plus each visible widget window's width. Unknown names sort after known ones alphabetically, so all processes agree even before registration.
2. **Each widget moves only itself.** No process ever moves another process's window, and the layout never depends on where windows currently are, so it can't oscillate. Changes are broadcast (`TaskbarWidget_Reposition`) and everyone re-runs the same calculation. Movement glides with an exponential ease.
3. **Never guess geometry.** The taskbar rectangle is validated (exists, horizontal, fully on its monitor). If it isn't valid (explorer restarting, auto-hide, mid display change), the widget hides instead of falling back to `(x, 0)`.
4. **Self-healing.** Explorer restarts re-own the window to the new taskbar (or recreate it if Windows destroyed it). A 1 s safety check catches anything an event missed. Taskbar/tray moves are picked up instantly through a WinEvent hook scoped to explorer.
5. **Shared-file safety.** Order-file writes happen under a cross-process mutex with atomic replace, because widgets usually all start at login at the same moment.

Drag a widget to reorder. The new order is saved as you drag and the neighbours slide out of the way.

Widgets built on older versions of this library still interoperate (same order file, window class prefix and broadcast message), but they keep their old positioning bugs until they're rebuilt.

## Building

Requires the .NET 10 SDK and, for NativeAOT publishing, the Visual Studio "Desktop development with C++" workload.

```bash
dotnet build TaskbarWidget.sln
dotnet publish samples/Demo/Demo.csproj -c Release -r win-x64   # ~1.7 MB native exe
```

Set `TASKBARWIDGET_LOG=1` for verbose logs in `%LOCALAPPDATA%\TaskbarWidget\logs\<name>.log`. Errors are always logged there.

An app project should set `PublishAot`, `InvariantGlobalization` and a PerMonitorV2 `app.manifest`. See `samples/Demo`.
