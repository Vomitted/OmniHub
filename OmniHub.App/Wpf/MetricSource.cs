// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows.Threading;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Optimize;
using OmniHub.Core.Telemetry;

namespace OmniHub.App.Wpf;

/// <summary>
/// One place that knows the current value of every reading, and its recent history.
///
/// Shared rather than per-panel because a workspace can hold a dozen metric panels and each one
/// reading the hardware for itself would be a dozen SMU transactions where one will do. The panels
/// are renderers: they subscribe, they read, they draw.
///
/// Two cadences, for the reason the overlay already separates them. Temperature and fan speed
/// arrive on the poll this application already performs and cost nothing extra. Package power, the
/// GPU and the system figures cost a round trip each, so they run on a slow timer off the UI
/// thread -- a cache miss in GpuTelemetry can still spawn nvidia-smi, and process startup on the
/// thread that draws is a visible stutter.
/// </summary>
public sealed class MetricSource : IDisposable
{
    private readonly HardwareContext _ctx;
    private readonly DispatcherTimer _slow;
    private readonly Dispatcher _dispatcher;

    private readonly AmdTuning? _tuning;
    private readonly SystemPerfReader _perf = new();

    // Its own sampler, for the reason every other delta in this application now has one.
    private readonly SelfUsage _self = new();

    private readonly Dictionary<string, double?> _values = new();
    private readonly Dictionary<string, Sparkline> _history = new();
    private readonly Dictionary<string, RunningStats> _stats = new();

    /// <summary>Raised on the UI thread whenever any value changed, and only while <see cref="Active"/>.</summary>
    public event Action? Updated;

    private bool _active = true;

    // The slow tick's two cadences. Five seconds is what a panel on screen needs; thirty is enough
    // for a session maximum to mean something while nobody is looking.
    private static readonly TimeSpan ShownInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HiddenInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether anybody is actually looking.
    ///
    /// This application spends most of its life minimised to the tray. Until this existed the slow
    /// tick took an SMU transaction, a GPU read and three syscalls every five seconds and raised
    /// an update for every hardware reading, to redraw panels nobody could see.
    ///
    /// Hidden, nothing is drawn and nothing is raised: no traces, no <see cref="Updated"/>. What
    /// continues is the bookkeeping behind the session figures, because the minimum, mean and
    /// maximum beside each reading are worth most after a game played with this window in the
    /// tray -- and a maximum that stopped counting whenever the window closed would be a number
    /// that looks like a measurement of the session and is not one. The fast readings cost nothing
    /// extra, since they arrive on the poll the fan curve needs anyway; the slow ones are read
    /// every thirty seconds instead of every five.
    ///
    /// Nothing about cooling goes through here. The fan curve keeps its own loop at its own
    /// cadence, which is the one thing in this application whose latency is a safety property
    /// rather than a preference.
    /// </summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            _slow.Interval = value ? ShownInterval : HiddenInterval;

            // Coming back, the traces are as old as the window has been hidden. Refreshing at once
            // means the first thing somebody sees is current rather than whatever was true when
            // they looked away.
            if (_active) RefreshSlow();
        }
    }

    public MetricSource(HardwareContext ctx)
    {
        _ctx = ctx;
        _dispatcher = Dispatcher.CurrentDispatcher;

        foreach (var metric in Metrics.All)
        {
            _history[metric.Key] = new Sparkline(minSpan: Metrics.TraceSpan(metric));
            _stats[metric.Key] = new RunningStats();
        }

        if (ctx.Smu is { } smu)
        {
            var tuning = new AmdTuning(smu);
            if (tuning.IsSupported) _tuning = tuning;
        }

        ctx.OnReading += OnReading;

        _slow = new DispatcherTimer { Interval = ShownInterval };
        _slow.Tick += (_, _) => RefreshSlow();
        _slow.Start();
        RefreshSlow();
    }

    /// <summary>The current value, or null when the reading was not available.</summary>
    public double? Value(string key) => _values.TryGetValue(key, out var v) ? v : null;

    /// <summary>The recent window for a reading, for drawing a trend beside it.</summary>
    public Sparkline History(string key) =>
        _history.TryGetValue(key, out var history) ? history : new Sparkline();

    /// <summary>
    /// The lowest, mean and highest value of a reading since <see cref="StatsSince"/>, counted
    /// whether or not the window was open.
    /// </summary>
    public RunningStats Stats(string key) =>
        _stats.TryGetValue(key, out var stats) ? stats : new RunningStats();

    /// <summary>When the session figures started counting: launch, or the last reset.</summary>
    public DateTime StatsSince { get; private set; } = DateTime.Now;

    /// <summary>Starts every session figure again from now.</summary>
    public void ResetStats()
    {
        foreach (var stats in _stats.Values) stats.Reset();
        StatsSince = DateTime.Now;
        if (_active) Updated?.Invoke();
    }

    /// <summary>
    /// The name of whatever is currently binding, so a panel showing the limit percentage can say
    /// what the percentage is of. Null when the SMU did not answer.
    /// </summary>
    public string? BindingLimitName { get; private set; }

    private void Set(string key, double? value)
    {
        _values[key] = value;
        if (_stats.TryGetValue(key, out var stats)) stats.Add(value);

        // The trace is only for drawing. Pushed while hidden, it would mix five-second samples with
        // thirty-second ones on an axis that assumes they are evenly spaced.
        if (_active && _history.TryGetValue(key, out var history)) history.Push(value);
    }

    // BeginInvoke, never Invoke: this arrives on the poll thread, and a synchronous marshal from
    // there stalls the temperature poll for as long as the UI thread is busy, which stops the fan
    // curve. The same reason DashboardView.OnReading gives.
    private void OnReading(Reading r) => _dispatcher.BeginInvoke(() =>
    {
        double tempC = double.IsNaN(r.PreciseTemperatureC) ? r.TemperatureC : r.PreciseTemperatureC;
        bool fromDie = r.TemperatureSource == TemperatureSource.SmuDieTctl;

        // Filtered, and a saturated zone reading is not a temperature -- the same two rules the
        // dashboard and the overlay apply. An uninitialised ACPI zone reporting 86 C on a cold
        // machine is not a measurement, and showing it beside a threshold colour would make it
        // look like one.
        double displayC = _ctx.CpuTrend.HasEnoughData ? _ctx.CpuTrend.FilteredTempC : tempC;
        bool ceiling = !fromDie && ThermalReader.IsAtCeiling(tempC, _ctx.ZoneCeilingC);

        Set("cpu", ceiling ? null : displayC);
        Set("fan", r.FanLevel1 is { } f1 ? _ctx.FanBackend.Calibration.RawToRpm(f1) : null);
        Set("fan2", r.FanLevel2 is { } f2 ? _ctx.FanBackend.Calibration.RawToRpm(f2) : null);

        if (_active) Updated?.Invoke();
    });

    private void RefreshSlow()
    {
        var tuning = _tuning;

        Task.Run(() =>
        {
            PowerSnapshot? power = null;
            if (tuning is not null)
            {
                try { power = tuning.ReadPower(); } catch { }
            }

            SystemPerf? perf = null;
            try { perf = _perf.Read(); } catch { }

            // Cheap: a refresh of this process's own counters, no driver and no WMI. Measured on
            // this thread with the rest so it reflects the same instant they do.
            double? selfCpu = null;
            double? selfMem = null;
            try { selfCpu = _self.CpuPercent(); selfMem = _self.MemoryMegabytes(); } catch { }

            return (power, gpu: GpuTelemetry.Read(), perf, selfCpu, selfMem);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            var (power, gpu, perf, selfCpu, selfMem) = t.Result;

            var binding = power?.TightestLimit();

            _dispatcher.BeginInvoke(() =>
            {
                // Null throughout is an ordinary answer, not a failure: no discrete GPU, no SMU,
                // or a card deliberately not woken because the machine is on battery. Each stays
                // null rather than holding its last value, so a dead reading cannot sit on screen
                // looking live.
                BindingLimitName = binding?.Name;

                Set("pkg", power?.StapmWatts);
                Set("limit", binding?.Percent);

                Set("gpu", gpu?.TempC);
                Set("gpuw", gpu?.PowerWatts);
                Set("gpuclk", gpu?.ClockMhz);
                Set("gpuload", gpu?.UtilisationPercent);

                Set("cpuload", perf?.CpuLoadPercent);
                Set("cpuclk", perf?.CpuClockGHz);
                Set("mem", perf?.MemoryUsedGB);

                Set("selfcpu", selfCpu);
                Set("selfmem", selfMem);

                if (_active) Updated?.Invoke();
            });
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        _ctx.OnReading -= OnReading;
        _slow.Stop();
    }
}
