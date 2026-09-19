// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
}
