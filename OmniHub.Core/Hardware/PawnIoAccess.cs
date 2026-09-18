// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace OmniHub.Core.Hardware;

/// <summary>Why a PawnIO executor could not be opened. Reported, never guessed around.</summary>
public enum PawnIoStatus
{
    Available,

    /// <summary>PawnIOLib.dll was not found -- the runtime from https://pawnio.eu is not installed.</summary>
    RuntimeNotInstalled,

    /// <summary>The library loaded but pawnio_open returned E_ACCESSDENIED. The driver requires elevation.</summary>
    AccessDenied,

    /// <summary>The library loaded but the kernel driver did not answer (service stopped or removed).</summary>
    DriverUnavailable,
}

/// <summary>
/// A thin, honest P/Invoke wrapper around PawnIO's user-mode library.
///
/// PawnIO is a signed, minimal kernel driver that runs sandboxed bytecode modules, rather
/// than handing userland a blanket "write any MSR" primitive the way WinRing0 does. It is
/// what UXTU itself uses on this machine (its Assets\AMD\PawnIO folder ships RyzenSMU.bin),
/// and the driver is already installed and running here.
///
/// This class used to be a deliberate stub, on the grounds that P/Invoke signatures which
/// cannot be checked against a real SDK are the kind of thing that silently returns garbage
/// temperatures. That objection is now answered rather than ignored: PawnIO installs its
/// public header at C:\Program Files\PawnIO\PawnIOLib.h, and every signature below is
/// transcribed from that header on this machine. Nothing here is inferred.
///
/// Two deliberate design choices:
///
/// The library is loaded by explicit path via NativeLibrary rather than a [DllImport]
/// attribute. PawnIO does not add itself to PATH (verified: its installer records only an
/// InstallLocation under the Uninstall key), so a plain DllImport would throw
/// DllNotFoundException deep inside the first call on any machine without it. Loading
/// manually turns "not installed" into an ordinary, catchable status instead.
///
/// Execute() serialises on a lock. A PawnIO module carries state across IOCTLs -- RyzenSMU's
/// PM table is resolved once and then updated/read as separate calls -- so two threads
/// interleaving on one executor could read a table another thread was mid-update on. This
/// mirrors the _sendLock already guarding BiosInterop for the same reason.
/// </summary>
public sealed class PawnIoAccess : IDisposable
{
    private const string LibraryName = "PawnIOLib.dll";
    private const int SOk = 0;
    private const int EAccessDenied = unchecked((int)0x80070005);

    // Delegates rather than raw function pointers so the project needs no AllowUnsafeBlocks.
    // STDAPICALLTYPE is __stdcall; on x64 there is only one calling convention, but naming it
    // keeps the declarations honest against the header.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int VersionFn(out uint version);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int OpenFn(out IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int LoadFn(IntPtr handle, byte[] blob, nuint size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    private delegate int ExecuteFn(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        ulong[] input,
        nuint inputCount,
        ulong[] output,
        nuint outputCount,
        out nuint returnedCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CloseFn(IntPtr handle);

    private sealed record Exports(VersionFn Version, OpenFn Open, LoadFn Load, ExecuteFn Execute, CloseFn Close);

    private static readonly object InitLock = new();
    private static Exports? _exports;
    private static bool _triedLoad;

    /// <summary>Full path of the loaded PawnIOLib.dll, or null when the runtime is absent.</summary>
    public static string? LibraryPath { get; private set; }

    private readonly object _executeLock = new();
    private IntPtr _handle;
    private bool _disposed;

    private PawnIoAccess(IntPtr handle) => _handle = handle;

    /// <summary>True when a module has been loaded into this executor and calls can be made.</summary>
    public bool IsModuleLoaded { get; private set; }

    /// <summary>
    /// Whether the current process can actually use PawnIO. Elevation is a genuine
    /// prerequisite, not a nicety: pawnio_open returns E_ACCESSDENIED without it (measured
    /// on this machine -- 0x80070005 unelevated, S_OK elevated).
    /// </summary>
    public static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>PawnIO runtime version as (major, minor, patch), or null if the library is absent.</summary>
    public static (int Major, int Minor, int Patch)? RuntimeVersion()
    {
        var exports = TryLoadLibrary();
        if (exports is null) return null;
        if (exports.Version(out uint v) != SOk) return null;
        return ((int)(v >> 16), (int)((v >> 8) & 0xFF), (int)(v & 0xFF));
    }

    /// <summary>
    /// Opens an executor, or reports why it could not. Never throws for an absent runtime or
    /// a missing driver -- those are ordinary states on a machine that has not installed
    /// PawnIO, and the caller is expected to degrade to the ACPI path rather than fail.
    /// </summary>
    public static PawnIoAccess? TryOpen(out PawnIoStatus status)
    {
        var exports = TryLoadLibrary();
        if (exports is null)
        {
            status = PawnIoStatus.RuntimeNotInstalled;
            return null;
        }

        int hr = exports.Open(out IntPtr handle);
        if (hr == SOk && handle != IntPtr.Zero)
        {
            status = PawnIoStatus.Available;
            return new PawnIoAccess(handle);
        }

        // E_ACCESSDENIED specifically means "the driver is there, you are not elevated",
        // which is worth telling the user apart from "the driver is not running at all" --
        // one is fixed by relaunching, the other by installing.
        status = hr == EAccessDenied ? PawnIoStatus.AccessDenied : PawnIoStatus.DriverUnavailable;
        return null;
    }

    /// <summary>
    /// Loads a PawnIO bytecode module into this executor. One executor holds one module.
    /// Throws on failure: unlike a missing runtime, a module that will not load is a real
    /// fault (wrong architecture, corrupt blob) and should not be swallowed.
    /// </summary>
    public void LoadModule(byte[] blob)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(blob);
        if (blob.Length == 0) throw new ArgumentException("Module blob is empty.", nameof(blob));

        var exports = _exports ?? throw new InvalidOperationException("PawnIO library is not loaded.");
        int hr = exports.Load(_handle, blob, (nuint)blob.Length);
        if (hr != SOk)
            throw new InvalidOperationException($"pawnio_load failed with HRESULT 0x{hr:X8}.");

        IsModuleLoaded = true;
    }

    /// <summary>
    /// Executes a named function in the loaded module.
    ///
    /// in/out sizes are ELEMENT counts of 64-bit words, not byte counts -- that is what the
    /// header specifies ("Input buffer count"), and passing a byte count here would overrun
    /// the output buffer by a factor of eight.
    /// </summary>
    /// <returns>The number of output elements the module actually wrote.</returns>
    public int Execute(string name, ReadOnlySpan<ulong> input, Span<ulong> output)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var exports = _exports ?? throw new InvalidOperationException("PawnIO library is not loaded.");

        // The marshaller needs real arrays to pin. A zero-length array is legal but pins to
        // an address the driver must never dereference; since the count it is given is 0 it
        // will not, and allocating one spare element keeps that pointer valid regardless.
        var inputArray = new ulong[Math.Max(input.Length, 1)];
        input.CopyTo(inputArray);
        var outputArray = new ulong[Math.Max(output.Length, 1)];

        int hr;
        nuint returned;
        lock (_executeLock)
        {
            hr = exports.Execute(
                _handle, name,
                inputArray, (nuint)input.Length,
                outputArray, (nuint)output.Length,
                out returned);
        }

        if (hr != SOk)
            throw new InvalidOperationException($"pawnio_execute(\"{name}\") failed with HRESULT 0x{hr:X8}.");

        // Trust the driver's own count, but never past the buffer we supplied.
        int written = (int)Math.Min(returned, (nuint)output.Length);
        outputArray.AsSpan(0, written).CopyTo(output);
        return written;
    }

    /// <summary>
    /// Locates and loads PawnIOLib.dll once per process. Returns null when it is not
    /// installed, which is a supported state rather than an error.
    /// </summary>
    private static Exports? TryLoadLibrary()
    {
        lock (InitLock)
        {
            if (_triedLoad) return _exports;
            _triedLoad = true;

            string? path = ResolveLibraryPath();
            if (path is null) return null;

            try
            {
                IntPtr module = NativeLibrary.Load(path);
                _exports = new Exports(
                    Bind<VersionFn>(module, "pawnio_version"),
                    Bind<OpenFn>(module, "pawnio_open"),
                    Bind<LoadFn>(module, "pawnio_load"),
                    Bind<ExecuteFn>(module, "pawnio_execute"),
                    Bind<CloseFn>(module, "pawnio_close"));
                LibraryPath = path;
                return _exports;
            }
            catch
            {
                // A library that is present but missing an export is a version mismatch this
                // code was not written against. Reporting "unavailable" is honest; calling
                // into a half-bound export table is not.
                _exports = null;
                return null;
            }
        }
    }

    private static T Bind<T>(IntPtr module, string export) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(module, export));

    /// <summary>
    /// Finds PawnIOLib.dll. The installer records only an InstallLocation under the Uninstall
    /// key (verified on this machine: PawnIO 2.2.0.0 -> C:\Program Files\PawnIO) and does not
    /// touch PATH, so the registry is the authoritative source and the fixed path is a fallback.
    /// </summary>
    private static string? ResolveLibraryPath()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;

                foreach (string subKeyName in uninstall.GetSubKeyNames())
                {
                    using var sub = uninstall.OpenSubKey(subKeyName);
                    if (sub?.GetValue("DisplayName") is not string displayName) continue;
                    if (!displayName.Contains("PawnIO", StringComparison.OrdinalIgnoreCase)) continue;
                    if (sub.GetValue("InstallLocation") is not string location || location.Length == 0) continue;

                    string candidate = Path.Combine(location, LibraryName);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            catch { /* An unreadable hive just means this view yields no candidate. */ }
        }

        string fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", LibraryName);
        return File.Exists(fallback) ? fallback : null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Take the same lock Execute uses: disposing while another thread is mid-IOCTL would
        // close the handle out from under it.
        lock (_executeLock)
        {
            if (_handle != IntPtr.Zero)
            {
                try { _exports?.Close(_handle); } catch { /* nothing useful to do while tearing down */ }
                _handle = IntPtr.Zero;
            }
        }
        IsModuleLoaded = false;
    }
}
