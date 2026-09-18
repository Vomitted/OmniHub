using OmniHub.Core.Telemetry;

namespace OmniHub.Core.Workspaces;

/// <summary>
/// The panel keys a layout can name.
///
/// Here rather than in the application project because a saved layout, a template and the picker
/// all have to agree about them, and a prefix written out in three places is a prefix that will one
/// day be written out wrong in one of them. The application still owns what a key builds; this owns
/// what a key is called.
/// </summary>
public static class PanelKeys
{
    /// <summary>A single reading. The rest of the key is a <see cref="Metrics"/> key.</summary>
    public const string MetricPrefix = "metric.";

    /// <summary>A chart. The rest of the key is a <see cref="ChartSubjects"/> key.</summary>
    public const string ChartPrefix = "chart.";

    public static string Metric(string metricKey) => MetricPrefix + metricKey;
    public static string Chart(string subjectKey) => ChartPrefix + subjectKey;

    /// <summary>What is limiting the machine, as all five constraints.</summary>
    public const string Limits = "limits";

    /// <summary>The fan curve in force, with the present position on it.</summary>
    public const string FanCurve = "fancurve";

    /// <summary>Every reading, its value and its source.</summary>
    public const string Readings = "readings";
}

/// <summary>
/// Workspaces somebody can start from.
///
/// Offered in the editor rather than shipped in the defaults, deliberately. The promise a fresh
/// install makes is that it looks exactly like the seven screens this application always had, and
/// adding an eighth workspace nobody asked for would break that for the sake of a demonstration.
/// A template is the same content behind a button, taken only when it is wanted.
///
/// Each one is an answer to a question rather than a tour of the panels: what is this machine doing
/// while a game runs, why is it loud, what can it see at all.
/// </summary>
public static class WorkspaceTemplates
{
    public static IReadOnlyList<Workspace> All { get; } = new[]
    {
        // Three readings across the top, then the constraint that explains them, then the shape of
        // the last hour. The order is the order the questions arrive in.
        new Workspace("Gaming", new[]
        {
            new PanelPlacement(PanelKeys.Metric("cpu"), 4),
            new PanelPlacement(PanelKeys.Metric("gpu"), 4),
            new PanelPlacement(PanelKeys.Metric("limit"), 4),
            new PanelPlacement(PanelKeys.Limits),
            new PanelPlacement(PanelKeys.Chart("thermal")),
        }),

        // Why it is loud: both fans, the curve that asked for it, and what the temperature was
        // doing at the time.
        new Workspace("Noise", new[]
        {
            new PanelPlacement(PanelKeys.Metric("fan"), 3),
            new PanelPlacement(PanelKeys.Metric("fan2"), 3),
            new PanelPlacement(PanelKeys.FanCurve, 6),
            new PanelPlacement(PanelKeys.Chart("thermal")),
        }),

        // Where the watts are going, on both halves of the package.
        new Workspace("Power", new[]
        {
            new PanelPlacement(PanelKeys.Metric("pkg"), 4),
            new PanelPlacement(PanelKeys.Metric("gpuw"), 4),
            new PanelPlacement(PanelKeys.Metric("cpuclk"), 4),
            new PanelPlacement(PanelKeys.Chart("power"), 6),
            new PanelPlacement(PanelKeys.Chart("gpu"), 6),
        }),

        // What this application can see at all, which is the first thing to check when something
        // reads wrong.
        new Workspace("Sensors", new[]
        {
            new PanelPlacement(PanelKeys.Readings),
        }),
    };

    public static Workspace? Find(string name)
    {
        foreach (var template in All)
            if (string.Equals(template.Name, name, StringComparison.OrdinalIgnoreCase)) return template;

        return null;
    }
}
