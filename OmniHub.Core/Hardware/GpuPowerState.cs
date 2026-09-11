using System.Runtime.InteropServices;

namespace OmniHub.Core.Hardware;

/// <summary>The PCI power state of a device, as Windows believes it to be.</summary>
public enum DevicePowerState
{
    /// <summary>Windows did not report one.</summary>
    Unknown = 0,

    /// <summary>Fully powered. For a discrete GPU on battery, this is the expensive answer.</summary>
    D0 = 1,
    D1 = 2,
    D2 = 3,

    /// <summary>Powered down. What a discrete laptop GPU should be doing when nothing needs it.</summary>
    D3 = 4,
}

/// <summary>
/// Reads a device's current PCI power state WITHOUT talking to the device.
///
/// This exists because the obvious instrument lies. Asking nvidia-smi what the discrete GPU is
/// doing can wake the card to answer, so a sleeping GPU reports an awake one and the act of
/// checking destroys what was being checked. Measured on this machine: a card sitting in D3
/// answered a single nvidia-smi query with "P4, 10.99 W", which reads as a GPU that never
/// sleeps and is in fact a GPU being woken up to be asked.
///
/// The configuration manager holds the answer instead. PD_MostRecentPowerState is tracked by
/// the PCI bus driver on the host side, so reading it is a lookup in Windows' own bookkeeping:
/// no PCIe transaction, no wake, no observer effect. It is the only honest way to tell someone
/// whether the power saving they were promised is actually happening.
/// </summary>
public static class GpuPowerState
{
    // CM_Get_DevNode_Property with DEVPKEY_Device_PowerData returns a CM_POWER_DATA struct.
    // Only the second field is wanted here; the rest describes latencies and capabilities.
    //
    //   DWORD              PD_Size                 offset 0
    //   DEVICE_POWER_STATE PD_MostRecentPowerState offset 4   <- this one
    //   DWORD              PD_Capabilities         offset 8
    //   ... latencies, state mapping, deepest wake
    private const int MostRecentPowerStateOffset = 4;
    private const int PowerDataMinimumBytes = 8;

    private const int CR_SUCCESS = 0;
    private const int CR_BUFFER_SMALL = 0x1A;

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid Fmtid;
        public uint Pid;
    }

    // DEVPKEY_Device_PowerData: {AFD97640-86A3-4210-B67C-E4A00A8B2CC3}, 14
    private static DevPropKey PowerDataKey = new()
    {
        Fmtid = new Guid("afd97640-86a3-4210-b67c-e4a00a8b2cc3"),
        Pid = 14,
    };

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(
        uint devInst, ref DevPropKey propertyKey, out uint propertyType,
        byte[]? propertyBuffer, ref uint propertyBufferSize, uint flags);

    /// <summary>
    /// The discrete NVIDIA GPU's power state, or Unknown when there is not one.
    ///
    /// The device path is found once and cached for the life of the process: it is a WMI
    /// query, and this is read on a timer, so repeating it would be the same mistake as the
    /// adapter enumeration that used to run every three seconds on the one code path battery
    /// mode leaves open.
    ///
    /// Deliberately NVIDIA-only. VEN_10DE is the one vendor whose laptop parts this project
    /// has actually measured, and picking "the discrete one" generically means guessing which
    /// adapter is which on hardware nobody here owns.
    /// </summary>
    public static DevicePowerState ReadDiscrete() => Read(NvidiaDevicePath.Value);

    private static readonly Lazy<string?> NvidiaDevicePath = new(FindNvidia,
        System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private static string? FindNvidia()
    {
        try
        {
            using var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT PNPDeviceID FROM Win32_VideoController");
            foreach (var mo in searcher.Get())
            {
                using (mo)
                {
                    string id = mo["PNPDeviceID"]?.ToString() ?? "";
                    if (id.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase)) return id;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// The device's current power state, or Unknown when it could not be read.
    ///
    /// Unknown is a real answer and is never dressed up as D0 or D3: a guess here would be a
    /// guess about the single thing this class exists to state honestly.
    /// </summary>
    /// <param name="pnpDeviceId">
    /// The device instance path, as Win32_VideoController.PNPDeviceID reports it, for example
    /// PCI\VEN_10DE&amp;DEV_28E0&amp;SUBSYS_8C2F103C&amp;REV_A1\4&amp;1234abcd&amp;0&amp;0008.
    /// </param>
    public static DevicePowerState Read(string? pnpDeviceId)
    {
        if (string.IsNullOrWhiteSpace(pnpDeviceId)) return DevicePowerState.Unknown;

        try
        {
            if (CM_Locate_DevNodeW(out uint devInst, pnpDeviceId, 0) != CR_SUCCESS)
                return DevicePowerState.Unknown;

            // Ask for the size first. CM_POWER_DATA has grown across Windows versions, so the
            // length comes from the API rather than from a hardcoded sizeof.
            uint size = 0;
            int r = CM_Get_DevNode_PropertyW(devInst, ref PowerDataKey, out _, null, ref size, 0);
            if (r != CR_BUFFER_SMALL || size < PowerDataMinimumBytes) return DevicePowerState.Unknown;

            var buffer = new byte[size];
            if (CM_Get_DevNode_PropertyW(devInst, ref PowerDataKey, out _, buffer, ref size, 0) != CR_SUCCESS)
                return DevicePowerState.Unknown;

            uint state = BitConverter.ToUInt32(buffer, MostRecentPowerStateOffset);
            return state is >= 1 and <= 4 ? (DevicePowerState)state : DevicePowerState.Unknown;
        }
        catch (DllNotFoundException) { return DevicePowerState.Unknown; }
        catch (EntryPointNotFoundException) { return DevicePowerState.Unknown; }
    }

    /// <summary>
    /// How to put the state to a reader.
    ///
    /// "asleep" rather than bare "D3", because the point of showing this is to answer "is the
    /// power saving working", and a reader who knows what D3 means still reads "asleep"
    /// correctly. The raw state stays alongside it so the precise answer is never hidden.
    /// </summary>
    public static string Describe(DevicePowerState state) => state switch
    {
        DevicePowerState.D0 => "awake (D0)",
        DevicePowerState.D1 => "low power (D1)",
        DevicePowerState.D2 => "low power (D2)",
        DevicePowerState.D3 => "asleep (D3)",
        _ => "unavailable",
    };
}
