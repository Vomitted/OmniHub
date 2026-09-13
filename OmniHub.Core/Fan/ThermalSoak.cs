namespace OmniHub.Core.Fan;

/// <summary>
/// Tracks how much heat the chassis has accumulated, so the fan can respond to thermal MASS and
/// not only to the temperature of the moment.
///
/// The curve on its own is a proportional controller: it looks at the die right now and picks a
/// level. That is correct while the temperature is the whole story, and it is wrong after a long
/// load, because a die and a heatsink cool at completely different rates. The die falls within a
/// second or two of the work stopping; the heatsink, the paste and the chassis are still
/// saturated and have nowhere to send their heat. Back the fan off on the strength of the die
/// reading alone and the accumulated heat flows straight back into it, which is the rebound
/// everyone recognises: temperature drops, the fan quietens, and half a minute later it is hot
/// again.
///
/// This is the integral term that answers that. It accumulates time spent above a baseline and
/// decays it with a half-life, so a long session at 80C leaves a large residue while a two-second
/// spike to 80C leaves almost none. The residue buys extra airflow until the heat behind it has
/// actually been carried away.
///
/// SAFETY. The boost is only ever ADDED, and it is bounded. That is the same argument the
/// predictive lead rests on: the curve is monotonic, so handing it more airflow can never quieten
/// the fan, and a wrong soak estimate therefore costs noise and cannot cost cooling. Nothing here
/// can produce the fan-stops-while-hot failure this application exists to prevent.
/// </summary>
public sealed class ThermalSoak
{
    /// <summary>
    /// Heat below this is not accumulating anywhere that matters. Sits just above the curve's own
    /// first active point at 55C, so ordinary idling never builds a residue at all.
    /// </summary>
    public double BaselineC { get; init; } = 62.0;

    /// <summary>
    /// How long accumulated heat takes to half-decay once the machine is back at baseline.
    ///
    /// Ninety seconds is the order of a laptop heatsink's own time constant rather than a tuned
    /// figure, and it is deliberately on the short side: too long and the fan stays up after the
    /// heat has genuinely gone, which is the "cool and loud" complaint the session report exists
    /// to catch.
    /// </summary>
    public double HalfLifeSeconds { get; init; } = 90.0;

    /// <summary>
    /// Percentage points of fan added per degree-minute of accumulated heat.
    ///
    /// With the cap below, a sustained ten degrees over baseline reaches the ceiling in roughly
    /// two minutes, which is about how long this chassis takes to saturate under an all-core load.
    /// </summary>
    public double GainPercentPerDegreeMinute { get; init; } = 1.2;

    /// <summary>
    /// Hardest the soak term may push, in percentage points.
    ///
    /// Capped so this can never become the dominant input. The curve still decides the fan level;
    /// this nudges it for heat the curve cannot see. An uncapped integral term is how a controller
    /// ends up pinned at maximum for reasons nobody can reconstruct afterwards.
    /// </summary>
    public double MaxBoostPercent { get; init; } = 18.0;

    private readonly object _lock = new();
    private double _degreeMinutes;
    private DateTime _lastUtc = DateTime.MinValue;

    /// <summary>Accumulated heat, in degree-minutes above the baseline.</summary>
    public double DegreeMinutes { get { lock (_lock) return _degreeMinutes; } }

    /// <summary>Extra fan percentage the accumulated heat currently justifies. Never negative.</summary>
    public double BoostPercent
    {
        get
        {
            lock (_lock) return Math.Clamp(_degreeMinutes * GainPercentPerDegreeMinute, 0, MaxBoostPercent);
        }
    }

    /// <summary>
    /// Folds one reading in.
    ///
    /// Integrated against the ACTUAL elapsed time rather than an assumed tick interval, because
    /// this loop does not run on a fixed period: it re-arms after each tick, and the real cadence
    /// was measured at 2.31s against a nominal 2s. Assuming the nominal figure would under-count
    /// every accumulation by whatever the drift happened to be that day.
    /// </summary>
    public void Ingest(double tempC, DateTime utcNow)
    {
        lock (_lock)
        {
            if (_lastUtc == DateTime.MinValue) { _lastUtc = utcNow; return; }

            double seconds = (utcNow - _lastUtc).TotalSeconds;
            _lastUtc = utcNow;

            // A gap this long is the machine having slept or the application having been closed,
            // not one enormous sample. Carrying it into the integral would manufacture an hour of
            // accumulated heat out of an hour of the laptop being shut.
            if (seconds > 60)
            {
                _degreeMinutes = 0;
                return;
            }

            // Zero or backwards is NOT a gap, and must not clear anything.
            //
            // These were originally folded into the reset above, which made two samples sharing a
            // timestamp wipe the entire accumulation. That is not hypothetical on a real machine:
            // a duplicated tick, a coarse clock, or simply two callers reading in the same
            // millisecond would silently throw away the residue at the exact moment the fan was
            // relying on it. Nothing has elapsed, so there is nothing to integrate; skip.
            if (seconds <= 0) return;

            // Decay first, then accumulate. The other order would exempt the newest sample from
            // the decay it is about to be subject to, biasing the residue upward by exactly one
            // tick's worth on every single call.
            _degreeMinutes *= Math.Pow(0.5, seconds / HalfLifeSeconds);

            double over = tempC - BaselineC;
            if (over > 0) _degreeMinutes += over * (seconds / 60.0);
        }
    }

    /// <summary>Clears the accumulation. Used on resume, where the readings either side describe different conditions.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _degreeMinutes = 0;
            _lastUtc = DateTime.MinValue;
        }
    }
}
