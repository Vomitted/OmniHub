// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Globalization;
using OmniHub.Core.Optimize;

namespace OmniHub.Core.Diagnostics;

/// <summary>
/// Pays back, at startup, whatever a previous run changed and did not restore.
///
/// Kept apart from <see cref="RestoreJournal"/> on purpose. The journal is bookkeeping and knows
/// nothing about hardware, which is what makes it testable without a laptop; this knows how to
/// apply each key and is the part that talks to the machine.
/// </summary>
public static class RestoreReconciler
{
    /// <summary>One thing put back, and what happened.</summary>
    /// <param name="Key">The journal key.</param>
    /// <param name="Applied">Whether the machine accepted the restore.</param>
    /// <param name="Detail">A sentence fit to show or log.</param>
    public readonly record struct Restored(string Key, bool Applied, string Detail);

    /// <summary>
    /// Applies every pending entry and clears the ones that took.
    ///
    /// An entry that fails is LEFT IN THE JOURNAL. The display may legitimately not accept a
    /// rate yet -- a monitor still enumerating, a dock not yet settled -- and forgetting the
    /// debt because the first attempt missed would be the same silent loss this exists to stop.
    ///
    /// An unrecognised key is dropped rather than kept forever: it can only come from a build
    /// that knew how to honour it, and a newer build has no way to guess.
    /// </summary>
    /// <param name="fan">
    /// The fan backend to hand control back through, when a previous run left it taken.
    ///
    /// Optional, and null is a supported state rather than a degraded one: a machine with no
    /// vendor interface has no backend to reconcile through, and the entry is then left in place
    /// rather than dropped, because the debt is real even when this launch cannot settle it.
    /// </param>
    public static IReadOnlyList<Restored> Run(RestoreJournal journal, Vendors.IFanBackend? fan = null)
    {
        var done = new List<Restored>();

        foreach (var (key, value) in journal.Pending)
        {
            switch (key)
            {
                case RestoreJournal.FanManualControl:
                {
                    // A previous run took the fans off the firmware's curve on a controller that
                    // does not give them back by itself, and did not live long enough to undo it.
                    // Nothing else will: the fan is holding whatever that process last sent.
                    if (fan is null)
                    {
                        done.Add(new Restored(key, false,
                            $"A previous run left the fans under manual control ({value}), and there "
                            + "is no fan backend on this machine to hand them back through. Left "
                            + "recorded so a launch that can will."));
                        break;
                    }

                    try
                    {
                        fan.RestoreAutomatic();
                        journal.Clear(key);
                        done.Add(new Restored(key, true,
                            "Handed the fans back to firmware control after an unclean shutdown; a "
                            + $"previous run had taken them ({value})."));
                    }
                    catch (Exception ex)
                    {
                        // Left in the journal deliberately, exactly as a failed display restore is.
                        // A fan still pinned by a dead process is not a debt to forget because the
                        // first attempt to settle it missed.
                        done.Add(new Restored(key, false,
                            $"Could not hand the fans back ({ex.Message}). Will try again next launch."));
                    }

                    break;
                }

                case RestoreJournal.DisplayRefreshHz:
                {
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hz) || hz <= 0)
                    {
                        journal.Clear(key);
                        done.Add(new Restored(key, false, $"Ignored an unreadable saved refresh rate ({value})."));
                        break;
                    }

                    // Already there: the debt is settled, whoever settled it. Re-issuing the
                    // mode change would be a visible flicker for nothing.
                    int? current = DisplayControl.CurrentRefreshHz();
                    if (current == hz)
                    {
                        journal.Clear(key);
                        done.Add(new Restored(key, true, $"Display was already at {hz} Hz."));
                        break;
                    }

                    var result = DisplayControl.SetRefreshHz(hz);
                    if (result.Applied) journal.Clear(key);

                    done.Add(new Restored(key, result.Applied, result.Applied
                        ? $"Restored the display to {hz} Hz after an unclean shutdown; a previous run had lowered it."
                        : $"Could not restore the display to {hz} Hz ({result.Detail}). Will try again next launch."));
                    break;
                }

                default:
                    journal.Clear(key);
                    done.Add(new Restored(key, false, $"Dropped an unrecognised restore entry ({key})."));
                    break;
            }
        }

        return done;
    }
}
