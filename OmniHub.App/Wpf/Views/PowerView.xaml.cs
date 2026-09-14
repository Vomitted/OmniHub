using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using OmniHub.Core.Hardware;

namespace OmniHub.App.Wpf.Views;

public partial class PowerView : UserControl
{
    private readonly HardwareContext _ctx;

    public PowerView(HardwareContext ctx)
    {
        InitializeComponent();
        _ctx = ctx;

        // Titles and accents live in XAML now; the StatTiles were replaced by cards that size
        // to their content, matching the Dashboard and Fans layout.
        RefreshBattery();
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
