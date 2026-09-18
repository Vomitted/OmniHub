using System.IO;
using System.Windows;
using System.Windows.Media;

namespace OmniHub.Tests;

/// <summary>
/// Every colour this application draws text in has to be legible on every surface it can land on,
/// in every palette.
///
/// This is checked rather than eyeballed because eyeballing it is what produced the failures it
/// was written to catch. A hardcoded white foreground on the primary button looked perfectly fine
/// for as long as the accent was a deep blue, and it survived the addition of seven more palettes
/// without anyone noticing that in Mono it had become white text on a #E8E8E8 to #FFFFFF button:
/// a contrast ratio of 1.00 to 1, which is not "hard to read", it is invisible. TextFaintColor
/// failed in all eight palettes at once, between 2.81 and 4.06 to 1, and it is the colour the 9px
/// status chips are drawn in.
///
/// Neither is visible in a diff and neither throws. The only way this stays fixed is if the
/// numbers are asserted, so a palette added later cannot quietly reintroduce it.
///
/// The threshold is WCAG 2.1 AA for normal text. Large text is allowed 3.0 by the standard, but
/// that allowance is not taken here: these colours are used at 9, 10 and 11 pixels far more often
/// than they are used large, and a per-key exception list would be a way of losing track of which
/// ones were deliberate.
/// </summary>
public class ContrastTests
{
    private const double MinimumRatio = OmniHub.Core.Theming.Contrast.MinimumRatio;

    /// <summary>
    /// The dimmest disabled-state Opacity any style in Styles.xaml actually sets.
    ///
    /// Read from the file rather than declared as a constant here. The first version of this test
    /// hardcoded the value it expected, which meant it asserted its own assumption instead of the
    /// application: dropping the real setters back to 0.45 left it passing happily. A test that
    /// cannot fail when the thing it describes changes is decoration.
    /// </summary>
    private static double DisabledOpacity => LazyDisabledOpacity.Value;

    private static readonly Lazy<double> LazyDisabledOpacity = new(() =>
    {
        string styles = File.ReadAllText(Path.Combine(WpfTestHost.WpfDir, "Styles.xaml"));

        // Only Opacity setters inside an IsEnabled=False trigger. Opacity appears elsewhere in
        // the file for sheens and rails, and those are not what fades a label.
        var matches = System.Text.RegularExpressions.Regex.Matches(
            styles,
            @"IsEnabled""\s+Value=""False"">.*?Property=""Opacity""\s+Value=""(?<v>[\d.]+)""",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        var values = matches.Select(m => double.Parse(m.Groups["v"].Value,
            System.Globalization.CultureInfo.InvariantCulture)).ToList();

        Assert.True(values.Count > 0, "no disabled-state Opacity setters found in Styles.xaml");
        return values.Min();
    });

    /// <summary>
    /// Every surface text can actually land on.
    ///
    /// The first version of this test listed only Background, Panel and PanelAlt, passed, and
    /// the user still reported dark text blending in. It was right about what it checked and
    /// wrong about what it covered:
    ///
    ///   PanelHoverColor backs every hovered button, pill, nav item, combo item and grid row.
    ///   AccentSoftColor backs the "active" status chips in Optimize, Settings and Tuning, and
    ///   it is TRANSLUCENT, so it has to be composited onto the card underneath before it means
    ///   anything -- comparing against the raw ARGB would compare against a colour nothing ever
    ///   paints.
    ///
    /// AccentDimColor is deliberately absent. It is declared and never used as a background, and
    /// including it would force every faint colour several shades brighter to satisfy a pair that
    /// cannot occur. Grounds belong here when something is drawn on them, not when they exist.
    /// </summary>
    private static IEnumerable<(string Name, Color Value)> GroundsFor(ResourceDictionary merged)
    {
        foreach (string key in new[] { "BackgroundColor", "PanelColor", "PanelAltColor", "PanelHoverColor" })
            if (merged[key] is Color c)
                yield return (key, c);

        if (merged["AccentSoftColor"] is Color soft)
        {
            foreach (string under in new[] { "PanelColor", "PanelAltColor" })
                if (merged[under] is Color bg)
                    yield return ($"AccentSoft over {under}", Composite(soft, bg));
        }
    }

    /// <summary>Alpha-composites a translucent colour onto an opaque one.</summary>
    private static Color Composite(Color fg, Color bg)
    {
        double a = fg.A / 255.0;
        return Color.FromRgb(
            (byte)Math.Round(fg.R * a + bg.R * (1 - a)),
            (byte)Math.Round(fg.G * a + bg.G * (1 - a)),
            (byte)Math.Round(fg.B * a + bg.B * (1 - a)));
    }

    /// <summary>
    /// Colours used as a Foreground somewhere in the application.
    ///
    /// The metric and status colours are on this list because they genuinely are drawn as text
    /// (FansView, GpuView, PowerView and DashboardView all set Foreground to one of them), not
    /// merely as fills. That is exactly how Nord's DangerColor came to sit at 3.82 to 1 on a card.
    /// </summary>
    private static readonly string[] TextColors =
    {
        "TextPrimaryColor", "TextMutedColor", "TextFaintColor",
        "AccentColor", "GoodColor", "WarnColor", "DangerColor",
        "MetricCpuColor", "MetricMemColor", "MetricGpuColor",
    };

    public static IEnumerable<object[]> Palettes() =>
        Directory.EnumerateFiles(Path.Combine(WpfTestHost.WpfDir, "Palettes"), "*.xaml")
                 .Select(p => new object[] { Path.GetFileName(p) });

    [Theory]
    [MemberData(nameof(Palettes))]
    public void EveryTextColourIsLegibleOnEverySurface(string paletteFile)
    {
        WpfTestHost.Run(() =>
        {
            var merged = WpfTestHost.LoadResources(paletteFile);
            var failures = new List<string>();

            foreach (string textKey in TextColors)
            {
                if (merged[textKey] is not Color text) continue;

                foreach ((string groundKey, Color ground) in GroundsFor(merged))
                {
                    double ratio = Contrast(text, ground);
                    if (ratio < MinimumRatio)
                        failures.Add($"{textKey} on {groundKey} = {ratio:0.00}:1");
                }
            }

            Assert.True(failures.Count == 0,
                $"{paletteFile} has text below {MinimumRatio}:1 --\n  " + string.Join("\n  ", failures));
        });
    }

    /// <summary>
    /// The inverse case, and the one that was actually broken: text drawn ON the accent rather
    /// than beside it.
    ///
    /// Both gradient stops are tested because AccentGradientBrush ramps between them and the text
    /// sits across the whole width, so being readable at one end is not enough.
    /// </summary>
    [Theory]
    [MemberData(nameof(Palettes))]
    public void TextOnTheAccentFillIsLegibleAcrossTheWholeGradient(string paletteFile)
    {
        WpfTestHost.Run(() =>
        {
            var merged = WpfTestHost.LoadResources(paletteFile);

            Assert.True(merged["OnAccentColor"] is Color,
                $"{paletteFile} declares no OnAccentColor, so PrimaryButtonStyle has nothing legible to use.");

            var onAccent = (Color)merged["OnAccentColor"]!;

            foreach (string stop in new[] { "AccentColor", "AccentColor2" })
            {
                if (merged[stop] is not Color accent) continue;

                double ratio = Contrast(onAccent, accent);
                Assert.True(ratio >= MinimumRatio,
                    $"{paletteFile}: OnAccentColor on {stop} = {ratio:0.00}:1, below {MinimumRatio}:1. "
                    + "This is the primary button's own text on its own background.");
            }
        });
    }

    /// <summary>
    /// Disabled controls, which is where the reported "dark text blending into the background"
    /// actually lived.
    ///
    /// Setting Opacity on a control fades the WHOLE element, so its label and its own fill both
    /// composite toward the card behind at the same rate and the contrast BETWEEN them collapses.
    /// That is easy to miss because the colours themselves are untouched and every static check
    /// of them passes. At the 0.40 and 0.45 these styles used to carry, the label measured
    /// between 3.70 and 4.14 to 1 across the palettes.
    /// </summary>
    [Theory]
    [MemberData(nameof(Palettes))]
    public void DisabledControlsKeepTheirLabelReadable(string paletteFile)
    {
        WpfTestHost.Run(() =>
        {
            var merged = WpfTestHost.LoadResources(paletteFile);

            var card = (Color)merged["PanelColor"]!;
            var label = (Color)merged["TextPrimaryColor"]!;
            var fill = (Color)merged["PanelAltColor"]!;

            Color Fade(Color c) => Color.FromRgb(
                (byte)Math.Round(c.R * DisabledOpacity + card.R * (1 - DisabledOpacity)),
                (byte)Math.Round(c.G * DisabledOpacity + card.G * (1 - DisabledOpacity)),
                (byte)Math.Round(c.B * DisabledOpacity + card.B * (1 - DisabledOpacity)));

            double ratio = Contrast(Fade(label), Fade(fill));
            Assert.True(ratio >= MinimumRatio,
                $"{paletteFile}: a disabled button's label is {ratio:0.00}:1 at the opacity Styles.xaml sets ({DisabledOpacity}).");
        });
    }

    /// <summary>
    /// WCAG 2.1 relative luminance.
    ///
    /// The channel values are linearised before weighting rather than used raw, and that step is
    /// what separates this from a naive brightness comparison: sRGB is gamma encoded, so a mid
    /// grey is nowhere near half the light of white, and averaging the encoded values would rate
    /// pairs as readable that plainly are not.
    ///
    /// Alpha is ignored. Every colour tested here is opaque, and a translucent one would need
    /// compositing against its actual backdrop rather than a ratio against a nominal one.
    /// </summary>
    // The arithmetic moved to Core when the palette editor came to need it too. Kept as thin
    // wrappers rather than inlined at every call site, so this file still reads in WPF colours --
    // and, more to the point, so this audit and the editor cannot disagree about whether a palette
    // is acceptable. Two implementations of one rule is worse than either answer.
    private static OmniHub.Core.Theming.Rgb ToRgb(Color c) => new(c.R, c.G, c.B);

    private static double RelativeLuminance(Color c) =>
        OmniHub.Core.Theming.Contrast.RelativeLuminance(ToRgb(c));

    private static double Contrast(Color a, Color b) =>
        OmniHub.Core.Theming.Contrast.Ratio(ToRgb(a), ToRgb(b));

    /// <summary>
    /// The formula itself, checked against the two anchors the standard fixes exactly, so a
    /// mistake in it cannot quietly pass every palette at once.
    /// </summary>
    [Fact]
    public void ContrastFormulaMatchesTheKnownAnchors()
    {
        Assert.Equal(21.0, Contrast(Colors.White, Colors.Black), 2);
        Assert.Equal(1.0, Contrast(Colors.White, Colors.White), 2);
    }
}
