namespace OmniHub.Core.Hardware;

/// <summary>What the selector sweep showed.</summary>
public enum EchoVerdict
{
    /// <summary>Not enough distinct selectors answered to conclude anything.</summary>
    Inconclusive,

    /// <summary>The answer tracked the selector. The reply is the question, handed back.</summary>
    Echo,

    /// <summary>The answer stayed put while the selector changed. Something is being reported.</summary>
    Independent,
}

/// <summary>
/// The evidence, kept alongside the conclusion so the conclusion can be checked.
/// </summary>
/// <param name="Samples">Each selector tried and the byte that came back.</param>
/// <param name="Verdict">What those samples show.</param>
/// <param name="Reported">The throttling state the ordinary reading gives.</param>
/// <param name="ClocksCapped">Whether Windows is holding a processor ceiling below nominal, or null.</param>
public sealed record ThrottlingEvidence(
    IReadOnlyList<(byte Selector, byte Answer)> Samples,
    EchoVerdict Verdict,
    ThrottlingState Reported,
    bool? ClocksCapped)
{
    /// <summary>
    /// What the evidence supports, in the terms somebody deciding whether to trust the reading
    /// would want.
    /// </summary>
    public string Describe() => Verdict switch
    {
        EchoVerdict.Echo =>
            "The throttling reading is an echo, not a measurement. Asked with different selector "
            + $"bytes ({Tried()}), the board returned each one back unchanged. The value normally "
            + "read here is the selector 4 that was sent, and ThrottlingState.Default happens to "
            + "equal 4 -- so this has been reporting the question rather than the answer. Treated "
            + "as unavailable.",

        EchoVerdict.Independent =>
            $"The throttling reading looks real. Asked with different selector bytes ({Tried()}), "
            + "the board returned the same answer each time rather than echoing the selector, so "
            + $"it is reporting something. It currently reads {Reported}."
            + ClockNote(),

        _ =>
            "Not enough of the board's replies came back to say whether the throttling reading is "
            + "a measurement or an echo of the byte sent to ask for it. The reading is shown, and "
            + "should be treated with the doubt its source comment already records.",
    };

    private string Tried() => string.Join(", ", Samples.Select(s => $"{s.Selector}->{s.Answer}"));

    /// <summary>
    /// The second instrument, where there is one.
    ///
    /// The processor's own ceiling is a different measurement of a related thing: this flag is
    /// the board's view of thermal throttling, and the ceiling is what Windows is currently
    /// allowing. They can legitimately differ, so the note says they differ rather than that one
    /// is wrong.
    /// </summary>
    private string ClockNote() => ClocksCapped switch
    {
        true when Reported != ThrottlingState.On =>
            " Windows is separately holding the processor below its nominal clock, which this "
            + "flag does not reflect -- the two describe different constraints.",

        false when Reported == ThrottlingState.On =>
            " No processor ceiling is in force at the same time, so whatever is being throttled "
            + "is not showing up as a clock limit.",

        _ => "",
    };
}

/// <summary>
/// Settles whether the throttling reading is a measurement.
///
/// The source has carried the doubt for a long time and states it plainly: GetCapability was
/// swept across its second input byte and the response simply echoed that byte back at data[1],
/// which is exactly the position the throttling read uses. The selector sent is 4, and
/// ThrottlingState.Default is 0x04 -- so every "Default" this application has ever shown may be
/// the number it asked with, reflected.
///
/// That is a hypothesis with an experiment attached, and the experiment is three read-only BIOS
/// calls: ask with other selectors and see whether the answer follows. It costs nothing to run
/// and it either retires the doubt or retires the reading.
///
/// Read-only throughout. GetCapability is a query whose second byte chooses which capability is
/// being asked about; sending a different one asks a different question rather than changing
/// anything, and the existing code already sends both 0 and 4.
/// </summary>
public static class ThrottlingProbe
{
    /// <summary>
    /// Selectors to try.
    ///
    /// Four is the one the throttling read uses and has to be included, or the experiment is not
    /// about the reading in question. The others are chosen to be distinct from each other and
    /// from every ThrottlingState value, so an echo cannot be mistaken for a plausible state.
    /// </summary>
    public static readonly byte[] Selectors = { 4, 9, 17 };

    /// <summary>
    /// Judges the sweep.
    ///
    /// Pure, and separate from the calls, because this is the half that decides whether a feature
    /// gets believed and the half that can be checked without a machine.
    /// </summary>
    internal static EchoVerdict Judge(IReadOnlyList<(byte Selector, byte Answer)> samples)
    {
        // One answer says nothing: a single selector cannot distinguish a constant from a
        // reflection of itself. That is precisely the position the original reading was in.
        if (samples.Select(s => s.Selector).Distinct().Count() < 2) return EchoVerdict.Inconclusive;

        if (samples.All(s => s.Answer == s.Selector)) return EchoVerdict.Echo;

        if (samples.Select(s => s.Answer).Distinct().Count() == 1) return EchoVerdict.Independent;

        // The answer moved but did not track the selector. Something else is going on, and
        // guessing what would be exactly the error this whole exercise exists to correct.
        return EchoVerdict.Inconclusive;
    }

    /// <summary>Runs the sweep against the board and gathers the surrounding evidence.</summary>
    public static ThrottlingEvidence Run(SystemController system)
    {
        var samples = new List<(byte Selector, byte Answer)>();

        foreach (byte selector in Selectors)
            if (system.ReadCapabilityByte(selector) is { } answer)
                samples.Add((selector, answer));

        ThrottlingState reported;
        try { reported = system.GetThrottling(); }
        catch { reported = ThrottlingState.Unknown; }

        var clocks = ProcessorClocks.Read();

        return new ThrottlingEvidence(
            samples,
            Judge(samples),
            reported,
            clocks.Count > 0 ? ProcessorClocks.IsCapped(clocks) : null);
    }
}
