namespace OmniHub.Core.Fan;

/// <summary>One rail's stored curve: the points and the safety floor that goes with them.</summary>
public readonly record struct CurveRail(IReadOnlyList<CurvePoint>? Points, double FloorTempC, byte FloorLevelPercent);

/// <summary>
/// Which of the two stored curves is in force, and turning it into a usable <see cref="FanCurve"/>.
///
/// This lives in Core rather than beside the settings class it is called from, because the settings
/// class is in the App project and the test project cannot reference the App project. The decision
/// here is small but it is the one that decides how loud the machine is on battery, so it is worth
/// being able to assert rather than to read.
/// </summary>
/// <summary>
/// Every curve this machine stores: two fans across two power rails.
///
/// Four, but not four decisions. Each of the two switches is independent and each defaults off,
/// so a machine nobody has configured holds the same curve in all four cells and behaves exactly
/// as it did before either feature existed.
/// </summary>
public readonly record struct CurveSet(
    CurveRail MainsFan1,
    CurveRail MainsFan2,
    CurveRail BatteryFan1,
    CurveRail BatteryFan2);

public static class CurveRails
{
    /// <summary>
    /// The battery rail only wins when the user has asked for two curves.
    ///
    /// The order matters: a battery curve left over from a time the feature was enabled must not
    /// quietly take over again when it is switched off.
    /// </summary>
    public static CurveRail Pick(bool onBattery, bool separateBatteryCurve, CurveRail mains, CurveRail battery)
        => onBattery && separateBatteryCurve ? battery : mains;

    /// <summary>
    /// Which of the four stored curves applies to one fan on one power rail.
    ///
    /// Composed from the rail choice rather than written out as four cases, so the two switches
    /// cannot drift apart: the second fan on battery falls back to the second fan on mains when
    /// only the rail feature is off, and to the first fan's battery curve when only the fan
    /// feature is off. Either switch alone therefore does exactly what its own name says.
    /// </summary>
    public static CurveRail Pick(CurveSet set, bool onBattery, bool separateBatteryCurve, bool fan2, bool separateFan2Curve)
    {
        bool second = fan2 && separateFan2Curve;

        return Pick(onBattery, separateBatteryCurve,
                    mains:   second ? set.MainsFan2   : set.MainsFan1,
                    battery: second ? set.BatteryFan2 : set.BatteryFan1);
    }

    /// <summary>Builds the curve for one fan on one power rail. See the two-rail overload.</summary>
    public static FanCurve Build(CurveSet set, bool onBattery, bool separateBatteryCurve, bool fan2, bool separateFan2Curve)
        => Build(Pick(set, onBattery, separateBatteryCurve, fan2, separateFan2Curve));

    /// <summary>
    /// Builds the curve for the rail in force.
    ///
    /// A rail with fewer than two points falls back to the shipped default rather than throwing.
    /// <see cref="FanCurve"/>'s constructor rejects a one-point curve, and a hand-edited or
    /// half-written settings file reaching that constructor would take the fan loop down with it --
    /// which is the one loop on this machine whose failure is a thermal problem, not a UI one.
    /// </summary>
    public static FanCurve Build(bool onBattery, bool separateBatteryCurve, CurveRail mains, CurveRail battery)
        => Build(Pick(onBattery, separateBatteryCurve, mains, battery));

    /// <summary>Turns one stored rail into a curve, with the fallback described above.</summary>
    public static FanCurve Build(CurveRail rail)
        => new(rail.Points is { Count: >= 2 } p ? p : FanCurve.DefaultPoints)
        {
            FloorTempC = rail.FloorTempC,
            FloorLevelPercent = rail.FloorLevelPercent,
        };
}
