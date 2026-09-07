namespace OmniHub.Core.Hardware;

// Constants for the documented HP "hpqBIntM" WMI BIOS interface
// (root\\wmi, instance ACPI\\PNP0C14\0_0). These identifiers are the
// public shape of HP's own BIOS interop surface — the same interface
// HP's Omen Gaming Hub and every open-source Omen utility (OmenMon,
// OmenHwCtl, omen-fan, etc.) talk to. Nothing here is proprietary code;
// it is the wire format needed to speak to hardware you own.
public static class BiosWmi
{
    public const string Namespace = "root\\wmi";
    public const string MethodClass = "hpqBIntM";
    public const string InstanceName = "ACPI\\PNP0C14\\0_0";

    // Each hpqBIOSIntN method's InData/OutData parameters are embedded WMI object
    // instances, not raw fields on the outer method-parameters object -- confirmed
    // via Get-CimClass introspection against a real Victus 15 fb2xxx.
    public const string InParamName = "InData";
    public const string OutParamName = "OutData";

    // hpqBDataIn: the embedded class that InData is an instance of.
    public const string InParamsClass = "hpqBDataIn";
    public const string SignField = "Sign";
    public const string CommandField = "Command";
    public const string CommandTypeField = "CommandType";
    public const string SizeField = "Size";
    public const string InDataField = "hpqBData";

    // hpqBDataOut{size}: the embedded class OutData is an instance of (field names
    // below are shared across the size-specific OutData classes, e.g. hpqBDataOut4).
    public const string OutDataField = "Data";
    public const string ReturnCodeField = "rwReturnCode";

    // Shared-secret signature HP's BIOS expects on every call
    public static readonly byte[] Signature = { 0x53, 0x45, 0x43, 0x55 }; // "SECU"
}

// Command group identifier (first DWORD of the call)
public enum BiosCmdGroup : uint
{
    Default = 0x20008,
    Keyboard = 0x20009,
    Legacy = 0x00001,
    GpuMode = 0x00002,
}

// Sub-command bytes within BiosCmdGroup.Default relevant to fan control.
// Values below are the documented command IDs used by the open-source
// Omen tooling ecosystem for the fan subsystem specifically.
public static class FanCmd
{
    public const byte GetFanCount = 0x10;
    public const byte GetFanType = 0x2C;
    public const byte GetFanLevel = 0x2D;
    public const byte SetFanLevel = 0x2E;
    public const byte SetFanMode = 0x1A;
    public const byte GetFanTable = 0x2F;
    public const byte SetFanTable = 0x32;
}

public enum FanMode : byte
{
    Default = 0x30,     // "Balanced" — the mode where fans-off-while-hot has been reported
    Performance = 0x31,
    Cool = 0x50,
    LegacyQuiet = 0x03,
}

/// <summary>
/// HP thermal policy, as encoded by the OmniControlSuite codebase, which drives the
/// SAME command this project's <see cref="FanCmd.SetFanMode"/> uses -- its
/// CallHpWmiBios(131080, 26, ...) is literally BiosCmdGroup.Default (0x20008) with
/// CommandType 0x1A. That much is independent confirmation our command IDs are right.
///
/// The PAYLOAD is where the two implementations disagree, and it matters:
///
///     OmniControl : { 0xFF, policy, 0x01, 0x00 }   policy = 0 Default / 1 Performance / 2 Quiet
///     OmniHub     : { 0xFF, mode,   0x00, 0x00 }   mode   = 0x30 Default / 0x31 Performance
///
/// Two differences: the mode byte itself, and byte[2] (0x01 vs 0x00, plausibly an
/// "apply" flag). Only one of these can be correct on this machine, and if it is
/// OmniControl's then this project's fan-mode switching has never actually taken
/// effect -- which would be a very good explanation for fans not behaving as commanded.
///
/// This is not resolvable by reading more code; it needs the hardware to answer. Both
/// encodings are therefore exposed and selectable (see FanController.SetThermalPolicy),
/// and the BIOS return code is checked so a rejected command reports as rejected
/// instead of appearing to succeed.
///
/// OmniControl also carries a field note worth testing directly, at VendorFanManager.cs:458:
/// "Avoid HP Quiet (255, 2, 1, 0) as it stops fans until 70C causing heat soak."
/// </summary>
public enum HpThermalPolicy : byte
{
    Default = 0x00,
    Performance = 0x01,
    Quiet = 0x02,
}

public enum FanType : byte
{
    Unsupported = 0x00,
    Cpu = 0x01,
    Gpu = 0x02,
    Exhaust = 0x03,
    Pump = 0x04,
    Intake = 0x05,
}

// Sub-commands for thermal/capability/idle queries under BiosCmdGroup.Default,
// plus the two commands that live under Legacy/GpuMode groups.
public static class SysCmd
{
    public const byte GetTemperature = 0x23;   // in [0x01,0,0,0], out 4B -> byte[0] = raw sensor value
    public const byte GetMaxFan = 0x26;
    public const byte SetMaxFan = 0x27;
    public const byte GetSystemData = 0x28;    // out 128B — SystemData (capability flags)
    public const byte SetIdle = 0x31;
    public const byte GetCapability = 0x35;    // in [0,0,0,0] -> overclock/undervolt support; in [0,4,0,0] -> throttling state
    public const byte SetCpuPower = 0x29;
    public const byte GetGpuPower = 0x21;
    public const byte SetGpuPower = 0x22;

    // These two live under different command groups, not Default
    public const byte GetGpuMode = 0x52;       // BiosCmdGroup.Legacy
    public const byte SetGpuMode = 0x52;       // BiosCmdGroup.GpuMode

    // Capability queries. Read-only, and they are what lets the app describe a board it has
    // never seen instead of inferring from a model string. Opcodes from OmenMon's BiosCtl,
    // which took them from HP's own Omen Gaming Hub -- not guessed here.
    public const byte GetAdapter = 0x0F;       // BiosCmdGroup.Legacy,   out 4B  -> HpAdapterStatus
    public const byte GetKeyboardType = 0x2B;  // BiosCmdGroup.Default,  out 4B  -> HpKeyboardType
    public const byte HasBacklight = 0x01;     // BiosCmdGroup.Keyboard, out 4B  -> byte[0] != 0x03
}

public enum IdleState : byte { Off = 0x00, On = 0x01 }

public enum GpuMode : byte { Hybrid = 0x00, Discrete = 0x01, Optimus = 0x02 }

public enum GpuPowerLevel : byte { Eco = 0x00, Balanced = 0x01, Performance = 0x02 }

public enum GpuCustomTgp : byte { Off = 0x00, On = 0x01 }
public enum GpuPpab : byte { Off = 0x00, On = 0x01 }
public enum GpuDState : byte { D1 = 0x01, D2 = 0x02, D3 = 0x03, D4 = 0x04, D5 = 0x05 }

public enum ThrottlingState : byte { Unknown = 0x00, On = 0x01, Default = 0x04 }

/// <summary>4-byte GPU power state, laid out exactly as the BIOS stores it.</summary>
public readonly struct GpuPowerData
{
    public readonly GpuCustomTgp CustomTgp;
    public readonly GpuPpab Ppab;
    public readonly GpuDState DState;
    public readonly byte PeakTemperatureC;

    public GpuPowerData(GpuCustomTgp customTgp, GpuPpab ppab, GpuDState dState, byte peakTempC)
    {
        CustomTgp = customTgp; Ppab = ppab; DState = dState; PeakTemperatureC = peakTempC;
    }

    public GpuPowerData(GpuPowerLevel level) : this(
        level == GpuPowerLevel.Eco ? GpuCustomTgp.Off : GpuCustomTgp.On,
        level == GpuPowerLevel.Performance ? GpuPpab.On : GpuPpab.Off,
        GpuDState.D1,
        0) { }

    public static GpuPowerData FromBytes(byte[] d) =>
        new((GpuCustomTgp)d[0], (GpuPpab)d[1], (GpuDState)d[2], d[3]);

    public byte[] ToBytes() => new[] { (byte)CustomTgp, (byte)Ppab, (byte)DState, PeakTemperatureC };

    public override string ToString() =>
        $"CustomTgp={CustomTgp}, Ppab={Ppab}, DState={DState}, PeakTemp={PeakTemperatureC}°C";
}

/// <summary>4-byte CPU power-limit state (watts, raw BIOS units observed 1:1 with watts on most SKUs).</summary>
public readonly struct CpuPowerData
{
    public readonly byte Limit1;      // PL1 sustained
    public readonly byte Limit2;      // matched to PL1 by convention
    public readonly byte Limit4;      // PL4 peak/boost
    public readonly byte LimitWithGpu; // concurrent CPU+GPU shared limit

    public CpuPowerData(byte limit1, byte limit2, byte limit4, byte limitWithGpu)
    {
        Limit1 = limit1; Limit2 = limit2; Limit4 = limit4; LimitWithGpu = limitWithGpu;
    }

    public byte[] ToBytes() => new[] { Limit1, Limit2, Limit4, LimitWithGpu };
}
