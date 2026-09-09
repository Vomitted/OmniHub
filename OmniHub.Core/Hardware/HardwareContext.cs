using OmniHub.Core.Fan;
using SysTimer = System.Threading.Timer;

namespace OmniHub.Core.Hardware;

/// <summary>
/// One poll's worth of hardware state.
///
/// TemperatureC is kept as a whole-degree byte because that is what every existing consumer
/// displays. PreciseTemperatureC carries the same reading unrounded, which matters once the
/// source is Tctl: that sensor resolves to 0.125C, and rounding it away at the record
/// boundary would discard most of the reason for reading it.
/// </summary>
public sealed record Reading(
    byte TemperatureC,
    byte FanLevel1,
    byte FanLevel2,
    bool MaxFanActive,
    ThrottlingState Throttling,
    double PreciseTemperatureC = double.NaN,
    TemperatureSource TemperatureSource = TemperatureSource.AcpiThermalZone);

/// <summary>
/// Owns the one BiosInterop connection and hands out the specialized
/// controllers, plus a shared poll loop so the Dashboard/Fans/GPU/Power
/// tabs aren't each opening their own WMI session.
/// </summary>
public sealed class HardwareContext : IDisposable
{
    private readonly BiosInterop _bios;
    private SysTimer? _pollTimer;

    public FanController Fan { get; }
    public GpuController Gpu { get; }
    public PowerController Power { get; }
    public SystemController System { get; }
    public ModelInfo Model { get; }

    /// <summary>
    /// SMU access through PawnIO, or null when it is unavailable -- see
    /// <see cref="SmuUnavailableReason"/>. Null is an ordinary state (no driver, not
    /// elevated, no module), not a failure, and everything here still works without it.
    /// </summary>
    public RyzenSmu? Smu { get; private set; }

    /// <summary>Why <see cref="Smu"/> is null, in words fit to show a user. Null when it opened.</summary>
    public string? SmuUnavailableReason { get; private set; }

    /// <summary>
    /// Whether this machine exposes the vendor control interface that fan control, GPU power
    /// and BIOS power limits all depend on.
    ///
    /// False is a supported state: temperatures, load, clocks, memory, battery and every
    /// Windows-side control still work, and the app runs normally with the vendor-specific
    /// panels reporting themselves unavailable rather than the process failing to start.
    /// </summary>
    public bool VendorSupported => _bios.IsAvailable;

    /// <summary>Why the vendor interface is unavailable, or null when it is present.</summary>
    public string? VendorUnavailableReason => _bios.UnavailableReason;

    private DateTime _nextSmuRetryUtc = DateTime.MinValue;

    /// <summary>How long to wait between attempts to open a driver that was not there yet.</summary>
    private static readonly TimeSpan SmuRetryInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Tries again to open the SMU, for as long as it has not opened.
    ///
    /// PawnIO's service is installed with Manual start, so when OmniHub launches at sign-in it
    /// regularly wins the race and the single attempt in the constructor fails. Without a
    /// retry that verdict stood for the whole session: no Tctl, every reading falling back to
    /// the ACPI zone, and that zone pins at 85 C -- which the fan curve correctly reads as a
    /// blind sensor and answers with 100% airflow. One missed driver open at boot was
    /// therefore worth hours of maximum fan.
    ///
    /// Cheap to call: it does nothing once Smu is open, and only retries every ten seconds
    /// while it is not.
    /// </summary>
    private void RetrySmuOpen()
    {
        if (Smu is not null || DateTime.UtcNow < _nextSmuRetryUtc) return;
        _nextSmuRetryUtc = DateTime.UtcNow + SmuRetryInterval;

        var smu = RyzenSmu.TryOpen(out string? reason);
        if (smu is null)
        {
            SmuUnavailableReason = reason;
            return;
        }

        Smu = smu;
        SmuUnavailableReason = null;
        System.AttachSmu(smu);
    }

    public event Action<Reading>? OnReading;

    /// <summary>
    /// Filtered CPU die temperature, for anything on screen.
    ///
    /// Lives here rather than on FanService because FanService STOPS whenever the fan mode
    /// leaves Auto -- MAX TURBO and BIOS Default both call Stop() -- and a stopped service
    /// stops ingesting while HasEnoughData stays true. FilteredTempC then held whatever it
    /// last computed, forever, and the dashboard rendered that number beside a live fan speed
    /// and a live clock. That is the "temperature is stuck" report: the reading was never
    /// wrong, it just had no writer any more.
    ///
    /// This poll loop runs for the life of the app regardless of fan mode, which makes it the
    /// only honest source for a readout that is always visible.
    /// </summary>
    public ThermalTrend CpuTrend { get; } = new();

    public HardwareContext()
    {
        _bios = new BiosInterop();

        // Opened before SystemController so it can be handed in. This never throws: an absent
        // driver comes back as null plus a reason, and the ACPI path continues to work.
        Smu = RyzenSmu.TryOpen(out string? smuReason);
        SmuUnavailableReason = smuReason;

        Fan = new FanController(_bios);
        Gpu = new GpuController(_bios);
        Power = new PowerController(_bios);
        System = new SystemController(_bios, Smu);
        Model = ModelProfile.Detect();

        // Read once, here. These bits describe the board, not its current state, so re-reading
        // them per tick would spend BIOS round trips on an answer that cannot change.
        Capabilities = System.ReadSystemData();

        // Only a CREDIBLE block may change the fan command encoding.
        //
        // LooksUnreported is load-bearing here, not decoration. A failed read comes back as all
        // zeroes, and byte #3 of zero decodes as ThermalPolicyVersion.Legacy -- so trusting the
        // field unguarded would switch a Victus onto the Pavilion encoding precisely when the
        // firmware had told us nothing, and fan control is the one thing in this application
        // that must not be broken by a bad guess. Anything short of a real answer leaves the
        // encoding at its default.
        if (Capabilities is { LooksUnreported: false } caps)
            Fan.Encoding = caps.ThermalPolicy;

        // Asked once, for the same reason as the capability block: it describes the chassis.
        try { FanCount = Fan.GetFanCount(); } catch { /* no vendor interface: stays unknown */ }

        // A per-model fan band, if anyone has measured this board and left a profile for it.
        // Null means no profile, or one that did not make a usable scale, and the measured
        // default stands -- which is the behaviour every build so far has had.
        if (FanProfiles.Load(Model) is { } calibration)
        {
            // Fully qualified: this type's own Fan property would otherwise shadow the namespace.
            OmniHub.Core.Fan.FanService.Calibration = calibration;
            FanCalibrationSource = $"profile for board {Model.BaseboardProduct}";
        }
    }

    /// <summary>
    /// Where the fan band in force came from, for the probe output to state plainly. A profile
    /// changes how every percentage on the curve is commanded, so which one is loaded is not a
    /// detail to leave implicit.
    /// </summary>
    public string FanCalibrationSource { get; private set; } = "built-in default (board 8C2F)";

    /// <summary>
    /// How many fans the firmware reports, or null when it would not say.
    ///
    /// This application drives two, and that is baked into the command payload, the reading
    /// record, the log schema and every readout. The count was being read for the probe printout
    /// and nowhere else, so a chassis with a different number would have been driven as though it
    /// had two, silently and with no indication anywhere that anything had been left out.
    ///
    /// Reported rather than acted on. The payload has two spare bytes that LOOK like room for
    /// fans three and four, but that is an inference from a layout, not a measurement, and
    /// guessing a wire format for hardware nobody here can test is the exact mistake this
    /// project exists not to make. Saying "this board reports four fans and only two are being
    /// driven" is honest and costs nothing; writing a speculative payload to find out is not.
    /// </summary>
    public byte? FanCount { get; private set; }

    /// <summary>True when the firmware reports more fans than this application drives.</summary>
    public bool HasUndrivenFans => FanCount is > 2;

    /// <summary>
    /// The one charger watcher, shared by everything that cares which rail the machine is on.
    ///
    /// There were two, polling GetSystemPowerStatus five seconds apart for the same answer. The
    /// duplicated syscall was the smaller half of it: two watchers observe a charger change at
    /// slightly different moments, so the tuning profile and the Windows power plan could briefly
    /// disagree about which rail the machine was on and act on different answers.
    ///
    /// Owned here because this already owns the machine's state. Whoever needs it starts it;
    /// a session that enables neither feature never polls.
    /// </summary>
    public Optimize.PowerSourceWatcher PowerSource { get; } = new();

    /// <summary>
    /// What the firmware says this board can do, or null when it would not say.
    ///
    /// This is the answer to "will OmniHub work on a Pavilion / an older Omen / a Victus", asked
    /// of the machine rather than of a hand-maintained list of model numbers.
    /// </summary>
    public HpSystemData? Capabilities { get; private set; }

    /// <summary>
    /// True unless the firmware positively states this board has no software fan control.
    /// </summary>
    public bool SoftwareFanControlAllowed => !FirmwareDenies(c => c.SoftwareFanControl);

    /// <summary>
    /// True unless the firmware positively states this board cannot switch GPU modes. The GPU
    /// tab offered Hybrid/Discrete/Optimus unconditionally before this, on every machine.
    /// </summary>
    public bool GpuModeSwitchAllowed => !FirmwareDenies(c => c.GpuModeSwitchSupported);

    // The rule itself lives on HpSystemData, where it can be tested without a machine. See
    // HpSystemData.Denies for why unknown must not disable anything.
    private bool FirmwareDenies(Func<HpSystemData, bool> capability) =>
        HpSystemData.Denies(Capabilities, capability);

    private byte _lastTemperatureC;
    private TemperatureReading _lastTemperature;
    private DateTime _lastTemperatureAtUtc = DateTime.MinValue;
    private int _polling;

    /// <summary>How many poll ticks pass between refreshes of the slow-moving BIOS flags.</summary>
    private const int SlowTickEvery = 5;

    // Seeded one short of the threshold so the very first poll refreshes these immediately,
    // rather than leaving the flags at their defaults for five ticks. This replaces a
    // "|| _slowTick == 1" special case that fired on tick one but also reset the counter to 1
    // instead of 0 -- which quietly made the real period four ticks, not the five named here.
    private int _slowTick = SlowTickEvery - 1;

    /// <summary>
    /// The last fan levels read back from the EC, refreshed on the slow tick.
    ///
    /// Empty until the first poll, which the seed above makes the very first tick, so the
    /// dashboard never renders a fabricated zero while waiting for a real one.
    /// </summary>
    private byte[] _lastLevels = Array.Empty<byte>();

    private bool _lastMaxFan;
    private ThrottlingState _lastThrottle = ThrottlingState.Unknown;

    /// <summary>How old a cached temperature may be before it stops counting as current.</summary>
    private static readonly TimeSpan TemperatureMaxAge = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The temperature from the most recent poll: the same value the UI is displaying.
    ///
    /// This exists so the fan curve and the on-screen readout cannot disagree. They used to.
    /// The poll timer read the sensor for the display, and FanService independently read it
    /// again on its own timer for the curve. Two unsynchronised 2-second loops meant the
    /// number on screen was never quite the number the fan was acting on, and each loop cost
    /// its own WMI query.
    ///
    /// Throws rather than returning a stale or default value: a silently wrong temperature
    /// here would command a near-silent fan while the machine is hot, which is the exact
    /// failure this app exists to prevent. Both callers already skip a failed tick.
    /// </summary>
    public byte CurrentTemperatureC() => (byte)Math.Clamp(Math.Round(CurrentTemperature().Celsius), 0, 255);

    /// <summary>
    /// The most recent reading at full precision, together with which sensor produced it.
    ///
    /// The fan curve should prefer this over <see cref="CurrentTemperatureC"/>: the source
    /// determines whether a reading near 85C means "the die is at 85C" or "the sensor is
    /// blind and it could be anything above that", and those call for opposite responses.
    ///
    /// Throws on a stale or absent reading for the same reason as before -- a silently wrong
    /// temperature here would command a near-silent fan while the machine is hot.
    /// </summary>
    public TemperatureReading CurrentTemperature()
    {
        var at = _lastTemperatureAtUtc;
        if (at == DateTime.MinValue)
            throw new InvalidOperationException("No temperature reading yet -- polling has not produced one.");

        var age = DateTime.UtcNow - at;
        if (age > TemperatureMaxAge)
            throw new InvalidOperationException(
                $"Temperature reading is stale ({age.TotalSeconds:0.#}s old) -- the poll loop is not keeping up.");

        return _lastTemperature;
    }

    public void StartPolling(TimeSpan interval)
    {
        if (_pollTimer is not null) return; // idempotent; a second call would orphan the first timer

        // Period is Infinite and the timer re-arms at the end of each cycle. With a fixed 2s
        // period, a cycle that overran -- four BIOS round-trips can -- would have the next
        // tick start on another thread pool thread while the previous was still running,
        // stacking concurrent pollers against shared hardware state.
        //
        // The timer is created STOPPED and captured in a local, then started below. Creating
        // it with a zero due time queues the first callback before the constructor returns,
        // so the callback could reach its re-arm line while the field it re-arms through was
        // still null -- the re-arm would silently no-op and the loop would stop after exactly
        // one reading. That failure is invisible (no exception) and total: the temperature
        // freezes, CurrentTemperatureC starts throwing stale, and the fan curve stops
        // commanding anything. Capturing the local removes the race entirely.
        SysTimer? timer = null;
        timer = new SysTimer(_ =>
        {
            // Belt and braces alongside the re-arm below: Dispose can race a queued callback.
            if (Interlocked.Exchange(ref _polling, 1) == 1) return;
            try
            {
                // Before the reading, so a late-arriving driver is picked up on the very next
                // tick rather than never.
                // Timed, because the tick body costs about 300ms and nobody knows which call it
                // is. The poll re-arms after it finishes rather than on a fixed period, so that
                // cost sets the real cadence: measured from the thermal log's own timestamps,
                // 2s nominal lands at 2.31s. Two rounds of optimisation aimed at this figure
                // barely moved it, which means the guesses were aimed at the wrong call.
                var tick = global::System.Diagnostics.Stopwatch.StartNew();

                RetrySmuOpen();

                var reading = System.ReadTemperature();
                long tempMs = tick.ElapsedMilliseconds;

                var temp = (byte)Math.Clamp(Math.Round(reading.Celsius), 0, 255);
                _lastTemperature = reading;
                _lastTemperatureC = temp;
                _lastTemperatureAtUtc = DateTime.UtcNow;
                CpuTrend.Ingest(reading.Celsius, _lastTemperatureAtUtc);

                long fanMs = 0;

                // Max-fan and throttling are read every fifth tick, not every tick.
                //
                // Each is a separate hpqBIntM round trip, and neither earns that rate. Max fan
                // only changes when something deliberately toggles it. Throttling is worse
                // than slow: SystemController.GetThrottling documents itself as unverified on
                // this firmware, since a diagnostic sweep showed the response echoing back the
                // selector byte it was sent. Paying for two BIOS calls a second to refresh a
                // flag that rarely moves and a flag we do not fully trust is the wrong trade.
                if (++_slowTick >= SlowTickEvery)
                {
                    _slowTick = 0;

                    // The fan level readback moved in here, and it is the single biggest thing
                    // this loop does. Measured with the poll instrumentation: 306ms of a 324ms
                    // tick -- 94% of it -- against about 8ms for each of the two reads below.
                    //
                    // The difference is not the work, it is the method. hpqBIntM exposes one per
                    // response-buffer size, and the fan level command answers only through
                    // hpqBIOSInt128; asking hpqBIOSInt4 for it is refused outright. So the cost
                    // cannot be argued down, only paid less often.
                    //
                    // Less often is right anyway. Nothing steers on this value: FanService reads
                    // temperature and WRITES levels, and it writes at most on a change or every
                    // thirty seconds. The readback exists so the screen and the log can show what
                    // the EC actually reports rather than what was last asked for, and at one
                    // read per five ticks that is still about every eleven seconds -- current by
                    // any standard the display needs, for a quarter of the poll's cost.
                    long beforeFan = tick.ElapsedMilliseconds;
                    try { _lastLevels = Fan.GetFanLevel(); } catch { /* keep the last good read */ }
                    fanMs = tick.ElapsedMilliseconds - beforeFan;

                    try
                    {
                        _lastMaxFan = System.GetMaxFanActive();
                        _lastThrottle = System.GetThrottling();
                    }
                    catch
                    {
                        // Vendor-only flags. Their absence leaves the last known values, which
                        // default to "not max fan" and "unknown throttling" -- both honest.
                    }
                }
                long slowMs = tick.ElapsedMilliseconds - tempMs - fanMs;

                var levels = _lastLevels;
                var maxFan = _lastMaxFan;
                var throttle = _lastThrottle;
                var payload = new Reading(temp,
                    levels.Length > 0 ? levels[0] : (byte)0,
                    levels.Length > 1 ? levels[1] : (byte)0,
                    maxFan, throttle,
                    reading.Celsius, reading.Source);

                // Each subscriber is invoked separately, in its own try.
                //
                // A plain OnReading?.Invoke walks the invocation list and stops dead at the
                // first handler that throws -- every later subscriber is skipped, and the
                // exception lands in the catch below where it is discarded. There are up to
                // seven subscribers here (throttle detection, the thermal log, the overlay,
                // the ribbon, and the dashboard, fans and tray views), several of them UI code
                // that can fail on a resource lookup or on a control being torn down. One of
                // those quietly disabling throttle detection and thermal logging for the rest
                // of the session is the same invisible partial failure this app has already
                // been bitten by twice.
                //
                // PowerSourceWatcher and ProcessWatcher already guard their raises this way.
                // This one, much the busiest, did not.
                if (OnReading is { } handlers)
                {
                    foreach (var handler in handlers.GetInvocationList())
                    {
                        try { ((Action<Reading>)handler)(payload); }
                        catch { /* one bad subscriber must not silence the others */ }
                    }
                }

                RecordTiming(tick.ElapsedMilliseconds, tempMs, fanMs, slowMs,
                             tick.ElapsedMilliseconds - tempMs - fanMs - slowMs, _slowTick == 0);
            }
            catch
            {
                // Transient BIOS call failures shouldn't crash the poll loop
            }
            finally
            {
                Interlocked.Exchange(ref _polling, 0);
                try { timer!.Change(interval, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
            }
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        _pollTimer = timer;
        timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan); // first tick now, then self-re-arming
    }

    /// <summary>The most recent tick's cost, in milliseconds, for the Diagnostics tab.</summary>
    public string LastTickTimings { get; private set; } = "not measured yet";

    private int _timingTicks;
    private long _sumTotal, _sumTemp, _sumFan, _sumSlow, _sumDispatch;

    /// <summary>
    /// Accumulates the tick breakdown and writes one averaged line per thirty ticks.
    ///
    /// Averaged rather than per-tick because a single sample of a BIOS round trip is mostly
    /// noise, and because a line every 2.3 seconds would be its own cost. Thirty ticks is about
    /// seventy seconds, which is enough to see whether the temperature read or the fan read
    /// dominates -- the question two rounds of optimisation guessed at and got wrong.
    ///
    /// Its own file, not the thermal log: that schema is the fan curve's, and diagnostics that
    /// force a column change would roll every reader's parser.
    /// </summary>
    private void RecordTiming(long total, long temp, long fan, long slow, long dispatch, bool wasSlowTick)
    {
        _sumTotal += total; _sumTemp += temp; _sumFan += fan; _sumSlow += slow; _sumDispatch += dispatch;

        LastTickTimings =
            $"{total} ms total: temperature {temp}, fan level {fan}, "
            + $"{(wasSlowTick ? $"max-fan/throttle {slow}, " : "")}subscribers {dispatch}";

        if (++_timingTicks < 30) return;

        try
        {
            string dir = OmniHub.Core.Fan.ThermalLog.LogDirectory;
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"polltiming-{DateTime.Now:yyyy-MM-dd}.csv");

            if (!File.Exists(path))
                File.AppendAllText(path, "timestamp,ticks,avg_total_ms,avg_temp_ms,avg_fan_ms,avg_slow_ms,avg_dispatch_ms\n");

            File.AppendAllText(path, string.Join(',',
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss'Z'", global::System.Globalization.CultureInfo.InvariantCulture),
                _timingTicks,
                _sumTotal / _timingTicks, _sumTemp / _timingTicks, _sumFan / _timingTicks,
                _sumSlow / _timingTicks, _sumDispatch / _timingTicks) + "\n");
        }
        catch { /* diagnostics must never be able to stop the poll */ }

        _timingTicks = 0;
        _sumTotal = _sumTemp = _sumFan = _sumSlow = _sumDispatch = 0;
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
        Smu?.Dispose();
        _bios.Dispose();
    }
}
