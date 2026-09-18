// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Hardware;

/// <summary>
/// One embedded-controller register this application is prepared to touch on a given board.
///
/// The range is the whole point. An EC register is not a general-purpose byte: it means whatever
/// that board's firmware decides it means, and values outside its intended span are not merely
/// ineffective. The published HP Pavilion fan register is the worked example -- fan speed is
/// written to 0x58 as though it were a temperature, and a value above 0x5A makes the machine
/// power itself off to cool down.
/// </summary>
/// <param name="Name">What this register does, in words, for an error message to use.</param>
/// <param name="Address">The register's address on this board.</param>
/// <param name="Min">Lowest value anybody has established is safe here.</param>
/// <param name="Max">Highest value anybody has established is safe here.</param>
/// <param name="Writable">
/// False for a register this application reads and must never write. Most of them: a fan
/// tachometer, a temperature, a battery figure. Reading is how support widens; writing is not.
/// </param>
public sealed record EcRegister(
    string Name,
    byte Address,
    byte Min,
    byte Max,
    bool Writable = false)
{
    /// <summary>Whether a value falls inside what this register was established to accept.</summary>
    public bool Accepts(byte value) => Writable && value >= Min && value <= Max;
}

/// <summary>Thrown when something tried to put a value into an EC register that does not take it.</summary>
public sealed class EcWriteRefusedException : InvalidOperationException
{
    public EcWriteRefusedException(string message, byte address, byte value)
        : base(message)
    {
        Address = address;
        Value = value;
    }

    public byte Address { get; }
    public byte Value { get; }
}

/// <summary>
/// The registers known for one board, and the rule that nothing else gets written.
///
/// This is the piece that makes supporting laptops nobody here owns defensible rather than
/// reckless. Every project that does this -- NoteBook FanControl and the several vendor-specific
/// tools built on the same idea -- drives the EC from a per-model map, and their own
/// documentation is blunt that a wrong register risks the machine. The difference this class
/// makes is that the map states what each register ACCEPTS, not merely where it lives, so a value
/// nobody has established is refused before it reaches hardware.
///
/// Refused rather than clamped, deliberately. Clamping would leave a caller believing it
/// commanded one thing while the hardware was told another, which is the same accepted-but-not-
/// applied confusion this project keeps having to untangle from the firmware side. It also
/// happens to be the safer of the two: an out-of-range value is a bug somewhere upstream, and
/// declining to write leaves the fan where the firmware had it instead of acting on a number that
/// came from a mistake.
/// </summary>
public sealed class EcRegisterMap
{
    private readonly Dictionary<byte, EcRegister> _byAddress;

    public EcRegisterMap(string board, IEnumerable<EcRegister> registers)
    {
        Board = board;
        Registers = registers.ToList();
        _byAddress = Registers.ToDictionary(r => r.Address);
    }

    /// <summary>The baseboard this map was established on. Maps do not transfer between boards.</summary>
    public string Board { get; }

    public IReadOnlyList<EcRegister> Registers { get; }

    /// <summary>The register at an address, or null when this map does not describe it.</summary>
    public EcRegister? At(byte address) => _byAddress.TryGetValue(address, out var r) ? r : null;

    /// <summary>
    /// Checks a write, and explains the refusal when there is one.
    ///
    /// Three ways to be refused, and they are different situations deserving different sentences:
    /// the map does not describe this register at all, the register is one this application only
    /// reads, or the value falls outside what the register was established to accept.
    /// </summary>
    public void CheckWrite(byte address, byte value)
    {
        var register = At(address);

        if (register is null)
            throw new EcWriteRefusedException(
                $"Register 0x{address:X2} is not described for board {Board}, so nothing here knows "
                + "what writing to it would do. Refusing.",
                address, value);

        if (!register.Writable)
            throw new EcWriteRefusedException(
                $"Register 0x{address:X2} ({register.Name}) on board {Board} is read-only to this "
                + "application. Refusing.",
                address, value);

        if (!register.Accepts(value))
            throw new EcWriteRefusedException(
                $"0x{value:X2} is outside the established range for 0x{address:X2} ({register.Name}) "
                + $"on board {Board}, which accepts 0x{register.Min:X2} to 0x{register.Max:X2}. "
                + "Refusing rather than clamping, because a value from outside the range is a fault "
                + "upstream and acting on part of it would hide that.",
                address, value);
    }

    /// <summary>Whether a write would be accepted, for a caller that wants to ask before trying.</summary>
    public bool Allows(byte address, byte value) => At(address)?.Accepts(value) == true;

    /// <summary>
    /// The HP Pavilion fan register, as published by the people who measured it.
    ///
    /// Present as the documented example rather than as shipped support: this map has not been
    /// exercised on a Pavilion by anybody here, so a backend built on it starts at Reading and its
    /// writes stay refused until somebody with the machine says otherwise.
    ///
    /// What makes it the right example is the shape of the hazard. The fan speed is written as
    /// though it were a temperature, the firmware takes control back unless the value is rewritten
    /// about once a second, and a value above 0x5A -- ninety, read as degrees -- makes the machine
    /// power itself off to cool down and complain about a temperature failure at next boot. One
    /// byte between fan control and a hard shutdown, which is the argument for this whole class.
    /// </summary>
    public static EcRegisterMap PavilionExample(string board) => new(board, new[]
    {
        new EcRegister("fan speed, written as a temperature", 0x58, Min: 0x00, Max: 0x5A, Writable: true),
    });
}
