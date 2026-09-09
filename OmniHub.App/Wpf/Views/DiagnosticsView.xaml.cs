using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

// This project sets both UseWindowsForms and UseWPF, so these names exist in both stacks and a
// bare reference is ambiguous. Aliased per file, as every other view here does; the csproj's
// global alias block deliberately does not cover these two, because a global alias collides with
// a local one rather than shadowing it, and twelve files already carry the local form.
using UserControl = System.Windows.Controls.UserControl;
using Panel = System.Windows.Controls.Panel;
using OmniHub.Core.Diagnostics;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Optimize;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// Measurement and ground truth: run a load test, read the firmware's own account of the board,
/// and save a probe report.
///
/// The load test existed as a class and a command-line flag with nowhere to run it from, which
/// made it a library nobody would reach for. It is here because the reason it was written is the
/// reason this tab is: version 1.1.1 shipped a regression the tests passed over and a short idle
/// sample looked fine through, caught only because the machine's owner said it felt hotter. Two
/// runs either side of a change turn that into a measurement.
/// </summary>
public partial class DiagnosticsView : UserControl
{
    private readonly HardwareContext _ctx;
    private readonly AmdTuning? _tuning;
    private CancellationTokenSource? _run;

    public DiagnosticsView(HardwareContext ctx)
    {
        InitializeComponent();
        _ctx = ctx;

        if (ctx.Smu is { } smu)
        {
            var tuning = new AmdTuning(smu);
            if (tuning.IsSupported) _tuning = tuning;
        }

        DurationCombo.ItemsSource = new[] { "1 minute", "3 minutes", "5 minutes", "10 minutes" };
        DurationCombo.SelectedIndex = 1;

        BuildCapabilityRows();
    }

    // ------------------------------------------------------------------ load test

    private void RunBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_run is not null) return;

        var duration = TimeSpan.FromMinutes(DurationCombo.SelectedIndex switch
        {
            0 => 1, 2 => 5, 3 => 10, _ => 3,
        });

        _run = new CancellationTokenSource();
        RunBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        RunSummary.Children.Clear();
        RunStatus.Text = $"Running for {duration.TotalMinutes:0.#} minutes. Every core is loaded; the machine will get hot.";

        var token = _run.Token;
        var progress = new Progress<LoadSample>(s => RunLive.Text =
            $"{s.Elapsed.TotalSeconds,4:0}s   {Show(s.TempC, "0.0")} C   {Show(s.PackageWatts, "0.0")} W   "
            + $"{Show(s.CpuClockGhz, "0.00")} GHz   {(s.FanRpm is int r ? $"{r} rpm" : "--")}");

        LoadTest.RunAsync(duration, Sample, progress: progress, token: token)
            .ContinueWith(t =>
            {
                _run?.Dispose();
                _run = null;
                RunBtn.IsEnabled = true;
                StopBtn.IsEnabled = false;

                if (t.IsFaulted)
                {
                    RunStatus.Text = $"The run failed: {t.Exception?.GetBaseException().Message}";
                    return;
                }

                ShowSummary(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        // The load always stops with the run, cancelled or not; see LoadTest.RunAsync's finally.
        RunStatus.Text = "Stopping.";
        _run?.Cancel();
    }

    /// <summary>
    /// One reading. Every field is guarded on its own and anything unreadable stays null: a run
    /// record is evidence, and a substituted zero inside one cannot be told from a measured zero.
    ///
    /// Called on the load test's own thread, never the UI thread.
    /// </summary>
    private LoadSample Sample(TimeSpan elapsed)
    {
        double? temp = null;
        string sensor = "unavailable";
        try { var r = _ctx.System.ReadTemperature(); temp = r.Celsius; sensor = r.Source.ToString(); } catch { }

        double? watts = null;
        try { watts = _tuning?.ReadPower()?.StapmWatts; } catch { }

        double? ghz = null;
        try { ghz = SystemPerfReader.Read()?.CpuClockGHz; } catch { }

        int? rpm = null;
        try { var levels = _ctx.Fan.GetFanLevel(); if (levels.Length > 0) rpm = FanService.RawToRpm(levels[0]); } catch { }

        // Recorded, but GetThrottling documents itself as unverified on this firmware, so the
        // throttle column is a hint rather than the finding.
        bool throttling = false;
        try { throttling = _ctx.System.GetThrottling() == ThrottlingState.On; } catch { }

        return new LoadSample(elapsed, temp, watts, ghz, rpm, null, throttling, sensor);
    }

    private void ShowSummary(IReadOnlyList<LoadSample> samples)
    {
        var s = RunStats.Summarise(samples);
        RunLive.Text = "";

        if (s.SampleCount == 0)
        {
            RunStatus.Text = "The run produced no samples.";
            return;
        }

        string? path = null;
        try { path = WriteRun(samples); } catch { }

        RunStatus.Text = path is null
            ? $"Finished: {s.SampleCount} samples over {s.Duration.TotalSeconds:0} seconds. The run could not be saved."
            : $"Finished: {s.SampleCount} samples over {s.Duration.TotalSeconds:0} seconds. Saved to {path}";

        AddRow(RunSummary, "Temperature",
            $"median {Show(s.MedianTempC, "0.0")} C, p90 {Show(s.P90TempC, "0.0")} C, max {Show(s.MaxTempC, "0.0")} C");
        AddRow(RunSummary, "Package power", $"median {Show(s.MedianWatts, "0.0")} W");
        AddRow(RunSummary, "CPU clock",
            $"median {Show(s.MedianClockGhz, "0.00")} GHz, floor {Show(s.MinClockGhz, "0.00")} GHz");
        AddRow(RunSummary, "Throttling", s.TimeToThrottle is { } t
            ? $"first seen at {t.TotalSeconds:0}s, {s.ThrottledFraction:P0} of samples"
            : "not observed");
    }

    /// <summary>
    /// Writes the run in its own schema, borrowing ThermalLog's discipline rather than its
    /// columns: invariant culture throughout, since a decimal comma would silently shift every
    /// field for anyone reading this back on a non-English system.
    /// </summary>
    private static string WriteRun(IReadOnlyList<LoadSample> samples)
    {
        Directory.CreateDirectory(ThermalLog.LogDirectory);
        string path = Path.Combine(ThermalLog.LogDirectory, $"loadtest-{DateTime.Now:yyyy-MM-dd-HHmmss}.csv");

        using var w = new StreamWriter(path);
        w.WriteLine("elapsed_s,temp_c,sensor,package_w,cpu_ghz,fan_rpm,throttling");
        foreach (var s in samples)
            w.WriteLine(string.Join(',',
                s.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture),
                Csv(s.TempC, "0.0"), s.Sensor, Csv(s.PackageWatts, "0.0"), Csv(s.CpuClockGhz, "0.00"),
                s.FanRpm?.ToString(CultureInfo.InvariantCulture) ?? "",
                s.Throttling ? "True" : "False"));

        return path;

        // An empty field, never a zero: a reader has to be able to tell "not measured" apart
        // from "measured zero".
        static string Csv(double? v, string f) => v is double d ? d.ToString(f, CultureInfo.InvariantCulture) : "";
    }

    private static string Show(double? value, string format) =>
        value is double v ? v.ToString(format, CultureInfo.InvariantCulture) : "--";

    // ------------------------------------------------------------ capability report

    private void BuildCapabilityRows()
    {
        var caps = _ctx.Capabilities;
        var band = FanService.Calibration;

        AddRow(CapabilityRows, "Vendor interface",
            _ctx.VendorSupported ? "present" : _ctx.VendorUnavailableReason ?? "not available");

        AddRow(CapabilityRows, "Capability block", caps switch
        {
            null => "the command is not implemented on this board",
            { LooksUnreported: true } => "answered, but carrying nothing. Treated as unknown, not as unsupported",
            _ => caps.ToString(),
        });

        AddRow(CapabilityRows, "Software fan control", caps switch
        {
            null or { LooksUnreported: true } => "not stated; the fan readings above are the real evidence",
            { SoftwareFanControl: true } => "supported",
            _ => "the board reports none, and the control is left enabled anyway",
        });

        AddRow(CapabilityRows, "Fan command encoding",
            $"{_ctx.Fan.Encoding} ({(_ctx.Fan.Encoding == ThermalPolicyVersion.Legacy ? "0x00-0x03" : "0x30-0x50")})");

        AddRow(CapabilityRows, "Fans",
            _ctx.FanCount is byte n ? $"{n} reported, {Math.Min((int)n, 2)} driven" : "not reported");

        AddRow(CapabilityRows, "Fan band",
            $"raw {band.MinRawLevel}-{band.MaxRawLevelFan1}"
            + (band.MaxRawLevelFan2 != band.MaxRawLevelFan1 ? $" / {band.MaxRawLevelFan2} on fan 2" : "")
            + $"  ({_ctx.FanCalibrationSource})");

        AddRow(CapabilityRows, "Graphics switching",
            _ctx.GpuModeSwitchAllowed ? "offered" : "the board reports none, and the control is disabled");

        AddRow(CapabilityRows, "Processor tuning",
            _ctx.Smu is null ? _ctx.SmuUnavailableReason ?? "the SMU could not be opened" : "available");
    }

    /// <summary>A label and a value on one line, sharing the fixed label column.</summary>
    private void AddRow(Panel host, string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("TileFoot"),
            Margin = new Thickness(0),
        };
        Grid.SetColumn(name, 0);

        var text = new TextBlock
        {
            Text = value,
            Style = (Style)FindResource("MutedText"),
            FontSize = 11.5,
            Margin = new Thickness(0),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(text, 1);

        grid.Children.Add(name);
        grid.Children.Add(text);
        host.Children.Add(grid);
    }

    // ----------------------------------------------------------------- probe report

    private void SaveProbeBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveProbeBtn.IsEnabled = false;
        ProbeStatus.Text = "Reading the BIOS...";

        // Off the UI thread: a series of hpqBIntM round trips behind one send lock.
        Task.Run(Program.CaptureProbeReport).ContinueWith(t =>
        {
            SaveProbeBtn.IsEnabled = true;
            ProbeStatus.Text = t.IsFaulted
                ? $"The probe failed: {t.Exception?.GetBaseException().Message}"
                : t.Result;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
