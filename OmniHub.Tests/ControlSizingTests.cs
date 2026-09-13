using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace OmniHub.Tests;

/// <summary>
/// A control given a fixed Height smaller than its style needs does not overflow, it CLIPS, and
/// WPF reports nothing when it happens.
///
/// Both halves of that are why this exists. A "Copy command" button was written with Height 28
/// against FlatButtonStyle, which measures 35.29 -- padding of 14,8 plus a 1px border plus 13px
/// text -- so seven pixels were cut off, and it built and ran without a word. The same mistake at
/// a smaller scale was already in every other view: Height 34 against that same 35.29, shaving
/// the descenders off "Copy", "Apply" and "Deploy" by 1.29px.
///
/// The numbers live in the styles and were being copied by hand into the views, which is the
/// actual defect. This measures the styles for real and checks the copies still agree.
/// </summary>
public class ControlSizingTests
{
    /// <summary>
    /// Styles that set their own FontSize, so their measurement is deterministic rather than
    /// dependent on whatever a parent inherits down.
    ///
    /// Styles that do NOT set one are deliberately absent. NumericTextBoxStyle inherits its size,
    /// so measuring it parentless here would produce a confident number with nothing to do with
    /// how it renders inside a view. A test that measures the wrong thing is worse than one that
    /// measures less.
    /// </summary>
    public static IEnumerable<object[]> SelfSizedButtonStyles() => new[]
    {
        new object[] { "FlatButtonStyle" },
        new object[] { "PrimaryButtonStyle" },
    };

    [Theory]
    [MemberData(nameof(SelfSizedButtonStyles))]
    public void FixedHeightsInTheViewsFitTheStyleTheyUse(string styleKey)
    {
        WpfTestHost.Run(() =>
        {
            var merged = WpfTestHost.LoadResources("Midnight.xaml");

            // The longest label in the application, measured unconstrained so it stays on one
            // line. A wrapped label would measure taller and pass this for the wrong reason.
            var probe = new Button
            {
                Content = "Test latency under load",
                Style = (Style)merged[styleKey],
            };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double needed = probe.DesiredSize.Height;

            var offenders = new List<string>();

            // Matched per ELEMENT, not per file.
            //
            // A first version searched for any Height in any file mentioning the style, and
            // "found" seven clipping controls that were nothing of the sort: a 22px toggle track
            // in Styles.xaml, 30px segmented pills, a 32px chip. Those carry different styles
            // with smaller fonts and are correct at those heights. Scoping to the opening tag
            // that actually names the style is the difference between a test and a nuisance.
            foreach (string file in Directory.EnumerateFiles(WpfTestHost.WpfDir, "*.xaml", SearchOption.AllDirectories))
            {
                string xaml = File.ReadAllText(file);
                if (!xaml.Contains(styleKey)) continue;

                foreach (Match tag in Regex.Matches(xaml, @"<Button\b[^>]*?/?>", RegexOptions.Singleline))
                {
                    if (!tag.Value.Contains(styleKey)) continue;

                    var h = Regex.Match(tag.Value, @"Height=""(\d+(?:\.\d+)?)""");
                    if (!h.Success) continue;

                    double height = double.Parse(h.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    if (height < needed)
                        offenders.Add($"{Path.GetFileName(file)}: Height={h.Groups[1].Value} on a {styleKey} button");
                }
            }

            Assert.True(offenders.Count == 0,
                $"{styleKey} measures {needed:0.00}px, so these clip:\n  "
                + string.Join("\n  ", offenders.Distinct()));
        });
    }

    /// <summary>
    /// Code-behind is the other half, and it is where the reported bug actually was. A button
    /// built in C# must not pin a Height; MinHeight is fine, since it sets a floor without
    /// capping what the content needs.
    /// </summary>
    [Fact]
    public void CodeBehindButtonsDoNotPinAHeight()
    {
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(WpfTestHost.WpfDir, "*.xaml.cs", SearchOption.AllDirectories))
        {
            string cs = File.ReadAllText(file);

            // Only the object initialisers that build one of these buttons. Narrowed to a window
            // around the style reference rather than the whole file, so an unrelated control
            // sized elsewhere in the same view is not swept in.
            foreach (Match init in Regex.Matches(cs, @"new Button\s*\{[^}]*\}", RegexOptions.Singleline))
            {
                if (!init.Value.Contains("ButtonStyle")) continue;

                var h = Regex.Match(init.Value, @"(?<!Min)Height\s*=\s*(\d+)");
                if (h.Success)
                    offenders.Add($"{Path.GetFileName(file)}: Height = {h.Groups[1].Value}");
            }
        }

        Assert.True(offenders.Count == 0,
            "a button built in code pins a Height, which clips when the style needs more:\n  "
            + string.Join("\n  ", offenders));
    }
}
