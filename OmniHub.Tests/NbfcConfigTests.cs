// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.IO;
using OmniHub.Core.Hardware;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// Reading somebody else's measurement of a laptop nobody here owns.
///
/// The fixture below is a real NoteBook FanControl configuration, not one written to make these
/// tests pass -- "HP Pavilion 17-ab240nd" by Erriez, from the nbfc-linux corpus, GPL-3.0 as this
/// project now is. Using the real thing corrected two assumptions that would otherwise have
/// shipped: registers are decimal rather than hex, and the speed bounds are not guaranteed to be
/// in ascending order.
/// </summary>
public class NbfcConfigTests
{
    /// <summary>
    /// HP Pavilion 17-ab240nd, from nbfc-linux. Author: Erriez. GPL-3.0.
    ///
    /// Worth reading closely: the fan register is 88, which is 0x58 -- the same register whose
    /// published documentation warns that a value above 90 makes the machine power itself off.
    /// The configuration's own ceiling is 70, comfortably below that. The person who measured it
    /// left the margin, which is the behaviour this importer must not undo by widening anything.
    /// </summary>
    private const string Pavilion = """
    {
     "LegacyTemperatureThresholdsBehaviour": true,
     "NotebookModel": "HP Pavilion 17-ab240nd",
     "Author": "Erriez",
     "EcPollInterval": 1000,
     "ReadWriteWords": false,
     "CriticalTemperature": 68,
     "FanConfigurations": [
      {
       "ReadRegister": 88,
       "WriteRegister": 88,
       "MinSpeedValue": 38,
       "MaxSpeedValue": 70,
       "IndependentReadMinMaxValues": true,
       "MinSpeedValueRead": 20,
       "MaxSpeedValueRead": 70,
       "ResetRequired": false,
       "FanSpeedResetValue": 0,
       "FanDisplayName": "CPU Fan",
       "TemperatureThresholds": [
        { "UpThreshold": 45, "DownThreshold": 38, "FanSpeed": 25.0 },
        { "UpThreshold": 65, "DownThreshold": 50, "FanSpeed": 100.0 }
       ]
      }
     ]
    }
    """;

    private static NbfcConfig Parsed()
    {
        var config = NbfcConfig.Parse(Pavilion, out string? problem);
        Assert.Null(problem);
        Assert.NotNull(config);
        return config!;
    }

    [Fact]
    public void ARealConfigurationParsesIntoTheFieldsItActuallyHas()
    {
        var config = Parsed();

        Assert.Equal("HP Pavilion 17-ab240nd", config.NotebookModel);
        Assert.Equal("Erriez", config.Author);
        Assert.Equal(68, config.CriticalTemperature);
        Assert.False(config.ReadWriteWords);

        // The refresh interval is not a preference. On this board the firmware takes fan control
        // back if the value is not rewritten, so a loop slower than this does not control the fan.
        Assert.Equal(1000, config.EcPollInterval);

        var fan = Assert.Single(config.FanConfigurations);
        Assert.Equal("CPU Fan", fan.FanDisplayName);
        Assert.Equal(88, fan.WriteRegister);
    }

    [Fact]
    public void TheAcceptedRangeIsExactlyWhatTheAuthorMeasuredAndNotAByteWider()
    {
        // The margin in this file is the whole reason it is safe. Register 88 is 0x58, whose
        // published hazard is that a value above 90 powers the machine off; the author's ceiling
        // is 70. Widening that for tidiness would spend somebody else's safety margin.
        var map = Parsed().ToRegisterMap("PAVILION-17");

        var register = map.At(88);
        Assert.NotNull(register);
        Assert.True(register!.Writable);
        Assert.Equal(38, register.Min);
        Assert.Equal(70, register.Max);

        map.CheckWrite(88, 70);
        Assert.Throws<EcWriteRefusedException>(() => map.CheckWrite(88, 71));
        Assert.Throws<EcWriteRefusedException>(() => map.CheckWrite(88, 90));
    }

    [Fact]
    public void AnInvertedControllerKeepsItsRangeInsteadOfLosingIt()
    {
        // NBFC allows the slowest value to be the larger number, because some controllers run
        // that way. Read as an ordered pair this produces an empty range, and an empty range means
        // either nothing can be written or -- much worse -- something silently swaps the bounds.
        var inverted = NbfcConfig.Parse("""
        {"NotebookModel":"Inverted","FanConfigurations":[
         {"ReadRegister":16,"WriteRegister":16,"MinSpeedValue":100,"MaxSpeedValue":20,
          "FanDisplayName":"Fan"}]}
        """, out _)!;

        var register = inverted.ToRegisterMap("INV").At(16)!;

        Assert.Equal(20, register.Min);
        Assert.Equal(100, register.Max);
        Assert.True(register.Accepts(60));

        // And the direction is reported rather than quietly normalised away, because a person
        // reading a fan percentage on that board needs to know which way it runs.
        Assert.Contains(inverted.Cautions(), c => c.Contains("inverted"));
    }

    [Fact]
    public void ARegisterOutsideAByteIsDroppedRatherThanTruncated()
    {
        // The schema stores plain integers and nothing stops a malformed file carrying 300, which
        // as a byte becomes 44 -- a real register on many boards, doing something entirely
        // unrelated. Silently writing there is the worst outcome available.
        var bad = NbfcConfig.Parse("""
        {"NotebookModel":"Bad","FanConfigurations":[
         {"ReadRegister":300,"WriteRegister":300,"MinSpeedValue":0,"MaxSpeedValue":100,
          "FanDisplayName":"Fan"}]}
        """, out _)!;

        var map = bad.ToRegisterMap("BAD");

        Assert.Null(map.At(44));
        Assert.Empty(map.Registers);
        Assert.Contains(bad.Cautions(), c => c.Contains("outside a byte"));
    }

    [Fact]
    public void AConfigurationDescribingNoFansIsRefusedWithAReason()
    {
        var config = NbfcConfig.Parse("""{"NotebookModel":"Empty","FanConfigurations":[]}""", out string? problem);

        Assert.Null(config);
        Assert.NotNull(problem);
        Assert.Contains("Empty", problem);
    }

    [Fact]
    public void RubbishIsReportedRatherThanThrown()
    {
        // These files come from strangers, for machines nobody here has. That is exactly the input
        // that must not be able to take the application down.
        Assert.Null(NbfcConfig.Parse("not json at all", out string? problem));
        Assert.NotNull(problem);

        Assert.Null(NbfcConfig.Load(Path.Combine(Path.GetTempPath(), "nbfc-does-not-exist.json"), out string? missing));
        Assert.NotNull(missing);
    }

    [Fact]
    public void AResetValueIsCalledOutBecauseStoppingWithoutItLeavesTheFanWhereItWas()
    {
        // Directly the latching hazard, arriving as a field in a config file: a board that needs
        // a specific value written to hand control back will keep whatever it was last told if
        // nothing writes it.
        var resets = NbfcConfig.Parse("""
        {"NotebookModel":"Resetter","FanConfigurations":[
         {"ReadRegister":20,"WriteRegister":21,"MinSpeedValue":0,"MaxSpeedValue":100,
          "ResetRequired":true,"FanSpeedResetValue":128,"FanDisplayName":"Fan"}]}
        """, out _)!;

        Assert.Contains(resets.Cautions(), c => c.Contains("128") && c.Contains("hand control back"));
    }

    [Fact]
    public void AWordAccessConfigurationSaysSoRatherThanBeingUsedByteWide()
    {
        // ReadWriteWords means 16-bit access, which this application's EC path does not perform.
        // Using such a config byte-wide would read and write half of each value.
        var words = NbfcConfig.Parse("""
        {"NotebookModel":"Wordy","ReadWriteWords":true,"FanConfigurations":[
         {"ReadRegister":16,"WriteRegister":16,"MinSpeedValue":0,"MaxSpeedValue":100}]}
        """, out _)!;

        Assert.Contains(words.Cautions(), c => c.Contains("16-bit"));
    }

    [Fact]
    public void ASeparateReadRegisterIsImportedAsReadOnly()
    {
        // The shape of this whole release: a board whose fan can be watched but not yet commanded
        // still gets its readings, and the register that provides them is never writable.
        var split = NbfcConfig.Parse("""
        {"NotebookModel":"Split","FanConfigurations":[
         {"ReadRegister":10,"WriteRegister":11,"MinSpeedValue":0,"MaxSpeedValue":100,
          "MinSpeedValueRead":0,"MaxSpeedValueRead":255,"FanDisplayName":"Fan"}]}
        """, out _)!;

        var map = split.ToRegisterMap("SPLIT");

        Assert.True(map.At(11)!.Writable);
        Assert.False(map.At(10)!.Writable);
        Assert.Throws<EcWriteRefusedException>(() => map.CheckWrite(10, 50));
    }
}
