// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Hardware;

namespace OmniHub.Core.Vendors;

/// <summary>
/// The HP Omen/Victus fan backend: <see cref="FanController"/> behind the interface the curve
/// loop now uses.
///
/// Deliberately a wrapper with nothing of its own. It adds no logic, changes no timing and makes
/// no decisions -- each method is one forwarding call to the method the loop was already calling
/// directly. That emptiness is the point: this code drives the fans of the machine it is being
/// written on, so the commit that introduces the seam should be provably incapable of changing
/// what the hardware is told.
/// </summary>
public sealed class HpFanBackend : IFanBackend
{
    private readonly FanController _fan;

    public HpFanBackend(FanController fan) => _fan = fan;

    /// <summary>
    /// HP's Performance mode, which is its name for "the EC will honour a level I write".
    ///
    /// Nothing about it is a performance profile in the sense the rest of this application uses
    /// that word, and nothing about it makes the machine faster. It is the manual-control latch,
    /// and the neutral name at the interface says so.
    /// </summary>
    public void TakeManualControl() => _fan.SetFanMode(FanMode.Performance);

    public void SetLevels(byte raw1, byte raw2) => _fan.SetFanLevel(raw1, raw2);

    public void RestoreAutomatic() => _fan.RestoreAutomaticControl();
}
