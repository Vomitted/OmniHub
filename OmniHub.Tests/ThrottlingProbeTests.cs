using OmniHub.Core.Hardware;

namespace OmniHub.Tests;

/// <summary>
/// Settling whether the throttling reading is a measurement or the question reflected back.
///
/// The doubt is recorded in the source and is specific: GetCapability is asked with selector 4
/// and the answer is read from the same byte position the selector was written to, and
/// ThrottlingState.Default is 0x04. Every "Default" this application has shown may therefore be
/// the number it asked with.
///
/// The judgement is the testable half. Its one real trap is that a single sample cannot tell a
/// constant from a reflection -- which is exactly the position the original reading was in, and
/// the reason it went unresolved for so long.
/// </summary>
public class ThrottlingProbeTests
{
    private static (byte, byte)[] Samples(params (byte Selector, byte Answer)[] s) => s;

    /// <summary>An answer that follows the selector is the selector coming back.</summary>
    [Fact]
    public void AnAnswerThatTracksTheSelectorIsAnEcho() =>
        Assert.Equal(EchoVerdict.Echo,
            ThrottlingProbe.Judge(Samples(((byte)4, (byte)4), ((byte)9, (byte)9), ((byte)17, (byte)17))));

    /// <summary>An answer that stays put while the selector moves is reporting something.</summary>
    [Fact]
    public void AConstantAnswerAcrossSelectorsIsIndependent() =>
        Assert.Equal(EchoVerdict.Independent,
            ThrottlingProbe.Judge(Samples(((byte)4, (byte)1), ((byte)9, (byte)1), ((byte)17, (byte)1))));

    /// <summary>
    /// One sample decides nothing, and saying so is the entire point.
    ///
    /// With a single selector, a constant and a reflection are indistinguishable. Any verdict
    /// from one sample would be the same confident guess this exercise exists to replace.
    /// </summary>
    [Fact]
    public void OneSampleIsInconclusive() =>
        Assert.Equal(EchoVerdict.Inconclusive, ThrottlingProbe.Judge(Samples(((byte)4, (byte)4))));

    /// <summary>Repeating the same selector is still one sample, however many times it is asked.</summary>
    [Fact]
    public void TheSameSelectorTwiceIsStillOneSample() =>
        Assert.Equal(EchoVerdict.Inconclusive,
            ThrottlingProbe.Judge(Samples(((byte)4, (byte)4), ((byte)4, (byte)4))));

    /// <summary>
    /// An answer that moves but does not track the selector is not explained, and says so.
    ///
    /// Something else is happening. Picking whichever of the two stories fits better would be
    /// precisely the error this whole probe was written to correct.
    /// </summary>
    [Fact]
    public void AnAnswerThatMovesWithoutTrackingIsInconclusive() =>
        Assert.Equal(EchoVerdict.Inconclusive,
            ThrottlingProbe.Judge(Samples(((byte)4, (byte)1), ((byte)9, (byte)7))));

    /// <summary>Nothing answered at all is inconclusive rather than an echo.</summary>
    [Fact]
    public void NoSamplesIsInconclusive() =>
        Assert.Equal(EchoVerdict.Inconclusive, ThrottlingProbe.Judge(Array.Empty<(byte, byte)>()));

    /// <summary>
    /// The echo verdict says the reading is unavailable, and shows its working.
    ///
    /// A conclusion this consequential -- it retires a reading the tray raises notifications from
    /// -- has to carry the selectors and answers that produced it, so somebody can disagree with
    /// it on the evidence rather than on trust.
    /// </summary>
    [Fact]
    public void TheEchoVerdictShowsItsWorking()
    {
        var evidence = new ThrottlingEvidence(
            Samples(((byte)4, (byte)4), ((byte)9, (byte)9)),
            EchoVerdict.Echo,
            ThrottlingState.Default,
            ClocksCapped: false);

        string text = evidence.Describe();

        Assert.Contains("echo, not a measurement", text);
        Assert.Contains("4->4", text);
        Assert.Contains("9->9", text);
        Assert.Contains("unavailable", text);
    }

    /// <summary>
    /// A real reading is reported as real, and a disagreeing second instrument is named.
    ///
    /// The clock ceiling and the board's throttling flag describe different constraints, so this
    /// reports that they differ rather than that one of them is wrong.
    /// </summary>
    [Fact]
    public void AnIndependentReadingNamesADisagreeingSecondInstrument()
    {
        var evidence = new ThrottlingEvidence(
            Samples(((byte)4, (byte)0), ((byte)9, (byte)0)),
            EchoVerdict.Independent,
            ThrottlingState.Unknown,
            ClocksCapped: true);

        string text = evidence.Describe();

        Assert.Contains("looks real", text);
        Assert.Contains("different constraints", text);
    }

    /// <summary>
    /// The selectors are chosen so the experiment can actually fail.
    ///
    /// If an echoed selector coincided with a ThrottlingState value, the echo would come back
    /// looking like a plausible reading and the sweep would prove nothing. Four is the exception
    /// and has to be included -- it is the reading under suspicion.
    /// </summary>
    [Fact]
    public void TheSelectorsCannotBeMistakenForStates()
    {
        Assert.Contains((byte)4, ThrottlingProbe.Selectors);
        Assert.True(ThrottlingProbe.Selectors.Distinct().Count() >= 2,
                    "one selector can never distinguish a constant from a reflection");

        foreach (byte selector in ThrottlingProbe.Selectors.Where(s => s != 4))
            Assert.False(Enum.IsDefined(typeof(ThrottlingState), selector),
                         $"selector {selector} coincides with a ThrottlingState, so an echo would look like a reading");
    }
}
