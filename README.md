# OmniHub

A replacement for Omen Gaming Hub: fan curve control, GPU power/mode, and CPU
power limits, talking directly to the same `hpqBIntM` BIOS WMI interface OGH,
OmenMon, and OmenCore all use, plus AMD SMU tuning through the PawnIO driver.

It runs on any Windows laptop. What it can *drive* depends on the machine, and
it says which on the dashboard rather than presenting dead controls — see
[Compatibility](#compatibility).

## Why this exists

Default/Balanced BIOS fan mode on some HP Omen/Victus laptops has a real bug:
the fan table has a 0% "idle" entry that can get hit while the laptop is still
genuinely hot, so the fan stops while the machine overheats. OmniHub's Auto
(Curve) mode fixes this with a safety floor: once you're past a configurable
temperature, the fan is never allowed to command 0% again. See
`OmniHub.Core/Fan/FanCurve.cs`.

The rule the whole project is built on: **it does not synthesize telemetry.**
No invented fan RPM, no "AI" workload classification, no plausible-looking
number standing in for a reading the hardware could not give. If a value is
unavailable, the UI says so. That extends to tuning — every apply reads the
hardware back, and the tuning screen distinguishes a limit the firmware
*accepted* from one it actually *enforces*, because on some platforms the SMU
returns success for limits the vendor firmware then arbitrates away.

## Compatibility

OmniHub starts and runs on any Windows laptop. Missing vendor support disables
the controls that need it — it is never a crash, and the dashboard names what
is unavailable and why.

| Feature | Works on |
| --- | --- |
| CPU/GPU temperature, load, clocks, memory, battery health | Any laptop |
| Windows power plans, scheduling, MMCSS, timer resolution, process priority | Any laptop |
| Discrete GPU telemetry | Any NVIDIA GPU (via `nvml.dll`, falling back to `nvidia-smi`) |
| CPU tuning: power limits, thermal limit, curve optimizer | AMD Ryzen, with the PawnIO driver |
| Fan curve control, GPU TGP unlock, BIOS power limits | HP laptops exposing `hpqBIntM` |

Other vendors (Lenovo, Dell, ASUS) each expose a completely different ACPI/WMI
interface and there is no cross-vendor standard. None is shipped unverified,
because untested code that writes to unknown ACPI methods on someone's laptop
should not exist.

This file used to claim the vendor layer was "isolated behind one availability
check, so a second backend can slot in beside the HP one". That was half true,
and the false half was the load-bearing one. What is isolated is the vendor
*failure*: one honest check, a readiness card, and a machine that degrades to a
monitor instead of crashing. The *implementation* was not isolated at all --
there was no interface anywhere at the hardware boundary, and the HP types were
the types every consumer named.

Work on that is under way rather than finished. The cooling loop now drives an
`IFanBackend` (`OmniHub.Core/Vendors/`) instead of HP's controller directly, so
the one loop whose failure is a hot machine with stopped fans is vendor-neutral
and, for the first time, testable without an HP laptop. The rest -- temperature
sources, GPU power, the capability block -- is still HP-shaped, and this section
will say so until it is not.

## Installing

Download page: **https://vomitted.github.io/OmniHub/**

When a release is published it carries a setup program: self-contained, needing no
.NET runtime, and offering the PawnIO driver and a sign-in task as tick-boxes. It is
listed there with a SHA-256 to check it against. The build is not
code-signed, so SmartScreen will warn about it — that warning means Microsoft
has not seen the file vouched for, not that it has been cleared.

Building it yourself avoids that question entirely. It takes about five minutes
and you only do it once per machine.

**Before you start:** Windows 10 or 11, and about 1 GB free for the .NET SDK.
OmniHub runs on any laptop; how much of it is *usable* depends on your
hardware — see [Compatibility](#compatibility) above.

### 1. Install the .NET SDK

In PowerShell or Terminal:

```
winget install Microsoft.DotNet.SDK.10
```

Close and reopen the terminal afterwards so `dotnet` is on your PATH. Check it:

```
dotnet --version
```

The app targets `net8.0-windows`, but install the **current** SDK, not the 8.0
one: the solution file is `.slnx`, a newer format the .NET 8 SDK cannot parse.
A current SDK builds `net8.0` targets perfectly well. If you already have only
the 8.0 SDK and would rather not add another, skip the solution and build the
project directly in step 3.

### 2. Get the source

```
git clone https://github.com/Vomitted/OmniHub.git
cd OmniHub
```

No git? Download the ZIP from the repository's green **Code** button, extract
it, and `cd` into the extracted folder.

### 3. Build it

```
dotnet build OmniHub.slnx -c Release
```

On the .NET 8 SDK, build the project instead — same result, no `.slnx`:

```
dotnet build OmniHub.App\OmniHub.App.csproj -c Release
```

Expect `Build succeeded` with 0 errors. The app lands at:

```
OmniHub.App\bin\Release\net8.0-windows\OmniHub.exe
```

### 4. Run it as administrator

Double-click `OmniHub.exe` and accept the UAC prompt.

**The prompt is not optional.** Fan and BIOS control go through an elevated
WMI session, and the SMU driver refuses unelevated callers. `app.manifest`
requests elevation automatically, so you will always see the prompt — if you
launch it some other way and skip elevation, the hardware panels will report
themselves unavailable.

Make a desktop shortcut to that `.exe` if you want it handy.

### 5. Install PawnIO, for CPU tuning (optional)

Only needed for the CPU tuning screen: power limits, thermal limit, die temperature
and adaptive mode. Everything else works without it.

Easiest way: launch OmniHub and click **Install the PawnIO driver** on the
dashboard — it appears only if the driver is missing. Or do it yourself:

```
winget install namazso.PawnIO
```

No restart needed either way. OmniHub retries the driver every ten seconds
while it is missing, so tuning comes online on its own once it registers.

### 6. Start it with Windows (recommended)

Open **Settings** and turn on **Launch when you sign in**.

This matters more than it sounds: with OmniHub closed, your fans are back on
the stock BIOS curve, including the 0%-while-hot behaviour described above.
It uses a Task Scheduler entry rather than a registry Run key, because the app
needs Administrator and a Run key will not reliably auto-elevate.

There is also **Start hidden in the tray** next to it, if you would rather it
came up as just a tray icon. Everything still applies at launch either way.

### Something missing?

The dashboard tells you. If a capability is unavailable on your machine, a
panel at the top names which one and why — a missing vendor interface, no SMU
driver, no NVIDIA GPU. It stays hidden when everything is present.

### Updating later

**Settings → Updates** shows the installed version, checks the published releases,
lists what changed in each, and downloads a newer build with progress before
revealing it in Explorer. It deliberately stops there rather than installing over
the running install: OmniHub runs elevated and holds the fan service, and an app
that replaces its own binary underneath itself — while it is the only thing
keeping a curve applied — is a bad trade for saving one manual run of setup.

Exit OmniHub through the tray, run the new setup, and it installs over the top. Setup
will stop and ask you to close the app if you forget, rather than killing it: a hard
kill never reaches the shutdown path that hands fan control back to the BIOS.

If you built from source instead:

```
git pull
dotnet build OmniHub.slnx -c Release
```

Your settings live in `%AppData%\OmniHub\settings.json` and are untouched by
either route. Release history is in [CHANGELOG.md](CHANGELOG.md), which mirrors
the published releases the app and website both read.

### First run on a new laptop model

Before trusting curve control on hardware that hasn't been checked, run:

```
OmniHub.exe -Probe
```

from an elevated terminal. This dumps the raw fan count/type/level/table,
temperature, throttling state, and GPU mode/power exactly as the BIOS
reports them, with no interpretation layered on top -- the ground truth
needed to confirm the command layout matches before relying on the curve.

## Workspaces

The sidebar is a list you build. A **workspace** is a name and an ordered set of
panels laid out across twelve columns; **EDIT LAYOUT** at the bottom of the
sidebar adds, renames, reorders and resizes them, and **1**-**9** switch between
the first nine. The arrangement lives in `%AppData%\OmniHub\workspaces.json`.

Editing is a mode rather than panels being draggable wherever they sit: what you
do ninety-nine times out of a hundred is read a number off a screen, and a layout
a slightly long click can destroy while you do that is worse than one that cannot
be rearranged at all.

**Nothing has to be arranged.** A fresh install ships the seven screens below, in
this order, each filling its own workspace -- so if you never open the editor you
see exactly the interface described here. A layout that cannot be read falls back
to these rather than failing to open.

### The screens

Three of them -- Performance, System and Diagnostics -- group related sub-screens,
so a measurement and the evidence behind it stay together rather than ending up
two clicks apart.

- **Dashboard** -- live temperature, fan duty and commanded level on one
  multi-series chart; what is currently holding the processor back, and which
  limit has been binding over the last hour; GPU mode; power draw; and three
  quick presets (Silent / Balanced / Performance).
- **Fans** -- the core fix. Switch between Auto (Curve), BIOS Default and Max
  Fan; edit the safety floor and the curve's lookup points, with a live chart.
  Says what the curve's last tick actually did, including what the predictive
  lead changed. Also has a **Manual Calibration** tool that steps the fan
  through raw levels so you can hear where it stops getting louder, a band
  editor that saves the result as a per-model profile, and a **Return to
  stock** control that undoes every write OmniHub has made.
- **Performance** -- **CPU**: the SMU tuning knobs (sustained, boost and APU
  power, core and SoC current, Curve Optimizer, thermal limit), the adaptive
  controller, and per-game rules. **GPU**: power preset and graphics mode
  (mode changes need a reboot and carry real risk on machines without a wired
  dGPU display path, which the UI warns about).
- **Battery** -- charge, health and cycle count; the firmware idle toggle; and
  where the power is going, which subtracts the processor package and the
  discrete GPU from what the battery reports and labels the remainder as the
  subtraction it is.
- **System** -- **Windows**: timer resolution, MMCSS, power plans and the rest
  of the OS-side controls. **Network**: adapter settings and latency
  measurements. **App GPU routing**: forces specific apps to the discrete or
  integrated GPU via the same `HKCU\...\DirectX\UserGpuPreferences` mechanism
  Windows Settings > Display > Graphics uses. Apps can be picked by browsing to
  the `.exe` or from the running-app list; it does not guess which apps are
  "games", you choose the preference explicitly.
- **Diagnostics** -- **Compare**: two stretches of time held against each other,
  and it records *runs* so a stretch can be named. Start one before a change and
  stop it afterwards, then compare against it by name instead of against whatever
  happened to come before. The power source and fan mode are recorded with it,
  because a comparison across those is not a comparison, and the trace behind a
  saved run is kept past the usual fourteen days so an old run still resolves to
  the data it names rather than to nothing. **Measure**: the load test,
  everything the firmware reports about this board, per-core clocks, memory and
  storage, the probe report and a support bundle. **History**: the thermal trace
  read back, with gaps drawn as gaps. **Stability**: every unclean shutdown
  reconstructed, and what is currently holding the machine awake.
- **Settings** -- launch at sign-in (via a Task Scheduler entry set to run
  elevated, not a registry Run key -- a Run key does not reliably auto-elevate
  an admin-required app), close-to-tray behaviour, logging, the predictive
  lead, and the theme.

### The smaller panels

Whole screens are not the only thing a workspace can hold, and they are not what
makes building one worthwhile:

- **A single reading** -- any of fourteen (die and GPU temperature, both fans,
  package and GPU power, GPU and CPU clock, GPU and CPU load, memory in use, how
  close the binding limit is, and what OmniHub itself is costing in processor
  time and memory), as a card with its recent trend beside it. The last pair is
  there because a tool for finding what drains a laptop should be willing to
  state what it draws.
  The two temperatures and the limit colour their figure past a threshold;
  nothing else does, because a wattage is not good or bad on its own.
- **A chart** -- temperature against the level that was commanded, GPU
  temperature against GPU power, package power, or how hard the binding
  constraint was being pressed. One hour, read back from the log, refreshed
  every thirty seconds, with gaps drawn as gaps.
- **What is limiting the machine** -- the same strip the Dashboard carries, of
  all five constraints as percentages of their own limits.
- **The fan curve in force**, read-only, with the machine's present position
  marked on it. Beside a temperature and a fan speed it answers the question
  those two raise: whether this is what the curve asked for.
- **Every reading, with its source** -- a table of all fourteen, their values and
  the route each takes, because a figure that looks wrong is only actionable
  once you know whether it came from the SMU, the NVIDIA driver or a kernel
  counter. A reading that did not answer is dimmed rather than dropped: the
  absence is the finding.

**EDIT LAYOUT** also offers four workspaces to start from -- Gaming, Noise,
Power and Sensors. They are starting points rather than shipped defaults, so a
fresh install still looks exactly like the seven screens above.

A panel from a version you no longer have is kept and drawn as a named
placeholder rather than dropped, so opening your layout in an older build and
closing it does not quietly destroy it.

### Building a theme

Eight ship. A ninth is yours: **Settings > Theme > Build your own** takes four
colours -- the ground, the panel, the accent and the text -- plus a corner
radius, and fills out all twenty-three colour keys a palette defines from them. The muted and faint text
are the text fading toward the ground; the borders are the panel moving toward
the text; the status and metric colours are moved until they read on the ground
you chose rather than being fixed values that only work on a dark one.

**A palette that would be unreadable is refused, not saved**, by the same WCAG
check the eight shipped ones are audited against, and the refusal names the two
colours, what they scored and what they needed. The verdict updates as you type,
so the button is disabled before you press it rather than after.

**Density** sits beside it: Compact, Normal or Roomy, scaling the padding, bar
heights and figure sizes the palette already asks for. Normal is the palette
untouched, and the two compose -- Compact on the roomiest theme is still roomier
than Normal on the tightest.

## Overlay

**Ctrl+Alt+O** toggles a small always-on-top readout that stays visible over a
game. Which metrics it shows is configurable; it is the only part of OmniHub
visible while something is running full-screen.

## Tray icon

- Single left-click opens a quick-glance flyout (temperature, throttling
  state, fan mode, commanded level) without opening the full window.
- Double-click, or the flyout's "Open OmniHub" button, opens the full window.
- Right-click gives quick fan-mode switches and Exit.
- A Windows notification fires the moment thermal throttling starts (not on
  every poll while it continues), so you find out even away from the app.

## Behavior on close

By default, closing the window minimizes to the system tray and keeps the
fan service running (that's the point -- the fix only holds while the app is
alive). Settings lets you change the X button to fully exit instead.
Either way, use the tray icon's **Exit** (or the X button, if set to exit) to
fully quit; this always hands fan control back to the BIOS's own automatic
mode first. Never quit an OmniHub headless process (`-RunHeadless`) with
`kill -9` / Task Manager "End task" for the same reason -- use Ctrl+C or the
tray Exit path so the fan isn't left pinned.

**Important:** the fan-curve fix only protects you while OmniHub is actually
running in Auto (Curve) mode. Any time it's closed (fully exited), crashed,
or not yet launched after a reboot, the laptop is back on the stock BIOS
curve -- including its 0%-while-hot bug. Enabling "Launch at sign-in" in
Settings is the way to make that protection the default instead of something
you have to remember.

## Known limitations

- Die temperature needs the PawnIO driver installed. `PawnIoAccess.cs` is a
  complete implementation whose P/Invoke signatures are transcribed from
  `C:\Program Files\PawnIO\PawnIOLib.h`, not inferred -- but without the
  driver present there is no Tctl reading, and the app falls back to the ACPI
  thermal zone, which is coarse (4-6 C steps) and blind above about 85 C. The
  log records which sensor produced every row, so a session that ran without
  Tctl is identifiable afterwards rather than having to be inferred from
  whether the temperatures had decimal places.
- Fan "level" sent to the BIOS is **not** a 0-255 PWM duty cycle -- it's a
  fan-speed target in units of ~100 RPM, confirmed against OmenMon and
  decompiled Omen Gaming Hub source (see `OmniHub.Core/Fan/FanService.cs`).
  The usable range measured on this board is raw 10-56 (~1000-5600 RPM), and
  that is what the built-in default now uses; the app's UI percentages (0-100%)
  map onto that band rather than onto the raw byte's full 0-255 span. The
  earlier figure of 20-55 was a borrowed community bound, and its floor cost a
  full 1000 RPM of available quiet. Other boards are expected to differ, which
  is what the Fans screen's calibration tool and per-model profiles are for.
  GetFanLevel is a real tachometer read rather than an echo of what was
  commanded -- but where the board returns fewer bytes than asked for, the
  missing levels are reported as unavailable rather than as a fan at zero.
- Per-model curve tuning is manual (via the Fans screen, its Manual Calibration
  tool, or `-Probe` output) -- there's no bundled database of per-model
  presets.
- `SystemController.GetThrottling()` is not independently verified against
  known-good hardware behavior -- see the doc comment on that method.

## Settings

Stored as plain JSON at `%AppData%\OmniHub\settings.json` (fan mode, curve
points, safety floor, close behavior, the theme and the custom palette), with
the workspace layout beside it in `workspaces.json`. No telemetry, nothing sent
anywhere.

Both are written to a temporary file and moved into place rather than truncated
and rewritten, so a machine that hangs mid-save comes back to the previous copy
instead of to a half-written one. That matters more than it sounds: a settings
file that fails to parse is not an error you see, it is an app that has quietly
forgotten your curve.

The startup toggle lives outside both files, as a Windows Task Scheduler
entry named `OmniHub_AutoStart` (see `OmniHub.App/StartupManager.cs`).

## Licence

**GPL-3.0-or-later.** See [LICENSE](LICENSE), and [NOTICE](NOTICE) for
attribution and the licence history.

OmniHub was MIT through 1.3.3 and was relicensed during 1.5 development. The
reason is specific: widening support past HP means using hardware interfaces
other people measured and published, and effectively all of that work --
NoteBook FanControl, LenovoLegionLinux, G-Helper, the Linux platform drivers --
is copyleft. Re-deriving it from scratch to keep a permissive licence would
have meant pretending not to know things that are freely documented.

A register number is a fact about hardware rather than authorship, and facts
can be used freely. Code cannot, so where any is reused it keeps its original
copyright header and is named in `NOTICE`. A licence that permits reuse is not
a licence to drop provenance.

If you took OmniHub under MIT, you keep MIT rights to that code. The change
binds what happens from here, not what was already given away.
