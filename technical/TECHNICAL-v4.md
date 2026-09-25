# OmniHub — version 4: what is on screen, and what it costs off screen

> Written against the graph at `graphify-out/graph.json` (4,698 nodes, 10,045 edges, 234
> communities, rebuilt from `ceb5936` for this change), the running application on the HP Victus
> 15-fb2999ax, and — for the first time — rendered images of every screen. `TECHNICAL-v3-ui.md`
> is the document this one follows; its packages U3–U6 are carried forward here unchanged in intent.

---

## 1. Why this document exists

Every previous visual pass on OmniHub was made blind. The application runs elevated
(`requireAdministrator`), so screenshots taken from outside it come back masked, and
`OmniHub.Tests` cannot reference `OmniHub.App` because the App's build output *is* the installed
program. Colour, type and layout were therefore changed by reading XAML and reasoning about it,
and three consecutive rounds were answered with "it still looks the same".

This round began by building a way to look.

### 1.1 The renderer

A scratch tool (not shipped) builds the App into a separate folder and renders its real
`MainWindow`, every workspace and every interface to PNG. Because the live OmniHub owns the fans,
the renderer is contained by the operating system rather than by care:

| Layer | What it guarantees | How it was verified |
| --- | --- | --- |
| **Low integrity** (file label on the executable) | Cannot write `%AppData%\OmniHub`, cannot open the PawnIO device, cannot call HP's WMI methods | The renderer refuses to start unless a write to the settings folder is *denied*, and the App's own `HardwareContext` reports no vendor interface and no SMU |
| **A separate, invisible desktop** (`CreateDesktop`) | Nothing it opens — a window, a dialog — can appear on the user's screen | — |
| **A plain `Application`, never OmniHub's `App`** | OmniHub's startup path (single-instance guard, second main window) never runs | Stack trace of every shutdown, see §1.2 |

### 1.2 What the first version of the renderer did wrong

The first version constructed OmniHub's own `App` class to get its resources. WPF queues
`OnStartup` from the `Application` constructor, so pumping the dispatcher ran OmniHub's real
launch path: against a running copy it hit the single-instance guard and put
**"OmniHub is already running"** on the user's screen; with no copy running it opened a real main
window. The live application exited cleanly twice in the same window of time — fans handed to the
BIOS first, relaunched within 40 seconds both times — most plausibly the user responding to those
dialogs. The fix is the third row of the table above, and the reason it is written down is that
"constructing an `Application` subclass runs its startup code" is not obvious and will be
rediscovered otherwise.

---

## 2. Findings

### 2.1 Rendering — five defects, each systemic

None of these produces a compiler warning, a failing test or an exception. All five are visible
on the screen the user opens every day (`Interface: Workspaces`, theme `Ember`).

| # | Defect | Extent | Mechanism |
| --- | --- | --- | --- |
| **R1** | Explanatory paragraphs sit **centred** in wide cards, 150–200 px right of the label above them | 68 `MaxWidth` in XAML + 13 in code, across every page | A WPF element with `HorizontalAlignment="Stretch"` (the default) and a `MaxWidth` smaller than its slot is **centred** in the slot. `SettingsView`'s changelog already sets `HorizontalAlignment = Left` beside its `MaxWidth` — the fix existed in one place |
| **R2** | A lighter rounded band across the top of every card, overlapping the labels, reads as a smudge | 73 `CardSheenStyle` borders in 12 views | Decorative "glass" highlight — the aesthetic the user has named as AI slop |
| **R3** | The Fans page label **TEMPERATURE** is invisible | 12 of 29 `TileLabel` uses | `TileLabel` sets no `Foreground`; callers that forget inherit WPF's default **black**. Measured RGB(4,4,3) on a RGB(27,22,19) card, ≈1.1:1, in every palette |
| **R4** | Bright white stock controls on a dark theme | Tuning's preset-name box, System's brightness slider | `NumericTextBoxStyle` and `OmniSliderStyle` exist but are keyed, so an unkeyed `TextBox`/`Slider` falls back to stock WPF |
| **R5** | The fan curve chart is clipped: the 0 % line and the entire temperature axis are cut off | Fans | Fixed container height below what the chart measures |

### 2.2 Background cost — measured on the running application

Baseline, OmniHub hidden in the tray (its normal state): **2.23 % of one core** sustained, 70 MB
working set, 138 MB private, 21 threads.

| # | Finding | Evidence |
| --- | --- | --- |
| **B1** | `TimeSeriesChart` runs a **100 ms** redraw timer from `Loaded` to `Unloaded`. Hiding a window does not unload it, and start-minimised hides the window *after* `Loaded` — so the Dashboard chart ticks ten times a second, and re-renders on every reading, all session | `TimeSeriesChart.xaml.cs:90,111-112` |
| **B2** | The activity ribbon recolours and pulses on every reading while the window is hidden | `MainWindow.xaml.cs:239` |
| **B3** | GPU TGP re-assertion reads the BIOS every **5 s** on AC; three comments in the same method describe 30 s | `MainWindow.xaml.cs:1659, 1713-1780` |

Dashboard, Battery and Tuning already gate their own timers on `IsVisibleChanged`; B1 and B2 are
the ones that were missed.

### 2.3 Behaviour that needs the user, not a patch

**C1.** `AutoPowerPlan` is on and binds AC to *OmniHub Performance*, but *Balanced* is active.
Both plans carry identical processor values (boost disabled, 100/99 % max, 5 % min), so the
processor is unaffected. Changing which plan is active is the user's decision under their standing
instruction, and is reported rather than changed.

### 2.4 Carried forward from version 3

U3 persistent instrument bar, U4 primary column, U5 Battery's power cards as a table, U6 one
primary Dashboard card. Unchanged in intent; now verifiable against rendered images.

---

## 3. Work packages

Each ends with **refactor + unit test**. Where a rule is asserted, the defect is injected and the
test is seen to fail before it is trusted.

| Package | Change | Test |
| --- | --- | --- |
| **W1** R1 | Every element with a `MaxWidth` is pinned left: 65 in markup, 5 in code | `EveryWidthCapSaysWhichSideItKeepsTo` — XAML parsed as XML, styles resolved, code initialisers scanned. Failed on 70 sites before the fix |
| **W2** R2 | The sheen is deleted: the style, the brush and all 72 borders | Covered by the existing `EveryResourceReferenceResolves`: any surviving reference to the deleted style is now a dangling key |
| **W3** R3 | `TileLabel` gains a muted default; callers that colour a label to its series keep their colour | `EveryTextStyleSaysWhatColourItIs`, over the real `Style` objects. Failed naming `TileLabel` before the fix. Eleven labels were affected, nine of them on the Network page |
| **W4** R4 | Implicit `TextBox`, `Slider` **and** `ComboBox` styles based on the keyed ones. The audit found six unstyled combo boxes as well; the combo template had no `PART_EditableTextBox`, so it gained one before it could become the default — otherwise the editable game picker would have lost typing | `EveryInputHasTheApplicationsLookByDefault`. Injected: renaming the editable part fails it |
| **W5** R5 | The chart stops owning a card and a height; both hosts already supplied one (a 240 px card inside 188 px and 190 px ones) | Renderer: full 0–100 °C axis, 0 % baseline and the safety-floor band now visible, one frame |
| **W6** B1, B2 | Chart redraw and ribbon pulse run only while visible | Measured on the live application after deploy, against the 2.23 % baseline |
| **W7** B3 | Comments corrected to the interval the code uses | — |
| **W8** U3–U6 | Structural changes to Workspaces | Renderer, then the user |

Order: W1–W5 first, because they are the defects on every screen and each has a precise test;
W6–W7 next, measured; W8 last, because it is the change most likely to need a second opinion.

---

### 3.1 What W6–W8 became

- **W6** found more than two timers. Four `RepeatBehavior.Forever` animations ran from launch
  whether or not the window was shown — the activity ribbon, the Dashboard's LIVE dot, the sidebar's
  selected rail, and the fan chart's live-point halo, which also leaked: every redraw started a new
  pair of clocks on a new transform. Each now starts on `IsVisibleChanged(true)` and stops on
  `false`; `AmbientMotionTests` holds the rule for markup and code and fails naming all four
  against the previous commit. The chart's redraw timer moved from `Loaded` to visibility.
- **W8/U3** is `InstrumentBar`: six readings above every workspace page, each with its trace. Drawn,
  it exposed that every trace in the application scaled to its own window, so 1.5 °C of sensor
  noise filled the box. `Sparkline` gained a minimum span and `Metrics.TraceSpan` sets it per unit.
- **W8/U5** is the power table; **U6** reordered the Dashboard around the chart. **U4** (two-column
  page bodies) was not done.
- Found on the way: ten decoratively coloured labels, now held neutral by
  `NoLabelOrHeadingIsColoured`; the interface switch leaked its chrome band's subscription.

## 4. Open: whole-machine stalls, not yet attributed

Measured with a user-mode probe — two threads pinned to cores 2 and 7, spinning on the performance
counter, recording every gap over 50 µs, and counting a gap as whole-machine only when both cores
lose the same interval. Over 60 s with OmniHub running: 1,763 simultaneous stalls, 49 over 500 µs,
13 over 1 ms, largest 2.4 ms, arriving in bursts — the largest 50 stalls in 162 ms, 25.7 ms frozen.
At 144 Hz a frame is 6.9 ms, so a burst touches most frames it overlaps.

This is the shape System Management Mode produces, and HP's BIOS interface enters it. It is **not
attributed to OmniHub**: the bursts are irregular rather than on the fan readback's ~10.4 s period,
they clustered after a build, and the machine was charging — the EC's charger traffic, GPU power
transitions and the hypervisor are all candidates. Settling it needs an A/B inside the application:
pause its own BIOS polling for twenty seconds and compare against the twenty before. Nothing is
changed on the strength of this until that has been run.

## 5. Verification

- `dotnet build -c Release` clean; `dotnet test` green (866 before this change, only grows).
- Every page, every interface, re-rendered after W1–W5 and compared with the images in §2.1.
- After deploy: OmniHub's own CPU while hidden, sampled over the same 30–40 s window as the
  baseline, and the fan loop confirmed running from the thermal log.
