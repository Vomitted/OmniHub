// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Runtime.InteropServices;

namespace OmniHub.Core.Optimize;

/// <summary>
/// Reclaims cached physical memory, reporting the measured difference rather than a claimed
/// one: available RAM is sampled immediately before and after, and the delta is what gets
/// shown. If Windows hands nothing back, that is what the UI says.
///
/// What this actually does: the standby list holds pages Windows has cached but no process
/// currently needs. It is NOT wasted memory -- Windows hands it to any process that asks,
/// and purging it means those pages must be read from disk again. Clearing it helps in one
/// narrow case: reclaiming a large cache footprint left behind by something else, right
/// before launching a game. It is not routine maintenance, and the UI is worded to say so
/// rather than presenting "free RAM" as an unqualified win.
/// </summary>
public static class MemoryTools
{
    // Declared in full rather than truncated: GlobalMemoryStatusEx validates dwLength
    // against the real struct size and fails outright if it does not match.
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public int PrivilegeCount;
        public long Luid;
        public int Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, IntPtr info, int length);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desired, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? host, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState, int bufferLength, IntPtr previous, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    // Pseudo-handle for the current process: a constant, not a real handle, so it needs no
    // CloseHandle. Process.GetCurrentProcess().Handle would instead allocate a Process
    // object owning a real handle that is never disposed -- a slow leak on a method the
    // user can press repeatedly.
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private const int SystemMemoryListInformation = 0x50;
    private const int MemoryPurgeStandbyList = 4;
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const int SE_PRIVILEGE_ENABLED = 0x0002;

    /// <summary>Physical memory currently available, in bytes. 0 if it could not be read.</summary>
    public static ulong AvailablePhysicalBytes()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.ullAvailPhys : 0UL;
    }

    public static ulong TotalPhysicalBytes()
    {
        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.ullTotalPhys : 0UL;
    }

    /// <summary>
    /// Purges the standby list. Requires SeProfileSingleProcessPrivilege, which an elevated
    /// process holds but must still explicitly enable on its token: privileges are present
    /// but disabled by default, so skipping that step fails with an access error even when
    /// running as Administrator.
    /// </summary>
    public static TuningResult PurgeStandbyList()
    {
        if (!TryEnablePrivilege("SeProfileSingleProcessPrivilege", out string privError))
            return new TuningResult(false, privError);

        ulong before = AvailablePhysicalBytes();
        if (before == 0) return new TuningResult(false, "Could not read memory status.");

        IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(buffer, MemoryPurgeStandbyList);
            int status = NtSetSystemInformation(SystemMemoryListInformation, buffer, sizeof(int));
            if (status != 0)
                return new TuningResult(false, $"Windows refused the purge (NTSTATUS 0x{status:X8}).");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        ulong after = AvailablePhysicalBytes();

        // Freeing nothing is a perfectly normal outcome: it means the standby list was
        // already small. Reporting that honestly is the entire point of measuring.
        if (after <= before)
            return new TuningResult(true, "Standby list purged; no measurable memory was reclaimed.");

        double freedMb = (after - before) / 1024.0 / 1024.0;
        return new TuningResult(true, $"Reclaimed {freedMb:0.#} MB of cached memory.");
    }

    private static bool TryEnablePrivilege(string name, out string error)
    {
        error = "";
        IntPtr token = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(),
                    TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
            {
                error = "Could not open the process token.";
                return false;
            }

            if (!LookupPrivilegeValue(null, name, out long luid))
            {
                error = $"Could not resolve {name}.";
                return false;
            }

            var tp = new TokenPrivileges { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref tp, Marshal.SizeOf<TokenPrivileges>(), IntPtr.Zero, IntPtr.Zero))
            {
                error = "Could not adjust token privileges.";
                return false;
            }

            // AdjustTokenPrivileges returns true even when it assigned nothing, so the last
            // error has to be checked separately. ERROR_NOT_ALL_ASSIGNED specifically means
            // the token does not hold the privilege; testing for "any non-zero" would also
            // trip on unrelated codes left over from an earlier call and report the wrong
            // cause.
            const int ERROR_NOT_ALL_ASSIGNED = 1300;
            int lastError = Marshal.GetLastWin32Error();
            if (lastError == ERROR_NOT_ALL_ASSIGNED)
            {
                error = "This needs Administrator rights, which the process does not currently hold.";
                return false;
            }
            if (lastError != 0)
            {
                error = $"Could not enable {name} (Win32 error {lastError}).";
                return false;
            }

            return true;
        }
        finally
        {
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }
}
