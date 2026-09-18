// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Hardware;
using OmniHub.Core.Vendors;

namespace OmniHub.Tests;

/// <summary>
/// A laptop that exists only in a test: it answers from a script, records what it was told, and
/// can be asked to behave badly on purpose.
///
/// This is the piece that makes supporting hardware nobody here owns a reviewable proposition
/// rather than a hopeful one. Every backend after HP will be written against a machine that
/// cannot be borrowed, so the alternative to a fake is shipping logic whose only test is
/// somebody else's laptop -- which is the arrangement this project is trying to get away from.
///
/// It models three things a real controller does and a naive stub would not:
///
/// <list type="bullet">
/// <item>it refuses writes when its tier says so, through the same gate the real backends use;</item>
/// <item>it can <see cref="Latches"/>, holding a commanded level after the process goes away,
/// which is what a controller that does NOT quietly revert would do to a machine;</item>
/// <item>it can fail, because firmware does, and a loop that dies on the first refusal leaves a
/// hot machine with stopped fans.</item>
/// </list>
/// </summary>
public sealed class ScriptedFanBackend : IFanBackend
{
    /// <summary>What a refusal from this backend calls itself.</summary>
    public const string Name = "scripted fan backend";

    /// <summary>Verified by default, so a test that is not about the gate does not have to say so.</summary>
    public VendorTier Tier { get; set; } = VendorTier.Verified;

    /// <summary>
    /// The measured HP band by default, so raw levels asserted in tests are the ones this
    /// machine would really be sent. Settable because a backend's scale is its own.
    /// </summary>
    public FanCalibration Calibration { get; set; } = FanCalibration.Default;

    /// <summary>The board a refusal names.</summary>
    public string? Board { get; set; }

    /// <summary>Every call in order, so a test can assert on sequence and not just on totals.</summary>
    public List<string> Calls { get; } = new();

    /// <summary>Each level pair this backend was commanded.</summary>
    public List<(byte Fan1, byte Fan2)> Levels { get; } = new();

    /// <summary>When set, every write throws it -- a backend whose firmware has gone away.</summary>
    public Exception? FailWith { get; set; }

    /// <summary>
    /// Whether this controller keeps a commanded level once nothing is commanding it.
    ///
    /// False matches the HP board this project was built on, whose EC reverts to its own curve,
    /// and which is the assumption the application currently rests on. True is the case nobody
    /// here can test against real silicon, and which would leave a fan pinned after a crash.
    /// </summary>
    public bool Latches { get; set; }

    /// <summary>Whether the fans are currently off the firmware's own curve.</summary>
    public bool UnderManualControl { get; private set; }

    /// <summary>What the controller is actually holding the fans at, or null for its own curve.</summary>
    public (byte Fan1, byte Fan2)? Held { get; private set; }

    public void TakeManualControl()
    {
        WriteGate.Require(Tier, Name, Board);
        Calls.Add("control");
        UnderManualControl = true;
        if (FailWith is { } ex) throw ex;
    }

    public void SetLevels(byte raw1, byte raw2)
    {
        WriteGate.Require(Tier, Name, Board);
        Calls.Add($"level {raw1}/{raw2}");
        Levels.Add((raw1, raw2));
        Held = (raw1, raw2);
        if (FailWith is { } ex) throw ex;
    }

    public void RestoreAutomatic()
    {
        WriteGate.Require(Tier, Name, Board);
        Calls.Add("restore");
        UnderManualControl = false;
        Held = null;
    }

    /// <summary>
    /// The process vanishes without running its shutdown path: a hang, a hard kill, a bugcheck.
    ///
    /// A controller that does not latch reverts to its own curve and the machine is fine. One
    /// that latches goes on holding whatever it was last told by a process that no longer exists,
    /// with nothing left to change it.
    /// </summary>
    public void SimulateUncleanExit()
    {
        Calls.Add("crash");
        if (!Latches) Held = null;
    }
}
