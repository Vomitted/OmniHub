// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

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
                "CardBorderStyle", "SubHeadingText", "BodyText", "MutedText",
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
    /// Every text style says what colour it is.
    ///
    /// A TextBlock with no Foreground of its own inherits one, and inside these views nothing above
    /// it sets one, so it inherits WPF's default: black. TileLabel had no Foreground and trusted each
    /// caller to supply one; eleven did not. The Fans page's TEMPERATURE label rendered at
    /// RGB(4,4,3) on a card of RGB(27,22,19), and four tiles and five column headers on the Network
    /// page were the same -- invisible in every palette. The contrast tests could not see any of it,
    /// because they check the colours the palettes define, and this text had none.
    /// </summary>
    [Fact]
    public void EveryTextStyleSaysWhatColourItIs()
    {
        WpfTestHost.Run(() =>
        {
            var merged = WpfTestHost.LoadResources();

            var textStyles = merged.MergedDictionaries
                .SelectMany(d => d.Keys.Cast<object>().Select(k => (Key: k, Value: d[k])))
                .Where(e => e.Value is Style s && typeof(System.Windows.Controls.TextBlock).IsAssignableFrom(s.TargetType))
                .ToList();

            Assert.True(textStyles.Count > 10, $"only {textStyles.Count} text styles found; this is not the real style layer");

            var colourless = textStyles.Where(e => !SetsForeground((Style)e.Value)).Select(e => e.Key.ToString()).ToList();
            Assert.True(colourless.Count == 0,
                "these text styles leave the colour to each caller, and a caller that forgets draws black: "
                + string.Join(", ", colourless));
        });
    }

    /// <summary>
    /// An input that names no style still belongs to the application, and an editable combo can
    /// still be typed in.
    ///
    /// The styles existed and were keyed, so they applied only where a view remembered to ask. Eleven
    /// inputs did not, and stock WPF drew them white on a near-black page. Making the styles the
    /// defaults exposed a second fault underneath: the combo template had no editable part, so the
    /// one editable combo in the application -- the game picker -- could only have been styled by
    /// losing the ability to type into it.
    /// </summary>
    [Fact]
    public void EveryInputHasTheApplicationsLookByDefault()
    {
        WpfTestHost.Run(() =>
        {
            var merged = WpfTestHost.LoadResources();

            foreach (var type in new[] { typeof(System.Windows.Controls.TextBox), typeof(System.Windows.Controls.Slider),
                                         typeof(System.Windows.Controls.ComboBox) })
            {
                Assert.True(merged[type] is Style s && s.TargetType == type,
                            $"no default style for {type.Name}; one that names no style is drawn by stock WPF");
            }

            var combo = new System.Windows.Controls.ComboBox { IsEditable = true };
            combo.Style = (Style)merged[typeof(System.Windows.Controls.ComboBox)];
            Assert.True(combo.ApplyTemplate(), "the combo template did not apply");

            var part = combo.Template.FindName("PART_EditableTextBox", combo) as System.Windows.Controls.TextBox;
            Assert.True(part is not null, "the combo template has no PART_EditableTextBox, so IsEditable is ignored");
            Assert.Equal(Visibility.Visible, part!.Visibility);
        });
    }

    private static bool SetsForeground(Style? style) =>
        style is not null
        && (style.Setters.OfType<Setter>().Any(s => s.Property == System.Windows.Controls.TextBlock.ForegroundProperty)
            || SetsForeground(style.BasedOn));

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
        // Card padding stopped being one shared value and became part of the palette, so
        // that a theme can be tight or roomy rather than only a different colour. This
        // used to assert the literal Thickness(16); asserting a literal now would be
        // asserting that themes cannot change density, which is the opposite of what the
        // setting is for.
        //
        // What still has to hold is the mechanism: the setter must be a DYNAMIC reference,
        // because a StaticResource is resolved once when the style is sealed and would bake
        // one theme's padding into every other theme. That is not a hypothetical -- every
        // brush in Theme.xaml is DynamicResource for exactly this reason, and live theme
        // switching is the whole feature.
        WpfTestHost.Run(() =>
        {
            var merged = WpfTestHost.LoadResources();
            var card = (Style)merged["CardBorderStyle"];

            var padding = card.Setters.OfType<Setter>()
                .FirstOrDefault(s => s.Property.Name == "Padding");

            Assert.True(padding is not null, "CardBorderStyle no longer sets Padding");

            var dynamic = Assert.IsType<DynamicResourceExtension>(padding!.Value);
            Assert.Equal("CardPadding", dynamic.ResourceKey);
        });
    }
}
