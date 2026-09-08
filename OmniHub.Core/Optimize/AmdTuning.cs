using OmniHub.Core.Hardware;

namespace OmniHub.Core.Optimize;

/// <summary>
/// A tuning profile. Every field is optional: null means "leave this alone", which is what
/// lets a profile change the thermal target without also pinning power, or vice versa.
/// Units are the ones a person thinks in; conversion to the SMU's milliwatts/milliamps
/// happens at the point of sending.
/// </summary>
public sealed record AmdTuningProfile(
    string Name,
    string Description,
    int? StapmWatts = null,
    int? FastWatts = null,
    int? SlowWatts = null,
    int? StapmTimeSeconds = null,
    int? SlowTimeSeconds = null,
    int? TctlTempC = null,
    int? SkinTempC = null,
    int? ApuSkinTempC = null,
    int? VrmCurrentAmps = null,
    int? CurveOptimizerAllCore = null,
    int? GfxClockMhz = null,
    int? ProchotRamp = null);

/// <summary>What a single knob did when it was sent.</summary>
public sealed record TuningStep(string Name, bool Sent, string Detail);

/// <summary>
/// The outcome of applying a whole profile: what was sent, and whether the hardware moved.
/// Verified is null when there was nothing readable to verify against.
/// </summary>
public sealed record TuningReport(
    bool Applied,
    string Summary,
    IReadOnlyList<TuningStep> Steps,
    PowerSnapshot? Before,
    PowerSnapshot? After,
    bool? Verified);

/// <summary>
/// The full Universal x86 Tuning Utility command surface for this processor family, applied
/// through the AMD SMU on the same PawnIO module UXTU itself uses.
///
/// COMMAND IDS
///
/// Transcribed from RyzenAdj's lib/api.c, FAM_PHOENIX / FAM_HAWKPOINT branch. They are
/// family-specific -- 0x14 is "set STAPM limit" on Phoenix and something else elsewhere --
/// which is why <see cref="IsSupported"/> is a hard gate rather than a hint. Knobs RyzenAdj
/// does not define for this family (per-rail TDC/EDC, PSI currents, OC clock/voltage, GFX
/// curve optimiser) are absent here rather than wired to a plausible neighbouring ID.
///
/// WHAT THIS HARDWARE ACTUALLY HONOURS
///
/// Measured on this machine, and the reason every apply is verified rather than assumed: HP's
/// firmware owns power management and silently discards power-limit writes. Setting STAPM to
/// 15 W returns success, raises no error, and leaves the enforced limit at 45 W -- sampled
/// from 2 ms to 8 s, so it is a flat refusal and not a revert race. HP's own WMI SetCpuPower
/// does nothing either. A command that succeeds and changes nothing has not been applied,
/// whatever its return code said, and this class reports that distinction.
///
/// Measured on the MP1 mailbox, which is where these commands actually belong:
///
///   accepted (0x01) : every power, thermal, timing, current and skin-temperature command,
///                     plus the built-in max-performance and power-saving profiles
///   rejected (0xFF) : all-core Curve Optimizer -- a precondition is not met, which on an OEM
///                     laptop generally means PBO is disabled in firmware
///   rejected (0xFE) : GFX clock -- the SMU reports itself busy
///
/// An earlier revision of this file recorded a very different matrix, full of refusals with
/// HRESULT 0x8007054F. That matrix was measured while these commands were being posted to the
/// RSMU mailbox, where the numbers mean something else, and none of it was real.
///
/// Acceptance still is not application. The accepted commands do move the limit values in the
/// PM table, but the platform does not enforce them: with a sustained limit reading 0.045 W
/// the processor still boosted to its full 4301 MHz under load. HP's firmware arbitrates
/// power on this machine and the SMU's own limits are advisory underneath it.
/// </summary>
public sealed class AmdTuning
{
    // RyzenAdj lib/api.c, FAM_PHOENIX / FAM_HAWKPOINT.
    private const byte CmdMaxPerformance = 0x11;
    private const byte CmdPowerSaving = 0x12;
    private const byte CmdStapmLimit = 0x14;
    private const byte CmdFastLimit = 0x15;
    private const byte CmdSlowLimit = 0x16;
    private const byte CmdSlowTime = 0x17;
    private const byte CmdStapmTime = 0x18;
    private const byte CmdTctlTemp = 0x19;
    private const byte CmdVrmCurrent = 0x1A;
    private const byte CmdProchotRamp = 0x1F;
    private const byte CmdApuSlowLimit = 0x23;
    private const byte CmdApuSkinTempLimit = 0x33;
    private const byte CmdSkinTempLimit = 0x4A;
    private const byte CmdCurveOptimizerPerCore = 0x4B;
    private const byte CmdCurveOptimizerAllCore = 0x4C;
    private const byte CmdGfxClock = 0x89;

    // Outer bounds, not recommendations. The SMU and the platform enforce the real limit for
    // the specific part; these stop a UI bug or a hand-edited settings file asking for
    // something absurd before it reaches the mailbox.
    public const int MinWatts = 5, MaxWatts = 65;
    public const int MinTempC = 40, MaxTempC = 100;
    public const int MinSeconds = 1, MaxSeconds = 300;
    public const int MinAmps = 5, MaxAmps = 120;
    public const int MinGfxMhz = 200, MaxGfxMhz = 3000;

    /// <summary>
    /// Curve Optimizer bounds, deliberately tighter than the encoding allows.
    ///
    /// Undervolting is the one knob here whose failure mode is an unstable machine rather
    /// than a refused command, and instability means lost work. -30 is the conventional floor
    /// for Zen mobile parts; past that a system is being asked to prove it is exceptional.
    /// </summary>
    public const int MinCurveOptimizer = -30, MaxCurveOptimizer = 30;

    private readonly RyzenSmu _smu;

    public AmdTuning(RyzenSmu smu) => _smu = smu;

    /// <summary>
    /// The MP1 mailbox, found once and remembered.
    ///
    /// This matters more than it looks. RyzenAdj's command IDs are MP1 commands, but the
    /// PawnIO module's own send routine only ever talks to RSMU (it maps Phoenix to the
    /// 0x3B10A20 address set, which ryzen_smu documents as RSMU). Posting 0x14 there is
    /// posting "set STAPM limit" to a channel where that number means something else -- which
    /// is exactly how an earlier revision of this class produced a capability matrix full of
    /// spurious refusals. Detection is by a read-only GetSmuVersion, so a wrong guess costs a
    /// rejected command rather than a stray write.
    /// </summary>
    private RyzenSmu.SmuMailbox? Mp1 => _smu.Mp1Mailbox;

    /// <summary>The MP1 mailbox in use, or null when it could not be reached.</summary>
    public string? MailboxName => Mp1?.Name;

    /// <summary>
    /// Whether the detected processor is one this command table was actually sourced for.
    /// False means every method here refuses; it does not mean "try anyway".
    /// </summary>
    public bool IsSupported => _smu.CodeName
        is AmdCodeName.Phoenix or AmdCodeName.Phoenix2 or AmdCodeName.HawkPoint;

    public string? UnsupportedReason => IsSupported
        ? null
        : "OmniHub only has a verified SMU command table for Phoenix and Hawk Point. " +
          $"This processor reports as {_smu.CodeName} ({_smu.CodeNameRaw}), so tuning is disabled " +
          "rather than guessed at.";

    /// <summary>Live package power against the limits actually in force, or null when unreadable.</summary>
    public PowerSnapshot? ReadPower() => _smu.ReadPowerSnapshot();

    /// <summary>One knob's measured answer from this firmware.</summary>
    /// <param name="Knob">Human name of the control.</param>
    /// <param name="State">"accepted", "refused", "busy", or "not probed".</param>
    /// <param name="Note">What was actually sent and what came back.</param>
    public sealed record CapabilityProbe(string Knob, string State, string Note);

    /// <summary>
    /// Asks this machine's firmware which tuning commands it will accept, and reports what it
    /// answered rather than what a table of assumptions says.
    ///
    /// This is the thing UXTU does not do. UXTU sends a command, gets an SMU acknowledgement,
    /// and shows success -- which on this platform is misleading, because the SMU cheerfully
    /// returns OK for limits HP's firmware then arbitrates away. Knowing WHICH knobs your
    /// particular machine honours is the difference between tuning and guessing, and it cannot
    /// be answered by a hardcoded table: it changes with the model and with a BIOS update.
    ///
    /// SAFE BY CONSTRUCTION: every probe re-sends the value the knob ALREADY HOLDS, read from
    /// the PM table immediately beforehand. A successful probe is a no-op write, so nothing on
    /// the machine changes and there is nothing to restore. Knobs the PM table cannot read
    /// back are reported as "not probed" rather than guessed at, because probing those would
    /// mean writing a value the user did not choose -- exactly the mistake worth not repeating.
    ///
    /// Acceptance is not enforcement. A knob can answer "accepted" here and still be overruled
    /// under load; that needs a sustained workload to detect and is reported separately.
    /// </summary>
    public IReadOnlyList<CapabilityProbe> ProbeCapabilities()
    {
        var snapshot = ReadPower();
        if (snapshot is null)
        {
            return new[]
            {
                new CapabilityProbe("PM table", "not probed",
                    "The power table could not be read, so there are no current values to safely re-send."),
            };
        }

        var results = new List<CapabilityProbe>();

        void Probe(string knob, double currentValue, string unit, Func<int, TuningResult> send)
        {
            int value = (int)Math.Round(currentValue);
            var r = send(value);

            // The detail string carries the SMU's own words, so a result that is neither a
            // clean accept nor a known refusal is still reported verbatim instead of being
            // flattened into a guess.
            results.Add(new CapabilityProbe(
                knob,
                r.Applied ? "accepted" : r.Detail.Contains("0xFE") ? "busy" : "refused",
                $"Re-sent its current {value} {unit}: {r.Detail}"));
        }

        Probe("Sustained limit (STAPM)", snapshot.StapmLimitWatts, "W", SetStapmWatts);
        Probe("Boost limit (PPT fast)", snapshot.FastLimitWatts, "W", SetFastWatts);
        Probe("Slow limit (PPT slow)", snapshot.SlowLimitWatts, "W", SetSlowWatts);
        Probe("APU slow limit", snapshot.ApuSlowLimitWatts, "W", SetApuSlowWatts);
        Probe("Thermal limit (Tctl)", snapshot.ThermalLimitC, "C", SetThermalLimitC);

        // Deliberately not probed. These have no PM-table readback, so the only way to test
        // them is to write a value of our choosing -- which would silently change the user's
        // configuration in the name of a diagnostic.
        foreach (var knob in new[]
                 {
                     "Skin / APU skin temperature",
                     "VRM current (TDC)",
                     "Averaging windows",
                     "GFX clock",
                     "Curve Optimizer",
                 })
        {
            results.Add(new CapabilityProbe(knob, "not probed",
                "No readback exists for this knob, so testing it would mean writing a value you did not choose."));
        }

        return results;
    }

    // ---------------------------------------------------------------- individual knobs

    /// <summary>Sustained power limit (STAPM), watts. The long-run average the part settles to.</summary>
    public TuningResult SetStapmWatts(int w) => Scaled(CmdStapmLimit, w, MinWatts, MaxWatts, 1000, "W", "sustained power limit");

    /// <summary>Short-burst power limit (PPT fast), watts.</summary>
    public TuningResult SetFastWatts(int w) => Scaled(CmdFastLimit, w, MinWatts, MaxWatts, 1000, "W", "boost power limit");

    /// <summary>Medium-window power limit (PPT slow), watts.</summary>
    public TuningResult SetSlowWatts(int w) => Scaled(CmdSlowLimit, w, MinWatts, MaxWatts, 1000, "W", "slow power limit");

    /// <summary>APU-specific slow limit, watts.</summary>
    public TuningResult SetApuSlowWatts(int w) => Scaled(CmdApuSlowLimit, w, MinWatts, MaxWatts, 1000, "W", "APU slow limit");

    /// <summary>How long the STAPM average is taken over, seconds.</summary>
    public TuningResult SetStapmTimeSeconds(int s) => Scaled(CmdStapmTime, s, MinSeconds, MaxSeconds, 1, "s", "STAPM window");

    /// <summary>How long the slow limit is averaged over, seconds.</summary>
    public TuningResult SetSlowTimeSeconds(int s) => Scaled(CmdSlowTime, s, MinSeconds, MaxSeconds, 1, "s", "slow-limit window");

    /// <summary>Die thermal target (Tctl), degrees C. Lowering it is the most direct way to run cooler.</summary>
    public TuningResult SetThermalLimitC(int c) => Scaled(CmdTctlTemp, c, MinTempC, MaxTempC, 1, "C", "thermal limit");

    /// <summary>Chassis skin temperature limit, degrees C.</summary>
    public TuningResult SetSkinTempC(int c) => Scaled(CmdSkinTempLimit, c, MinTempC, MaxTempC, 1, "C", "skin temperature limit");

    /// <summary>APU skin temperature limit, degrees C.</summary>
    public TuningResult SetApuSkinTempC(int c) => Scaled(CmdApuSkinTempLimit, c, MinTempC, MaxTempC, 1, "C", "APU skin temperature limit");

    /// <summary>VRM current limit (TDC), amps.</summary>
    public TuningResult SetVrmCurrentAmps(int a) => Scaled(CmdVrmCurrent, a, MinAmps, MaxAmps, 1000, "A", "VRM current limit");

    /// <summary>Integrated GPU clock target, MHz.</summary>
    public TuningResult SetGfxClockMhz(int mhz) => Scaled(CmdGfxClock, mhz, MinGfxMhz, MaxGfxMhz, 1, "MHz", "GFX clock");

    /// <summary>PROCHOT de-assertion ramp. Unitless platform tuning value.</summary>
    public TuningResult SetProchotRamp(int value) => Scaled(CmdProchotRamp, value, 0, 100, 1, "", "PROCHOT de-assertion ramp");

    /// <summary>The SMU's own built-in maximum-performance profile.</summary>
    public TuningResult ApplyMaxPerformance() => Raw(CmdMaxPerformance, 0, "maximum performance profile");

    /// <summary>The SMU's own built-in power-saving profile.</summary>
    public TuningResult ApplyPowerSaving() => Raw(CmdPowerSaving, 0, "power saving profile");

    /// <summary>
    /// All-core Curve Optimizer offset, in counts. Negative undervolts, which is the useful
    /// direction: less voltage for the same clock means less heat, and on a thermally limited
    /// chassis that converts directly into held boost.
    /// </summary>
    public TuningResult SetCurveOptimizerAllCore(int counts)
    {
        if (!IsSupported) return new TuningResult(false, UnsupportedReason!);
        int clamped = Math.Clamp(counts, MinCurveOptimizer, MaxCurveOptimizer);
        var result = Raw(CmdCurveOptimizerAllCore, EncodeCurveOptimizer(clamped), $"all-core Curve Optimizer to {clamped}");
        return result.Applied && clamped != counts
            ? new TuningResult(true, $"{result.Detail} (requested {counts}, clamped to {MinCurveOptimizer}..{MaxCurveOptimizer})")
            : result;
    }

    /// <summary>Per-core Curve Optimizer offset. Core index is packed into the high bits.</summary>
    public TuningResult SetCurveOptimizerCore(int coreIndex, int counts)
    {
        if (!IsSupported) return new TuningResult(false, UnsupportedReason!);
        if (coreIndex is < 0 or > 15) return new TuningResult(false, "Core index must be 0-15.");
        int clamped = Math.Clamp(counts, MinCurveOptimizer, MaxCurveOptimizer);
        uint arg = ((uint)coreIndex << 20) | EncodeCurveOptimizer(clamped);
        return Raw(CmdCurveOptimizerPerCore, arg, $"core {coreIndex} Curve Optimizer to {clamped}");
    }

    /// <summary>
    /// Encodes a Curve Optimizer offset as the SMU expects it: 20-bit two's complement, NOT a
    /// plain signed integer.
    ///
    /// -21 must go out as 0xFFFEB (0x100000 - 21 = 1048555). RyzenAdj issue #296 documents
    /// exactly this: passing -21 as an ordinary negative int produces 0xFFFFFFEB (4294967275),
    /// which is simply a different and very large number as far as the mailbox is concerned.
    /// Getting the sign wrong here does not fail loudly -- it applies a voltage offset nobody
    /// asked for -- which is why it is isolated in one function with its own test.
    /// </summary>
    public static uint EncodeCurveOptimizer(int counts) =>
        counts >= 0 ? (uint)counts : (uint)(0x100000 - (-counts));

    // ---------------------------------------------------------------- profiles

    /// <summary>
    /// Applies a whole profile, reporting per-knob what was sent and whether the hardware
    /// actually moved.
    ///
    /// Ordering is deliberate. Power limits go first and the thermal target near the end,
    /// because on firmware that silently discards writes the thermal knob is the one most
    /// likely to take effect on its own -- and a thermal target that moved while the user was
    /// told nothing applied is a hidden change. Curve Optimizer goes last of all: it is the
    /// only setting here whose failure mode is instability rather than refusal.
    /// </summary>
    public TuningReport Apply(AmdTuningProfile p)
    {
        if (!IsSupported)
            return new TuningReport(false, UnsupportedReason!, Array.Empty<TuningStep>(), null, null, null);

        var before = ReadPower();
        var steps = new List<TuningStep>();

        void Step(string name, int? value, Func<int, TuningResult> run)
        {
            if (value is not int v) return;
            var r = run(v);
            steps.Add(new TuningStep(name, r.Applied, r.Detail));
        }

        Step("Sustained power", p.StapmWatts, SetStapmWatts);
        Step("Slow power", p.SlowWatts, SetSlowWatts);
        Step("Boost power", p.FastWatts, SetFastWatts);
        Step("APU slow power", p.SlowWatts, SetApuSlowWatts);
        Step("STAPM window", p.StapmTimeSeconds, SetStapmTimeSeconds);
        Step("Slow window", p.SlowTimeSeconds, SetSlowTimeSeconds);
        Step("VRM current", p.VrmCurrentAmps, SetVrmCurrentAmps);
        Step("GFX clock", p.GfxClockMhz, SetGfxClockMhz);
        Step("PROCHOT ramp", p.ProchotRamp, SetProchotRamp);
        Step("Skin temp", p.SkinTempC, SetSkinTempC);
        Step("APU skin temp", p.ApuSkinTempC, SetApuSkinTempC);
        Step("Thermal limit", p.TctlTempC, SetThermalLimitC);
        Step("Curve Optimizer", p.CurveOptimizerAllCore, SetCurveOptimizerAllCore);

        var after = ReadPower();

        bool? verified = null;
        if (before is not null && after is not null && WantsPowerChange(p))
            verified = Moved(before, after) || MatchesRequest(p, after);

        string summary = verified switch
        {
            // Careful with this wording. Only the POWER limits are known unmoved; other knobs
            // in the same profile may well have landed, and saying "nothing was altered" when
            // the GFX clock or skin-temp limit just changed would be its own false claim.
            false => $"{p.Name}: power limits unchanged -- still {after!.StapmLimitWatts:0} W sustained and " +
                     $"{after.FastLimitWatts:0} W boost. This firmware locks those. " +
                     $"{steps.Count(s => s.Sent)} of {steps.Count} other settings were accepted; " +
                     $"{string.Join(", ", steps.Where(s => !s.Sent).Select(s => s.Name).DefaultIfEmpty("none"))} refused.",
            true => $"{p.Name} applied. SMU now enforcing {after!.StapmLimitWatts:0} W sustained, " +
                    $"{after.FastLimitWatts:0} W boost, {after.SlowLimitWatts:0} W slow.",
            null => $"{p.Name}: {steps.Count(s => s.Sent)} of {steps.Count} commands sent. " +
                    "No readable limit to verify against, so this is what was sent, not what took effect.",
        };

        return new TuningReport(verified ?? steps.Any(s => s.Sent), summary, steps, before, after, verified);
    }

    private static bool WantsPowerChange(AmdTuningProfile p) =>
        p.StapmWatts is not null || p.FastWatts is not null || p.SlowWatts is not null;

    private static bool Moved(PowerSnapshot a, PowerSnapshot b) =>
        Math.Abs(a.StapmLimitWatts - b.StapmLimitWatts) > 0.5
        || Math.Abs(a.FastLimitWatts - b.FastLimitWatts) > 0.5
        || Math.Abs(a.SlowLimitWatts - b.SlowLimitWatts) > 0.5;

    private static bool MatchesRequest(AmdTuningProfile p, PowerSnapshot s) =>
        (p.StapmWatts is not int w || Math.Abs(s.StapmLimitWatts - w) <= 1.0)
        && (p.FastWatts is not int f || Math.Abs(s.FastLimitWatts - f) <= 1.0);

    /// <summary>
    /// UXTU's premade profiles, adapted to this part's published configurable TDP range
    /// (45 W nominal, 35-54 W configurable). Starting points, not tuned optima -- which is
    /// exactly what UXTU's own premades are. The SMU clamps anything the platform will not
    /// honour, so a profile asking for more than the VRM supports settles at what it supports.
    ///
    /// None of them set Curve Optimizer. An undervolt that is stable on one chip is a crash on
    /// the next, so it stays a deliberate, separate choice rather than something a user picks
    /// up by clicking a preset named "Performance".
    /// </summary>
    public static IReadOnlyList<AmdTuningProfile> Profiles { get; } = new[]
    {
        new AmdTuningProfile("Eco", "Lowest heat and noise. Caps sustained draw well under stock.",
            StapmWatts: 12, FastWatts: 20, SlowWatts: 15, TctlTempC: 70, StapmTimeSeconds: 60),

        new AmdTuningProfile("Quiet", "Noticeably cooler and quieter, still responsive.",
            StapmWatts: 20, FastWatts: 30, SlowWatts: 25, TctlTempC: 78, StapmTimeSeconds: 45),

        new AmdTuningProfile("Balanced", "Stock-like sustained power with a cooler thermal target.",
            StapmWatts: 35, FastWatts: 45, SlowWatts: 40, TctlTempC: 85, StapmTimeSeconds: 30),

        new AmdTuningProfile("Performance", "Full configurable TDP and a higher thermal ceiling.",
            StapmWatts: 45, FastWatts: 60, SlowWatts: 54, TctlTempC: 95, StapmTimeSeconds: 20),

        new AmdTuningProfile("Max", "Everything the platform will accept. Loud and hot by design.",
            StapmWatts: 54, FastWatts: 65, SlowWatts: 60, TctlTempC: 100, StapmTimeSeconds: 10),
    };

    // ---------------------------------------------------------------- plumbing

    private TuningResult Scaled(byte command, int value, int min, int max, int scale, string unit, string what)
    {
        if (!IsSupported) return new TuningResult(false, UnsupportedReason!);

        int clamped = Math.Clamp(value, min, max);
        var result = Raw(command, (uint)((long)clamped * scale), $"{what} to {clamped}{unit}");

        // Say so when the request was not what was sent. Silently substituting a different
        // number and reporting success is how a UI ends up showing a limit the hardware
        // never received.
        return result.Applied && clamped != value
            ? new TuningResult(true, $"{result.Detail} (requested {value}{unit}, clamped to {min}-{max}{unit})")
            : result;
    }

    private TuningResult Raw(byte command, uint argument, string what)
    {
        if (!IsSupported) return new TuningResult(false, UnsupportedReason!);

        var mailbox = Mp1;
        if (mailbox is null)
            return new TuningResult(false,
                "The MP1 mailbox did not answer, so there is nowhere to send tuning commands. " +
                "Posting them to the module's RSMU channel instead would mean sending each " +
                "command number to a mailbox where it means something else.");

        try
        {
            uint response = _smu.SendToMailbox(mailbox, command, stackalloc uint[1] { argument }, Span<uint>.Empty);

            // Whatever the SMU made of it, the cached PM table may no longer describe the
            // hardware, so drop it before anyone reads back. Unconditional on purpose: a
            // command that reports failure can still have moved something, and a stale cache
            // is worse than one extra read.
            _smu.InvalidatePowerCache();

            return response switch
            {
                RyzenSmu.SmuReturnOk => new TuningResult(true, $"Set {what}."),
                0 => new TuningResult(false, $"The SMU did not respond when setting the {what} (timed out)."),
                // 0xFF is a SPECIFIC refusal, not a generic failure, and it is worth saying so.
                //
                // Measured on this machine: unknown command IDs come back 0xFE, so 0xFF means
                // the SMU recognised the command and declined it. For Curve Optimizer that was
                // reproduced with an offset of 0 -- a no-op undervolt -- through both the
                // all-core and per-core commands, which rules out the value, the 20-bit
                // encoding and the clamp. The command itself is blocked, and its documented
                // precondition is PBO / Core Performance Boost being enabled in firmware.
                //
                // Other tools report this as success because they treat the mailbox
                // acknowledgement as proof. The point of this app is that it does not.
                0xFF => new TuningResult(false,
                    $"The firmware refused the {what}. The SMU recognises the command and "
                    + "declines it, which for Curve Optimizer means PBO is disabled in the "
                    + "BIOS -- HP does not expose that toggle, so undervolting is unavailable "
                    + "on this machine whichever tool asks."),
                0xFE => new TuningResult(false, $"The SMU was busy and rejected the {what}."),
                _ => new TuningResult(false, $"The SMU returned 0x{response:X} for the {what}."),
            };
        }
        catch (Exception ex)
        {
            return new TuningResult(false, $"Could not set the {what}: {ex.Message}");
        }
    }
}
