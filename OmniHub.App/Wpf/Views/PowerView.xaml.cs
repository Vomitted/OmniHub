// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using UserControl = System.Windows.Controls.UserControl;
using OmniHub.Core.Hardware;
using OmniHub.Core.Optimize;

namespace OmniHub.App.Wpf.Views;

public partial class PowerView : UserControl, IDisposable
{
    private readonly HardwareContext _ctx;

    /// <summary>
    /// How often the power budget is re-read.
    ///
    /// The slowest input governs: the SMU snapshot is cached for five seconds because reading it
    /// takes a global PCI mutex, and asking faster would mean either stale numbers or a DPC
    /// latency cost paid for nothing. Three seconds gives a readout that moves without the timer
    /// ever being the reason a number is old.
    /// </summary>
    private static readonly TimeSpan DrawInterval = TimeSpan.FromSeconds(3);

    private readonly DispatcherTimer _drawTimer;
    private bool _drawInFlight;

    public PowerView(HardwareContext ctx)
    {
        InitializeComponent();
        _ctx = ctx;

        // Titles and accents live in XAML now; the StatTiles were replaced by cards that size
        // to their content, matching the Dashboard and Fans layout.
        RefreshBattery();

        _drawTimer = new DispatcherTimer { Interval = DrawInterval };
        _drawTimer.Tick += (_, _) => RefreshDraw();

        // Gated on visibility, the way the Dashboard and the overlay already are. Nothing here is
        // worth a WMI query and an SMU read every three seconds while the tab is not on screen --
        // least of all on the tab whose subject is what the machine is drawing.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { _drawTimer.Start(); RefreshDraw(); }
            else _drawTimer.Stop();
        };
    }

    /// <summary>Stops the refresh timer. MainWindow disposes whichever of its views can be.</summary>
    public void Dispose()
    {
        try { _drawTimer.Stop(); } catch { }
    }

    // BatteryInfoReader.Read() runs several WMI queries (Win32_Battery, root\wmi
    // BatteryStaticData/BatteryFullChargedCapacity/BatteryCycleCount) -- real I/O, called
    // directly from the constructor. Same class of startup-blocking issue as
    // DashboardView.RefreshGpuMode() and GpuView.RefreshMode(), missed in the original
    // freeze-fix pass since none of those three views' constructors were audited then.
    private void RefreshBattery()
    {
        Task.Run(() => BatteryInfoReader.Read()).ContinueWith(t =>
        {
            var b = t.Result;
            Dispatcher.Invoke(() =>
            {
                if (b is null)
                {
                    ChargeValue.Text = "--"; ChargeFoot.Text = "UNAVAILABLE";
                    HealthValue.Text = "--"; HealthUnit.Text = ""; HealthFoot.Text = "";
                    CyclesValue.Text = "--"; CyclesFoot.Text = "";
                    return;
                }

                ChargeValue.Text = b.ChargePercent.ToString();
                ChargeFoot.Text = b.Status.ToUpperInvariant();

                if (b.DesignCapacityMWh > 0 && b.FullChargeCapacityMWh > 0)
                {
                    double healthPct = b.FullChargeCapacityMWh * 100.0 / b.DesignCapacityMWh;
                    HealthValue.Text = $"{healthPct:0}";
                    HealthUnit.Text = "%";
                    HealthFoot.Text = $"{b.FullChargeCapacityMWh} / {b.DesignCapacityMWh} mWh";

                    // Battery wear is one of the few genuinely diagnostic numbers here, so it
                    // is allowed to carry colour. Thresholds follow the usual replacement
                    // guidance: under 80% of design capacity is a worn cell.
                    HealthValue.Foreground = (Brush)FindResource(
                        healthPct < 70 ? "DangerBrush" : healthPct < 80 ? "WarnBrush" : "TextPrimaryBrush");
                }
                else
                {
                    // Absent capacities mean the firmware did not report them, which is not
                    // the same as a battery in poor health.
                    HealthValue.Text = "--";
                    HealthUnit.Text = "";
                    HealthFoot.Text = "NOT REPORTED BY FIRMWARE";
                }

                CyclesValue.Text = b.CycleCount > 0 ? b.CycleCount.ToString() : "--";
                CyclesFoot.Text = b.CycleCount > 0 ? "CHARGE CYCLES" : "NOT REPORTED BY FIRMWARE";
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Reads the three power figures and shows what is left over.
    ///
    /// Single-flighted: a battery WMI query, an SMU table read and a GPU query in one tick can
    /// outlast three seconds on a busy machine, and letting the next tick start on top of that
    /// would queue reads behind each other until the whole thing fell over.
    /// </summary>
    private async void RefreshDraw()
    {
        if (_drawInFlight) return;
        _drawInFlight = true;

        try
        {
            var budget = await Task.Run(() =>
            {
                var draw = BatterySaver.ReadDraw();

                // PPT slow rather than STAPM. All three are package power with different filter
                // windows, and the battery's own discharge rate is reported on a seconds-scale
                // average -- so the slow limit's window is the one that matches. STAPM averages
                // over minutes and would report a package still "drawing" heat budget it spent
                // some time ago, against a battery figure describing right now.
                double? package = _ctx.Smu?.ReadPowerSnapshot()?.SlowWatts;

                // Null on battery by design. The application will not spawn nvidia-smi on
                // battery, because waking the card to ask what it draws changes what it draws,
                // and PowerBudget treats the absence as "inside the remainder" rather than zero.
                double? gpu = GpuTelemetry.Read()?.PowerWatts;

                return PowerBudget.Compute(draw, package, gpu);
            }).ConfigureAwait(true);

            DrawTotalValue.Text = Watts(budget.TotalWatts);
            DrawPackageValue.Text = Watts(budget.PackageWatts);

            DrawGpuValue.Text = Watts(budget.GpuWatts);
            DrawGpuUnit.Text = budget.GpuWatts is null ? "" : "W";
            DrawGpuFoot.Text = budget.GpuWatts is null ? "NOT MEASURED ON BATTERY" : "NVIDIA-SMI";

            DrawRestValue.Text = Watts(budget.RemainderWatts);
            DrawRestUnit.Text = budget.RemainderWatts is null ? "" : "W";
            DrawRestFoot.Text = budget.RemainderShare is { } share
                ? $"{share:0}% OF THE TOTAL, BY SUBTRACTION"
                : "A SUBTRACTION";

            DrawNote.Text = budget.Describe();
        }
        catch (Exception ex)
        {
            DrawNote.Text = $"The power budget could not be read ({ex.Message}).";
        }
        finally
        {
            _drawInFlight = false;
        }
    }

    /// <summary>A watt figure, or the two dashes this application uses for "no reading".</summary>
    private static string Watts(double? value) => value is { } v ? v.ToString("0.0") : "--";

    private void IdleToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool enabled = IdleToggle.IsChecked == true;
        SafeCall(() => _ctx.Power.SetIdle(enabled));
    }

    // The PL1/PL4 sliders that used to live here are gone. They were a second set of CPU
    // power controls duplicating the Tuning tab's, writing the same two BIOS commands from a
    // different screen with no shared state, so the two could disagree about what the machine
    // was set to. Tuning owns CPU power now; this tab is about the battery.

    // Every action passed here is a synchronous BIOS call -- run off the UI
    // thread so a button click doesn't freeze the window while it completes.
    private void SafeCall(Action a)
    {
        Task.Run(() =>
        {
            try { a(); }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => System.Windows.MessageBox.Show(ex.Message, "Power command failed"));
            }
        });
    }
}
