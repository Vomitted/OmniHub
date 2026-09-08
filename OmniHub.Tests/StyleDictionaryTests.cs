using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;

namespace OmniHub.Tests;

/// <summary>
/// Builds the application's style layer for real, rather than reading it as text.
///
/// A resource dictionary is not checked by the compiler. A Setter naming a property the target
/// type does not have, a trigger on a property that does not exist, a template that cannot be
/// constructed, a BasedOn pointing at a key that is not there -- every one of those throws when
/// the dictionary is parsed, and the dictionaries are parsed at application startup. Until now
/// the only way to find out was to launch the application, and with views built lazily some of
/// it would not surface until a particular tab was opened.
///
/// This parses the same three layers App.xaml merges, in the same order, on a thread of its own.
/// It is what makes changes to the shared styles safe to make without a machine to look at.
/// </summary>
public class StyleDictionaryTests
{
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "OmniHub.App")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not locate the repository root from the test binary");
        return dir!;
    }

    private static string WpfDir => Path.Combine(RepoRoot().FullName, "OmniHub.App", "Wpf");

    /// <summary>
    /// Runs a delegate on a fresh STA thread and rethrows whatever it threw.
    ///
    /// WPF objects require a single-threaded apartment and the test host's threads are not one.
    /// Owning the thread here, rather than marking the whole assembly STA, leaves the rest of the
    /// suite exactly as it was.
    /// </summary>
    /// <summary>
    /// One STA thread with a live dispatcher and a single Application, shared by every test here.
    ///
    /// WPF allows exactly one Application per AppDomain, so a thread per test cannot each make
    /// their own. The Application is not decoration: StaticResource inside a dictionary loaded
    /// through Source resolves against Application.Current.Resources, which is precisely how the
    /// three layers find each other at runtime and why parsing them in isolation cannot work.
    ///
    /// A background thread, so it never holds the test host open.
    /// </summary>
    private static readonly Lazy<Dispatcher> UiThread = new(() =>
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

    private static void OnStaThread(Action action)
    {
        Exception? failure = null;
        UiThread.Value.Invoke(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });

        if (failure is not null) throw failure;
    }

    /// <summary>
    /// Merges the three layers the way App.xaml does: palette, then Theme, then Styles.
    ///
    /// Order matters and is the point of the test. Theme's brushes are built from the palette's
    /// colours, and Styles refers to Theme's brushes, fonts and radii by StaticResource, which
    /// resolves at parse time and would fail on its own.
    /// </summary>
    private static ResourceDictionary BuildMerged(string paletteFile)
    {
        var paths = new[]
        {
            Path.Combine(WpfDir, "Palettes", paletteFile),
            Path.Combine(WpfDir, "Theme.xaml"),
            Path.Combine(WpfDir, "Styles.xaml"),
        };

        foreach (var path in paths)
            Assert.True(File.Exists(path), $"{path} is missing");

        // Merged one at a time, with the result live on the Application as it goes.
        //
        // This ordering is the whole mechanism, not a detail. StaticResource is resolved while a
        // document is parsed, and a dictionary loaded through Source is its own document -- so
        // Styles.xaml asking for TextFaintBrush finds it only because Theme.xaml is already in
        // Application.Current.Resources by the time Styles.xaml is read. Parsing the three in
        // isolation and merging afterwards fails on exactly that lookup, which is what the app
        // itself would do without an Application to fall back to.
        var merged = new ResourceDictionary();
        Application.Current.Resources = merged;

        foreach (var path in paths)
            merged.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(path) });

        return merged;
    }

    public static IEnumerable<object[]> Palettes() =>
        Directory.EnumerateFiles(Path.Combine(WpfDir, "Palettes"), "*.xaml")
                 .Select(p => new object[] { Path.GetFileName(p) });

    /// <summary>
    /// Every palette has to build the whole style layer, not only the one currently in use.
    ///
    /// ThemeManager swaps the palette at runtime, so a colour present in OledBlack and missing
    /// from Ember is a crash waiting for whoever changes theme. There are four, and they are
    /// edited by hand.
    /// </summary>
    [Theory]
    [MemberData(nameof(Palettes))]
    public void EveryPaletteBuildsTheWholeStyleLayer(string paletteFile)
    {
        OnStaThread(() =>
        {
            var merged = BuildMerged(paletteFile);

            // Indexing forces the deferred content to be realised. A dictionary that parsed can
            // still fail here if a Setter or a trigger inside one of these is wrong.
            foreach (string key in new[]
            {
                "CardBorderStyle", "CardSheenStyle", "SubHeadingText", "BodyText", "MutedText",
                "TileLabel", "TileValue", "TileUnit", "TileFoot",
                "PillRadioStyle", "FlatButtonStyle", "PrimaryButtonStyle", "ToggleSwitchStyle",
                "OmniCheckBoxStyle", "OmniSliderStyle", "OmniComboBoxStyle", "OmniDataGridStyle",
                "StatusChipStyle", "NumericTextBoxStyle",
            })
            {
                Assert.True(merged[key] is Style, $"{key} is missing or is not a Style in {paletteFile}");
            }

            // The geometry tokens the card style now points at.
            Assert.IsType<CornerRadius>(merged["RadiusMd"]);
            Assert.IsType<Thickness>(merged["CardPadding"]);
        });
    }

    /// <summary>
    /// The card's padding default has to stay the value its callers stopped writing out.
    ///
    /// Thirty-nine borders had their Padding="16" deleted on the strength of this default. If it
    /// drifts back to something else, all thirty-nine change silently and at once -- which is
    /// exactly what happened in the other direction when the default sat at 18 and every caller
    /// quietly overrode it.
    /// </summary>
    [Fact]
    public void TheCardPaddingDefaultIsWhatTheCallersRelyOn()
    {
        OnStaThread(() =>
        {
            var merged = BuildMerged("OledBlack.xaml");
            var card = (Style)merged["CardBorderStyle"];

            var padding = card.Setters.OfType<Setter>()
                .FirstOrDefault(s => s.Property.Name == "Padding");

            Assert.True(padding is not null, "CardBorderStyle no longer sets Padding");
            Assert.Equal(new Thickness(16), padding!.Value);
        });
    }
}
