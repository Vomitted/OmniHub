// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Hardware;

/// <summary>The ioctl names an embedded-controller PawnIO module exports.</summary>
/// <param name="Read">Takes a register address, returns its value.</param>
/// <param name="Write">Takes a register address and a value.</param>
public sealed record EcIoctlNames(string Read, string Write)
{
    /// <summary>
    /// The names this build will try first, and they are a GUESS.
    ///
    /// Stated rather than buried, because this project's whole standard is that it does not act
    /// on numbers nobody measured, and an ioctl name is no different. RyzenSMU's eight names were
    /// read out of the shipped binary's own string table; the same has not been done for
    /// LpcACPIEC, because that module is not present here and downloading somebody's signed
    /// driver blob to read strings out of it is the user's call rather than mine.
    ///
    /// Nothing rests on the guess being right. <see cref="EcAccess.TryOpen"/> proves the read
    /// name by performing one before it returns anything usable, so wrong names produce a clean
    /// "this module does not export what was expected" and no access at all. The write name is
    /// never reached on a machine whose read did not work.
    /// </summary>
    public static readonly EcIoctlNames Assumed = new("ioctl_read_ec", "ioctl_write_ec");
}

/// <summary>
/// Reading and writing one embedded-controller register, through PawnIO's LpcACPIEC module.
///
/// This is the transport every laptop that is not an HP Omen will need. Lenovo's IdeaPads, HP's
/// Pavilions, most MSI and Acer machines expose no vendor command set worth the name; what they
/// have is an EC, and the way anybody reaches it is a signed driver plus a per-model register map.
///
/// OmniHub already installs the driver. PawnIO ships LpcACPIEC as a signed module alongside the
/// RyzenSMU one this application has used for months, and <see cref="PawnIoAccess"/> dispatches by
/// module-exported name rather than knowing anything about the SMU -- so the wrapper needed no
/// changes to reach a second module. That is the one genuinely lucky part of this.
///
/// WHAT IS AND IS NOT ESTABLISHED HERE, since the distinction is the difference between careful
/// and reckless:
///
/// <list type="bullet">
/// <item>The refusal logic is established and tested -- <see cref="EcRegisterMap"/> decides what
/// may be written where, and the Pavilion ceiling has thirteen tests behind it.</item>
/// <item>The ioctl names are a guess, marked as one, and proved by round-trip before use.</item>
/// <item>The module binary is not shipped. Nothing here downloads it, and without it this class
/// reports itself unavailable and does nothing at all.</item>
/// <item>No write path has been exercised against real silicon by anybody on this project. That
/// is what the tier system is for: a backend built on this starts at Reading.</item>
/// </list>
/// </summary>
public sealed class EcAccess : IDisposable
{
    private const string ModuleFileName = "LpcACPIEC.bin";

    private readonly PawnIoAccess _pawnIo;
    private readonly EcIoctlNames _names;

    private EcAccess(PawnIoAccess pawnIo, EcRegisterMap map, EcIoctlNames names)
    {
        _pawnIo = pawnIo;
        _names = names;
        Map = map;
    }

    /// <summary>The board's register map. Every write is checked against it first.</summary>
    public EcRegisterMap Map { get; }

    /// <summary>Where the module was loaded from, for a diagnostics screen to state.</summary>
    public string? ModulePath { get; private init; }

    /// <summary>
    /// Opens the EC, or explains why it could not.
    ///
    /// Returns null for every ordinary reason -- no module, no driver, not elevated, a module that
    /// does not export what was expected -- because on the great majority of machines this will
    /// legitimately be unavailable, and that is not a failure.
    ///
    /// The liveness check is a READ, and specifically a read of a register the map already
    /// describes. Opening by writing something would mean the first thing this class ever did to
    /// an unverified machine was change it.
    /// </summary>
    public static EcAccess? TryOpen(EcRegisterMap map, out string? unavailableReason, EcIoctlNames? names = null)
    {
        names ??= EcIoctlNames.Assumed;

        // The map is checked before the module, deliberately. Both are reasons to stop, but they
        // are not equally useful to hear: "no map for your board" is about this machine and is
        // what somebody can act on, while "no module installed" is about this installation and
        // would be the answer given to every board on earth. A machine with no map has no use for
        // the module even if it were present, so the specific answer comes first.
        if (map.Registers.Count == 0)
        {
            unavailableReason =
                $"No register map is known for board {map.Board}, so there is nothing this could " +
                "safely read. A map has to be established on the machine before it is of any use.";
            return null;
        }

        string? modulePath = ResolveModulePath();
        if (modulePath is null)
        {
            unavailableReason =
                $"{ModuleFileName} was not found. Embedded-controller access needs it in " +
                "Assets\\PawnIO beside OmniHub.exe; it is published in the PawnIO.Modules releases " +
                "at github.com/namazso/PawnIO.Modules. OmniHub does not download it for you.";
            return null;
        }

        var pawnIo = PawnIoAccess.TryOpen(out var status);
        if (pawnIo is null)
        {
            unavailableReason = status switch
            {
                PawnIoStatus.RuntimeNotInstalled => "The PawnIO driver is not installed (https://pawnio.eu).",
                PawnIoStatus.AccessDenied => "PawnIO requires administrator rights; OmniHub is not elevated.",
                _ => "The PawnIO driver did not respond. Its service may be stopped.",
            };
            return null;
        }

        try
        {
            pawnIo.LoadModule(File.ReadAllBytes(modulePath));

            var access = new EcAccess(pawnIo, map, names) { ModulePath = modulePath };

            // Prove the read ioctl exists and answers, against a register the map describes.
            // A module that loaded but does not export what this expects is exactly the case the
            // guessed names make possible, and it has to end here rather than at the first write.
            if (access.Read(map.Registers[0].Address) is null)
            {
                pawnIo.Dispose();
                unavailableReason =
                    $"{ModuleFileName} loaded but did not answer '{names.Read}'. The ioctl names " +
                    "this build expects have not been confirmed against the module's own exports.";
                return null;
            }

            unavailableReason = null;
            return access;
        }
        catch (Exception ex)
        {
            pawnIo.Dispose();
            unavailableReason = $"{ModuleFileName} failed to load: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// One register's current value, or null when the module did not answer.
    ///
    /// Null rather than zero, for the reason everything else in this application returns null: a
    /// register that did not answer is not a register reading zero, and on an EC zero is a
    /// perfectly ordinary value for a fan to be at.
    /// </summary>
    public byte? Read(byte address)
    {
        try
        {
            Span<ulong> output = stackalloc ulong[1];
            if (_pawnIo.Execute(_names.Read, new ulong[] { address }, output) < 1) return null;
            return (byte)(output[0] & 0xFF);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes one register, having first established that this board accepts the value there.
    ///
    /// Three things happen in order, and the order is the safety property. The map refuses
    /// anything it does not recognise. Only then does the value reach hardware. Then it is read
    /// back, because firmware accepting a command and doing something else with it is a failure
    /// mode this project has already met from the SMU side -- a write reporting success while
    /// changing nothing is worse than one that failed.
    /// </summary>
    /// <exception cref="EcWriteRefusedException">The map does not permit this write.</exception>
    /// <returns>What the register read back as, or null when the read-back itself failed.</returns>
    public byte? Write(byte address, byte value)
    {
        Map.CheckWrite(address, value);

        _pawnIo.Execute(_names.Write, new ulong[] { address, value }, Span<ulong>.Empty);

        return Read(address);
    }

    private static string? ResolveModulePath() =>
        new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "PawnIO", ModuleFileName),
            Path.Combine(AppContext.BaseDirectory, ModuleFileName),
        }.FirstOrDefault(File.Exists);

    public void Dispose() => _pawnIo.Dispose();
}
