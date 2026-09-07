using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace OmniHub.Core.Update;

/// <summary>One published OmniHub release, as the update checker understands it.</summary>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string Title,
    string Notes,
    DateTimeOffset PublishedAt,
    bool IsPrerelease,
    string? DownloadUrl,
    long DownloadSize);

/// <summary>
/// Checks GitHub for a newer OmniHub build, and doubles as the changelog source.
///
/// There is no separate release feed to maintain: the releases published on the repository are
/// the changelog, so the app and the website read the same list and cannot drift apart. A
/// hand-maintained version file would be a second place to forget to update -- this project
/// already carries the scar of one, version.json, which still advertises a different
/// application.
///
/// The identification rule is the part that matters. This repository hosts releases for TWO
/// applications: OmniHub, and the older OmniControl Suite whose v9.0.0 tag is numerically the
/// highest and is what GitHub's own /releases/latest returns. Asking for "latest" would offer
/// every OmniHub user an unrelated program as an upgrade. A release counts as OmniHub only if
/// it carries an asset named OmniHub-*.zip -- a structural test rather than a cosmetic one,
/// which additionally guarantees there is something to download.
/// </summary>
public static class UpdateCheck
{
    private const string ReleasesUrl = "https://api.github.com/repos/Vomitted/OmniHub/releases";

    /// <summary>Where a user is sent to read the full release, and to download it by hand.</summary>
    public const string ReleasesPage = "https://github.com/Vomitted/OmniHub/releases";

    // GitHub rejects requests without a User-Agent. One client for the process lifetime: a new
    // HttpClient per check exhausts sockets under TIME_WAIT, which is the classic .NET mistake.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("OmniHub-UpdateCheck");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>
    /// The running build's version, normalised to three parts so it compares cleanly against a
    /// tag like v1.2.0. Falls back to 0.0.0 rather than throwing, so a missing attribute
    /// degrades into "everything looks newer" instead of breaking the Settings tab.
    /// </summary>
    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    private static Version ReadCurrentVersion()
    {
        var v = (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version;
        return v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
    }

    /// <summary>
    /// Turns a release-list response into the OmniHub releases it contains, newest first.
    ///
    /// Pure and separated from the network so the identification rule -- the thing that stops
    /// OmniControl Suite being offered as an OmniHub update -- is testable without GitHub.
    /// Malformed entries are skipped rather than throwing: one unparseable release should not
    /// hide every other one.
    /// </summary>
    public static IReadOnlyList<ReleaseInfo> ParseReleases(string json)
    {
        var found = new List<ReleaseInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return found;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                try
                {
                    string tag = Str(el, "tag_name");
                    if (ParseVersion(tag) is not { } version) continue;

                    // The discriminator. No OmniHub asset, not an OmniHub release.
                    string? url = null;
                    long size = 0;
                    if (el.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var a in assets.EnumerateArray())
                        {
                            string name = Str(a, "name");
                            if (!name.StartsWith("OmniHub", StringComparison.OrdinalIgnoreCase) ||
                                !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                            url = Str(a, "browser_download_url");
                            size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out long n) ? n : 0;
                            break;
                        }
                    }
                    if (url is null) continue;

                    var published = el.TryGetProperty("published_at", out var p) &&
                                    p.ValueKind == JsonValueKind.String &&
                                    DateTimeOffset.TryParse(p.GetString(), out var when)
                        ? when
                        : DateTimeOffset.MinValue;

                    string title = Str(el, "name");
                    found.Add(new ReleaseInfo(
                        version, tag,
                        title.Length > 0 ? title : tag,
                        Str(el, "body").Replace("\r\n", "\n").Trim(),
                        published,
                        el.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True,
                        url, size));
                }
                catch { /* skip this release, keep the rest */ }
            }
        }
        catch { /* not JSON, or not the shape we expect: no releases */ }

        found.Sort((a, b) => b.Version.CompareTo(a.Version));
        return found;
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>
    /// Reads a version out of a tag like "v1.2.3", "1.2", or "v2.0.0-beta.1".
    ///
    /// Anything after the numeric part is ignored for ordering but the caller still sees the raw
    /// tag. Returns null for a tag with no leading number, which is how a non-version tag gets
    /// dropped instead of comparing as 0.0.0 and looking like an ancient release.
    /// </summary>
    public static Version? ParseVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];

        int end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        s = s[..end].TrimEnd('.');
        if (s.Length == 0) return null;

        var parts = s.Split('.');
        if (!int.TryParse(parts[0], out int major)) return null;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out int m) ? m : 0;
        int patch = parts.Length > 2 && int.TryParse(parts[2], out int b) ? b : 0;
        return new Version(major, minor, patch);
    }

    /// <summary>
    /// The newest stable release ahead of <paramref name="current"/>, or null when up to date.
    ///
    /// Pre-releases are skipped: someone running a stable build has not asked to be moved onto a
    /// beta, and an updater that does that without being told is a way to lose trust quickly.
    /// </summary>
    public static ReleaseInfo? NewerThan(IEnumerable<ReleaseInfo> releases, Version current)
    {
        ReleaseInfo? best = null;
        foreach (var r in releases)
        {
            if (r.IsPrerelease || r.Version <= current) continue;
            if (best is null || r.Version > best.Version) best = r;
        }
        return best;
    }

    /// <summary>
    /// Fetches the release list. Returns an empty list on any failure -- no network, rate
    /// limited, GitHub down -- because an update check is a convenience and must never surface
    /// as an error in a tool whose job is keeping fans running.
    /// </summary>
    public static async Task<IReadOnlyList<ReleaseInfo>> FetchAsync(CancellationToken ct = default)
    {
        try
        {
            using var res = await Http.GetAsync(ReleasesUrl, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return Array.Empty<ReleaseInfo>();
            return ParseReleases(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch
        {
            return Array.Empty<ReleaseInfo>();
        }
    }

    /// <summary>
    /// Downloads a release's zip to a temporary folder and returns the path, reporting progress
    /// as a fraction between 0 and 1.
    ///
    /// It deliberately stops there rather than unpacking over the running installation. OmniHub
    /// runs elevated and holds the fan service; a process that replaces its own binary while it
    /// is the only thing keeping a fan curve applied is a bad trade for saving one manual
    /// extract. The caller reveals the file and lets the user exit and swap it.
    ///
    /// ponytail: no self-replace, no delta patching. Revisit only if updating by hand is
    /// actually the friction people report.
    /// </summary>
    public static async Task<string?> DownloadAsync(
        ReleaseInfo release, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (release.DownloadUrl is null) return null;

        string dir = Path.Combine(Path.GetTempPath(), "OmniHub-update");
        string file = Path.Combine(dir, $"OmniHub-{release.Tag}-win-x64.zip");

        try
        {
            Directory.CreateDirectory(dir);

            using var res = await Http.GetAsync(
                release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;

            long total = res.Content.Headers.ContentLength ?? release.DownloadSize;
            using var src = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            // Written to a .part and moved on success, so an interrupted download can never be
            // mistaken for a complete one by whoever opens the folder.
            string partial = file + ".part";
            using (var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    if (total > 0) progress?.Report(Math.Clamp((double)done / total, 0, 1));
                }
            }

            if (File.Exists(file)) File.Delete(file);
            File.Move(partial, file);
            return file;
        }
        catch
        {
            return null;
        }
    }
}
