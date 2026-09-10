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
hardware back, and the Tuning tab distinguishes a limit the firmware
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
| Discrete GPU telemetry | Any NVIDIA GPU (via `nvidia-smi`) |
| CPU tuning: power limits, thermal limit, curve optimizer | AMD Ryzen, with the PawnIO driver |
| Fan curve control, GPU TGP unlock, BIOS power limits | HP laptops exposing `hpqBIntM` |

Other vendors (Lenovo, Dell, ASUS) each expose a completely different ACPI/WMI
interface and there is no cross-vendor standard. The vendor layer is isolated
behind one availability check, so a second backend can slot in beside the HP
one — but none is shipped unverified, because untested code that writes to
unknown ACPI methods on someone's laptop should not exist.

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

Only needed for the Tuning tab: power limits, thermal limit, die temperature
and adaptive mode. Everything else works without it.

Easiest way: launch OmniHub and click **Install the PawnIO driver** on the
dashboard — it appears only if the driver is missing. Or do it yourself:

```
winget install namazso.PawnIO
```

No restart needed either way. OmniHub retries the driver every ten seconds
while it is missing, so tuning comes online on its own once it registers.

### 6. Start it with Windows (recommended)

Open the **Settings** tab and turn on **Launch when you sign in**.

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

## Tabs

- **Dashboard** -- live temperature/fan/throttling readout, GPU mode, and
  three quick presets (Silent / Balanced / Performance) that combine a GPU
  power preset with a fan mode.
- **Fans** -- the core fix. Switch between Auto (Curve), BIOS Default, and
  Max Fan; edit the safety floor and the curve's lookup points directly, with
  a live chart showing the curve and the current reading. Also has a
  **Manual Calibration** tool that steps the fan through raw speed levels one
  at a time so you can listen for where it stops getting louder -- this
  hardware family's 20-55 usable raw range (see below) is a well-sourced but
  *borrowed* community bound, not something measured on any specific unit.
- **GPU** -- power preset (Eco/Balanced/Performance) and graphics mode
  (Hybrid/Discrete/Optimus -- mode changes need a reboot and carry real risk
  on machines without a wired dGPU display path, which the UI warns about).
- **Power** -- CPU sustained (PL1) and boost (PL4) wattage limits, and idle
  power-saving toggle.
- **App GPU Routing** -- forces specific apps to the discrete or integrated
  GPU via the same `HKCU\...\DirectX\UserGpuPreferences` registry mechanism
  Windows Settings > Display > Graphics uses. Apps can be picked by browsing
  to the `.exe`, or via **Detect Running App**, which lists currently running
  processes with a visible window -- it doesn't guess which apps are "games"
  or GPU-heavy, you still choose the preference explicitly.
- **Settings** -- toggle launching OmniHub automatically at sign-in (via a
  Task Scheduler entry set to run elevated, not a registry Run key -- a Run
  key doesn't reliably auto-elevate an admin-required app), and choose
  whether the window's X button minimizes to tray or fully exits.

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
alive). The Settings tab lets you change the X button to fully exit instead.
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

- `OmniHub.Core/Hardware/PawnIoAccess.cs` is an intentional stub. Real
  CPU-package temperature via MSR read (a cross-check against the BIOS's
  single coarse sensor) needs the PawnIO SDK wired in against your actual
  installed version -- see the comments in that file.
- Fan "level" sent to the BIOS is **not** a 0-255 PWM duty cycle -- it's a
  fan-speed target in units of ~100 RPM, confirmed against OmenMon and
  decompiled Omen Gaming Hub source (see `OmniHub.Core/Fan/FanService.cs`).
  The real usable range across this HP EC family is only about raw 20-55
  (~2000-5500 RPM); this app's UI percentages (0-100%) are mapped onto that
  range, not onto the raw byte's full 0-255 span. This hardware interface
  does not expose real tachometer RPM; nothing in this app invents an RPM
  number that wasn't actually read.
- Per-model curve tuning is manual (via the Fans tab, its Manual Calibration
  tool, or `-Probe` output) -- there's no bundled database of per-model
  presets.
- `SystemController.GetThrottling()` is not independently verified against
  known-good hardware behavior -- see the doc comment on that method.

## Settings

Stored as plain JSON at `%AppData%\OmniHub\settings.json` (fan mode, curve
points, safety floor, close behavior). No telemetry, nothing sent anywhere.
The startup toggle lives outside this file, as a Windows Task Scheduler
entry named `OmniHub_AutoStart` (see `OmniHub.App/StartupManager.cs`).
