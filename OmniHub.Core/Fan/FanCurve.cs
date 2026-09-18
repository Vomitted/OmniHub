// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Fan;

public readonly record struct CurvePoint(double TempC, byte LevelPercent);

/// <summary>
/// Temperature to fan-level curve with two protections layered on top of
/// a plain lookup table:
///
///   1. Safety floor -- above <see cref="FloorTempC"/> the fan is never
///      allowed to report/command 0%, which is the exact failure mode
///      hit on Default/Balanced mode (BIOS fan table has a 0% entry
///      that's supposed to mean "idle" but gets hit while still hot).
///
///   2. Hysteresis + ramp-down limiting -- prevents the fan speed from
///      chattering up and down every poll cycle, which is what makes
///      curve-controlled fans sound "constantly loud" compared to the
///      BIOS's own smoother (if less safe) ramping.
/// </summary>
public sealed class FanCurve
{
    private List<CurvePoint> _points;
    private byte _lastLevel;

    public double FloorTempC { get; set; } = 55.0;
    public byte FloorLevelPercent { get; set; } = 15;

    /// <summary>Minimum degrees the temperature must drop before the fan is allowed to step down.</summary>
    public double RampDownDeadbandC { get; init; } = 4.0;

    /// <summary>Max percentage points the fan level may drop in a single evaluation, to avoid abrupt drops.</summary>
    public byte MaxStepDown { get; init; } = 10;

    private double _lastEvalTemp = double.MinValue;

    public FanCurve(IEnumerable<CurvePoint> points)
    {
        _points = points.OrderBy(p => p.TempC).ToList();
        if (_points.Count < 2)
            throw new ArgumentException("Fan curve needs at least 2 points.");
    }

    /// <summary>
    /// Default starting curve, tuned for quiet rather than for airflow -- because airflow was
    /// measured and it does not buy anything here.
    ///
    /// Under a 45-second all-core soak, pinning the fans to their 5600 rpm ceiling instead of
    /// letting the BIOS run them at 3000 rpm produced 2.12C lower die temperature and 0.5%
    /// more throughput. Half a percent is noise. This machine is limited by core current
    /// (measured at 138.7 A against a 140 A EDC ceiling), not by heat, so extra fan speed
    /// converts electricity into sound and almost nothing else.
    ///
    /// Earlier revisions of this curve got steeper each time the fans "felt weak", which was
    /// reasoning from noise rather than from measurement. It now ramps gently through the band
    /// the machine actually sits in and still reaches full speed by 92C, because the safety
    /// argument for the top of the curve never depended on performance.
    /// </summary>
    public static FanCurve CreateDefault() => new(DefaultPoints);

    /// <summary>The default curve's points, so a settings reset restores exactly these.</summary>
    public static IReadOnlyList<CurvePoint> DefaultPoints { get; } = new[]
    {
        new CurvePoint(0, 0),
        new CurvePoint(45, 0),
        new CurvePoint(55, 12),
        new CurvePoint(65, 28),
        new CurvePoint(75, 52),
        new CurvePoint(82, 75),
        new CurvePoint(88, 100),
    };

    public IReadOnlyList<CurvePoint> Points => _points;

    /// <summary>Replaces the lookup table wholesale (e.g. after the user edits it in the Fans tab).</summary>
    public void SetPoints(IEnumerable<CurvePoint> points)
    {
        var sorted = points.OrderBy(p => p.TempC).ToList();
        if (sorted.Count < 2)
            throw new ArgumentException("Fan curve needs at least 2 points.");
        _points = sorted;
    }

    /// <summary>Evaluate the curve for a given temperature and return the target level (0-100%).</summary>
    public byte Evaluate(double tempC)
    {
        byte raw = Interpolate(tempC);

        // Safety floor: never let it drop to 0 while genuinely warm
        if (tempC >= FloorTempC && raw < FloorLevelPercent)
            raw = FloorLevelPercent;

        // Hysteresis on the way down only -- ramping up should stay responsive
        if (raw < _lastLevel)
        {
            bool tempDroppedEnough = (_lastEvalTemp - tempC) >= RampDownDeadbandC;
            if (!tempDroppedEnough)
            {
                raw = _lastLevel;
            }
            else
            {
                int maxDrop = Math.Min(MaxStepDown, _lastLevel);
                raw = (byte)Math.Max(raw, _lastLevel - maxDrop);
            }
        }

        // The reference temperature moves only when the level goes UP.
        //
        // Two earlier versions of this line were both wrong, in opposite directions, and the
        // difference between them is the whole behaviour of the fan on a cooldown.
        //
        // Moving it on every evaluation made the deadband unsatisfiable: on a gradual cooldown
        // each tick lowers the temperature by a fraction of a degree, so "has it dropped 4C
        // since last time" was really "has it dropped 4C in the last two seconds", which never
        // happens outside a crash in load. The fan latched at its peak -- observed at 100% and
        // 5600 rpm while the die sat at 76C.
        //
        // Moving it whenever the level CHANGED fixed that case and introduced a subtler one: a
        // staircase ratchet. Each step down re-anchored to the temperature that had just
        // justified it, so the next step needed another full 4C of cooling BELOW that. On a
        // flat temperature there is no further drop to give, and the fan stops descending
        // partway. Measured on this machine: after a two-second burst, 70% held for seventeen
        // seconds with the die flat at 63.1C, where the curve asks for 25%. Across a 90-minute
        // log, 48% of samples sat 10 or more points above the curve's own answer, including
        // 50% at 44C. That is the "fans spike on light tasks and stay up" report.
        //
        // Anchoring to the last temperature that RAISED the level is what makes this a deadband
        // rather than a ratchet. Going down, the anchor stays at the peak, so once the die is
        // genuinely 4C below it every following tick is free to take another step until the
        // level meets the curve -- the clamp above already stops it undershooting. Going up
        // re-anchors, so the deadband is measured from the new peak.
        if (raw > _lastLevel) _lastEvalTemp = tempC;
        _lastLevel = raw;
        return raw;
    }

    private byte Interpolate(double tempC)
    {
        // Read the field ONCE into a local.
        //
        // Evaluate runs on the fan loop's thread while SetPoints is called from the UI thread
        // when the user applies an edited curve. The assignment in SetPoints is atomic, so a
        // single read always yields a whole, valid list -- but this method used to read the
        // field six separate times, so a swap landing mid-call could size the loop against one
        // list and index into another. A shorter new curve then threw IndexOutOfRange inside
        // the fan tick. Snapshotting makes the whole evaluation see one consistent curve.
        var points = _points;

        if (tempC <= points[0].TempC) return points[0].LevelPercent;
        if (tempC >= points[^1].TempC) return points[^1].LevelPercent;

        for (int i = 0; i < points.Count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            if (tempC >= a.TempC && tempC <= b.TempC)
            {
                double t = (tempC - a.TempC) / (b.TempC - a.TempC);
                return (byte)Math.Round(a.LevelPercent + t * (b.LevelPercent - a.LevelPercent));
            }
        }
        return points[^1].LevelPercent;
    }
}
