using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using OmniHub.Core.Hardware;
using OmniHub.Core.Optimize;

namespace OmniHub.App.Wpf.Views;

public partial class PowerView : UserControl
{
    private readonly HardwareContext _ctx;

    public PowerView(HardwareContext ctx)
    {
        InitializeComponent();
        _ctx = ctx;
        _self = System.Diagnostics.Process.GetCurrentProcess().Id;

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

    // ---- what is using the machine ------------------------------------------

    private readonly int _self;

    private async void DrainBtn_Click(object sender, RoutedEventArgs e)
    {
        DrainBtn.IsEnabled = false;
        DrainStatus.Text = "Sampling...";
        DrainRows.Children.Clear();

        List<ProcessDrain> rows;
        try { rows = await ProcessPowerUse.SampleAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true); }
        catch (Exception ex)
        {
            DrainStatus.Text = $"Could not sample: {ex.Message}";
            DrainBtn.IsEnabled = true;
            return;
        }

        foreach (var r in rows.Take(12))
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            foreach (var w in new[] { 2.0, 1.0, 1.0, 1.0 })
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w, GridUnitType.Star) });

            // OmniHub names itself rather than hiding. An application that measures what is
            // costing power and quietly omits its own row is not being honest about the answer,
            // and this one does poll the firmware every couple of seconds.
            string label = r.Pid == _self ? $"{r.Name}  (this app)" : r.Name;

            Cell(grid, 0, label, "BodyText");
            Cell(grid, 1, r.GpuPercent >= 0.1 ? $"{r.GpuPercent:0.#}%" : "-", "BodyText");
            Cell(grid, 2, r.CpuPercent >= 0.1 ? $"{r.CpuPercent:0.#}%" : "-", "BodyText");
            Cell(grid, 3, r.TimerPercent >= 0.1 ? $"{r.TimerPercent:0.#}%" : "-", "MutedText");

            DrainRows.Children.Add(grid);
        }

        DrainStatus.Text = rows.Count == 0
            ? "Nothing measurable was running, which is a good sign on battery."
            : $"{rows.Count} process(es) doing measurable work; the busiest are listed.";

        DrainBtn.IsEnabled = true;
    }

    private void Cell(Grid grid, int column, string text, string styleKey)
    {
        var tb = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource(styleKey),
            FontSize = 11.5,
        };
        Grid.SetColumn(tb, column);
        grid.Children.Add(tb);
    }
}
