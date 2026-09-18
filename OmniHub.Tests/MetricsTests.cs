// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Globalization;
using OmniHub.Core.Telemetry;
using Xunit;

namespace OmniHub.Tests;

public class MetricsTests
{
    [Fact]
    public void EveryKeyIsUnique()
    {
        // The catalogue is looked up by key from a picker, a saved layout and the overlay. A
        // duplicate would resolve to whichever came first and the other would be unreachable
        // without anything saying so.
        Assert.Equal(Metrics.All.Count, Metrics.All.Select(m => m.Key).Distinct().Count());
    }

    [Fact]
    public void NoMetricIsHotBeforeItIsWarm()
    {
        // A table typo the other way round would make the warning level unreachable, and the
        // figure would jump straight from ordinary to hot.
        foreach (var metric in Metrics.All)
            if (metric is { WarnAt: { } warn, HotAt: { } hot })
                Assert.True(hot > warn, $"{metric.Key}: hot {hot} is not above warn {warn}");
    }

    [Fact]
    public void AMetricWithAHotThresholdHasAWarnThreshold()
    {
        // LevelOf reads WarnAt first and returns Unknown without it, so a hot threshold on its own
        // would silently never fire.
        foreach (var metric in Metrics.All)
            if (metric.HotAt is not null)
                Assert.NotNull(metric.WarnAt);
    }

    [Fact]
    public void AMissingReadingRendersAsUnavailableForEveryMetric()
    {
        foreach (var metric in Metrics.All)
        {
            Assert.Equal(Metrics.Unavailable, Metrics.Text(metric.Key, null));
            Assert.Equal(Metrics.Unavailable, Metrics.Text(metric.Key, double.NaN));
        }
    }

    [Fact]
    public void AnUnknownKeyIsUnavailableRatherThanAThrow()
    {
        // A layout saved by a later build can name a metric this one does not have. It has to
        // render as a gap, not take the panel down.
        Assert.Null(Metrics.Find("something-from-a-later-build"));
        Assert.Equal(Metrics.Unavailable, Metrics.Text("something-from-a-later-build", 42));
        Assert.Equal(MetricLevel.Unknown, Metrics.LevelOf("something-from-a-later-build", 42));
    }

    [Fact]
    public void FiguresAreWrittenInInvariantCulture()
    {
        var was = Thread.CurrentThread.CurrentCulture;
        try
        {
            // A locale that uses a decimal comma, which this machine's owner plausibly runs. A
            // comma inside a figure standing next to a unit reads as a thousands separator.
            Thread.CurrentThread.CurrentCulture = new CultureInfo("id-ID");

            Assert.Equal("72.4°", Metrics.Text("cpu", 72.4));
            Assert.Equal("31.9W", Metrics.Text("pkg", 31.9));
        }
        finally { Thread.CurrentThread.CurrentCulture = was; }
    }

    [Theory]
    [InlineData(70, MetricLevel.Normal)]
    [InlineData(79.9, MetricLevel.Normal)]
    [InlineData(80, MetricLevel.Warn)]      // the boundary is inclusive
    [InlineData(89.9, MetricLevel.Warn)]
    [InlineData(90, MetricLevel.Hot)]
    [InlineData(98.6, MetricLevel.Hot)]
    public void TheDieTemperatureCrossesItsThresholdsWhereItShould(double value, MetricLevel expected)
    {
        Assert.Equal(expected, Metrics.LevelOf("cpu", value));
    }

    [Fact]
    public void TheBindingLimitUsesTheSameThresholdTheRestOfTheProjectDoes()
    {
        // Not a second opinion about what "binding" means -- LimitHistory already decided, and a
        // panel that coloured at ninety while the history counted at ninety-five would be two
        // answers to one question.
        Assert.Equal(LimitHistory.BindingPercent, Metrics.Find("limit")!.WarnAt);

        Assert.Equal(MetricLevel.Normal, Metrics.LevelOf("limit", LimitHistory.BindingPercent - 0.1));
        Assert.Equal(MetricLevel.Warn, Metrics.LevelOf("limit", LimitHistory.BindingPercent));
    }

    [Fact]
    public void AMetricWithNoThresholdsNeverColoursItsFigure()
    {
        // A wattage is not good or bad on its own. Unknown rather than Normal, so a caller cannot
        // read "no threshold" as "within threshold".
        foreach (var metric in Metrics.All.Where(m => m.WarnAt is null))
            Assert.Equal(MetricLevel.Unknown, Metrics.LevelOf(metric.Key, 9999));
    }

    [Fact]
    public void TheTemperatureMetricsKeepTheirDegreeSignAndTheirPrecision()
    {
        // The die reads to a tenth and the GPU to a whole degree, which is what each source
        // actually reports -- NVML gives integers, the SMU gives a fraction.
        Assert.Equal("85.4°", Metrics.Text("cpu", 85.44));
        Assert.Equal("45°", Metrics.Text("gpu", 45.4));
    }

    [Fact]
    public void EveryMetricHasANameThatFitsANarrowCard()
    {
        // The overlay draws these in a label column about sixty pixels wide. A name that does not
        // fit is not a cosmetic problem there: the column is fixed, so it clips rather than wraps,
        // and a clipped name is a reading the user cannot identify.
        foreach (var metric in Metrics.All)
            Assert.True(metric.Compact.Length <= 8,
                        $"{metric.Key}: compact name \"{metric.Compact}\" is {metric.Compact.Length} characters");
    }

    [Fact]
    public void ACompactNameIsOnlyDefinedWhereItDiffers()
    {
        // A short label identical to the long one is a line of table that says nothing and one
        // more place to forget to change.
        foreach (var metric in Metrics.All)
            Assert.NotEqual(metric.Label, metric.ShortLabel);
    }

    [Fact]
    public void EveryReadingNamesWhereItComesFrom()
    {
        // The project's first rule, asserted rather than trusted. A figure whose source is blank
        // is one a panel will show without saying what to distrust when it looks wrong.
        foreach (var metric in Metrics.All)
            Assert.False(string.IsNullOrWhiteSpace(metric.Source), $"{metric.Key} names no source");
    }

    [Fact]
    public void ASourceDescribesARouteRatherThanRepeatingTheName()
    {
        // "CPU: CPU" is a row that costs a line and says nothing. The useful content is which
        // thing to distrust, so the source has to be longer and different than the label.
        foreach (var metric in Metrics.All)
        {
            Assert.NotEqual(metric.Label, metric.Source, StringComparer.OrdinalIgnoreCase);
            Assert.True(metric.Source.Length > metric.Label.Length,
                        $"{metric.Key}: source \"{metric.Source}\" says no more than its name");
        }
    }
}
