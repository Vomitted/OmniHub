# OmniHub — Technical Document v1

**The system as it is built, at commit `b0c9513`.**

This describes what exists, not what is planned. Where something is unverified, this says so;
where a number was measured, this says on what. It is the input to the graph mapping, and the
baseline the v2 specification is written against.

Not in `docs/` on purpose: `wrangler.toml` publishes that directory as-is to both Cloudflare
Pages and GitHub Pages, so anything dropped there becomes a public web page.

---

## 1. What this is, and the rule everything else follows

OmniHub replaces HP's Omen Gaming Hub on Windows laptops. It drives the same `hpqBIntM` BIOS
WMI interface OGH, OmenMon and OmenCore use, plus AMD SMU tuning through the PawnIO driver.

It exists because Default/Balanced BIOS fan mode on some HP Omen and Victus laptops has a real
defect: the fan table carries a 0% idle entry that can be reached while the machine is still
genuinely hot, so the fan stops while the laptop overheats. Auto (Curve) mode fixes that with a
safety floor — past a configurable temperature the fan is never allowed to command 0% again.

**The governing rule: it does not synthesize telemetry.** No invented fan RPM, no workload
classification, no plausible-looking number standing in for a reading the hardware could not
give. A value that cannot be read renders as unavailable. This is not a style preference; it is
enforced by the type system, tested, and the reason several features are smaller than they
could be. Section 6 describes the mechanism.

Three further rules constrain the code:

- **The Windows processor power settings are not written.** `PROCTHROTTLEMIN`,
  `PROCTHROTTLEMAX` and `PERFBOOSTMODE` are never set by any route. `PowerPlanSetup.NeverWrite`
  is the backstop and `PowerPlanSetupTests` fails the build if any of the three GUIDs appears in
  a write path under `Optimize/`.
- **A control lives with the hardware it drives**, not with whichever view already had the
  plumbing.
- **Colour is not applied to prose.** It belongs to chips, dots, bars, gauges and chart lines,
  and to threshold-coloured numeric readouts.

### Target machine

Developed and measured on an **HP Victus 15 fb2999ax**: Ryzen 5 8645HS ("Phoenix"), RTX 4060
Laptop, 1920×1080 144 Hz panel, baseboard `8C2F`. Every measured figure in this document comes
from that machine unless stated otherwise. The application starts and runs on any Windows
laptop; missing vendor support disables the controls that need it and the dashboard names what
is unavailable and why.

---

## 2. Projects and layering

| Project | Target | Contents |
| --- | --- | --- |
| `OmniHub.Core` | `net8.0-windows`, x64 | Hardware access, fan control, tuning, networking, diagnostics. One package reference: `System.Management`. |
| `OmniHub.App` | `net8.0-windows`, WinExe | WPF shell. `UseWPF` **and** `UseWindowsForms` (the tray icon). Zero package references. `requireAdministrator`. |
| `OmniHub.Tests` | `net8.0-windows` | xunit, 303 tests. |

Two constraints follow from this layout and shape a great deal of the code:

**`OmniHub.Tests` references Core only, never App.** The App project's build output *is* the
installed application — the desktop shortcut, the Start Menu entry and the `OmniHub_AutoStart`
scheduled task all point at `bin\Release\net8.0-windows\OmniHub.exe` — so referencing it would
make `dotnet test` fail whenever OmniHub is running. Running the suite must not require somebody
to shut down their fan control. **Consequence: any logic that needs testing has to live in
Core.** Markup that declares a type from the App assembly is checked as text rather than parsed.

**`OmniHub.App` sets both `UseWindowsForms` and `UseWPF`**, so several type names exist in both
stacks and a bare reference is ambiguous. `Brush`, `Color`, `Colors`, `SolidColorBrush`,
`FontFamily`, `Point` and `Size` are aliased globally in the csproj. This broke the build three
separate times before the aliases were made global.

---

## 3. Hardware access

### 3.1 Transport — the single WMI connection

`Hardware/BiosWmi.cs` is constants only: namespace `root\wmi`, class `hpqBIntM`, instance
`ACPI\PNP0C14\0_0`, signature `"SECU"`. It declares the command groups (`Default = 0x20008`,
`Keyboard`, `Legacy`, `GpuMode`) and every command ID.

`Hardware/BiosInterop.cs` owns the connection. A non-HP machine is a supported state, not a
crash: construction failures set `IsAvailable = false` and `UnavailableReason`.

Three details are load-bearing and were each paid for once:

- The `hpqBDataIn` `ManagementClass` is **cached**. Fetching the class definition was a DCOM
  round trip on every `Send()`, two to three times a second.
- `_sendLock` serialises every BIOS call. The poll timer and the fan curve both call
  `GetMethodParameters`/`InvokeMethod` on one `ManagementObject`, which is not thread-safe.
- The embedded `outData` is **deliberately not disposed**. A `using` there double-released the
  COM wrapper and killed the process silently within seconds.

Response size selects the method: `hpqBIOSInt0/4/128/1024/4096`.

### 3.2 Controllers

`Hardware/BiosCommands.cs` holds four:

- **`GpuController`** — graphics mode (`0x52`), GPU power read/write (`0x21`/`0x22`), power
  presets. One-second read cache, invalidated after every write. A `ForceMaxPower` latch stops
  any caller lowering the TGP ceiling.
- **`PowerController`** — CPU sustained/boost watts (`0x29`), clamped 10–140 W, and the firmware
  idle state (`0x31`). *The two wattage methods currently have no caller: the PL1/PL4 sliders
  were removed.* There is no corresponding Get, so the idle toggle cannot reflect hardware state.
- **`SystemController`** — temperature arbitration, system data (`0x28`), adapter status
  (`0x0F`), keyboard type (`0x2B`), max-fan (`0x26`/`0x27`), throttling (`0x35`).
- **`FanController`** (own file) — count, type, level, mode, table, and
  `RestoreAutomaticControl()`.

### 3.3 Sensors

| Source | Mechanism | Notes |
| --- | --- | --- |
| **AMD SMU** (`RyzenSmu.cs`) | PawnIO bytecode module `RyzenSMU.bin` | Tctl from `THM_TCON_CUR_TMP`, decoded `((raw>>21)&0x7FF)*0.125` with a −49 offset on bit 19. Implausible values return **null**, not a number. |
| **PM table** (`RyzenSmu.cs`) | MP1 mailbox | 22 mapped fields from a 1024-word table, gated on version `0x004C0009`. Cached **5 s** — the refresh holds the global `Access_PCI` mutex and shows up as DPC latency and audio dropouts. |
| **ACPI thermal zone** | WMI `MSAcpi_ThermalZoneTemperature` | Tenths of Kelvin, max across zones, 6 s cache. **Throws** on zero rows rather than reporting 0 °C. |
| **GPU** (`GpuTelemetry.cs`) | `nvidia-smi` preferred, WMI fallback | Process spawn measured at **56 ms**, hence a 3 s cache and a background single-flight. On battery it is skipped entirely — the PCIe conversation wakes the card out of D3 and costs 12–14 W. |
| **GPU D-state** (`GpuPowerState.cs`) | `cfgmgr32` `DEVPKEY_Device_PowerData` | Host-side bookkeeping only, no PCIe transaction. **Known stale**: it does not track the runtime D3 transitions an NVIDIA driver manages itself. |
| **CPU/memory** (`SystemPerfInfo.cs`) | WMI perf counters + `GlobalMemoryStatusEx` | Measured **1.29 WMI queries/second** with the dashboard open. `CurrentClockSpeed` is not guaranteed to track turbo. |
| **Battery** (`BatteryInfo.cs`) | `Win32_Battery`, `root\wmi Battery*` | Live draw lives separately in `BatterySaver.ReadDraw()`. |

### 3.4 Temperature arbitration

`SystemController.Merge(die, zone)` is static and pure, and is the rule the whole thermal
picture rests on. The ACPI zone saturates at **85 °C** (`SensorCeilingC`), so a saturated zone
must never outvote a real Tctl reading — but an in-range zone that is genuinely hotter still
wins. `TemperatureReading.IsCeilingLimited` is a property of the *source*, not of the number, so
a genuine 85.0 °C die reading is not mistaken for a blind sensor.

---

## 4. The polling loop

`HardwareContext.StartPolling(TimeSpan)`, started at **2 s** from `MainWindow`.

- A `System.Threading.Timer` with period `Infinite`, re-armed by the callback in `finally`, so a
  long tick cannot stack pollers. A re-entrancy interlock guards the body.
- The timer is created stopped and captured in a local before being started. A zero due-time in
  the constructor let the callback reach its re-arm line while the field was still null, which
  silently stopped the loop after one reading.
- **Every fifth tick** additionally reads fan level, max-fan state and throttling. The fan
  readback through `hpqBIOSInt128` was measured at **306 ms of a 324 ms tick — 94%** — and
  nothing steers on it.
- Tick body ≈ **300 ms**. Measured over **200,169 rows** of this machine's own thermal trace
  (19 files, 2–16 September), the interval within a run is **2.00 s median, 2.23 s mean**, with
  p90 and p99 both at **3.00 s**. The loop is not systematically late: it hits 2 s on the
  ordinary tick, and the every-fifth-tick slow path costs about a second more, which is what
  pulls the mean above the median. An earlier figure of 2.31 s came from a much smaller sample
  and was a mean quoted without that distinction.
- Timing is averaged over 30 ticks (~70 s) into `polltiming-*.csv`.

### `Reading`

```csharp
public sealed record Reading(
    byte TemperatureC, byte FanLevel1, byte FanLevel2,
    bool MaxFanActive, ThrottlingState Throttling,
    double PreciseTemperatureC = double.NaN,
    TemperatureSource TemperatureSource = TemperatureSource.AcpiThermalZone);
```

The last two carry defaults so an older-shaped `Reading` still constructs; consumers test
`double.IsNaN(PreciseTemperatureC)` and fall back.

`OnReading` handlers run **on the poll thread while it holds the interlock**, so a synchronous
`Dispatcher.Invoke` parks the temperature poll — and the UI thread can block unboundedly on a
modal dialog. Every per-tick handler therefore uses `BeginInvoke`, and expensive reads are
hoisted *before* the marshal. Dispatch is per-handler in its own `try`: a plain `?.Invoke`
stopped at the first thrower and silently disabled every later subscriber.

---

## 5. Fan control

`Fan/FanCurve.cs` evaluates in three stages: interpolate, apply the safety floor, then apply
ramp-down hysteresis.

- Default points: `(0,0) (45,0) (55,12) (65,28) (75,52) (82,75) (88,100)`.
- Safety floor: past `FloorTempC` (55 °C) the level is never below `FloorLevelPercent` (15%).
- **Hysteresis applies on the way down only.** `RampDownDeadbandC = 4.0` and `MaxStepDown = 10`.
  The anchor moves **only when the level rises** — the version that moved it on every evaluation
  was a staircase ratchet that left 48% of samples ten or more points above the curve. Fixing it
  moved mean fan duty 41.7% → 34.1%, and 21.3% → 7.9% while the die was under 55 °C.
- Both knobs are `init`-only and **no caller sets them**; only the floor is user-configurable.

`Fan/FanService.cs` is the loop, at 2 s:

- The control temperature is `max(CPU, dGPU)` — they share the fans.
- Optional predictive lead takes `max(measured, forecast)`.
- 100% is forced only after **five consecutive** ceiling-limited readings (≈12 s), which
  suppresses the "jet engine every boot" a cold, uninitialised thermal zone used to cause.
- Writes only on change or every 30 s, and **re-asserts `SetFanMode(Performance)` on every
  write** — the missing half of "the fans stop and only a restart brings them back".
- `Stop()` calls `RestoreAutomaticControl()`, which is why the application must be exited
  through the tray rather than killed.

### The measured fan band

The EC's fan byte is an **RPM/100 target**, not a duty cycle, and the usable range is a property
of the chassis. On board `8C2F`: readback tracks the command to **54** and pins at **56**; the
floor is **10** (1000 rpm), not 20; raw 0 hands the fans back to the BIOS; raw 255 is OmenMon's
*release* sentinel, not maximum. `FanProfiles` loads and saves `profiles/<baseboard>.json`.

### Modes

`Auto` (curve owns the fans) · `BiosDefault` (service stopped, mode returned to Default — the UI
warns it may idle at 0% while hot) · `Max`. `FansView.ApplyMode` is the single choke point for
the mode buttons, the tray menu and the saved-mode restore.

---

## 6. How "unavailable" is represented

This is the rule from section 1, made mechanical. Nine distinct devices are in use:

1. **Nullable returns** mean "not measured" — `GpuReading.TempC`, `RyzenSmu.ReadDieTemperatureC`,
   `BatterySaver.ReadDraw`, every field of `LoadSample`.
2. **Throw rather than return stale.** `CurrentTemperature()` throws if no reading exists or the
   last is older than 8 s. `ReadAcpiZoneC` throws on zero zones rather than returning 0 °C.
3. **A dedicated `Unknown` enum member**, never collapsed into a real one — `ThrottlingState`,
   `DevicePowerState`, `PowerSource`, `AmdCodeName`.
4. **Tri-state `bool?`** where absent ≠ off — `GamingToggle.Enabled`, `HasKeyboardBacklight`,
   `TuningReport.Verified`.
5. **A distinct sentinel object**, not a zero-valued one — `LatencyStats.None` with `IsEmpty`.
6. **A separate "the sensor stopped measuring" flag** — `IsCeilingLimited`.
7. **A separate "has this ever been computed" flag** where 0 is legal — `FanService.HasCommanded`,
   because 0% is a real fan level, so the log writes `-1` instead.
8. **In CSV, an empty field, never a zero** — a lost network probe writes nothing in `rtt_ms` and
   sets a separate `lost` column.
9. **Reasons, not just nulls** — `SmuUnavailableReason`, `VendorUnavailableReason`,
   `AmdTuning.UnsupportedReason`, `FanService.LastError`, `NetworkMonitor.LastError`.

---

## 7. The data layer

There is no SQL database. State lives in five places.

### 7.1 `settings.json`

`%AppData%\OmniHub\settings.json`, `System.Text.Json`, indented. **44 properties** on
`AppSettings` covering fan mode and curve, tray and close behaviour, predictive lead, thermal and
network logging, theme, timer/MMCSS, power-plan automation and bound scheme GUIDs, overlay layout
and metrics, custom tuning profiles, per-game rules, startup profile, GPU max power, thermal
limit, tuning mode, adaptive controller rails, and Auto Eco.

Written through `AtomicFile` — temp file, flush **through** the OS cache to disk, then
`File.Replace`. It was a plain `File.WriteAllText`, which truncates first and writes second; with
42 `Save()` call sites and a machine that records unexpected shutdowns most days, losing that
race left an empty file, a caught parse failure, and a silent reset to defaults.

`Load()` falls back to a default instance on any failure, so a corrupt file never blocks startup.

### 7.2 CSV logs — `%AppData%\OmniHub\logs`

| File | Header | Cadence | Flush | Retention |
| --- | --- | --- | --- | --- |
| `thermal-YYYY-MM-DD.csv` | `timestamp,temp_c,forecast_c,fan1_raw,fan2_raw,commanded_pct,throttling,mode,sensor` | 2.00 s median | 10 s | 14 days |
| `network-YYYY-MM-DD.csv` | `timestamp,target,rtt_ms,lost,avg_ms,jitter_ms,loss_pct` | 5 s default | 10 s | 14 days |
| `power-YYYY-MM-DD.csv` | `timestamp,source,event,detail,action` | on transition | **every row** | 14 days |
| `polltiming-YYYY-MM-DD.csv` | `timestamp,ticks,avg_total_ms,avg_temp_ms,avg_fan_ms,avg_slow_ms,avg_dispatch_ms` | 30 ticks | per row | 14 days |
| `loadtest-YYYY-MM-DD-HHmmss.csv` | `elapsed_s,temp_c,sensor,package_w,cpu_ghz,fan_rpm,throttling` | 2 s | — | **never** (deliberate artefact) |
| `crash.log` | free text | on exception | per write | trimmed at 2 MB, keeps last 512 KB |

All timestamps UTC, `yyyy-MM-ddTHH:mm:ssZ`, `InvariantCulture`. `thermal-*.csv` carries a UTF-8
BOM; the others do not. Today's file rolls to `-N.csv` when its header differs from the current
one, so **four column layouts (8, 9, 13 and 15 fields) exist on disk** and a reader must parse by
header name. `forecast_c` and `commanded_pct` use **`-1` as a sentinel**, not as a value.

The power log flushes on every row deliberately: these rows are written at the moment the machine
may stop executing, and a buffered row describing the transition that killed it never reaches
disk.

### 7.3 `restore.json`

Machine state a run changed and has not put back, so a crash cannot cancel the debt. Today one
key: the display refresh rate Auto Eco lowered. Written before the change, cleared after the
restore, reconciled at startup.

### 7.4 Registry

| Purpose | Key | Access |
| --- | --- | --- |
| Per-app GPU preference | `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` | read/write/delete, merge-preserving |
| HAGS | `HKLM\…\GraphicsDrivers\HwSchMode` | read/write + verify |
| Game Mode | `HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled` | read/write + verify |
| Game DVR | `HKCU\System\GameConfigStore\GameDVR_Enabled` | read/write + verify |
| PawnIO discovery | `HKLM\…\Uninstall\*` | read only, both views |

### 7.5 Elsewhere

Windows power schemes (created by duplicating Balanced, never edited in place, and **never
written with processor values**); the `OmniHub_AutoStart` scheduled task, registered from XML
whose `StopIfGoingOnBatteries=false` and `AllowHardTerminate=false` flags are the fix for Task
Scheduler killing the app on unplug; `profiles/<baseboard>.json`; update downloads under
`%TEMP%\OmniHub-update`, verified by length and, when GitHub publishes one, SHA-256 before being
moved into place.

---

## 8. The UI

A plain WPF `Window` with the OS caption bar, tinted through DWM attributes. Two columns: a
212 px sidebar of seven `RadioButton`s with hand-drawn `Canvas` icons and one travelling accent
indicator, and a `ContentControl` host.

**Seven destinations**: Dashboard · Fans · Performance (CPU + GPU via `GroupView`) · Battery ·
System (Windows + Network + App GPU routing via `GroupView`) · Diagnostics · Settings.

**There is no MVVM.** No view has a `DataContext`. Views are built with manual constructor
injection of three shared singletons — `HardwareContext`, `FanService`, `AppSettings` — and
updated by `x:Name` plus direct property assignment. `KnobRow` is the only
`INotifyPropertyChanged` model in the application.

Dashboard, Fans and GPU are constructed eagerly; the rest are lazy factories prewarmed one per
`ApplicationIdle` callback. A view whose constructor throws is replaced by a labelled failure
placeholder rather than taking down startup.

### Design system

`Palettes/*.xaml` (8 files, identical 30-key schema) → `Theme.xaml` (brushes, fonts, geometry) →
`Styles.xaml` (44 named styles). Every brush binds its colour with `DynamicResource`, which is
the whole mechanism behind live theme switching.

A palette carries more than colour: `RadiusSm`, `RadiusMd`, `CardPadding`, `HeadingFont`,
`TrackHeight` and `MetricValueSize` vary per theme, so radii range 0 (Mono) to 14 (Sandstone).
**Several styles hard-code a radius and two hard-code `White`**, which visibly disagree with the
cards around them at those extremes.

### Drawing

Everything is hand-rolled WPF geometry; there is no charting package anywhere in the solution.

- `WaveformChart` — the only time-series control. One `Queue<double>`, 60 samples, one series,
  Catmull-Rom smoothing deliberately tensioned so it cannot overshoot and invent peaks. Used once.
- `FanCurveChart` — XY plot with axes, a shaded floor region, editable handles and a live marker.
  Straight segments, because `FanCurve.Interpolate` is piecewise-linear.
- `CircularGauge` and `StatTile` — **dead code, zero call sites.**
- `TrackStyle`/`TrackFillStyle` — defined, no consumers; the Dashboard hand-rolls its four bars.

`ThermalLog`, `NetworkLog`, `PowerTransitionLog` and the load-test runs all write CSVs that
**nothing in the UI reads back or plots**.

---

## 9. Threading

Ten background loops, every one `Task.Run` + `Task.Delay` or a self-re-arming timer, every one
with its whole tick body inside a broad `try`, and every one reporting `IsRunning` from **both**
the token and `_loop.IsCompleted` — a faulted first tick otherwise leaves a loop reporting
"running" forever.

| Loop | Interval |
| --- | --- |
| Hardware poll | 2 s (2.00 s median measured) |
| Fan curve | 2 s |
| Adaptive tuning | 3 s |
| Process watch | 4 s |
| Power source | 5 s |
| Network monitor | 5 s (2–60 configurable) |
| Auto eco | 10 s |
| New-app scan | 20 s |
| GPU telemetry refresh | on demand, ≥3 s apart |
| Load test | `LongRunning`, one per core |

Locks: one per shared resource — the BIOS send lock, the PawnIO execute lock, the GPU read
cache, the PM-table cache, the MP1 probe, the thermal trend, each log writer, the network window.
`FanCurve` has none and relies on an atomic reference swap read once into a local.

**What can block the UI thread:** `MainWindow`'s constructor, which opens WMI, opens PawnIO,
reads the capability block and calls `GetFanCount()` (~300 ms), detects the model with two more
WMI queries, and constructs three views — all synchronously before the window is shown.

### Shutdown ordering, which is load-bearing

Unhook power events → dispose the app-watch timer → **dispose views first** (they own auto-eco,
adaptive tuning and two watchers, and auto-eco must restore the refresh rate) → stop the fan
service (hands the fans back to the BIOS) → **close the overlay before disposing the context**
(it reads the SMU on its own timer) → dispose context, monitor, logs → unregister the hotkey →
dispose the tray icon.

---

## 10. What is not verified

Recorded here because the project's rule cuts both ways: a reading it is not sure of should not
be presented as one it is.

- **Two incompatible `SetFanMode` payload encodings are documented, and which one this machine
  honours is unknown.** The source states it plainly: if OmniControl's is the correct one, this
  project's fan-mode switching has never taken effect.
- **`GetThrottling` is unverified.** A sweep showed `GetCapability` echoing the selector byte
  back at exactly the position this method reads, so "Default" may be an artefact rather than
  hardware state. The tray shows a throttling notification based on it.
- **`SysCmd.GetTemperature` (`0x23`) returns all-zero on this BIOS**, confirmed by direct sweep.
  Not wired up.
- **Fan count is reported, not acted on.** The payload has two spare bytes that look like room
  for fans three and four, but that is an inference from a layout, not a measurement.
- **The PM table runs to 1024 words and only 22 are mapped.** The rest certainly carry per-core
  clocks and voltages; they stay unmapped rather than guessed.
- **Acceptance is not application.** The SMU has returned success for a sustained limit reading
  0.045 W while the processor still boosted to its full 4301 MHz.
- **The suspend stand-down may be dead code.** It hangs off `PBT_APMSUSPEND`; this laptop has no
  S3 at all, only Modern Standby. Three days of power-transition logs contain **zero**
  `PBT_APMSUSPEND` rows, which suggests the handler has never run.
- **Auto Eco targets the primary display.** With an external monitor set as primary it reads and
  writes that panel's refresh rate rather than the laptop's.

### Open incident

Windows recorded **nine unexpected shutdowns between 10 and 15 September**. The freezes are not
correlated with sleep transitions — every Modern Standby entry on record exits within 26 seconds
and none coincides with a hang. Three of the four most recent occurred within ~90 seconds of a
driver installation or Windows Update servicing operation. No cause is established. The
power-transition log exists to gather evidence, not because it is suspected.

---

## 11. Figures this project actually knows

Every number below was measured on the machine in section 1.

| Quantity | Value |
| --- | --- |
| Poll tick body | ~300 ms |
| Poll interval, nominal / median / mean | 2 s / **2.00 s** / **2.23 s** (n = 199,968 intervals) |
| Poll interval p90 / p99 | **3.00 s** / **3.00 s** — the every-fifth-tick slow path |
| `GetFanLevel` via `hpqBIOSInt128` | **306 ms**, 94% of a 324 ms tick |
| `nvidia-smi` process spawn | **56 ms** |
| WMI query rate, dashboard open | **1.29 /s** |
| Fan band, board 8C2F | raw **10–56**, i.e. 1000–5600 rpm |
| ACPI zone ceiling | **85 °C** |
| Ramp-down fix: mean fan duty | 41.7% → **34.1%** |
| Ramp-down fix: mean duty below 55 °C | 21.3% → **7.9%** |
| Fans pinned at 5600 rpm vs BIOS 3000 rpm | **2.12 °C**, 0.5% throughput |
| Battery, measured by capacity delta | **29.7 W** (2,479 mWh over 5.01 min) |
| Battery design capacity / wear / cycles | 62,262 mWh / **0%** / 90 |
| Worst text contrast across 8 palettes | 3.43 → **4.51 : 1** |
| GPU woken by `nvidia-smi` on battery | **12–14 W** |
