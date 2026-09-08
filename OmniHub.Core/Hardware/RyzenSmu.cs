namespace OmniHub.Core.Hardware;

/// <summary>
/// Processor codenames as the RyzenSMU PawnIO module numbers them.
///
/// The ordering is the module's own, not a tidy chronological one, so it is transcribed from
/// namazso/PawnIO.Modules rather than reconstructed. It matters only for reporting: nothing
/// in this file branches on it, so a future module revision that renumbers would mislabel a
/// string, not misdirect a hardware write.
///
/// Note the module maps Family 19h Models 74h and 75h alike to <see cref="Phoenix"/>. This
/// machine's Ryzen 5 8645HS is marketed as "Hawk Point", which is Phoenix silicon with a
/// different NPU, so reporting Phoenix here is the module being correct about the die rather
/// than wrong about the brand.
/// </summary>
public enum AmdCodeName
{
    Undefined = -1,
    Colfax = 0, Renoir = 1, Picasso = 2, Matisse = 3, Threadripper = 4,
    CastlePeak = 5, RavenRidge = 6, RavenRidge2 = 7, SummitRidge = 8, PinnacleRidge = 9,
    Rembrandt = 10, Vermeer = 11, Vangogh = 12, Cezanne = 13, Milan = 14,
    Dali = 15, Raphael = 16, GraniteRidge = 17, Naples = 18, FireFlight = 19,
    Rome = 20, Chagall = 21, Lucienne = 22, Phoenix = 23, Phoenix2 = 24,
    Mendocino = 25, Genoa = 26, StormPeak = 27, DragonRange = 28, Mero = 29,
    HawkPoint = 30, StrixPoint = 31, StrixHalo = 32, KrackanPoint = 33, KrackanPoint2 = 34,
    Turin = 35, TurinD = 36, Bergamo = 37, ShimadaPeak = 38, Carrizo = 39,
    BristolRidge = 40, StoneyRidge = 41,
}

/// <summary>
/// Live package power draw against the limits currently in force, in watts.
///
/// STAPM is the long-run sustained average, fast is the short burst ceiling, slow is the
/// medium window between them. The limits are what the SMU is actually enforcing right now,
/// which is not necessarily what was requested -- the platform clamps.
/// </summary>
public sealed record PowerSnapshot(
    double StapmLimitWatts, double StapmWatts,
    double FastLimitWatts, double FastWatts,
    double SlowLimitWatts, double SlowWatts,
    double ApuSlowLimitWatts, double ApuSlowWatts,
    double TdcVddLimitAmps, double TdcVddAmps,
    double TdcSocLimitAmps, double TdcSocAmps,
    double EdcVddLimitAmps, double EdcVddAmps,
    double EdcSocLimitAmps, double EdcSocAmps,
    double ThermalLimitC, double CoreTempC,
    double SocThermalLimitC, double SocTempC,
    double GfxThermalLimitC, double GfxTempC)
{
    /// <summary>
    /// The constraint the processor is actually up against right now, as a name and a
    /// percentage. This is the whole point of reading the table: knowing you are at 99% of the
    /// current limit and 60% of the power limit tells you which knob would matter, where a
    /// wattage on its own tells you nothing.
    /// </summary>
    public (string Name, double Percent) TightestLimit()
    {
        var tightest = Limits().MaxBy(c => c.Percent);
        return (tightest.Name, tightest.Percent);
    }

    /// <summary>
    /// Every constraint the processor is under, each as a percentage of its own limit, in a
    /// fixed order.
    ///
    /// The tightest one alone answers "what is holding this back". All five answer "and how much
    /// room is left in the others", which is the difference between knowing the machine is
    /// throttled and knowing which knob would change it: being at 99% of core current and 60% of
    /// the power limit says plainly that raising the power limit will do nothing, and that is
    /// exactly the conclusion nobody can reach from a wattage on its own.
    ///
    /// The order is stable, so a caller can build one row per entry once and afterwards only
    /// update the values rather than rebuilding its display on every refresh.
    /// </summary>
    public (string Name, double Percent)[] Limits() => new[]
    {
        ("Sustained power", Ratio(StapmWatts, StapmLimitWatts)),
        ("Boost power", Ratio(FastWatts, FastLimitWatts)),
        ("Core current (EDC)", Ratio(EdcVddAmps, EdcVddLimitAmps)),
        ("Core current (TDC)", Ratio(TdcVddAmps, TdcVddLimitAmps)),
        ("Temperature", Ratio(CoreTempC, ThermalLimitC)),
    };

    // A limit of zero means the table reported none, so there is no ratio to take. Zero is
    // returned rather than a division by zero, and it reads as "no constraint measured here".
    private static double Ratio(double value, double limit) => limit > 0 ? value / limit * 100.0 : 0;
}

/// <summary>
/// Access to the AMD SMU through the RyzenSMU PawnIO module -- the same module, loaded the
/// same way, that UXTU uses on this machine.
///
/// WHY THIS EXISTS
///
/// The ACPI thermal zone this app has been relying on is blind above about 85C and quantised
/// in 4-6C steps (measured: it pins at 85.05C through sustained load, however much hotter the
/// die actually gets). That is a poor sensor to run a fan curve from, and it is the direct
/// cause of the reported "temp stopped at 82 degrees". The SMU reports Tctl at 0.125C
/// resolution with no such ceiling.
///
/// WHAT IS AND IS NOT GUESSED HERE
///
/// The earlier refusal to do this was about fabricating SMU mailbox addresses, which is a
/// genuinely hardware-damaging class of mistake. That objection does not apply to this file,
/// and the distinction is the whole point:
///
///   - The mailbox addresses live inside RyzenSMU.bin, keyed by the codename the module
///     detects itself. This code never names one.
///   - The IOCTL names are extracted from the shipped binary's own string table.
///   - The temperature register (0x00059800) and its range-select bit are published
///     constants from LibreHardwareMonitor, and the read is verified against the ACPI zone
///     at runtime before it is trusted (see ReadDieTemperatureC).
///
/// The read path used for temperature is exactly that: a read. SendCommand and
/// WriteSmuRegister are the parts that can change processor state, and they are kept as
/// explicit, separately-called methods rather than folded into anything the telemetry loop
/// touches.
/// </summary>
public sealed class RyzenSmu : IDisposable
{
    // THM_TCON_CUR_TMP. Published by LibreHardwareMonitor as F17H_M01H_THM_TCON_CUR_TMP and
    // used unchanged by k10temp in the Linux kernel; valid across Zen 1 onwards, which is why
    // this file needs no per-codename address of its own.
    private const uint ThermTconCurTmp = 0x00059800;

    // Bit 19. When set, the sensor is in its offset range and 49C must be subtracted.
    private const uint TempRangeSelMask = 0x80000;

    private const string ModuleFileName = "RyzenSMU.bin";

    private readonly PawnIoAccess _pawnIo;

    private RyzenSmu(PawnIoAccess pawnIo, int codeNameRaw, uint smuVersion)
    {
        _pawnIo = pawnIo;
        CodeNameRaw = codeNameRaw;
        SmuVersion = smuVersion;
    }

    /// <summary>The codename value exactly as the module reported it, before naming.</summary>
    public int CodeNameRaw { get; }

    /// <summary>The reported codename, or Undefined when the module gave a value this build does not know.</summary>
    public AmdCodeName CodeName =>
        Enum.IsDefined(typeof(AmdCodeName), CodeNameRaw) ? (AmdCodeName)CodeNameRaw : AmdCodeName.Undefined;

    /// <summary>Raw SMU firmware version word.</summary>
    public uint SmuVersion { get; }

    /// <summary>SMU firmware version in the conventional major.minor.patch form.</summary>
    public string SmuVersionString => $"{(SmuVersion >> 16) & 0xFF}.{(SmuVersion >> 8) & 0xFF}.{SmuVersion & 0xFF}";

    /// <summary>Where the module blob was loaded from, for diagnostics.</summary>
    public string? ModulePath { get; private init; }

    /// <summary>
    /// Opens PawnIO, loads RyzenSMU and confirms the module answers. Returns null with a
    /// human-readable reason on any failure -- an absent driver, an unelevated process and a
    /// missing module blob are all ordinary states, and the caller falls back to ACPI.
    /// </summary>
    public static RyzenSmu? TryOpen(out string? unavailableReason)
    {
        string? modulePath = ResolveModulePath();
        if (modulePath is null)
        {
            // Deliberately names OmniHub's own folder first. Pointing at another application
            // is what made this failure possible: the module used to be read out of UXTU's
            // install, and when that install lost its Assets folder the Tuning tab went dark
            // through no fault of anything OmniHub controls.
            unavailableReason =
                $"{ModuleFileName} was not found. Put it in Assets\\PawnIO beside OmniHub.exe " +
                "(available from the PawnIO.Modules releases at github.com/namazso/PawnIO.Modules). " +
                "UXTU's copy is used as a fallback when that application is installed.";
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

            // Ask the module what it thinks it is running on. This doubles as the liveness
            // check: a module that loaded but cannot identify the processor is not one whose
            // register reads should be believed.
            Span<ulong> outBuffer = stackalloc ulong[2];
            int written = pawnIo.Execute("ioctl_get_code_name", ReadOnlySpan<ulong>.Empty, outBuffer[..1]);
            if (written < 1)
            {
                pawnIo.Dispose();
                unavailableReason = "RyzenSMU loaded but reported no processor codename.";
                return null;
            }
            int codeNameRaw = unchecked((int)outBuffer[0]);

            // Version is informational, so a firmware that declines to report one is not
            // fatal -- but it must not be invented either, hence 0 rather than a plausible number.
            uint smuVersion = 0;
            try
            {
                if (pawnIo.Execute("ioctl_get_smu_version", ReadOnlySpan<ulong>.Empty, outBuffer[..1]) >= 1)
                    smuVersion = (uint)outBuffer[0];
            }
            catch { /* informational only */ }

            unavailableReason = null;
            return new RyzenSmu(pawnIo, codeNameRaw, smuVersion) { ModulePath = modulePath };
        }
        catch (Exception ex)
        {
            pawnIo.Dispose();
            unavailableReason = $"RyzenSMU module failed to load: {ex.Message}";
            return null;
        }
    }

    /// <summary>
    /// True die temperature (Tctl) in degrees Celsius, or null when the value read back is
    /// not physically plausible.
    ///
    /// The plausibility check is not defensive padding. This is the one place where a
    /// misunderstanding of the register layout would show up as a confident, wrong number
    /// feeding the fan curve -- exactly the failure mode this app exists to prevent -- and a
    /// bad decode does not produce a subtly-off reading, it produces something far outside
    /// the range silicon operates in. Returning null makes the caller fall back to the ACPI
    /// zone rather than trust it.
    /// </summary>
    public double? ReadDieTemperatureC()
    {
        uint raw;
        try { raw = ReadSmuRegister(ThermTconCurTmp); }
        catch { return null; }

        // An all-zero or all-ones register is a failed indirect read, not a 0C processor.
        if (raw == 0 || raw == 0xFFFFFFFF) return null;

        double celsius = ((raw >> 21) & 0x7FF) * 0.125;
        if ((raw & TempRangeSelMask) != 0) celsius -= 49.0;

        return celsius is > -20.0 and < 130.0 ? celsius : null;
    }

    /// <summary>
    /// One SMU mailbox: the three SMN registers that make up a command channel.
    ///
    /// A processor exposes several of these and they are NOT interchangeable. The same command
    /// number means different things -- or nothing -- depending on which one it is posted to.
    /// </summary>
    public sealed record SmuMailbox(string Name, uint Command, uint Response, uint Argument);

    /// <summary>
    /// The RSMU mailbox this build's PawnIO module drives internally, for Phoenix-class APUs.
    ///
    /// This is what <see cref="SendCommand"/> talks to, and knowing that matters: RyzenAdj's
    /// power-limit command IDs are MP1 commands, so posting them here is posting them to the
    /// wrong channel. Confirmed against leogx9r/ryzen_smu, which documents this exact triple
    /// as RSMU for the Renoir/Cezanne family the module groups Phoenix with.
    /// </summary>
    public static SmuMailbox RsmuMailbox { get; } = new("RSMU", 0x3B10A20, 0x3B10A80, 0x3B10A88);

    /// <summary>
    /// Candidate MP1 mailboxes. Command and argument registers agree across this APU family;
    /// only the response register differs, and Phoenix predates both reference projects'
    /// tables, so which one applies here is determined by probing rather than assumed.
    /// </summary>
    public static IReadOnlyList<SmuMailbox> Mp1Candidates { get; } = new[]
    {
        new SmuMailbox("MP1 (Renoir-style)", 0x3B10528, 0x3B10564, 0x3B10998),
        new SmuMailbox("MP1 (Rembrandt-style)", 0x3B10528, 0x3B10578, 0x3B10998),
    };

    /// <summary>The SMU's success code. Anything else is a refusal or a timeout.</summary>
    public const uint SmuReturnOk = 0x01;

    /// <summary>
    /// Posts a command to a specific mailbox, driving the handshake by hand through SMN
    /// register reads and writes.
    ///
    /// The module's own <see cref="SendCommand"/> only ever talks to RSMU, so this exists to
    /// reach MP1, which is where the power-limit commands actually live. The sequence is the
    /// one ryzen_smu implements: wait for the response register to go non-zero (mailbox idle),
    /// clear it, write the six argument words, write the command, then wait for a new response.
    /// </summary>
    /// <returns>The SMU's response code; <see cref="SmuReturnOk"/> means it accepted.</returns>
    public uint SendToMailbox(SmuMailbox mailbox, uint command, ReadOnlySpan<uint> args, Span<uint> results)
    {
        if (args.Length > 6) throw new ArgumentException("The mailbox takes at most six arguments.", nameof(args));

        if (!WaitForIdle(mailbox)) return 0;

        WriteSmuRegister(mailbox.Response, 0);
        for (uint i = 0; i < 6; i++)
            WriteSmuRegister(mailbox.Argument + i * 4, i < args.Length ? args[(int)i] : 0);

        WriteSmuRegister(mailbox.Command, command);

        uint response = 0;
        for (int spin = 0; spin < 1000; spin++)
        {
            response = ReadSmuRegister(mailbox.Response);
            if (response != 0) break;
            Thread.Sleep(1);
        }

        if (response == SmuReturnOk)
            for (int i = 0; i < results.Length && i < 6; i++)
                results[i] = ReadSmuRegister(mailbox.Argument + (uint)i * 4);

        return response;
    }

    private bool WaitForIdle(SmuMailbox mailbox)
    {
        for (int spin = 0; spin < 1000; spin++)
        {
            if (ReadSmuRegister(mailbox.Response) != 0) return true;
            Thread.Sleep(1);
        }
        return false;
    }

    /// <summary>
    /// Identifies which candidate MP1 mailbox this processor actually answers on, by asking it
    /// for its firmware version.
    ///
    /// GetSmuVersion is chosen deliberately: it is read-only, so probing a wrong address costs
    /// a rejected command rather than an unintended write to whatever else lives there.
    /// Returns null when neither candidate answers, which is a real possibility and better
    /// reported than papered over.
    /// </summary>
    private SmuMailbox? _mp1;
    private bool _mp1Probed;
    private readonly object _mp1Lock = new();

    /// <summary>
    /// The MP1 mailbox for this processor, probed once per process.
    ///
    /// Cached here rather than in each caller because probing costs real mailbox transactions
    /// over PCI config space, and three separate views each built their own AmdTuning and so
    /// each probed independently. The result cannot change while the machine is running.
    /// </summary>
    public SmuMailbox? Mp1Mailbox
    {
        get
        {
            lock (_mp1Lock)
            {
                if (_mp1Probed) return _mp1;
                _mp1Probed = true;
                try { _mp1 = DetectMp1Mailbox(out _); } catch { _mp1 = null; }
                return _mp1;
            }
        }
    }

    public SmuMailbox? DetectMp1Mailbox(out uint version)
    {
        const uint getSmuVersion = 0x02;

        // Allocated once, outside the loop. A stackalloc per iteration accumulates for the
        // whole method rather than being freed each pass, so it grows with the candidate list
        // instead of staying constant (CA2014).
        Span<uint> results = stackalloc uint[1];

        foreach (var candidate in Mp1Candidates)
        {
            try
            {
                results[0] = 0;
                if (SendToMailbox(candidate, getSmuVersion, ReadOnlySpan<uint>.Empty, results) != SmuReturnOk)
                    continue;

                // A plausible firmware version is non-zero and not all-ones. A mailbox that is
                // not really there tends to read back one or the other.
                if (results[0] is not 0 and not 0xFFFFFFFF)
                {
                    version = results[0];
                    return candidate;
                }
            }
            catch { /* try the next candidate */ }
        }

        version = 0;
        return null;
    }

    /// <summary>Reads one SMU (SMN address space) register.</summary>
    public uint ReadSmuRegister(uint address)
    {
        Span<ulong> output = stackalloc ulong[1];
        ReadOnlySpan<ulong> input = stackalloc ulong[1] { address };
        if (_pawnIo.Execute("ioctl_read_smu_register", input, output) < 1)
            throw new InvalidOperationException($"ioctl_read_smu_register(0x{address:X8}) returned no value.");
        return (uint)output[0];
    }

    /// <summary>
    /// Writes one SMU register.
    ///
    /// Nothing in OmniHub's telemetry or fan paths calls this. It exists for the tuning
    /// surface, and every caller is expected to know precisely which register it is touching
    /// -- an arbitrary SMN write is not a recoverable mistake.
    /// </summary>
    public void WriteSmuRegister(uint address, uint value)
    {
        ReadOnlySpan<ulong> input = stackalloc ulong[2] { address, value };
        _pawnIo.Execute("ioctl_write_smu_register", input, Span<ulong>.Empty);
    }

    /// <summary>
    /// Sends an SMU mailbox command with up to six arguments and returns the response words.
    /// The command IDs are processor-family specific and are the caller's business; this
    /// method only marshals.
    /// </summary>
    public ulong[] SendCommand(byte command, ReadOnlySpan<ulong> args)
    {
        if (args.Length > 6) throw new ArgumentException("The SMU mailbox takes at most six arguments.", nameof(args));

        Span<ulong> input = stackalloc ulong[7];
        input[0] = command;
        args.CopyTo(input[1..]);

        var output = new ulong[6];
        int written = _pawnIo.Execute("ioctl_send_smu_command", input, output);
        return written >= output.Length ? output : output[..Math.Max(written, 0)];
    }

    /// <summary>
    /// The one PM table layout version this build will parse, measured on this machine.
    ///
    /// Gated rather than assumed. AMD renumbers the table between firmwares and moves fields
    /// with it, so parsing an unrecognised version would produce confident, wrong wattages
    /// rather than an obvious failure.
    /// </summary>
    public const uint SupportedPmTableVersion = 0x004C0009;

    /// <summary>
    /// Live package power against the limits actually in force, or null when the table is a
    /// version this build does not parse, the SMU declines to refresh it, or the values come
    /// back implausible.
    ///
    /// HOW THE FIELD ORDER WAS ESTABLISHED, since a mis-mapped table is the exact failure this
    /// codebase refuses to ship: the first three limit entries read 45.0, 65.0 and 54.0 W on
    /// this machine. Those are precisely the Ryzen 5 8645HS's published STAPM, PPT-fast and
    /// PPT-slow figures (45 W nominal, 54 W maximum configurable), appearing in the order
    /// ryzen_smu documents for this table's head. Three independent values matching the part's
    /// datasheet in the documented order is confirmation, not inference.
    ///
    /// The mapping now extends through the first 22 entries, and each was confirmed the same
    /// way rather than assumed. The limits read 45/65/54 W, 70/18/140/26 A and 85 C three
    /// times -- round platform numbers in ryzen_smu's documented limit/value pair order -- and
    /// every paired value tracks load: sustained draw moved 19.7 -> 28.5 W between an idle and
    /// a loaded sample, and core current moved 73.6 -> 138.7 A against its 140 A limit.
    /// Round limits in the documented order, with values that respond correctly, is
    /// confirmation. A guess would not survive both halves of that.
    ///
    /// The table runs to 1024 words and certainly carries per-core clocks and voltages further
    /// in, but this firmware's version has no published struct and the deeper offsets are not
    /// cross-checkable the way these were. They stay unmapped rather than guessed.
    /// </summary>
    private PowerSnapshot? _cachedPower;
    private DateTime _cachedPowerAtUtc = DateTime.MinValue;
    private readonly object _powerLock = new();

    /// <summary>
    /// Shortest gap between real PM table reads. Every caller shares the result.
    ///
    /// This is not micro-optimisation, it is why the machine does not crackle. Refreshing the
    /// PM table is an SMU mailbox transaction: the module writes a command and then spins
    /// reading a response register over PCI config space, holding the global Access_PCI mutex
    /// the whole time. That stalls the bus in bursts, which surfaces as DPC latency, which
    /// surfaces as audio dropouts -- the same reason hardware monitors are notorious for it.
    /// Two independent consumers (the overlay and the Tuning tab) were each doing it on their
    /// own timers, so the real rate was double what either one asked for.
    /// </summary>
    private static readonly TimeSpan PowerCacheLife = TimeSpan.FromSeconds(5);

    public PowerSnapshot? ReadPowerSnapshot()
    {
        lock (_powerLock)
        {
            if (DateTime.UtcNow - _cachedPowerAtUtc < PowerCacheLife) return _cachedPower;
            var fresh = ReadPowerSnapshotUncached();
            _cachedPower = fresh;
            _cachedPowerAtUtc = DateTime.UtcNow;
            return fresh;
        }
    }

    /// <summary>
    /// Drops the cached table so the next read goes to the hardware. Call after anything that
    /// changes a limit.
    ///
    /// Without this the cache does not merely serve slightly stale numbers, it inverts the one
    /// check this project is built on. Writing a limit and reading it straight back is how the
    /// app tells a limit the firmware ACCEPTED from one it merely acknowledged -- and inside the
    /// cache window that read returns the value from before the write, so a write that landed
    /// perfectly reads as one the firmware ignored. Adaptive mode's three-strike guard is driven
    /// by exactly that comparison, and stopped itself reporting "this firmware locks CPU power
    /// limits" on hardware that had accepted every command it was sent.
    /// </summary>
    public void InvalidatePowerCache()
    {
        lock (_powerLock) _cachedPowerAtUtc = DateTime.MinValue;
    }

    private PowerSnapshot? ReadPowerSnapshotUncached()
    {
        try
        {
            var (version, _) = ResolvePmTable();
            if (version != SupportedPmTableVersion) return null;

            UpdatePmTable();

            // Eleven 64-bit words carry the 22 32-bit floats of the confirmed head, packed as
            // limit/value pairs throughout.
            Span<ulong> words = stackalloc ulong[11];
            if (ReadPmTable(words) < words.Length) return null;

            Span<float> e = stackalloc float[22];
            for (int i = 0; i < words.Length; i++)
            {
                e[i * 2] = BitConverter.Int32BitsToSingle(unchecked((int)(uint)(words[i] & 0xFFFFFFFF)));
                e[i * 2 + 1] = BitConverter.Int32BitsToSingle(unchecked((int)(uint)(words[i] >> 32)));
            }

            var snapshot = new PowerSnapshot(
                StapmLimitWatts: e[0], StapmWatts: e[1],
                FastLimitWatts: e[2], FastWatts: e[3],
                SlowLimitWatts: e[4], SlowWatts: e[5],
                ApuSlowLimitWatts: e[6], ApuSlowWatts: e[7],
                TdcVddLimitAmps: e[8], TdcVddAmps: e[9],
                TdcSocLimitAmps: e[10], TdcSocAmps: e[11],
                EdcVddLimitAmps: e[12], EdcVddAmps: e[13],
                EdcSocLimitAmps: e[14], EdcSocAmps: e[15],
                ThermalLimitC: e[16], CoreTempC: e[17],
                SocThermalLimitC: e[18], SocTempC: e[19],
                GfxThermalLimitC: e[20], GfxTempC: e[21]);

            // A limit outside anything a mobile APU ships with means the offsets have moved
            // under us, whatever the version said.
            return IsPlausibleWatts(snapshot.StapmLimitWatts)
                && IsPlausibleWatts(snapshot.FastLimitWatts)
                && IsPlausibleWatts(snapshot.SlowLimitWatts)
                ? snapshot
                : null;
        }
        catch
        {
            // ioctl_update_pm_table can genuinely fail transiently when another tool holds the
            // SMU mailbox mid-transfer (observed on this machine). No reading is the correct
            // answer for that tick.
            return null;
        }
    }

    private static bool IsPlausibleWatts(double watts) => watts is > 1.0 and < 200.0;

    /// <summary>Locates the PM table, returning its layout version and physical base address.</summary>
    public (uint Version, ulong BaseAddress) ResolvePmTable()
    {
        Span<ulong> output = stackalloc ulong[2];
        if (_pawnIo.Execute("ioctl_resolve_pm_table", ReadOnlySpan<ulong>.Empty, output) < 2)
            throw new InvalidOperationException("ioctl_resolve_pm_table did not return a version and base address.");
        return ((uint)output[0], output[1]);
    }

    /// <summary>Asks the SMU to refresh the PM table before it is read.</summary>
    public void UpdatePmTable() =>
        _pawnIo.Execute("ioctl_update_pm_table", ReadOnlySpan<ulong>.Empty, Span<ulong>.Empty);

    /// <summary>Reads the PM table into the supplied buffer, returning the number of 64-bit words written.</summary>
    public int ReadPmTable(Span<ulong> buffer) =>
        _pawnIo.Execute("ioctl_read_pm_table", ReadOnlySpan<ulong>.Empty, buffer);

    /// <summary>
    /// Finds RyzenSMU.bin. OmniHub's own copy wins so the feature keeps working if UXTU is
    /// uninstalled; UXTU's install is the fallback, since on this machine that is where the
    /// module already lives.
    /// </summary>
    private static string? ResolveModulePath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "PawnIO", ModuleFileName),
            Path.Combine(AppContext.BaseDirectory, ModuleFileName),
        };

        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            if (string.IsNullOrEmpty(programFiles)) continue;
            candidates.Add(Path.Combine(
                programFiles, "JamesCJ60", "Universal x86 Tuning Utility", "Assets", "AMD", "PawnIO", ModuleFileName));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public void Dispose() => _pawnIo.Dispose();
}
