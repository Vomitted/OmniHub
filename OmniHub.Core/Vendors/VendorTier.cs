// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Vendors;

/// <summary>
/// How much this application is allowed to do to a particular machine.
///
/// The rule the whole widening effort is built on: reading is offered broadly, writing only
/// where somebody has confirmed it on that board. Supporting laptops beyond one HP means using
/// register maps and command identifiers other people published, and a wrong one is not a
/// cosmetic failure -- the documented HP Pavilion fan register takes a value that, above 0x5A,
/// powers the machine off. One byte, on hardware nobody here owns.
///
/// This project already refuses to show a number the hardware did not give. Applied to writes,
/// the same rule reads: do not send a byte on the strength of a config file nobody has checked
/// against the board in front of them.
/// </summary>
public enum VendorTier
{
    /// <summary>
    /// The vendor and board are identified and nothing more. Nothing is read, nothing is written.
    /// </summary>
    Detected,

    /// <summary>
    /// A read path is known good on this board: fans, temperatures, power. Writes are refused.
    ///
    /// This is as far as a backend ships. A bundled configuration being present is not evidence
    /// that anybody ran it, and treating the two as the same thing is exactly the confusion this
    /// tier exists to prevent.
    /// </summary>
    Reading,

    /// <summary>
    /// Writes are allowed, because this board's control path has actually been exercised.
    ///
    /// Reached one of two ways and never by default: the board appears in the verified list, or
    /// the person in front of the machine unlocked it deliberately after being told what could
    /// go wrong.
    /// </summary>
    Verified,
}

/// <summary>
/// Thrown when something tried to write to hardware that has not been verified.
///
/// A refusal rather than a silent no-op, and it names the backend and the board. A write that
/// quietly does nothing is the failure mode this project keeps meeting from the other direction
/// -- firmware accepting a command and discarding it -- and reproducing it deliberately would be
/// a poor joke.
/// </summary>
public sealed class HardwareWriteRefusedException : InvalidOperationException
{
    public HardwareWriteRefusedException(string backend, VendorTier tier, string? board)
        : base(Describe(backend, tier, board))
    {
        Backend = backend;
        Tier = tier;
        Board = board;
    }

    public string Backend { get; }
    public VendorTier Tier { get; }
    public string? Board { get; }

    private static string Describe(string backend, VendorTier tier, string? board)
    {
        string where = string.IsNullOrWhiteSpace(board) ? "this board" : $"board {board}";
        return tier == VendorTier.Detected
            ? $"{backend} has not been read from on {where}, so it will not be written to."
            : $"{backend} can read {where} but writing to it has not been verified, so it is refused. "
              + "Confirm it on this machine first.";
    }
}

/// <summary>
/// The one check every hardware write goes through.
///
/// A single function rather than a convention, so that a backend added later cannot forget: the
/// test suite enumerates write methods by reflection and requires each to route through here.
/// A rule this project states without asserting is a rule it has already broken twice.
/// </summary>
public static class WriteGate
{
    /// <summary>Allows the write, or refuses it with a message naming what would have to change.</summary>
    public static void Require(VendorTier tier, string backend, string? board = null)
    {
        if (tier != VendorTier.Verified)
            throw new HardwareWriteRefusedException(backend, tier, board);
    }

    /// <summary>Whether a write would be allowed, for a UI that wants to say so before it is tried.</summary>
    public static bool Allows(VendorTier tier) => tier == VendorTier.Verified;
}
