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

    private readonly Dictionary<string, double?> _values = new();
    private readonly Dictionary<string, Sparkline> _history = new();

    /// <summary>Raised on the UI thread whenever any value changed.</summary>
    public event Action? Updated;

    public MetricSource(HardwareContext ctx)
    {
        _ctx = ctx;
        _dispatcher = Dispatcher.CurrentDispatcher;

        foreach (var metric in Metrics.All) _history[metric.Key] = new Sparkline();

        if (ctx.Smu is { } smu)
        {
            var tuning = new AmdTuning(smu);
            if (tuning.IsSupported) _tuning = tuning;
        }

        ctx.OnReading += OnReading;

        _slow = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
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
    /// The name of whatever is currently binding, so a panel showing the limit percentage can say
    /// what the percentage is of. Null when the SMU did not answer.
    /// </summary>
    public string? BindingLimitName { get; private set; }

    private void Set(string key, double? value)
    {
        _values[key] = value;
        if (_history.TryGetValue(key, out var history)) history.Push(value);
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
        bool ceiling = !fromDie && SystemController.IsAtSensorCeiling(tempC);

        Set("cpu", ceiling ? null : displayC);
        Set("fan", r.FanLevel1 is { } f1 ? FanService.RawToRpm(f1) : null);
        Set("fan2", r.FanLevel2 is { } f2 ? FanService.RawToRpm(f2) : null);

        Updated?.Invoke();
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

            return (power, gpu: GpuTelemetry.Read(), perf);
        }).ContinueWith(t =>
        {
            if (t.IsFaulted) return;
            var (power, gpu, perf) = t.Result;

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

                Updated?.Invoke();
            });
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        _ctx.OnReading -= OnReading;
        _slow.Stop();
    }
}
