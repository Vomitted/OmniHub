# Changelog

Notable changes to OmniHub.

The **published GitHub releases are the authoritative changelog** — the app's Settings tab and
the website both read that list directly, so neither can drift from what actually shipped. This
file is the same history for anyone reading the repository, plus the unreleased work sitting on
`main`.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions are
[semantic](https://semver.org/spec/v2.0.0.html).

---

## [Unreleased]

Battery. 1.2.0 was tuned, measured and released entirely on mains, and almost everything below
is a thing that only goes wrong once the charger comes out.

### Fixed

- **The app was not crashing on unplug — Windows was killing it.** The auto-start task carried
  both of Task Scheduler's battery defaults, `DisallowStartIfOnBatteries` and
  `StopIfGoingOnBatteries`, because `schtasks`' command-line form cannot express either and the
  task had always been created with it. So OmniHub did not start at sign-in while unplugged, and
  was terminated the moment the charger came out — `LastTaskResult` `0x8007042B`, the process
  terminated unexpectedly. It is a hard kill, so the fan controller never reached its shutdown
  path and never handed control back to the BIOS: a power event could leave the fans pinned at
  whatever was last commanded, on the rail where a stuck fan is also draining the battery. The
  task is registered from XML now, with both flags false and no execution time limit.
- **A failed registration no longer leaves the machine with no task at all.** `/Create` deletes
  before it writes, so a rejected XML left nothing behind and OmniHub stopped coming back after a
  reboot — worse than the defect being fixed. It falls back to the plain command-line form now,
  which carries Windows' battery defaults but does exist, and says plainly that it did rather
  than reporting success for something degraded.
- **A failure says what Windows said.** The error from the call that actually failed was being
  overwritten by the status re-query that follows it, so a dialog reporting "The system cannot
  find the file specified" was faithfully quoting a query for a task that had just failed to be
  created, and the create's own reason had already been discarded. `schtasks` is also resolved
  from `Environment.SystemDirectory` rather than by bare name — not a diagnosis, just one
  possibility taken off the table, since a system binary should not be reached through an
  inherited `PATH`.
- **The AC-to-battery transition reached 90 °C.** Unplugging switched profiles, which switched
  *out* of Adaptive, so the controller that would have backed the limit off had stopped running.
  Adaptive now owns both rails itself and the profile switch stands aside.
- **The discrete GPU never slept on battery.** `nvidia-smi` reported P4 at 0% utilisation against
  a 43 W discharge — awake and idling rather than in D3cold — because polling it over PCIe wakes
  the card, and because the TGP re-assertion loop kept overriding the firmware's own reclaim. On
  battery the loop stands down and the card is left alone; the ceiling is released on unplug
  rather than waiting ~90 s for the firmware to take it back.

### Added

- **A battery rail for adaptive tuning.** Separate maximum wattage and temperature target that
  take effect on unplug and are restored on plug-in, clamped immediately on the transition
  instead of drifting into the new rail over the following minutes.

### Changed

- **The fan level is read two bytes at a time.** `hpqBIntM` exposes one method per response-buffer
  size, and the 128-byte one costs ~300 ms on this firmware against ~8 ms for the 4-byte one. That
  single call was 94% of the hardware poll. Measured: tick body ~314 ms to ~72 ms, real cadence
  2.32 s to 2.08 s.

---

## [1.2.0] — 2026-09-09

Measurement, broader hardware support, and a UI grouped by subject. The thread running through
it: this release exists because 1.1.1 compiled, passed every test, and was wrong — so most of the
work is about being able to tell.

### Added

- **Diagnostics tab.** A load test that pins every core for a chosen duration and records
  temperature, package power, CPU clock and fan speed, writes the run to a CSV, and reports
  median, p90, max and the clock *floor* — the figure that moves when sustained behaviour does.
  It only reads: no fan commands, no power writes, so it is safe to run while OmniHub is
  controlling the fans, which is the point. Alongside it, the firmware's own capability answers
  and a one-click probe report. Also available as `OmniHub.exe -LoadTest <minutes>`.
- **What is limiting the processor** (Performance › CPU). All five constraints — sustained power,
  boost power, EDC, TDC, temperature — each as a fraction of its own limit. Being at 99% of core
  current and 60% of power says plainly that raising the power limit will change nothing, which
  no single figure on the page could say. Hidden entirely without an SMU.
- **Shared CPU/GPU thermal budget.** The two chips are cooled by one heatpipe but were tuned as
  independent knobs, so a GPU-bound game left the CPU holding a sustained limit it was not using
  while the package sat at its thermal target. When the GPU is busy *and* the package has reached
  target, the CPU yields. Both conditions are required: a busy GPU on a cool machine is not
  competing for anything.
- **Per-model fan calibration.** `profiles/<baseboard>.json` can carry a chassis's real raw fan
  band, measured with the Manual Calibration tool. A profile missing a value, or one whose
  ceiling sits at or below its floor, is refused rather than half-applied.
- **Poll timing.** Each phase of the hardware poll is timed and averaged to `polltiming-*.csv`,
  and shown on Diagnostics.

### Changed

- **Seven sidebar destinations instead of nine**, grouped by subject: CPU tuning and GPU power
  are two halves of one decision about how much the machine may draw, so they share a
  **Performance** tab; app GPU routing joins the Windows settings under **System**. No screen's
  content moved.
- **The firmware gates behaviour.** `GetSystemData` decoded completely and fed nothing but a line
  of `-Probe` output. It is read at startup now, and three states are kept distinct: supported,
  denied, and *not stated*. Only a stated denial disables a control — a reply carrying nothing is
  unknown, and switching off working hardware on the strength of a failed read is the silent
  failure worth avoiding.
- **Legacy boards get the right fan encoding.** Pavilion Gaming and early Omen take a 0/1/2
  thermal policy; current Omen and Victus take `0x30`–`0x50`. The legacy path existed with zero
  callers, so those boards were being sent an out-of-range mode byte.
- The tuning tab's knob rows are a data template bound to a model, rather than fifty lines of
  imperative construction per knob with two dictionaries of live controls standing in for state.
- The fan count is read and reported. A board claiming more than two fans is named honestly
  rather than driven as though it had two.

### Fixed

- **The GPU query is off the fan curve's critical path.** It held a global lock while launching
  `nvidia-smi` and waiting up to three seconds, and the callers behind that lock were the fan
  control loop and the hardware poll. One slow query stalled both.
- **A dead reading no longer sits on screen.** The dashboard returned early when the performance
  reader failed, leaving the previous clock, load and memory figures looking live.
- **The readiness card appears.** It was written, styled, and never called, so a machine missing
  the PawnIO driver got no explanation anywhere for why half the application was dark.
- **Dragging the adaptive target no longer freezes the window.** Every intermediate slider
  position applied a thermal limit inline on the UI thread, and that is an SMU transaction that
  spins.
- Tabs build in the background rather than on first click; the tray flyout stops subscribing to
  the poll forever after one click; the gauge stops rebuilding unfrozen geometry at frame rate;
  the "every fifth tick" refresh happens every fifth tick; thermal logs are pruned after a
  fortnight; one charger watcher instead of two observing the same change at different moments.

### Notes

1.1.1 should be skipped; 1.1.2 fixed it. The build is not code-signed, so SmartScreen will warn.
The SHA-256 of the archive is published with the release and on the download page.

---

## [1.1.2] — 2026-09-08

### Fixed

- **A written power limit is read back from the hardware, not from a cache.** The PM table is
  cached for five seconds to keep mailbox traffic down, and nothing dropped that cache when a
  limit was written — so a read taken immediately after a write returned the value from *before*
  it. That inverts the check this project is built on: writing a limit and reading it straight
  back is how the app distinguishes a limit the firmware accepted from one it merely
  acknowledged, and inside the cache window a write that landed perfectly read as one the
  firmware had ignored.

  Adaptive mode's three-strike guard is driven by that comparison. In 1.1.1 it stopped itself
  within seconds of launch, reporting "this firmware locks CPU power limits" on hardware that
  had accepted every command it was sent — leaving the sustained limit frozen wherever it
  happened to be, with nothing to bring it down when the machine got hot. 1.1.1 should be
  skipped. The cache is now dropped at the single write path, so every reader benefits, not just
  the controller.

---

## [1.1.1] — 2026-09-08

### Fixed

- **Adaptive tuning no longer heats an idle machine to its target temperature.** The controller
  steered on temperature alone, and "below target, so add power" is true of an idle laptop at
  every single tick — so the sustained power limit ratcheted up to the configured maximum while
  nothing was running, and stayed there. Because that limit is SMU firmware state, it survived
  the reboot: the next boot ran its startup work at full sustained power and put the die on the
  85 °C target within thirteen seconds of starting, with the fans at 88% to hold it there. The
  reading was correct and the fan curve was correct — the temperature was being *caused* by the
  controller meant to be limiting it. This is integral windup: the loop kept integrating while
  its output could not act.

  The fix adds a demand term. Headroom is granted only to a processor already using the headroom
  it has, and handed back by one that is not — which costs nothing, because by definition that
  power was not being spent. Only the sustained limit is steered, so short interactive bursts
  keep their full boost power however far it has wound down. Seven tests cover the control law,
  including the idle sequence that produced the bug.

---

## [1.1.0] — 2026-09-07

Updates, power plans, and broader hardware support. The theme running through all of it: ask the
machine what it can do instead of assuming, and say plainly when the answer is "unknown".

### Added

- **In-app updater.** Settings gains an Updates card: installed version, a manual check, the
  release notes for anything newer, and a download with progress that reveals the file in
  Explorer. It stops short of unpacking over the running install — OmniHub runs elevated and
  holds the fan service, and swapping its own binary underneath itself to save one manual
  extract is not a trade worth making in an app whose absence puts the laptop back on the stock
  curve.
- **Changelog in the app and on the website**, both read from the published releases rather than
  from a file either could forget to update.
- **Power plan builder** (System tab). One slider runs from maximum battery life to maximum
  performance; every setting is derived from it, and the resolved values for both rails are
  shown before anything is written. Available values are read from Windows rather than listed in
  the source, so a machine with a different set of boost modes gets a builder that matches it.
- **Automatic power plan switching** (Settings), off by default, with a picker per rail listing
  every scheme on the machine — so an existing tuned plan can be targeted instead of taking the
  two OmniHub creates.
- **GPU telemetry on every machine, not only NVIDIA ones.** `Win32_VideoController` names the
  adapter and the GPU engine performance counters give 3D load, on any vendor. Temperature,
  power and clock stay unavailable there rather than estimated, because Windows exposes no
  generic thermal sensor for a GPU.
- **HP capability reporting.** `GetSystemData` (`0x28`) was declared and never called. It is now
  decoded, and answers "will this work on my laptop" from the firmware rather than from a
  hand-maintained list of model numbers. Two fields matter: whether software fan control is
  supported at all, and which thermal-policy encoding the board speaks — legacy boards including
  Pavilion Gaming take `0x00`–`0x03`, current Omen and Victus take `0x30`–`0x50`. Smart adapter
  status, keyboard type and backlight support are read too.
- `-Probe` reports all of the above, and two screenshots of the running application are on the
  download page.

### Changed

- **No coloured status text.** Red was decorating permanent labels and repeating what result
  sentences already said. Numeric readouts keep their thresholds — the temperature figure still
  turns red past 80 °C — and bars, gauges, chart lines and status chips keep their colour. Prose
  and labels no longer take it.
- The readiness panel distinguishes *no adapter readable at all* from *an adapter that reports
  name and load but exposes no thermal sensor*. Conflating them was misleading on every
  non-NVIDIA machine, and readouts no longer say "DISCRETE" when availability no longer implies
  a discrete NVIDIA card.
- Power plans are only ever **created**, never edited. Duplicating a scheme leaves the machine's
  own configuration untouched and reversible.

### Fixed

- **The update checker never offers OmniControl Suite as an OmniHub upgrade.** That application
  shares this repository and its `v9.0.0` tag is numerically the highest here, so a naive check
  would push an unrelated program at every user, forever. A release counts as OmniHub only if it
  carries an `OmniHub-*.zip` asset. Covered by tests.
- **The system design query is sent with no payload**, the way HP's own software sends it. Asked
  with a four-byte buffer it returned a well-formed reply with every capability byte clear,
  which decoded into "this machine supports no fan control" — printed on a laptop whose fans the
  application was driving at that moment. A zeroed capability block is now reported as a failed
  read rather than a featureless board.
- **Boost mode is selected by name, not by index.** It was written as `3` under a comment saying
  "Aggressive"; Windows enumerates `3` as Efficient Enabled and `2` as Aggressive on this
  hardware. Index assumptions apply cleanly, report success, and do the wrong thing.
- `-Probe` runs while the application is open. The single-instance guard sat in front of it, so
  the one command you would run to diagnose a live machine answered with a dialog instead of
  output. The guard still covers `-Calibrate` and `-RunHeadless`, which command the fan.
- The changelog list no longer swallows the mouse wheel on the Settings tab.

### Notes

The build is not code-signed, so SmartScreen will warn. The SHA-256 of the archive is published
with the release and on the download page.

---

## [1.0.0] — 2026-09-06

First release. A replacement for Omen Gaming Hub built around one rule: it does not synthesize
telemetry. If a value cannot be read, the interface says so.

### Added

- **Fan curve control with a safety floor.** On some HP Omen and Victus laptops the
  Default/Balanced BIOS fan table contains a 0% idle entry reachable while the machine is still
  genuinely hot. Past a temperature you set, Auto (Curve) mode never allows 0% again.
- **Manual fan calibration** that steps through raw speed levels, because the usable 20–55 raw
  band is a community bound for this EC family rather than a measurement of any specific unit.
- **GPU power and mode** — Eco/Balanced/Performance presets, Hybrid/Discrete/Optimus switching,
  and a Custom TGP + Dynamic Boost unlock that is re-asserted every five seconds because the
  firmware reverts it after roughly ninety.
- **AMD CPU tuning** through the PawnIO driver: sustained and boost power limits, thermal limit,
  die temperature, and an adaptive controller. Every apply reads the hardware back, and the
  Tuning tab distinguishes a limit the firmware *accepted* from one it actually *enforces*.
- **App GPU routing**, Windows power plans, scheduling, MMCSS, timer resolution and process
  priority — all of which work on any laptop.
- **Tray flyout, overlay and a global hotkey**, plus a notification the moment thermal
  throttling starts.
- **`-Probe`**, which dumps raw fan count, type, level, table, temperature, throttling state and
  GPU mode exactly as the BIOS reports them, for confirming the command layout on hardware
  nobody has checked.

### Known limitations

- The protection holds only while OmniHub is running.
- Curve Optimizer is refused by firmware on the machine this was developed against — the SMU
  returns `0xFF` where an invalid command returns `0xFE`, so the mailbox recognises the command
  and specifically declines it. The app reports that rather than showing an undervolt it did not
  apply.
- `PawnIoAccess.cs` is an intentional stub; MSR package temperature needs the PawnIO SDK wired
  against your installed version.
- Throttling detection is not independently verified against known-good hardware.

[Unreleased]: https://github.com/Vomitted/OmniHub/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/Vomitted/OmniHub/releases/tag/v1.2.0
[1.1.2]: https://github.com/Vomitted/OmniHub/releases/tag/v1.1.2
[1.1.1]: https://github.com/Vomitted/OmniHub/releases/tag/v1.1.1
[1.1.0]: https://github.com/Vomitted/OmniHub/releases/tag/v1.1.0
[1.0.0]: https://github.com/Vomitted/OmniHub/releases/tag/v1.0.0
