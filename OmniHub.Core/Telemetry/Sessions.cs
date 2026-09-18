// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmniHub.Core.Telemetry;

/// <summary>
/// A named stretch of time, so a measurement can be compared against another one on purpose.
///
/// A name and two timestamps, not a new recording. Everything worth comparing already streams to
/// CSV continuously and there are two weeks of it on disk, so a session format would be a second
/// copy of the same bytes, a schema to version, and -- the part that decides it -- something that
/// only works forward. A name and a range works retroactively on everything already logged.
/// </summary>
/// <param name="EndedUtc">Null while it is still running.</param>
public sealed record Session(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("startedUtc")] DateTime StartedUtc,
    [property: JsonPropertyName("endedUtc")] DateTime? EndedUtc = null,

    /// <summary>
    /// What the machine was plugged into, and which profile was on.
    ///
    /// Recorded because a comparison across them is not a comparison. Two runs on different power
    /// sources differ by the rail before they differ by anything the user changed, and the
    /// comparison has to be able to say so rather than reporting the difference as a result.
    /// </summary>
    [property: JsonPropertyName("powerSource")] string PowerSource = "",
    [property: JsonPropertyName("profile")] string Profile = "",
    [property: JsonPropertyName("appVersion")] string AppVersion = "")
{
    /// <summary>Whether this session has been stopped.</summary>
    [JsonIgnore]
    public bool IsClosed => EndedUtc is not null;

    /// <summary>How long it ran, or how long it has been running.</summary>
    public TimeSpan Duration(DateTime? nowUtc = null) =>
        (EndedUtc ?? nowUtc ?? DateTime.UtcNow) - StartedUtc;

    /// <summary>Whether a moment falls inside this session.</summary>
    public bool Covers(DateTime atUtc) =>
        atUtc >= StartedUtc && atUtc <= (EndedUtc ?? DateTime.UtcNow);
}

/// <summary>
/// The sessions somebody has recorded, and the file they live in.
/// </summary>
public sealed record SessionLog(
    [property: JsonPropertyName("sessions")] IReadOnlyList<Session> Sessions)
{
    /// <summary>
    /// How many are kept.
    ///
    /// Sessions are tiny and the reason to have one is to come back to it, so this is generous.
    /// It exists at all because the file is appended to and never otherwise bounded.
    /// </summary>
    public const int Capacity = 200;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniHub", "sessions.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static SessionLog Empty { get; } = new(Array.Empty<Session>());

    /// <summary>
    /// The session still running, if there is one.
    ///
    /// At most one: starting a second while one is open would leave two ranges overlapping and no
    /// way to say which a reading belonged to.
    /// </summary>
    public Session? Open => Sessions.FirstOrDefault(s => !s.IsClosed);

    /// <summary>
    /// Starts one, closing whatever was open.
    ///
    /// Closing rather than refusing: the common way a session is left open is a hang or a hard
    /// exit, and telling somebody they cannot start a new measurement because the machine crashed
    /// during the last one would be punishing them for the thing this application is trying to
    /// diagnose.
    /// </summary>
    public SessionLog Start(Session session)
    {
        var sessions = Sessions.Select(s => s.IsClosed ? s : s with { EndedUtc = session.StartedUtc }).ToList();
        sessions.Add(session);

        return new SessionLog(Trimmed(sessions));
    }

    /// <summary>Closes the open session, if there is one. Returns this unchanged if there is not.</summary>
    public SessionLog Stop(DateTime endedUtc)
    {
        if (Open is null) return this;

        return new SessionLog(
            Sessions.Select(s => s.IsClosed ? s : s with { EndedUtc = endedUtc }).ToList());
    }

    /// <summary>
    /// The earliest moment any session still refers to, or null when none do.
    ///
    /// This is what the log retention needs. A session pointing into a fortnight-old range would
    /// silently resolve to nothing once the trace behind it was swept -- an A/B comparison that
    /// has forgotten A, which is worse than one that refuses to run.
    /// </summary>
    public DateTime? EarliestReferenced =>
        Sessions.Count == 0 ? null : Sessions.Min(s => s.StartedUtc);

    private static List<Session> Trimmed(List<Session> sessions) =>
        sessions.Count <= Capacity
            ? sessions
            : sessions.OrderByDescending(s => s.StartedUtc).Take(Capacity).OrderBy(s => s.StartedUtc).ToList();

    /// <summary>Reads the log, falling back to empty rather than failing.</summary>
    public static SessionLog Load(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            if (!File.Exists(path)) return Empty;

            var read = JsonSerializer.Deserialize<SessionLog>(File.ReadAllText(path), Json);

            // A null or absent array deserialises to a record with a null list, which every
            // caller would then have to guard. Normalise it here instead.
            return read?.Sessions is null ? Empty : read;
        }
        catch
        {
            return Empty;
        }
    }

    /// <summary>Writes the log through a temporary file, so an interrupted write keeps the old one.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, Json));

        if (File.Exists(path)) File.Replace(temporary, path, null);
        else File.Move(temporary, path);
    }
}
