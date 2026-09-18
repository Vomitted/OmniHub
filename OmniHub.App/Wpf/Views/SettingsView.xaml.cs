// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OmniHub.App.Wpf;
using UserControl = System.Windows.Controls.UserControl;
using RadioButton = System.Windows.Controls.RadioButton;
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
        NetMonitorToggle.IsChecked = _settings.NetworkMonitorEnabled;
        NetLogToggle.IsChecked = _settings.NetworkLogging;
        NetTargetBox.Text = _settings.NetworkMonitorTarget;
        _thermalLoggingAtLoad = _settings.ThermalLogging;
        InitialiseOverlayControls();
        BuildDensityPills();
        InitialisePaletteEditor();
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
            // The custom palette has no file behind it, so its swatch is built from the palette
            // itself -- and skipped entirely until there is one, because an entry in the picker
            // that resolves to nothing is worse than an entry that is not there yet.
            //
            // Without this the empty source reaches ResourceDictionary and throws, which does not
            // cost a swatch: it takes the whole Settings screen with it, and Settings is where the
            // control for undoing whatever caused it lives.
            ResourceDictionary dict;

            if (string.IsNullOrEmpty(theme.Source))
            {
                if (ThemeManager.Custom is not { } built) continue;

                dict = new ResourceDictionary();
                foreach (var (key, hex) in built.Build())
                    if (OmniHub.Core.Theming.Rgb.Parse(hex) is { } c) dict[key] = Color.FromRgb(c.R, c.G, c.B);

                dict["RadiusMd"] = new CornerRadius(built.RadiusMd);
                dict["RadiusSm"] = new CornerRadius(Math.Max(0, built.RadiusMd - 1));
                dict["CardPadding"] = new Thickness(14);
            }
            else
            {
                dict = new ResourceDictionary { Source = new Uri(theme.Source, UriKind.Relative) };
            }

            Color Pick(string key, Color fallback) => dict[key] is Color c ? c : fallback;

            var bg = Pick("BackgroundColor", Colors.Black);
            var panel = Pick("PanelColor", Colors.DimGray);
            var accent = Pick("AccentColor", Colors.SkyBlue);
            var accent2 = Pick("AccentColor2", accent);
            var text = Pick("TextPrimaryColor", Colors.White);
            var border = Pick("BorderColor", Colors.Gray);

            // The preview is a miniature CARD, drawn with the theme's own corner radius.
            //
            // Three colour chips alone could not tell two themes apart when both used the
            // same accent, which is precisely how OLED Black and Midnight ended up looking
            // identical in this picker. Now a palette also sets shape and density, and the
            // swatch has to show that: the outer border takes RadiusMd, the inner card takes
            // RadiusSm, and the gap between them is the theme's own CardPadding scaled down.
            // A square, tight preview and a round, roomy one are distinguishable at a glance
            // even when the hue is close.
            var mdRadius = dict["RadiusMd"] is CornerRadius rMd ? rMd : new CornerRadius(6);
            var smRadius = dict["RadiusSm"] is CornerRadius rSm ? rSm : new CornerRadius(3);
            var pad = dict["CardPadding"] is Thickness p ? p.Left : 16.0;
            double inset = Math.Clamp(pad / 3.0, 2, 8);

            var preview = new Border
            {
                Width = 132,
                Height = 56,
                CornerRadius = mdRadius,
                Background = new SolidColorBrush(bg),
                BorderBrush = new SolidColorBrush(border),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(inset),
                Child = new Border
                {
                    CornerRadius = smRadius,
                    Background = new SolidColorBrush(panel),
                    BorderBrush = new SolidColorBrush(border),
                    BorderThickness = new Thickness(1),
                    Child = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Children =
                        {
                            Swatch(text), Swatch(accent), Swatch(accent2),
                        },
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
    /// <summary>
    /// Three pills for the three densities.
    ///
    /// Built here rather than in markup for the same reason the workspace switcher is: the options
    /// come from an enum, and writing them out again is a second list that can disagree with the
    /// first.
    /// </summary>
    private void BuildDensityPills()
    {
        DensityPills.Children.Clear();

        foreach (var density in Enum.GetValues<OmniHub.Core.Optimize.UiDensity>())
        {
            var captured = density;

            var pill = new System.Windows.Controls.RadioButton
            {
                Content = density.ToString().ToUpperInvariant(),
                GroupName = "Density",
                Style = (Style)FindResource("PillRadioStyle"),
                Height = 28,
                MinWidth = 92,
                Margin = new Thickness(0, 0, 3, 0),
                IsChecked = density == _settings.Density,
            };

            pill.Checked += (_, _) =>
            {
                _settings.Density = captured;
                _settings.Save();

                // Immediately, like the theme beside it. A density you have to restart to see is
                // not a choice, it is a setting.
                ThemeManager.ApplyDensity(captured);
            };

            DensityPills.Children.Add(pill);
        }
    }

    // ---------- the palette somebody builds ----------

    private bool _suppressPaletteEvents;

    private void InitialisePaletteEditor()
    {
        _suppressPaletteEvents = true;

        PaletteBackgroundBox.Text = _settings.CustomPaletteBackground;
        PalettePanelBox.Text = _settings.CustomPalettePanel;
        PaletteAccentBox.Text = _settings.CustomPaletteAccent;
        PaletteTextBox.Text = _settings.CustomPaletteText;
        PaletteRadiusBox.Text = _settings.CustomPaletteRadius.ToString();

        _suppressPaletteEvents = false;

        RefreshPaletteStatus();
    }

    /// <summary>
    /// The four boxes as a palette, or null when one of them is not a colour.
    ///
    /// Read from the boxes rather than from settings, so the verdict shown is about what is on
    /// screen rather than about what was last saved -- which is the whole point of showing it
    /// before the button is pressed.
    /// </summary>
    private OmniHub.Core.Theming.CustomPalette? PaletteFromBoxes()
    {
        var background = OmniHub.Core.Theming.Rgb.Parse(PaletteBackgroundBox.Text);
        var panel = OmniHub.Core.Theming.Rgb.Parse(PalettePanelBox.Text);
        var accent = OmniHub.Core.Theming.Rgb.Parse(PaletteAccentBox.Text);
        var text = OmniHub.Core.Theming.Rgb.Parse(PaletteTextBox.Text);

        if (background is null || panel is null || accent is null || text is null) return null;

        int radius = int.TryParse(PaletteRadiusBox.Text, out int r) ? Math.Clamp(r, 0, 24) : 10;

        return new OmniHub.Core.Theming.CustomPalette(
            "Custom", background.Value, panel.Value, accent.Value, text.Value, radius);
    }

    private void PaletteChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // TextChanged fires while the view is still being built, before the other boxes exist.
        if (_suppressPaletteEvents || PaletteStatus is null) return;

        RefreshPaletteStatus();
    }

    /// <summary>
    /// Says whether what is in the boxes could be used, before anybody presses the button.
    ///
    /// Naming the failing pair rather than saying the palette is unreadable. One tells somebody to
    /// give up; the other tells them which of the four to change.
    /// </summary>
    private void RefreshPaletteStatus()
    {
        if (PaletteFromBoxes() is not { } palette)
        {
            PaletteStatus.Text = "One of those is not a colour. Use six hex digits, such as #0A0D12.";
            PaletteApplyBtn.IsEnabled = false;
            return;
        }

        var problems = palette.Problems();

        PaletteApplyBtn.IsEnabled = problems.Count == 0;

        PaletteStatus.Text = problems.Count == 0
            ? "Readable. Every text colour clears the contrast threshold on every surface it meets."
            : "Not readable yet:\n  " + string.Join("\n  ", problems);
    }

    private void PaletteApply_Click(object sender, RoutedEventArgs e)
    {
        if (PaletteFromBoxes() is not { } palette || !palette.IsReadable) return;

        _settings.CustomPaletteBackground = palette.Background.ToString();
        _settings.CustomPalettePanel = palette.Panel.ToString();
        _settings.CustomPaletteAccent = palette.Accent.ToString();
        _settings.CustomPaletteText = palette.TextPrimary.ToString();
        _settings.CustomPaletteRadius = palette.RadiusMd;
        _settings.ThemeName = ThemeManager.CustomId;
        _settings.Save();

        ThemeManager.ApplyCustom(palette);
    }

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
        OverlayScaleSlider.Value = Math.Clamp(_settings.OverlayScale, 0.7, 2.0);
        OverlayScaleLabel.Text = $"{OverlayScaleSlider.Value * 100:0}%";
        OverlaySparklineToggle.IsChecked = _settings.OverlaySparklines;
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

    private void OverlayScale_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressOverlayEvents || OverlayScaleLabel is null) return;

        _settings.OverlayScale = OverlayScaleSlider.Value;
        OverlayScaleLabel.Text = $"{OverlayScaleSlider.Value * 100:0}%";
        _settings.Save();
        Owner()?.RefreshOverlayAppearance();
    }

    private void OverlaySparklines_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressOverlayEvents) return;

        _settings.OverlaySparklines = OverlaySparklineToggle.IsChecked == true;
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

    /// <summary>
    /// Network settings take effect immediately rather than on next launch.
    ///
    /// The thermal log's toggle warns that it waits for a restart, and that is a wart rather than
    /// a pattern worth copying: a switch that does nothing yet, while saying it is on, is
    /// indistinguishable from a switch that is broken. MainWindow.StartNetworkMonitor is
    /// idempotent and re-reads every one of these, so calling it is the whole implementation.
    /// </summary>
    private void ApplyNetworkSettings()
    {
        _settings.Save();
        if (Window.GetWindow(this) is MainWindow main) main.StartNetworkMonitor();
    }

    private void NetMonitor_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.NetworkMonitorEnabled = NetMonitorToggle.IsChecked == true;
        ApplyNetworkSettings();
    }

    private void NetLog_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        _settings.NetworkLogging = NetLogToggle.IsChecked == true;
        ApplyNetworkSettings();
    }

    private void NetTarget_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        string wanted = NetTargetBox.Text.Trim();

        // An empty box means the default, not "watch nothing". Silently monitoring an empty
        // string would leave the feature switched on and permanently reporting no answer.
        if (wanted.Length == 0)
        {
            wanted = "1.1.1.1";
            NetTargetBox.Text = wanted;
        }

        if (string.Equals(wanted, _settings.NetworkMonitorTarget, StringComparison.OrdinalIgnoreCase)) return;
        _settings.NetworkMonitorTarget = wanted;
        ApplyNetworkSettings();
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
        var (saver, perf, detail) = OmniHub.Core.Optimize.PowerPlanSetup.EnsurePlans();

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
    /// Downloads the update and installs it: progress bar, then the application restarts.
    ///
    /// This used to stop at revealing the file in Explorer, and the reasoning was sound at
    /// the time: OmniHub holds the fan service and runs elevated, and an application that
    /// swaps its own binary underneath itself is a bad trade in a program whose absence puts
    /// the laptop back on the stock BIOS curve. What changed is that there is now an
    /// installer to hand the job to, so nothing has to overwrite anything underneath itself.
    ///
    /// The sequence, and why it is in this order: setup is launched first and OmniHub closes
    /// itself immediately afterwards. Closing goes through Application.Shutdown, which raises
    /// Closed, which runs MainWindow.Cleanup -- the path that hands fan control back to the
    /// BIOS. Setup waits for this process s mutex to disappear before it touches anything, so
    /// the overlap is safe and the user is never asked to close an application that is
    /// already closing. Setup then relaunches OmniHub with its own elevated token, so there
    /// is no second UAC prompt.
    ///
    /// A release older than 1.3.0 published a zip rather than an installer, and there is
    /// nothing to hand off to in that case, so that path still reveals the file.
    /// </summary>
    private async void DownloadBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_available is not { } up) return;

        DownloadBtn.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;

        var progress = new Progress<double>(p => DownloadProgress.Value = p);
        var (path, error) = await OmniHub.Core.Update.UpdateCheck.DownloadAsync(up, progress).ConfigureAwait(true);

        DownloadProgress.Visibility = Visibility.Collapsed;
        DownloadBtn.IsEnabled = true;

        if (path is null)
        {
            // The reason is shown rather than swallowed. "It did not complete" covers a dropped
            // connection and a file that failed its checksum equally, and those deserve very
            // different reactions from whoever is reading.
            UpdateStatus.Text = error
                ?? "The download did not complete. The release page has the file if you would rather fetch it yourself.";
            return;
        }

        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            // A zip, from a release older than 1.3.0. Nothing to hand off to.
            UpdateStatus.Text = "Downloaded. Exit OmniHub, extract it over your current copy, and start it again.";
            Reveal(path);
            return;
        }

        UpdateStatus.Text = "Installing. OmniHub will close and reopen when it finishes.";
        DownloadBtn.IsEnabled = false;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                // SILENT shows setup s own progress window and skips the wizard pages; the
                // user already chose to update and has nothing left to decide. NORESTART
                // forbids rebooting Windows. RESTARTAPP is ours, and is what tells setup to
                // start OmniHub again afterwards rather than leaving the machine on the
                // stock fan curve.
                Arguments = "/SILENT /NORESTART /RESTARTAPP",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = $"The installer would not start ({ex.Message}). The file is in your Downloads folder.";
            DownloadBtn.IsEnabled = true;
            Reveal(path);
            return;
        }

        // Closed, not killed. Shutdown raises Closed on the main window, which runs Cleanup,
        // which is what returns the fans to the BIOS before setup replaces the binary.
        System.Windows.Application.Current.Shutdown();
    }

    private static void Reveal(string path)
    {
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
