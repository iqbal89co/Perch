# Perch architecture

Perch is a .NET 8 WPF app (`net8.0-windows10.0.19041.0`) that shows a small "island" at the top of the
screen. Resting, it is a pill showing the clock or the most important live activity; on hover it springs
open into a panel with tabs (pages).

```
Perch/
  Perch.sln
  src/Perch/
    App.xaml(.cs)          startup, single instance, services, test switches (--expand, --screenshot ...)
    Core/                  contracts shared by everything
      IslandPage.cs        base class for a tab  + IIslandService for background features
      IslandContext.cs     settings, activity hub, media, Notify(), Navigate()
      ActivityHub.cs       what the resting pill shows (PillActivity)
      AppSettings.cs       persisted settings (data/settings.json)
      JsonStore.cs         atomic JSON persistence under %APPDATA%\Perch\data
      AppPaths.cs, Log.cs, Format.cs, Spring.cs, ThemeManager.cs
    Services/              MediaService (Windows media sessions), FullscreenWatcher, Autostart, Native
    Shell/                 IslandWindow (the island), Styles.xaml, TrayIcon, EqualizerBars
    Pages/<Feature>/       one folder per tab
    SettingsUi/            the Settings window
  tests/Perch.Tests/       xUnit tests; one <Feature>Tests.cs file per feature
```

## Adding a page (tab)

Create `src/Perch/Pages/<Feature>/` and a class deriving from `Perch.Core.IslandPage`. It is discovered by
reflection and created once at startup on the UI thread; no registration anywhere else. See
`Pages/Today/TodayPage.xaml(.cs)` for the reference pattern (XAML root `<core:IslandPage ...>`).

```csharp
public partial class TasksPage : IslandPage
{
    public override string Id => "tasks";        // lowercase, stable
    public override string Title => "Tasks";
    public override string Glyph => "";    // Segoe Fluent Icons code point
    public override int Order => 40;             // media 10, today 20, shelf 30, tasks 40, focus 50, system 60
    public override void OnShown() { }           // tab became visible (island open)
    public override void OnHidden() { }
}
```

* Content area is about 608 x 256 DIPs (ExpandedWidth 640 - 32, ExpandedHeight 320 - 64). Design for that
  and don't scroll the whole page; use a ScrollViewer only for lists.
* Pages keep running while hidden (timers etc.), so stop expensive polling in OnHidden.
* `Ctx` (= `IslandContext.Current`) gives `Settings`, `UpdateSettings(...)`, `SaveSettings()`,
  `Activities` (resting pill), `Media`, `Notify(glyph, text)`, `Navigate(pageId)`, `IsExpanded`, `Dispatcher`.

## Background features

Implement `Perch.Core.IIslandService` (public parameterless ctor). It is created and `Start(ctx)` is called
once after the island exists. Implement `IDisposable` if it holds OS resources.

## The resting pill

Post a `PillActivity` to `Ctx.Activities`:

```csharp
Ctx.Activities.Post(new PillActivity {
    Key = "focus", Priority = 60, Width = 200,
    Leading = someGlyphTextBlock, Center = label, Trailing = timeText,
    ExpiresAt = null });            // or DateTime.Now.AddSeconds(3) for transient notices
Ctx.Activities.Remove("focus");
```

Priority: transient notices 100, focus timer 60, media 40. Highest wins; the clock shows when none.
Re-posting with the same key replaces the old one. Leading/Center/Trailing must be fresh elements
(an element can only have one parent) or the same element re-posted under the same key. Keep text short:
the pill is 32 DIPs tall. `Ctx.Notify(glyph, text)` is a shortcut for a 3-second notice.

## Look and feel

* Never hard-code colours. Use DynamicResource keys from ThemeManager: `IslandForeground`, `IslandSubtle`,
  `IslandAccent`, `IslandAccentSoft`, `IslandOnAccent`, `IslandCard`, `IslandCardHover`, `IslandStroke`,
  `IslandDanger`. In code: `element.SetResourceReference(TextBlock.ForegroundProperty, "IslandSubtle")`.
* Use the styles in `Shell/Styles.xaml`: `PerchTitle`, `PerchBody`, `PerchCaption`, `PerchGlyph` (TextBlock),
  `PerchCard` (Border), `PerchIconButton`, `PerchButton`, `PerchGhostButton` (Button), `PerchToggle`
  (CheckBox as switch), `PerchTextBox`, `PerchSlider`.
* Fonts: `{StaticResource UiFont}`, `{StaticResource DisplayFont}`, `{StaticResource IconFont}`
  (Segoe Fluent Icons / Segoe MDL2 Assets glyphs like `&#xE768;` play, `&#xE769;` pause).
* Rounded corners 10-14, generous spacing, one accent colour. Modern and calm.

## Persistence

`JsonStore.Load<T>("name")` / `JsonStore.Save("name", value)` -> `%APPDATA%\Perch\data\name.json`
(snake_case JSON). Each feature owns its own file name. Files dropped on the Shelf go to `AppPaths.Shelf`.
Never write anywhere else. Tests call `AppPaths.OverrideRoot(tempDir)` first.

## Gotchas

* `System.IO` is a global using. If a file also imports `System.Windows.Shapes`, write `System.IO.Path`
  explicitly (it clashes with `Shapes.Path`).
* WinForms types are available but NOT globally imported; alias them (`using WinForms = System.Windows.Forms;`).
* All UI work on the UI thread. `MediaService` already raises its events on the UI thread.
* Keep pure logic (parsers, timers' state machines, math) in plain classes without WPF so it is unit-testable.
* Log with `Log.Info/Warn/Error`. Never let an exception escape an event handler or timer tick.

## Build, test, look

```bash
dotnet build Perch/src/Perch/Perch.csproj -c Debug
dotnet test Perch/tests/Perch.Tests
# render your page to a PNG with a throwaway data folder (does not touch real settings):
Perch/src/Perch/bin/Debug/net8.0-windows10.0.19041.0/Perch.exe --data-dir <tmp> --expand <pageid> --screenshot <file.png> --delay 1500
```
