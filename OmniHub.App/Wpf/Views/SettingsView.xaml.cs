using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OmniHub.App.Wpf;
using UserControl = System.Windows.Controls.UserControl;
using RadioButton = System.Windows.Controls.RadioButton;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace OmniHub.App.Wpf.Views;

public partial class SettingsView : UserControl
{
    private readonly AppSettings _settings;
    private bool _suppressEvents;

    public SettingsView(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        _suppressEvents = true;
        if (_settings.CloseBehavior == CloseBehavior.Exit) ExitCloseBtn.IsChecked = true;
        else TrayCloseBtn.IsChecked = true;

        // Snap the saved lead to whichever preset it matches; anything else (a hand-edited
        // settings file) falls back to Off rather than silently showing the wrong pill.
        var lead = _settings.PredictiveLeadSeconds;
        if (lead >= 20) Lead20Btn.IsChecked = true;
        else if (lead >= 10) Lead10Btn.IsChecked = true;
        else if (lead >= 5) Lead5Btn.IsChecked = true;
        else LeadOffBtn.IsChecked = true;

        LoggingToggle.IsChecked = _settings.ThermalLogging;
        _thermalLoggingAtLoad = _settings.ThermalLogging;
        InitialiseOverlayControls();
        _suppressEvents = false;

        // After the suppression flag clears: these start work whose completion touches
        // controls, and neither must run while the view is still wiring itself up.
        InitialisePowerPlanControls();
        InitialiseUpdateControls();

        RefreshLoggingChip();

        BuildThemeSwatches();

        // schtasks /Query is a subprocess call -- keep it off the UI thread so opening
        // this tab doesn't stall the window the way earlier synchronous BIOS calls did.
        Task.Run(() => StartupManager.IsEnabled()).ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                _suppressEvents = true;
                StartupToggle.IsChecked = t.Result;
                _suppressEvents = false;

                // Reflects the actual scheduled task, queried from Windows, not the setting
                // we would like to be true.
                SetChip(StartupChip, StartupChipText, t.Result, "ENABLED");
            });
        }, TaskScheduler.Default);
    }

    // Swatches are built in code rather than declared in XAML because each one previews its
    // own palette, which means loading that palette's dictionary and reading colours out of
    // it. A XAML DataTemplate can only bind to the *active* theme's brushes, so every swatch
    // would have looked identical -- the one thing a theme picker must not do.
    private void BuildThemeSwatches()
    {
        foreach (var theme in ThemeManager.All)
        {
            var dict = new ResourceDictionary { Source = new Uri(theme.Source, UriKind.Relative) };
            Color Pick(string key, Color fallback) => dict[key] is Color c ? c : fallback;

            var bg = Pick("BackgroundColor", Colors.Black);
            var panel = Pick("PanelColor", Colors.DimGray);
            var accent = Pick("AccentColor", Colors.SkyBlue);
            var text = Pick("TextPrimaryColor", Colors.White);
            var border = Pick("BorderColor", Colors.Gray);

            var preview = new Border
            {
                Width = 132,
                Height = 56,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(border),
                BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        Swatch(panel), Swatch(text), Swatch(accent),
                    },
                },
            };

            var radio = new RadioButton
            {
                GroupName = "Theme",
                Tag = theme.Id,
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(0, 0, 10, 10),
                IsChecked = string.Equals(theme.Id, _settings.ThemeName, StringComparison.OrdinalIgnoreCase),
                Content = new StackPanel
                {
                    Children =
                    {
                        preview,
                        new TextBlock
                        {
                            Text = theme.DisplayName,
                            Margin = new Thickness(2, 6, 0, 0),
                            FontSize = 12,
                            Foreground = (Brush)FindResource("TextPrimaryBrush"),
                        },
                        new TextBlock
                        {
                            Text = theme.Description,
                            Margin = new Thickness(2, 1, 0, 0),
                            FontSize = 10.5,
                            MaxWidth = 132,
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = (Brush)FindResource("TextFaintBrush"),
                        },
                    },
                },
                Template = (ControlTemplate)FindResource("ThemeSwatchTemplate"),
            };
            radio.Checked += Theme_Checked;
            ThemeList.Items.Add(radio);
        }
    }

    private static Border Swatch(Color c) => new()
    {
        Width = 20,
        Height = 20,
        Margin = new Thickness(3, 0, 3, 0),
        CornerRadius = new CornerRadius(3),
        Background = new SolidColorBrush(c),
    };

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is RadioButton rb && rb.Tag is string id)
        {
            _settings.ThemeName = id;
            _settings.Save();
            ThemeManager.Apply(id);
        }
    }

    /// <summary>
    /// Fills the corner picker and syncs both overlay controls to the saved settings.
    /// Called from the constructor inside the _suppressEvents window, so populating the
    /// combo does not immediately fire its own SelectionChanged and re-save.
    /// </summary>
    private void InitialiseOverlayControls()
    {
        OverlayToggle.IsChecked = _settings.OverlayEnabled;

        OverlayCornerPicker.Items.Clear();
        foreach (var corner in Enum.GetValues<OverlayCorner>())
            OverlayCornerPicker.Items.Add(Describe(corner));
        OverlayCornerPicker.SelectedIndex = (int)_settings.OverlayCorner;

        _suppressOverlayEvents = true;
        OverlayOpacitySlider.Value = Math.Clamp(_settings.OverlayOpacity, 0.2, 1.0);
        OverlayOpacityLabel.Text = $"{OverlayOpacitySlider.Value * 100:0}%";
        _suppressOverlayEvents = false;

        // One checkbox per available metric, ticked if the user has it. Overlay order follows
        // the saved list, so re-ticking a metric appends it rather than restoring its old
        // position -- predictable enough not to need drag ordering.
        foreach (var (key, label) in Wpf.OverlayWindow.AvailableMetrics)
        {
            var box = new System.Windows.Controls.CheckBox
            {
                Style = (Style)FindResource("OmniCheckBoxStyle"),
                Content = label,
                IsChecked = _settings.OverlayMetrics.Contains(key),
                Margin = new Thickness(0, 0, 18, 8),
                Tag = key,
            };
            box.Checked += OverlayMetric_Changed;
            box.Unchecked += OverlayMetric_Changed;
            OverlayMetricList.Children.Add(box);
        }
    }

    private bool _suppressOverlayEvents;

    private void OverlayOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Slider.ValueChanged fires during InitializeComponent, before the label field exists.
        if (_suppressOverlayEvents || OverlayOpacityLabel is null) return;

        _settings.OverlayOpacity = OverlayOpacitySlider.Value;
        OverlayOpacityLabel.Text = $"{OverlayOpacitySlider.Value * 100:0}%";
        _settings.Save();
        Owner()?.RefreshOverlayAppearance();
    }

    private void OverlayMetric_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox { Tag: string key } box) return;

        if (box.IsChecked == true)
        {
            if (!_settings.OverlayMetrics.Contains(key)) _settings.OverlayMetrics.Add(key);
        }
        else _settings.OverlayMetrics.Remove(key);

        _settings.Save();
        Owner()?.RefreshOverlayAppearance();
    }

    /// <summary>The MainWindow hosting this view, or null before it is up.</summary>
    private MainWindow? Owner() => Window.GetWindow(this) as MainWindow;

    private static string Describe(OverlayCorner corner) => corner switch
    {
        OverlayCorner.TopLeft => "Top left",
        OverlayCorner.TopRight => "Top right",
        OverlayCorner.BottomLeft => "Bottom left",
        _ => "Bottom right",
    };

    private void OverlayToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;

        _settings.OverlayEnabled = OverlayToggle.IsChecked == true;
        _settings.Save();
        (System.Windows.Application.Current.MainWindow as MainWindow)?.SetOverlayVisible(_settings.OverlayEnabled);
    }

    private void OverlayCorner_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || OverlayCornerPicker.SelectedIndex < 0) return;

        _settings.OverlayCorner = (OverlayCorner)OverlayCornerPicker.SelectedIndex;
        _settings.Save();
        (System.Windows.Application.Current.MainWindow as MainWindow)?.RefreshOverlayPosition();
    }

    private void StartupToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        bool enabled = StartupToggle.IsChecked == true;

        Task.Run(() =>
        {
            bool ok = false;
            string? failure = null;
            try { ok = StartupManager.SetEnabled(enabled); } catch (Exception ex) { failure = ex.Message; }

            // Captured HERE, before anything else runs schtasks.
            //
            // LastError is overwritten by every call, and the re-query below is itself a call
            // that fails whenever the task is absent -- which is exactly the situation after a
            // failed create. So the dialog was faithfully reporting the query's "cannot find the
            // file specified" while the create's actual reason had already been thrown away,
            // which sent the diagnosis in the wrong direction entirely.
            failure ??= StartupManager.LastError;

            // Re-query rather than assume the write took: schtasks can report success while
            // policy blocks the task, and the chip must show the machine's state.
            bool actual = false;
            try { actual = StartupManager.IsEnabled(); } catch { }
            Dispatcher.Invoke(() => SetChip(StartupChip, StartupChipText, actual, "ENABLED"));

            // Enabled, but not the way it was asked for: the XML form was rejected and the plain
            // fallback carries Windows' battery defaults back with it. Startup genuinely is on, so
            // the toggle stays on -- but the degradation is said out loud, because a task that
            // silently declines to start on battery and kills the app on unplug is exactly the
            // failure that spent this long looking like a crash.
            if (ok && failure is { Length: > 0 } warning)
            {
                Dispatcher.Invoke(() => System.Windows.MessageBox.Show(
                    warning, "Startup", MessageBoxButton.OK, MessageBoxImage.Warning));
            }
            else if (!ok)
            {
                Dispatcher.Invoke(() =>
                {
                    _suppressEvents = true;
                    StartupToggle.IsChecked = !enabled;
                    _suppressEvents = false;
                    // Says what Windows said. The bare sentence sent someone hunting for a
                    // one-line schema mistake that schtasks had already named exactly.
                    string detail = failure is { Length: > 0 } why
                        ? $"Could not update the startup task.\n\n{why}"
                        : "Could not update the startup task.";

                    System.Windows.MessageBox.Show(
                        detail, "Startup", MessageBoxButton.OK, MessageBoxImage.Warning);
                });
            }
        });
    }

    /// <summary>
    /// StartMinimizedToTray existed in the settings file with no UI and no reader -- saved,
    /// serialised, and completely inert. It is wired to both ends now.
    /// </summary>
    private void StartMinimized_Changed(object sender, RoutedEventArgs e)
    {
        _settings.StartMinimizedToTray = StartMinimizedToggle.IsChecked == true;
        _settings.Save();
    }

    private void CloseBehavior_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            _settings.CloseBehavior = tag == "Exit" ? CloseBehavior.Exit : CloseBehavior.MinimizeToTray;
            _settings.Save();
        }
    }

    // Persisted only. Applying it live would mean changing the cooling behaviour of a loop
    // that is already running against a curve with its own hysteresis state, mid-flight;
    // taking effect on restart keeps the running session predictable.
    private void Lead_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        if (sender is RadioButton rb && rb.Tag is string tag && double.TryParse(tag, out var seconds))
        {
            _settings.PredictiveLeadSeconds = seconds;
            _settings.Save();
        }
    }

    private void LoggingToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.ThermalLogging = LoggingToggle.IsChecked == true;
        _settings.Save();
        RefreshLoggingChip();
    }

    // Whether logging was already running when this window opened. A box ticked just now is
    // saved but not yet writing, and the chip has to distinguish those two states.
    private bool _thermalLoggingAtLoad;

    private void RefreshLoggingChip()
    {
        bool wanted = _settings.ThermalLogging;

        // Three states, not two. "ON NEXT START" is the one that matters: ThermalLog is
        // constructed once in MainWindow's constructor, so a freshly enabled setting is not
        // writing anything yet. Showing ACTIVE would send someone looking for a file that
        // does not exist, and turning it back off returns to plain OFF, not to pending.
        string label = !wanted ? "OFF"
            : _thermalLoggingAtLoad ? "LOGGING"
            : "ON NEXT START";

        bool live = wanted && _thermalLoggingAtLoad;
        SetChip(LoggingChip, LoggingChipText, live, label);

        // Pending reads amber: enabled, but not doing anything yet.
        if (wanted && !_thermalLoggingAtLoad)
        {
            LoggingChipText.Foreground = (Brush)FindResource("WarnBrush");
            LoggingChip.BorderBrush = (Brush)FindResource("WarnBrush");
        }
    }

    /// <summary>
    /// Paints an active-state chip. State is passed in rather than read from a control, so the
    /// indicator reflects what is actually true rather than what a switch is showing.
    /// </summary>
    private void SetChip(Border chip, TextBlock text, bool active, string activeLabel = "ACTIVE")
    {
        text.Text = active ? activeLabel : (activeLabel == "ACTIVE" ? "OFF" : activeLabel);
        text.Foreground = (Brush)FindResource(active ? "AccentBrush" : "TextFaintBrush");
        chip.BorderBrush = (Brush)FindResource(active ? "AccentBrush" : "BorderBrush");
        chip.Background = (Brush)FindResource(active ? "AccentSoftBrush" : "PanelAltBrush");
    }

    private void OpenLogsBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Created here rather than assumed to exist: the folder only appears once
            // logging has actually run, and opening a missing path just fails silently.
            System.IO.Directory.CreateDirectory(OmniHub.Core.Fan.ThermalLog.LogDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = OmniHub.Core.Fan.ThermalLog.LogDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message, "Could not open the log folder",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------- power plan

    private OmniHub.Core.Optimize.PowerPlanAutomation? _powerPlan;

    /// <summary>
    /// Boost mode written to the mains plan: 2 = Aggressive.
    ///
    /// It was 3 here, from the assumption that the indices run Disabled, Enabled, Efficient
    /// Enabled, Aggressive. They do not. Windows enumerates them on this machine as 0 Disabled,
    /// 1 Enabled, 2 Aggressive, 3 Efficient Enabled, 4 Efficient Aggressive, 5 Aggressive At
    /// Guaranteed, 6 Efficient Aggressive At Guaranteed -- so 3 would have written Efficient
    /// Enabled under a comment claiming Aggressive.
    ///
    /// Named rather than inlined because it is the one value in the whole feature with a real
    /// thermal cost, and it was chosen by the machine's owner rather than by this code. The
    /// plan builder reads these indices from Windows rather than hard-coding them, which is
    /// what this constant should eventually give way to.
    /// </summary>
    private const uint MainsBoostMode = 2;

    private void InitialisePowerPlanControls()
    {
        _suppressEvents = true;
        PowerPlanToggle.IsChecked = _settings.AutoPowerPlan;
        _suppressEvents = false;

        if (_settings.AutoPowerPlan) StartPowerPlanAutomation(announce: false);
        RefreshPowerPlanChip();
    }

    private void PowerPlanToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;

        _settings.AutoPowerPlan = PowerPlanToggle.IsChecked == true;
        _settings.Save();

        if (_settings.AutoPowerPlan) StartPowerPlanAutomation(announce: true);
        else
        {
            _powerPlan?.Stop();
            // The active plan is deliberately left alone. Switching the machine back on the
            // way out would be a second surprise change from a toggle that was just turned off.
            PowerPlanStatus.Text = "Automation off. The plan currently active was left as it is.";
        }

        RefreshPowerPlanChip();
    }

    /// <summary>
    /// Creates the plans if needed and begins watching.
    ///
    /// Plan creation is idempotent, so this runs at every enable without multiplying entries in
    /// the Windows power menu. Failure turns the switch back off rather than leaving it on over
    /// an automation that is not actually running.
    /// </summary>
    private void StartPowerPlanAutomation(bool announce)
    {
        var (saver, perf, detail) = OmniHub.Core.Optimize.PowerPlanSetup.EnsurePlans(MainsBoostMode);

        if (saver is not { } dc || perf is not { } ac)
        {
            PowerPlanStatus.Text = detail;
            _suppressEvents = true;
            PowerPlanToggle.IsChecked = false;
            _suppressEvents = false;
            _settings.AutoPowerPlan = false;
            _settings.Save();
            return;
        }

        // The OmniHub plans are the default, not the mandate. A picked scheme wins, so someone
        // who already keeps a tuned plan of their own can point the automation at it.
        PopulatePlanChoices(defaultAc: ac, defaultDc: dc);

        Guid useAc = SelectedPlan(AcPlanCombo) ?? ac;
        Guid useDc = SelectedPlan(DcPlanCombo) ?? dc;

        _settings.AcPlanId = useAc;
        _settings.DcPlanId = useDc;
        _settings.Save();

        _powerPlan ??= new OmniHub.Core.Optimize.PowerPlanAutomation();
        _powerPlan.OnApplied -= OnPowerPlanApplied;
        _powerPlan.OnApplied += OnPowerPlanApplied;
        _powerPlan.Start(useAc, useDc);

        if (announce) PowerPlanStatus.Text = detail;
    }

    /// <summary>
    /// Fills both pickers with every scheme on the machine.
    ///
    /// Selection prefers what was saved, then the supplied default, so re-running this does not
    /// silently move a choice the user made. A saved id that no longer exists -- the plan was
    /// deleted from Windows -- falls back rather than leaving the automation pointed at nothing.
    /// </summary>
    private void PopulatePlanChoices(Guid defaultAc, Guid defaultDc)
    {
        var schemes = OmniHub.Core.Optimize.PowerPlan.List();
        if (schemes.Count == 0) return;

        _suppressEvents = true;
        foreach (var (combo, saved, fallback) in new[]
        {
            (AcPlanCombo, _settings.AcPlanId, defaultAc),
            (DcPlanCombo, _settings.DcPlanId, defaultDc),
        })
        {
            combo.Items.Clear();
            object? select = null;

            foreach (var s in schemes)
            {
                var item = new ComboBoxItem { Content = s.Name, Tag = s.Id };
                combo.Items.Add(item);
                if (s.Id == saved) select = item;
                else if (select is null && s.Id == fallback) select = item;
            }

            combo.SelectedItem = select ?? combo.Items[0];
        }
        _suppressEvents = false;
    }

    private static Guid? SelectedPlan(System.Windows.Controls.ComboBox combo) =>
        combo.SelectedItem is ComboBoxItem { Tag: Guid id } ? id : null;

    private void PlanChoice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents) return;
        if (SelectedPlan(AcPlanCombo) is not { } ac || SelectedPlan(DcPlanCombo) is not { } dc) return;

        _settings.AcPlanId = ac;
        _settings.DcPlanId = dc;
        _settings.Save();

        // Re-pointed and applied immediately rather than at the next plug event, so the choice
        // visibly takes effect instead of waiting for a cable to move.
        if (_settings.AutoPowerPlan && _powerPlan is { } automation)
        {
            automation.Start(ac, dc);
            automation.ApplyNow();
        }
        else
        {
            PowerPlanStatus.Text = "Plan choice saved. It applies once the switch above is on.";
        }
    }

    // Raised from the watcher's poll thread; the UI has to be touched on the dispatcher.
    private void OnPowerPlanApplied(string message) =>
        Dispatcher.BeginInvoke(() =>
        {
            PowerPlanStatus.Text = message;
            RefreshPowerPlanChip();
        });

    private void RefreshPowerPlanChip() =>
        SetChip(PowerPlanChip, PowerPlanChipText, _powerPlan?.IsRunning == true, "WATCHING");

    private void RemovePlansBtn_Click(object sender, RoutedEventArgs e)
    {
        // Stopped first: deleting a scheme the automation is about to re-activate would leave
        // the two fighting each other on the next plug event.
        _powerPlan?.Stop();
        _suppressEvents = true;
        PowerPlanToggle.IsChecked = false;
        _suppressEvents = false;
        _settings.AutoPowerPlan = false;
        _settings.AcPlanId = null;
        _settings.DcPlanId = null;
        _settings.Save();

        PowerPlanStatus.Text = OmniHub.Core.Optimize.PowerPlanSetup.Remove();
        RefreshPowerPlanChip();
    }

    // ---------------------------------------------------------------- updates

    private OmniHub.Core.Update.ReleaseInfo? _available;
    private bool _checking;

    /// <summary>
    /// Fills in the version line and pulls the release list once the tab is first built.
    ///
    /// Fire-and-forget on purpose: this is a network call, and the Settings tab must open at
    /// once whether or not GitHub answers. Everything it touches is set back on the dispatcher.
    /// </summary>
    private void InitialiseUpdateControls()
    {
        VersionText.Text = $"OmniHub {OmniHub.Core.Update.UpdateCheck.CurrentVersion}";
        _ = RefreshReleasesAsync(userAsked: false);
    }

    private void CheckUpdateBtn_Click(object sender, RoutedEventArgs e) =>
        _ = RefreshReleasesAsync(userAsked: true);

    private async Task RefreshReleasesAsync(bool userAsked)
    {
        if (_checking) return;
        _checking = true;
        CheckUpdateBtn.IsEnabled = false;
        if (userAsked) UpdateStatus.Text = "Checking...";

        var releases = await OmniHub.Core.Update.UpdateCheck.FetchAsync().ConfigureAwait(true);
        var current = OmniHub.Core.Update.UpdateCheck.CurrentVersion;
        _available = OmniHub.Core.Update.UpdateCheck.NewerThan(releases, current);

        RenderChangelog(releases, current);

        if (_available is { } up)
        {
            UpdateStatus.Text = $"Version {up.Version} is available.";
            UpdateHeadline.Text = $"{up.Title} -- {up.DownloadSize / 1048576} MB";
            UpdateNotes.Text = Summarise(up.Notes);
            UpdateBanner.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateBanner.Visibility = Visibility.Collapsed;

            // "Could not check" and "nothing newer" are different answers, and reporting the
            // first as the second would quietly tell someone they are current when the check
            // never happened.
            UpdateStatus.Text = releases.Count == 0
                ? "Could not reach GitHub. No update information -- this is not a claim that you are up to date."
                : "You are on the newest release.";
        }

        CheckUpdateBtn.IsEnabled = true;
        _checking = false;
    }

    /// <summary>First paragraph only; the full text is one click away on the release page.</summary>
    private static string Summarise(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return "No notes were published with this release.";
        int split = notes.IndexOf("\n\n", StringComparison.Ordinal);
        string head = split > 0 ? notes[..split] : notes;
        return head.Length > 400 ? head[..400].TrimEnd() + "..." : head.Trim();
    }

    private void RenderChangelog(IReadOnlyList<OmniHub.Core.Update.ReleaseInfo> releases, Version current)
    {
        ChangelogList.Items.Clear();

        if (releases.Count == 0)
        {
            ChangelogList.Items.Add(new TextBlock
            {
                Text = "The release list could not be read. It needs a network connection.",
                Style = (Style)FindResource("MutedText"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var r in releases)
        {
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            header.Children.Add(new TextBlock
            {
                Text = r.Tag,
                Style = (Style)FindResource("SettingTitle"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            header.Children.Add(new TextBlock
            {
                Text = r.PublishedAt == DateTimeOffset.MinValue
                    ? ""
                    : r.PublishedAt.ToLocalTime().ToString("d MMM yyyy"),
                Style = (Style)FindResource("MutedText"),
                FontSize = 10.5,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });

            // The running build is marked rather than left for the reader to work out by
            // comparing a version string in one place against a tag in another.
            if (r.Version == current)
            {
                header.Children.Add(new TextBlock
                {
                    Text = "INSTALLED",
                    Style = (Style)FindResource("DataLabelText"),
                    Foreground = (Brush)FindResource("GoodBrush"),
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            var block = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            block.Children.Add(header);
            block.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(r.Notes) ? "No notes were published." : r.Notes,
                Style = (Style)FindResource("MutedText"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 640,
                HorizontalAlignment = HorizontalAlignment.Left,
            });
            ChangelogList.Items.Add(block);
        }
    }

    /// <summary>
    /// Downloads the release zip and shows it in Explorer.
    ///
    /// It stops at revealing the file rather than unpacking over the running install. OmniHub
    /// holds the fan service and runs elevated; swapping its own binary underneath itself to
    /// save one manual extract is not a trade worth making in an app whose absence puts the
    /// laptop back on the stock curve.
    /// </summary>
    private async void DownloadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_available is not { } up) return;

        DownloadBtn.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;

        var progress = new Progress<double>(p => DownloadProgress.Value = p);
        string? path = await OmniHub.Core.Update.UpdateCheck.DownloadAsync(up, progress).ConfigureAwait(true);

        DownloadProgress.Visibility = Visibility.Collapsed;
        DownloadBtn.IsEnabled = true;

        if (path is null)
        {
            UpdateStatus.Text = "The download did not complete. The release page has the file if you would rather fetch it yourself.";
            return;
        }

        UpdateStatus.Text = "Downloaded. Exit OmniHub, extract it over your current copy, and start it again.";
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch { /* the path is already in the status line if Explorer will not open */ }
    }

    private void ReleasePageBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _available?.Tag is { } tag
                    ? $"{OmniHub.Core.Update.UpdateCheck.ReleasesPage}/tag/{tag}"
                    : OmniHub.Core.Update.UpdateCheck.ReleasesPage,
                UseShellExecute = true,
            });
        }
        catch { }
    }
}
