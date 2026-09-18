// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Vendors;

namespace OmniHub.Core.Optimize;

/// <summary>
/// Undoes what this application has done to the machine, and says plainly what it could not.
///
/// The reason this exists is not tidiness. This laptop has recorded nine unexpected shutdowns
/// in six days with no established cause, and over that investigation OmniHub was suspected
/// three separate times -- each time costing an evening of stopping services, closing the
/// application, rebooting and measuring again, only to exonerate it. A single control that makes
/// the application passive turns "is it OmniHub?" from an evening into one click and a reading.
///
/// It is also the safety net under every hardware-writing feature added after it. A feature that
/// can be switched off completely, in one place, by somebody who does not know which of its
/// pieces is misbehaving, is a much safer thing to ship than one that cannot.
///
/// WHAT IT CANNOT DO is as important as what it can, and is reported rather than glossed. Two
/// classes of change do not come back without a reboot:
///
///   - SMU power and thermal limits. The mailbox takes a value; there is no "release" command
///     and no record of what the firmware's own defaults were before anything was written. A
///     reset that guessed at stock values would be inventing them.
///   - A custom GPU TGP already applied. The latch that re-asserts it is cleared here, so
///     nothing puts it back -- but the value the firmware is holding stays until the card is
///     re-initialised.
///
/// Saying so is the point. A report claiming the machine is stock when two of its limits are
/// still whatever OmniHub last wrote would make the next round of diagnosis worse, not better,
/// because it would rule out the wrong thing.
/// </summary>
public static class ReturnToStock
{
    /// <summary>
    /// Three outcomes, not two. "Could not" and "was never changed" look identical in a boolean
    /// and mean opposite things to somebody deciding whether OmniHub is still a suspect.
    /// </summary>
    public enum StockState
    {
        /// <summary>Put back. The machine no longer carries this change.</summary>
        Restored,

        /// <summary>This application never changed it, so there is nothing to undo.</summary>
        NotChanged,

        /// <summary>Changed, and cannot be undone from here. The detail says what that leaves.</summary>
        NeedsReboot,

        /// <summary>The attempt failed. The detail says why.</summary>
        Failed,
    }

    /// <summary>One thing attempted, and what actually happened to it.</summary>
    /// <param name="Name">What was attempted, in words fit to show.</param>
    /// <param name="State">Whether it came back, could not, or was never changed.</param>
    /// <param name="Detail">A sentence explaining the state.</param>
    public readonly record struct Step(string Name, StockState State, string Detail);

    /// <summary>
    /// Returns the machine to stock as far as it can be, in an order chosen so that a failure
    /// part-way through still leaves the most important thing done.
    ///
    /// The fans come first deliberately. They are the one change with a thermal consequence: a
    /// process holding fan control and then failing half way through a reset is the situation
    /// that leaves a hot laptop on a stopped fan, and handing them back to the BIOS first means
    /// every later failure is merely untidy.
    /// </summary>
    /// <param name="service">The fan loop, stopped and handed back to the BIOS.</param>
    /// <param name="gpu">The GPU controller, whose force-max latch is cleared.</param>
    /// <param name="tuningWasApplied">Whether SMU limits were written this session.</param>
    /// <param name="restorePlan">The scheme to leave active, or null to leave the plan alone.</param>
    /// <param name="journal">Cleared for anything restored here, so startup does not redo it.</param>
    public static IReadOnlyList<Step> Run(
        FanService? service,
        IGpuPowerBackend? gpu,
        bool tuningWasApplied,
        Guid? restorePlan,
        Diagnostics.RestoreJournal? journal = null)
    {
        var steps = new List<Step>();

        // 1. Fans. First, for the reason on the summary above.
        if (service is null)
            steps.Add(new Step("Fan control", StockState.NotChanged, "No fan service on this machine."));
        else
        {
            try
            {
                bool wasRunning = service.IsRunning;
                service.Stop();
                steps.Add(new Step("Fan control", wasRunning ? StockState.Restored : StockState.NotChanged,
                    wasRunning
                        ? "Stopped, and the fans handed back to the BIOS curve."
                        : "Was not driving the fans."));
            }
            catch (Exception ex)
            {
                steps.Add(new Step("Fan control", StockState.Failed,
                    $"Could not hand the fans back ({ex.Message}). Exit through the tray, which also restores them."));
            }
        }

        // 2. The GPU force-max latch. Clearing it stops anything re-asserting; it does not undo
        //    a ceiling the firmware is already holding.
        if (gpu is null)
            steps.Add(new Step("GPU power ceiling", StockState.NotChanged, "No vendor interface on this machine."));
        else if (!gpu.HoldAtMaximum)
            steps.Add(new Step("GPU power ceiling", StockState.NotChanged, "The unlock was not in force."));
        else
        {
            gpu.HoldAtMaximum = false;
            steps.Add(new Step("GPU power ceiling", StockState.NeedsReboot,
                "Nothing will re-assert the unlock now, but the ceiling the firmware is already holding "
                + "stays until the card is re-initialised. A restart clears it."));
        }

        // 3. SMU limits. No release command exists, and the firmware's own defaults were never
        //    recorded, so this is reported rather than attempted.
        steps.Add(tuningWasApplied
            ? new Step("Processor limits", StockState.NeedsReboot,
                "Power and thermal limits written to the SMU stay until a restart. There is no release "
                + "command, and this application never recorded what the firmware held before it wrote.")
            : new Step("Processor limits", StockState.NotChanged, "No limits were written this session."));

        // 4. Timer resolution and MMCSS. Both process-scoped, both genuinely releasable.
        Add(steps, "Timer resolution", SystemTuning.ReleaseHighResolutionTimer);
        Add(steps, "Desktop composition priority", () => SystemTuning.SetMmcss(false));

        // 5. The power plan. The caller decides which scheme is the user's; this only activates.
        if (restorePlan is not { } plan)
            steps.Add(new Step("Windows power plan", StockState.NotChanged, "Left as it is."));
        else
        {
            var result = PowerPlan.Activate(plan);
            steps.Add(new Step("Windows power plan",
                result.Applied ? StockState.Restored : StockState.Failed, result.Detail));
        }

        // 6. Display refresh rate, if Auto Eco lowered it and has not put it back.
        if (journal?.Pending.ContainsKey(Diagnostics.RestoreJournal.DisplayRefreshHz) == true)
        {
            foreach (var r in Diagnostics.RestoreReconciler.Run(journal))
                steps.Add(new Step("Display refresh rate",
                    r.Applied ? StockState.Restored : StockState.Failed, r.Detail));
        }
        else
        {
            steps.Add(new Step("Display refresh rate", StockState.NotChanged,
                "Not lowered, or already put back."));
        }

        return steps;
    }

    /// <summary>True when every step either came back or was never changed.</summary>
    public static bool IsFullyStock(IReadOnlyList<Step> steps)
    {
        foreach (var s in steps)
            if (s.State is not (StockState.Restored or StockState.NotChanged)) return false;
        return true;
    }

    /// <summary>
    /// One sentence for the whole run, leading with what did NOT come back.
    ///
    /// Leading with the failures is the point: somebody reads this to decide whether OmniHub can
    /// be ruled out, and a summary that opened with four successes and buried the one limit still
    /// in force would answer that question wrongly.
    /// </summary>
    public static string Summarise(IReadOnlyList<Step> steps)
    {
        var outstanding = steps.Where(s => s.State is StockState.NeedsReboot or StockState.Failed)
                               .Select(s => s.Name.ToLowerInvariant())
                               .ToArray();

        if (outstanding.Length == 0)
            return "The machine is back to stock. Nothing this application changed is still in force.";

        return $"Mostly back to stock. Still in force until a restart: {string.Join(", ", outstanding)}. "
             + "Everything else has been handed back.";
    }

    private static void Add(List<Step> steps, string name, Func<TuningResult> action)
    {
        try
        {
            var r = action();
            steps.Add(new Step(name, r.Applied ? StockState.Restored : StockState.NotChanged, r.Detail));
        }
        catch (Exception ex)
        {
            steps.Add(new Step(name, StockState.Failed, ex.Message));
        }
    }
}
