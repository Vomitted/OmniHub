// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Fan;

/// <summary>
/// Estimates where the temperature is heading, so the fan can start ramping before the
/// heat actually arrives instead of always chasing it.
///
/// Originally ported from OmniControlSuite's ThermalTrendEstimator, which despite its name
/// ("NeuralThermalPredictor") contained no neural network and no Kalman filter -- it was an
/// alpha-beta-gamma tracking filter. That recursion has since been replaced entirely by the
/// least-squares fit below (see Ingest for why), so none of the original maths remains; what
/// carried over is the idea and the instance-scoped state.
///
/// The filter tracks temperature and its rate of change (velocity, C/s), then extrapolates
/// linearly. The exponential dissipation term damps the forecast the further out it looks.
/// </summary>
public sealed class ThermalTrend
{
    private const int MaxHistory = 10;

    // The clamp exists because the filter is fed a BIOS temperature quantised to whole
    // degrees: a 1C step across a short dt produces a large apparent velocity. 2 C/s is
    // already aggressive for a laptop die responding to a step load; the previous 5.0 allowed
    // a projection of 50 C over a 10-second horizon, which is not a temperature, it is an
    // artefact.
    private const double MaxVelocityCPerSec = 2.0;

    /// <summary>
    /// Hard ceiling on how far a forecast may depart from the current filtered temperature.
    /// The forecast feeds a fan curve, so a wrong number is not a cosmetic problem: it drives
    /// the hardware. This bound is what keeps a noisy sensor from producing a step change in
    /// fan speed.
    /// </summary>
    private const double MaxForecastDeltaC = 12.0;

    private readonly object _lock = new();

    // Temperature and its timestamp travel together, so the window's start time rolls with
    // the window. Keeping the start time in a separate field looks equivalent and is not: it
    // only ever recorded the FIRST sample the object had seen, so the span it was divided
    // over kept growing while the sample count stayed pinned at MaxHistory. After an hour of
    // 2-second polls the computed interval was minutes rather than seconds, the (Beta/dt)
    // correction term collapsed toward zero, and the filter quietly stopped tracking -- with
    // no error, and a forecast that still looked plausible.
    private readonly Queue<(double TempC, DateTime At)> _samples = new();

    private double _filteredTemp;
    private double _velocity;

    /// <summary>False until enough samples have arrived for the forecast to mean anything.</summary>
    public bool HasEnoughData { get; private set; }

    /// <summary>Current smoothed temperature, in C. Not the raw reading.</summary>
    public double FilteredTempC { get { lock (_lock) return _filteredTemp; } }

    /// <summary>Rate of change, in C per second. Positive means heating up.</summary>
    public double VelocityCPerSec { get { lock (_lock) return _velocity; } }

    public void Ingest(double rawTempC, DateTime timestampUtc)
    {
        lock (_lock)
        {
            if (_samples.Count == 0) _filteredTemp = rawTempC;

            _samples.Enqueue((rawTempC, timestampUtc));
            while (_samples.Count > MaxHistory) _samples.Dequeue();

            if (_samples.Count < 3)
            {
                _filteredTemp = rawTempC;
                HasEnoughData = false;
                return;
            }

            // Mean interval across the RETAINED window: oldest still-held sample to newest,
            // divided by the gaps between them. Averaging rather than using the gap since the
            // last sample alone, because the poll loop's spacing drifts as it awaits hardware
            // I/O and one short interval would otherwise spike the velocity term.
            var oldest = _samples.Peek().At;
            double span = (timestampUtc - oldest).TotalSeconds;
            double dt = Math.Max(0.2, span / (_samples.Count - 1));

            // A gap far longer than the poll interval means the machine slept, or the loop
            // stalled. Extrapolating a rate of change across a discontinuity produces a
            // fabricated trend, so the filter restarts from this sample instead.
            if (dt > 30)
            {
                _samples.Clear();
                _samples.Enqueue((rawTempC, timestampUtc));
                _filteredTemp = rawTempC;
                _velocity = 0;
                HasEnoughData = false;
                return;
            }

            // Least-squares straight line through the retained window, rather than a
            // recursive alpha-beta-gamma filter.
            //
            // The recursive filter was the wrong instrument for this signal. It carries
            // internal state that compounds, and it estimated a second derivative -- from a
            // sensor quantised to whole degrees, sampled every ~2.4s, that jumps several
            // degrees between readings. Differentiating that twice yields noise with units,
            // and in practice the acceleration term sat pinned at its clamp and threw the
            // forecast 30 C either side of reality while the die was thermally steady.
            //
            // A regression over the window has no accumulating state to diverge: every
            // estimate is computed fresh from the samples actually held, so a bad reading
            // ages out instead of persisting. Quantisation error largely cancels across ten
            // points, which is exactly the noise this sensor has.
            (_filteredTemp, _velocity) = FitLine(timestampUtc);
            _velocity = Math.Clamp(_velocity, -MaxVelocityCPerSec, MaxVelocityCPerSec);
            HasEnoughData = true;
        }
    }

    /// <summary>
    /// Projected temperature <paramref name="secondsAhead"/> from now, in C. Returns the
    /// current filtered temperature when there is not yet enough history to extrapolate, so
    /// a caller never has to special-case startup.
    /// </summary>
    public double ForecastC(double secondsAhead)
    {
        lock (_lock)
        {
            if (!HasEnoughData || secondsAhead <= 0) return _filteredTemp;

            // LINEAR projection only. The textbook form adds 0.5*a*t^2, and that term is what
            // made this unusable: at a 10-second horizon it contributed +/-75 C from a sensor
            // quantised to whole degrees, driving the forecast onto both clamp rails and --
            // since the curve consumes max(measured, forecast) -- slamming the fan between
            // 80% and 100% on nothing but noise. Acceleration is no longer estimated at all.
            double dissipation = Math.Exp(-0.02 * secondsAhead);
            double delta = _velocity * secondsAhead * dissipation;

            // Second guard, independent of the velocity clamp: bounds how far any forecast
            // may sit from reality regardless of how the terms combine.
            delta = Math.Clamp(delta, -MaxForecastDeltaC, MaxForecastDeltaC);

            return Math.Clamp(_filteredTemp + delta, 25.0, 110.0);
        }
    }

    /// <summary>
    /// Ordinary least squares over the retained window, with time measured in seconds
    /// relative to <paramref name="now"/> so the intercept IS the value at the present
    /// instant. Returns (smoothed temperature now, slope in C/s).
    ///
    /// Degenerate input -- every sample at the same timestamp, which would divide by zero --
    /// falls back to the newest reading and a zero slope rather than producing infinity.
    /// </summary>
    private (double Temp, double Slope) FitLine(DateTime now)
    {
        double sumX = 0, sumY = 0, sumXX = 0, sumXY = 0;
        int n = 0;

        foreach (var (temp, at) in _samples)
        {
            double x = (at - now).TotalSeconds; // negative, newest == 0
            sumX += x;
            sumY += temp;
            sumXX += x * x;
            sumXY += x * temp;
            n++;
        }

        double denominator = (n * sumXX) - (sumX * sumX);
        if (n < 2 || Math.Abs(denominator) < 1e-9)
            return (_samples.Last().TempC, 0);

        double slope = ((n * sumXY) - (sumX * sumY)) / denominator;
        double intercept = (sumY - (slope * sumX)) / n;
        return (intercept, slope);
    }
}
