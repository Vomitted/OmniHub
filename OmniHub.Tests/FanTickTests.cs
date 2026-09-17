using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;

namespace OmniHub.Tests;

/// <summary>
/// The sentence describing what the cooling loop just did.
///
/// It lives in Core rather than in the view for the reason the test project exists to enforce:
/// the tests cannot reference the application, so anything written in a view is written where
/// nothing can check it. A five-branch sentence about the fan loop is not a place for that.
/// </summary>
public class FanTickTests
{
    private static FanTick Tick(
        double measured = 70,
        double effective = 70,
        TemperatureSource? source = TemperatureSource.SmuDieTctl,
        bool ceiling = false,
        bool commanded = true,
        byte percent = 40,
        double lead = 0,
        string? error = null) =>
        new(measured, effective, source, ceiling, commanded, percent, lead, error);

    /// <summary>The ordinary case names the measurement, the sensor and the level.</summary>
    [Fact]
    public void AnOrdinaryTickNamesTheReadingTheSensorAndTheLevel()
    {
        string text = Tick(measured: 72.4, effective: 72.4, percent: 45).Describe();

        Assert.Contains("72.4 C", text);
        Assert.Contains("Tctl", text);
        Assert.Contains("Commanded 45%", text);
    }

    /// <summary>
    /// The predictive lead is reported as the difference it made.
    ///
    /// This is the whole point of the readout. The setting exists, the user can change it, and
    /// until now its only observable effect was that the fans behaved slightly differently.
    /// </summary>
    [Fact]
    public void PredictionIsReportedAsTheDifferenceItMade()
    {
        string text = Tick(measured: 70, effective: 74.5, lead: 10).Describe();

        Assert.Contains("10 s ahead", text);
        Assert.Contains("74.5 C", text);
        Assert.Contains("+4.5 C", text);
    }

    /// <summary>
    /// Prediction that changed nothing says so rather than going quiet.
    ///
    /// Silence here would be ambiguous: a user who turned the lead on and saw no mention of it
    /// could not tell whether it was working and idle or not working at all.
    /// </summary>
    [Fact]
    public void PredictionThatChangedNothingStillSaysItIsOn()
    {
        string text = Tick(measured: 70, effective: 70, lead: 10).Describe();

        Assert.Contains("Prediction is on", text);
        Assert.Contains("costing nothing", text);
    }

    /// <summary>With prediction off, the sentence does not mention it at all.</summary>
    [Fact]
    public void PredictionOffIsNotMentioned()
    {
        string text = Tick(lead: 0).Describe();

        Assert.DoesNotContain("ahead", text);
        Assert.DoesNotContain("Prediction", text);
    }

    /// <summary>
    /// A sensor on its ceiling is reported as a floor on the truth, not as a temperature.
    ///
    /// The ACPI zone is blind above roughly 85 C. Printing "85.0 C" there would state a
    /// measurement the hardware did not make, and the fan being at maximum would look like an
    /// overreaction rather than the correct response to an unknown.
    /// </summary>
    [Fact]
    public void ACeilingReadingIsReportedAsAtLeastThatHot()
    {
        string text = Tick(measured: 85, source: TemperatureSource.AcpiThermalZone, ceiling: true).Describe();

        Assert.Contains("at least that hot", text);
        Assert.Contains("unknown", text);
    }

    /// <summary>
    /// A failed tick leads with the failure and says what the fans are doing meanwhile.
    ///
    /// The error field's own doc comment says it exists so that "a loop that achieves nothing is
    /// diagnosable". Reporting a stale temperature and level as though the tick had succeeded
    /// would be the exact opposite.
    /// </summary>
    [Fact]
    public void AFailedTickLeadsWithTheFailure()
    {
        string text = Tick(error: "WMI call failed.").Describe();

        Assert.StartsWith("The last tick failed", text);
        Assert.DoesNotContain("Commanded", text);
    }

    /// <summary>
    /// Before the first tick, nothing is claimed.
    ///
    /// The commanded level defaults to zero and zero is a real fan level, so a readout that did
    /// not check this would report "Commanded 0%" on a machine whose fans had never been
    /// touched -- which looks exactly like the fans-stopped-while-hot fault this whole
    /// application exists to catch.
    /// </summary>
    [Fact]
    public void BeforeTheFirstTickNothingIsClaimed()
    {
        string text = Tick(commanded: false, percent: 0).Describe();

        Assert.Contains("not completed a tick", text);
        Assert.DoesNotContain("0%", text);
    }

    /// <summary>The lead is the plain difference, including when the forecast came down.</summary>
    [Fact]
    public void TheLeadIsTheDifferenceInEitherDirection()
    {
        Assert.Equal(4.5, Tick(measured: 70, effective: 74.5).LeadC, 3);
        Assert.Equal(-2.0, Tick(measured: 70, effective: 68).LeadC, 3);
    }

    /// <summary>An unnamed sensor is described as unnamed rather than guessed at.</summary>
    [Fact]
    public void AnAbsentSensorIsNotGuessed() =>
        Assert.Contains("unnamed sensor", Tick(source: null).Describe());
}
