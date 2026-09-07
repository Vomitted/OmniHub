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

### Added

- **In-app updater.** Settings gains an Updates card: current version, a manual check, the
  release notes for anything newer, and a download with progress that reveals the file in
  Explorer. It stops short of unpacking over the running install — OmniHub runs elevated and
  holds the fan service, and swapping its own binary underneath itself to save one manual
  extract is not a trade worth making in an app whose absence puts the laptop back on the stock
  curve.
- **Changelog in the app and on the website**, both read from the published releases rather than
  from a file either could forget to update.
- **GPU telemetry on every machine, not only NVIDIA ones.** `Win32_VideoController` names the
  adapter and the GPU engine performance counters give 3D load, on any vendor. Temperature,
  power and clock stay unavailable there rather than estimated, because Windows exposes no
  generic thermal sensor for a GPU and inventing one is the thing this project does not do.
- Two screenshots of the running application on the download page.

### Changed

- The readiness panel now distinguishes *no adapter readable at all* from *an adapter that
  reports name and load but exposes no thermal sensor*. Conflating them was misleading on every
  non-NVIDIA machine.
- Readouts that said "DISCRETE" no longer do, since GPU availability no longer implies a
  discrete NVIDIA card.

### Fixed

- The update checker never offers **OmniControl Suite** as an OmniHub upgrade. That application
  shares this repository, its `v9.0.0` tag is numerically the highest here, and it is what
  GitHub's own `/releases/latest` returns — so a naive check would push an unrelated program at
  every user, forever. A release counts as OmniHub only if it carries an `OmniHub-*.zip` asset.
  Covered by tests.

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

[Unreleased]: https://github.com/Vomitted/OmniHub/compare/v1.0.0...HEAD
[1.0.0]: https://github.com/Vomitted/OmniHub/releases/tag/v1.0.0
