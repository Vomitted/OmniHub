namespace OmniHub.Core.Fan;

/// <summary>What a split-command experiment showed about the two fans.</summary>
public enum FanSplitVerdict
{
    /// <summary>Not enough reported readings, or the readings settled somewhere that says neither.</summary>
    Inconclusive,

    /// <summary>The two fans ended up together despite being commanded apart.</summary>
    Linked,

    /// <summary>They held apart, by enough of the commanded difference to be the command and not drift.</summary>
    Independent,
}

/// <summary>
/// Whether this board's SetFanLevel really drives its two fans separately.
///
/// The question is open on this machine and matters, because a per-fan curve that the firmware
/// quietly collapses into one is a control that appears to work and does nothing. The evidence
/// available without an experiment points at independence but does not settle it: across 192,791
/// logged readings the two fans sit together in 91.9 per cent of steady samples while this
/// application is commanding them -- which it has only ever done with one value -- and sit exactly
/// two apart in 98.4 per cent of the steady samples where the firmware was driving instead. The
/// firmware clearly runs them at different speeds. Whether it lets anyone else do so is what the
/// experiment asks.
///
/// Only the judgement lives here. Commanding the split, waiting and reading back is the caller's
/// job, because it touches hardware and this has to be testable without any.
/// </summary>
public static class FanSplitProbe
{
    /// <summary>
    /// Below this many usable samples nothing is concluded.
    ///
    /// The caller reads the fans directly rather than waiting on the poll loop, whose readback is
    /// only refreshed every fifth tick. At a two-second cadence twelve samples is twenty-four
    /// seconds of fans held apart -- the floor for saying anything, not a comfortable margin.
    /// </summary>
    public const int MinimumSamples = 12;

    /// <summary>
    /// The fraction of the commanded difference the fans must actually hold to count as separate.
    ///
    /// Not all of it: fans reach a commanded speed at their own rate and the two here differ,
    /// which is visible in the log as one running a step ahead of the other through every ramp.
    /// Half is well clear of that and well clear of the firmware's own two-unit offset.
    /// </summary>
    public const double RequiredShareOfSplit = 0.5;

    /// <summary>
    /// The largest gap still counted as the fans being together.
    ///
    /// Two units is the offset the firmware holds on its own, measured in 98.4 per cent of the
    /// steady samples where it was driving. A result at or under that is this board doing what it
    /// does anyway, not the command taking effect.
    /// </summary>
    public const int LinkedTolerance = 2;

    /// <summary>
    /// Judges a run of readback samples taken while the two fans were commanded apart.
    ///
    /// Only the last third is considered. The fans take about six seconds to reach a commanded
    /// step on this chassis and the readback lags further, so the early samples describe the
    /// previous state and including them would drag any real split toward zero.
    /// </summary>
    public static FanSplitVerdict Judge(
        IReadOnlyList<(byte? Fan1, byte? Fan2)> samples, byte commandedFan1, byte commandedFan2)
    {
        if (samples is null) return FanSplitVerdict.Inconclusive;

        int settledFrom = samples.Count - samples.Count / 3;
        var gaps = new List<int>();

        for (int i = settledFrom; i < samples.Count; i++)
            if (samples[i].Fan1 is { } f1 && samples[i].Fan2 is { } f2)
                gaps.Add(f1 - f2);

        // Counted against the whole run, not against the settled part: a probe that reported for
        // most of its length and then stopped has not produced a usable window either.
        int reported = 0;
        foreach (var s in samples) if (s.Fan1 is not null && s.Fan2 is not null) reported++;

        if (reported < MinimumSamples || gaps.Count == 0) return FanSplitVerdict.Inconclusive;

        int observed = Median(gaps);
        int commanded = commandedFan1 - commandedFan2;

        // A command with no split in it cannot answer the question however the fans respond.
        if (Math.Abs(commanded) <= LinkedTolerance) return FanSplitVerdict.Inconclusive;

        if (Math.Sign(observed) == Math.Sign(commanded) &&
            Math.Abs(observed) >= Math.Abs(commanded) * RequiredShareOfSplit)
            return FanSplitVerdict.Independent;

        if (Math.Abs(observed) <= LinkedTolerance) return FanSplitVerdict.Linked;

        // Moved, but not by enough to be the command, or in the wrong direction. Saying so is the
        // honest answer; calling it either way would be reading a result into a measurement that
        // did not produce one.
        return FanSplitVerdict.Inconclusive;
    }

    /// <summary>Describes a verdict in the terms of the experiment that produced it.</summary>
    public static string Describe(FanSplitVerdict verdict, int commandedSplit) => verdict switch
    {
        FanSplitVerdict.Independent =>
            $"The fans held apart. Commanded {commandedSplit} raw units apart, they stayed apart, "
            + "so this board drives its two fans separately and a second curve will take effect.",

        FanSplitVerdict.Linked =>
            $"The fans ended up together. Commanded {commandedSplit} raw units apart, they settled "
            + "within two of each other, which is the offset this firmware holds on its own. A "
            + "second curve would not take effect on this board.",

        _ => "No conclusion. Either the board declined to report a fan speed often enough, or the "
           + "fans settled somewhere that is neither together nor as far apart as commanded.",
    };

    private static int Median(List<int> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }
}
