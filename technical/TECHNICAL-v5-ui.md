# OmniHub — interface, version 5: a console, not a column

> Written against the graph at `graphify-out/graph.json` (4,846 nodes, rebuilt at `5081bef`), the
> source at that commit, and — as in version 4 — rendered images of every screen in the interface
> and palette the owner actually runs (`Interface: Workspaces`, theme `Ember`, density 2, read from
> `%AppData%\OmniHub\settings.json`). `TECHNICAL-v3-ui.md` and `TECHNICAL-v4.md` are the documents
> this one follows; v3's unfinished U4 (two-column page bodies) is absorbed here.

---

## 1. The bar this has to clear

Every earlier visual pass that changed how content was *drawn* — palette, radius, gradients, type —
was answered with "it still looks the same", and the answers were right. The owner judges a redesign
by structure: **what is on the screen, how much of it, and how it is arranged**. Their own tools
say the same thing: HWiNFO, MSI Afterburner and RivaTuner are all maximally dense and
information-first, and the register they asked for is open-source tool documentation — tables,
measured numbers, every figure naming its source.

So the question this document answers is not "how should it look" but "what should be on screen at
once, and arranged how", at the size the window opens at (1100 × 740).

## 2. What the renders show

Rendered with `tools/snapshot` at `5081bef`, sidebar interface, Ember, 1100 × 740.

| # | Finding | Evidence |
| --- | --- | --- |
| **F1** | **Every page is one scrolling column.** Above the fold, the Fans page shows a banner, three tiles holding one number each, and the top half of the curve; Tuning shows a heading, a lede and one card; System shows two toggles. The pages are 2–4 screens tall and the window shows the first. | All seven renders; every view is a `ScrollViewer` › `StackPanel` of `CardBorderStyle` borders |
| **F2** | **A tab looks exactly like a hardware switch.** The section selector over Performance, System and Diagnostics, the fan mode (AUTO/BIOS/MAX) and the power profile (Eco/Balanced/Performance) are one control, `PillRadioStyle`. Choosing a *view* and choosing what the *fans do* are drawn identically, one above the other on the same page. | `GroupView.xaml.cs:47`, `FansView.xaml:30-32`, `DashboardView.xaml:153-155` |
| **F3** | **The sidebar says one thing.** Seven names and an ACTIVE MODE card holding one fact (the fan mode). What is actually in force — fan level, power profile, battery state, which tweaks are on — is spread over four pages and visible from none of the others. | `MainWindow.xaml:53-66`, `BuildWorkspaceNav` |
| **F4** | **No reading has a history beside it except in the top bar.** `MetricSource` keeps a rolling window for every reading, but no surface shows a minimum, maximum or mean, and the tiles show a bare number. HWiNFO's whole value — *now, min, max, average* per sensor — is absent. | `MetricSource.History`, `ReadingsTablePanel` (value and source only) |
| **F5** | **Prose takes the prime space.** The KNOWN HP ISSUE banner sits at the top of Fans whatever the mode, though it describes only BIOS mode; every System toggle is a card of explanation around one switch. | `FansView.xaml:39-49`, `OptimizeView.xaml` |
| **F6** | **The Dashboard repeats the bar above it.** Four metric cards restate the CPU temperature, GPU temperature, memory and load the instrument bar already shows; six quick-action chips duplicate controls that live on Fans and System. | `DashboardView.xaml:196-335` |
| **F7** | **The fan loop's decisions are thrown away.** `FanTick` records measured, filtered and effective temperature, the sensor, the ceiling flag, the level and any error, every tick. The page shows the latest as one sentence, overwritten every two seconds. The sawtooth W9 fixed was visible only by replaying the thermal log offline. | `FansView.xaml.cs:602` |

## 3. The design

### 3.1 Principles

1. **At the opening size, the page's subject is on screen without scrolling.** Scrolling is for
   detail and rarely used tools, never for the instrument.
2. **A reading appears with its history**: now, minimum, mean, maximum and a trace, on every table
   that lists readings.
3. **Tables where there are rows**, panes where there are instruments, cards nowhere by default.
4. **Explanation sits where it applies** — beside the control, in the row, or shown when the mode it
   describes is chosen — not above everything.
5. **Navigation and hardware control never share a look.** A tab is an underline; a mode switch is a
   segmented control.

### 3.2 The shell

**S1 — The sidebar becomes an index of what is in force.** Each workspace keeps its icon and name and
gains a second line: the live state of the subject its first panel drives, in the monospace figure
style. The ACTIVE MODE card is removed; its one fact moves into the Fans line, alongside two more.

| First panel | Second line (examples) |
| --- | --- |
| `dashboard` | `limited by temperature · 75%` |
| `fans` | `auto curve · 40% · 2,800 rpm` |
| `performance` | `adaptive · 19.3 W` |
| `power` | `100% · on AC` |
| `system` | `2 of 5 on` |
| `settings` | `Ember` |

The text is composed in Core (`NavSummary`) from plain facts, so it is testable; the shell supplies
the facts on the tick `MetricSource` already runs, only while the window is visible. A workspace led
by a panel with no summary shows its name alone, as today.

**S2 — Sections are a tab bar.** `GroupView`'s pills become document tabs across the top of the page:
text, a 2 px accent underline on the selected one, a hairline under the bar. They no longer resemble
the AUTO/BIOS/MAX switch below them.

**S3 — Panes.** A pane is a bordered surface with its title *inside* it — `PaneTitle` (12.5 px
semibold) at the left of a header line, `PaneMeta` (mono, faint) at the right for a count, a window
or a timestamp. It replaces the heading-rule-card triple, which spent ~40 px per section on a
heading floating above its own content.

**S4 — The sensor table.** One control, `SensorTable`, used on the Dashboard and — filtered to its
own readings — on each hardware page:

```
SENSOR          NOW      MIN      AVG      MAX    TREND        SOURCE
CPU die        69.0°    52.1°    61.3°    81.1°   ╱╲╱──        SMU die temperature…
Fan 1          2800     2300     2640     3900    ╱╲           vendor BIOS fan readback…
```

- *Now* keeps the threshold colouring the readings already define (`Metrics.LevelOf`); nothing else
  in the row is coloured.
- *Min / Avg / Max* are **since the application started, or since Reset** — the HWiNFO semantics.
  The window-limited trace can say nothing about the spike an hour ago; a session maximum can.
  Computed in Core by `RunningStats`, fed by `MetricSource.Set`, which already sees every sample.
- *Trend* is the existing `Sparkline` for that reading, with its minimum span.
- *Source* appears when the table is wide enough to set it on one line, and is always the row's
  tooltip. It is `MetricDefinition.Source`, unchanged — this project's first rule is that a reading
  names its source.
- A reading that did not answer stays in the table, dimmed, reading `--`. The absence is a finding.

### 3.3 The pages

**Dashboard — the console.** Opens on the whole machine:

```
┌ Sensors ──────────────── since 22:35 · Reset ┐ ┌ Limits ──────────────┐
│ every reading: now min avg max trend          │ │ the five constraints  │
│                                               │ └───────────────────────┘
│                                               │ ┌ In force ─────────────┐
│                                               │ │ fans, profile, plan…  │
│                                               │ │ [Eco|Balanced|Perf]   │
└───────────────────────────────────────────────┘ └───────────────────────┘
┌ Last five minutes ─────────────────────────────────────────────────────┐
│ die temperature, fan duty, commanded                                    │
└─────────────────────────────────────────────────────────────────────────┘
```

The four metric cards and the six chips go (F6): every figure they held is in the table with more
beside it, and every chip's action lives on the page for the hardware it drives. The readiness card
stays, above everything, shown only when something is missing.

**Fans — the curve and the loop.** The curve at the left with the live point on it; at the right a
*Now* pane: measured temperature, the temperature acted on (the median of five, W9), GPU
temperature, commanded level, both fans' RPM, mode, and the safety floor inline. Below, **What the
loop did**: the last twenty ticks as a table — time, measured, acted on, level, note — so a spike set
aside, a forecast, a failure or a sensor on its ceiling is visible as it happens rather than
reconstructed from a log (F7). The HP-issue note appears when BIOS mode is chosen, which is the only
time it describes what the fans are doing (F5). Curve points, calibration and return to stock
follow.

The ticks are kept by `FanService` in Core (`FanTickLog`, a small ring), not by the page, so the log
is complete when the page is first opened; the short note is `FanTick.Note()`, tested beside
`Describe()`.

**Battery, System, Diagnostics, Settings, Performance.** Panes and row lists. Tiles holding one
number become rows in a table; each System tweak becomes a row — name, state, one line of effect,
switch — in one pane per section instead of a card each. Where a page has an instrument and its
settings, the instrument takes the left column and the settings the right.

### 3.4 What does not change

- **Dark only**, every palette including Ember, and the palette system itself.
- **The sidebar icons**, keyed off the first panel, as before; S1 adds to them.
- **No coloured prose.** Colour is in marks, bars, traces, and on a figure past its threshold.
- **Motion stays quick** (220 ms on figures, 240 ms on colour) and stops when the window is hidden.
- **The workspace model and every panel type**; the four alternate interfaces keep working and
  reach every page.
- **No fabricated telemetry.** A denser screen is more places to show a number nobody measured.
  Unavailable stays `--`.

## 4. Work packages

Each ends with refactor and unit tests. Rules that are asserted are asserted by a test that is first
seen to fail against the defect.

| # | Package | Test |
| --- | --- | --- |
| **V5-1** | `RunningStats` in Core; `MetricSource` feeds it and can reset it | `RunningStatsTests`: min/max/mean over samples, a missing sample leaves the stats alone, reset empties them |
| **V5-2** | Styles: `PaneStyle`, `PaneTitle`, `PaneMeta`, `TabBarRadioStyle`, `NavStatusText` | `ContrastTests` extended to the new text styles in every palette; `PageStructureTests` extended to `PaneTitle` (sentence case, uncoloured) |
| **V5-3** | `SensorTable` control | Rendered; its arithmetic is V5-1 |
| **V5-4** | Shell: sidebar index (`NavSummary` in Core) and the tab bar | `NavSummaryTests`; `PillRadioStyle` no longer used for navigation, asserted |
| **V5-5** | Dashboard console | Rendered; `PageStructureTests` unchanged |
| **V5-6** | Fans: two columns, *What the loop did* (`FanTickLog`, `FanTick.Note`) | `FanTickLogTests`; `FanTick.Note` cases beside `Describe` |
| **V5-7** | Battery, System, Diagnostics, Settings, Performance to panes and rows | Rendered |
| **V5-8** | Documents, graph, site | — |

## 5. Verification

- `dotnet build -c Release` clean; `dotnet test` green (919 before this change, only grows).
- Every page rendered at 1100 × 740 and maximised, in Ember and in one other palette, and checked
  for the principle in 3.1: the subject above the fold.
- Contrast of every new text style against the surface it lands on, in all nine palettes, by test.
- Deployed to the owner's install, the running copy checked, and the page they open by default is
  the one that changed.

## 6. What it became

- **Every page is panes.** 42 heading-rule-card triples across eleven views became panes with the
  title inside; 37 by a scripted rewrite that located each card's closing tag by indentation and
  left anything irregular alone, five by hand. Page gutters are one value, 18 px, and the vertical
  rhythm is one value, 10.
- **Two columns where there is an instrument and its settings**: Dashboard, Fans, Battery, System
  (Windows), Graphics. Below each page's own width threshold the side column stacks underneath
  (`ColumnReflow`), because at 150% scaling a laptop screen is 1280 px wide.
- **The density setting reaches the new geometry.** The owner runs *Roomy*. Pane padding and table
  row height are theme resources scaled by the same pass as card padding, with a floor so Compact
  cannot clip a 12 px line (`Density.RowHeight`).
- **Session figures are true session figures.** While hidden, `MetricSource` keeps counting —
  the fast readings from the poll the fan curve needs anyway, the slow ones every 30 s instead of 5 —
  and draws and raises nothing, so a maximum reached during a game played from the tray is on the
  table afterwards.
- **Also removed**: six Dashboard chips duplicating controls on other pages (one changed the saved
  fan mode without the Fans page's selector finding out); four metric cards restating the bar above
  them; a Battery card repeating its own lede; the GPU page's local copy of `TileLabel`, which
  shadowed the shared one without its text colour; two lines of coloured status text on Fans.

### Found on the way

- **Settings centred every note** — `SettingNote`, a style local to the view, capped width without
  pinning alignment: the version 4 defect R1 again. `EveryWidthCapSaysWhichSideItKeepsTo` read
  styles from `Styles.xaml` only and passed. It now reads every style in every file, and failed
  naming `SettingsView.xaml: SettingNote` before the fix.
- **A faint line on a selected sidebar item** is text on AccentSoft composited over the window
  ground, which the contrast matrix did not contain. Added; all nine palettes pass.

### Rules asserted, each seen to fail against its defect

| Rule | Test | Injected |
| --- | --- | --- |
| A pane title is sentence case | `SectionHeadingsAreSentenceCaseRatherThanShouted` | `CURVE POINTS` |
| No title, meta, cell or sidebar line is coloured | `NoLabelOrHeadingIsColoured` | a pane title in `WarnBrush` |
| A tab is not drawn as a hardware switch | `NavigationIsNotDrawnAsAHardwareSwitch` | `PillRadioStyle` back in `GroupView` |
| Every sidebar line fits its 149 px column | `EveryLineFitsTheSidebar` | "limited at" |
| A missing reading is not a zero | `AMissingReadingLeavesTheFiguresAlone` | null counted as 0 |
| A width cap pins its side, in any file | `EveryWidthCapSaysWhichSideItKeepsTo` | (the real `SettingNote`) |

Each injection was reverted byte for byte, checked by hash. Suite: 919 before this change, 945
after, green.

## 7. Widgets: the same information, drawn

The console was answered "sure its nice, but its just pure text, i want widgets, graphics,
something actually pleasing to look at". The tables stay as the detail layer; the headline is now
drawn.

| Widget | Draws | Full scale |
| --- | --- | --- |
| `RingGauge` | a temperature as a 240° arc, figure in the middle, label in the arc's open bottom | the reading's own hot point (`Metrics.FullScale`) |
| `Meter` | a figure over a gradient bar | 100% for a load; the SMU's sustained limit for package power; NVML's enforced limit for GPU power; the fan band's measured maximum; installed memory |
| `FanGlyph` | an eleven-blade impeller that turns faster when the fans do | — (the real rpm is printed beside it) |
| `BatteryGlyph` | the pack filled to its charge, a bolt while charging | 100% |

**No scale, no gauge.** `Gauge.Fraction` returns null for a missing reading or a missing scale, and
a null draws the track alone with the figure beside it. Package power on a machine without an SMU
is a number, not an arc against an invented maximum. The two new scales are read, not assumed:
`MetricSource.PackageLimitWatts` comes from the same power-table read as the package figure, and
`Nvml.KnownPowerCeilingWatts` is the limit NVML already reported for its own plausibility check.

**The fan's rate is legible, not literal.** A fan at 5,600 rpm turns 93 times a second, which drawn
literally is a blur or a wheel crawling backwards against the frame rate. `Gauge.SecondsPerTurn`
maps speed to one turn per second at 3,000 rpm, clamped between 0.4 s and 8 s, and a stopped fan is
drawn still. The spin is an endless animation, so it starts and stops on visibility like every
other; `AmbientMotionTests` failed naming `Widgets.cs … in Spin` when the gate was removed.

**Where they are.** The Dashboard leads with four widget cards — processor, graphics, cooling,
power — over the chart, the profile and the limits, with the sensor table last. The Fans page's
live column is a temperature ring beside the turning fan; the GPU page draws the card's ring and
bars; the Battery page draws the pack. Cockpit's dials now share `Arc` with the rings rather than
keeping a copy.

**Open, not addressed here.** Switching theme live leaves the gradient accents — rings, fan, bars,
traces — in the previous palette until restart; the palette swap does not reach brushes whose stops
are dynamic references. It predates this work and is visible only after a live switch.
