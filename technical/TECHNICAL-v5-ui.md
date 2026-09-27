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
are dynamic references. It predates this work and is visible only after a live switch. *(Fixed in
section 8.)*

## 8. The fourth pass: history as the hero, colour, and light

"starting to get there, but not quite yet", answered with all four directions offered: graphics
everywhere, a richer look, big live charts, and different colours.

### The theme switch, fixed first

Blue-to-cyan depends on it, so it went first. Measured on a live switch from Ember to Midnight, only
the pane surfaces followed; the ground, the sidebar, the muted text and every gradient kept Ember.
The cause: a `DynamicResource` reference caches what it found and forgets it only when its owning
element announces a resource change, and a brush shared by hundreds of elements has no single owner
to hear it from. (Styles and templates clone the brush per element; those clones do have one, which
is why a few surfaces followed.)

`ThemeManager.Apply` now re-links each shared brush after the palette swap, from
`Core/Theming/ThemeBrushes` — a table of brush to palette colour keys that `ThemeBrushTests` holds to
`Theme.xaml` brush for brush. Each colour gets a *fresh* reference, which is what the XAML parser
attached in the first place. Two tempting alternatives are wrong, and the code says why: assignment
makes the brush freezable, and a style sealed afterwards freezes it; `SetCurrentValue` layered over a
reference makes WPF's per-element clone throw an `InvalidCastException` (seen in a render).

Found on the way: a custom palette never produced `AccentColor2`, so every gradient on one ended on a
leftover colour — and its test agreed, because its list of required keys was a hand copy. The list is
now read from a shipped palette. Midnight is the default for new installs (`AppSettings.ThemeName`
follows `ThemeManager.DefaultId`), and the sidebar names the theme actually applied.

### History as the hero

`ChartStack` draws four strips on one clock — temperature, fans, power, load — with one crosshair
through all of them and each strip's live figures in its header, which show the values under the
pointer while it hovers. Stacked rather than overlaid, because the question is coincidence and that
is read down a vertical line. 5, 15 or 30 minutes.

The data is `MetricSource.Recent`, a `RecentSeries` per reading holding the last thirty minutes,
recorded whether or not anything is drawn: a chart that collected for itself would open empty after
a session in the tray, exactly when its history mattered. Fan levels are recorded only on the ticks
that read them — the thermal log's rule. While hidden the slow readings arrive every thirty seconds,
so their series carry an explicit 95-second gap threshold. A missing reading is not left to that
rule: `RecentSeries` records it as a break (one NaN per run), and `TimeSeriesDecimator` ends the line
at any non-finite value, so a GPU asleep for a minute is a gap, never a straight line across it.

The chart control gained what the strips needed, and every chart shares it: `ShareScale` draws the
series of one unit against one scale (before, a second series sat at a height its axis did not
describe); horizontal gridlines at the labelled values; a darker plot well; fills rebuilt from the
line's colour so a theme switch reaches them. A short plot gets no more intervals than it can label
(`height / 22`) and of those the tightest (`ValueAxis.Tightest`) — two intervals alone drew a
0–5,500 rpm fan band on a 0–10,000 scale.

### Light

Glow is a `DropShadowEffect` on the marks alone — ring arcs and meter fills — in the mark's own
colour, so an arc past its warning glows amber. The fan gets a halo instead: a masked static fill
behind the blades, lit only while the fan turns, because a blur on the rotor would be paid on every
frame it rotates. The rings are 136 px with a tick every tenth and the stretch past the warning
threshold tinted on the track like a tachometer's red zone. Panes are lit from above
(`CardGradientBrush`) with a highlighted top edge (`PaneEdgeBrush`). Figures roll to a new reading
in 240 ms (`Roll`), only while visible and only when Windows' animations are on — short enough that a
number in passing is not read as a reading.

### Graphics everywhere

| Where | Was | Now |
| --- | --- | --- |
| Dashboard, GPU page | sensor table | `SensorTiles`: figure, filled trace, session min and max; mean and source in the tooltip (`SensorTable` deleted) |
| Dashboard, Tuning | 3 px limit hairlines; Tuning kept a hand-built copy | `LimitStrip` meters with a mark at 95%, where a limit counts as binding; Tuning uses the shared control |
| Tuning, Live | "18.3 W of 25 W" text, and a "Limited by" line coloured red and amber | meters against each limit, amber once binding; the words uncoloured |
| System, timer | "1 ms, finest 0.5 ms" | a meter between the coarsest and finest resolution on a log scale (`Gauge.LogFraction`) |
| System, memory | available and total | in use against installed |
| System, shader cache and disk cleanup | text lists | `ShareBar`: each location's share of the whole, shades of the accent |

### Verification

964 tests pass. Each new rule was broken on purpose and its test failed: a row dropped from
`ThemeBrushes`, `AccentColor2` dropped from the custom palette, `Tightest` reduced to two intervals,
`LogFraction` made linear — each restored byte-identically. Rendered: a live switch from Ember to
Midnight across all seven pages, the Dashboard, and System.

**Checked and left alone.** The thermal log's `sensor` column alternates between the die sensor and
the ACPI zone. That is `ThermalReader.Merge` taking the higher of Tctl and the six-second-cached zone
on purpose, so the fan never acts on a lower temperature than the ACPI-only path would have; it is
pinned by `SmuTemperatureTests`.

## 9. The reference, measured, and built to

The fourth pass was answered "i feel like either the ui, or the widget or even the graphic is still
not there yet". The user had already settled what "there" looks like: on 31 August they supplied a
reference image — "use this as a reference, remember, clean, elegant, gradient, and very cool, give it
that premium app vibe you get from a very expensive brand" — and the Dashboard had drifted from it.
Set side by side, the gap is structural, not a matter of colour: the reference is composed around a
centre, the Dashboard was four equal boxes in a row in one colour.

### What the reference is, in numbers

Sampled from the image itself rather than remembered. One remembered fact was wrong: the ground is
neutral, not blue-tinted.

| Element | Measured |
| --- | --- |
| Ground, sidebar | `#0D0D0D` – `#0E0E0E` |
| Cards; centre panel; card edge | `#151515` – `#161616`; `#121212`; `#1B1B1B` |
| Processor: bar, edge stripe, icon | `#FC4080`, `#C73667`, `#B6325F` |
| Graphics: bar, stripe, icon | `#01E3FD`, `#07A5B7`, `#07A8BA` |
| Memory: bar, stripe, icon | `#6FF8BC`, `#5AC496`, `#5AC496` |
| Thermals: stripe, icon | `#B87008`, `#D17E05` |
| Centre ring, status dot | mint, `#68E8B0` at its brightest |
| Small labels | `#77787D` |
| Selected preset | `#DBE2FF` fill |

### Its structure

A header — title, one-line subtitle, the presets as a segmented control at the right. Under it three
columns: a card per component down each side (processor and memory left, graphics and thermals
right), each with its colour as a stripe along its outer edge, an icon in that colour, a small
category over its name, one large light-weight figure with a raised unit, a thin bar in the colour,
and two small labelled figures at its foot. In the centre, spanning both rows, one large ring on a
panel of faint geometric line-work, a status under the figure, and two readouts in a box beneath.
A wide live chart closes the screen.

### What it becomes here

The reference's centre is "System Index 98.4", a score, and its cards carry figures this machine does
not have (VRAM, memory latency, coolant). The project's first rule outranks the reference: every slot
keeps its place and its form, and takes a reading this application genuinely has.

| Slot | Here |
| --- | --- |
| Title and subtitle | the page's name and this machine's |
| Presets | Eco, Balanced, Performance as the segmented control, with what the selected one sends |
| Centre ring | die temperature against its hot point; mint, amber past 80 °C, red past 90; a status word with the colour on its dot only; fan speed and package power beneath |
| Processor (left) | clock large; load bar; package power and its sustained limit |
| Memory (left) | memory in use large, against installed; battery charge and power source |
| Graphics (right) | clock large; load bar; power against the driver's limit; temperature |
| Thermals (right) | processor and graphics temperatures as bars with their figures in badges; fan speed |
| Live chart | the history strips, in the components' colours |

Colour identity is per component, everywhere a component is drawn — the cards, the history, the
tiles: processor pink, graphics cyan, memory green, cooling amber. Cooling gets its own palette key,
`MetricFanColor`, so a fan's amber is never read as the warning colour it happens to resemble.

### How it was built

`ComponentCard` and `HeroDial` (`Controls/Console.cs`) are the reference's two shapes; the ring they
share with the small gauges moved out of `RingGauge` into `ArcDial`, so the Dashboard's dial and the
Fans page's are one piece of code. Names on the cards are the parts' own, shortened
(`HardwareNames`, tested): the processor's from the value Windows writes at boot, the graphics card's
from the driver. Midnight took the measured values, and three of them one step lighter than
measured — the pink, the danger red and the faint text — because with a mint accent the selected
surfaces are lighter, and `ContrastTests` put all three at 4.0 to 4.5 to 1 on them. The console
stacks below 960 px: the dial across the top, the cards two by two.

Rendered at 1,440 and 1,180 px against the reference; 974 tests pass.

## 10. Closer to the reference, and the previous look kept

Section 9's Dashboard was answered "it not quite there yet, also make the previous ui be a setting".
Asked which part, the user said all of it — the cards and numbers, the centre dial, the top bar and
sidebar, the lower half — and chose the new console as the default, with the previous interface one
setting away.

### The setting

**Settings → Look: Console | Previous.** `AppSettings.Look` (`UiLook`, default `Console`) is read by
`MainWindow` in three places: the Dashboard it builds (`DashboardView` or `DashboardClassicView`, the
section 8 Dashboard restored intact under its own class), the bar across the top (`ConsoleBar` or
`InstrumentBar`), and the chrome (`ApplyLook`: the one-line sidebar with its footer card, or the
two-line sidebar under the OMNIHUB heading). `SetLook` swaps all three live; nothing restarts. The
previous colours are a palette of their own, **Midnight Blue**, because Midnight itself took the
reference's neutral black in section 9 and a look is not a palette.

### Measured again, this time for proportion

Section 9 matched the reference's colours and its parts. What it missed was their sizes, and the
difference was large enough to read as a different design.

| | Reference | Section 9 | Now |
| --- | --- | --- | --- |
| Card : centre width | 1 : 2.07 | 1 : 1.3 | 1 : 2 |
| Card shape | portrait, figure at the foot | landscape, figure under the title | taller than wide, figure at the foot |
| Ring stroke, as a share of its diameter D | 2.2 % | 4.4 % | 2.5 % |
| Visible gap at twelve o'clock (after the round caps) | about 3° | about 15° | about 3° |
| Inner dashes | 0.82 D, visible beside the plate | 0.72 D, hidden behind it | 0.82 D |
| Readout box | 0.31 D – 0.46 D below centre, the ring showing under it | covering the ring's foot | as the reference |
| Line-work behind the ring | faint; the ring the only bright thing | as bright as the ring's track | 0.12 – 0.5 opacity |

Two departures are deliberate, and both are the project's first rule outranking the image:

- **The warning stretch is a redline.** The reference's ring is whole because its score is near the
  top. This ring is a temperature against its hot point, and where warm begins is information; it is
  now a 2 px amber arc just outside the ring rather than a band tinted into the track, which read as
  a brown gap.
- **The live chart draws temperature from 0 to the hot point**, the scale the dial and the sensor
  bars already use. A scale fitted to the data made a two-degree wobble look like a cliff; the
  reference's calm curves are what a real full scale looks like. The curves are smoothed by
  `Spline` (Catmull-Rom with every control point clamped to its own segment, so no stretch bulges past
  the two readings it joins — tested, and the test fails with the clamp removed), and each fill sits
  on an opaque copy of its own shape, so the lower curve's band covers the upper one's: pink between
  the lines, the second colour under its own, as the reference bands them.

### A defect found on the way: bars drawn as spindles

Every bar in the application — the cards', the meters', the limit strip's — rounded its ends with the
theme's `RadiusPill`, 999. A WPF `Border` whose radii are longer than its sides scales each axis on its
own, so a bar 5 px high and 100 wide got corners 50 px long and 2.5 high: thick in the middle and
pointed at both ends. `Pill.Round` sets the radius to half the bar's actual height instead. This
affected both looks and every theme.

### The chrome

`ConsoleBar` is the reference's top bar: a status pill at the left (what the fans are doing, the dot
coloured, the words not), and three readings at the right — die temperature, GPU clock, fan speed, each
figure in its component's colour — with the settings button after them. The sidebar is one line per
page in the monospace face, the selected page a neutral pill, and a footer card carries this machine's
short name (`HardwareNames.ShortModel`, tested) over the same fan status.

### Verification

Rendered at 1,440 and 1,180 px (three columns hold down to about 900) and compared against the
reference crops; the Previous look rendered in Midnight and Midnight Blue. The snapshot tool gained
two steps for this — `look=Previous` and `scroll=N`, the second because the lower half is below the
fold at any window a screen allows — and its log replay now starts each series fresh: appending the
log after readings the sandbox had already taken left a series out of time order, and a filled curve
folded back on itself. 991 tests pass.

## 11. Studio: the console for the screen it is actually on

Section 10's console was answered "sure, save it and make it an option to enable, but the ui is still
not there yet". Before designing anything the live install was read rather than assumed:
`settings.json` has theme **OLED Black**, density 2, on a **1920 × 1080** display at 96 DPI. Every
comparison against the reference in sections 9 and 10 had been rendered in Midnight at 1,440 px. Rendered
as the user sees it, three failures are plain, and none of them is visible in Midnight.

### What the user's screen shows

1. **Every component is the same colour.** OLED Black gives the processor `#22D3EE` and the graphics
   card `#2DD4BF` — two cyans. The processor card, the graphics card, both sensor bars, both top-bar
   figures and both curves of the live chart are cyan, and the chart's two lines cannot be told apart.
   The console's whole idea, a colour per component, does not exist on this palette. Measured across
   all ten palettes (CIE76 ΔE, smallest distance among the four component colours):

   | Palette | Smallest ΔE | Between |
   | --- | --- | --- |
   | Midnight | 46.1 | graphics, memory |
   | Custom (built) | 37.2 | processor, memory |
   | OLED Black | 27.2 | processor, graphics |
   | Ember | 26.5 | processor, fans |
   | Sandstone | 22.6 | processor, graphics |
   | Graphite | 12.8 | processor, graphics |
   | Nord | 9.6 | processor, graphics |
   | Mono | 8.0 | processor, graphics |
   | Midnight Blue | **0.0** | processor, fans — the same value |
   | Terminal | **0.0** | processor, memory — the same value |

   CIE76 flatters the cyans — 27 reads as "the same colour" on screen — so the floor is set at **30**.
2. **At 1,920 px the reference's arrangement goes empty.** Two cards a side and a centre twice a card's
   width gives cards 400 px wide with nothing between name and figure, and an 820 × 600 dial panel with
   the ring in its middle. The reference's cards and centre are *portrait* (0.65 and 0.66 wide to tall);
   on a 1.85 : 1 screen the two-by-two arrangement makes both landscape.
3. **Headings are set in a terminal face.** OLED Black's `HeadingFont` is Cascadia Mono, so every page
   title — "Fan control", "Tuning", "Battery" — is heavy monospace, while the reference sets its title in
   a sans.

### The looks

`UiLook` becomes three, and the section 10 console is kept exactly as it is:

| Look | What it is |
| --- | --- |
| **Studio** (default) | The console, arranged for the screen and carrying its history — below |
| **Console** | Section 10, unchanged: two cards a side, the reference's proportions |
| **Classic** | Section 8: the widget row, the four history strips, the readout bar (was "Previous") |

The values are renumbered — Studio 0, Classic 1, Console 2 — so the stored default moves to Studio:
the console look never shipped in a release, and its only stored `0` is the default on this machine.

### Studio

- **Wide: one row.** At 1,400 px of content and more, the four cards and the dial stand in one row —
  processor, memory, dial, graphics, sensors — every card portrait again (about 300 × 520 at 1,920)
  and the dial in a portrait panel, which is the reference's proportion kept on a wide screen rather
  than its arrangement stretched. The live chart is then on the first screen with room to spare.
  Below 1,400 the two-by-two of the console; below 900, stacked. The console grid becomes nine columns
  so one `Reflow` places all three.
- **Every card carries its history.** The space between a card's name and its figure — empty in the
  reference, and very empty at 1,920 — holds the last five minutes of the card's reading as a trace
  against the **same full scale as its bar**: processor and graphics load against 100 %, memory in use
  against installed, the two temperatures against their hot points. A trace with no real top is not
  drawn. It is the project's rule that history sits beside every current value, in the form the user
  asked for: drawn, not written.
- **A sans for headings.** The look sets `HeadingFont` over the palette's, so a palette decides colour
  and Studio decides type; the Console and Classic looks keep each palette's own face.

### Colour identity, in every palette

Not a Studio feature: a defect in nine palettes, fixed in all of them, and held by a test.

| Palette | Processor | Graphics | Memory | Fans | Smallest ΔE |
| --- | --- | --- | --- | --- | --- |
| OLED Black | `#FF4F8B` | `#22D3EE` | `#22C55E` | `#F5A524` | 72.2 |
| Midnight Blue | `#4D8DF7` | `#22D3EE` | `#22C55E` | `#F2A33A` | 59.9 |
| Ember | `#E4673B` | `#F5C542` | `#57C48A` | `#6FB3D2` | 52.0 |
| Graphite | `#E07A8F` | `#6FB7D8` | `#22C55E` | `#D4A24C` | 55.2 |
| Mono | `#FFFFFF` | `#9E9E9E` | `#22C55E` | `#D9A441` | 34.9 |
| Nord | `#88C0D0` | `#C3A0BD` | `#A3BE8C` | `#DC9A82` | above 30 |
| Sandstone | `#EE8266` | `#4EA8C4` | `#8AB17D` | `#E9C46A` | above 30 |
| Terminal | `#3FB950` | `#58A6FF` | `#D2A8FF` | `#FFA657` | 32.7 |

`Contrast.Difference` (CIE76, in Core) and `ComponentColourTests` assert the floor for every palette
file and for `CustomPalette`'s output. The fan colour joins the text-contrast set in `ContrastTests`,
because the top bar and the sensor card now draw figures in it.

The tests found three more things on their first runs. `CustomPalette` on a light ground darkened its
blue and violet seeds until they met at 29.8, so its seeds are now the reference's four. Sandstone's
new processor colour and Nord's new graphics colour sat at 4.30 and 4.35 : 1 on the tinted surface and
were lightened a step. And Nord's fan colour, which the console's top bar had already been drawing as
text, was below 4.5 : 1 there too — a shipped defect the old text list could not see.

### Structure of the change

- `Controls/Curves.cs` — the curve drawing `LiveChart` had inline (decimate, spline, line and filled
  area, the gap each series tolerates), shared with the new `Trace`, so the two cannot drift.
- `Controls/Trace.cs` — a card's sparkline: series, a real top, subscribed while loaded only.
- `ComponentCard.SetTrace` — the trace in the card's free row.
- `DashboardView(..., studio)` — one flag; the Console look constructs the same view without it.
- `MainWindow` — one `ConsoleChrome` test for the chrome the Studio and Console looks share; `ApplyLook`
  sets or clears the heading face.
- Snapshot tool — `look=Studio|Console|Classic`, and `hide=<Name>` so a render can leave out the
  readiness card the sandbox always shows and the user's install never does.

### What the user did next, and what it changed

`settings.json` was written at 12:40:09, four minutes after the section 10 deploy: the look was set to
**Previous** — the classic look — and left there. Seen side by side with the console, the user chose the
denser screen: six readings with traces across the top, each page's state under its sidebar name, four
history strips with their values and times. It is the same preference already on record ("boring" means
under-informed), and it settles what Studio must be: at least as informative as Classic, in the
reference's look. So Studio also takes from Classic:

| From Classic | In Studio |
| --- | --- |
| Six readings, each with its trace, across the top | `ConsoleBar(dense: true)`: die and GPU temperature, fans, package and GPU power, CPU load, each trace in its component's colour; traces drop below 1,250 px, the last two readings below 1,000 |
| The state under each sidebar name | The console sidebar keeps its style and gains the second line |
| Four strips on one clock, 5/15/30 minutes, values and times | `LiveChart(detailed: true)`: a fourth subject for the fans against their measured maximum, the 5/15/30-minute window, two or three labelled rules, clock times under the plot |
| A fan that turns with the real one | The cooling card's icon is the `FanGlyph` again |

The Console look keeps none of these: it is the section 10 console, kept as the user asked.

The deploy moves the stored look to Studio. The user's Classic choice was made between the two looks
that existed; Studio is the answer to the request that followed it, and a default nobody sees is the
failure recorded in the project's notes on this user. Classic stays one click away, and the message
that announces the deploy says so.

### Verification

Rendered as the user sees it — OLED Black, 1,920 × 1,040, the readiness card the sandbox raises hidden
(`hide=`), and 90 to 100 seconds of real readings gathered first (`wait=`), since the log replay has no
load or memory columns — and at 1,440 × 900, where Studio falls back to two a side; the Console look at
1,920 to confirm it is unchanged. 1,003 tests pass; `ComponentColourTests` fails with OLED Black's old
processor colour put back.
