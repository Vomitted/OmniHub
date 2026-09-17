using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniHub.Core.Workspaces;

/// <summary>
/// One panel in a workspace: what to show, and how wide.
///
/// Placement is an order and a width rather than a column and a row. Panels flow left to right and
/// wrap, the way text does, which makes two of the failures a free grid has to defend against
/// impossible to express: nothing can overlap, and nothing can be placed outside the grid. The
/// arrangements a free grid buys over this one are holes in the layout, and nobody wants those.
/// </summary>
/// <param name="Type">
/// A catalogue key. Deliberately a string rather than an enum: the catalogue lives in the
/// application project, a saved layout outlives the build that wrote it, and a type this build
/// does not recognise has to survive being read and written back rather than being dropped.
/// </param>
/// <param name="Span">Width in grid columns, 1 to <see cref="WorkspaceLayout.Columns"/>.</param>
public sealed record PanelPlacement(string Type, int Span = WorkspaceLayout.Columns)
{
    /// <summary>The same panel with its width clamped into the grid.</summary>
    public PanelPlacement Clamped() => this with { Span = Math.Clamp(Span, 1, WorkspaceLayout.Columns) };
}

/// <summary>A named screen: an ordered list of panels.</summary>
public sealed record Workspace(string Name, IReadOnlyList<PanelPlacement> Panels)
{
    public static Workspace Empty(string name) => new(name, Array.Empty<PanelPlacement>());
}

/// <summary>
/// Every workspace the user has, and the file they live in.
///
/// The shape this replaces was seven hard-coded tabs. A layout somebody can rearrange cannot be
/// code, so it is data -- and it lives in Core rather than beside the window it draws because the
/// test project cannot reference the application project, and an untestable layout model is the
/// one piece of this change that would be irresponsible to leave unasserted: it is what stands
/// between a corrupt file and an application that will not open.
/// </summary>
public sealed record WorkspaceLayout(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("workspaces")] IReadOnlyList<Workspace> Workspaces)
{
    /// <summary>The grid every workspace is laid out on. Twelve divides by 2, 3, 4 and 6.</summary>
    public const int Columns = 12;

    /// <summary>
    /// The version this build writes.
    ///
    /// Read permissively and written honestly: a file from a later build opens rather than being
    /// rejected, because the alternative is that installing an older build silently discards the
    /// user's arrangement. Anything in it this build does not understand is preserved where it can
    /// be and ignored where it cannot.
    /// </summary>
    public const int CurrentVersion = 1;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniHub", "workspaces.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// The arrangement a fresh install gets: one workspace per tab this application used to have,
    /// each holding that tab's panel at full width.
    ///
    /// This is the compatibility promise made concrete. Someone who never opens the layout editor
    /// sees the same screens, in the same order, with the same controls on them. The interface is
    /// completely different in what it can become, not in what it takes away.
    /// </summary>
    public static WorkspaceLayout Defaults() => new(CurrentVersion, new[]
    {
        new Workspace("Dashboard",   new[] { new PanelPlacement("dashboard") }),
        new Workspace("Fans",        new[] { new PanelPlacement("fans") }),
        new Workspace("Performance", new[] { new PanelPlacement("performance") }),
        new Workspace("Battery",     new[] { new PanelPlacement("power") }),
        new Workspace("System",      new[] { new PanelPlacement("system") }),
        new Workspace("Diagnostics", new[] { new PanelPlacement("diagnostics") }),
        new Workspace("Settings",    new[] { new PanelPlacement("settings") }),
    });

    /// <summary>
    /// The layout with everything that could be repaired, repaired.
    ///
    /// Applied on the way in and on the way out, so a file edited by hand and a layout built by
    /// the editor go through the same rules. Nothing here drops a panel whose type is unknown --
    /// that is the caller's business, and dropping it would destroy the arrangement of anybody who
    /// opened their file in an older build.
    /// </summary>
    public WorkspaceLayout Normalised()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var workspaces = new List<Workspace>();

        foreach (var w in Workspaces ?? Array.Empty<Workspace>())
        {
            if (w is null) continue;

            // A blank name leaves an unclickable gap in the switcher, and two workspaces with one
            // name leave the user unable to tell which they are editing.
            string name = string.IsNullOrWhiteSpace(w.Name) ? "Workspace" : w.Name.Trim();
            string unique = name;
            for (int n = 2; !seen.Add(unique); n++) unique = $"{name} {n}";

            var panels = (w.Panels ?? Array.Empty<PanelPlacement>())
                .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Type))
                .Select(p => p.Clamped())
                .ToArray();

            workspaces.Add(new Workspace(unique, panels));
        }

        // Never nothing. A switcher with no workspaces has no way back to a workspace.
        if (workspaces.Count == 0) return Defaults();

        return new WorkspaceLayout(Version <= 0 ? CurrentVersion : Version, workspaces);
    }

    /// <summary>
    /// Reads the layout, falling back to the defaults rather than failing.
    ///
    /// Same rule as AppSettings.Load, and for a stronger reason: this file decides whether there
    /// is a window at all. A layout that cannot be read is a bad day; a layout that cannot be read
    /// and throws is an application that will not start, on a machine whose fan control it holds.
    /// </summary>
    public static WorkspaceLayout Load(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            if (!File.Exists(path)) return Defaults();

            var read = JsonSerializer.Deserialize<WorkspaceLayout>(File.ReadAllText(path), Json);
            return read is null ? Defaults() : read.Normalised();
        }
        catch
        {
            return Defaults();
        }
    }

    /// <summary>
    /// Writes the layout, via a temporary file so an interrupted write cannot destroy the old one.
    ///
    /// The machine this runs on has recorded nine unexpected shutdowns in six days, and
    /// File.WriteAllText truncates before it writes. Replacing the file atomically means the
    /// worst case is the previous arrangement rather than no arrangement.
    /// </summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(Normalised(), Json));

        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }
}
