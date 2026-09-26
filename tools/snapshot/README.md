# snapshot

Renders OmniHub's real main window — every workspace, in any interface and theme — to PNG.

OmniHub runs elevated, so screenshots taken from outside it come back masked, and the test project
cannot reference the App because the App's build output is the installed program. Until this
existed, every change to the interface was made by reading XAML and reasoning about it. The first
images it produced showed five defects on every page that no test, warning or exception had
caught: paragraphs drawn centred, eleven labels in black on a black card, a fan curve with its axis
cut off, stock white inputs, and a smudge across every card.

```powershell
.\tools\snapshot\run.ps1                             # every workspace, saved interface and theme
.\tools\snapshot\run.ps1 -Plan "iface=Cockpit,0"     # the Dashboard in Cockpit
.\tools\snapshot\run.ps1 -Plan "theme=Midnight,ws"   # every workspace in Midnight
.\tools\snapshot\run.ps1 -Replay                     # live figures fed from today's thermal log
```

Images land in `%TEMP%\omnihub-snapshot` unless `-Out` says otherwise. The window is 1100×740, the
size OmniHub opens at; set `SNAPSHOT_WIDTH` / `SNAPSHOT_HEIGHT` to try another.

## Why it cannot touch the machine

The window it builds is OmniHub's own `MainWindow`, which on a normal launch takes the fans, applies
power limits and starts automations — while the real OmniHub is usually running beside it. So the
containment is done by Windows, not by care:

| Layer | Guarantees |
| --- | --- |
| **Low integrity**, from a label on the executable | Cannot write the settings file, the registry, WMI methods or the PawnIO device. The tool **refuses to start** unless a write to `%AppData%\OmniHub` is denied and the App's own `HardwareContext` reports no vendor interface and no SMU. |
| **A separate, invisible desktop** | Nothing it opens — a window, a dialog, a balloon — can appear in front of the person at the machine. |
| **A plain `Application`, never OmniHub's `App`** | OmniHub's startup path never runs. Constructing an `Application` subclass queues its `OnStartup`; an early version of this tool did that, and put OmniHub's "already running" dialog on the user's screen. |

On top of those, every automation setting is switched off in memory before any page is built.

## Limits

- No hardware readings: the sandbox is the point. Live figures read "--" unless `-Replay` feeds in
  recorded ones, and a replayed image shows how a screen lays out, **never** what was measured.
- It drives `MainWindow` by reflection (`ShowWorkspace`, `SelectNavItem`, `_settings`, `_layout`,
  `_metrics`). Renaming one breaks this tool, not the application.
- A grouped page (Performance, System, Diagnostics) renders its first tab unless the step names one:
  `5/History` is the History tab of the sixth workspace. The plan crosses a command line, where a
  space ends an argument, so a caption with spaces is written with underscores: `4/App_GPU_routing`.
