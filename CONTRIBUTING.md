# Contributing to Island

Thanks for helping! Island is a native Windows app (C# / .NET 10 + Direct3D 11 + DirectComposition). There's no UI framework: we draw every pixel and animate it with springs.

## Getting started

```powershell
dotnet run --project src/Island.Lab -c Release -- --showcase
```

`--showcase` gives you a scripted team of agents, usage and limits, so you don't need Claude Code (or your own sessions on screen) while you work. `Ctrl+Alt+0` toggles slow motion, which is the best way to judge a transition.

## Code layout

| Folder | What lives there |
|---|---|
| `Island/IslandModel.cs` | Modes, springs, hit-testing, alerts: the island's brain |
| `Island/Scenes.cs` | Drawing for each mode (dashboard, media, focus…) |
| `Island/TabBar.cs`, `Scenes.Tabs.cs` | The dashboard's tabs: the catalogue, your picks, the tab library, and each tab's data and panel |
| `Island/AgentTeam.cs` | Claude Code agents as a team of Bloubs: grid, focus, list, new agent |
| `Island/ClaudeSessions.cs`, `ClaudeUsage.cs`, `ClaudeLimits.cs` | Reading Claude Code's local files and plan limits |
| `Island/AgentRunner.cs` | Headless `claude -p` agents over stream-json |
| `Island/Showcase.cs` | The scripted demo data behind `--showcase` |
| `Island/StageScene.cs`, `Welcome.cs`, `Celebration.cs` | Full-screen moments where Bloub leaves the island |
| `Mascot/` | The Bloub engine (states, moods, shapes, gaze), pure functions of time |
| `Motion/Spring.cs` | The analytic damped spring everything moves with |
| `Render/` | D3D11/D2D device, frame renderer, canvas helpers |
| `Platform/` | Win32 interop, tray, terminal focusing, shell helpers |

## Adding a tab

A new tab is a great first PR. It touches four places:

1. **`TabBar.Catalogue`**: add a `TabDef` with an id, a name, a Segoe MDL2 icon, a colour and a one-line blurb. It shows up in the tab library right away.
2. **`Scenes.Tabs.cs`**: draw its panel (`static void YourPanel(Canvas c, IslandModel m, float x, float y, float w, float h)`). Reuse `Card`, `PillButton`, `Chip` and `Empty` so it looks like the rest.
3. **`Scenes.Full`**: add a `case "yourid":` that calls your panel.
4. **Data**: keep it in `TabBar` and refresh it from `TabBar.Update` only while the tab is selected. In `--showcase` mode, return believable sample data instead of reading the user's machine.

Clicks are ids like `"yourid:action:3"`, which `IslandModel.ActTabs` routes. Try it with `ISLAND_DEMO=tab:yourid` and `--showcase`.

## The rules that matter

1. **Motion quality first.** Use springs (`SpringSpec.Island`, `.Bounce`, `.Content`…), never fixed durations, and make sure an animation can be interrupted midway without jumping.
2. **Never steal focus and never block clicks** outside the black shape.
3. **Local-only.** Don't add network calls, telemetry or writes to `~/.claude`.
4. **Match the surrounding code:** the same naming, comment density and idioms.

## Pull requests

- Keep each PR focused on one feature or fix.
- For anything visual, attach a short GIF or clip (record it in `--showcase` mode).
- Make sure `dotnet build -c Release` passes with no new warnings.

## Reporting bugs

Use the [bug report template](https://github.com/UsoguiH/dynamic-island-windows/issues/new?template=bug_report.yml). Include your Windows version, display scale/refresh rate and the log at `%TEMP%\islandlab.log`.
