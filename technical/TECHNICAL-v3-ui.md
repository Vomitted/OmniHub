# OmniHub — interface, version 3

> Written against the graph at `graphify-out/graph.json` (4,541 nodes, 225 communities,
> rebuilt for this change) and the XAML as it stands at `9e8024f`. The two documents before
> it, `TECHNICAL-v1.md` and `TECHNICAL-v2.md`, describe the system whose surface this one
> changes.

---

## 1. Why this is not a palette change

The last visual pass on this application rebased the palette, the corner radius and the
gradients onto a supplied reference image, deployed it, and the entire verdict was **"it
still looks the same"**.

That verdict was correct, and the reason is worth stating precisely rather than treating as
taste. Colour, radius and gradient are properties of surfaces. What a person reads when they
open a screen is its *structure*: how many things are on it, which of them is most
important, where one section ends and the next begins, and what the words say. None of that
is a property of a surface, so none of it moved.

This document specifies the structural half. Where it names a colour, the colour is carrying
structure — a rule under a heading, a rail beside a section — not being chosen for its hue.

---

## 2. What is actually wrong

Five findings, taken from the source rather than from an opinion about it.

### 2.1 The type scale has a hole in it, and the hole is where headings go

| Style | Size | Family | Colour | Used for |
| --- | --- | --- | --- | --- |
| `HeadingText` | 22 | UI semibold | primary | the page title |
| — | — | — | — | **nothing occupies this range** |
| `SubHeadingText` | 10.5 | mono bold | **faint** | section headings |
| `SectionHeadingText` | 10 | mono semibold | **muted** | section headings |
| `DataLabelText` | 10 | mono bold | muted | column labels |
| `CardTagText` | 10 | mono | muted | chips and units |
| `BodyText` | 13 | UI | primary | prose |

Between the 22px page title and the 13px body there is nothing. Every section heading in the
application is 10 or 10.5 pixels, in the two lowest-contrast foregrounds the palette has, in
a monospace face, in capitals.

One finding, three consequences:

- **The capitals are compensating.** `KNOWN HP ISSUE`, `CURVE / TEMPERATURE TO FAN LEVEL`,
  `WHAT THE CURVE JUST DID`, `WHERE THE POWER IS GOING`, `PM TABLE EXPLORER` are set in caps
  because at 10px in a muted grey nothing else would register as a heading. Size is the
  correct tool for that and it is not being used.
- **Hierarchy is flat.** A page's main subject and its least important note are labelled
  identically, so a screen presents as an undifferentiated field of small shouting.
- **The structure is carried by the least legible text on screen.** The highest-level
  organising element on every page is set in the colours reserved for the lowest-value
  information.

### 2.2 No page states what it is for

Every view opens directly onto controls. `Fan Control`, `Graphics`, `Battery`, `System` — a
two-word title, then a card. The register asked for is open-source tool documentation, and
documentation opens a section by saying what the section is about.

### 2.3 Machine state disappears when you navigate

The live strip — state dot, model, temperature, load, package power — exists only in
`DashboardView.xaml:121-160`. Leave the Dashboard to change a fan curve and the machine's
actual condition is no longer on screen, which is backwards: the reason to open the Fans page
is usually something the temperature readout would have explained.

### 2.4 Pages are flat stacks of equally weighted cards

Every view is a vertical stack of `CardBorderStyle` borders at the same visual weight.
Nothing on a page is primary. The Dashboard's four metric cards are the clearest case:
Processor, Graphics, Memory and Utilisation are drawn identically, though the first is the
subject of the application and the last is context.

### 2.5 Cards are used where tables would be denser

Named in the original design audit and still true. `PowerView`'s breakdown — whole machine,
at the battery, processor, discrete GPU, everything else — is five cards expressing one
subtraction, which is a four-row table with a rule above the total.

---

## 3. The change

### 3.1 Restore the missing heading level

Add three styles, retire two from one of their jobs.

```
PageTitle       26 / UI semibold / primary     the page, once, at the top
PageLede        13 / UI regular  / muted       one sentence under it: what this page controls
SectionTitle    15 / UI semibold / primary     NEW. Sentence case. A section of a page.
SectionRule      1px hairline, BorderBrush     NEW. Sits directly under a SectionTitle.
FieldLabel      11 / UI regular  / muted       sentence case, labels a single control
DataLabelText   10 / mono bold   / muted       UNCHANGED, but only inside tables and cards
CardTagText     10 / mono        / muted       UNCHANGED, chips and units only
```

`SubHeadingText` and `SectionHeadingText` stop being used as section headings. Their
definitions stay — they are right for column headers and unit tags — but no page-level
section may use them. Section headings become **sentence case**: `Known HP issue`, `The
curve in force`, `Where the power is going`.

This is the change that does most of the work, because it is what makes a page read as a
document rather than as a control panel.

### 3.2 A page header block

Every view opens with the same three parts:

```
Fan control
The curve this machine's fans follow, and the floor that stops them idling while it is hot.
────────────────────────────────────────────────────────────────────────────────────────
```

Title, one sentence, a rule. The sentence is not decoration: several of these pages drive
hardware whose behaviour is not guessable from a two-word title, and this project's standard
is that the interface explains what it is doing.

### 3.3 The instrument bar becomes persistent

The Dashboard's status strip moves out of `DashboardView` and into the shell, in
`MainWindow.xaml`, pinned above the view host. It shows what it shows today — state, model,
temperature, load, package power, fan duty — on every screen.

Consequences to handle rather than discover:

- `DashboardView` loses that markup and its code-behind; the bar subscribes to
  `HardwareContext.OnReading` itself.
- The `ActivityRibbon` at `MainWindow.xaml:78-100` moves from the window's top edge to the
  bottom edge of the bar, so the one moving element stays attached to the live data.
- The bar must not become a second navigation surface. It is a readout; nothing in it is
  clickable.

### 3.4 Pages gain a primary column

Views whose content supports it move from one stack to a two-column body: the instrument or
the thing being configured on the left at roughly two thirds, its settings and notes on the
right. `FansView`, `GpuView`, `TuningView` and `PowerView` each currently stack a large
subject above small settings that would sit beside it.

The Dashboard's four equal cards become one primary card — Processor, the subject — at double
width above three secondary cards, rather than four identical quarters.

### 3.5 Two card grids become tables

- **`PowerView`** — the five power cards become a four-row table with a rule above
  "everything else", because what is being shown is a subtraction and that is how a
  subtraction is written.
- **`DiagnosticsView`** — "what the firmware reports" is already rows of label and value in
  card clothing; it becomes a two-column table.

---

## 4. What does not change

Stated because a redesign that quietly drops things is how craft gets lost.

- **Dark only.** The audience is gamers and this was asked for explicitly, with that reason.
- **The accent stays blue-to-cyan**, on tracks, the app mark and the primary chart line. The
  "AI slop" objection was about landing-page hero gradients and glowing orbs, not about an
  instrument accent.
- **No coloured prose.** Chips, dots, rails, bars, gauges and chart lines carry colour.
  Threshold colouring of a *numeric readout* stays — the temperature figure going red past
  80 °C is data visualisation. The words beside it are not.
- **The sidebar icons stay.** They are keyed off the first panel's type. A number or a label
  is not a substitute for an icon.
- **Motion stays quick** — 220 ms on numeric readouts, 240 ms on colour.
- **Every panel type in `MainWindow.PanelCatalogue` keeps working.** The workspace model is
  the user's arrangement; this change does not touch what can be arranged.
- **No fabricated telemetry.** A denser table is more places to accidentally show a number
  nobody measured. Unavailable stays unavailable.

---

## 5. Order of work

Each package ends with refactor and tests, per the standing method.

| # | Package | Touches |
| --- | --- | --- |
| U1 | The type scale: `PageTitle`, `PageLede`, `SectionTitle`, `SectionRule`, `FieldLabel` | `Styles.xaml` |
| U2 | Page header block, applied to all 14 views | every `Views/*.xaml` |
| U3 | Persistent instrument bar | `MainWindow.xaml`, `MainWindow.xaml.cs`, `DashboardView` |
| U4 | Two-column page bodies | `FansView`, `GpuView`, `TuningView`, `PowerView` |
| U5 | Cards to tables | `PowerView`, `DiagnosticsView` |
| U6 | Dashboard card weighting | `DashboardView` |

---

## 6. How it is verified

The suite already guards two of the three failure modes this change could introduce, and both
found real defects on their first run, so they are extended rather than trusted:

| Property | Test |
| --- | --- |
| Every text colour is legible on the surface it lands on, in every palette | `ContrastTests`, extended with the new styles |
| No fixed `Height` clips the style it carries | `ControlSizingTests` |
| No page-level section heading uses the 10px mono caps styles | `ViewMarkupTests` — **new assertion**, the rule this document exists to enforce |
| Every view has exactly one `PageTitle` and one `PageLede` | `ViewMarkupTests` — **new assertion** |
| Every panel in the catalogue still resolves to a view | `WorkspaceTemplateTests` |

The third and fourth are what keep this from decaying: a rule nobody asserts survives exactly
until the next person adds a section.

And one that cannot be automated, written down so it is not skipped: **look at the running
application.** This user inspects it and has reported contrast and clipping defects in work
already delivered as complete. A build that compiles and passes its tests is not evidence
that a control is not cut off.
