// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Runtime.InteropServices;

namespace OmniHub.Core.Optimize;

public sealed record PowerScheme(Guid Id, string Name);

/// <summary>
/// Reads and switches the Windows power scheme.
///
/// Distinct from the CPU power limits in BiosCommands.PowerController: those are firmware
/// wattage caps written over the HP BIOS interface, while this is the OS-level policy that
/// governs processor states, idle behaviour and display timeouts. Both matter, and confusing
/// the two is why "power settings" in tools like this are usually incoherent.
///
/// Scheme names are read from the OS rather than hardcoded, because they are localised and
/// because OEMs ship their own. A machine with an HP-supplied plan shows that plan's real
/// name instead of an invented "Balanced".
/// </summary>
public static class PowerPlan
{
    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingGuid,
        uint accessFlags, uint index, byte[]? buffer, ref uint bufferSize);

    [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid,
        IntPtr subGroupOfPowerSettingGuid, IntPtr powerSettingGuid, byte[]? buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private const uint ACCESS_SCHEME = 16;
    private const uint ERROR_SUCCESS = 0;

    public static Guid? GetActiveSchemeId()
    {
        IntPtr ptr = IntPtr.Zero;
        try
        {
            if (PowerGetActiveScheme(IntPtr.Zero, out ptr) != ERROR_SUCCESS || ptr == IntPtr.Zero) return null;
            return Marshal.PtrToStructure<Guid>(ptr);
        }
        catch { return null; }
        finally
        {
            // PowerGetActiveScheme allocates with LocalAlloc; the caller owns the buffer.
            if (ptr != IntPtr.Zero) LocalFree(ptr);
        }
    }

    public static IReadOnlyList<PowerScheme> List()
    {
        var schemes = new List<PowerScheme>();
        try
        {
            for (uint index = 0; ; index++)
            {
                uint size = 16; // a GUID
                var buffer = new byte[size];
                if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, index, buffer, ref size) != ERROR_SUCCESS)
                    break;

                var id = new Guid(buffer);
                schemes.Add(new PowerScheme(id, ReadFriendlyName(id) ?? id.ToString()));
            }
        }
        catch { /* keep whatever was enumerated before the failure */ }
        return schemes;
    }

    private static string? ReadFriendlyName(Guid scheme)
    {
        uint size = 0;
        // First call with a null buffer to learn the required size, as the API expects.
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size) != ERROR_SUCCESS)
            return null;

        var buffer = new byte[size];
        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != ERROR_SUCCESS)
            return null;

        // Unicode and null-terminated; trim the terminator rather than shipping it to the UI.
        return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    public static TuningResult Activate(Guid schemeId)
    {
        var target = schemeId;
        uint result = PowerSetActiveScheme(IntPtr.Zero, ref target);
        if (result != ERROR_SUCCESS)
            return new TuningResult(false, $"Windows refused the power plan change (error {result}).");

        // Read back rather than trusting the call: a scheme can be blocked by policy on a
        // managed machine, and the set can report success while the active plan is unchanged.
        if (GetActiveSchemeId() != schemeId)
            return new TuningResult(false, "The power plan did not change; it may be locked by system policy.");

        return new TuningResult(true, $"Power plan set to {ReadFriendlyName(schemeId) ?? schemeId.ToString()}.");
    }
}
