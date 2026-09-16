using OmniHub.Core.Telemetry;

namespace OmniHub.Tests;

/// <summary>
/// Where the gridlines go.
///
/// Round instants, not evenly spaced offsets from wherever the window began. A tick at 14:37
/// followed by one at 15:07 is arithmetically correct and useless -- reading a chart means
/// finding "about half past two", which only works if the lines land where a clock would put
/// them.
/// </summary>
public class TimeAxisTests
{
    private static readonly DateTime Odd = new(2026, 9, 15, 14, 37, 19, DateTimeKind.Utc);

    /// <summary>
    /// The headline property. Starting from a deliberately awkward instant, every tick across
    /// six hours lands on a whole or half hour.
    /// </summary>
    [Fact]
    public void TicksLandOnRoundInstantsNotOnTheWindowStart()
    {
        var ticks = TimeAxis.Ticks(Odd, Odd.AddHours(6));

        Assert.NotEmpty(ticks);
        Assert.All(ticks, t =>
        {
            Assert.Equal(0, t.Second);
            Assert.True(t.Minute % 30 == 0, $"{t:HH:mm} is not on a half hour");
        });
    }

    /// <summary>Every tick is inside the window. A gridline off the edge is drawn nowhere.</summary>
    [Fact]
    public void EveryTickIsInsideTheWindow()
    {
        var from = Odd;
        var to = Odd.AddHours(6);

        Assert.All(TimeAxis.Ticks(from, to), t => Assert.InRange(t, from, to));
    }

    /// <summary>
    /// Across every span the application will ever draw -- ten seconds to a fortnight -- the
    /// count stays near the target. A hole in the ladder shows up here as an axis that suddenly
    /// has two ticks or twenty.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(600)]
    [InlineData(1800)]
    [InlineData(3600)]
    [InlineData(6 * 3600)]
    [InlineData(24 * 3600)]
    [InlineData(3 * 24 * 3600)]
    [InlineData(7 * 24 * 3600)]
    [InlineData(14 * 24 * 3600)]
    public void TheTickCountStaysNearTheTarget(int spanSeconds)
    {
        var from = Odd;
        var to = from.AddSeconds(spanSeconds);

        int count = TimeAxis.Ticks(from, to, targetCount: 6).Count;

        Assert.InRange(count, 3, 12);
    }

    /// <summary>
    /// The step comes off the ladder, so the axis is never divided into intervals nobody thinks
    /// in. Twenty seconds and four hours are arithmetically reasonable and not how clocks work.
    /// </summary>
    [Fact]
    public void TheStepIsAlwaysOneAHumanReads()
    {
        var allowed = new[]
        {
            1.0, 2, 5, 10, 15, 30,
            60, 120, 300, 600, 900, 1800,
            3600, 7200, 10800, 21600, 43200,
            86400, 172800, 604800, 1209600, 2419200,
        };

        for (int seconds = 5; seconds < 20 * 24 * 3600; seconds = (int)(seconds * 1.35) + 1)
            Assert.Contains(TimeAxis.Step(TimeSpan.FromSeconds(seconds)).TotalSeconds, allowed);
    }

    /// <summary>A day boundary is a tick, not an accident of where the window opened.</summary>
    [Fact]
    public void ADayLongWindowTicksOnWholeHours()
    {
        var ticks = TimeAxis.Ticks(Odd, Odd.AddHours(24));

        Assert.All(ticks, t => Assert.Equal(0, t.Minute));
        Assert.All(ticks, t => Assert.Equal(0, t.Second));
    }

    [Fact]
    public void AnInvertedOrEmptyWindowHasNoTicks()
    {
        Assert.Empty(TimeAxis.Ticks(Odd, Odd));
        Assert.Empty(TimeAxis.Ticks(Odd, Odd.AddHours(-1)));
    }

    /// <summary>
    /// The label says only what changes across the window. A date on every tick of a two-minute
    /// view wastes width the readings need; time alone on a fortnight leaves Tuesday and Friday
    /// indistinguishable.
    /// </summary>
    [Fact]
    public void TheLabelFormatFollowsTheSpan()
    {
        Assert.Equal("HH:mm:ss", TimeAxis.LabelFormat(TimeSpan.FromMinutes(2)));
        Assert.Equal("HH:mm", TimeAxis.LabelFormat(TimeSpan.FromHours(6)));
        Assert.Equal("ddd HH:mm", TimeAxis.LabelFormat(TimeSpan.FromDays(3)));
        Assert.Equal("MMM d", TimeAxis.LabelFormat(TimeSpan.FromDays(14)));
    }

    /// <summary>Ticks come back as UTC, like everything else that crosses this boundary.</summary>
    [Fact]
    public void TicksAreUtc() =>
        Assert.All(TimeAxis.Ticks(Odd, Odd.AddHours(6)), t => Assert.Equal(DateTimeKind.Utc, t.Kind));
}
