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
    /// Builds the curve for the rail in force.
    ///
    /// A rail with fewer than two points falls back to the shipped default rather than throwing.
    /// <see cref="FanCurve"/>'s constructor rejects a one-point curve, and a hand-edited or
    /// half-written settings file reaching that constructor would take the fan loop down with it --
    /// which is the one loop on this machine whose failure is a thermal problem, not a UI one.
    /// </summary>
    public static FanCurve Build(bool onBattery, bool separateBatteryCurve, CurveRail mains, CurveRail battery)
    {
        var rail = Pick(onBattery, separateBatteryCurve, mains, battery);

        return new FanCurve(rail.Points is { Count: >= 2 } p ? p : FanCurve.DefaultPoints)
        {
            FloorTempC = rail.FloorTempC,
            FloorLevelPercent = rail.FloorLevelPercent,
        };
    }
}
