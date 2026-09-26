// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Telemetry;

/// <summary>
/// The arithmetic behind the widgets: how full to draw a reading, and how fast to turn a fan.
///
/// In Core rather than in the controls that draw them because the test project cannot reference
/// the application, and "a gauge with no real full scale is not drawn" is a rule worth a test.
/// </summary>
public static class Gauge
{
    /// <summary>
    /// How full to draw <paramref name="value"/> against <paramref name="scale"/>, from 0 to 1.
    ///
    /// Null when there is nothing honest to draw: a missing reading, or a scale that is missing or
    /// not positive. A gauge without a real full scale is decoration that looks like measurement,
    /// so the caller shows the figure alone instead. Out-of-range values are clamped rather than
    /// refused, because a die briefly past its threshold is a full gauge, not a broken one.
    /// </summary>
    public static double? Fraction(double? value, double? scale)
    {
        if (value is not { } v || !double.IsFinite(v)) return null;
        if (scale is not { } s || !double.IsFinite(s) || s <= 0) return null;
        return Math.Clamp(v / s, 0, 1);
    }

    /// <summary>
    /// Seconds per turn for a drawn fan at <paramref name="rpm"/>, or null when it should stand still.
    ///
    /// Deliberately not the real rate. A fan at 5,600 rpm turns ninety-three times a second, which
    /// drawn literally is a blur, or worse a wheel that seems to crawl backwards as the frame rate
    /// beats against it. So the drawing turns once per second at 3,000 rpm, proportionally faster
    /// and slower around that, within limits the eye can follow: faster reads as faster, and the
    /// figure printed beside it carries the real number. Below 100 rpm the fan is stopped and so is
    /// the drawing.
    /// </summary>
    public static double? SecondsPerTurn(double? rpm)
    {
        if (rpm is not { } r || !double.IsFinite(r) || r < 100) return null;
        return Math.Clamp(3000 / r, 0.4, 8);
    }
}
