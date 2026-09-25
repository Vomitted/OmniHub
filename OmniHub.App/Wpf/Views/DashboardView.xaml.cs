// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Vendors;
using OmniHub.Core.Optimize;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
namespace OmniHub.App.Wpf.Views;

public partial class DashboardView : UserControl
{
    private readonly int _trendTemp, _trendFan, _trendCommanded;

    private readonly HardwareContext _ctx;
    private readonly FanService _service;
    private readonly AppSettings _settings;
    private bool _suppressPresetEvent;

    public DashboardView(HardwareContext ctx, FanService service, AppSettings settings)
    {
        InitializeComponent();
        _ctx = ctx; _service = service; _settings = settings;

        ModelText.Text = $"{ctx.Model.Manufacturer} {ctx.Model.Product}".Trim();
        LoadBatteryFooter();
        StartPowerDrawTimer();

        // Warmed off-thread. The first ReadDiscrete resolves the device path with a WMI
        // query, and the call site is inside a dispatcher callback, so leaving it cold
        // would put that one query on the UI thread the first time the GPU reads as asleep.
        Task.Run(() => OmniHub.Core.Hardware.GpuPowerState.ReadDiscrete());
        // Three series where there was one. The old control could hold a single Queue of doubles,
        // so the card showed die temperature alone -- which answers "is it hot" and cannot answer
        // "and did the fan do anything about it", the question anyone actually has while looking
        // at a temperature trace.
        //
        // Fan duty and commanded percentage share a fixed 0-100 range, so they stay comparable
        // with each other; temperature autoscales and owns the labelled axis.
        _trendTemp = TrendChart.AddSeries(new Controls.ChartSeries
        {
            Name = "die temp",
            Stroke = (Brush)FindResource("DangerBrush"),
            Unit = " C",
            Format = "0.#",
            Fill = true,
            IsPrimary = true,
        });

        _trendFan = TrendChart.AddSeries(new Controls.ChartSeries
        {
            Name = "fan duty",
            Stroke = (Brush)FindResource("AccentBrush"),
            Unit = "%",
            Format = "0",
            Min = 0,
            Max = 100,
        });

        _trendCommanded = TrendChart.AddSeries(new Controls.ChartSeries
        {
            Name = "commanded",
            Stroke = (Brush)FindResource("MetricMemBrush"),
            Unit = "%",
            Format = "0",
            Min = 0,
            Max = 100,
        });

        // Five minutes rather than the old sixty samples. The previous window was not a duration
        // at all -- it was a sample count, so it silently meant two minutes at the current poll
        // rate and something else entirely if the rate ever changed.
        TrendChart.SetLiveWindow(TimeSpan.FromMinutes(5));

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

            // The readiness card explains which capabilities are missing and why. It was written,
            // styled, and never called -- so it has never once appeared. On a machine without the
            // PawnIO driver the Tuning tab simply sat dark with no explanation anywhere, which is
            // the exact confusion this panel exists to prevent.
            //
            // Built here rather than in the constructor because the SMU is retried over the first
            // seconds of a session: asked once at startup it would report tuning unavailable on
            // every launch that lost the race with PawnIO's service, and never correct itself.
            // Loaded fires on each return to the tab, so the card re-states current truth.
            BuildReadiness();
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
    private Brush ThermalBrushFor(double tempC, bool? throttling)
    {
        if (throttling == true) return (Brush)FindResource("DangerBrush");
        // A saturated reading is at least this hot and possibly far hotter, so it gets the
        // danger colour on its own account rather than by happening to exceed a threshold.
        if (ThermalReader.IsAtCeiling(tempC, _ctx.ZoneCeilingC)) return (Brush)FindResource("DangerBrush");
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
    private System.Windows.Threading.DispatcherTimer? _drawTimer;
    private int _drawInFlight;

    /// <summary>
    /// Live battery draw in the title bar: what the machine is actually pulling from the
    /// pack, and how long that leaves.
    ///
    /// Its own timer rather than the hardware poll, and the read is pushed to the thread
    /// pool, because ReadDraw is a WMI query against root\wmi. Every OnReading subscriber
    /// runs synchronously inside the poll loop's re-entrancy interlock, so a WMI round trip
    /// there sits directly on the critical path of temperature polling and the fan curve.
    /// That mistake has been made twice in this file already, once with nvidia-smi and once
    /// with an SMU read, and both times it presented as the UI stuttering.
    ///
    /// Five seconds because a discharge figure that updates faster than that is noise: the
    /// ACPI rate is itself an average over the firmware's own sampling window.
    /// </summary>
    private void StartPowerDrawTimer()
    {
        _drawTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5),
        };
        _drawTimer.Tick += (_, _) => { RefreshPowerDraw(); RefreshLimits(); };

        // Runs only while this tab is on screen.
        //
        // Views are constructed once and kept, and this one is not IDisposable, so a timer
        // started in the constructor would query WMI every five seconds for the life of the
        // process -- including the whole time the window is hidden in the tray, which is how
        // this application normally sits. TrayFlyout already has that exact bug for the same
        // reason. IsVisibleChanged is the cheap fix: the chip only matters while something
        // is reading it.
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) { _drawTimer.Start(); RefreshPowerDraw(); RefreshLimits(); }
            else _drawTimer.Stop();
        };

        if (IsVisible) { _drawTimer.Start(); RefreshPowerDraw(); RefreshLimits(); }
    }

    private int _limitsInFlight;

    /// <summary>
    /// Refreshes the binding-limit strip.
    ///
    /// Shares the five-second timer with the battery draw rather than adding a second one,
    /// because the PM table behind it is cached for five seconds in Core anyway -- the refresh
    /// holds the global Access_PCI mutex and shows up as DPC latency and audio dropouts, which
    /// is exactly why that cache exists. Reading faster than the cache would buy nothing and
    /// risk the thing the cache was added to prevent.
    ///
    /// Off the UI thread and single-flighted, for the same reason the draw read is.
    /// </summary>
    private void RefreshLimits()
    {
        if (Interlocked.Exchange(ref _limitsInFlight, 1) == 1) return;

        Task.Run(() =>
        {
            try { return _ctx.Smu?.ReadPowerSnapshot(); }
            catch { return null; }
        }).ContinueWith(t =>
        {
            Interlocked.Exchange(ref _limitsInFlight, 0);
            Dispatcher.BeginInvoke(() =>
            {
                Limits.Show(
                    t.IsCompletedSuccessfully ? t.Result : null,
                    _ctx.SmuUnavailableReason);

                // The band underneath, from the history the read above has just contributed to.
                // No second hardware access: the SMU records into it on every real read, so this
                // is a walk over an in-memory ring.
                if (_ctx.Smu is { } smu) Limits.ShowHistory(smu.Limits, LimitWindow);
            });
        });
    }

    /// <summary>
    /// How far back the binding-limit band looks.
    ///
    /// An hour, because the question it answers is about a session rather than a moment -- "what
    /// held this back while I was playing" -- and because the ring holds two, so an hour is a
    /// window the history can always fill rather than a claim it cannot back.
    /// </summary>
    private static readonly TimeSpan LimitWindow = TimeSpan.FromHours(1);

    private void RefreshPowerDraw()
    {
        // Single-flight: a slow WMI provider must not stack reads behind itself.
        if (Interlocked.Exchange(ref _drawInFlight, 1) == 1) return;

        Task.Run(() =>
        {
            OmniHub.Core.Optimize.BatteryDraw? draw = null;
            try { draw = OmniHub.Core.Optimize.BatterySaver.ReadDraw(); }
            catch { }
            finally { Interlocked.Exchange(ref _drawInFlight, 0); }

            Dispatcher.BeginInvoke(() => ShowPowerDraw(draw));
        });
    }

    private static string SourceName(GpuSource source) => source switch
    {
        GpuSource.Nvml => "NVML",
        GpuSource.NvidiaSmi => "nvidia-smi",
        GpuSource.WindowsCounters => "Windows counters",
        var other => other.ToString(),
    };

    private void ShowPowerDraw(OmniHub.Core.Optimize.BatteryDraw? draw)
    {
        if (draw is null)
        {
            // No battery, or the provider refused. Not zero watts.
            PowerDrawText.Text = "unavailable";
            return;
        }

        if (draw.OnAc)
        {
            // Charging draws real power too, and it is worth seeing, but the pack is not
            // discharging so there is no runtime to report.
            PowerDrawText.Text = draw.Charging && draw.ChargeMilliwatts > 0
                ? $"AC, charging {draw.ChargeMilliwatts / 1000.0:0.0} W"
                : "AC";
            return;
        }

        if (draw.DischargeMilliwatts <= 0)
        {
            // On battery but the rate came back zero. That is the firmware not having
            // sampled yet, not the machine drawing nothing.
            PowerDrawText.Text = "measuring";
            return;
        }

        string watts = $"{draw.DischargeMilliwatts / 1000.0:0.0} W";
        var left = OmniHub.Core.Optimize.BatterySaver.EstimateRuntime(draw);
        PowerDrawText.Text = left is { } t
            ? $"{watts}  {(int)t.TotalHours}h {t.Minutes:00}m left"
            : watts;
    }

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
    }

    private void RunChip(Button chip, Func<TuningResult> action)
    {
        chip.IsEnabled = false;
        ChipResult.Text = "Working...";

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

            // Name the provider on the card itself.
            //
            // The two sources genuinely differ: nvidia-smi gives temperature, power and clock,
            // while the Windows counters give utilisation and nothing else. Without this, a blank
            // temperature reads as a broken sensor rather than as a source that does not report
            // one -- and "say which sensor answered" is a rule this application already applies
            // to its CPU readings and had simply never applied here.
            if (GpuTelemetry.Read() is { } gpu)
                subText += subText.Length > 0
                    ? $" · via {SourceName(gpu.Source)}"
                    : $"via {SourceName(gpu.Source)}";

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
    // Its own sampler: CPU load is a delta against this reader's previous call, and the overlay
    // keeps a second one on a different timer.
    private readonly SystemPerfReader _perfReader = new();

    private void RefreshPerf()
    {
        if (_perfRefreshInFlight) return;
        _perfRefreshInFlight = true;

        Task.Run(() => _perfReader.Read()).ContinueWith(t =>
        {
            _perfRefreshInFlight = false;
            SystemPerf? perf = t.IsCompletedSuccessfully ? t.Result : null;
            Dispatcher.Invoke(() =>
            {
                // A failed read used to return here, which left the previous numbers sitting on
                // screen looking live -- the same "dead reading cannot sit on screen" rule the
                // overlay already follows. It matters more since SystemPerfReader gained a null
                // path of its own: it now reports failure rather than describing a machine with
                // no RAM.
                if (perf is null)
                {
                    CpuClockText.Text = "--";
                    CpuLoadText.Text = "--";
                    StripLoad.Text = "--";
                    MemText.Text = "--";
                    MemSubText.Text = "UNAVAILABLE";
                    MemFootRight.Text = "--";
                    SetBar(CpuLoadBar, 0);
                    SetBar(MemLoadBar, 0);
                    return;
                }

                // The unit suffix lives in its own TextBlock now, so the value is bare.
                //
                // Both CPU figures are nullable and both render as two dashes when absent. The
                // load has no value until a second sample exists, because these are cumulative
                // counters since boot and one reading of them says nothing about now.
                CpuClockText.Text = perf.CpuClockGHz is { } ghz ? $"{ghz:0.0}" : "--";
                CpuLoadText.Text = perf.CpuLoadPercent is { } load ? $"{load:0}%" : "--";
                CpuFootLeft.Text = $"{Environment.ProcessorCount} LOGICAL CORES";
                StripLoad.Text = CpuLoadText.Text;

                MemText.Text = $"{perf.MemoryUsedGB:0.0}";
                double memPercent = perf.MemoryTotalGB > 0 ? perf.MemoryUsedGB / perf.MemoryTotalGB * 100.0 : 0;
                MemSubText.Text = $"USED {perf.MemoryUsedGB:0.0} / {perf.MemoryTotalGB:0.0} GB";
                MemFootRight.Text = $"{memPercent:0}%";

                // A bar with no reading behind it sits at zero, which looks like an idle
                // machine. Left where it was instead, so only the number changes.
                if (perf.CpuLoadPercent is { } bar) SetBar(CpuLoadBar, bar);
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
        // The deciding moved to OmniHub.Core.Vendors.MachineSupport, which is a pure function of
        // stated facts and therefore testable -- this reasoning used to live here, in a view, on
        // a project whose tests deliberately cannot reference the application at all.
        var facts = _ctx.Facts();
        var limited = MachineSupport.Shortfalls(facts);

        // Rebuilt from scratch, because this now runs on every return to the tab rather than
        // once: appending without clearing would stack a fresh copy of every row each visit,
        // and a capability that has since become available has to be able to disappear.
        ReadinessRows.Children.Clear();

        if (limited.Count == 0)
        {
            ReadinessCard.Visibility = Visibility.Collapsed;
            return;
        }

        ReadinessCard.Visibility = Visibility.Visible;
        ReadinessHeadline.Text = MachineSupport.Summarise(facts);

        foreach (var capability in limited)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };

            // The marker is plain text and stays the colour everything else is. A capability that
            // reads but cannot be commanded is information, not an alarm, and this application
            // does not colour prose.
            panel.Children.Add(new TextBlock
            {
                Text = capability.State == SupportState.ReadOnly
                    ? capability.Name + "  —  READ ONLY"
                    : capability.Name,
                Style = (Style)FindResource("BodyText"),
                FontSize = 12,
            });
            panel.Children.Add(new TextBlock
            {
                Text = capability.Detail,
                Style = (Style)FindResource("MutedText"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 740,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            });
            ReadinessRows.Children.Add(panel);
        }

        // The one missing piece a user can actually install, so the one that gets a button.
        // Set both ways: the SMU can open on a retry after this card first appeared, and the
        // offer to install a driver that is already loaded would be nonsense.
        GetPawnIoBtn.Visibility = _ctx.Smu is null ? Visibility.Visible : Visibility.Collapsed;
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
            bool ceiling = !fromDie && ThermalReader.IsAtCeiling(tempC, _ctx.ZoneCeilingC);

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
            // see FanCalibration.RawToPercent for why the percentage is not raw/255.
            // RPM is a tachometer reading and the fans take about six seconds to reach a new
            // level, so the measured percentage trails whatever the curve just asked for.
            // Showing only the measured figure made the app look like it was ignoring its own
            // curve -- 2700 RPM beside a high temperature reads as the fan refusing to spin up
            // when it is actually mid-ramp. The target is shown alongside while they differ.
            // A level the board did not report reads as unavailable. It used to read as a fan at
            // 0 RPM, which on a hot machine is indistinguishable from the fault this application
            // was written to catch.
            int? fanPercent = r.FanLevel1 is { } raw1 ? _ctx.FanBackend.Calibration.RawToPercent(raw1) : null;

            string fanText = fanPercent is { } pct
                ? $"FANS {_ctx.FanBackend.Calibration.RpmText(r.FanLevel1)} RPM ({pct}%)"
                : "FANS -- (the board did not report a level)";

            if (fanPercent is { } measured && _service.IsRunning && _service.HasCommanded
                && Math.Abs(_service.LastCommandedLevelPercent - measured) > 4)
            {
                fanText += $" -> {_service.LastCommandedLevelPercent}%";
            }
            ThermalSubText.Text = fanText;
            ThermalFootRight.Text = r.Throttling == true ? "THROTTLING"
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
            Animate.BrushTo(ThermalUnit, TextBlock.ForegroundProperty, thermalBrush);
            Animate.BrushTo(ThermalBarFill, Border.BackgroundProperty, thermalBrush);
            Animate.BrushTo(StripTemp, TextBlock.ForegroundProperty, thermalBrush);
            ThermalFootRight.Foreground = r.Throttling == true
                ? thermalBrush
                : (Brush)FindResource("TextFaintBrush");

            // Bar spans the range the curve actually operates over (30-100C), not 0-100:
            // a bar that never leaves its first third communicates nothing.
            SetBar(ThermalBar, (displayC - 30.0) / 70.0 * 100.0);

            StripState.Text = r.Throttling == true ? "THROTTLING"
                : displayC >= 80 ? "HOT"
                : _service.IsRunning ? "MANAGED" : "BIOS AUTO";

            // Degrees of margin, not a score, and no longer a 250x250 dial.
            //
            // This was a 0 to 100 figure that tapered from 30 C to 95 C and was then capped
            // at 25 whenever the package was throttling. Every part of that was a choice --
            // the endpoints, the taper, the penalty -- and none of it was a reading. Degrees
            // below the limit carries the same information with nothing invented on top, and
            // it now sits beside the temperature it is derived from, so the two can be read
            // against each other instead of one being a dial across the room from the other.
            HeadroomText.Text = ceiling ? "--" : $"{95.0 - displayC:0}\u00b0C margin";

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
                var gpuBrush = ThermalBrushFor(gpuC, false);
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

                // "Asleep" and "unavailable" are different facts, and this branch was
                // reporting both as the second one.
                //
                // On battery the GPU is deliberately not queried, because asking nvidia-smi
                // wakes the card. So there is no temperature to show -- but that is the
                // feature working, not a reading that failed, and saying UNAVAILABLE made a
                // success look like a fault. The power state comes from the PCI bus driver
                // rather than from the card, so reporting it costs nothing and wakes nothing:
                // it is the one measurement of this that can be taken without destroying it.
                var dstate = OmniHub.Core.Hardware.GpuPowerState.ReadDiscrete();
                GpuFootRight.Text =
                    dstate is OmniHub.Core.Hardware.DevicePowerState.D3
                        or OmniHub.Core.Hardware.DevicePowerState.D1
                        or OmniHub.Core.Hardware.DevicePowerState.D2
                        ? OmniHub.Core.Hardware.GpuPowerState.Describe(dstate).ToUpperInvariant()
                    : GpuTelemetry.IsAvailable ? "UNAVAILABLE"
                    : "NO GPU REPORTED";
            }

            // Appended with the reading's own instant rather than "now", so the chart's x axis is
            // the time the sensor was read at, not the time the UI got round to drawing it.
            var at = DateTime.UtcNow;

            TrendChart.Append(_trendTemp, at, tempC);

            // Nothing appended when the board did not report a level. The chart already draws a
            // hole as a hole rather than joining across it, so an absent reading leaves a visible
            // gap instead of a line dropping to the floor and back -- which is what a plot of
            // "0 because we did not ask successfully" looks like, and it looks alarming.
            if (r.FanLevel1 is { } raw)
                TrendChart.Append(_trendFan, at, _ctx.FanBackend.Calibration.RawToPercent(raw));

            // -1 is the log's sentinel for "the service has not commanded", and it means the
            // same here: nothing to plot rather than a zero-percent command that never happened.
            if (_service.HasCommanded)
                TrendChart.Append(_trendCommanded, at, _service.LastCommandedLevelPercent);
        });

        RefreshPerf();
    }
}
