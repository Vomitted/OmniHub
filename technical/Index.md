# OmniHub — index

The way into this repository when it is opened as an Obsidian vault. The documents here in
`technical/` are written by hand and committed. The code map under `graphify-out/obsidian/` is
generated from the source by graphify: one note per class, method and document section, rebuilt
rather than edited, and not committed.

## Documents

| Document | What it is |
| --- | --- |
| [README](../README.md) | What OmniHub is, for someone who has never seen it |
| [CHANGELOG](../CHANGELOG.md) | Every release, and under *Unreleased* what `main` has beyond the download |
| [Technical document v1](TECHNICAL-v1.md) | The system as built at `b0c9513` — the baseline everything after is measured against |
| [Technical document v2](TECHNICAL-v2.md) | The specification for the Instrument release |
| [Interface, version 3](TECHNICAL-v3-ui.md) | The structural half of the interface: headings, page structure, the instrument bar |
| [Version 4](TECHNICAL-v4.md) | What is on screen and what it costs off screen: the rendering defects, the hidden-window cost, the open stall measurement |
| [Snapshot tool](../tools/snapshot/README.md) | Rendering the real windows from a sandbox, and why it cannot touch the machine |
| [Graph report](../graphify-out/GRAPH_REPORT.md) | graphify's own summary: hubs, communities, surprising connections |

## The code map

Every note under `graphify-out/obsidian/` is one node of the code graph. Its links are the graph's
edges — what it calls, contains, implements or references — so the backlinks pane answers "what
uses this". The properties at the top name the source file and line (`location: L171`), which is
the way back into the code. Tags sort the notes: `#graphify/code`, `#graphify/document` for a
section of one of the documents above, `#graphify/concept`, and `#community/<name>` for the
subsystem a node was clustered into.

In the graph view the colours are a legend: **orange** for these hand-written documents, **amber**
for document sections, **blue** for code, **teal** for concepts. Build output, graphify's dated
backups and its cache are excluded from search and the graph.

### Where to start

**The hubs**, most connected first: [MainWindow](../graphify-out/obsidian/MainWindow.md) ·
[AppSettings](../graphify-out/obsidian/AppSettings.md) · [TuningView](../graphify-out/obsidian/TuningView.md) ·
[HardwareContext](../graphify-out/obsidian/HardwareContext.md) · [FanService](../graphify-out/obsidian/FanService.md)

**The subsystems**, each a community note listing its members and their files:

| Area | Communities |
| --- | --- |
| Cooling | [FanService](../graphify-out/obsidian/_COMMUNITY_FanService.md) · [FanCurve](../graphify-out/obsidian/_COMMUNITY_FanCurve.md) · [FanController](../graphify-out/obsidian/_COMMUNITY_FanController.md) · [spike filter](../graphify-out/obsidian/SpikeFilter.md) |
| Hardware | [HardwareContext](../graphify-out/obsidian/_COMMUNITY_HardwareContext.md) · [RyzenSmu](../graphify-out/obsidian/_COMMUNITY_RyzenSmu.md) · [SystemController](../graphify-out/obsidian/SystemController.md) · [EcAccess](../graphify-out/obsidian/_COMMUNITY_EcAccess.md) |
| Safety | [VendorTier and the write gate](../graphify-out/obsidian/_COMMUNITY_VendorTier.md) · [WriteGateTests](../graphify-out/obsidian/_COMMUNITY_WriteGateTests.md) · [RestoreJournal](../graphify-out/obsidian/_COMMUNITY_RestoreJournal.md) |
| Tuning | [AdaptiveTuning](../graphify-out/obsidian/_COMMUNITY_AdaptiveTuning.md) · [TuningView](../graphify-out/obsidian/_COMMUNITY_TuningView.md) · [PowerPlanAutomation](../graphify-out/obsidian/_COMMUNITY_PowerPlanAutomation.md) |
| Telemetry | [MetricSource](../graphify-out/obsidian/_COMMUNITY_MetricSource.md) · [TelemetryHistory](../graphify-out/obsidian/_COMMUNITY_TelemetryHistory.md) · [ThermalLog](../graphify-out/obsidian/_COMMUNITY_ThermalLog.md) · [Sparkline](../graphify-out/obsidian/_COMMUNITY_Sparkline.md) · [ValueAxis](../graphify-out/obsidian/ValueAxis.md) · [LimitHistory](../graphify-out/obsidian/LimitHistory.md) |
| Interface | [MainWindow](../graphify-out/obsidian/_COMMUNITY_MainWindow.md) · [AlternateInterface](../graphify-out/obsidian/_COMMUNITY_AlternateInterface.md) · [instrument bar](../graphify-out/obsidian/InstrumentBar.md) · [DashboardView](../graphify-out/obsidian/_COMMUNITY_DashboardView.md) · [FansView](../graphify-out/obsidian/_COMMUNITY_FansView.md) · [TimeSeriesChart](../graphify-out/obsidian/_COMMUNITY_TimeSeriesChart.md) |
| Other laptops, Linux | [Hwmon](../graphify-out/obsidian/_COMMUNITY_Hwmon.md) · [LinuxMachine](../graphify-out/obsidian/LinuxMachine.md) · [NbfcConfig](../graphify-out/obsidian/_COMMUNITY_NbfcConfig.md) |

The whole graph is also laid out on one canvas: [graph.canvas](../graphify-out/obsidian/graph.canvas).
It is large; the graph view is quicker for finding a way around.

## Keeping it current

The code map describes the source at the moment it was exported, so it goes stale as the code
changes. From the repository root:

```
graphify update .
graphify export obsidian --dir graphify-out/obsidian
```

The export only rewrites notes it created itself, so a note of your own added beside the documents
here is never touched. Write notes there, not inside `graphify-out/`, and link to any code note by
its name — `[[FanService]]` — which Obsidian resolves wherever the note lives.
