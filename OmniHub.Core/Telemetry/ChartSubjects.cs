namespace OmniHub.Core.Telemetry;

/// <summary>
/// One line on a chart: what to call it, how to write its values, and which field of a sample it
/// reads.
/// </summary>
/// <param name="Select">
/// Returns null where the sample has no reading. That is the whole contract: the writer leaves an
/// empty field when a sensor did not answer, the reader keeps it null, and a selector that turned
/// it into a zero here would undo both at the last possible moment -- a flat line along the bottom
/// of a chart, which reads as a measurement of nothing rather than as nothing measured.
/// </param>
public sealed record ChartLine(
    string Name,
    string Unit,
    string Format,
    Func<ThermalSample, double?> Select,
    double? Min = null,
    double? Max = null,
    bool Primary = false);

/// <summary>A chart somebody can place: a title and the lines it draws.</summary>
public sealed record ChartSubject(string Key, string Title, IReadOnlyList<ChartLine> Lines);

/// <summary>
/// The charts a workspace can hold.
///
/// Here rather than beside the control for the reason the rest of the telemetry maths is: the test
/// project cannot reference the application project, so a subject defined in a WPF control is a
/// subject nobody can assert. What is asserted is the part that matters -- that a line reads the
/// field it claims to, and that a missing reading stays missing.
///
/// Deliberately not one subject per column. A chart of die temperature alone answers less than one
/// showing the temperature against the fan that was responding to it, and the pairs below are the
/// ones where the second line explains the first.
/// </summary>
public static class ChartSubjects
{
    public static IReadOnlyList<ChartSubject> All { get; } = new[]
    {
        new ChartSubject("thermal", "Temperature and fan", new[]
        {
            new ChartLine("die temp", " C", "0.#", s => s.TempC, Primary: true),
            new ChartLine("commanded", "%", "0", s => s.CommandedPercent, Min: 0, Max: 100),
        }),

        // The columns added when the log learned to record the discrete GPU. Nothing has read them
        // back until now, which is the gap this subject exists to close.
        new ChartSubject("gpu", "GPU temperature and power", new[]
        {
            new ChartLine("gpu temp", " C", "0", s => s.GpuTempC, Primary: true),
            new ChartLine("gpu power", "W", "0.#", s => s.GpuWatts),
        }),

        new ChartSubject("power", "Package power", new[]
        {
            new ChartLine("package", "W", "0.#", s => s.PackageWatts, Primary: true),
        }),

        // How hard the binding constraint was being pressed, whichever one it was. The name of it
        // is in the log too, but a line cannot draw a name -- the limit panel says which.
        new ChartSubject("limit", "How close to the limit", new[]
        {
            new ChartLine("limit", "%", "0", s => s.LimitPercent, Min: 0, Max: 100, Primary: true),
        }),
    };

    public static ChartSubject? Find(string key)
    {
        foreach (var subject in All)
            if (subject.Key == key) return subject;

        return null;
    }
}
