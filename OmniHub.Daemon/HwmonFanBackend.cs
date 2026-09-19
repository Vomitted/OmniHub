// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Hardware;
using OmniHub.Core.Vendors;

namespace OmniHub.Daemon;

/// <summary>
/// Drives a laptop's fans through the Linux hardware monitoring class.
///
/// The Linux counterpart of HpFanBackend, and deliberately the same shape: a thin wrapper whose
/// methods forward one call each, so the file deciding WHAT to command stays the cross-platform
/// one and this file only knows HOW.
///
/// Two differences from the HP path matter enough to state plainly.
///
/// The first is that this controller LATCHES. HP's embedded controller returns the fans to its
/// own curve when nothing is commanding it, which is why fan state was deliberately left out of
/// the restore journal for four versions. A hwmon PWM channel in manual mode does no such thing:
/// whatever was last written stays written, and a daemon that dies leaves the fan exactly where
/// it was with nothing running to change it. <see cref="RevertsWhenUncommanded"/> is therefore
/// false, which is what makes FanService write the takeover down before performing it.
///
/// The second is the unit. HP's byte is an RPM/100 target; PWM is an eight-bit duty cycle with no
/// fixed relationship to a speed. Actual speed is read from the tachometer instead of inferred,
/// which is better information than the HP path has -- and the reason this backend never turns a
/// duty cycle into an RPM figure to show somebody.
/// </summary>
public sealed class HwmonFanBackend : IFanBackend
{
    private readonly Hwmon _hwmon;
    private readonly HwmonChip _chip;
    private readonly IReadOnlyList<int> _channels;
    private readonly string _board;

    /// <summary>
    /// What pwmN_enable said before this process touched it, per channel.
    ///
    /// Captured rather than assumed, because "automatic" is not one number. The convention is
    /// 0 = no control (full speed), 1 = manual, 2 = automatic, but drivers differ on which modes
    /// they implement and some offer 3 and above for their own curve modes. Restoring a hard 2
    /// would move a machine that started in mode 3 onto a different curve from the one its owner
    /// had, so what is put back is what was found.
    /// </summary>
    private readonly Dictionary<int, long> _enableWas = new();

    /// <param name="hwmon">The sysfs accessor, so tests can point it at a fake tree.</param>
    /// <param name="chip">The chip to drive, from <see cref="Hwmon.Chips"/>.</param>
    /// <param name="board">Baseboard name, so a refusal can say which machine it is refusing about.</param>
    /// <param name="verified">
    /// Whether somebody has watched a commanded change move this board's fans. This is the only
    /// route to <see cref="VendorTier.Verified"/>, and it comes from the board's profile rather
    /// than from anything this class could work out for itself.
    /// </param>
    /// <param name="calibration">
    /// The duty band. Null means the whole 0-255 the PWM attribute accepts, which is the honest
    /// default: unlike HP's fan byte, a PWM channel genuinely does take its full range. A floor
    /// belongs here only once somebody has measured the duty below which their fan stalls.
    /// </param>
    public HwmonFanBackend(
        Hwmon hwmon, HwmonChip chip, string board, bool verified, FanCalibration? calibration = null)
    {
        _hwmon = hwmon;
        _chip = chip;
        _board = board;
        _channels = chip.Pwm;
        Calibration = calibration ?? new FanCalibration(MinRawLevel: 0, MaxRawLevelFan1: 255, MaxRawLevelFan2: 255);

        // Reading is offered as soon as a chip answers; writing waits for evidence. A PWM file
        // existing means the kernel has a driver for the chip, which is not the same as that
        // driver being wired to the fan in THIS chassis, nor as the write being honoured rather
        // than quietly swallowed by firmware that takes control straight back.
        Tier = verified ? VendorTier.Verified
             : chip.Fans.Count > 0 || chip.Temps.Count > 0 ? VendorTier.Reading
             : VendorTier.Detected;
    }

    public VendorTier Tier { get; }

    public FanCalibration Calibration { get; }

    /// <summary>
    /// False, because a hwmon PWM channel holds whatever it was last given.
    ///
    /// Answering true here would be the expensive kind of wrong: FanService would skip the
    /// journal entry, and an unclean exit would leave the fan pinned at whatever the curve last
    /// commanded with nothing left running to release it. The cost of being wrong the other way
    /// is one redundant hand-back at the next launch.
    /// </summary>
    public bool RevertsWhenUncommanded => false;

    /// <summary>How the fans are actually turning, per tachometer, or null where none answered.</summary>
    public IReadOnlyList<int?> ReadRpm() =>
        _chip.Fans.Select(n => (int?)_hwmon.ReadNumber(Path.Combine(_chip.Path, $"fan{n}_input"))).ToList();

    public void TakeManualControl()
    {
        WriteGate.Require(Tier, nameof(HwmonFanBackend), _board);

        foreach (int n in _channels)
        {
            string enable = Path.Combine(_chip.Path, $"pwm{n}_enable");

            // Remember the mode we found, once. Re-asserting control happens on a timer, and
            // capturing on every pass would overwrite the original with our own 1 the second time
            // through -- which would make RestoreAutomatic put the machine back into manual mode
            // and call the debt settled.
            if (!_enableWas.ContainsKey(n) && _hwmon.ReadNumber(enable) is { } was)
                _enableWas[n] = was;

            _hwmon.WriteNumber(enable, 1);
        }
    }

    public void SetLevels(byte raw1, byte raw2)
    {
        WriteGate.Require(Tier, nameof(HwmonFanBackend), _board);

        // One level per channel, in the order the chip publishes them, with the second level
        // applying to every channel beyond the first. A chip with three fans is not a reason to
        // leave the third uncommanded while the other two are held at maximum.
        for (int i = 0; i < _channels.Count; i++)
            _hwmon.WriteNumber(Path.Combine(_chip.Path, $"pwm{_channels[i]}"), i == 0 ? raw1 : raw2);
    }

    /// <summary>
    /// Puts every channel back to the mode it was in when this process found it.
    ///
    /// Falls back to 2 (the driver's own automatic curve) for a channel whose original mode could
    /// not be read. That is a guess, and it is the right way to be wrong: a machine left in mode 1
    /// is a machine whose fans nothing is commanding, whereas a machine wrongly put onto its
    /// automatic curve is merely on a curve its owner did not pick.
    ///
    /// Restores nothing when nothing was taken. Without that, a board this backend had refused to
    /// write to would still get a mode written to every one of its PWM channels on the way out --
    /// a hardware write on exactly the machine the gate exists to keep this code away from.
    /// </summary>
    public void RestoreAutomatic()
    {
        WriteGate.Require(Tier, nameof(HwmonFanBackend), _board);

        if (_enableWas.Count == 0) return;

        foreach (int n in _channels)
            _hwmon.WriteNumber(Path.Combine(_chip.Path, $"pwm{n}_enable"), _enableWas.GetValueOrDefault(n, 2));
    }
}
