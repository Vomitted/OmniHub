// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Theming;

/// <summary>
/// How long the interface may take to move.
///
/// The user asked for most of the interface to be animated, and had earlier found an animation-heavy
/// pass slow -- durations were halved to 220-240 ms then. Both hold: a page's panels enter one after
/// another, but the stagger compresses as the count grows so the whole entrance always finishes
/// inside <see cref="EntranceMs"/>, however many panels a page has.
/// </summary>
public static class MotionBudget
{
    /// <summary>One element's own fade and rise.</summary>
    public const int EnterMs = 240;

    /// <summary>The gap between one element starting and the next, when there is room for it.</summary>
    public const int StepMs = 40;

    /// <summary>The whole of one entrance, first element starting to last element settled.</summary>
    public const int EntranceMs = 450;

    /// <summary>When the <paramref name="index"/>th of <paramref name="count"/> elements starts.</summary>
    public static int Delay(int index, int count)
    {
        if (count <= 1 || index <= 0) return 0;
        int room = EntranceMs - EnterMs;                      // what the stagger may spend in total
        int step = Math.Min(StepMs, room / (count - 1));
        return Math.Min(index, count - 1) * step;
    }
}
