using OmniHub.Core.Hardware;

namespace OmniHub.Core.Fan;

/// <summary>
/// Polls temperature, evaluates the curve, and (re-)applies fan levels.
/// Re-applies on every tick even if the level hasn't changed -- the BIOS
/// can silently revert to auto control mid-session, so "set once and
/// trust it" is not reliable on this hardware family.
/// </summary>
public sealed class FanService : IDisposable
{
    private readonly FanController _fan;
    private readonly Func<TemperatureReading> _readTemperature;
    private readonly FanCurve _curve;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _cts;

    private Task? _loop;

    /// <summary>
    /// True only while the curve loop is genuinely alive.
    ///
    /// This used to test the cancellation token alone, which made it lie in exactly the case
    /// that mattered. RunAsync's first statement was an unguarded BIOS call, so a transient
    /// WMI failure faulted the task before the loop ran a single tick; Task.Run's result was
    /// discarded, so nothing observed the exception; the token was never cancelled, so this
    /// property kept answering true. The service reported itself running, the UI agreed, and
    /// the fans were never commanded again until the app was restarted.
    /// </summary>
    public bool IsRunning => _cts is { IsCancellationRequested: false } && _loop is { IsCompleted: false };

    /// <summary>
    /// Message from the last failed tick, or null when the last tick succeeded. Recorded so a
    /// loop that is running but achieving nothing is diagnosable rather than invisible.
    /// </summary>
    public string? LastError { get; private set; }
    public FanCurve Curve => _curve;
    public byte LastCommandedLevelPercent { get; private set; }

    /// <summary>
    /// False until the curve loop has completed a tick and actually computed a level.
    ///
    /// Exists because LastCommandedLevelPercent defaults to 0, and 0 is a meaningful fan
    /// level rather than a sentinel. Anything reading it before the first tick -- the thermal
    /// log did -- records a commanded 0% that never happened. In a fan log specifically that
    /// is worse than a missing row: a spurious 0% while hot looks exactly like the
    /// fan-stops-while-hot failure this whole app exists to detect.
    /// </summary>
    public bool HasCommanded { get; private set; }
    public double LastReadTempC { get; private set; }

    /// <summary>
    /// Trend of the CONTROL temperature -- the hotter of CPU and GPU -- which is what the
    /// curve steers on and what the predictive lead forecasts.
    /// </summary>
    public ThermalTrend Trend { get; } = new();

    // The CPU-only trend used to live here, but this service stops whenever the fan mode
    // leaves Auto, which froze the display. It is now HardwareContext.CpuTrend, fed by the
    // poll loop that never stops.

    /// <summary>
    /// How far ahead the curve is allowed to look, in seconds. 0 disables prediction and
    /// restores the original behaviour exactly. Around 10s is the useful range on this
    /// hardware: long enough to beat the die's thermal ramp, short enough that the forecast
    /// still means something.
    /// </summary>
    public double PredictiveLeadSeconds { get; set; }

    /// <summary>Temperature the curve was actually evaluated against last tick, in C. Equals
    /// the measured value unless prediction raised it.</summary>
    public double LastEffectiveTempC { get; private set; }

    /// <summary>
    /// True when the last reading sat on the thermal zone's ceiling, meaning the real
    /// temperature is unknown but at least that high, and the fan has been forced to maximum
    /// as a result. Surfaced so the UI can say so rather than showing a confident number.
    /// </summary>
    public bool SensorCeilingReached { get; private set; }

    public event Action<double, byte>? OnTick;

    /// <summary>Which sensor the last tick acted on. Null until the first tick has run.</summary>
    public TemperatureSource? TemperatureSource { get; private set; }

    /// <summary>
    /// Optional second heat source for the curve, typically the discrete GPU.
    ///
    /// One pair of fans cools the whole chassis, so a curve driven by CPU temperature alone is
    /// blind to a gaming load: the die can sit at 70C while the GPU is at 85C and the machine
    /// throttles anyway. The curve evaluates against whichever is hotter.
    ///
    /// Null when there is no discrete GPU, or when the reading is unavailable -- which must
    /// leave the CPU reading untouched rather than pulling the control temperature down.
    /// </summary>
    public Func<double?>? ReadSecondaryTempC { get; set; }

    public FanService(FanController fan, Func<TemperatureReading> readTemperature, FanCurve curve, TimeSpan? interval = null)
    {
        _fan = fan;
        _readTemperature = readTemperature;
        _curve = curve;
        _interval = interval ?? TimeSpan.FromSeconds(2);
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        // Task.Run, not a bare call: RunAsync is an async method, and C# runs an
        // async method's body synchronously on the CALLING thread up until its
        // first await. RunAsync's first lines are BIOS calls (SetFanMode, then a
        // full curve evaluation including SetFanLevel) -- called directly from a
        // UI click handler, that chain of hardware calls was blocking the UI
        // thread for its entire duration before ever yielding.
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken token)
    {
        bool modeTaken = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                // Taking manual control lives INSIDE the loop, inside this try.
                //
                // It used to be the first statement of this method, outside every handler, so
                // one transient failure on this single BIOS call killed the whole loop before
                // it ran a tick -- silently, because Task.Run's result was discarded and the
                // cancellation token stayed uncancelled. The fans then did nothing at all
                // until the app was relaunched, which is exactly how it was reported.
                //
                // Retrying it each tick until it succeeds also covers what it was written for:
                // the BIOS refusing manual control at the moment the service happens to start.
                //
                // It is re-asserted on the periodic refresh below as well -- see there.
                var reading = _readTemperature();
                double temp = reading.Celsius;
                TemperatureSource = reading.Source;

                // The hotter of CPU and GPU drives the fan, because one set of fans cools both.
                // Taken as a max rather than an average: averaging a 60C CPU with an 85C GPU
                // produces 72C, which is a temperature nothing in the machine is actually at
                // and which would under-cool the part that needs it.
                double? secondary = null;
                try { secondary = ReadSecondaryTempC?.Invoke(); } catch { }
                if (secondary is double gpu && gpu > temp) temp = gpu;

                Trend.Ingest(temp, DateTime.UtcNow);

                // Predictive lead: evaluate the curve against whichever is HIGHER, the
                // measured temperature or the forecast. Taking the max is the whole safety
                // argument -- the curve is monotonic, so feeding it a higher temperature can
                // only ever command more airflow, never less. A wrong forecast therefore
                // costs some fan noise; it can never cause the fan to back off while the
                // machine is hot, which is the exact failure this app exists to prevent.
                double effectiveTemp = temp;
                if (PredictiveLeadSeconds > 0)
                    effectiveTemp = Math.Max(temp, Trend.ForecastC(PredictiveLeadSeconds));

                byte levelPercent = _curve.Evaluate(effectiveTemp);

                // The ACPI zone saturates at its ceiling and reports that same value however
                // much hotter the die actually gets (measured: it pins at 85.05C through
                // sustained 100% load). Evaluating the curve at the ceiling would command
                // whatever 85C maps to -- around 70% -- while the real temperature could be
                // 95C and climbing.
                //
                // Once the sensor is blind, the only honest response is maximum airflow: the
                // reading is a floor, not a measurement. This costs fan noise in the one
                // situation where noise is the right trade, and it is the same principle as
                // the safety floor -- never quiet the fan on the strength of a number that
                // cannot be trusted.
                //
                // Asks the READING whether it is ceiling-limited rather than testing the
                // number, because that is now a property of the sensor and not of the value.
                // A Tctl reading of 85C is a real 85C and should be cooled as such; an ACPI
                // reading of 85C means the zone has run out of range and the die could be
                // anywhere above it. Testing the bare number would force maximum fan on every
                // genuine 85C die reading, which is a lot of noise for no information.
                // ...but only once it has PERSISTED. A single saturated sample is not evidence
                // of a thermal emergency, and at startup it is close to guaranteed: the SMU
                // may not have opened yet, the zone is cached for six seconds, and it powers
                // up sitting on its ceiling. Answering the first such sample with 5600 RPM is
                // the "jet engine every time I boot" behaviour, fired by a sensor that had
                // merely not warmed up.
                //
                // Five consecutive samples is about twelve seconds -- long enough to outlast
                // the driver-open retry and the zone's own cache, short enough that a sensor
                // which really has gone blind while hot still reaches full speed inside the
                // fan's own six-second spin-up. The curve stays free to command 100% on the
                // temperature alone meanwhile; this suppresses only the override.
                if (reading.IsCeilingLimited)
                {
                    _ceilingTicks++;
                    if (_ceilingTicks >= CeilingTicksBeforeMaxFan)
                    {
                        levelPercent = 100;
                        SensorCeilingReached = true;
                    }
                }
                else
                {
                    _ceilingTicks = 0;
                    SensorCeilingReached = false;
                }

                // Per-fan maxima: the two fans do not share a ceiling on this chassis.
                byte raw1 = PercentToRaw(levelPercent, MaxRawLevelFan1);
                byte raw2 = PercentToRaw(levelPercent, MaxRawLevelFan2);

                // Only write when the level actually changes, plus a slow refresh.
                //
                // This loop used to re-send the same value every tick on the belief that the
                // BIOS silently reverts manual control. That was tested: a level commanded once
                // and then left completely alone held at exactly the commanded value for a full
                // minute, with HP's own OmenCap service running. There is no keep-alive
                // requirement here, so re-sending an unchanged level cost one hpqBIntM round
                // trip every two seconds and bought nothing -- and WMI round trips are the
                // single largest cost this app has.
                //
                // The periodic refresh stays because "no revert within a minute" is not the
                // same as "never reverts": sleep, resume and a firmware mode change are all
                // plausible and none of them were tested.
                bool levelChanged = raw1 != _lastRaw1 || raw2 != _lastRaw2;
                bool refreshDue = DateTime.UtcNow - _lastFanWriteUtc >= FanRefreshInterval;

                if (levelChanged || refreshDue)
                {
                    // Manual control is RE-ASSERTED here, not taken once at startup.
                    //
                    // SetFanMode is the call that lifts the fans off the BIOS curve, and
                    // SetFanLevel is ignored by the EC unless that mode is currently held.
                    // Asserting it once and assuming it sticks was the rest of "the fans stop
                    // and only a restart brings them back": when the EC reclaims control --
                    // sleep, resume, a firmware mode change, the vendor's own service -- every
                    // later level write goes nowhere, the BIOS curve takes over with its
                    // 0%-while-hot entry, and nothing here noticed because the level writes
                    // carried on "succeeding". Restarting the app worked purely because Start
                    // ran this assert again.
                    //
                    // The comment above already said "no revert within a minute" is not "never
                    // reverts", which is exactly why the level refresh exists. The mode assert
                    // simply was not part of it. It costs one extra BIOS call per 30 seconds,
                    // on the same schedule as a write that was already happening.
                    if (!modeTaken || refreshDue)
                    {
                        _fan.SetFanMode(FanMode.Performance);
                        modeTaken = true;
                    }

                    _fan.SetFanLevel(raw1, raw2);
                    _lastRaw1 = raw1;
                    _lastRaw2 = raw2;
                    _lastFanWriteUtc = DateTime.UtcNow;
                }

                LastReadTempC = temp;
                LastEffectiveTempC = effectiveTemp;
                LastCommandedLevelPercent = levelPercent;
                HasCommanded = true; // set only after the level was genuinely computed and sent
                LastError = null;
                OnTick?.Invoke(temp, levelPercent);
            }
            catch (Exception ex)
            {
                // Deliberately broad and deliberately non-fatal: nothing a single tick can do
                // may end this loop, because a dead fan loop is silent and the machine keeps
                // getting hotter. Recorded rather than swallowed so "the fans did nothing" can
                // be diagnosed instead of guessed at.
                LastError = ex.Message;
            }

            // OperationCanceledException, not just TaskCanceledException: the token can also
            // surface the base type, and letting that escape would end the loop through the
            // one path that does not set HasCommanded or restore automatic control.
            try { await Task.Delay(_interval, token); }
            catch (OperationCanceledException) { break; }
        }
    }

    // The raw SetFanLevel byte is NOT a 0-255 PWM duty cycle -- it's a fan-speed
    // target in units of ~100 RPM (confirmed via OmenHwCtl's decompiled Omen
    // Gaming Hub source, where the working -SetFanLevel handler literally logs
    // "raw * 100 rpm" before sending the same command this app uses).
    // 255 specifically is not "max" -- OmenMon's own suspend/shutdown path sends
    // {0xFF,0xFF} deliberately to release control back to the BIOS's own
    // automatic curve, which matches this app's earlier finding that raw=255
    // made the fan quieter instead of louder.
    //
    // The ceiling below is MEASURED, and measured the right way -- which took two attempts.
    //
    // The first attempt engaged the BIOS's own max-fan command and watched GetFanLevel settle
    // at 54 and 52. That looked like a hardware ceiling and was recorded as one. It was not:
    // it is HP's max-fan POLICY, which is a different and lower thing.
    //
    // Driving the fans directly instead, commanding 40/48/54/57/60/63/70/80 and letting each
    // settle for six seconds, the readback tracks the command exactly up to 54 and then pins
    // at 56 for every value above it. 56 is where the EC actually clamps, on both fans -- so
    // the earlier per-fan split was an artifact of the wrong experiment too, and capping at
    // 54/52 was leaving roughly 200-400 RPM unused.
    //
    // Worth knowing when reading GetFanLevel back: it is a real tachometer, not an echo of
    // what was last set. A 25 -> 54 step ramps 36/38/40/43/45/48/50/52/54 across about six
    // seconds, so a readback WILL disagree with the commanded level mid-ramp. That is the
    // fan spinning up, not a bug.
    // The floor was measured the same way, and it was not 20 either. Commanding 0/10/15/20/25/30
    // and letting each settle for nine seconds, the readback tracks the command exactly from 10
    // upward: raw 10 really is 1000 rpm. Only 0 behaves differently, settling at 22 because it
    // hands the fan back to the BIOS. Carrying a floor of 20 cost a full 1000 rpm of silence at
    // the quiet end of every curve for no reason other than that nobody had checked.
    /// <summary>How often the fan level is re-sent even when it has not changed.</summary>
    private static readonly TimeSpan FanRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>Consecutive ceiling-limited readings required before the fan is forced to
    /// maximum. At a 2s poll this is about twelve seconds.</summary>
    private const int CeilingTicksBeforeMaxFan = 5;
    private int _ceilingTicks;

    private byte _lastRaw1, _lastRaw2;
    private DateTime _lastFanWriteUtc = DateTime.MinValue;

    /// <summary>
    /// The raw band this chassis runs, set once at startup from a per-model profile where one
    /// exists and left at the measured default where none does.
    ///
    /// Static rather than an instance member because the conversions below are static and are
    /// called from the UI as well as from the loop, and because this describes the machine the
    /// process is running on, of which there is exactly one. Assigned during startup and not
    /// afterwards; HardwareContext is the only writer.
    /// </summary>
    public static FanCalibration Calibration { get; set; } = FanCalibration.Default;

    private static byte MinRawLevel => Calibration.MinRawLevel;
    private static byte MaxRawLevelFan1 => Calibration.MaxRawLevelFan1;
    private static byte MaxRawLevelFan2 => Calibration.MaxRawLevelFan2;

    // percent==0 must map to raw 0, not MinRawLevel -- the whole point of the
    // curve's flat 0% segment through true idle (<=40C, see FanCurve.CreateDefault)
    // is a silent fan, and raw is a real RPM/100 target elsewhere in this codebase
    // (see FanController.SetFanLevel), so raw=0 is literally "target 0 RPM," not a
    // fabricated new meaning. Without this special case every "silent" 0% tick was
    // actually being sent as raw=20 (~2000 RPM) -- audibly running, never silent.
    // MinRawLevel/MaxRawLevel remain the correct scale for percent in (0,100].
    private static byte PercentToRaw(byte percent, byte maxRaw) => percent == 0
        ? (byte)0
        : (byte)Math.Round(MinRawLevel + percent / 100.0 * (maxRaw - MinRawLevel));

    /// <summary>
    /// Inverse of <see cref="PercentToRaw"/>, for displaying a level read back from
    /// the BIOS (GetFanLevel) as a percentage. Lives here so both directions share the
    /// one MinRawLevel/MaxRawLevel definition -- the UI previously did its own
    /// raw/255*100 conversion, which is the 0-255 PWM assumption this whole file
    /// documents as wrong, and under-reported every readback by roughly half.
    /// Raw values under MinRawLevel clamp to 0 rather than going negative.
    /// </summary>
    public static byte RawToPercent(byte raw) => raw == 0
        ? (byte)0
        : (byte)Math.Clamp(Math.Round((raw - MinRawLevel) * 100.0 / (MaxRawLevelFan1 - MinRawLevel)), 0, 100);

    /// <summary>
    /// Approximate RPM for a raw fan level, for display. The raw byte is an RPM/100 target,
    /// so this is a unit conversion rather than an estimate -- but it is the target the EC was
    /// given, not a tachometer reading, and the two differ while a fan is still spinning up.
    /// </summary>
    public static int RawToRpm(byte raw) => raw * 100;

    /// <summary>
    /// The raw EC level a curve percentage maps to, and the RPM that implies.
    ///
    /// Public counterpart to <see cref="RawToPercent"/> so the UI never has to reimplement the
    /// mapping -- it did once, using the debunked raw/255 PWM assumption, and under-reported
    /// every fan figure on screen by roughly half.
    /// </summary>
    public static byte PercentToRawLevel(byte percent) => PercentToRaw(percent, MaxRawLevelFan1);

    /// <summary>Lowest and highest RPM this chassis will actually run the fans at, measured.
    /// Exposed so the UI can tell the user what the percentage scale spans.</summary>
    public static int MinRpm => RawToRpm(MinRawLevel);
    public static int MaxRpm => RawToRpm(MaxRawLevelFan1);

    public void Stop()
    {
        _cts?.Cancel();
        // Cleared with the loop: after a stop the last level is history, not a live command,
        // and a restarted service must not report a stale figure as current.
        HasCommanded = false;

        // Guarded for the same reason SetFanMode now is: this is a BIOS call, callers reach it
        // from mode buttons and from shutdown, and a throw here used to escape into whichever
        // of those happened to be running.
        try { _fan.RestoreAutomaticControl(); }
        catch (Exception ex) { LastError = ex.Message; }
    }

    public void Dispose() => Stop();
}
