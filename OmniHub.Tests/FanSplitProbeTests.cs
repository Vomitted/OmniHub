// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Fan;
using Xunit;

namespace OmniHub.Tests;

public class FanSplitProbeTests
{
    /// <summary>A run of samples: <paramref name="gap"/> is fan1 - fan2 once settled.</summary>
    private static List<(byte? Fan1, byte? Fan2)> Run(int count, int gap, int settleAfter = 0, byte baseline = 20)
    {
        var samples = new List<(byte? Fan1, byte? Fan2)>();
        for (int i = 0; i < count; i++)
        {
            int g = i < settleAfter ? 0 : gap;
            samples.Add(((byte)(baseline + g), baseline));
        }
        return samples;
    }

    [Fact]
    public void FansThatHeldTheCommandedSplitAreIndependent()
    {
        Assert.Equal(FanSplitVerdict.Independent,
                     FanSplitProbe.Judge(Run(20, gap: 18), commandedFan1: 40, commandedFan2: 20));
    }

    [Fact]
    public void FansThatEndedUpTogetherAreLinked()
    {
        // Commanded twenty apart, settled within the two units this firmware holds on its own.
        Assert.Equal(FanSplitVerdict.Linked,
                     FanSplitProbe.Judge(Run(20, gap: 2), commandedFan1: 40, commandedFan2: 20));
    }

    [Fact]
    public void OnlyTheSettledTailIsJudged()
    {
        // The fans take about six seconds to reach a step, so a run that starts together and ends
        // apart is a successful split, not a half-one. Including the lead-in would drag the median
        // toward zero and report Linked for a board that had in fact obeyed.
        Assert.Equal(FanSplitVerdict.Independent,
                     FanSplitProbe.Judge(Run(21, gap: 18, settleAfter: 12), commandedFan1: 40, commandedFan2: 20));
    }

    [Fact]
    public void AGapTooSmallToBeTheCommandButTooLargeToBeTogetherConcludesNothing()
    {
        // Eight of a commanded twenty. Reading that as either answer would be inventing a result.
        Assert.Equal(FanSplitVerdict.Inconclusive,
                     FanSplitProbe.Judge(Run(20, gap: 8), commandedFan1: 40, commandedFan2: 20));
    }

    [Fact]
    public void AGapInTheWrongDirectionIsNotASuccessfulSplit()
    {
        Assert.Equal(FanSplitVerdict.Inconclusive,
                     FanSplitProbe.Judge(Run(20, gap: -18), commandedFan1: 40, commandedFan2: 20));
    }

    [Fact]
    public void TooFewReportedSamplesConcludeNothing()
    {
        Assert.Equal(FanSplitVerdict.Inconclusive,
                     FanSplitProbe.Judge(Run(FanSplitProbe.MinimumSamples - 1, gap: 18), 40, 20));
    }

    [Fact]
    public void SamplesTheBoardDeclinedToReportDoNotCountTowardTheMinimum()
    {
        // A long run that is mostly nulls looks like plenty of samples and is not. The sentinel
        // this board returns instead of a reading is common enough that this is the ordinary case,
        // not an edge one.
        var samples = new List<(byte? Fan1, byte? Fan2)>();
        for (int i = 0; i < 30; i++)
            samples.Add(i % 5 == 0 ? ((byte?)38, (byte?)20) : (null, null));

        Assert.Equal(FanSplitVerdict.Inconclusive, FanSplitProbe.Judge(samples, 40, 20));
    }

    [Fact]
    public void ACommandWithNoSplitInItCannotAnswerTheQuestion()
    {
        // Whatever the fans do, an experiment that never asked them to differ has not tested
        // anything -- including when they happen to come out apart.
        Assert.Equal(FanSplitVerdict.Inconclusive,
                     FanSplitProbe.Judge(Run(20, gap: 18), commandedFan1: 40, commandedFan2: 39));
    }

    [Fact]
    public void NoSamplesAtAllConcludeNothing()
    {
        Assert.Equal(FanSplitVerdict.Inconclusive, FanSplitProbe.Judge(new List<(byte?, byte?)>(), 40, 20));
    }
}
