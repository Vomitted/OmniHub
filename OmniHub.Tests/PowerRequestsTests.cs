using OmniHub.Core.Diagnostics;

namespace OmniHub.Tests;

/// <summary>
/// Parsing what is holding this machine awake.
///
/// Every test here exists because powercfg's output has a shape that is easy to parse almost
/// correctly. An entry's reason is printed on its own unindented line, so it looks exactly like
/// an entry that happens not to start with a bracket; the names are sometimes NT device paths and
/// sometimes device instance IDs, which both contain backslashes and want opposite treatment; and
/// the headers are localised, so a parser that keys off the English words reports an empty result
/// on a machine that is full of requests.
/// </summary>
public class PowerRequestsTests
{
    /// <summary>
    /// A block in the shape powercfg actually prints: every category present, most of them
    /// empty, one driver holding the machine awake and one process holding the display on.
    /// </summary>
    private const string RealShape = """
        DISPLAY:
        [PROCESS] \Device\HarddiskVolume3\Program Files\Mozilla Firefox\firefox.exe
        Video Wake Lock

        SYSTEM:
        [DRIVER] Realtek High Definition Audio (HDAUDIO\FUNC_01&VEN_10EC&DEV_0257&SUBSYS_103C8C2F&REV_1001\4&1c3f0d2c&0&0001)
        An audio stream is currently in use.

        AWAYMODE:
        None.

        EXECUTION:
        None.

        PERFBOOST:
        None.

        ACTIVELOCKSCREEN:
        None.
        """;

    [Fact]
    public void ARealBlockParsesIntoTheRightCategories()
    {
        var requests = PowerRequests.Parse(RealShape);

        Assert.Equal(2, requests.Count);

        Assert.Equal(PowerRequestKind.Display, requests[0].Kind);
        Assert.Equal("PROCESS", requests[0].Origin);

        Assert.Equal(PowerRequestKind.System, requests[1].Kind);
        Assert.Equal("DRIVER", requests[1].Origin);
    }

    /// <summary>
    /// "None." is not a request.
    ///
    /// It is the commonest line in the whole output and it does not begin with a bracket, so a
    /// parser that treats every non-header line as content reports six phantom requests on a
    /// machine that is holding none.
    /// </summary>
    [Fact]
    public void NoneIsNotARequest() =>
        Assert.Empty(PowerRequests.Parse("SYSTEM:\nNone.\n\nDISPLAY:\nNone.\n"));

    /// <summary>The reason powercfg prints underneath an entry belongs to that entry.</summary>
    [Fact]
    public void TheReasonAttachesToTheEntryAboveIt()
    {
        var requests = PowerRequests.Parse(RealShape);

        Assert.Equal("Video Wake Lock", requests[0].Reason);
        Assert.Equal("An audio stream is currently in use.", requests[1].Reason);
    }

    /// <summary>
    /// A reason cannot leap a blank line into the next section.
    ///
    /// This is the case that makes the blank-line reset load-bearing rather than decorative, and
    /// it took injecting the defect to find it: within a well-formed English block the *header*
    /// reset already does the job, so an obvious-looking test of this passes either way.
    ///
    /// The case that genuinely needs it is a header this build fails to recognise as one --
    /// "Affichage:" below is title case, so the all-capitals test rejects it. Without the blank
    /// line ending the entry, that line would be appended to the previous section's last request
    /// and the screen would report an audio driver held for reason "Affichage:".
    /// </summary>
    [Fact]
    public void AReasonDoesNotCrossABlankLine()
    {
        var request = Assert.Single(PowerRequests.Parse(
            "SYSTEM:\n[DRIVER] Audio\nAn audio stream is currently in use.\n\nAffichage:\nNone.\n"));

        Assert.Equal("An audio stream is currently in use.", request.Reason);
    }

    /// <summary>A reason printed over two lines is joined rather than half-kept.</summary>
    [Fact]
    public void AMultiLineReasonIsJoined()
    {
        var requests = PowerRequests.Parse(
            "SYSTEM:\n[SERVICE] wuauserv\nWindows Update is servicing\nthe machine right now.\n");

        Assert.Equal("Windows Update is servicing the machine right now.", Assert.Single(requests).Reason);
    }

    /// <summary>
    /// A process path shortens to its executable; a driver name does not.
    ///
    /// Both contain backslashes, which is why "shorten anything with a backslash" is wrong: it
    /// would reduce the Realtek entry to "4&amp;1c3f0d2c&amp;0&amp;0001)" and lose the only part
    /// naming the device.
    /// </summary>
    [Fact]
    public void OnlyPathsAreShortened()
    {
        var requests = PowerRequests.Parse(RealShape);

        Assert.Equal("firefox.exe", requests[0].FriendlyName);
        Assert.StartsWith("Realtek High Definition Audio", requests[1].FriendlyName);
    }

    /// <summary>
    /// A localised header still yields its entries, classified as Unknown and named verbatim.
    ///
    /// On a non-English Windows every header is a word this build has never seen. Reporting
    /// nothing would be the worst outcome -- the machine is full of requests and the screen would
    /// say it was idle -- so the entries survive and only the classification is withheld.
    /// </summary>
    [Fact]
    public void ALocalisedHeaderKeepsItsEntries()
    {
        var request = Assert.Single(PowerRequests.Parse("SISTEMA:\n[DRIVER] Audio\nIn uso.\n"));

        Assert.Equal(PowerRequestKind.Unknown, request.Kind);
        Assert.Equal("SISTEMA", request.Category);
        Assert.Equal("Audio", request.Name);
    }

    /// <summary>The summary leads with what is holding the machine awake.</summary>
    [Fact]
    public void TheSummaryLeadsWithTheSystemRequests()
    {
        string summary = PowerRequests.Summarise(PowerRequests.Parse(RealShape));

        Assert.StartsWith("Holding this machine awake: Realtek", summary);
        Assert.Contains("Holding the display on: firefox.exe", summary);
    }

    /// <summary>An idle machine is said plainly, in both directions.</summary>
    [Fact]
    public void AnIdleMachineIsSaidPlainly()
    {
        string summary = PowerRequests.Summarise(Array.Empty<PowerRequest>());

        Assert.Contains("Nothing is holding this machine awake", summary);
        Assert.Contains("Nothing is holding the display on", summary);
    }

    /// <summary>
    /// Read-only, against this machine, in the same spirit as HardwareReadTests.
    ///
    /// The test suite does not run elevated, so the usual outcome here is the honest refusal --
    /// which is itself the thing being checked, because an unelevated read must report why rather
    /// than return an empty list that reads as "nothing is holding anything".
    ///
    /// When it does run elevated, it asserts shape rather than content: a healthy idle machine
    /// with no requests at all has to pass too. The assertions are aimed at the realistic failure,
    /// which is a reason line being mistaken for an entry -- that produces a request with an empty
    /// origin or an empty name, and both are caught here.
    /// </summary>
    [Fact]
    public void TheRealPowercfgOnThisMachineReads()
    {
        var requests = PowerRequests.Read(out string? error);

        if (error is { Length: > 0 })
        {
            Assert.Empty(requests);
            return;
        }

        Assert.All(requests, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r.Name), "a request with no name means a line was misread");
            Assert.False(string.IsNullOrWhiteSpace(r.Origin), "a request with no origin means a reason line became an entry");
            Assert.False(string.IsNullOrWhiteSpace(r.Category), "a request outside any category means the headers were not found");
        });
    }
}
