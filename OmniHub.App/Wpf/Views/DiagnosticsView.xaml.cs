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

            // Matched as "any non-null" rather than as a discard. A discard does not narrow the
            // null state, so the compiler read this arm's result as possibly-null and warned --
            // the only warning in the build, and one that had been hiding behind a wall of
            // file-lock noise from building over the running application.
            { } block => block.ToString(),
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

        // Which fan is which.
        //
        // GetFanType has been implemented since the fan controller was written and reached only
        // the -Probe console path, so the UI has always said "fan 1" and "fan 2" about a board
        // that will state plainly which one cools the processor and which one cools the graphics.
        AddRow(CapabilityRows, "Fan roles", DescribeFanTypes());

        // The charger.
        //
        // GetAdapter returns a direct "this power supply is below what this laptop requires"
        // diagnosis, and it has never reached a screen. On a machine that throttles under load
        // it is the first thing worth ruling out, and it is one BIOS call away.
        AddRow(CapabilityRows, "Power adapter", DescribeAdapter());

        AddRow(CapabilityRows, "Graphics switching",
            _ctx.GpuModeSwitchAllowed ? "offered" : "the board reports none, and the control is disabled");

        // Where GPU readings come from.
        //
        // GpuSource's own doc comment says "Shown in the UI, because the two sources differ" --
        // and it had zero references in the App project. nvidia-smi reports temperature, power
        // and clock; the Windows counters report utilisation and a name, and nothing else. A
        // reader looking at a blank GPU temperature deserves to know which of those they are
        // looking at, in an application whose first rule is that a reading names its source.
        AddRow(CapabilityRows, "GPU telemetry source", DescribeGpuSource());

        // Per-logical-processor clocks, rather than the one averaged figure from WMI whose own
        // source comment warns it may not track turbo at all.
        AddRow(CapabilityRows, "Processor clocks", DescribeClocks());

        AddRow(CapabilityRows, "Processor tuning",
            _ctx.Smu is null ? _ctx.SmuUnavailableReason ?? "the SMU could not be opened" : "available");

        // The poll re-arms after each tick rather than on a fixed period, so this figure is the
        // real cadence, and it is the number two rounds of optimisation aimed at and missed.
        // Averages are appended to polltiming-*.csv beside the thermal logs.
        AddRow(CapabilityRows, "Poll tick cost", _ctx.LastTickTimings);

        // The two static facts that answer "why is this one slower than the same laptop somebody
        // else has" more often than any tuning knob does. This processor's integrated graphics
        // have no memory of their own, so the channel count is a graphics bandwidth figure; and
        // memory running below its own rating is both common and correctable. Neither has ever
        // been reported anywhere in this application, and neither is visible in Windows without
        // going looking for it.
        // The throttling reading, and whether it is a reading at all.
        //
        // Its source comment has carried the doubt for a long time: GetCapability echoes the
        // selector byte back at exactly the position this value is read from, and the selector
        // sent is 4, which is also ThrottlingState.Default's numeric value. Three read-only
        // queries with different selectors settle it either way, and the answer is shown rather
        // than the suspicion.
        AddRow(CapabilityRows, "Throttling reading", ThrottlingProbe.Run(_ctx.System).Describe());

        AddRow(CapabilityRows, "Memory", SystemInventory.ReadMemory().Describe());

        AddRow(CapabilityRows, "Storage",
            SystemInventory.DescribeStorage(SystemInventory.ReadStorage(out string? driveError), driveError));
    }

    /// <summary>
    /// What the board says each fan is for.
    ///
    /// The reply is a 128-byte block, one byte per fan. Anything past the reported fan count is
    /// not a fan, and an Unsupported entry is the board declining to say rather than a fan that
    /// does nothing -- so both are reported as such instead of being dressed up.
    /// </summary>
    private string DescribeFanTypes()
    {
        try
        {
            byte[] raw = _ctx.Fan.GetFanType();
            if (raw.Length == 0) return "the command returned nothing";

            int count = _ctx.FanCount is byte n && n > 0 ? Math.Min(n, raw.Length) : Math.Min(2, raw.Length);

            var named = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                var type = (FanType)raw[i];
                named.Add($"fan {i + 1}: " + (Enum.IsDefined(type) && type != FanType.Unsupported
                    ? type.ToString().ToLowerInvariant()
                    : $"not stated (0x{raw[i]:X2})"));
            }

            return string.Join(", ", named);
        }
        catch (Exception ex)
        {
            return $"could not be read ({ex.Message})";
        }
    }

    /// <summary>
    /// Whether the attached power supply is big enough for this laptop.
    ///
    /// BelowRequirement is the reading worth having: it is the board stating that the charger
    /// cannot deliver what the machine may ask for, which shows up as throttling under load and
    /// is otherwise invisible. The others are reported plainly rather than collapsed into "OK".
    /// </summary>
    private string DescribeAdapter()
    {
        try
        {
            return _ctx.System.ReadAdapterStatus() switch
            {
                HpAdapterStatus.MeetsRequirement => "meets this laptop's requirement",
                HpAdapterStatus.BelowRequirement =>
                    "BELOW this laptop's requirement. The board is saying the attached supply cannot "
                    + "deliver what the machine may draw, which shows up as throttling under load.",
                HpAdapterStatus.BatteryPower => "on battery; nothing to report",
                HpAdapterStatus.NotFunctioning => "the board reports the adapter as not functioning",
                HpAdapterStatus.NotSupported => "the board does not report adapter status",
                null => "not reported",
                var other => $"reported as {other}",
            };
        }
        catch (Exception ex)
        {
            return $"could not be read ({ex.Message})";
        }
    }

    /// <summary>Which provider answered for the GPU, and what that provider can report.</summary>
    private static string DescribeGpuSource()
    {
        var reading = GpuTelemetry.Read();

        if (reading is null)
            return GpuTelemetry.IsAvailable ? "no reading yet" : "no GPU reported";

        return reading.Source switch
        {
            GpuSource.Nvml =>
                $"NVML ({reading.Name}): temperature, power, clock and utilisation, read directly "
                + "from the driver's own library rather than by launching nvidia-smi, which was "
                + "measured here at 58 to 61 ms per refresh",

            GpuSource.NvidiaSmi =>
                $"nvidia-smi ({reading.Name}): temperature, power, clock and utilisation"
                + (Nvml.UnavailableReason is { Length: > 0 } why
                    ? $". NVML was tried first and declined: {why}"
                    : ". NVML is released while on battery, so this is the mains-only path"),
            GpuSource.WindowsCounters =>
                $"Windows performance counters ({reading.Name}): utilisation only. "
                + "Temperature, power and clock are not exposed by this source and read as unavailable.",
            var other => other.ToString(),
        };
    }

    /// <summary>
    /// What each logical processor is doing, and whether Windows is holding any of them down.
    ///
    /// The cap is the part worth having. When a power policy or a thermal event lowers the
    /// ceiling, MhzLimit drops below MaxMhz and the platform says so -- a constraint the machine
    /// is genuinely under, reported rather than inferred from a clock that happens to look low.
    /// </summary>
    private static string DescribeClocks()
    {
        var clocks = ProcessorClocks.Read();
        if (clocks.Count == 0) return "not reported by this platform";

        int peak = ProcessorClocks.PeakMhz(clocks) ?? 0;
        int max = clocks.Max(c => c.MaxMhz);
        int parked = clocks.Count(c => c.CurrentMhz < max / 4);

        string text = $"{clocks.Count} logical, peak {peak} MHz of {max} MHz nominal";
        if (parked > 0) text += $", {parked} near idle";

        if (ProcessorClocks.IsCapped(clocks))
        {
            int limit = clocks.Where(c => c.LimitMhz > 0).Min(c => c.LimitMhz);
            text += $". Windows is holding the ceiling at {limit} MHz, below the {max} MHz nominal.";
        }

        return text;
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

    /// <summary>
    /// Writes everything needed to diagnose this machine to one archive.
    ///
    /// The user picks where. Somewhere convenient is offered as a default rather than chosen for
    /// them, because the archive carries their settings and their machine's activity times, and a
    /// file like that should land where they said and nowhere else.
    /// </summary>
    private void SaveBundleBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save OmniHub support bundle",
            FileName = $"omnihub-support-{DateTime.Now:yyyy-MM-dd-HHmmss}.zip",
            DefaultExt = ".zip",
            Filter = "Zip archive (*.zip)|*.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };

        if (dialog.ShowDialog() != true) return;

        SaveBundleBtn.IsEnabled = false;
        BundleStatus.Text = "Collecting...";

        // The notes are gathered here rather than inside the bundle, because this is where the
        // hardware has already been asked. Building an archive should not become a reason to
        // start another round of BIOS round trips.
        var notes = new Dictionary<string, string>
        {
            ["capabilities.txt"] = DescribeCapabilities(),
            ["system.txt"] = DescribeSystem(),
        };

        string path = dialog.FileName;

        Task.Run(() => SupportBundle.Create(path, notes)).ContinueWith(t =>
        {
            SaveBundleBtn.IsEnabled = true;

            if (t.IsFaulted)
            {
                BundleStatus.Text = $"The bundle failed: {t.Exception?.GetBaseException().Message}";
                return;
            }

            var result = t.Result;

            BundleStatus.Text = result.Error is { Length: > 0 } error
                ? $"The bundle could not be written: {error}"
                : $"Wrote {result.Entries.Count} file(s), {result.Bytes / 1024.0 / 1024.0:0.#} MB "
                  + $"before compression, to {result.Path}. MANIFEST.txt inside lists every one.";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>The capability rows as text, so the archive carries what the screen shows.</summary>
    private string DescribeCapabilities()
    {
        var text = new System.Text.StringBuilder();

        foreach (var child in CapabilityRows.Children.OfType<Grid>())
        {
            var blocks = child.Children.OfType<TextBlock>().ToList();
            if (blocks.Count >= 2) text.AppendLine($"{blocks[0].Text}: {blocks[1].Text}");
        }

        return text.Length > 0 ? text.ToString() : "The capability rows had not been built yet.";
    }

    /// <summary>Model, memory and storage, which is the first thing anybody asks about a machine.</summary>
    private string DescribeSystem()
    {
        var text = new System.Text.StringBuilder();

        text.AppendLine($"{_ctx.Model.Manufacturer} {_ctx.Model.Product}".Trim());
        text.AppendLine($"Baseboard {_ctx.Model.BaseboardProduct}");
        text.AppendLine($"{Environment.OSVersion.VersionString}, {Environment.ProcessorCount} logical processors");
        text.AppendLine();
        text.AppendLine(SystemInventory.ReadMemory().Describe());
        text.AppendLine();
        text.AppendLine(SystemInventory.DescribeStorage(
            SystemInventory.ReadStorage(out string? driveError), driveError));

        return text.ToString();
    }
}
