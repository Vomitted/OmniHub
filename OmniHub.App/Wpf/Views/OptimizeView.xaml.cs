using System.Windows;
using System.Windows.Controls;
using OmniHub.Core.Hardware;
using OmniHub.Core.Optimize;
using UserControl = System.Windows.Controls.UserControl;
using RadioButton = System.Windows.Controls.RadioButton;
namespace OmniHub.App.Wpf.Views;

public partial class OptimizeView : UserControl
{
    private readonly AppSettings _settings;
    private readonly HardwareContext _ctx;
    private bool _suppressEvents;

    public OptimizeView(AppSettings settings, HardwareContext ctx)
    {
        InitializeComponent();
        _settings = settings;
        _ctx = ctx;

        _suppressEvents = true;
        TimerToggle.IsChecked = _settings.HighResolutionTimer;
        MmcssToggle.IsChecked = _settings.DwmMmcss;
        _suppressEvents = false;

        SetChip(MmcssChip, MmcssChipText, _settings.DwmMmcss);

        RefreshTimer();
        RefreshMemory();
        RefreshBattery();
        RefreshForeground();
        BuildGamingToggles();
        BuildPowerPlans();
        InitialisePlanBuilder();

        // Scanning the caches walks several directory trees, which is real disk I/O. Kept off
        // the UI thread for the same reason every BIOS call in this app is: doing that work in
        // a constructor is exactly what made earlier tabs stall the window when first opened.
        RescanCaches();
        RescanDisk();
    }

    // ---------- scheduling ----------

    /// <summary>
    /// Paints an active-state chip. Takes the state as an argument rather than reading a
    /// toggle's IsChecked, so the indicator always reflects what the system actually reports
    /// back and can never drift into agreeing with a control the OS refused.
    /// </summary>
    private void SetChip(Border chip, TextBlock text, bool active, string activeLabel = "ACTIVE")
    {
        text.Text = active ? activeLabel : "OFF";
        text.Foreground = (Brush)FindResource(active ? "AccentBrush" : "TextFaintBrush");
        chip.BorderBrush = (Brush)FindResource(active ? "AccentBrush" : "BorderBrush");
        chip.Background = (Brush)FindResource(active ? "AccentSoftBrush" : "PanelAltBrush");
    }

    private void RefreshTimer()
    {
        double current = SystemTuning.CurrentTimerResolutionMs();
        double best = SystemTuning.BestTimerResolutionMs();

        TimerValue.Text = double.IsNaN(current) ? "--" : $"{current:0.###} ms";
        TimerSub.Text = double.IsNaN(best) ? "" : $"finest available {best:0.###} ms";

        // Derived from the granted resolution, not from the toggle: if the request was
        // refused, or another process already holds a finer timer, the chip tells the truth
        // about the machine rather than about the switch.
        bool sharp = !double.IsNaN(current) && !double.IsNaN(best) && current <= best + 0.001;
        SetChip(TimerChip, TimerChipText, sharp, "SHARP");
    }

    private void TimerToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool on = TimerToggle.IsChecked == true;

        var result = on ? SystemTuning.ApplyHighResolutionTimer() : SystemTuning.ReleaseHighResolutionTimer();
        _settings.HighResolutionTimer = on && result.Applied;
        _settings.Save();

        RefreshTimer();

        // Revert the toggle if the OS refused, rather than leaving it showing a state the
        // system is not actually in.
        if (!result.Applied)
        {
            _suppressEvents = true;
            TimerToggle.IsChecked = false;
            _suppressEvents = false;
            TimerSub.Text = result.Detail;
        }
    }

    private void MmcssToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool on = MmcssToggle.IsChecked == true;

        var result = SystemTuning.SetMmcss(on);
        _settings.DwmMmcss = on && result.Applied;
        _settings.Save();
        SetChip(MmcssChip, MmcssChipText, _settings.DwmMmcss);

        if (!result.Applied)
        {
            _suppressEvents = true;
            MmcssToggle.IsChecked = false;
            _suppressEvents = false;
            System.Windows.MessageBox.Show(result.Detail, "DWM priority",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- battery ----------

    private void RefreshBattery()
    {
        // Two namespaces' worth of WMI queries; kept off the UI thread like every other
        // hardware read in this app.
        Task.Run(() => (Draw: BatterySaver.ReadDraw(), Brightness: BatterySaver.GetBrightness()))
            .ContinueWith(t =>
            {
                var (draw, brightness) = t.Result;
                Dispatcher.Invoke(() =>
                {
                    DrawValue.Text = draw is null ? "--"
                        : draw.Charging ? $"+{draw.ChargeMilliwatts / 1000.0:0.0} W"
                        : draw.OnAc ? "AC"
                        : $"{draw.DischargeMilliwatts / 1000.0:0.0} W";

                    string sub = BatterySaver.DescribeDraw(draw);
                    if (draw is not null)
                    {
                        var runtime = BatterySaver.EstimateRuntime(draw);
                        if (runtime is not null)
                            sub += $" - about {runtime.Value.Hours}h {runtime.Value.Minutes:00}m left";
                    }
                    DrawSub.Text = sub;

                    if (brightness is not null)
                    {
                        _suppressEvents = true;
                        BrightnessSlider.Value = Math.Clamp(brightness.Value, 10, 100);
                        _suppressEvents = false;
                        BrightnessLabel.Text = $"{brightness.Value}%";
                    }
                    else
                    {
                        BrightnessSlider.IsEnabled = false;
                        BrightnessLabel.Text = "n/a";
                    }
                });
            }, TaskScheduler.Default);
    }

    // Remembered so the dim is reversible. Dimming the screen is the one action here the user
    // physically sees, and an unreversible one reads as damage rather than a setting.
    private int? _brightnessBeforeSaver;

    private void BatterySaverBtn_Click(object sender, RoutedEventArgs e)
    {
        BatterySaverBtn.IsEnabled = false;
        BatteryResult.Text = "Applying...";
        BatteryResult.Foreground = (Brush)FindResource("TextMutedBrush");

        Task.Run(() =>
        {
            var (bright, previous) = BatterySaver.ApplyBatterySaver();
            // Eco covers the GPU level, the Windows power plan and releasing the timer.
            var eco = PerformanceProfile.Apply(PerformanceMode.Eco, _ctx.Gpu, _ctx.Power);
            return (bright, previous, eco);
        }).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                var (bright, previous, eco) = t.Result;
                _brightnessBeforeSaver = previous;
                UndoBrightnessBtn.IsEnabled = previous is not null;

                BatteryResult.Text = $"{bright.Detail} {eco.Detail}";
                BatterySaverBtn.IsEnabled = true;
                RefreshBattery();
            });
        }, TaskScheduler.Default);
    }

    private void UndoBrightnessBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_brightnessBeforeSaver is not int previous) return;
        UndoBrightnessBtn.IsEnabled = false;

        Task.Run(() => BatterySaver.SetBrightness(previous)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                BatteryResult.Text = t.Result.Detail;
                // Only the brightness is undone; the Eco profile stays until changed on
                // purpose, and the label does not claim otherwise.
                _brightnessBeforeSaver = null;
                RefreshBattery();
            });
        }, TaskScheduler.Default);
    }

    // Debounce for the brightness slider. ValueChanged fires for every intermediate position,
    // and each apply is a WMI method invoke -- issuing one per pixel of drag would flood the
    // provider. Restarting a short timer on each change means only the value the user
    // actually settled on gets written, while the label still tracks live.
    private System.Windows.Threading.DispatcherTimer? _brightnessDebounce;

    private void Brightness_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // BrightnessLabel is declared after the Slider in XAML, so it is still null when the
        // slider's own Value="80" raises this during InitializeComponent.
        if (_suppressEvents || BrightnessLabel is null) return;

        int target = (int)BrightnessSlider.Value;
        BrightnessLabel.Text = $"{target}%";

        _brightnessDebounce ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _brightnessDebounce.Stop();

        // Re-subscribed each time so the closure captures the current target rather than
        // whatever it was when the timer was first created.
        _brightnessDebounce.Tick -= ApplyPendingBrightness;
        _brightnessDebounce.Tick += ApplyPendingBrightness;
        _pendingBrightness = target;
        _brightnessDebounce.Start();
    }

    private int _pendingBrightness = -1;

    private void ApplyPendingBrightness(object? sender, EventArgs e)
    {
        _brightnessDebounce?.Stop();
        int target = _pendingBrightness;
        if (target < 0) return;

        Task.Run(() => BatterySaver.SetBrightness(target)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                BatteryResult.Text = t.Result.Detail;
            });
        }, TaskScheduler.Default);
    }

    // ---------- memory ----------

    private void RefreshMemory()
    {
        ulong available = MemoryTools.AvailablePhysicalBytes();
        ulong total = MemoryTools.TotalPhysicalBytes();

        if (available == 0 || total == 0)
        {
            MemValue.Text = "--";
            MemSub.Text = "unavailable";
            return;
        }

        MemValue.Text = $"{available / 1024.0 / 1024 / 1024:0.0} GB";
        MemSub.Text = $"of {total / 1024.0 / 1024 / 1024:0.0} GB total";
    }

    private void PurgeBtn_Click(object sender, RoutedEventArgs e)
    {
        PurgeBtn.IsEnabled = false;
        MemResult.Text = "Working...";

        Task.Run(() => MemoryTools.PurgeStandbyList()).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                MemResult.Text = t.Result.Detail;
                PurgeBtn.IsEnabled = true;
                RefreshMemory();
            });
        }, TaskScheduler.Default);
    }

    // ---------- windows gaming ----------

    private void BuildGamingToggles()
    {
        Task.Run(() => WindowsGaming.ReadAll()).ContinueWith(t =>
        {
            var toggles = t.IsFaulted ? Array.Empty<GamingToggle>() : t.Result.ToArray();
            Dispatcher.Invoke(() =>
            {
                GamingList.Items.Clear();
                foreach (var toggle in toggles) GamingList.Items.Add(BuildGamingRow(toggle));
            });
        }, TaskScheduler.Default);
    }

    private UIElement BuildGamingRow(GamingToggle toggle)
    {
        var check = new System.Windows.Controls.CheckBox
        {
            Style = (Style)FindResource("ToggleSwitchStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = toggle.Id,
            // Three-state so "Windows default" is visually distinct from an explicit off.
            // Showing an absent value as off would be the UI asserting something it does
            // not know.
            IsThreeState = toggle.Enabled is null,
            IsChecked = toggle.Enabled,
        };
        check.Checked += Gaming_Changed;
        check.Unchecked += Gaming_Changed;

        var text = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        text.Children.Add(new TextBlock
        {
            Text = toggle.Name + (toggle.RequiresReboot ? "  (restart required)" : ""),
            Foreground = (Brush)FindResource("TextPrimaryBrush"),
            FontSize = 13,
        });
        text.Children.Add(new TextBlock
        {
            Text = toggle.Enabled is null ? toggle.Description + "  Currently: Windows default." : toggle.Description,
            Foreground = (Brush)FindResource("TextFaintBrush"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 480,
            Margin = new Thickness(0, 2, 0, 0),
        });

        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        Grid.SetColumn(check, 1);
        row.Children.Add(text);
        row.Children.Add(check);
        return row;
    }

    private void Gaming_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is not System.Windows.Controls.CheckBox cb || cb.Tag is not string id) return;

        bool enable = cb.IsChecked == true;
        cb.IsEnabled = false;

        Task.Run(() => WindowsGaming.Set(id, enable)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                var r = t.IsFaulted
                    ? new TuningResult(false, t.Exception?.GetBaseException().Message ?? "Failed.")
                    : t.Result;

                GamingResult.Text = r.Detail;
                cb.IsEnabled = true;

                // Put the switch back if the machine refused, so it never shows a state the
                // registry does not actually hold.
                if (!r.Applied)
                {
                    _suppressEvents = true;
                    cb.IsChecked = !enable;
                    _suppressEvents = false;
                }
                else
                {
                    // A successful write means the value now exists, so it is no longer
                    // "Windows default" and the third state should go away.
                    cb.IsThreeState = false;
                }
            });
        }, TaskScheduler.Default);
    }

    // ---------- processes ----------

    private void RefreshForeground()
    {
        Task.Run(() =>
        {
            using var p = ProcessTuning.ForegroundProcess();
            // Read everything needed while the handle is alive; the Process is disposed
            // before the result crosses back to the UI thread.
            return p is null ? null : new { p.ProcessName, Priority = SafePriority(p), Mb = p.WorkingSet64 / 1024.0 / 1024.0 };
        }).ContinueWith(t =>
        {
            var info = t.IsFaulted ? null : t.Result;
            Dispatcher.Invoke(() =>
            {
                if (info is null)
                {
                    ForegroundValue.Text = "--";
                    ForegroundSub.Text = "no foreground window";
                    return;
                }
                ForegroundValue.Text = info.ProcessName;
                ForegroundSub.Text = $"{info.Priority} priority - {info.Mb:0} MB working set";
            });
        }, TaskScheduler.Default);
    }

    private static string SafePriority(System.Diagnostics.Process p)
    {
        // PriorityClass throws for protected processes rather than returning a value.
        try { return p.PriorityClass.ToString(); } catch { return "unknown"; }
    }

    private void PrioritiseBtn_Click(object sender, RoutedEventArgs e)
    {
        // NOTE: clicking a button in this window makes OmniHub itself the foreground app, so
        // the target is captured on a short delay -- long enough for the user to alt-tab to
        // the app they actually mean. Without this the feature would only ever raise
        // OmniHub's own priority, which is useless.
        ProcessResult.Text = "Switch to the app you want prioritised... (3s)";
        ProcessResult.Foreground = (Brush)FindResource("TextMutedBrush");
        PrioritiseBtn.IsEnabled = false;

        Task.Run(() => { Thread.Sleep(3000); return ProcessTuning.PrioritiseForeground(); })
            .ContinueWith(t =>
            {
                Dispatcher.Invoke(() =>
                {
                    var r = t.IsFaulted ? new TuningResult(false, t.Exception?.GetBaseException().Message ?? "Failed.") : t.Result;
                    ProcessResult.Text = r.Detail;
                    PrioritiseBtn.IsEnabled = true;
                    RefreshForeground();
                });
            }, TaskScheduler.Default);
    }

    private void TrimBtn_Click(object sender, RoutedEventArgs e)
    {
        TrimBtn.IsEnabled = false;
        ProcessResult.Text = "Working...";
        ProcessResult.Foreground = (Brush)FindResource("TextMutedBrush");

        Task.Run(() => ProcessTuning.TrimBackgroundWorkingSets()).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                var r = t.Result;
                ProcessResult.Text = r.ProcessesTrimmed == 0
                    ? "Nothing had a working set worth trimming."
                    : $"Trimmed {r.ProcessesTrimmed} process(es), releasing {ShaderCache.FormatBytes(r.BytesReleased)} to the standby list.";
                TrimBtn.IsEnabled = true;
                RefreshMemory();
            });
        }, TaskScheduler.Default);
    }

    // ---------- shader cache ----------

    private void RescanCaches()
    {
        CacheValue.Text = "scanning...";
        CacheList.Items.Clear();

        Task.Run(() => ShaderCache.Scan()).ContinueWith(t =>
        {
            var locations = t.Result;
            Dispatcher.Invoke(() =>
            {
                CacheList.Items.Clear();
                long total = locations.Sum(l => l.Bytes);
                CacheValue.Text = locations.Count == 0 ? "0 KB" : ShaderCache.FormatBytes(total);

                foreach (var location in locations)
                {
                    CacheList.Items.Add(new TextBlock
                    {
                        Text = $"{location.Label} - {ShaderCache.FormatBytes(location.Bytes)} ({location.Files} files)",
                        FontSize = 11,
                        Margin = new Thickness(0, 2, 0, 0),
                        Foreground = (Brush)FindResource("TextFaintBrush"),
                    });
                }
            });
        }, TaskScheduler.Default);
    }

    private void RescanBtn_Click(object sender, RoutedEventArgs e)
    {
        CacheResult.Text = "";
        RescanCaches();
        RescanDisk();
    }

    private void ClearCacheBtn_Click(object sender, RoutedEventArgs e)
    {
        // Deleting files is not undoable, so it is confirmed rather than fired on one click.
        var confirm = System.Windows.MessageBox.Show(
            "Delete the contents of the GPU shader caches?\n\n" +
            "This is safe: drivers rebuild them automatically. The next launch of a game or " +
            "3D application will pause once while shaders recompile.",
            "Clear shader caches", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        ClearCacheBtn.IsEnabled = false;
        CacheResult.Text = "Working...";

        Task.Run(() => ShaderCache.Clear()).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                CacheResult.Text = t.Result.Detail;
                ClearCacheBtn.IsEnabled = true;
                RescanCaches();
            });
        }, TaskScheduler.Default);
    }

    // The PERFORMANCE MODE buttons that were here are gone. They set the GPU power level and
    // the Windows power plan -- the first duplicating the Dashboard's three presets, the
    // second duplicating the WINDOWS POWER PLAN section further down THIS page. Three controls
    // for two settings. The Dashboard drives performance presets; the power plan is set below.
    // PerformanceProfile itself stays, because the battery saver still applies Eco wholesale.

    // ---------- disk cleanup ----------

    private IReadOnlyList<CleanupTarget> _diskTargets = Array.Empty<CleanupTarget>();

    private void RescanDisk()
    {
        DiskValue.Text = "scanning...";
        DiskList.Items.Clear();

        Task.Run(() => DiskCleanup.Scan()).ContinueWith(t =>
        {
            var targets = t.Result;
            Dispatcher.Invoke(() =>
            {
                _diskTargets = targets;
                DiskList.Items.Clear();
                long total = targets.Sum(x => x.Bytes);
                DiskValue.Text = targets.Count == 0 ? "0 KB" : ShaderCache.FormatBytes(total);

                foreach (var target in targets)
                {
                    DiskList.Items.Add(new TextBlock
                    {
                        Text = $"{target.Label} - {ShaderCache.FormatBytes(target.Bytes)} ({target.Files} files)",
                        FontSize = 11,
                        Margin = new Thickness(0, 2, 0, 0),
                        Foreground = (Brush)FindResource("TextFaintBrush"),
                    });
                }
            });
        }, TaskScheduler.Default);
    }

    private void RescanDiskBtn_Click(object sender, RoutedEventArgs e)
    {
        DiskResult.Text = "";
        RescanDisk();
    }

    private void CleanDiskBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_diskTargets.Count == 0)
        {
            DiskResult.Text = "Nothing to clean.";
            return;
        }

        long total = _diskTargets.Sum(x => x.Bytes);
        var confirm = System.Windows.MessageBox.Show(
            $"Delete {ShaderCache.FormatBytes(total)} from {_diskTargets.Count} location(s)?\n\n" +
            string.Join("\n", _diskTargets.Select(x => $"  {x.Label}  ({ShaderCache.FormatBytes(x.Bytes)})")) +
            "\n\nOnly files older than 24 hours are removed. This cannot be undone.",
            "Clean up disk", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var targets = _diskTargets;
        CleanDiskBtn.IsEnabled = false;
        DiskResult.Text = "Working...";

        Task.Run(() => DiskCleanup.Clean(targets)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                DiskResult.Text = t.Result.Detail;
                CleanDiskBtn.IsEnabled = true;
                RescanDisk();
            });
        }, TaskScheduler.Default);
    }

    // ---------- power plan ----------

    private void BuildPowerPlans()
    {
        Task.Run(() => (Schemes: PowerPlan.List(), Active: PowerPlan.GetActiveSchemeId())).ContinueWith(t =>
        {
            var (schemes, active) = t.Result;
            Dispatcher.Invoke(() =>
            {
                PlanList.Items.Clear();
                if (schemes.Count == 0)
                {
                    PlanResult.Text = "No power plans could be read from Windows.";
                    return;
                }

                // Suppressed while building: assigning IsChecked raises Checked, which would
                // otherwise re-apply the plan that is already active on every tab open.
                _suppressEvents = true;
                foreach (var scheme in schemes)
                {
                    var radio = new RadioButton
                    {
                        GroupName = "PowerPlan",
                        Content = scheme.Name,
                        Tag = scheme.Id,
                        Foreground = (Brush)FindResource("TextPrimaryBrush"),
                        Margin = new Thickness(0, 3, 0, 3),
                        IsChecked = active is not null && active.Value == scheme.Id,
                        Cursor = System.Windows.Input.Cursors.Hand,
                    };
                    radio.Checked += Plan_Checked;
                    PlanList.Items.Add(radio);
                }
                _suppressEvents = false;
            });
        }, TaskScheduler.Default);
    }

    private void Plan_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is not RadioButton rb || rb.Tag is not Guid id) return;

        Task.Run(() => PowerPlan.Activate(id)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                PlanResult.Text = t.Result.Detail;
            });
        }, TaskScheduler.Default);
    }

    // ----------------------------------------------------------- plan builder

    /// <summary>
    /// The settings this machine actually exposes, read once.
    ///
    /// Read from Windows rather than listed here, so a machine with a different set of boost
    /// modes -- or no PCI Express control at all -- gets a builder that matches it instead of
    /// one that writes values the machine will ignore. This project has already been caught out
    /// by an assumed index once.
    /// </summary>
    private IReadOnlyList<PowerKnob> _knobs = Array.Empty<PowerKnob>();

    private void InitialisePlanBuilder()
    {
        _knobs = PowerKnobReader.Read();

        if (_knobs.Count == 0)
        {
            BuilderResult.Text = "Windows did not report any adjustable power settings, so a plan cannot be built here.";
            CreatePlanBtn.IsEnabled = false;
            DeletePlanBtn.IsEnabled = false;
            return;
        }

        RefreshPreview();
    }

    private PowerPlanRecipe CurrentRecipe() =>
        new(PlanName.Text.Trim(), (int)Math.Round(PlanBias.Value));

    private void PlanBias_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => RefreshPreview();
    private void PlanName_Changed(object sender, TextChangedEventArgs e) => RefreshPreview();

    /// <summary>
    /// Shows exactly what pressing Create would write, both rails, before it writes anything.
    ///
    /// A power plan is not self-evident from a slider position, and this feature exists to be
    /// trusted with processor settings. Showing the resolved values first is the difference
    /// between a control and a wish.
    /// </summary>
    private void RefreshPreview()
    {
        // ValueChanged fires while the XAML is still being parsed, before the later-declared
        // elements exist.
        if (PlanPreview is null || PlanBiasLabel is null || _knobs.Count == 0) return;

        int bias = (int)Math.Round(PlanBias.Value);
        PlanBiasLabel.Text = bias switch
        {
            <= 10 => "Maximum battery life",
            < 40 => $"Leaning towards battery ({bias})",
            <= 60 => $"Balanced ({bias})",
            < 90 => $"Leaning towards performance ({bias})",
            _ => "Maximum performance",
        };

        PlanPreview.Items.Clear();
        PlanPreview.Items.Add(Row("", "PLUGGED IN", "ON BATTERY", "DataLabelText", "DataLabelText"));

        foreach (var (knob, ac, dc) in CurrentRecipe().Resolve(_knobs))
            PlanPreview.Items.Add(Row(knob.Label, knob.Describe(ac), knob.Describe(dc), "MutedText", "BodyText"));

        Grid Row(string label, string ac, string dc, string labelStyle, string valueStyle)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            row.Children.Add(Cell(label, 0, labelStyle));
            row.Children.Add(Cell(ac, 1, valueStyle));
            row.Children.Add(Cell(dc, 2, "MutedText"));
            return row;
        }

        TextBlock Cell(string text, int column, string style)
        {
            var t = new TextBlock
            {
                Text = text,
                Style = (Style)FindResource(style),
                FontSize = 11.5,
                TextWrapping = TextWrapping.NoWrap,
            };
            Grid.SetColumn(t, column);
            return t;
        }
    }

    private void CreatePlanBtn_Click(object sender, RoutedEventArgs e)
    {
        var recipe = CurrentRecipe();
        CreatePlanBtn.IsEnabled = false;

        Task.Run(() => PowerPlanSetup.CreateFromRecipe(recipe, _knobs)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                CreatePlanBtn.IsEnabled = true;
                BuilderResult.Text = t.Result.Detail;

                // The list above now has a new entry; leaving it stale would make the plan look
                // as though it had not been created.
                if (t.Result.Id is not null) BuildPowerPlans();
            });
        }, TaskScheduler.Default);
    }

    private void DeletePlanBtn_Click(object sender, RoutedEventArgs e)
    {
        string name = PlanName.Text.Trim();
        Task.Run(() => PowerPlanSetup.RemoveByName(name)).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                BuilderResult.Text = t.Result;
                BuilderResult.Foreground = (Brush)FindResource("TextMutedBrush");
                BuildPowerPlans();
            });
        }, TaskScheduler.Default);
    }
}
