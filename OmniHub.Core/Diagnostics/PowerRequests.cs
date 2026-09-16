using System.Diagnostics;
using OmniHub.Core.Hardware;

namespace OmniHub.Core.Diagnostics;

/// <summary>Which kind of power request an entry holds.</summary>
public enum PowerRequestKind
{
    /// <summary>
    /// A category header this build does not recognise.
    ///
    /// powercfg's output is localised, so on a non-English Windows every header is a word this
    /// switch has never seen. The entries underneath are still real and still listed -- what is
    /// lost is the ability to say which of them is the one holding sleep off, so the raw header
    /// is carried on the record and shown verbatim rather than guessed at.
    /// </summary>
    Unknown,

    /// <summary>Holding the display on.</summary>
    Display,

    /// <summary>Holding the machine awake. The one that matters most here.</summary>
    System,

    /// <summary>Holding away mode -- the screen may sleep, the machine may not.</summary>
    AwayMode,

    /// <summary>Holding execution, which stops the process itself being suspended.</summary>
    Execution,

    /// <summary>Holding a performance-boost request.</summary>
    PerfBoost,

    /// <summary>Holding the active lock screen.</summary>
    ActiveLockScreen,
}

/// <summary>
/// One power request, as Windows reports it.
/// </summary>
/// <param name="Kind">The classified category, or <see cref="PowerRequestKind.Unknown"/>.</param>
/// <param name="Category">The header powercfg printed, kept verbatim.</param>
/// <param name="Origin">DRIVER, PROCESS or SERVICE -- who is holding it.</param>
/// <param name="Name">The full name powercfg gave, which for a process is an NT device path.</param>
/// <param name="Reason">The explanation powercfg printed underneath, when it printed one.</param>
public sealed record PowerRequest(
    PowerRequestKind Kind,
    string Category,
    string Origin,
    string Name,
    string? Reason)
{
    /// <summary>
    /// The name with the path taken off, for a readout that has to fit on one line.
    ///
    /// A process request is reported as a full NT path -- 90-odd characters of
    /// <c>\Device\HarddiskVolume3\Program Files\...</c> before the part anybody wants. A driver
    /// request is not a path at all, though it often has a device instance ID with backslashes
    /// inside it, so shortening on "contains a backslash" would butcher those. Only something
    /// that begins as a device path or carries a drive letter is treated as one.
    /// </summary>
    public string FriendlyName
    {
        get
        {
            bool isPath = Name.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)
                          || Name.Contains(@":\", StringComparison.Ordinal);

            if (!isPath) return Name;

            string tail = Name[(Name.LastIndexOf('\\') + 1)..];
            return tail.Length > 0 ? tail : Name;
        }
    }
}

/// <summary>
/// What is holding this machine awake, right now.
///
/// This reading has already earned its place once. Tracking down why the laptop would not stay
/// asleep took a dozen commands by hand and ended at a USB headset holding a System Required
/// request -- one line of output that no screen in this application could show. It is read-only,
/// it needs the elevation the application already has, and it is the single cheapest answer to
/// "why did this thing not sleep" that Windows offers.
///
/// The parser is separated from the process spawn on purpose. Everything interesting about this
/// is the shape of powercfg's output, and a pure function over a string is the only half that can
/// be tested at all: the other half needs an elevated process and a machine in a particular state.
/// </summary>
public static class PowerRequests
{
    /// <summary>
    /// How long to wait for powercfg before giving up.
    ///
    /// It normally answers in well under a second. The bound exists because this can be called
    /// from a view's refresh and a wedged child process must not take the UI with it -- the same
    /// reasoning as the nvidia-smi query, which has the same guard for the same reason.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Runs <c>powercfg /requests</c> and parses it.
    ///
    /// Returns an empty list with <paramref name="error"/> set when it could not be read, and an
    /// empty list with <paramref name="error"/> null when it ran and found nothing -- those are
    /// different facts and the caller has to be able to tell them apart. "Nothing is holding this
    /// machine awake" and "nobody asked" would otherwise render identically.
    /// </summary>
    public static IReadOnlyList<PowerRequest> Read(out string? error)
    {
        if (!PawnIoAccess.IsProcessElevated())
        {
            error = "powercfg /requests needs Administrator rights, which this process does not hold.";
            return Array.Empty<PowerRequest>();
        }

        try
        {
            // The full path rather than the bare name. This runs elevated, and resolving an
            // executable through PATH in an elevated process is a larger surface than it needs
            // to be for something that has lived in System32 since Windows XP.
            string exe = Path.Combine(Environment.SystemDirectory, "powercfg.exe");

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/requests");

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                error = "Windows would not start powercfg.";
                return Array.Empty<PowerRequest>();
            }

            // Both pipes drained, stderr concurrently. A child that fills an undrained pipe
            // blocks on the write while this thread blocks on the read, and the timeout below
            // never gets to apply because the deadlock happens first.
            var stderr = proc.StandardError.ReadToEndAsync();
            string output = proc.StandardOutput.ReadToEnd();

            if (!proc.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { proc.Kill(true); } catch { }
                error = $"powercfg did not answer within {Timeout.TotalSeconds:0} seconds.";
                return Array.Empty<PowerRequest>();
            }

            if (proc.ExitCode != 0)
            {
                string message = stderr.Result.Trim();
                if (message.Length == 0) message = output.Trim();
                error = message.Length > 0 ? message : $"powercfg exited with code {proc.ExitCode}.";
                return Array.Empty<PowerRequest>();
            }

            error = null;
            return Parse(output);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return Array.Empty<PowerRequest>();
        }
    }

    /// <summary>
    /// Parses powercfg's output.
    ///
    /// The format is a sequence of all-capitals category headers, each followed either by the
    /// word "None." or by entries of the form <c>[ORIGIN] name</c>, where an entry may be
    /// followed by an unindented sentence giving the reason. That last part is what makes a
    /// line-oriented parse necessary rather than a regular expression: a reason line looks like
    /// nothing in particular, and is recognised only by what came before it.
    /// </summary>
    public static IReadOnlyList<PowerRequest> Parse(string text)
    {
        var found = new List<PowerRequest>();

        string category = "";
        var kind = PowerRequestKind.Unknown;
        PowerRequest? current = null;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            // A blank line ends an entry, so a stray sentence in the next section cannot be
            // attached to the previous section's last request.
            if (line.Length == 0) { current = null; continue; }

            if (IsHeader(line))
            {
                category = line[..^1];
                kind = KindFor(category);
                current = null;
                continue;
            }

            if (line.StartsWith('['))
            {
                int close = line.IndexOf(']');
                if (close < 0) continue;

                current = new PowerRequest(
                    kind,
                    category,
                    line[1..close].Trim(),
                    line[(close + 1)..].Trim(),
                    Reason: null);

                found.Add(current);
                continue;
            }

            // Anything else belongs to the entry above it. With no entry above it -- "None.",
            // or a banner line before the first header -- there is nothing to attach it to and
            // it is dropped rather than invented into a request.
            if (current is null) continue;

            current = current with
            {
                Reason = current.Reason is null ? line : current.Reason + " " + line,
            };

            found[^1] = current;
        }

        return found;
    }

    /// <summary>
    /// What the requests amount to, in a sentence.
    ///
    /// Leads with the system requests because those are the ones that answer the question people
    /// arrive with. It reports what is holding what; it does not say that any of them is wrong to.
    /// </summary>
    public static string Summarise(IReadOnlyList<PowerRequest> requests)
    {
        var system = requests.Where(r => r.Kind == PowerRequestKind.System).ToList();
        var display = requests.Where(r => r.Kind == PowerRequestKind.Display).ToList();
        int others = requests.Count - system.Count - display.Count;

        var parts = new List<string>
        {
            system.Count == 0
                ? "Nothing is holding this machine awake."
                : $"Holding this machine awake: {Names(system)}.",

            display.Count == 0
                ? "Nothing is holding the display on."
                : $"Holding the display on: {Names(display)}.",
        };

        if (others > 0)
            parts.Add($"{others} further request(s) of other kinds.");

        return string.Join(" ", parts);
    }

    /// <summary>At most three names, because a sentence listing nine is not a sentence.</summary>
    private static string Names(IReadOnlyList<PowerRequest> requests)
    {
        var names = requests.Take(3).Select(r => r.FriendlyName);
        string text = string.Join(", ", names);

        return requests.Count > 3 ? $"{text} and {requests.Count - 3} more" : text;
    }

    /// <summary>
    /// Whether a line is a category header.
    ///
    /// Ends with a colon and is otherwise all capitals. The capitals test is what keeps a reason
    /// sentence from being mistaken for a header, and it survives localisation because an
    /// unrecognised header still classifies as a header -- just not as a known kind.
    /// </summary>
    private static bool IsHeader(string line)
    {
        if (line.Length < 2 || !line.EndsWith(':')) return false;

        string head = line[..^1];
        return head.Length <= 40 && head == head.ToUpperInvariant();
    }

    private static PowerRequestKind KindFor(string category) => category.ToUpperInvariant() switch
    {
        "DISPLAY" => PowerRequestKind.Display,
        "SYSTEM" => PowerRequestKind.System,
        "AWAYMODE" => PowerRequestKind.AwayMode,
        "EXECUTION" => PowerRequestKind.Execution,
        "PERFBOOST" => PowerRequestKind.PerfBoost,
        "ACTIVELOCKSCREEN" => PowerRequestKind.ActiveLockScreen,
        _ => PowerRequestKind.Unknown,
    };
}
