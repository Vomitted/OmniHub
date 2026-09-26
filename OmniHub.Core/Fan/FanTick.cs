// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Hardware;

namespace OmniHub.Core.Fan;

/// <summary>
/// What the fan curve's last tick saw and decided.
///
/// Every field here was already being computed twice a second and thrown away. The service
/// records the measured temperature, the temperature the curve was actually evaluated against,
/// which sensor answered, whether that sensor was on its ceiling, and the reason the last tick
/// failed -- and until now nothing read any of them. The doc comment on the error field says in
/// so many words that it exists "so a loop that achieves nothing is diagnosable", and nothing
/// was diagnosing it.
///
/// The pair that matters most is measured against effective. Predictive lead is a setting the
/// user can change, and its entire effect is the difference between those two numbers; with
/// neither on screen, turning it up or down has been an act of faith.
/// </summary>
/// <param name="MeasuredC">What the sensor said.</param>
/// <param name="EffectiveC">What the curve was evaluated against: the filtered temperature, raised only by prediction.</param>
/// <param name="Source">Which sensor answered, or null before the first tick.</param>
/// <param name="SensorCeilingReached">The reading sat on the ACPI zone's ceiling: at least that hot, exact value unknown.</param>
/// <param name="HasCommanded">False until a level has genuinely been computed and sent.</param>
/// <param name="CommandedPercent">The level sent, meaningless unless <paramref name="HasCommanded"/>.</param>
/// <param name="PredictiveLeadSeconds">How far ahead the curve was allowed to look. 0 disables prediction.</param>
/// <param name="Error">Why the last tick failed, or null when it succeeded.</param>
/// <param name="FilteredC">
/// The control temperature after brief spikes were set aside, before any lead. Null from anything
/// that predates the spike filter, which is read as equal to <paramref name="MeasuredC"/>.
/// </param>
public sealed record FanTick(
    double MeasuredC,
    double EffectiveC,
    TemperatureSource? Source,
    bool SensorCeilingReached,
    bool HasCommanded,
    byte CommandedPercent,
    double PredictiveLeadSeconds,
    string? Error,
    double? FilteredC = null)
{
    /// <summary>
    /// How much hotter the curve treated the machine as being than the filtered temperature.
    ///
    /// This is the predictive lead's whole output, and only its output: the spike filter's effect
    /// is the difference between measured and filtered, and it is described separately rather than
    /// being credited to a setting the user may have switched off. Positive means the forecast
    /// pushed the fan harder than the present temperature warranted; zero means prediction is off,
    /// or the die is steady and the forecast agrees -- which is itself worth seeing, because it
    /// says the lead is costing nothing right now.
    /// </summary>
    public double LeadC => EffectiveC - (FilteredC ?? MeasuredC);

    /// <summary>
    /// The tick in a sentence.
    ///
    /// Written here rather than in the view because the view cannot be tested: the test project
    /// is forbidden from referencing the application, since the application's build output is
    /// the copy the user runs. A five-branch sentence about what the cooling loop just did is
    /// exactly the kind of thing that should not live where nothing can check it.
    /// </summary>
    public string Describe()
    {
        if (Error is { Length: > 0 })
            return $"The last tick failed: {Error} The fans are at whatever was last commanded.";

        if (!HasCommanded)
            return "The curve has not completed a tick yet, so nothing has been commanded.";

        string sensor = Source switch
        {
            TemperatureSource.SmuDieTctl => "the processor's own Tctl sensor",
            TemperatureSource.AcpiThermalZone => "an ACPI thermal zone",
            _ => "an unnamed sensor",
        };

        string text = $"Measured {MeasuredC:0.#} C from {sensor}";

        if (SensorCeilingReached)
            text += ", which is sitting on its ceiling -- the die is at least that hot and the "
                  + "exact value is unknown, so the fan has been forced to maximum";

        // Only when the filter actually moved the number, in whichever direction it moved it: a
        // reading that had not persisted was not acted on, or a fall that had not persisted yet
        // kept the fan where it was. Either way the screen says so, instead of showing a
        // temperature and a level that the curve, read by hand, would not connect.
        if (FilteredC is { } filtered && Math.Abs(filtered - MeasuredC) >= 0.05)
            text += filtered < MeasuredC
                ? $". That reading had not persisted, so the curve acted on {filtered:0.#} C, the median of the last five"
                : $". The fall had not persisted yet, so the curve still acted on {filtered:0.#} C, the median of the last five";

        // Only mentioned when prediction actually moved the number. Saying "lead 0.0 C" every
        // tick on a machine with prediction switched off would be noise dressed as information.
        if (PredictiveLeadSeconds > 0 && Math.Abs(LeadC) >= 0.05)
            text += $". Looking {PredictiveLeadSeconds:0.#} s ahead, the curve was evaluated "
                  + $"against {EffectiveC:0.#} C instead -- {LeadC:+0.#;-0.#} C of predictive lead";
        else if (PredictiveLeadSeconds > 0)
            text += $". Prediction is on at {PredictiveLeadSeconds:0.#} s but the die is steady, "
                  + "so the forecast matches the measurement and is costing nothing";

        return text + $". Commanded {CommandedPercent}%.";
    }

    /// <summary>
    /// The tick in a few words, for one row of a table: only what was not simply the curve.
    ///
    /// Empty for an ordinary tick, deliberately. A column that says "on the curve" thirty times is
    /// a column nobody reads, and the rows that matter -- a spike set aside, a forecast, a sensor on
    /// its ceiling, a failure -- have to stand out from the ones that do not.
    /// </summary>
    public string Note()
    {
        if (Error is { Length: > 0 }) return $"failed: {Error}";
        if (!HasCommanded) return "nothing commanded yet";
        if (SensorCeilingReached) return "sensor on its ceiling, forced to full";

        var notes = new List<string>(2);

        if (FilteredC is { } filtered && Math.Abs(filtered - MeasuredC) >= 0.05)
            notes.Add(filtered < MeasuredC ? "spike set aside" : "fall not yet trusted");

        if (PredictiveLeadSeconds > 0 && Math.Abs(LeadC) >= 0.05)
            notes.Add($"forecast {LeadC.ToString("+0.#;-0.#", System.Globalization.CultureInfo.InvariantCulture)} C");

        return string.Join(", ", notes);
    }
}
