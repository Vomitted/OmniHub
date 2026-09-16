# OmniHub — Technical Document v2

**The target specification for the "Instrument" release.**

Written against the graph mapping (`graphify-out/graph.json`, 2,667 nodes / 5,190 edges / 138
communities) and against [TECHNICAL-v1.md](TECHNICAL-v1.md), which describes the system as
built. Where this document says "today", it means v1.

---

## 1. The delta, in one paragraph

OmniHub can change this machine in a dozen ways and can barely show what any of those changes
did. It writes six logs and reads none of them back. The whole application contains one
time-series chart, on one tab, showing one metric over a fixed two-minute window, with no time
axis and no ability to draw a second series. This release makes it the instrument that proves
whether its own controls did anything — and rebuilds the shell so that the readings can be
arranged by the person reading them rather than by whoever laid out the tab.

Two things follow from the graph that shape how it is done. `TuningView` (76 edges) and
`MainWindow` (68 edges) are the most connected nodes in the codebase, so every package touches
them and neither can be left as it is. And `OmniHub.Tests` cannot reference `OmniHub.App`, so
**every piece of new logic that needs a test must live in `OmniHub.Core`** and the WPF side must
be a renderer over it.

---

## 2. Invariants

These hold before and after. They are the acceptance criteria for the release as a whole, not
aspirations.

1. **No synthesized telemetry.** Every new readout names its source and renders "unavailable"
   when the hardware did not answer. The nine mechanisms in v1 §6 are the vocabulary; new code
   picks from them rather than inventing a tenth.
2. **`PROCTHROTTLEMIN`, `PROCTHROTTLEMAX` and `PERFBOOSTMODE` are never written.** Enforced by
   `PowerPlanSetup.NeverWrite` and by a test that scans every file under `Optimize/`.
3. **Nothing existing is removed** except code with zero call sites (`CircularGauge`,
   `StatTile`, `TrackStyle`/`TrackFillStyle`), and `WaveformChart` — that one only after its
   replacement has shipped on a different screen, in a separate commit.
4. **Every new behaviour defaults to today's behaviour.** A user who changes nothing sees the
   same machine behaviour.
5. **One package, one commit.** Any single package can be reverted without taking the others.
6. **The test suite only grows**, and every new test is verified by injecting the defect it
   names and watching it fail.
7. **A control's home is the hardware it drives.** Panels are arrangeable; that rule is not.

---

## 3. New Core surface

All of it in `OmniHub.Core`, all of it testable without a laptop.

### 3.1 `OmniHub.Core.Telemetry`

| Type | Responsibility |
| --- | --- |
| `TelemetrySamples` | `ThermalSample`, `NetworkSample`, `PowerEvent`, `PollTimingSample`, `LoadRunSample`, `FrameTimeSample`. Nullable wherever the CSV can be empty. |
| `CsvLogReader` | Streams rows keyed by **the file's own header**. Never by position. |
| `TelemetryHistory` | Time-range queries across day-boundary and `-N` roll files; coverage bounds; read warnings. |
| `SampleGaps` | Gap detection with the threshold derived from the data's own median interval. |
| `TimeSeriesDecimator` | Min/max bucketing to a pixel-column budget, split at gaps, returning **segments**. |
| `TimeAxis` | Round tick instants from a fixed step ladder; label format from the span. |
| `Session`, `SessionStore`, `SessionCompare` | A session is a name and a UTC time range. Comparison by moving-block bootstrap. |

Four facts about the files on disk decide `CsvLogReader`'s design, all verified directly:

- **Four thermal header layouts exist right now** — 8, 9, 13 and 15 columns. Parse by header
  name; a positional reader shifts `mode` into `sensor` and looks plausible doing it.
- **`thermal-*.csv` carries a UTF-8 BOM and the others do not.** `ThermalLog` uses
  `new StreamWriter(path, append, Encoding.UTF8)`; the rest wrap a `FileStream`.
- **`-1` is a sentinel, not a value** — 8,694 of them in one day's file. `forecast_c = -1` means
  prediction was off; `commanded_pct = -1` means the service had not commanded.
- **`ThermalLog` holds the file `FileAccess.Write, FileShare.Read`**, so a reader must open
  `FileAccess.Read, FileShare.ReadWrite`. A torn final row is normal — the writer flushes on a
  10-second interval — so a bad last line is skipped and counted, never thrown.

**Decimation is min/max, not largest-triangle-three-buckets.** LTTB renders beautifully and drops
real extremes. On this data a dropped extreme is a thermal excursion that did not happen, which
is the same class of error as the Catmull-Rom overshoot `WaveformChart` went out of its way to
avoid. Min/max per column costs one extra point and cannot hide a spike.

**Gaps are segments, not a `NaN` sentinel.** WPF mishandles `NaN` inside a figure, and this
codebase already shows NaN sentinels get forgotten at boundaries — `Reading.PreciseTemperatureC`
needs an explicit `IsNaN` guard at every consumer. `thermal-2026-09-15.csv` alone contains holes
of 21,512 s, 3,000 s, 1,810 s, 1,539 s and 1,068 s; a line drawn across the largest would span a
quarter of a one-day chart on invented data.

### 3.2 `OmniHub.Core.Diagnostics` additions

`FrameTimeMonitor`, `FrameTimeStats`, `FrameTimeLog`, `StabilityHistory`.

Frame timing consumes `Microsoft-Windows-DxgKrnl` through a real-time ETW session. All four
relevant providers are present on this machine and 37 of the platform's 64 session slots are in
use, so a session is available. The application already runs elevated.

Four requirements, each of which the implementation fails without:

- **It measures present-to-present intervals — frame *pacing*, not displayed frame time.**
  Correlating present through to scanout needs a per-present state machine over
  Flip/MMIOFlip/VSyncDPC with per-vendor quirks. The UI says **"frame time (present)"** and does
  not claim dropped frames, tearing, v-sync state, GPU render time or click-to-photon.
- **The 1% low is the mean frame rate of the worst 1% of frames**, not the 99th-percentile
  frametime converted to FPS. Quoting one under the other's name is the commonest benchmark
  error there is.
- **Snapshots are pulled, not pushed.** `OnReading` works because it fires 0.43 times a second;
  at 165 Hz a `Dispatcher.BeginInvoke` per frame queues faster than the UI drains it. Events land
  in a fixed ring on a dedicated ETW thread; the UI reads a snapshot at 4–10 Hz.
- **An orphaned ETW session outlives a crash.** Fixed session name `OmniHub-FrameTime` — a GUID
  suffix would be worse, because after a crash you could not find it — small buffers, and a
  reclaim sweep at application startup whether or not the feature is ever opened.

Default **off**. Measuring costs a little power on a thermally limited laptop and the UI says so.

> **Dependency risk.** This needs `Microsoft.Diagnostics.Tracing.TraceEvent`: the first
> `PackageReference` outside `System.Management`, ~20 MB unpacked, shipping native binaries that
> `installer/OmniHub.iss`'s `Source: "{#SourceDir}\*"` glob picks up silently. The alternative is
> 600–900 lines of untestable unsafe interop. The package is the right call, and it is why this
> is the **last** package built: everything above it ships and is useful with no frame-time data
> at all.

### 3.3 `OmniHub.Core.Workspaces`

The layout model. In Core because `OmniHub.Tests` cannot reference `OmniHub.App`, so a model in
the App project would be untestable by construction — and this is the one piece of new state a
user can corrupt by hand.

`Workspace` (name, ordinal, grid of `PanelInstance`), `PanelInstance` (type id, column, row,
width, height, per-panel settings), `WorkspaceStore` (load, save, defaults, export/import).

---

## 4. Data, new and changed

### 4.1 `workspaces.json`

`%AppData%\OmniHub\workspaces.json`, written through `AtomicFile`. Carries a schema version.

Three rules, each because a layout file is the one thing here a user will hand-edit:

- An **unknown panel type** renders as a placeholder naming it. Never an exception.
- A **corrupt or truncated file** falls back to the shipped defaults, exactly as
  `AppSettings.Load` already does.
- A file written by a **newer** build still opens; unknown fields are carried through rather than
  discarded, so switching versions does not silently destroy a layout.

### 4.2 `frametime-YYYY-MM-DD.csv`

**One row per second, not per frame.** Per-frame at 165 Hz is 14 million rows a day, which is
not a log, it is a disk-filling bug. Uses `NetworkLog`'s `StreamWriter(FileStream)` shape so it
does not inherit `ThermalLog`'s BOM. Subject to the shared 14-day retention.

Columns: `timestamp,process,frames,fps_avg,p50_ms,p95_ms,p99_ms,low1_fps,low01_fps,stutters`.

### 4.3 `sessions.json`

A session is **a name and a UTC time range** — not a new artefact format. All five telemetry
streams already persist continuously with fourteen days of history; a bundle would duplicate the
bytes, add a schema to version, and only work forward. A time range works retroactively on
everything already on disk, and costs about forty lines.

### 4.4 Changed: `loadtest-*.csv` gains a `timestamp` column

It has **no wall-clock column** today, only `elapsed_s`, and its filename is written from
`DateTime.Now` — local time, while every other log is UTC. Aligning a load run against the
thermal log is therefore wrong by the UTC offset, and wrong differently across a DST boundary.
Old files still resolve by filename; new ones carry the column.

### 4.5 Changed: `ThermalLog.PruneOldLogs` must respect saved sessions

Retention deletes `thermal-*.csv` after fourteen days. A session pointing at an older range would
silently resolve to nothing — an A/B comparison that forgets A. Prune skips any file inside a
saved session's range.

---

## 5. The shell

Seven fixed tabs become **workspaces built from panels**. That is a different application to use,
it is customizable by construction rather than by adding options to a fixed design, and it is the
only reading of "completely different" that does not break invariant 3 — because every existing
view survives *as a panel* and the default workspaces reproduce today's tabs.

**Composite panels** wrap each existing view with its current constructor unchanged
(`ctx, service, settings`). This is what makes the change non-breaking.

**Atomic panels** are the new vocabulary and the reason the rebuild is worth doing: a single
Metric, a Chart with its series chosen per instance, the Limit strip, Limits-over-time, a Sensor
table in the documentation register, Frame time, Fan curve, Process power, Sleep blockers,
Stability timeline, Session compare, Log tail, Notes.

**Default workspaces**: Overview, Cooling, Performance, Battery, System, Measure — laid out to
match today's tabs — plus Gaming and Forensics, which only the new structure makes possible.

**Theme.** The palette schema already carries `RadiusSm`, `RadiusMd`, `CardPadding`,
`HeadingFont`, `TrackHeight` and `MetricValueSize`, so a user-owned ninth palette is mostly
plumbing that exists. Two guards, both already built: every custom palette is run through the
same WCAG check `ContrastTests` enforces and refused with the failing pair named; and colour
still never lands on prose.

---

## 6. Changes to what exists

| Node | Change |
| --- | --- |
| `MainWindow` (68 edges) | Loses the entire navigation block to the workspace shell. Tray and overlay wiring move to their own collaborators. The cleanup ordering is load-bearing and documented — it moves as a unit or not at all. |
| `TuningView` (76 edges) | Roughly forty controls built imperatively into twenty empty named `StackPanel`s. `KnobRow` already shows the way out; extend that pattern to the limit rows, the preset row, the game-rule list and the capability rows. |
| `GpuView` | Shows no telemetry at all today — temperature, power, clock and utilisation are read and land only in Tuning's live rows and the overlay. They move to the tab for the hardware they belong to, with the source named. |
| `DiagnosticsView` | Becomes the Measure workspace: a sensor table naming every source, plus History, Sessions, Frame time, Load test, Stability, Probe. |
| `PowerView` | 104 XAML lines today. Rebuilt around measured discharge, per-process attribution, the rest-of-system figure, and discharge history. |
| `OverlayWindow` | `AvailableMetrics` is a static eight-entry list. Becomes the same panel vocabulary at miniature scale. |
| `Program.WriteRun` / `DiagnosticsView.WriteRun` | Duplicated verbatim, header string included. Moves to Core, which §4.2 needs anyway. |

---

## 7. Readings that exist and are not shown

Cheapest value in the release: the hard part — talking to the firmware — is done.

- `FanService.LastReadTempC`, `LastEffectiveTempC`, `SensorCeilingReached`, `TemperatureSource`
  and `LastError` are assigned every tick and read by nothing. The second is the *predicted*
  temperature the curve acted on, so a user-settable feature has an invisible effect.
- `GetAdapter` returns `HpAdapterStatus.BelowRequirement` — a direct "this charger is underpowered
  for this laptop" diagnosis — and reaches no view.
- `GetFanType` names each fan; the UI says "fan 1" and "fan 2".
- `GetFanTable` returns **the BIOS's own fan curve**, printed as raw hex and never parsed. Drawn
  on the same axes as the user's curve it answers "what am I actually changing?" for the first
  time.
- `PowerSnapshot` carries 22 mapped fields; Tuning renders 10. `SocThermalLimitC`,
  `GfxThermalLimitC` and the APU slow pair have no consumer.
- `GpuSource`'s own doc comment says *"Shown in the UI, because the two sources differ"*. It has
  zero references in `OmniHub.App`.
- `CallNtPowerInformation(ProcessorPowerInformation)` gives **per-logical-processor** clocks with
  no driver and no WMI, against the one `CurrentClockSpeed` shown today whose own source warns it
  may not track turbo.
- `powercfg /requests` names what is holding a power request right now. It needs elevation, which
  this application has.

---

## 8. Out of scope, explicitly

- **Per-game automatic profiles, keyboard/RGB lighting, a battery charge limit.** Offered and
  declined twice in favour of things that change what the machine does under load.
  `HpKeyboardType.PerKeyRgb` is already detected and stays unused.
- **A fix for the freezes.** Nine unexpected shutdowns in six days and no established cause. The
  stability work builds the instrument; it does not claim a diagnosis.
- **Displayed frame time, dropped frames, click-to-photon.** Not derivable from present events.
- **Purging `v11.zip` from git history.** Rewriting and force-pushing a public repository is the
  owner's call.
- **A release.** This lands on `main` and is deployed to the install. The public download stays
  at 1.3.3 and `docs/index.html` keeps describing 1.3.3 honestly.

---

## 9. Done means

- `dotnet build -c Release` clean and `dotnet test` green after every package, with each new
  test verified by injecting the defect it names.
- The golden path walked on every default workspace: every control that existed on the old tabs
  is present, in the same panel, still reading back from the hardware.
- Chart output spot-checked against the raw CSV rows at three timestamps, and the 14 September
  hole rendering as a **gap**.
- Frame-time FPS compared against an independent reading, with the delta recorded; no orphaned
  ETW session after a kill.
- The A/B comparison returning "indistinguishable" for two samples of the same process in at
  least 95 of 100 trials — the test for the whole feature, since a tool that invents a winner is
  worse than no tool.
- Thermal-log statistics diffed across the release: mean fan duty, mean die temperature and the
  fraction of samples above the curve unchanged unless a package intended to move them.
