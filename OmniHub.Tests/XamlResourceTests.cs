using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OmniHub.Tests;

/// <summary>
/// Every StaticResource a view asks for must exist.
///
/// This is the one class of UI failure that is both easy to introduce and invisible until the
/// screen is opened: a missing key throws at parse time, and because views are built lazily now,
/// that throw arrives when someone clicks a tab rather than when the application starts. The
/// compiler does not check resource keys, the suite could not see XAML at all, and the only way
/// to find out was to run the application and look.
///
/// It matters more since the metric-tile styles were consolidated: deleting a local style whose
/// key is not in the shared dictionary is exactly this bug, and this is what catches it.
/// Deliberately parsed as XML rather than loaded through WPF, so it needs no UI thread, no
/// display and no hardware.
/// </summary>
public class XamlResourceTests
{
    /// <summary>
    /// Walks up from the test binary to the repository root. Tests run out of bin, and the XAML
    /// they check is source rather than an artefact, so there is nothing beside the assembly to
    /// point at.
    /// </summary>
    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "OmniHub.App")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not locate the repository root from the test binary");
        return dir!;
    }

    private static string WpfDir => Path.Combine(RepoRoot().FullName, "OmniHub.App", "Wpf");

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(WpfDir, "*.xaml", SearchOption.AllDirectories);

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Every x:Key defined anywhere in the application's XAML.</summary>
    private static HashSet<string> DefinedKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in XamlFiles())
            foreach (var element in XDocument.Load(file).Descendants())
                if (element.Attribute(X + "Key")?.Value is { Length: > 0 } key)
                    keys.Add(key);
        return keys;
    }

    // Matches {StaticResource Foo} and {DynamicResource Foo}, including inside a larger markup
    // extension such as {Binding ..., Converter={StaticResource Foo}}.
    private static readonly Regex Reference =
        new(@"\{(?:Static|Dynamic)Resource\s+([A-Za-z0-9_.]+)\s*\}", RegexOptions.Compiled);

    [Fact]
    public void EveryResourceReferenceResolves()
    {
        var defined = DefinedKeys();
        var missing = new List<string>();
        int checkedRefs = 0;

        foreach (var file in XamlFiles())
        {
            string text = File.ReadAllText(file);
            foreach (Match m in Reference.Matches(text))
            {
                checkedRefs++;
                string key = m.Groups[1].Value;
                if (!defined.Contains(key))
                    missing.Add($"{Path.GetFileName(file)} references {{StaticResource {key}}}, which is defined nowhere");
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing));

        // A test that matched nothing would pass just as quietly as one that matched everything,
        // and a regex is exactly the kind of thing that stops matching after a refactor renames
        // or reformats what it was looking for. Measured at 733 references across 22 files; the
        // floor is deliberately well below that so ordinary edits do not trip it, while a match
        // count that collapses does.
        Assert.True(checkedRefs > 400,
            $"only {checkedRefs} resource references were found, so this test is no longer looking at the real markup");
    }

    [Fact]
    public void TheSharedTileStylesExistForTheViewsThatStoppedDefiningTheirOwn()
    {
        // PowerView and FansView deleted byte-identical local copies of these and now resolve
        // them from Styles.xaml. If that dictionary ever loses one, both views throw on open.
        var defined = DefinedKeys();

        foreach (string key in new[] { "TileLabel", "TileValue", "TileUnit", "TileFoot" })
            Assert.True(defined.Contains(key), $"{key} is referenced by views that no longer define it");
    }

    [Fact]
    public void EveryXamlFileIsWellFormed()
    {
        // XML comments cannot contain a double hyphen, which is easy to write out of habit as an
        // em-dash and produces a build error rather than anything readable. Parsing each file
        // here names the file instead.
        foreach (var file in XamlFiles())
        {
            var ex = Record.Exception(() => XDocument.Load(file));
            Assert.True(ex is null, $"{Path.GetFileName(file)} is not well-formed XML: {ex?.Message}");
        }
    }
}
