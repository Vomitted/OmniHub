// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.IO;
using OmniHub.Core.Optimize;

namespace OmniHub.Tests;

/// <summary>
/// The three Windows processor settings this application must never write.
///
/// This is not a style rule. The owner of the machine this was found on had deliberately set
/// boost mode to Disabled on both rails and a 99% maximum processor state on battery, because
/// the laptop runs hot, and asked for those to be left alone. OmniHub then created two power
/// schemes by duplicating Balanced, wrote PERFBOOSTMODE = 2 (Aggressive) and a 60% battery
/// ceiling into them, and activated one -- so the machine ran for days on settings its owner
/// had explicitly rejected, while Balanced still showed the values they chose.
///
/// The irony was written into the source at the time: PowerPlanSetup's own summary argues that
/// "someone who set boost off because their laptop runs hot does not expect a fan utility to
/// turn it back on". The design was right and the implementation contradicted it, which is
/// exactly the kind of defect that survives code review and only a test catches.
///
/// Nothing needs to replace those writes. A duplicated scheme inherits every value of the
/// scheme it was copied from, so the plans now carry whatever their owner set.
/// </summary>
public class PowerPlanSetupTests
{
    private const string ProcThrottleMin = "893dee8e-2bef-41e0-89c6-b55d0929964c";
    private const string ProcThrottleMax = "bc5038f7-23e0-4960-96da-33abaf5935ec";
    private const string PerfBoostMode   = "be337238-0d82-4146-a960-4f3749d470c7";

    /// <summary>
    /// The backstop list is the three settings and nothing else. If someone adds a fourth
    /// forbidden setting they have to come here, which is the point.
    /// </summary>
    [Fact]
    public void TheNeverWriteListIsExactlyTheThreeProcessorSettings()
    {
        var actual = PowerPlanSetup.NeverWrite.Select(g => g.ToString()).OrderBy(x => x).ToArray();
        var expected = new[] { ProcThrottleMin, ProcThrottleMax, PerfBoostMode }.OrderBy(x => x).ToArray();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// The source-level guard, and the one that actually keeps this fixed.
    ///
    /// Scans every file under Optimize/ for the three GUIDs. The only place any of them may
    /// appear is PowerPlanSetup's NeverWrite table, which exists to refuse them. A GUID
    /// anywhere else in that directory is a write path -- that is what the whole namespace
    /// does -- so its presence is the defect, whatever the surrounding code claims to do.
    ///
    /// Written as a text scan rather than a behavioural test on purpose: the behavioural
    /// version would have to call powrprof and change this machine's power configuration to
    /// prove it did not change this machine's power configuration.
    /// </summary>
    [Fact]
    public void NoFileUnderOptimizeMentionsAForbiddenSettingOutsideTheRefusalList()
    {
        string optimize = Path.Combine(WpfTestHost.RepoRoot().FullName, "OmniHub.Core", "Optimize");
        Assert.True(Directory.Exists(optimize), $"{optimize} is missing");

        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(optimize, "*.cs", SearchOption.AllDirectories))
        {
            string[] lines = File.ReadAllLines(file);
            bool isSetup = Path.GetFileName(file) == "PowerPlanSetup.cs";

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (!line.Contains(ProcThrottleMin, StringComparison.OrdinalIgnoreCase)
                    && !line.Contains(ProcThrottleMax, StringComparison.OrdinalIgnoreCase)
                    && !line.Contains(PerfBoostMode, StringComparison.OrdinalIgnoreCase))
                    continue;

                // The refusal list itself. Recognised by the comment that names each entry,
                // so a declaration that merely sits in the same file does not get a pass.
                if (isSetup && (line.Contains("// PROCTHROTTLE") || line.Contains("// PERFBOOSTMODE")))
                    continue;

                offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A Windows processor power setting the user asked never to be written appears in a "
            + "write path:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The plan builder takes a free-text name and then looks it up, so typing the name of one
    /// of Windows' own schemes used to resolve to that scheme and write into it. Stock schemes
    /// are recognised so the builder can refuse instead.
    /// </summary>
    [Theory]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e")]  // Balanced
    [InlineData("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c")]  // High performance
    [InlineData("a1841308-3541-4fab-bc81-f71556f20b4a")]  // Power saver
    [InlineData("3de59f83-06ae-4fc3-9084-51083167dc96")]  // Gaming
    public void WindowsOwnSchemesAreRecognisedAsStock(string guid) =>
        Assert.True(PowerPlanSetup.IsStock(new Guid(guid)));

    /// <summary>A scheme OmniHub made is not stock, or it could never write to its own plans.</summary>
    [Fact]
    public void AnOmniHubCreatedSchemeIsNotStock() =>
        Assert.False(PowerPlanSetup.IsStock(Guid.NewGuid()));

    /// <summary>
    /// Read-only, against this machine, in the same spirit as HardwareReadTests: whatever
    /// Windows reports it supports, the builder must not be offering a processor knob.
    /// </summary>
    [Fact]
    public void TheBuilderOffersNoProcessorKnob()
    {
        var offered = PowerKnobReader.Read();

        Assert.DoesNotContain(offered, k => k.Key is "procmin" or "procmax" or "boost");
        Assert.DoesNotContain(offered, k => Array.IndexOf(PowerPlanSetup.NeverWrite, k.Id) >= 0);
    }
}
