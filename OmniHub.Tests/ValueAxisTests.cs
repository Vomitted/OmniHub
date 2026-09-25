// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Globalization;
using OmniHub.Core.Telemetry;

namespace OmniHub.Tests;

public class ValueAxisTests
{
    /// <summary>
    /// The case that exposed it. A die between 60 and 80 C, padded by the chart, was split into
    /// quarters at 58.4, 64.2, 70.0, 75.8 and 81.6 and labelled "58", "64", "70", "76", "82" -- four
    /// labels naming a value their gridline was not at.
    /// </summary>
    [Fact]
    public void EveryTickSitsOnARoundValue()
    {
        var scale = ValueAxis.Nice(58.4, 81.6);

        Assert.Equal(5.0, scale.Step);
        Assert.All(scale.Ticks(), t => Assert.Equal(0.0, Math.IEEERemainder(t, 5), 9));
    }

    [Theory]
    [InlineData(58.4, 81.6)]    // a die temperature
    [InlineData(0, 1)]          // an empty chart
    [InlineData(0.13, 0.91)]
    [InlineData(1180, 5540)]    // fan speed in RPM
    [InlineData(-3, 7)]
    [InlineData(3.2, 3.9)]      // clock in GHz
    [InlineData(42, 42)]        // a flat window
    public void TheScaleCoversTheDataInAReadableNumberOfSteps(double lo, double hi)
    {
        var scale = ValueAxis.Nice(lo, hi);

        Assert.True(scale.Lo <= lo + 1e-9 && scale.Hi >= hi - 1e-9,
                    $"{scale.Lo}..{scale.Hi} does not cover {lo}..{hi}");
        Assert.InRange(scale.Ticks().Count - 1, 2, 8);
    }

    [Fact]
    public void ALabelWritesItsValueExactly()
    {
        // The empty chart printed its lines at 0.25 and 0.75 as "0.3" and "0.8". Written with the
        // step's own decimals, every label reads back as the value it sits at.
        var scale = ValueAxis.Nice(0, 1, targetIntervals: 4);

        Assert.Equal(0.25, scale.Step);
        foreach (double t in scale.Ticks())
        {
            string label = t.ToString("F" + scale.Decimals, CultureInfo.InvariantCulture);
            Assert.Equal(t, double.Parse(label, CultureInfo.InvariantCulture), 12);
        }
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(2.5, 1)]
    [InlineData(0.25, 2)]
    [InlineData(0.002, 3)]
    [InlineData(1000, 0)]
    public void TheDecimalsAreTheFewestThatWriteTheStep(double step, int decimals)
    {
        Assert.Equal(decimals, ValueAxis.DecimalsFor(step));
    }

    [Fact]
    public void ABoundAlreadyOnAStepIsNotPushedOutAWholeStep()
    {
        // Floating-point residue on exactly 80 would otherwise add an empty band the height of a
        // whole step to the top of the chart.
        var scale = ValueAxis.Nice(60, 80);
        Assert.Equal(60.0, scale.Lo, 9);
        Assert.Equal(80.0, scale.Hi, 9);
    }

    [Fact]
    public void NonsenseInputProducesAUsableScaleRatherThanAThrow()
    {
        var scale = ValueAxis.Nice(double.NaN, 5);
        Assert.True(scale.Hi > scale.Lo);
    }
}
