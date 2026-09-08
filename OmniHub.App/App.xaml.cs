using System.Threading;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using OmniHub.App.Wpf;

namespace OmniHub.App;

public partial class App : Application
{
    // Held for the process lifetime -- a local variable would be eligible for GC
    // (and release the mutex) as soon as OnStartup returns. Guards every launch
    // path (GUI and all three CLI modes), since two instances -- whether two GUI
    // windows or a GUI plus a headless run -- would double up BIOS polling and can
    // actively fight each other for fan control via competing SetFanLevel calls.
    private Mutex? _singleInstanceMutex;

    /// <summary>
    /// Catches what would otherwise kill the process, and writes it down.
    ///
    /// Everything below OnStartup's own try was unprotected, so any unhandled exception --
    /// anywhere, at any time -- terminated OmniHub silently. Two real examples from this
    /// machine's event log in one week: a frozen TranslateTransform that threw the moment a
    /// card was hovered, and a WMI "Invalid query" from a background reader. Neither had any
    /// business ending the process, and neither left a trace the app could show.
    ///
    /// That matters more here than in most applications. While OmniHub is closed the fans are
    /// back on the stock BIOS curve, including the 0%-while-hot behaviour it exists to prevent,
    /// so a crash does not merely inconvenience: it silently removes the protection.
    ///
    /// Dispatcher exceptions are marked handled, because a UI hiccup should not take fan
    /// control down with it. That trades fail-fast for continuity, which is exactly why each
    /// one is logged rather than swallowed -- crash.log is what makes them findable after.
    /// </summary>
    private static void InstallCrashHandlers()
    {
        Current.DispatcherUnhandledException += (_, e) =>
        {
            Log("Dispatcher", e.Exception);
            e.Handled = true;
        };

        // Cannot be prevented, only recorded: the runtime is already tearing down.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("AppDomain", e.ExceptionObject as Exception);

        // A faulted Task nobody awaited. Harmless by default in .NET, but it is exactly where a
        // background hardware read goes to die unnoticed, so it is worth writing down.
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("Task", e.Exception);
            e.SetObserved();
        };
    }

    private static void Log(string source, Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniHub", "logs");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(dir, "crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{source}]  {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Logging a crash must never cause one.
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InstallCrashHandlers();

        // -Probe runs BEFORE the single-instance guard, deliberately.
        //
        // The guard exists because two instances fight for fan control: both poll the BIOS and
        // both issue SetFanLevel, so whichever wrote last wins and the curve becomes a race.
        // That reasoning covers -Calibrate and -RunHeadless, which command the fan. It does not
        // cover -Probe, which only reads -- fan count, type, level, table, temperature,
        // throttling state, GPU mode and power.
        //
        // Blocking it bought nothing and cost the one thing the probe is for. It is what you
        // run on an unverified laptop to see what the BIOS actually reports, and the natural
        // moment to run it is while the app is up. Instead it popped a dialog saying OmniHub
        // was already running -- advice, in place of the output that was asked for.
        if (e.Args.Length > 0 && e.Args[0].Equals("-Probe", StringComparison.OrdinalIgnoreCase))
        {
            Program.RunProbeCli();
            Shutdown();
            return;
        }

        _singleInstanceMutex = new Mutex(true, "Local\\OmniHub_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "OmniHub is already running -- check your system tray icon, or Task Manager's " +
                "Details tab (not just the Processes search) if you don't see it there.",
                "OmniHub already running", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Above the guard for the same reason as -Probe: it only reads. It loads the CPU, but it
        // never commands a fan or writes a power limit, so it cannot race the running instance
        // for control -- and running it WHILE the application is up is the intended use, since
        // the point is to measure the machine as it normally behaves.
        if (e.Args.Length > 0 && e.Args[0].Equals("-LoadTest", StringComparison.OrdinalIgnoreCase))
        {
            Program.RunLoadTestCli(e.Args);
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && e.Args[0].Equals("-Calibrate", StringComparison.OrdinalIgnoreCase))
        {
            Program.RunCalibrateCli();
            Shutdown();
            return;
        }

        if (e.Args.Length > 0 && e.Args[0].Equals("-RunHeadless", StringComparison.OrdinalIgnoreCase))
        {
            Program.RunHeadlessCli();
            Shutdown();
            return;
        }

        try
        {
            // Applied before the window is constructed so it opens already in the saved
            // theme, rather than painting the default palette and re-tinting a frame after.
            ThemeManager.Apply(AppSettings.Load().ThemeName);

            var window = new MainWindow();
            window.Show();
        }
        catch (Exception ex)
        {
            // Full ex.ToString() (not just ex.Message) deliberately -- a XAML load
            // failure's real cause is almost always in the InnerException, and the
            // outer message alone ("TypeConverterMarkupExtension threw an exception")
            // is too generic to act on.
            MessageBox.Show(ex.ToString(), "OmniHub failed to start", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }
}
