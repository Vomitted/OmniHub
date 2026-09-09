using OmniHub.Core.Hardware;

namespace OmniHub.Core.Optimize;

/// <summary>
/// UXTU's Adaptive mode: instead of pinning one sustained power limit, walk it up and down to
/// hold a temperature target, so the machine spends its thermal budget on whatever it is
/// actually doing rather than on a number chosen in advance.
///
/// It steers on two inputs, temperature and demand. Temperature alone is not enough -- see
/// <see cref="Direction"/> for the failure that proves it.
///
/// The control law is deliberately dull -- a bounded single-step nudge, never outside
/// [MinWatts, MaxWatts]. Anything cleverer would be a PID whose gains nobody can justify
/// against a thermal mass this slow, and the failure mode of a badly tuned controller here is
/// a machine oscillating between hot and throttled.
///
/// It stops itself if the hardware is not listening. On firmware that silently discards power
/// writes -- which is this machine, measured -- a controller would otherwise spin forever
/// adjusting a number nothing reads, reporting confident nonsense. After three ticks with a
/// commanded change and no movement in the enforced limit, it gives up and says why.
///
/// ponytail: single-step integral control, no PID. Add a derivative term only if temperature
/// is seen to overshoot the target across a real workload.
/// </summary>
public sealed class AdaptiveTuning : IDisposable
{
    private readonly AmdTuning _tuning;
    private readonly Func<double> _readTempC;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;

    /// <summary>Lowest sustained limit the controller may command, watts.</summary>
    public int MinWatts { get; init; } = 15;

    /// <summary>Highest sustained limit the controller may command, watts.</summary>
    public int MaxWatts { get; init; } = 54;

    /// <summary>Die temperature the controller steers towards, degrees C.</summary>
    public int TargetTempC { get; init; } = 85;

    /// <summary>Watts added or removed per tick. Small enough that a wrong step is cheap.</summary>
    public int StepWatts { get; init; } = 3;

    /// <summary>Degrees either side of the target treated as close enough -- stops hunting.</summary>
    public double DeadbandC { get; init; } = 3.0;

    /// <summary>
    /// Fraction of the limit in force that the processor must actually be drawing before the
    /// controller will raise that limit. Below it the chip is work-limited rather than
    /// power-limited, and more headroom would change nothing.
    ///
    /// A calibration knob, not a constant. STAPM is a rolling average whose closeness to its
    /// own cap under a genuinely power-limited load depends on the platform's averaging window,
    /// so the right value is a measured property of the machine, not a derivable one.
    /// </summary>
    public double DemandThreshold { get; init; } = 0.85;

    /// <summary>
    /// What the discrete GPU is doing, or null when there is nothing to ask.
    ///
    /// Optional on purpose. With no reader supplied the controller steers exactly as it did
    /// before the budget term existed, because <see cref="GpuDemand"/>'s default is all-null and
    /// an unreadable GPU load is never treated as busy. That keeps machines with no discrete GPU,
    /// and machines whose GPU cannot be read, on the behaviour that has actually been measured.
    /// </summary>
    public Func<GpuDemand>? ReadGpu { get; init; }

    /// <summary>The limits and target that apply on one power rail.</summary>
    public readonly record struct Rail(int MinWatts, int MaxWatts, int TargetTempC);

    /// <summary>
    /// What to steer towards on battery, or null to use the same figures as on mains.
    ///
    /// "Adaptive" meant adaptive to temperature only: one ceiling and one target, whether or not
    /// the machine was plugged in. On battery that is the wrong shape entirely. The goal there is
    /// not to spend the thermal budget well, it is not to spend the battery, and a ceiling chosen
    /// for a mains workload lets the processor burst to it every time a browser tab opens.
    ///
    /// Null keeps the previous single-rail behaviour exactly, so a machine with no battery, or a
    /// user who has not configured one, is unaffected.
    /// </summary>
    public Rail? BatteryRail { get; init; }

    /// <summary>
    /// Where the machine is drawing power from. Defaults to the real reading; injectable so the
    /// rail switching can be tested without a battery.
    /// </summary>
    public Func<PowerSource> ReadPowerSource { get; init; } = PowerSourceWatcher.Read;

    /// <summary>
    /// The rail in force for a given power source.
    ///
    /// Unknown takes the mains rail, for the reason PowerSourceWatcher documents: Windows reports
    /// an unknown line status during resume and on some docks, and detuning a plugged-in machine
    /// on that basis is a worse error than briefly over-supplying an unplugged one.
    /// </summary>
    public Rail RailFor(PowerSource source) =>
        source == PowerSource.Battery && BatteryRail is { } battery
            ? battery
            : new Rail(MinWatts, MaxWatts, TargetTempC);

    /// <summary>The limit the controller last commanded, or null before its first tick.</summary>
    public int? CommandedWatts { get; private set; }

    /// <summary>Set when the controller stopped itself. Null while running normally.</summary>
    public string? StoppedReason { get; private set; }

    private Task? _loop;

    /// <summary>True only while the loop is genuinely alive -- see FanService.IsRunning for
    /// why testing the cancellation token alone was not enough.</summary>
    public bool IsRunning => _cts is { IsCancellationRequested: false } && _loop is { IsCompleted: false };

    /// <summary>Fires each tick with the temperature seen and the limit commanded.</summary>
    public event Action<double, int>? OnTick;

    public AdaptiveTuning(AmdTuning tuning, Func<double> readTempC, TimeSpan? interval = null)
    {
        _tuning = tuning;
        _readTempC = readTempC;
        _interval = interval ?? TimeSpan.FromSeconds(3);
    }

    public void Start()
    {
        if (IsRunning) return;
        StoppedReason = null;
        _cts = new CancellationTokenSource();
        // Task.Run rather than a bare call: the body opens with SMU round-trips, and an async
        // method runs synchronously up to its first await -- called from a click handler that
        // would block the UI thread for the whole of the first tick.
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        CommandedWatts = null;
    }

    /// <summary>
    /// Which way to move the sustained limit: -1 down, 0 hold, +1 up.
    ///
    /// Steering on temperature alone is the bug this function exists to fix. "Below target, so
    /// add power" is true of an idle machine at every tick, so the limit ratcheted up to
    /// MaxWatts during idle and stayed there -- and the next piece of work, however trivial,
    /// then ran at full power and drove the die straight onto the target. A laptop three
    /// minutes into a boot sat at 85 C with its fans at full speed, held there by the
    /// controller that was supposed to be protecting it, and the temperature looked like a
    /// misreading because nothing on screen connected it to a power limit wound up during idle.
    /// That is integral windup: the loop kept integrating while its output could not act.
    ///
    /// The demand term closes it. Headroom is granted only to a processor already using the
    /// headroom it has, and taken back from one that is not -- which costs nothing, because by
    /// definition that power was not being spent. Only the sustained (STAPM) limit is steered
    /// here; the boost limit is untouched, so short interactive bursts keep their full power
    /// however far this has wound down.
    /// </summary>
    /// <param name="drawWatts">Sustained power actually drawn, or null if it cannot be read.</param>
    public static int Direction(
        double tempC, int watts, double? drawWatts,
        int targetC, double deadbandC, double demandThreshold)
    {
        // Too hot outranks everything, including a chip that is genuinely asking for more.
        if (tempC > targetC + deadbandC) return -1;

        // An unreadable draw leaves the demand term with nothing to say, so fall back to
        // steering on temperature alone rather than freezing at the current limit. Firmware
        // that cannot read its power table back is caught by the loop's three-strike guard,
        // which is the right place for it -- silence here is not evidence of a locked limit.
        if (drawWatts is double draw && draw < watts * demandThreshold) return -1;

        return tempC < targetC - deadbandC ? 1 : 0;
    }

    private async Task RunAsync(CancellationToken token)
    {
        // Seeded on the first tick, INSIDE the try, not before the loop.
        //
        // Reading the enforced limit is an SMU round trip, and as this method's first
        // statement it sat outside every handler: one transient failure faulted the task
        // before the loop began, Task.Run's result was discarded so nothing observed it, and
        // IsRunning kept answering true off an uncancelled token. Adaptive mode then did
        // nothing whatsoever while the checkbox showed it as running -- the same shape as the
        // fan loop's SetFanMode, and just as invisible.
        int watts = MaxWatts;
        bool seeded = false;

        int ignoredTicks = 0;

        // The rail in force last tick, so a charger going in or out is noticed here rather than
        // waited out. See the clamp below for why that matters.
        var lastSource = PowerSource.Unknown;
        bool sourceSeen = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                // One read per tick, feeding both the seed and the demand term. The snapshot is
                // cached inside the SMU layer, so this is not an extra mailbox transaction.
                PowerSnapshot? power = _tuning.ReadPower();

                // Which rail applies right now. Read every tick rather than at startup, because
                // the whole point is that a charger can move while this is running.
                PowerSource source;
                try { source = ReadPowerSource(); } catch { source = lastSource; }
                var rail = RailFor(source);

                // Start from whatever the hardware is enforcing now, so the first move is a
                // nudge rather than a jump away from an assumed value.
                if (!seeded)
                {
                    watts = Math.Clamp(
                        (int)Math.Round(power?.StapmLimitWatts ?? rail.MaxWatts),
                        rail.MinWatts, rail.MaxWatts);
                    seeded = true;
                }

                // A rail change is applied AT ONCE, not walked to three watts at a time.
                //
                // This is the rough transition. Unplugging left the controller holding a mains
                // ceiling and stepping down from it, so the processor kept a gaming-sized
                // sustained limit for the best part of a minute on battery. Plugging in was
                // worse in the other direction: the ceiling jumped, the chip took all of it at
                // once, and the die reached ninety degrees before the loop had taken its second
                // step.
                //
                // Clamping into the new rail immediately makes the change as sharp as the event
                // that caused it, which is what someone plugging in a charger expects.
                if (!sourceSeen || source != lastSource)
                {
                    lastSource = source;
                    sourceSeen = true;

                    int clamped = Math.Clamp(watts, rail.MinWatts, rail.MaxWatts);
                    if (clamped != watts)
                    {
                        watts = clamped;
                        try { _tuning.SetStapmWatts(watts); } catch { }
                    }
                }

                double temp = _readTempC();

                // The GPU shares this chassis' cooling, so the CPU limit is not decided by the
                // CPU's own numbers alone. With no reader this collapses to Direction unchanged.
                GpuDemand gpu = default;
                try { if (ReadGpu is { } read) gpu = read(); } catch { }

                int next = Math.Clamp(
                    watts + StepWatts * ThermalBudget.CpuDirection(
                        temp, watts, power?.StapmWatts, gpu,
                        rail.TargetTempC, DeadbandC, DemandThreshold),
                    rail.MinWatts, rail.MaxWatts);

                if (next != watts)
                {
                    _tuning.SetStapmWatts(next);

                    // Did the hardware take it? Read the enforced limit rather than trusting
                    // the command's return code, which reports success even when nothing moved.
                    double? enforced = _tuning.ReadPower()?.StapmLimitWatts;
                    if (enforced is double e && Math.Abs(e - next) > 1.5)
                    {
                        if (++ignoredTicks >= 3)
                        {
                            StoppedReason =
                                $"Adaptive mode stopped: the SMU accepted {next} W but the enforced limit stayed " +
                                $"at {e:0} W across three attempts. This firmware locks CPU power limits, so there " +
                                "is nothing for the controller to steer.";
                            Stop();
                            return;
                        }
                    }
                    else ignoredTicks = 0;

                    watts = next;
                }

                CommandedWatts = watts;
                OnTick?.Invoke(temp, watts);
            }
            catch
            {
                // One failed tick should not end the loop; the next reading will do.
            }

            try { await Task.Delay(_interval, token); }
            catch (TaskCanceledException) { break; }
        }
    }

    public void Dispose() => Stop();
}
