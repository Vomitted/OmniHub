// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OmniHub.Tests;

/// <summary>
/// Nothing moves while nobody can see it.
///
/// WPF services every active animation clock about sixty times a second, and it does so whether
/// or not the window is on screen. This application spends most of its life hidden in the tray,
/// and four Forever animations -- the activity ribbon, the dashboard's LIVE dot, the sidebar's
/// selected rail and the fan chart's live-point halo -- ran from launch for the whole session.
/// The halo was worse than the rest: each redraw started a fresh pair on a fresh transform, and
/// the old pair never stopped, so every visit to the Fans page left two more clocks behind.
///
/// Measured on the running application before this change: 2.23% of a core, sustained, with the
/// window hidden. The rule these tests hold is the one every fix applied: a Forever animation is
/// started by the element becoming visible and stopped by it becoming hidden.
/// </summary>
public class AmbientMotionTests
{
    private static IEnumerable<string> Files(string pattern) =>
        Directory.EnumerateFiles(WpfTestHost.WpfDir, pattern, SearchOption.AllDirectories);

    [Fact]
    public void EveryEndlessStoryboardIsConditionedOnVisibility()
    {
        int found = 0;
        var offenders = new List<string>();

        foreach (var file in Files("*.xaml"))
        {
            foreach (var anim in XDocument.Load(file).Descendants()
                                          .Where(e => (string?)e.Attribute("RepeatBehavior") == "Forever"))
            {
                found++;

                // The trigger that starts it has to include IsVisible=True among its conditions.
                var trigger = anim.Ancestors().FirstOrDefault(a => a.Name.LocalName is "MultiTrigger" or "Trigger" or "EventTrigger");
                bool gated = trigger is not null && trigger.Descendants()
                    .Any(c => c.Name.LocalName == "Condition"
                              && (string?)c.Attribute("Property") == "IsVisible"
                              && (string?)c.Attribute("Value") == "True");

                if (!gated) offenders.Add($"{Path.GetFileName(file)}: {(string?)anim.Attribute("Storyboard.TargetName") ?? anim.Name.LocalName}");
            }
        }

        Assert.True(found > 0, "no endless storyboard found at all; this test is no longer looking at the real markup");
        Assert.True(offenders.Count == 0,
            "these run forever without a visibility condition, so they tick while the window is hidden: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryEndlessAnimationInCodeStartsFromAVisibilityChange()
    {
        int found = 0;
        var offenders = new List<string>();
        var method = new Regex(@"(?:private|public|internal|protected)[^\n;=]*?\s(?<name>\w+)\s*\([^)]*\)\s*(?:=>|\{|\r?\n)", RegexOptions.Compiled);

        foreach (var file in Files("*.cs"))
        {
            string code = File.ReadAllText(file);

            foreach (Match forever in Regex.Matches(code, @"RepeatBehavior\s*=\s*RepeatBehavior\.Forever"))
            {
                found++;

                // The member it sits in: the last declaration before it.
                var owner = method.Matches(code[..forever.Index]).LastOrDefault();
                if (owner is null) { offenders.Add($"{Path.GetFileName(file)}: no enclosing member"); continue; }

                string body = code[owner.Index..forever.Index];
                string name = owner.Groups["name"].Value;

                // Started inline by a visibility change, or in a member only a visibility change calls.
                bool inline = body.Contains("IsVisibleChanged");
                bool called = Regex.IsMatch(code, @"IsVisibleChanged\s*\+=[^;]*\b" + Regex.Escape(name) + @"\s*\(");

                if (!inline && !called)
                    offenders.Add($"{Path.GetFileName(file)}:{code[..forever.Index].Count(c => c == '\n') + 1} in {name}");
            }
        }

        Assert.True(found > 0, "no endless animation found in code; this test is no longer looking at the real source");
        Assert.True(offenders.Count == 0,
            "these start a Forever animation that nothing stops when the window is hidden: "
            + string.Join(", ", offenders));
    }
}
