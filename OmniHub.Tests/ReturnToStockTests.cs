using OmniHub.Core.Optimize;

namespace OmniHub.Tests;

/// <summary>
/// The control that makes this application passive, and the reporting that makes it useful.
///
/// The value here is entirely in what it admits. Two of the changes OmniHub makes -- SMU power
/// and thermal limits, and a GPU ceiling the firmware is already holding -- cannot be undone
/// without a restart: the mailbox has no release command and the firmware's own defaults were
/// never recorded, so a reset that claimed to restore them would be inventing the values it put
/// back. A summary that said "back to stock" while two limits were still whatever OmniHub last
/// wrote would make the next round of diagnosis worse, because it would rule out the wrong
/// thing.
///
/// These tests cover the reporting rather than the hardware, which is the half that decides
/// whether somebody reaches the right conclusion.
/// </summary>
public class ReturnToStockTests
{
    private static ReturnToStock.Step Step(ReturnToStock.StockState state, string name = "Thing") =>
        new(name, state, "detail");

    [Fact]
    public void EverythingRestoredOrUntouchedIsFullyStock()
    {
        var steps = new[]
        {
            Step(ReturnToStock.StockState.Restored, "Fan control"),
            Step(ReturnToStock.StockState.NotChanged, "GPU power ceiling"),
        };

        Assert.True(ReturnToStock.IsFullyStock(steps));
        Assert.Contains("back to stock", ReturnToStock.Summarise(steps));
    }

    /// <summary>
    /// "Never changed" and "could not be changed back" are opposite answers to the question
    /// somebody is actually asking, and a boolean cannot tell them apart. This is why the state
    /// is an enum with four members rather than a success flag.
    /// </summary>
    [Fact]
    public void SomethingThatCannotBeUndoneIsNotFullyStock()
    {
        var steps = new[]
        {
            Step(ReturnToStock.StockState.Restored, "Fan control"),
            Step(ReturnToStock.StockState.NeedsReboot, "Processor limits"),
        };

        Assert.False(ReturnToStock.IsFullyStock(steps));
    }

    [Fact]
    public void AFailedStepIsNotFullyStock() =>
        Assert.False(ReturnToStock.IsFullyStock(new[] { Step(ReturnToStock.StockState.Failed) }));

    /// <summary>
    /// The summary leads with what did NOT come back.
    ///
    /// Somebody reads this line to decide whether OmniHub can be ruled out of a fault. A sentence
    /// that opened with four successes and mentioned the outstanding limit at the end would
    /// answer that question wrongly for a reader who stopped at the first clause.
    /// </summary>
    [Fact]
    public void TheSummaryNamesWhatIsStillInForce()
    {
        var steps = new[]
        {
            Step(ReturnToStock.StockState.Restored, "Fan control"),
            Step(ReturnToStock.StockState.Restored, "Timer resolution"),
            Step(ReturnToStock.StockState.Restored, "Desktop composition priority"),
            Step(ReturnToStock.StockState.Restored, "Windows power plan"),
            Step(ReturnToStock.StockState.NeedsReboot, "Processor limits"),
            Step(ReturnToStock.StockState.NeedsReboot, "GPU power ceiling"),
        };

        string summary = ReturnToStock.Summarise(steps);

        Assert.Contains("processor limits", summary);
        Assert.Contains("gpu power ceiling", summary);
        Assert.DoesNotContain("The machine is back to stock", summary);

        // The outstanding items appear before the reassurance, not after it.
        Assert.True(summary.IndexOf("processor limits", StringComparison.Ordinal)
                    < summary.IndexOf("handed back", StringComparison.Ordinal));
    }

    /// <summary>A failed step is reported as outstanding too, not quietly counted as done.</summary>
    [Fact]
    public void AFailedStepIsNamedInTheSummary() =>
        Assert.Contains("fan control", ReturnToStock.Summarise(new[]
        {
            Step(ReturnToStock.StockState.Failed, "Fan control"),
        }));

    [Fact]
    public void AnEmptyRunIsVacuouslyStock() =>
        Assert.True(ReturnToStock.IsFullyStock(Array.Empty<ReturnToStock.Step>()));
}
