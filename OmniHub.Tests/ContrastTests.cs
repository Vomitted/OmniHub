using System.IO;
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
    private const double MinimumRatio = 4.5;

    /// <summary>
    /// Surfaces text is drawn on. CardSheenStyle lightens the top of a card very slightly over
    /// PanelColor, which only ever helps against a dark ground, so testing the unlit colour is the
    /// conservative choice rather than an oversight.
    /// </summary>
    private static readonly string[] Grounds = { "BackgroundColor", "PanelColor", "PanelAltColor" };

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

                foreach (string groundKey in Grounds)
                {
                    if (merged[groundKey] is not Color ground) continue;

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
    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        (double hi, double lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

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
