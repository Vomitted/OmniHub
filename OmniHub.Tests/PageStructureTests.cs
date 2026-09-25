// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// Every page is built the same way, and stays that way.
///
/// The interface had a hole in its type scale. The page title was 22px and the next thing down
/// was a 10.5px monospace capital in the faintest foreground the palette owns, so every section
/// heading in the application was set at the size and colour reserved for the least important
/// information on screen. The capitals were compensating: at 10px in a muted grey, nothing short
/// of shouting reads as a heading.
///
/// Restoring the missing level fixed the pages that existed on the day it was done. These tests
/// are for the pages that come after. A convention nobody asserts survives exactly until the next
/// person adds a section, and the failure is invisible to every other check this project runs:
/// the markup parses, the view builds, the suite passes, and the screen quietly goes back to
/// being a field of small grey capitals.
/// </summary>
public class PageStructureTests
{
    private static string ViewsDir => Path.Combine(WpfTestHost.WpfDir, "Views");

    public static IEnumerable<object[]> Views() =>
        Directory.EnumerateFiles(ViewsDir, "*.xaml").Select(p => new object[] { Path.GetFileName(p) });

    private static string Read(string file) => File.ReadAllText(Path.Combine(ViewsDir, file));

    /// <summary>Every self-closing TextBlock as its raw tag, so an assertion can see what it carries.</summary>
    private static IEnumerable<string> TextBlocks(string xaml) =>
        Regex.Matches(xaml, @"<TextBlock\b[^>]*?/>", RegexOptions.Singleline).Select(m => m.Value);

    private static string? TextOf(string tag) =>
        Regex.Match(tag, @"Text=""([^""]*)""") is { Success: true } m ? m.Groups[1].Value : null;

    [Theory]
    [MemberData(nameof(Views))]
    public void NoSectionHeadingUsesTheStyleReservedForCardTags(string file)
    {
        // SubHeadingText and SectionHeadingText are 10px monospace in a muted grey. They are right
        // for a column header inside a table and for the unit on a chip, and they were wrong for
        // the thing that tells somebody which part of the page they are looking at.
        //
        // Scoped by the heading's own text: a heading is prose, so it contains a space. That
        // deliberately leaves single-word tags alone, because a check that cries wolf teaches
        // everyone to ignore it, and this project has already written one that did.
        var offenders = TextBlocks(Read(file))
            .Where(t => t.Contains("SubHeadingText") || t.Contains("SectionHeadingText"))
            .Select(TextOf)
            .Where(t => t is not null && t.Contains(' '))
            .ToList();

        Assert.True(offenders.Count == 0,
            $"{file} labels a section with the 10px card-tag style: {string.Join(", ", offenders)}. "
            + "Use SectionTitle, which is the heading level that exists for this.");
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void SectionHeadingsAreSentenceCaseRatherThanShouted(string file)
    {
        // The capitals were standing in for size. Now that the size is there, they are only
        // shouting, and a page of shouting has no hierarchy in it either.
        //
        // An all-capitals word offends only when it is not an acronym, because MUX, GPU and HP are
        // how those things are written.
        string[] acronyms =
        {
            "HP", "GPU", "CPU", "MUX", "PM", "SMU", "AMD", "UI", "IO", "OS", "AC", "DC",
            "RPM", "TGP", "DWM", "MMCSS", "NVML", "WMI", "EC", "API", "JSON", "CSV", "P95",
        };

        var shouted = TextBlocks(Read(file))
            .Where(t => t.Contains("SectionTitle") || t.Contains("PageTitle"))
            .Select(TextOf)
            .Where(text => text is not null
                           && text.Split(' ', '/')
                                  .Where(w => w.Length > 1 && w.All(char.IsLetter))
                                  .Any(w => w.All(char.IsUpper) && !acronyms.Contains(w)))
            .ToList();

        Assert.True(shouted.Count == 0,
            $"{file} shouts a heading: {string.Join(", ", shouted)}. Sentence case, please.");
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void APageHasOneTitleAndOneSentenceSayingWhatItIsFor(string file)
    {
        // Two views legitimately have neither. DashboardView is the landing screen and opens on
        // the machine's live state rather than on a heading about it; GroupView is not a page at
        // all, but the container holding a sub-selector over several of them.
        if (file is "DashboardView.xaml" or "GroupView.xaml") return;

        string xaml = Read(file);
        int titles = Regex.Matches(xaml, @"StaticResource PageTitle\}").Count;
        int ledes = Regex.Matches(xaml, @"StaticResource PageLede\}").Count;

        Assert.True(titles == 1, $"{file} has {titles} page titles; a page has exactly one.");

        // The sentence is the part most likely to be skipped, and it is what makes these screens
        // read as documentation rather than as a wall of controls. Several of these pages drive
        // hardware whose behaviour nobody could guess from a two-word title.
        Assert.True(ledes == 1,
            $"{file} has {ledes} lede sentences; every page says in one line what it controls.");
    }

    [Theory]
    [MemberData(nameof(Views))]
    public void TheScaleIsSetByTheStyleRatherThanRedecidedPerView(string file)
    {
        // The other half of the same defect. Every call site was overriding FontSize inline, so
        // the type scale was not being set in Styles.xaml at all: it was re-chosen per view, and
        // inconsistently. A style whose size every caller overrides is not a style.
        var overridden = TextBlocks(Read(file))
            .Where(t => (t.Contains("PageTitle") || t.Contains("PageLede") || t.Contains("SectionTitle"))
                        && t.Contains("FontSize="))
            .Select(t => TextOf(t) ?? t)
            .ToList();

        Assert.True(overridden.Count == 0,
            $"{file} overrides the page scale inline: {string.Join(", ", overridden)}. "
            + "If a size genuinely must differ, base a style on the scale so the exception has a name.");
    }

    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static bool Pins(string? alignment) => alignment is { Length: > 0 } a && a != "Stretch";

    /// <summary>Keys of styles that pin horizontal alignment, and every style that caps width without doing so.</summary>
    private static (HashSet<string> Pinning, List<string> Unpinned) StyleAlignment()
    {
        var pinning = new HashSet<string>(StringComparer.Ordinal);
        var unpinned = new List<string>();

        foreach (var style in XDocument.Load(Path.Combine(WpfTestHost.WpfDir, "Styles.xaml"))
                                       .Descendants().Where(e => e.Name.LocalName == "Style"))
        {
            string? Setter(string property) => style.Elements()
                .FirstOrDefault(s => s.Name.LocalName == "Setter" && (string?)s.Attribute("Property") == property)
                ?.Attribute("Value")?.Value;

            string key = (string?)style.Attribute(X + "Key") ?? $"(implicit {(string?)style.Attribute("TargetType")})";
            if (Pins(Setter("HorizontalAlignment"))) pinning.Add(key);
            else if (Setter("MaxWidth") is not null) unpinned.Add(key);
        }

        return (pinning, unpinned);
    }

    /// <summary>
    /// A width cap always says which side it keeps to.
    ///
    /// WPF centres an element whose MaxWidth is narrower than its slot when its alignment is Stretch,
    /// which is the default. Every explanatory paragraph in the application is capped at a readable
    /// measure, and every one of them that did not also say Left was drawn centred in its card, 150 to
    /// 200 pixels to the right of the label above it -- on every page, in every interface. It is
    /// invisible in the markup, which is why it spread to sixty-eight places before anyone could see
    /// the screen: the fix already existed in one of them.
    /// </summary>
    [Fact]
    public void EveryWidthCapSaysWhichSideItKeepsTo()
    {
        var (pinning, unpinnedStyles) = StyleAlignment();
        var offenders = new List<string>(unpinnedStyles.Select(k => $"Styles.xaml: style {k} caps width without pinning alignment"));
        int caps = 0;

        foreach (var file in Directory.EnumerateFiles(WpfTestHost.WpfDir, "*.xaml", SearchOption.AllDirectories))
        {
            foreach (var el in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants())
            {
                if (el.Attribute("MaxWidth") is null) continue;
                caps++;

                if (Pins((string?)el.Attribute("HorizontalAlignment"))) continue;

                var styleRef = Regex.Match((string?)el.Attribute("Style") ?? "", @"Resource\s+(\w+)\}");
                if (styleRef.Success && pinning.Contains(styleRef.Groups[1].Value)) continue;

                offenders.Add($"{Path.GetFileName(file)}:{((IXmlLineInfo)el).LineNumber} <{el.Name.LocalName}>");
            }
        }

        // Code-built elements. An initializer that sets MaxWidth must set the alignment in the same
        // braces; a statement that assigns it must assign the alignment to the same target.
        foreach (var file in Directory.EnumerateFiles(WpfTestHost.WpfDir, "*.cs", SearchOption.AllDirectories))
        {
            string code = File.ReadAllText(file);
            // Any value, not only a literal: a cap written as a named constant centres just the same.
            foreach (Match m in Regex.Matches(code, @"(?<target>[\w.]+\.)?MaxWidth\s*=\s*[\w.]"))
            {
                // A grid column or row has no alignment of its own; capping one is the fix, not the fault.
                if (!m.Groups["target"].Success && Regex.IsMatch(code[..m.Index], @"new\s+(Column|Row)Definition\s*\{[^{}]*$"))
                    continue;

                caps++;
                string scope = m.Groups["target"].Success
                    ? code
                    : Enclosing(code, m.Index);
                string needle = m.Groups["target"].Success ? m.Groups["target"].Value + "HorizontalAlignment" : "HorizontalAlignment";

                if (!scope.Contains(needle))
                    offenders.Add($"{Path.GetFileName(file)}:{code[..m.Index].Count(c => c == '\n') + 1} (code)");
            }
        }

        Assert.True(caps > 50, $"only {caps} width caps found; this test is no longer looking at the real markup");
        Assert.True(offenders.Count == 0,
            $"{offenders.Count} width caps with no alignment, each drawn centred in a wide slot:{Environment.NewLine}"
            + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>The object initializer containing <paramref name="at"/>: from its unmatched '{' to the matching '}'.</summary>
    private static string Enclosing(string code, int at)
    {
        int depth = 0, start = at;
        for (; start > 0; start--)
        {
            if (code[start] == '}') depth++;
            else if (code[start] == '{' && depth-- == 0) break;
        }

        depth = 0;
        int end = start;
        for (; end < code.Length; end++)
        {
            if (code[end] == '{') depth++;
            else if (code[end] == '}' && --depth == 0) break;
        }

        return code[start..Math.Min(end + 1, code.Length)];
    }
}
