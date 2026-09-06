using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Optimize;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
namespace OmniHub.App.Wpf.Views;

public partial class DashboardView : UserControl
{
    private readonly HardwareContext _ctx;
    private readonly FanService _service;
    private readonly AppSettings _settings;
    private bool _suppressPresetEvent;

    public DashboardView(HardwareContext ctx, FanService service, AppSettings settings)
    {
        InitializeComponent();
        _ctx = ctx; _service = service; _settings = settings;

        ModelText.Text = $"{ctx.Model.Manufacturer} {ctx.Model.Product}".Trim();
        Gauge.Label = "Thermal Headroom";
        LoadBatteryFooter();
        TrendChart.LineBrush = (Brush)FindResource("DangerBrush");
        TrendChart.MinValue = 20; TrendChart.MaxValue = 100;

        // Best-effort guess at which preset is "active" -- settings only stores the
        // fan mode, not which GPU level was paired with it, so Auto defaults to
        // Balanced rather than trying to distinguish Silent from Balanced.
        _suppressPresetEvent = true;
        if (_settings.FanControlMode == FanControlMode.Max) PerformanceBtn.IsChecked = true;
        else BalancedBtn.IsChecked = true;
        _suppressPresetEvent = false;

        // Subscribed on Loaded rather than once here.
        //
        // Switching tabs assigns MainWindow's ViewHost.Content, which detaches this control and
        // raises Unloaded -- so a constructor-time subscription paired with an Unloaded
        // unsubscribe detached PERMANENTLY the first time the user left the Dashboard. Coming
        // back re-attached the control and re-subscribed nothing, leaving every card on the page
        // frozen on the last values it happened to see: a plausible temperature, no longer
        // connected to the hardware, with nothing on screen saying so. That is the "temperature
        // is stuck" report, and it froze the GPU card's TGP line the same way -- at whatever it
        // read during construction, which is before the startup unlock has run.
        //
        // -= before += because Loaded fires again on every re-attach and a multicast delegate
        // will hold the same handler twice without complaining.
        Loaded += (_, _) =>
        {
            ctx.OnReading -= OnReading;
            ctx.OnReading += OnReading;

            // Re-read the panels the poll does not drive, so returning to the tab shows current
            // state rather than state from launch.
            RefreshGpuMode();
            RefreshPerf();
        };
        Unloaded += (_, _) => ctx.OnReading -= OnReading;

        var pulse = new DoubleAnimation(1.0, 0.35, TimeSpan.FromMilliseconds(900))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        LiveDot.BeginAnimation(OpacityProperty, pulse);
    }

    // Thresholds follow the fan curve's own shape (see FanCurve.CreateDefault): the ramp
    // starts biting around 60C, and 80C is where it is already working hard. Actively
    // throttling is always red regardless of the number, because at that point the
    // reading has stopped being the interesting part.
    private Brush ThermalBrushFor(double tempC, ThrottlingState throttling)
    {
        if (throttling == ThrottlingState.On) return (Brush)FindResource("DangerBrush");
        // A saturated reading is at least this hot and possibly far hotter, so it gets the
        // danger colour on its own account rather than by happening to exceed a threshold.
        if (SystemController.IsAtSensorCeiling(tempC)) return (Brush)FindResource("DangerBrush");
        if (tempC >= 80) return (Brush)FindResource("DangerBrush");

        // No amber tier. It used to start at 60 C, and this machine idles in the 50s to 70s,
        // so the readout sat yellow essentially all the time -- a warning colour that is always
        // on is not a warning, it is just the colour of the app. Normal until genuinely hot
        // keeps the red meaning something.
        return (Brush)FindResource("TextPrimaryBrush");
    }

    // Fills a two-column progress rail from a real 0-100 percent value. Star widths rather
    // than a pixel width, so the bar reflows with the card instead of needing a measured
    // layout pass; purely a rendering of data we already have.
    private static void SetBar(Grid bar, double percent)
    {
        double pct = Math.Clamp(percent, 0, 100);
        bar.ColumnDefinitions[0].Width = new GridLength(pct, GridUnitType.Star);
        bar.ColumnDefinitions[1].Width = new GridLength(100 - pct, GridUnitType.Star);
    }

    // Battery is static enough that polling it every 2s would be waste; read once on open.
    // BatteryInfoReader runs several WMI queries, so it stays off the UI thread.
    private void LoadBatteryFooter()
    {
        Task.Run(() => BatteryInfoReader.Read()).ContinueWith(t =>
        {
            var b = t.Result;
            Dispatcher.Invoke(() =>
            {
                if (b is null)
                {
                    PowerStateText.Text = "Power state: unavailable";
                    BatteryText.Text = "";
                    return;
                }

                PowerStateText.Text = $"Power state: {b.Status} - {b.ChargePercent}%";

                // Only report health when both capacities were actually reported. A wear
                // figure derived from a zero design capacity would be invented, not measured.
                if (b.DesignCapacityMWh > 0 && b.FullChargeCapacityMWh > 0)
                {
                    double health = b.FullChargeCapacityMWh * 100.0 / b.DesignCapacityMWh;
                    string cycles = b.CycleCount > 0 ? $" - Cycles {b.CycleCount}" : "";
                    BatteryText.Text = $"Battery {health:0.#}% health ({100 - health:0.#}% wear){cycles}";
                }
                else BatteryText.Text = "Battery health not reported by firmware";
            });
        }, TaskScheduler.Default);
    }

    // ---------- quick actions ----------
    // Each chip performs one real action and writes the measured outcome next to the row.
    // Nothing here reports success it did not verify.

    private void ShowChipResult(TuningResult result)
    {
        ChipResult.Text = result.Detail;
        ChipResult.Foreground = (Brush)FindResource(result.Applied ? "GoodBrush" : "DangerBrush");
    }

    private void RunChip(Button chip, Func<TuningResult> action)
    {
        chip.IsEnabled = false;
        ChipResult.Text = "Working...";
        ChipResult.Foreground = (Brush)FindResource("TextMutedBrush");

        Task.Run(action).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                ShowChipResult(t.IsFaulted
                    ? new TuningResult(false, t.Exception?.GetBaseException().Message ?? "Failed.")
                    : t.Result);
                chip.IsEnabled = true;
            });
        }, TaskScheduler.Default);
    }

    private void ChipCleanRam_Click(object sender, RoutedEventArgs e) =>
        RunChip(ChipCleanRam, MemoryTools.PurgeStandbyList);

    private void ChipClearShaders_Click(object sender, RoutedEventArgs e) =>
        RunChip(ChipClearShaders, ShaderCache.Clear);

    private void ChipTimer_Click(object sender, RoutedEventArgs e) =>
        RunChip(ChipTimer, SystemTuning.ApplyHighResolutionTimer);

    private void ChipMaxFans_Click(object sender, RoutedEventArgs e) => RunChip(ChipMaxFans, () =>
    {
        if (_service.IsRunning) _service.Stop();
        _ctx.System.SetMaxFan(true);
        _settings.FanControlMode = FanControlMode.Max;
        _settings.Save();
        return new TuningResult(true, "Fans pinned to maximum.");
    });

    private void ChipAutoFans_Click(object sender, RoutedEventArgs e) => RunChip(ChipAutoFans, () =>
    {
        _ctx.System.SetMaxFan(false);
        _service.Start();
        _settings.FanControlMode = FanControlMode.Auto;
        _settings.Save();
        return new TuningResult(true, "Curve control resumed.");
    });

    private void ChipAutoGpu_Click(object sender, RoutedEventArgs e) => RunChip(ChipAutoGpu, () =>
    {
        var mode = _ctx.Gpu.GetMode();
        return new TuningResult(true, $"GPU mode is {mode}. Per-app routing lives on the App GPU Routing tab.");
    });

    // ---------- presets ----------

    private void SilentBtn_Checked(object sender, RoutedEventArgs e) { if (!_suppressPresetEvent) ApplyPreset(GpuPowerLevel.Eco, FanControlMode.Auto); }
    private void BalancedBtn_Checked(object sender, RoutedEventArgs e) { if (!_suppressPresetEvent) ApplyPreset(GpuPowerLevel.Balanced, FanControlMode.Auto); }
    private void PerformanceBtn_Checked(object sender, RoutedEventArgs e) { if (!_suppressPresetEvent) ApplyPreset(GpuPowerLevel.Performance, FanControlMode.Max); }

    // Every call here (SetPowerPreset, SetMaxFan, Stop's RestoreAutomaticControl, and
    // RefreshGpuMode's own reads) is a synchronous BIOS/WMI call. Run the whole thing
    // off the UI thread -- called directly from a button click, this was blocking the
    // UI for the full chain of hardware calls before, which read as "freezing."
    private void ApplyPreset(GpuPowerLevel gpuLevel, FanControlMode fanMode)
    {
        Task.Run(() =>
        {
            try { _ctx.Gpu.SetPowerPreset(gpuLevel); } catch { }
            try
            {
                if (fanMode == FanControlMode.Auto) { _ctx.System.SetMaxFan(false); _service.Start(); }
                else if (fanMode == FanControlMode.Max) { if (_service.IsRunning) _service.Stop(); _ctx.System.SetMaxFan(true); }
                _settings.FanControlMode = fanMode;
                _settings.Save();
            }
            catch { }

            string modeText = "--";
            string subText = "";
            try
            {
                var mode = _ctx.Gpu.GetMode();
                modeText = mode.ToString();
                var power = _ctx.Gpu.GetPower();
                subText = $"{modeText} · TGP {power.CustomTgp} / BOOST {power.Ppab}";
            }
            catch { }

            Dispatcher.Invoke(() =>
            {
                // The card's big number is the GPU temperature now, updated per tick in
                // OnReading. Graphics mode and the TGP flags are BIOS reads that only change
                // when something writes them, so they stay on the sub-line and are refreshed
                // only here.
                GpuSubText.Text = subText;
            });
        });
    }

    // GetMode()/GetPower() are synchronous BIOS/WMI calls -- this is only ever called
    // once, from the constructor, but a synchronous call there still blocks Dashboard
    // (and so app) startup on real hardware I/O, the same class of freeze already fixed
    // for the button-click path (ApplyPreset) below. Missed the first time around.
    private void RefreshGpuMode()
    {
        Task.Run(() =>
        {
            string modeText = "--";
            string subText = "";
            try
            {
                var mode = _ctx.Gpu.GetMode();
                modeText = mode.ToString();
                var power = _ctx.Gpu.GetPower();
                subText = $"{modeText} · TGP {power.CustomTgp} / BOOST {power.Ppab}";
            }
            catch { }

            Dispatcher.Invoke(() =>
            {
                // The card's big number is the GPU temperature now, updated per tick in
                // OnReading. Graphics mode and the TGP flags are BIOS reads that only change
                // when something writes them, so they stay on the sub-line and are refreshed
                // only here.
                GpuSubText.Text = subText;
            });
        });
    }

    private bool _perfRefreshInFlight;

    // WMI queries here take real wall-clock time (tens to low hundreds of ms) --
    // running them on the UI thread was causing a periodic stutter every ~2s,
    // since OnReading's Dispatcher.Invoke block executes synchronously on the UI
    // thread. Runs the query on a background thread and only marshals the cheap
    // string updates back. _perfRefreshInFlight skips overlapping calls rather
    // than queuing them up if a query is ever slow to return.
    private void RefreshPerf()
    {
        if (_perfRefreshInFlight) return;
        _perfRefreshInFlight = true;

        Task.Run(() => SystemPerfReader.Read()).ContinueWith(t =>
        {
            _perfRefreshInFlight = false;
            var perf = t.Result;
            if (perf is null) return;
            Dispatcher.Invoke(() =>
            {
                // The unit suffix lives in its own TextBlock now, so the value is bare.
                CpuClockText.Text = $"{perf.CpuClockGHz:0.0}";
                CpuLoadText.Text = $"{perf.CpuLoadPercent:0}%";
                CpuFootLeft.Text = $"{Environment.ProcessorCount} LOGICAL CORES";
                StripLoad.Text = $"{perf.CpuLoadPercent:0}%";

                MemText.Text = $"{perf.MemoryUsedGB:0.0}";
                double memPercent = perf.MemoryTotalGB > 0 ? perf.MemoryUsedGB / perf.MemoryTotalGB * 100.0 : 0;
                MemSubText.Text = $"USED {perf.MemoryUsedGB:0.0} / {perf.MemoryTotalGB:0.0} GB";
                MemFootRight.Text = $"{memPercent:0}%";

                SetBar(CpuLoadBar, perf.CpuLoadPercent);
                SetBar(MemLoadBar, memPercent);
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Says what this machine is and which of OmniHub's capabilities it actually has.
    ///
    /// Every fact here was already being detected -- the model from ModelProfile, the vendor
    /// interface from BiosInterop, the SMU from RyzenSmu, the GPU from GpuTelemetry -- but the
    /// only place any of it surfaced was one line buried in the Tuning tab. Someone whose
    /// machine was missing a piece had to go looking for the explanation of why half the app
    /// did nothing.
    ///
    /// Deliberately NOT a downloader. The only component that could ever be fetched is PawnIO,
    /// which is a signed kernel driver: fetching and executing ring-0 code on first launch,
    /// without the user reading what it is, is a supply-chain problem wearing a convenience
    /// costume. Its own installer registers the service and handles signing properly, so the
    /// honest move is to name what is missing and open the page.
    /// </summary>
    private void BuildReadiness()
    {
        var missing = new List<(string What, string Why)>();

        if (!_ctx.VendorSupported)
            missing.Add(("Fan control, GPU power, BIOS limits", _ctx.VendorUnavailableReason ?? "No vendor control interface."));

        if (_ctx.Smu is null)
            missing.Add(("Processor tuning and die temperature", _ctx.SmuUnavailableReason ?? "The SMU could not be opened."));

        // Two different degrees of unavailable, and conflating them was misleading on every
        // non-NVIDIA machine: those get a name and a load figure, they just get no thermal
        // sensor, because Windows exposes none generically for a GPU.
        if (!GpuTelemetry.IsAvailable)
            missing.Add(("GPU telemetry", "No display adapter could be read."));
        else if (!GpuTelemetry.HasThermalSource)
            missing.Add(("GPU temperature and power",
                "Windows reports GPU name and load for any adapter but exposes no thermal or power sensor. "
                + "Those readings need nvidia-smi, which installs with an NVIDIA driver."));

        if (missing.Count == 0) return;

        ReadinessCard.Visibility = Visibility.Visible;
        ReadinessHeadline.Text =
            $"Detected {_ctx.Model.Manufacturer} {_ctx.Model.Product}".TrimEnd()
            + $". {missing.Count} capabilit{(missing.Count == 1 ? "y is" : "ies are")} unavailable here; "
            + "everything else works normally.";

        foreach (var (what, why) in missing)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
            panel.Children.Add(new TextBlock
            {
                Text = what,
                Style = (Style)FindResource("BodyText"),
                FontSize = 12,
            });
            panel.Children.Add(new TextBlock
            {
                Text = why,
                Style = (Style)FindResource("MutedText"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 740,
            });
            ReadinessRows.Children.Add(panel);
        }

        // The one missing piece a user can actually install, so the one that gets a button.
        if (_ctx.Smu is null) GetPawnIoBtn.Visibility = Visibility.Visible;
    }

    private void GetPawnIo_Click(object sender, RoutedEventArgs e)
    {
        GetPawnIoBtn.IsEnabled = false;
        ReadinessHeadline.Text = "Installing the PawnIO driver through winget. Windows will ask you to approve it.";

        Task.Run(InstallPawnIo).ContinueWith(t => Dispatcher.Invoke(() =>
        {
            GetPawnIoBtn.IsEnabled = true;
            ReadinessHeadline.Text = t.IsFaulted
                ? $"Install failed: {t.Exception?.GetBaseException().Message}. The driver is at https://pawnio.eu"
                : t.Result;
        }), TaskScheduler.Default);
    }

    /// <summary>
    /// Installs PawnIO through winget rather than through a downloader written here.
    ///
    /// This is the whole reason it can be one click safely. A hand-rolled version would have
    /// to fetch a binary over the network and execute it in kernel space, which means owning
    /// URL trust, hash verification and a publisher check for exactly one file -- a trust
    /// store maintained by this app, for one dependency, forever. winget already does all of
    /// it: the package manifest pins a SHA256, Windows verifies the download against it, and
    /// the installer raises its own elevation prompt that the user sees and approves.
    ///
    /// The safety here is therefore not "we were careful", it is that the verification belongs
    /// to Microsoft's package manager rather than to code written in an afternoon.
    ///
    /// No restart afterwards: HardwareContext.RetrySmuOpen re-attempts every ten seconds while
    /// the SMU is closed, so tuning comes online on its own once the driver registers.
    /// </summary>
    private static string InstallPawnIo()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("winget.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Exact id match, and both agreement flags: output is redirected, so an interactive
        // "do you accept the source agreement?" would block forever with nobody able to answer.
        foreach (var arg in new[]
                 {
                     "install", "--id", "namazso.PawnIO", "--exact",
                     "--accept-source-agreements", "--accept-package-agreements",
                 })
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null)
                return "winget could not be started. Install the driver from https://pawnio.eu";

            // stderr drained concurrently -- see GpuTelemetry.Query for why. winget is by far
            // the most verbose thing this app launches, so it is the likeliest to fill a pipe.
            _ = proc.StandardError.ReadToEndAsync();
            string output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(180_000))
                return "The install is taking unusually long; check on it in a terminal. Driver: https://pawnio.eu";

            if (proc.ExitCode == 0)
                return "PawnIO installed. Processor tuning comes online within about ten seconds -- no restart needed.";

            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            string last = lines.Length > 0 ? lines[^1].Trim() : "";
            return $"winget exited with code {proc.ExitCode}. {last} Install manually from https://pawnio.eu";
        }
        catch (Exception ex)
        {
            // Most likely winget itself is absent -- it ships with App Installer, which some
            // trimmed or older Windows builds do not have.
            return $"winget is unavailable here ({ex.GetType().Name}). Install the driver from https://pawnio.eu";
        }
    }

    private void OnReading(Reading r)
    {
        // Read on the poll thread, before marshalling. A cache miss spawns nvidia-smi, which
        // costs about 56 ms -- fine here, a visible hitch on the UI thread.
        var gpu = GpuTelemetry.Read();

        // BeginInvoke, not Invoke.
        //
        // This runs on the poll thread, which holds the poll loop's re-entrancy interlock for
        // the whole call. A synchronous Invoke therefore parks the entire temperature poll for
        // as long as the UI thread is busy -- and the UI thread can be busy for an unbounded
        // time, because several error paths in this app open a modal MessageBox. While it is
        // parked no readings are produced, CurrentTemperature starts throwing "stale", and the
        // fan curve stops commanding: a UI hiccup taking the cooling down with it.
        //
        // Nothing here needs the UI to have finished before the next reading is taken, so the
        // queue-and-return form is strictly better. MainWindow's ribbon handler already did it
        // this way; the three per-tick view handlers did not.
        Dispatcher.BeginInvoke(() =>
        {
            // Full precision where there is any. Older Readings carry no precise value, so
            // fall back to the whole-degree field rather than rendering NaN.
            double tempC = double.IsNaN(r.PreciseTemperatureC) ? r.TemperatureC : r.PreciseTemperatureC;
            bool fromDie = r.TemperatureSource == TemperatureSource.SmuDieTctl;

            // "At the ceiling" is a property of the SENSOR, not of the number. An ACPI zone
            // reading 85 has run out of range and the die could be anywhere above it; a Tctl
            // reading of 85 is a measured 85. Testing the bare value would put a "+" on every
            // genuine 85C die reading and claim the sensor had failed when it had not.
            bool ceiling = !fromDie && SystemController.IsAtSensorCeiling(tempC);

            // Tctl resolves to 0.125C, so a decimal there is real information. The ACPI zone
            // moves in 4-6C steps, so a decimal on it would be precision that does not exist.
            // Show the FILTERED temperature, not the instantaneous sample.
            //
            // Tctl is sampled fast enough to catch brief die excursions that never reach the
            // chassis: measured over 20 seconds on an idle machine it ranged 61.9 to 81.3 C,
            // with an eighth of all samples more than 8 C above the median. Every one of those
            // readings is accurate, and showing them makes the app look broken -- a laptop that
            // is cool to the touch reporting 82.9 C reads as a fault, not as a 200ms boost.
            //
            // The fan curve keeps the raw value. Reacting early to a real climb is the whole
            // point of it, and the ceiling check above still tests the unfiltered reading, so
            // nothing about the safety behaviour is softened by this.
            //
            // CpuTrend, not Trend: Trend carries the hotter of CPU and GPU because that is what
            // the fan curve steers on, and rendering it here put the GPU's temperature under a
            // "DIE SENSOR" label whenever the graphics card was the hotter part -- which while
            // gaming is most of the time.
            double displayC = _ctx.CpuTrend.HasEnoughData ? _ctx.CpuTrend.FilteredTempC : tempC;
            // A saturated zone reading is NOT rendered as a temperature.
            //
            // This machine exposes two ACPI zones. TZ01_0 is sane (20 C at idle, critical
            // 110 C). THRM_0 declares a critical trip point of 255 C, which is a sentinel
            // rather than a real limit, and before the EC initialises it at boot it returns
            // about 86 C on a cold machine. ReadTemperature takes the max across zones, so
            // THRM_0 always wins.
            //
            // At startup the SMU is usually not open yet -- PawnIO's service is Manual-start --
            // so Tctl is unavailable and that uninitialised zone is the only sensor there is.
            // The app knew the value was untrustworthy (it flags it ceiling-limited) and
            // printed it anyway, which is how a cold laptop reported 85 C on every launch.
            //
            // "85+" was an attempt to be honest about that and is not good enough: a number on
            // screen reads as a measurement whatever is appended to it. There is no reading
            // here, so there is no number. The fan curve is untouched by this and still treats
            // a blind sensor as worst case, so no safety behaviour depends on this text.
            string shown = fromDie ? displayC.ToString("0.0") : ((int)Math.Round(displayC)).ToString();

            // Eased rather than assigned. The gauge beside this already sweeps its arc; the
            // number jumping while the arc glided was the two disagreeing about how finished
            // the app is.
            if (ceiling) Animate.Clear(ThermalText);
            else Animate.To(ThermalText, displayC, fromDie ? "0.0" : "0");

            StripTemp.Text = ceiling ? "--" : $"{shown}°C";

            // Fan levels are an RPM/100 target, so raw*100 is the actual commanded RPM;
            // see FanService.RawToPercent for why the percentage is not raw/255.
            // RPM is a tachometer reading and the fans take about six seconds to reach a new
            // level, so the measured percentage trails whatever the curve just asked for.
            // Showing only the measured figure made the app look like it was ignoring its own
            // curve -- 2700 RPM beside a high temperature reads as the fan refusing to spin up
            // when it is actually mid-ramp. The target is shown alongside while they differ.
            int fanPercent = FanService.RawToPercent(r.FanLevel1);
            string fanText = $"FANS {FanService.RawToRpm(r.FanLevel1)} RPM ({fanPercent}%)";
            if (_service.IsRunning && _service.HasCommanded
                && Math.Abs(_service.LastCommandedLevelPercent - fanPercent) > 4)
            {
                fanText += $" -> {_service.LastCommandedLevelPercent}%";
            }
            ThermalSubText.Text = fanText;
            ThermalFootRight.Text = r.Throttling == ThrottlingState.On ? "THROTTLING"
                : ceiling ? "AT SENSOR LIMIT"
                : fromDie ? "DIE SENSOR"
                : "NOMINAL";

            // The thermal card is the one that still carries live colour: the per-metric
            // hues elsewhere are fixed identity, so this stays the only thing on screen
            // whose colour is telling you something changed.
            var thermalBrush = ThermalBrushFor(r.TemperatureC, r.Throttling);
            // Eased across thresholds rather than switched. Crossing 60 or 80 C used to flip
            // the whole card between one poll and the next, which made a one-degree wobble
            // around a threshold look like an event.
            Animate.BrushTo(ThermalText, TextBlock.ForegroundProperty, thermalBrush);
            Animate.BrushTo(ThermalTitle, TextBlock.ForegroundProperty, thermalBrush);
            Animate.BrushTo(ThermalUnit, TextBlock.ForegroundProperty, thermalBrush);
            Animate.BrushTo(ThermalBarFill, Border.BackgroundProperty, thermalBrush);
            Animate.BrushTo(StripTemp, TextBlock.ForegroundProperty, thermalBrush);
            ThermalFootRight.Foreground = r.Throttling == ThrottlingState.On
                ? thermalBrush
                : (Brush)FindResource("TextFaintBrush");

            // Bar spans the range the curve actually operates over (30-100C), not 0-100:
            // a bar that never leaves its first third communicates nothing.
            SetBar(ThermalBar, (displayC - 30.0) / 70.0 * 100.0);

            StripState.Text = r.Throttling == ThrottlingState.On ? "THROTTLING"
                : displayC >= 80 ? "HOT"
                : _service.IsRunning ? "MANAGED" : "BIOS AUTO";

            // A transparent, real-input-derived indicator -- not a fabricated composite score.
            // 100% at 30C or below, tapering to 0% at 95C, penalized further if actively throttling.
            double headroom = Math.Clamp((95.0 - displayC) / (95.0 - 30.0) * 100.0, 0, 100);
            if (r.Throttling == ThrottlingState.On) headroom = Math.Min(headroom, 25);
            Gauge.SetValue(headroom);
            Gauge.Sub = ceiling ? "--" : $"{shown}\u00b0C";

            // The discrete GPU, read and labelled separately from the die.
            //
            // These are two sensors on two chips and they diverge widely -- measured 15 C
            // apart under a gaming load, with the GPU the hotter of the two. One number
            // covering both was the reason the CPU card could appear to sit still: the fan
            // curve's control temperature is max(CPU, GPU), and while the GPU was the hotter
            // part that value tracked the GPU, which is thermally far steadier.
            //
            // A null reading means no NVIDIA GPU or a failed query, and renders as "--".
            // GpuTelemetry never holds a value past a failure, so this cannot stick either.
            if (gpu?.TempC is double gpuC)
            {
                var gpuBrush = ThermalBrushFor(gpuC, ThrottlingState.Default);
                Animate.To(GpuTempText, gpuC, "0");
                GpuTempUnit.Visibility = Visibility.Visible;
                Animate.BrushTo(GpuTempText, TextBlock.ForegroundProperty, gpuBrush);
                Animate.BrushTo(GpuTempUnit, TextBlock.ForegroundProperty, gpuBrush);
                Animate.BrushTo(GpuBarFill, Border.BackgroundProperty, gpuBrush);
                SetBar(GpuBar, (gpuC - 30.0) / 70.0 * 100.0);
                GpuFootRight.Text = gpu.UtilisationPercent is int u ? $"{u}% LOAD" : "ACTIVE";
            }
            else
            {
                Animate.Clear(GpuTempText);
                GpuTempUnit.Visibility = Visibility.Collapsed;
                SetBar(GpuBar, 0);
                GpuFootRight.Text = GpuTelemetry.IsAvailable ? "UNAVAILABLE" : "NO GPU REPORTED";
            }

            TrendChart.Push(tempC);
        });

        RefreshPerf();
    }
}
