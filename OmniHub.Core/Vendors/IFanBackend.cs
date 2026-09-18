// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Hardware;

namespace OmniHub.Core.Vendors;

/// <summary>
/// What the curve loop needs from a machine in order to drive its fans.
///
/// Three methods, and that is the whole of it. <see cref="OmniHub.Core.Fan.FanService"/> reads a
/// temperature through a delegate it is handed and writes through this; everything in between --
/// the curve, the hysteresis, the safety floor, the predictive lead, the blind-sensor override --
/// is arithmetic that does not care whose laptop it is running on.
///
/// That asymmetry is the argument for keeping this interface small rather than expressive. The
/// loop is the one part of this application whose failure mode is a hot machine with stopped
/// fans, so the less of it that varies per vendor, the fewer places a vendor can break it.
///
/// Levels are in the backend's own raw units -- not percentages, and not some normalised scale
/// invented here. HP's byte is an RPM/100 target; other boards take a PWM duty cycle; a fourth
/// unit defined at this seam to paper over that difference would match no hardware at all. The
/// backend owns its scale and states it; the loop asks.
/// </summary>
public interface IFanBackend
{
    /// <summary>
    /// How far this backend is trusted on the machine it is attached to.
    ///
    /// Reading is offered wherever a read path is known good; writing waits until the board's
    /// control path has actually been exercised. Every write method here refuses below
    /// <see cref="VendorTier.Verified"/>, and the suite checks that by reflection rather than
    /// trusting each backend to remember.
    /// </summary>
    VendorTier Tier { get; }

    /// <summary>
    /// The raw band this machine's fans actually run, and the arithmetic that interprets it.
    ///
    /// Carried by the backend rather than held in one place for the whole process, because it is
    /// the property of a machine that differs most between them: HP's byte is an RPM/100 target
    /// clamping at 56 on this chassis, and a board taking a PWM duty cycle shares neither the
    /// range nor the units. A single band for the process could only ever be right about one.
    /// </summary>
    FanCalibration Calibration { get; }

    /// <summary>
    /// Takes the fans off the firmware's own curve, so that a level written afterwards is
    /// honoured instead of ignored.
    ///
    /// Called before the first write and re-asserted periodically rather than once at startup.
    /// Firmware reclaims control on sleep, on resume, on a mode change and sometimes on the
    /// vendor's own service starting -- and a backend that has lost control still accepts level
    /// writes and silently does nothing with them, which is indistinguishable from working.
    /// </summary>
    void TakeManualControl();

    /// <summary>Commands both fans, in this backend's own raw units.</summary>
    void SetLevels(byte raw1, byte raw2);

    /// <summary>
    /// Hands the fans back to the firmware's own control.
    ///
    /// The one method here whose failure is dangerous rather than merely unhelpful. It runs on
    /// exit, and a machine left under the manual control of a process that is no longer running
    /// is a machine whose fans nothing is commanding.
    /// </summary>
    void RestoreAutomatic();
}
