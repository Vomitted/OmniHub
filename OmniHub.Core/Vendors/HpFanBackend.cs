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

    /// <summary>
    /// The measured band, or a profile for this board where one exists.
    ///
    /// Defaulted rather than required, because the default is itself a measurement -- 10 to 56 on
    /// board 8C2F -- and not a placeholder. A caller with no profile is in exactly the position
    /// every build so far has been in.
    /// </summary>
    public FanCalibration Calibration { get; }

    private readonly string? _board;

    /// <summary>
    /// Verified when HP's own control interface answered on this machine, Detected when it did
    /// not.
    ///
    /// This is a deliberately different standard from the one an embedded-controller backend will
    /// have to meet, and the difference is the nature of the evidence. hpqBIntM is the vendor's
    /// own command set, the same one their software uses, and the firmware validates what it is
    /// sent -- a command it does not like is refused rather than acted on. A raw EC register map
    /// taken from a config file has no such arbiter: the byte goes where the file says, and on a
    /// Pavilion the published fan register powers the machine off above 0x5A. Those two should
    /// not clear the same bar.
    ///
    /// It also cannot regress anything. Without the interface, BiosInterop.Send already throws
    /// NotSupportedException, so a machine that fails this check had no fan control to lose.
    /// </summary>
    public VendorTier Tier { get; }

    /// <param name="vendorInterfaceAvailable">
    /// Whether hpqBIntM answered -- HardwareContext.VendorSupported. Defaulted true so a caller
    /// constructing this over a working FanController gets today's behaviour.
    /// </param>
    public HpFanBackend(
        FanController fan,
        FanCalibration? calibration = null,
        bool vendorInterfaceAvailable = true,
        string? board = null)
    {
        _fan = fan;
        _board = board;
        Calibration = calibration ?? FanCalibration.Default;
        Tier = vendorInterfaceAvailable ? VendorTier.Verified : VendorTier.Detected;
    }

    /// <summary>
    /// HP's Performance mode, which is its name for "the EC will honour a level I write".
    ///
    /// Nothing about it is a performance profile in the sense the rest of this application uses
    /// that word, and nothing about it makes the machine faster. It is the manual-control latch,
    /// and the neutral name at the interface says so.
    /// </summary>
    public void TakeManualControl()
    {
        WriteGate.Require(Tier, Name, _board);
        _fan.SetFanMode(FanMode.Performance);
    }

    public void SetLevels(byte raw1, byte raw2)
    {
        WriteGate.Require(Tier, Name, _board);
        _fan.SetFanLevel(raw1, raw2);
    }

    /// <summary>
    /// Handing the fans back is gated too, and that is worth stating because the opposite is
    /// tempting: surely releasing control is always safe?
    ///
    /// It is not a question of safety but of meaning. This backend can only have taken control
    /// through a gate that was open, so if the gate is shut it never took any, and the command
    /// would be telling firmware to stop doing something it was never asked to do. Exempting it
    /// would also put one write outside the rule, and one exception is how a rule stops being
    /// checkable.
    /// </summary>
    public void RestoreAutomatic()
    {
        WriteGate.Require(Tier, Name, _board);
        _fan.RestoreAutomaticControl();
    }

    /// <summary>What a refusal calls this backend.</summary>
    public const string Name = "HP fan control";
}
