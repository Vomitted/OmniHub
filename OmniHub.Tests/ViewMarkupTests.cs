using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Markup;

namespace OmniHub.Tests;

/// <summary>
/// Parses every view's markup for real.
///
/// This is the part of the application no test could see. A view is built when its tab is first
/// opened, so a malformed template, a style applied to the wrong target type, a resource key that
/// does not exist, or a control the parser cannot resolve all surface as an exception at that
/// moment -- not at build, and not at startup. In a process that holds fan control, a throw out
/// of a click handler is not a cosmetic problem.
///
/// The markup is parsed rather than the view constructed: a real view wants a HardwareContext,
/// which wants a laptop. Everything that can go wrong in the XAML itself is still checked.
/// </summary>
public class ViewMarkupTests
{
    /// <summary>
    /// The markup this can parse: views and windows that do not declare a type from the App
    /// assembly. The dictionaries have their own tests.
    ///
    /// The exclusion is a deliberate limit rather than an oversight. Parsing markup that says
    /// clr-namespace:OmniHub.App.Wpf.Controls needs that assembly loaded, which means referencing
    /// the App project, and the App project's build output IS the installed application: the
    /// shortcuts and the OmniHub_AutoStart task all point at bin\Release\net8.0-windows. Building
    /// it fails whenever OmniHub is running, because the process holds its own executable open,
    /// and running the test suite must not require shutting down someone's fan control. Those
    /// files are still covered as text by XamlResourceTests.
    /// </summary>
    public static IEnumerable<object[]> Markup() =>
        Directory.EnumerateFiles(WpfTestHost.WpfDir, "*.xaml", SearchOption.AllDirectories)
                 .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}Palettes{Path.DirectorySeparatorChar}")
                          && Path.GetFileName(p) is not ("Theme.xaml" or "Styles.xaml")
                          && !File.ReadAllText(p).Contains("clr-namespace:OmniHub"))
                 .Select(p => new object[] { Path.GetRelativePath(WpfTestHost.WpfDir, p) });

    // x:Class ties the markup to a code-behind type the parser would then demand.
    private static readonly Regex ClassAttribute = new(@"\s+x:Class="".*?""", RegexOptions.Compiled);

    // An application-relative resource URI, as in Icon="/app.ico". Resolving one means loading it
    // out of the App assembly, which is the reference this suite deliberately does not take. The
    // attribute is dropped so the rest of the file -- its styles, templates, triggers and
    // bindings, which is what actually breaks -- can still be parsed.
    private static readonly Regex AppResourceUri =
        new(@"\s+[A-Za-z]\w*=""/[^""]+\.(?:ico|png|jpg|jpeg|gif)""", RegexOptions.Compiled);

    /// <summary>
    /// Removes the event wiring, which only a compiled code-behind can satisfy.
    ///
    /// Handler names come from the view's own .xaml.cs rather than from a list of event names.
    /// Guessing gets it wrong in both directions: IsChecked is a property ending in "Checked",
    /// and MouseDoubleClick is an event matching no obvious suffix. Matching the actual method
    /// names is exact, and anything it fails to strip shows up as a parse failure rather than as
    /// a silently skipped check.
    /// </summary>
    private static string StripHandlers(string xaml, string codeBehindPath)
    {
        if (!File.Exists(codeBehindPath)) return xaml;

        var methods = Regex.Matches(
                File.ReadAllText(codeBehindPath),
                @"\b(?:private|public|protected|internal)\s+(?:static\s+)?(?:async\s+)?(?:void|Task)\s+(\w+)\s*\(")
            .Select(m => m.Groups[1].Value)
            .Distinct();

        foreach (var method in methods)
            xaml = Regex.Replace(xaml, $@"\s+[A-Za-z]\w*=""{Regex.Escape(method)}""", "");

        return xaml;
    }

    [Theory]
    [MemberData(nameof(Markup))]
    public void EveryViewParses(string relativePath)
    {
        string path = Path.Combine(WpfTestHost.WpfDir, relativePath);
        string xaml = ClassAttribute.Replace(File.ReadAllText(path), "");
        xaml = AppResourceUri.Replace(xaml, "");
        xaml = StripHandlers(xaml, path + ".cs");

        WpfTestHost.Run(() =>
        {
            // The same three layers the application merges, so StaticResource resolves the way it
            // does at runtime.
            WpfTestHost.LoadResources();

            var ex = Record.Exception(() => XamlReader.Parse(xaml));
            Assert.True(ex is null, $"{relativePath} does not parse: {ex?.Message}");
        });
    }
}
