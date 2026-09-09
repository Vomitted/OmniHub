using System.IO;
using System.Windows;

namespace OmniHub.Tests;

/// <summary>
/// Builds the application's style layer for real, rather than reading it as text.
///
/// A resource dictionary is not checked by the compiler. A Setter naming a property the target
/// type does not have, a trigger on a property that does not exist, a template that cannot be
/// constructed, a BasedOn pointing at a key that is not there -- every one of those throws when
/// the dictionary is parsed, and the dictionaries are parsed at application startup. Until now
/// the only way to find out was to launch the application, and with views built lazily some of
/// it would not surface until a particular tab was opened.
/// </summary>
public class StyleDictionaryTests
{
    public static IEnumerable<object[]> Palettes() =>
        Directory.EnumerateFiles(Path.Combine(WpfTestHost.WpfDir, "Palettes"), "*.xaml")
                 .Select(p => new object[] { Path.GetFileName(p) });

    /// <summary>
    /// Every palette has to build the whole style layer, not only the one currently in use.
    ///
    /// ThemeManager swaps the palette at runtime, so a colour present in OledBlack and missing
    /// from Ember is a crash waiting for whoever changes theme. There are four, and they are
    /// edited by hand.
    /// </summary>
    [Theory]
    [MemberData(nameof(Palettes))]
    public void EveryPaletteBuildsTheWholeStyleLayer(string paletteFile)
    {
        WpfTestHost.Run(() =>
        {
            var merged = WpfTestHost.LoadResources(paletteFile);

            // Indexing forces the deferred content to be realised. A dictionary that parsed can
            // still fail here if a Setter or a trigger inside one of these is wrong.
            foreach (string key in new[]
            {
                "CardBorderStyle", "CardSheenStyle", "SubHeadingText", "BodyText", "MutedText",
                "TileLabel", "TileValue", "TileUnit", "TileFoot",
                "PillRadioStyle", "FlatButtonStyle", "PrimaryButtonStyle", "ToggleSwitchStyle",
                "OmniCheckBoxStyle", "OmniSliderStyle", "OmniComboBoxStyle", "OmniDataGridStyle",
                "StatusChipStyle", "NumericTextBoxStyle",
            })
            {
                Assert.True(merged[key] is Style, $"{key} is missing or is not a Style in {paletteFile}");
            }

            // The geometry tokens the card style now points at.
            Assert.IsType<CornerRadius>(merged["RadiusMd"]);
            Assert.IsType<Thickness>(merged["CardPadding"]);
        });
    }

    /// <summary>
    /// The card's padding default has to stay the value its callers stopped writing out.
    ///
    /// Thirty-nine borders had their Padding="16" deleted on the strength of this default. If it
    /// drifts back to something else, all thirty-nine change silently and at once -- which is
    /// exactly what had happened in the other direction, with the default sitting at 18 and every
    /// caller quietly overriding it.
    /// </summary>
    [Fact]
    public void TheCardPaddingDefaultIsWhatTheCallersRelyOn()
    {
        WpfTestHost.Run(() =>
        {
            var merged = WpfTestHost.LoadResources();
            var card = (Style)merged["CardBorderStyle"];

            var padding = card.Setters.OfType<Setter>()
                .FirstOrDefault(s => s.Property.Name == "Padding");

            Assert.True(padding is not null, "CardBorderStyle no longer sets Padding");
            Assert.Equal(new Thickness(16), padding!.Value);
        });
    }
}
