using System.Management;

namespace OmniHub.Core.Hardware;

public sealed class BiosInterop : IDisposable
{
    // Not readonly, and nullable: on a machine without HP's interface these are never
    // assigned, and that is a supported state rather than a failure.
    private ManagementScope? _scope;
    private ManagementObject? _wmiInstance;

    // Send() is called concurrently from two independent loops -- HardwareContext's poll
    // timer (GetFanLevel / GetMaxFanActive / GetThrottling) and FanService's curve loop
    // (SetFanLevel) -- and both share this one instance. ManagementObject is not
    // thread-safe: GetMethodParameters and InvokeMethod against the same instance from two
    // threads can interleave, and the observable result is a call returning another call's
    // data, or throwing outright. That race is live every 2 seconds, and it is a very
    // plausible source of fan levels and throttle flags that look wrong on screen.
    private readonly object _sendLock = new();

    /// <summary>
    /// False when this machine does not expose HP's WMI control interface -- i.e. on every
    /// laptop that is not an HP, and on HP models that ship without it.
    ///
    /// Absence is an ordinary state, not a failure. This constructor used to let the WMI
    /// connect and instance fetch throw, and HardwareContext constructs it as its very first
    /// statement, so on a non-HP machine OmniHub did not merely lose fan control -- it failed
    /// to start at all. Everything that does NOT depend on the vendor interface (die and
    /// thermal-zone temperatures, CPU load and clocks, memory, battery, and the Windows-side
    /// power plan and scheduling controls) works without it, and now still runs.
    /// </summary>
    public bool IsAvailable { get; }

    /// <summary>Why the vendor interface is unavailable, in words fit to show a user.</summary>
    public string? UnavailableReason { get; }

    public BiosInterop()
    {
        try
        {
            Initialise();
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            // Described by what it means to the user rather than by the WMI error, which is
            // "Not found" and explains nothing.
            UnavailableReason =
                "This machine does not expose HP's WMI control interface, so fan control, GPU "
                + "power and BIOS power limits are unavailable. Temperatures, load, battery and "
                + $"the Windows-side controls still work. ({ex.GetType().Name})";
            IsAvailable = false;
        }
    }

    private void Initialise()
    {
        _scope = new ManagementScope(BiosWmi.Namespace);
        _scope.Connect();
        var scope = _scope;

        // WMI relative-path syntax double-escapes backslashes inside a quoted key
        // value (confirmed against the real __RELPATH: InstanceName="ACPI\\PNP0C14\\0_0")
        // -- that's on top of BiosWmi.InstanceName already being the plain single-
        // backslash value, so it must be re-escaped here rather than embedded as-is.
        var escapedInstanceName = BiosWmi.InstanceName.Replace("\\", "\\\\");
        var path = new ManagementPath($"{BiosWmi.MethodClass}.InstanceName=\"{escapedInstanceName}\"");
        _wmiInstance = new ManagementObject(scope, path, null);
        _wmiInstance.Get();

        // Fetched once, here, rather than on every Send().
        //
        // Constructing a ManagementClass from a path is not a local operation: it fetches the
        // class DEFINITION from the WMI service, which is a DCOM round trip with its own
        // marshalling and authentication. SendLocked used to do that on every call, and Send
        // runs two to three times a second between the poll loop and the fan curve, so the app
        // was paying for a schema lookup per BIOS command all day. The definition of
        // hpqBDataIn cannot change while the machine is running, so once is enough.
        _inDataClass = new ManagementClass(_scope, new ManagementPath(BiosWmi.InParamsClass), null);
        _inDataClass.Get();
    }

    private ManagementClass? _inDataClass;

    public byte[] Send(BiosCmdGroup group, byte commandId, byte[]? inData, int outSize)
    {
        inData ??= new byte[4];
        if (inData.Length != 4)
        {
            var padded = new byte[4];
            Array.Copy(inData, padded, Math.Min(4, inData.Length));
            inData = padded;
        }

        // HP's hpqBIntM class does not expose one generic "hpqBIOSInt" method -- it
        // exposes several, one per response-buffer size (hpqBIOSInt0/4/128/1024/4096),
        // confirmed directly against a real Victus 15 fb2xxx via WMI class introspection.
        // Each method's InData/OutData parameters are themselves embedded WMI object
        // instances (classes hpqBDataIn / hpqBDataOut{size}), not raw byte-array fields
        // on the outer parameters object -- also confirmed via that same introspection.
        var methodName = MethodNameFor(outSize);
        // One clear refusal rather than a NullReferenceException from deep inside WMI. Every
        // caller of Send already treats a throw as "this control is not available here", so a
        // non-HP machine degrades along paths that already exist.
        if (!IsAvailable)
            throw new NotSupportedException(UnavailableReason ?? "The vendor control interface is unavailable.");

        lock (_sendLock)
        {
            return SendLocked(methodName, group, commandId, inData, outSize);
        }
    }

    /// <summary>
    /// Sends a command with NO input payload: Size is set to 0 and hpqBData is left unset.
    ///
    /// Deliberately a separate entry point rather than a new meaning for Send's null, because
    /// null already means something here -- it pads to a four-byte zero buffer, and the fan
    /// commands depend on that. GetFanCount, GetFanType, GetFanLevel and GetFanTable all pass
    /// null and all work, and OmenMon sends those same commands with an explicit four-byte
    /// buffer, so the padding is right for them.
    ///
    /// A few commands want the other thing. The system design data query (0x28) is one: asked
    /// with a four-byte payload it answers, but with a degenerate buffer -- every capability
    /// byte zero -- which decodes into a confident claim that the machine supports nothing.
    /// Asked with no payload at all, the way HP's own software asks, it answers properly.
    /// </summary>
    public byte[] SendWithoutPayload(BiosCmdGroup group, byte commandId, int outSize)
    {
        if (!IsAvailable)
            throw new NotSupportedException(UnavailableReason ?? "The vendor control interface is unavailable.");

        lock (_sendLock)
        {
            return SendLocked(MethodNameFor(outSize), group, commandId, null, outSize);
        }
    }

    private byte[] SendLocked(string methodName, BiosCmdGroup group, byte commandId, byte[]? inData, int outSize)
    {
        // Non-null by construction: Send refuses before reaching here unless IsAvailable, and
        // IsAvailable is only set once Initialise has assigned both of these.
        using var inParams = _wmiInstance!.GetMethodParameters(methodName);

        // CreateInstance on the cached class is local: the schema is already in hand, so this
        // allocates an instance rather than asking WMI for anything. The instance is still
        // per-call and still disposed; only the class definition is shared.
        using var inDataInstance = _inDataClass!.CreateInstance();
        inDataInstance[BiosWmi.SignField] = BiosWmi.Signature;
        inDataInstance[BiosWmi.CommandField] = (uint)group;
        inDataInstance[BiosWmi.CommandTypeField] = (uint)commandId;
        // Null is the no-payload call: Size 0, and hpqBData left entirely unset rather than set
        // to an empty array. Setting the field at all is what the BIOS treats as "there is a
        // payload", so writing an empty buffer is not the same request as omitting it.
        if (inData is null)
        {
            inDataInstance[BiosWmi.SizeField] = 0u;
        }
        else
        {
            inDataInstance[BiosWmi.SizeField] = (uint)inData.Length;
            inDataInstance[BiosWmi.InDataField] = inData;
        }

        inParams[BiosWmi.InParamName] = inDataInstance;

        using var outParams = _wmiInstance.InvokeMethod(methodName, inParams, null);
        if (outParams is null)
            throw new InvalidOperationException("BIOS call returned no result -- check Administrator privileges.");

        // NOT disposed deliberately. This is an embedded property value owned by
        // outParams, not an independently-owned object -- adding a `using` here
        // double-releases the underlying COM wrapper (outParams releases it too),
        // which killed the process silently a few seconds after startup, with no
        // managed exception and no Windows Error Reporting entry. Send() runs every
        // 2s from two loops, so it reproduced within seconds every launch.
        var outData = (ManagementBaseObject)outParams[BiosWmi.OutParamName];
        var returnCode = (uint)outData[BiosWmi.ReturnCodeField];
        if (returnCode != 0)
            throw new InvalidOperationException($"BIOS call failed (0x{returnCode:X}) -- group={group}, cmd=0x{commandId:X2}");

        var raw = (byte[])outData[BiosWmi.OutDataField];
        var result = new byte[outSize];
        Array.Copy(raw, 0, result, 0, Math.Min(outSize, raw.Length));
        return result;
    }

    private static string MethodNameFor(int outSize) => outSize switch
    {
        0 => "hpqBIOSInt0",
        4 => "hpqBIOSInt4",
        128 => "hpqBIOSInt128",
        1024 => "hpqBIOSInt1024",
        4096 => "hpqBIOSInt4096",
        _ => throw new ArgumentOutOfRangeException(nameof(outSize), outSize,
            "HP's WMI interface only exposes hpqBIOSInt{0,4,128,1024,4096} -- no method for this response size."),
    };

    public void Dispose()
    {
        // Null-conditional: on a machine without the vendor interface these were never
        // created, and disposing is still a perfectly ordinary thing for a caller to do.
        _inDataClass?.Dispose();
        _wmiInstance?.Dispose();
    }
}
