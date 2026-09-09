using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using UserControl = System.Windows.Controls.UserControl;
using OmniHub.Core.Apps;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.App.Wpf.Views;
using WinForms = System.Windows.Forms;

namespace OmniHub.App.Wpf;

public partial class MainWindow : Window
{
    private readonly HardwareContext _ctx;
    private readonly FanService _service;
    private readonly AppSettings _settings;
    private readonly Dictionary<string, UserControl> _views = new();

    /// <summary>
    /// How to build the tabs that are not needed until someone opens them. A key lives here or
    /// in <see cref="_views"/>, never both -- <see cref="ResolveView"/> moves it across on first
    /// use. Anything still here has never been constructed, which is also why Cleanup can keep
    /// iterating _views and dispose only what actually exists.
    /// </summary>
    private readonly Dictionary<string, Func<UserControl>> _viewFactories = new();
    private WinForms.NotifyIcon? _trayIcon;
    private TrayFlyout? _flyout;
    private FansView _fansView = null!;

    /// <summary>
    /// The GPU screen. Built at startup for the TGP unlock in its constructor, then handed to
    /// the Performance group rather than owned as a tab of its own.
    /// </summary>
    private GpuView _gpuView = null!;
    private bool _allowClose;
    private bool _cleanedUp;
    private bool _wasThrottling;
    private readonly HashSet<string> _seenAppPaths = new(StringComparer.OrdinalIgnoreCase);
    private string? _suggestedAppPath;
    private System.Threading.Timer? _appWatchTimer;
    private ThermalLog? _thermalLog;
    private OverlayWindow? _overlay;

    public MainWindow()
    {
        InitializeComponent();

        _settings = AppSettings.Load();
        _ctx = new HardwareContext();
        // Reads the poll loop's cached value rather than querying the sensor again. Two
        // independent reads meant the displayed temperature and the temperature the curve
        // acted on were taken at different moments and routinely disagreed; now there is one
        // reading per tick and the screen shows exactly what the fan is responding to.
        // CurrentTemperatureC throws if polling has produced nothing yet or the last reading
        // has gone stale, and FanService already skips a tick whose read failed.
        _service = new FanService(_ctx.Fan, () => _ctx.CurrentTemperature(), _settings.BuildCurve())
        {
            // The discrete GPU shares these fans. Reading it is cached inside GpuTelemetry, so
            // the curve's own tick does not pay for a process spawn every time.
            ReadSecondaryTempC = () => GpuTelemetry.Read()?.TempC,
        };

        ModelLabel.Text = $"{_ctx.Model.Manufacturer} {_ctx.Model.Product}".Trim();

        _fansView = new FansView(_ctx, _service, _settings);
        _fansView.ModeChanged += UpdateActiveModeLabel;
        // Built now, because each does something at startup that must not wait for a click:
        // the dashboard is the landing tab, FansView drives the saved fan mode through
        // ApplySavedMode below, and GpuView's constructor applies the TGP unlock (see the
        // comment on that constructor, which relies on being built here).
        _views["dashboard"] = new DashboardView(_ctx, _service, _settings);
        _views["fans"] = _fansView;

        // Held rather than placed in _views: the GPU screen now lives inside the Performance
        // group, but it still has to be BUILT at startup, because its constructor applies the
        // TGP unlock. Handing the group this instance keeps the unlock on the launch path while
        // the screen itself moves behind a sub-selector.
        _gpuView = new GpuView(_ctx, _settings);

        // Built on first visit. All eight were constructed before the first frame, so launching
        // the app paid for every tab whether or not it was ever opened -- and two of these are
        // expensive: TuningView builds roughly forty controls imperatively and opens the SMU,
        // and SettingsView loads all four palette dictionaries to draw its theme swatches and
        // fires an HTTPS request to the GitHub releases API. Neither belongs on the path to
        // showing a window.
        _viewFactories["power"] = () => new PowerView(_ctx);
        _viewFactories["diagnostics"] = () => new DiagnosticsView(_ctx);
        _viewFactories["settings"] = () => new SettingsView(_settings);

        // Grouped by subject rather than by screen. CPU tuning and GPU power are two halves of
        // one decision about how much the machine may draw, and app GPU routing is a Windows-side
        // setting like the rest of the System tab. Neither screen's markup moves; only the route
        // to it does. GroupView builds its sections lazily too, so opening Performance does not
        // construct the half nobody looked at.
        _viewFactories["performance"] = () => new GroupView(
            ("CPU", () => new TuningView(_ctx, _settings)),
            ("GPU", () => _gpuView));

        _viewFactories["system"] = () => new GroupView(
            ("Windows", () => new OptimizeView(_settings, _ctx)),
            ("App GPU routing", () => new AppRoutingView()));

        // Neither the timer resolution nor the MMCSS request survives a process restart, so
        // a saved preference has to be re-asserted here or the toggle would show "on" while
        // the system had long since reverted.
        if (_settings.HighResolutionTimer) OmniHub.Core.Optimize.SystemTuning.ApplyHighResolutionTimer();
        if (_settings.DwmMmcss) OmniHub.Core.Optimize.SystemTuning.SetMmcss(true);

        ViewHost.Content = _views["dashboard"];

        BuildTrayIcon();

        // Predictive lead is applied from settings rather than hardcoded, and defaults to 0
        // (disabled), so an existing install keeps behaving exactly as it did until opted in.
        _service.PredictiveLeadSeconds = _settings.PredictiveLeadSeconds;
        if (_settings.ThermalLogging) _thermalLog = new ThermalLog();

        _ctx.StartPolling(TimeSpan.FromSeconds(2));
        _fansView.ApplySavedMode();
        UpdateActiveModeLabel();
        _ctx.OnReading += _ => ReassertGpuPower();
        _ctx.OnReading += OnThrottleCheck;
        _ctx.OnReading += OnLogReading;
        _ctx.OnReading += OnOverlayReading;
        if (_settings.OverlayEnabled) SetOverlayVisible(true);

        // Deferred to Loaded rather than done here: App calls Show() after this constructor
        // returns, so hiding a window that has not been shown yet accomplishes nothing and the
        // app came up visible whatever the setting said. Everything else has already been
        // applied by this point, so starting hidden costs no functionality.
        if (_settings.StartMinimizedToTray) Loaded += (_, _) => Hide();
        _ctx.OnReading += r => Dispatcher.BeginInvoke(() => { UpdateRibbonColour(r); PulseActivity(); });

        StartActivityRibbon();

        Closing += OnClosing;
        Closed += (_, _) => Cleanup();
        ThemeManager.ThemeChanged += OnThemeChanged;
        // Minimize now behaves like minimize: the window goes to the taskbar and stays
        // there. It used to call Hide(), which removed it from the taskbar and Alt-Tab
        // entirely, leaving only the tray icon -- indistinguishable from the app having
        // closed itself. Going to the tray is the CLOSE button's job (see OnClosing and
        // the Close Behavior setting); the two gestures should not do the same thing.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Cleanup();
    }

    // The caption bar, its buttons and the window border are drawn by DWM, not WPF, so no
    // amount of XAML reaches them -- which is why a black app sat under a white title bar.
    // OnSourceInitialized is the first point where the HWND exists to talk to.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyToWindowFrame(this);
        RegisterOverlayHotkey(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    private void OnThemeChanged(ThemeDefinition theme) => ThemeManager.ApplyToWindowFrame(this);

    // ---------- activity ribbon ----------

    private DateTime _lastRibbonPulse = DateTime.MinValue;

    private void StartActivityRibbon()
    {
        // Honours the system's "show animations" preference. A continuously moving element
        // is exactly what that setting exists to suppress, and ignoring it is an
        // accessibility failure, not a style choice.
        if (!SystemParameters.ClientAreaAnimation)
        {
            ActivityRibbon.Opacity = 0;
            return;
        }

        // Translating in relative units against a SpreadMethod="Repeat" brush: sliding by
        // exactly one tile width and repeating forever gives a seamless loop with no jump at
        // the wrap point. 9s is slow enough to read as ambient rather than as a progress bar.
        var slide = new DoubleAnimation(0, 0.32, TimeSpan.FromSeconds(9))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        RibbonSlide.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    /// <summary>
    /// Brightens the ribbon briefly. Called when something real happens rather than on a
    /// timer, so the motion carries information instead of being decoration.
    /// </summary>
    private void PulseActivity()
    {
        if (!SystemParameters.ClientAreaAnimation) return;

        // Rate-limited: the poll loop fires every 2s and actions can land in bursts, and a
        // ribbon that is permanently mid-pulse conveys nothing.
        var now = DateTime.UtcNow;
        if ((now - _lastRibbonPulse) < TimeSpan.FromMilliseconds(700)) return;
        _lastRibbonPulse = now;

        var pulse = new DoubleAnimationUsingKeyFrames();
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.95, KeyTime.FromPercent(0.12), new CubicEase { EasingMode = EasingMode.EaseOut }));
        pulse.KeyFrames.Add(new EasingDoubleKeyFrame(0.3, KeyTime.FromPercent(1.0), new CubicEase { EasingMode = EasingMode.EaseOut }));
        pulse.Duration = TimeSpan.FromMilliseconds(1100);
        ActivityRibbon.BeginAnimation(OpacityProperty, pulse);
    }

    private void NavChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.RadioButton rb) return;
        MoveNavIndicator(rb);
        if (rb.Tag is string key && ResolveView(key) is { } view) AnimateTo(view);
    }

    /// <summary>
    /// The view for a nav key, building it on first request and caching it afterwards.
    ///
    /// Views are cached rather than rebuilt per visit because they hold live subscriptions and
    /// per-tab state, and because the entrance animation already depends on reuse -- see
    /// ResetStaggeredCards, which exists precisely because an abandoned stagger would otherwise
    /// leave a reused view's cards stuck at zero opacity.
    /// </summary>
    private UserControl? ResolveView(string key)
    {
        if (_views.TryGetValue(key, out var existing)) return existing;
        if (!_viewFactories.TryGetValue(key, out var build)) return null;

        UserControl view;
        try
        {
            view = build();
        }
        catch (Exception ex)
        {
            // A tab that cannot be built must not take the application down with it.
            //
            // This process holds fan control. If it dies the laptop reverts to the stock BIOS
            // curve, including the 0%-while-hot behaviour this application exists to prevent --
            // so losing the process is a worse outcome than losing a tab. Building every view up
            // front used to mean such a failure surfaced at launch; deferring construction moved
            // it to a click, and an exception out of a click handler reaches the dispatcher
            // unhandled. Caught here, and shown in place of the tab.
            view = FailedTab(key, ex);
        }

        _views[key] = view;
        _viewFactories.Remove(key);
        return view;
    }

    /// <summary>Stands in for a tab whose constructor threw, naming what happened.</summary>
    private static UserControl FailedTab(string key, Exception ex) => new()
    {
        Content = new TextBlock
        {
            Margin = new Thickness(24),
            TextWrapping = TextWrapping.Wrap,
            Text = $"The {key} tab could not be opened.\n\n{ex.Message}\n\n"
                 + "Everything else, including fan control, is still running.",
        },
    };

    /// <summary>
    /// Slides the sidebar's selection rail to the chosen item.
    ///
    /// Position is measured from the live visual tree rather than computed from item heights:
    /// the nav buttons carry margins and the style could change either, and a hardcoded stride
    /// would drift silently the first time one did.
    /// </summary>
    private void MoveNavIndicator(FrameworkElement target)
    {
        // The tree is not arranged yet on the very first Checked, which fires during
        // InitializeComponent; deferring to Loaded priority gives it real coordinates.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (NavItems is null || NavIndicator is null || target.ActualHeight <= 0) return;

            double y;
            try
            {
                y = target.TransformToAncestor(NavItems).Transform(new Point(0, 0)).Y;
            }
            catch (InvalidOperationException)
            {
                // Thrown when the element is not yet connected to NavItems' visual tree.
                return;
            }

            // Centre a fixed-height rail on the item rather than matching its full height, so
            // the indicator reads as a marker and not a second background.
            double top = y + (target.ActualHeight - NavIndicator.Height) / 2.0;

            if (!SystemParameters.ClientAreaAnimation)
            {
                NavIndicatorSlide.Y = top;
                NavIndicator.Opacity = 1;
                return;
            }

            // First appearance jumps into place and fades in: there is no previous position to
            // travel from, and sliding down from zero would read as a glitch.
            if (NavIndicator.Opacity < 0.01)
            {
                NavIndicatorSlide.Y = top;
                NavIndicator.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
                return;
            }

            var slide = new DoubleAnimation(top, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            NavIndicatorSlide.BeginAnimation(TranslateTransform.YProperty, slide);
        });
    }

    // Incremented on every navigation. Both the fade-out completion and the deferred stagger
    // capture the value they were started with and abandon themselves if it has moved on.
    //
    // Without this, switching tabs faster than the 100ms fade-out meant two completion
    // handlers were in flight: the stale one would assign its own (now wrong) view and start a
    // second stagger over the top of the first, which is the flicker.
    private int _navToken;

    // Views are cached and reused, so a stagger that gets abandoned mid-flight leaves its
    // cards at Opacity 0 permanently. Whatever was last staggered is tracked so it can be put
    // back before anything else runs.
    private UserControl? _staggeredView;

    private void AnimateTo(UserControl view)
    {
        if (ReferenceEquals(ViewHost.Content, view)) return;
        PulseActivity();

        int token = ++_navToken;
        ResetStaggeredCards();

        // Straight swap when the system has animations turned off.
        if (!SystemParameters.ClientAreaAnimation)
        {
            ViewHost.Content = view;
            return;
        }

        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
        fadeOut.Completed += (_, _) =>
        {
            if (token != _navToken) return; // superseded by a later navigation

            ViewHost.Content = view;
            ViewSlideTransform.X = 18;
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            var slideIn = new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            ViewHost.BeginAnimation(OpacityProperty, fadeIn);
            ViewSlideTransform.BeginAnimation(TranslateTransform.XProperty, slideIn);

            // Cards rise in behind the view fade, each one a beat after the last. The
            // staggering is what separates "a screen appeared" from "a screen assembled";
            // a uniform fade of everything at once reads as a repaint.
            StaggerCards(view, token);
        };
        ViewHost.BeginAnimation(OpacityProperty, fadeOut);
    }

    /// <summary>
    /// Puts the previously staggered view's cards back to their resting state. Clearing the
    /// animation with BeginAnimation(prop, null) matters: an animation left in place holds the
    /// property at its animated value and any plain assignment afterwards is ignored, so the
    /// card would stay invisible no matter what opacity was written.
    /// </summary>
    private void ResetStaggeredCards()
    {
        if (_staggeredView is null) return;

        var panel = FindContentStack(_staggeredView);
        _staggeredView = null;
        if (panel is null) return;

        foreach (var child in panel.Children.OfType<FrameworkElement>())
        {
            child.BeginAnimation(OpacityProperty, null);
            child.Opacity = 1;
            if (child.RenderTransform is TranslateTransform t)
            {
                t.BeginAnimation(TranslateTransform.YProperty, null);
                t.Y = 0;
            }
        }
    }

    /// <summary>
    /// Fades and lifts the top-level cards of a view in sequence. Deliberately shallow: only
    /// the direct children of the view's outermost StackPanel are animated, so this stays
    /// O(cards) rather than walking the whole visual tree on every tab change.
    /// </summary>
    private void StaggerCards(UserControl view, int token)
    {
        // The view has just been assigned, so its tree is not built yet; wait for layout.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            // Checked again here, not just at fade-out: this callback is deferred to Loaded
            // priority, which is long enough for another tab click to land in between.
            if (token != _navToken) return;

            var panel = FindContentStack(view);
            if (panel is null) return;
            _staggeredView = view;

            int index = 0;
            foreach (var child in panel.Children.OfType<FrameworkElement>())
            {
                // Section labels are tiny and would flicker rather than animate; skip them so
                // the sequence lands on the cards themselves.
                if (child is TextBlock) continue;
                if (index > 7) break; // anything below the fold is not worth animating

                var offset = new TranslateTransform(0, 14);
                child.RenderTransform = offset;
                child.Opacity = 0;

                var delay = TimeSpan.FromMilliseconds(40 * index);
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260))
                { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                var rise = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(300))
                { BeginTime = delay, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

                child.BeginAnimation(OpacityProperty, fade);
                offset.BeginAnimation(TranslateTransform.YProperty, rise);
                index++;
            }
        });
    }

    /// <summary>
    /// The panel whose children are a view's cards, or null when a view is not built that way.
    ///
    /// Leaf views are all ScrollViewer &gt; Panel. A GroupView is not: it is a Grid holding a
    /// section selector and a host, with the real screen one level further in -- so the plain
    /// check returned null for it and the entrance animation silently stopped happening on the
    /// grouped tabs. Silently, because a null here is a legitimate "this view is shaped
    /// differently, skip the stagger", which is exactly the kind of quiet regression a grouped
    /// tab would have shipped with.
    ///
    /// Still returns null rather than guessing on anything else.
    /// </summary>
    private static System.Windows.Controls.Panel? FindContentStack(UserControl view)
    {
        if (view is GroupView group && group.CurrentSection is { } section)
            return FindContentStack(section);

        if (view.Content is System.Windows.Controls.ScrollViewer { Content: System.Windows.Controls.Panel panel })
            return panel;

        return null;
    }

    private void UpdateActiveModeLabel()
    {
        ActiveModeLabel.Text = _settings.FanControlMode switch
        {
            FanControlMode.Auto => "Auto (Curve)",
            FanControlMode.BiosDefault => "BIOS Default",
            FanControlMode.Max => "Max Fan",
            _ => "--",
        };
    }

    private void BuildTrayIcon()
    {
        var menu = new WinForms.ContextMenuStrip();
        var showItem = new WinForms.ToolStripMenuItem("Show OmniHub");
        showItem.Click += (_, _) => RestoreFromTray();
        var autoItem = new WinForms.ToolStripMenuItem("Auto (Curve)");
        autoItem.Click += (_, _) => { _fansView.ApplyModeFromTray(FanControlMode.Auto); UpdateActiveModeLabel(); };
        var biosItem = new WinForms.ToolStripMenuItem("BIOS Default");
        biosItem.Click += (_, _) => { _fansView.ApplyModeFromTray(FanControlMode.BiosDefault); UpdateActiveModeLabel(); };
        var maxItem = new WinForms.ToolStripMenuItem("Max Fan");
        maxItem.Click += (_, _) => { _fansView.ApplyModeFromTray(FanControlMode.Max); UpdateActiveModeLabel(); };
        var exitItem = new WinForms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => { _allowClose = true; Close(); };

        menu.Items.Add(showItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(autoItem);
        menu.Items.Add(biosItem);
        menu.Items.Add(maxItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        System.Drawing.Icon? icon = null;
        try { icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location); } catch { }

        _trayIcon = new WinForms.NotifyIcon
        {
            Icon = icon ?? System.Drawing.SystemIcons.Application,
            Text = "OmniHub",
            Visible = true,
            ContextMenuStrip = menu,
        };
        // Single left-click opens the quick-glance flyout; double-click (which fires its
        // own leading single click too, an unavoidable NotifyIcon quirk) restores the
        // full window and dismisses the flyout so the two don't both end up on screen.
        _trayIcon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) ShowFlyout(); };
        _trayIcon.DoubleClick += (_, _) => { _flyout?.Hide(); RestoreFromTray(); };
        _trayIcon.BalloonTipClicked += (_, _) => AcceptSuggestedApp();

        // Seed the "already seen" set with everything already running *before* the
        // watcher starts -- otherwise the first scan would treat every app the user
        // already had open (browser, Discord, whatever) as "newly launched" and
        // immediately suggest whichever one happens to come first, which isn't what
        // "detect a NEW app" means. Only apps that launch after this point should
        // ever trigger a suggestion.
        //
        // Runs off the UI thread and the watch timer only starts once it completes:
        // enumerating every process and opening each one's MainModule can take well
        // over a second on a system with many processes, which would otherwise freeze
        // app startup -- the same class of mistake already fixed elsewhere in this app
        // for BIOS calls. Automatic per-app GPU detection: periodically scans for
        // newly-launched apps with a visible window that aren't already routed, and
        // suggests one via a notification instead of requiring the user to open a
        // picker. Each exe path is only ever suggested once per app session (via
        // _seenAppPaths), whether or not the suggestion is accepted, so this can't nag
        // repeatedly about the same app.
        Task.Run(() =>
        {
            foreach (var app in RunningAppDetector.GetVisibleApps()) _seenAppPaths.Add(app.ExecutablePath);
        }).ContinueWith(_ =>
        {
            _appWatchTimer = new System.Threading.Timer(__ => ScanForNewApps(), null,
                TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(20));
        }, TaskScheduler.Default);
    }

    // Logs the raw reading plus whatever the curve commanded for it. Deliberately records
    // the BIOS's own read-back fan levels alongside our commanded percentage: if those two
    // ever disagree, that is the single most useful fact in the file -- it means the machine
    // is not honouring the command, which no amount of curve tuning would fix.
    // Same thresholds the Dashboard's thermal card uses, so the ribbon and the number can
    // never disagree about what counts as hot.
    private void UpdateRibbonColour(Reading r)
    {
        var key = r.Throttling == ThrottlingState.On || r.TemperatureC >= 80 ? "DangerColor"
                : r.TemperatureC >= 60 ? "WarnColor"
                : "AccentColor";

        // Fully qualified: this file references WinForms for the tray icon, so a bare Color
        // is ambiguous between System.Drawing and System.Windows.Media.
        if (TryFindResource(key) is System.Windows.Media.Color c) RibbonStop.Color = c;
    }

    private void OnLogReading(Reading r)
    {
        var log = _thermalLog;
        if (log is null) return;

        // HasCommanded, not just IsRunning: the service is "running" from the moment Start()
        // returns, but its first tick has not computed a level yet, and logging the default 0
        // there records a fan command of 0% that never happened.
        // -1 when prediction is off, so a reader can tell "not forecasting" from "forecast
        // happened to equal the reading".
        double forecast = _service.PredictiveLeadSeconds > 0 && _service.Trend.HasEnoughData
            ? _service.Trend.ForecastC(_service.PredictiveLeadSeconds)
            : -1;

        log.Append(DateTime.UtcNow,
            double.IsNaN(r.PreciseTemperatureC) ? r.TemperatureC : r.PreciseTemperatureC,
            forecast, r.FanLevel1, r.FanLevel2,
                   _service.IsRunning && _service.HasCommanded ? _service.LastCommandedLevelPercent : -1,
                   r.Throttling == ThrottlingState.On,
                   _settings.FanControlMode.ToString(),
                   r.TemperatureSource.ToString());
    }

    private void ScanForNewApps()
    {
        try
        {
            var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var routed = new HashSet<string>(
                GpuAppRouting.GetAll().Select(r => r.ExecutablePath), StringComparer.OrdinalIgnoreCase);

            foreach (var app in RunningAppDetector.GetVisibleApps())
            {
                if (!_seenAppPaths.Add(app.ExecutablePath)) continue; // already suggested (or routed) before
                if (routed.Contains(app.ExecutablePath)) continue;
                if (app.ExecutablePath.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(app.ExecutablePath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) continue;

                _suggestedAppPath = app.ExecutablePath;
                _trayIcon?.ShowBalloonTip(6000, "OmniHub",
                    $"New app detected: {app.ProcessName}. Click to route it to the discrete GPU.",
                    WinForms.ToolTipIcon.Info);
                break; // one suggestion at a time -- the next scan picks up any others
            }
        }
        catch { }
    }

    private void AcceptSuggestedApp()
    {
        if (_suggestedAppPath is null) return;
        GpuAppRouting.SetPreference(_suggestedAppPath, AppGpuPreference.HighPerformance);
        _suggestedAppPath = null;

        // App routing now lives inside the System group, which owns building and showing it, so
        // there is nothing to reach into from here. Navigating is enough: the screen reads the
        // registry when it is constructed, and the preference was written a line ago.
        NavSystem.IsChecked = true;
    }

    // Fires a real Windows notification the moment thermal throttling starts, so you
    // find out even if you're not looking at the app or its tray flyout. Only fires on
    // the OFF->ON transition (not every 2s poll while it's ongoing) and resets once
    // throttling clears, so a later episode can notify again.
    /// <summary>
    /// Shows or hides the overlay, creating it on first use.
    ///
    /// Owner is deliberately NOT set. An owned window is force-closed with its owner and, more
    /// to the point, gets dragged in front whenever the main window is activated -- which for
    /// something meant to sit quietly over a game is exactly wrong.
    /// </summary>
    public void SetOverlayVisible(bool visible)
    {
        if (visible)
        {
            _overlay ??= new OverlayWindow(_ctx, _settings);
            _overlay.Show();
            _overlay.MoveToCorner();
        }
        else
        {
            _overlay?.Hide();
        }
    }

    /// <summary>Re-anchors the overlay after the corner setting changes.</summary>
    public void RefreshOverlayPosition() => _overlay?.MoveToCorner();

    /// <summary>Rebuilds the overlay's rows and opacity after a settings change.</summary>
    public void RefreshOverlayAppearance()
    {
        _overlay?.ApplyAppearance();
        _overlay?.MoveToCorner();
    }

    // ---------------------------------------------------------------- global hotkey

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int OverlayHotkeyId = 0xA17;
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModNoRepeat = 0x4000;
    private const uint VkO = 0x4F;
    private const int WmHotkey = 0x0312;
    private IntPtr _hotkeyHwnd = IntPtr.Zero;

    /// <summary>
    /// Registers Ctrl+Alt+O to toggle the overlay from anywhere, including from inside a game.
    ///
    /// A global hotkey rather than a WPF InputBinding, because the whole point of the overlay
    /// is that it is on screen while something else has focus -- a binding on this window
    /// would only fire when this window is focused, which is precisely when you do not need it.
    ///
    /// MOD_NOREPEAT so holding the keys toggles once rather than flickering at the keyboard's
    /// repeat rate. Failure is non-fatal and silent by design: another application may already
    /// own this combination, and losing a shortcut is not a reason to fail startup.
    /// </summary>
    private void RegisterOverlayHotkey(IntPtr hwnd)
    {
        _hotkeyHwnd = hwnd;
        RegisterHotKey(hwnd, OverlayHotkeyId, ModControl | ModAlt | ModNoRepeat, VkO);

        System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg != WmHotkey || w.ToInt32() != OverlayHotkeyId) return IntPtr.Zero;

            _settings.OverlayEnabled = !_settings.OverlayEnabled;
            _settings.Save();
            SetOverlayVisible(_settings.OverlayEnabled);
            handled = true;
            return IntPtr.Zero;
        });
    }

    private void OnOverlayReading(Reading r)
    {
        var overlay = _overlay;
        if (overlay is null) return;

        // Marshalled because the poll timer runs on a thread pool thread, and skipped entirely
        // while hidden so a switched-off overlay costs nothing per tick.
        overlay.Dispatcher.BeginInvoke(() =>
        {
            if (overlay.IsVisible) overlay.Update(r, _service);
        });
    }

    private DateTime _nextGpuCheckUtc = DateTime.MinValue;

    /// <summary>
    /// How often the GPU power unlock is checked and, if dropped, re-sent.
    ///
    /// Five seconds, not thirty. Measured: at a 30s interval the enforced ceiling oscillated
    /// 70 -> 75 -> 70, because the firmware reclaims Custom TGP far faster than that and the
    /// GPU spent most of its time back at the Off/On combination, which is exactly 70 W. The
    /// interval has to beat the reversion, not merely notice it eventually.
    ///
    /// The steady-state cost is one BIOS read per five seconds; a write only happens on a tick
    /// that finds the flag actually dropped.
    /// </summary>
    private static readonly TimeSpan GpuReassertInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Puts the GPU power unlock back when the firmware takes it away.
    ///
    /// Measured: with the app not touching it, Custom TGP was applied as On and read back Off
    /// ninety seconds later, with Ppab still On -- a combination no code path here writes, so
    /// the firmware reclaimed it on its own. The enforced ceiling drops 70 W -> 60 W when that
    /// happens, which is exactly the reported "maxed out, but it peaks around 70" and then
    /// falls back.
    ///
    /// This is the same failure as the fan loop's SetFanMode: a setting applied once at
    /// startup, silently reverted by firmware, and never re-asserted. Applying it once is not
    /// enough on this platform for anything the EC also has an opinion about.
    ///
    /// Reads before writing, so the steady state costs one BIOS read per 30s rather than a
    /// write -- and the read is what tells us it drifted at all.
    /// </summary>
    private int _gpuCheckInFlight;

    private void ReassertGpuPower()
    {
        if (!_settings.GpuMaxPower || DateTime.UtcNow < _nextGpuCheckUtc) return;
        _nextGpuCheckUtc = DateTime.UtcNow + GpuReassertInterval;

        // Off the poll thread, and never twice at once.
        //
        // This began life running inline on the poll callback, which holds the poll loop's
        // re-entrancy interlock for its whole duration -- so a BIOS read every five seconds,
        // plus a write whenever the flag had been dropped, sat directly on the critical path
        // of temperature polling. BIOS round trips are the most expensive thing this app does,
        // and putting two of them there is what made it feel sluggish.
        //
        // The interlock stops a slow round trip from stacking further checks behind it.
        if (Interlocked.Exchange(ref _gpuCheckInFlight, 1) == 1) return;

        Task.Run(() =>
        {
            try
            {
                if (_ctx.Gpu.GetPower().CustomTgp == GpuCustomTgp.On) return;

                _ctx.Gpu.ForceMaxPower = true;
                _ctx.Gpu.SetPower(new GpuPowerData(GpuCustomTgp.On, GpuPpab.On, GpuDState.D1, 0));
            }
            catch
            {
                // A failed BIOS round trip must not disturb anything. The next tick retries.
            }
            finally { Interlocked.Exchange(ref _gpuCheckInFlight, 0); }
        });
    }

    private void OnThrottleCheck(Reading r)
    {
        bool isThrottling = r.Throttling == ThrottlingState.On;
        if (isThrottling && !_wasThrottling)
        {
            _trayIcon?.ShowBalloonTip(5000, "OmniHub",
                $"Thermal throttling detected at {r.TemperatureC}°C.",
                WinForms.ToolTipIcon.Warning);
        }
        _wasThrottling = isThrottling;
    }

    private void ShowFlyout()
    {
        _flyout ??= new TrayFlyout(_ctx, _service, _settings, this);
        _flyout.ShowNearTray();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void RestoreFromTrayPublic() => RestoreFromTray();

    // Both the tray "Exit" item and a CloseBehavior=Exit choice from the X button route
    // through here: they set _allowClose and let the window actually close, rather than
    // duplicating a Shutdown() call at each call site. App.xaml uses
    // ShutdownMode="OnExplicitShutdown" (so the window closing alone never ends the
    // process, which is what lets "minimize to tray" work) -- so Shutdown() below is the
    // one place that actually terminates the process, exactly once, when intended.
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        if (_settings.CloseBehavior == CloseBehavior.Exit)
        {
            _allowClose = true;
            return;
        }
        e.Cancel = true;
        Hide();
    }

    private void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        try { _appWatchTimer?.Dispose(); } catch { }

        // Views first, and before the hardware context they depend on.
        //
        // These are not passive panels: TuningView owns auto eco, adaptive tuning and two
        // background watchers, and auto eco has changed the display refresh rate that only its
        // shutdown puts back. Disposing after _ctx would have them reaching into a disposed
        // BIOS connection on the way out.
        foreach (var view in _views.Values.OfType<IDisposable>())
        {
            try { view.Dispose(); } catch { }
        }

        try { _service.Stop(); } catch { }

        // The overlay closes BEFORE the context, not after.
        //
        // It reads package power on its own five-second timer, so disposing the hardware
        // underneath it left a window where an in-flight tick could touch a disposed SMU.
        // Closing it first makes the window invisible, which stops that timer, before there
        // is anything disposed for it to reach for.
        try { _overlay?.Close(); } catch { }
        try { _ctx.Dispose(); } catch { }
        try { _thermalLog?.Dispose(); } catch { }
        try { ThemeManager.ThemeChanged -= OnThemeChanged; } catch { }
        try { if (_hotkeyHwnd != IntPtr.Zero) UnregisterHotKey(_hotkeyHwnd, OverlayHotkeyId); } catch { }
        try { if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); } } catch { }
        if (_allowClose) System.Windows.Application.Current.Shutdown();
    }
}
