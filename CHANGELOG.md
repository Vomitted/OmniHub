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

Nothing yet.

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

[Unreleased]: https://github.com/Vomitted/OmniHub/compare/v1.1.1...HEAD
[1.1.1]: https://github.com/Vomitted/OmniHub/releases/tag/v1.1.1
[1.1.0]: https://github.com/Vomitted/OmniHub/releases/tag/v1.1.0
[1.0.0]: https://github.com/Vomitted/OmniHub/releases/tag/v1.0.0
