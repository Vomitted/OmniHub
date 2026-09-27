// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.IO;
using System.Text.RegularExpressions;
using OmniHub.Core.Theming;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// A colour per component -- processor, graphics, memory, fans -- is a code the whole interface
/// uses: the cards' stripes and bars, the chart's lines, the top bar's figures. A code whose symbols
/// cannot be told apart is not a code.
///
/// Midnight was built with four distinct colours and every other palette inherited the idea without
/// the check. Rendered in OLED Black on the user's own screen, the processor and the graphics card
/// were two cyans and the live chart's two lines were one; Midnight Blue gave the processor and the
/// fans the same value outright, and Terminal the processor and memory. None of that is visible in a
/// diff, and none of it was visible in the palette the console was designed in.
/// </summary>
public class ComponentColourTests
{
    private static readonly string[] Keys = { "MetricCpuColor", "MetricGpuColor", "MetricMemColor", "MetricFanColor" };

    public static IEnumerable<object[]> Palettes() => ContrastTests.Palettes();

    [Theory]
    [MemberData(nameof(Palettes))]
    public void EveryPaletteTellsTheComponentsApart(string paletteFile)
    {
        string xaml = File.ReadAllText(Path.Combine(WpfTestHost.WpfDir, "Palettes", paletteFile));
        var colours = Keys.ToDictionary(k => k, k =>
        {
            var m = Regex.Match(xaml, $@"x:Key=""{k}"">\s*(#[0-9A-Fa-f]{{6,8}})\s*<");
            Assert.True(m.Success, $"{paletteFile} does not define {k}");
            return Rgb.Parse(m.Groups[1].Value)!.Value;
        });

        AssertApart(paletteFile, colours);
    }

    [Theory]
    [InlineData("#0A0D12", "#11151B", "#4F9CF5", "#E8ECF2")]   // this machine's saved custom palette
    [InlineData("#F4F4F2", "#FFFFFF", "#2563EB", "#111111")]   // a light one, where the seeds are darkened
    public void ABuiltPaletteTellsTheComponentsApart(string ground, string panel, string accent, string text)
    {
        var built = new CustomPalette("test", Rgb.Parse(ground)!.Value, Rgb.Parse(panel)!.Value,
                                      Rgb.Parse(accent)!.Value, Rgb.Parse(text)!.Value).Build();

        AssertApart("the built palette", Keys.ToDictionary(k => k, k => Rgb.Parse(built[k])!.Value));
    }

    [Fact]
    public void DifferenceMeasuresWhatAnEyeSees()
    {
        var black = new Rgb(0, 0, 0);
        var white = new Rgb(255, 255, 255);

        Assert.Equal(0, Contrast.Difference(white, white), 6);
        Assert.Equal(100, Contrast.Difference(black, white), 1);   // L* runs 0 to 100

        // The pair that started this: two cyans the palette called two components.
        Assert.True(Contrast.Difference(Rgb.Parse("#22D3EE")!.Value, Rgb.Parse("#2DD4BF")!.Value)
                    < Contrast.MinimumComponentDifference);
    }

    private static void AssertApart(string source, IReadOnlyDictionary<string, Rgb> colours)
    {
        var failures = new List<string>();
        for (int i = 0; i < Keys.Length; i++)
            for (int j = i + 1; j < Keys.Length; j++)
            {
                double d = Contrast.Difference(colours[Keys[i]], colours[Keys[j]]);
                if (d < Contrast.MinimumComponentDifference)
                    failures.Add($"{Keys[i]} {colours[Keys[i]]} and {Keys[j]} {colours[Keys[j]]} are {d:0.0} apart");
            }

        Assert.True(failures.Count == 0,
            $"In {source} two components share a colour, and needs {Contrast.MinimumComponentDifference:0}:\n  "
            + string.Join("\n  ", failures));
    }
}
