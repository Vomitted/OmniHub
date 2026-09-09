using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace OmniHub.Tests;

/// <summary>
/// One STA thread, one Application, and the application's real resource dictionaries.
///
/// Shared because WPF permits exactly one Application per AppDomain, so test classes cannot each
/// stand up their own. The Application is not ceremony: StaticResource inside a dictionary loaded
/// through Source resolves against Application.Current.Resources, which is how the palette, the
/// theme and the styles find each other at runtime, and why parsing them in isolation fails.
///
/// The thread is a background one with a live dispatcher, so it never holds the test host open.
/// </summary>
internal static class WpfTestHost
{
    private static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            _ = new Application();
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        return dispatcher!;
    });

    /// <summary>Runs a delegate on the shared STA thread and rethrows whatever it threw.</summary>
    public static void Run(Action action)
    {
        Exception? failure = null;
        Ui.Value.Invoke(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });

        if (failure is not null) throw failure;
    }

    public static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "OmniHub.App")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not locate the repository root from the test binary");
        return dir!;
    }

    public static string WpfDir => Path.Combine(RepoRoot().FullName, "OmniHub.App", "Wpf");

    /// <summary>
    /// Merges palette, theme and styles onto the Application, in App.xaml's order.
    ///
    /// One at a time, and live on the Application as it goes. That ordering is the mechanism
    /// rather than a detail: StaticResource is resolved while a document is parsed, and a
    /// dictionary loaded through Source is its own document, so Styles.xaml finds TextFaintBrush
    /// only because Theme.xaml is already reachable by the time it is read.
    /// </summary>
    public static ResourceDictionary LoadResources(string paletteFile = "OledBlack.xaml")
    {
        var paths = new[]
        {
            Path.Combine(WpfDir, "Palettes", paletteFile),
            Path.Combine(WpfDir, "Theme.xaml"),
            Path.Combine(WpfDir, "Styles.xaml"),
        };

        foreach (var path in paths)
            Assert.True(File.Exists(path), $"{path} is missing");

        var merged = new ResourceDictionary();
        Application.Current.Resources = merged;

        foreach (var path in paths)
            merged.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(path) });

        return merged;
    }
}
