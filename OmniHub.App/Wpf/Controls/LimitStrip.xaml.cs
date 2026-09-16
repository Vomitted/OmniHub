using System.Windows;
using System.Windows.Controls;
using OmniHub.Core.Hardware;
using UserControl = System.Windows.Controls.UserControl;

namespace OmniHub.App.Wpf.Controls;

/// <summary>
/// What is actually holding this processor back, right now.
///
/// The arithmetic behind this has existed for a while and is already tested: PowerSnapshot
/// reports all five constraints as percentages of their own limits, and TightestLimit picks the
/// binding one. It writes the right sentence too. What it did not have was anywhere to appear:
/// it lived in a card on the Tuning tab that is Visibility.Collapsed by default, reached through
/// a sub-tab of a sub-tab.
///
/// That is the wrong place for it. Being at 99% of core current and 60% of the power limit says
/// plainly that raising the power limit will do nothing -- which is the single most useful thing
/// this application can tell somebody about their machine, and a conclusion nobody can reach
/// from a wattage on its own. It belongs where the readings are, not behind a disclosure.
///
/// A control rather than a block of code in one view, because two screens want it: the Dashboard
/// as a permanent strip, and Performance beside the knobs it tells you not to bother turning.
/// </summary>
public partial class LimitStrip : UserControl
{
    /// <summary>
    /// Above this, a constraint is treated as binding.
    ///
    /// Ninety-five rather than a hundred because these are sampled ratios of instantaneous
    /// readings: a processor genuinely pinned against a limit reads 97, 99, 101, 98 across
    /// consecutive samples, and a threshold at 100 would report it as unconstrained most of the
    /// time it was constrained.
    /// </summary>
    private const double BindingPercent = 95.0;

    private readonly List<(Border Fill, ColumnDefinition Filled, ColumnDefinition Remainder, TextBlock Value)> _rows = new();

    public LimitStrip() => InitializeComponent();

    /// <summary>
    /// Shows the snapshot, or says why there is nothing to show.
    ///
    /// A null snapshot is a real and common state -- no PawnIO driver, a firmware whose PM-table
    /// version this build does not recognise, a machine that is not AMD at all -- and it renders
    /// as a sentence naming the reason rather than as five empty bars, which would read as five
    /// limits sitting at zero.
    /// </summary>
    public void Show(PowerSnapshot? snapshot, string? unavailableReason = null)
    {
        if (snapshot is not { } s)
        {
            Verdict.Text = unavailableReason is { Length: > 0 } reason
                ? $"Processor limits unavailable: {reason}"
                : "Processor limits unavailable on this machine.";

            Rows.Children.Clear();
            _rows.Clear();
            return;
        }

        var limits = s.Limits();

        // Built once, then only the values move. The order PowerSnapshot returns is documented as
        // stable precisely so a caller can do this.
        if (_rows.Count != limits.Length)
        {
            Rows.Children.Clear();
            _rows.Clear();
            foreach (var (name, _) in limits) BuildRow(name);
        }

        for (int i = 0; i < limits.Length; i++)
        {
            double percent = Math.Clamp(limits[i].Percent, 0, 100);

            _rows[i].Value.Text = $"{percent:0}%";
            _rows[i].Filled.Width = new GridLength(percent, GridUnitType.Star);
            _rows[i].Remainder.Width = new GridLength(100 - percent, GridUnitType.Star);

            // Colour on the bar, never on the name. A limit at 99% is a fact about the machine,
            // not a warning about it, and a red label would read as something being wrong.
            _rows[i].Fill.SetResourceReference(Border.BackgroundProperty,
                percent >= BindingPercent ? "WarnBrush" : "MetricCpuBrush");
        }

        var tightest = s.TightestLimit();

        Verdict.Text = tightest.Percent >= BindingPercent
            ? $"Held back by {tightest.Name.ToLowerInvariant()}, at {tightest.Percent:0}% of its limit. "
              + "Raising anything with room to spare below will not change this."
            : $"Nothing is close to its limit. The tightest is {tightest.Name.ToLowerInvariant()} "
              + $"at {tightest.Percent:0}%.";
    }

    private void BuildRow(string name)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(148) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });

        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(StyleProperty, "TileFoot");
        Grid.SetColumn(label, 0);

        var track = new Grid
        {
            Height = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 10, 0),
        };

        var filled = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        var rest = new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star) };
        track.ColumnDefinitions.Add(filled);
        track.ColumnDefinitions.Add(rest);

        var bar = new Border { CornerRadius = new CornerRadius(2) };
        bar.SetResourceReference(Border.BackgroundProperty, "MetricCpuBrush");
        Grid.SetColumn(bar, 0);

        var remainder = new Border { CornerRadius = new CornerRadius(2) };
        remainder.SetResourceReference(Border.BackgroundProperty, "PanelAltBrush");
        Grid.SetColumn(remainder, 1);

        track.Children.Add(bar);
        track.Children.Add(remainder);
        Grid.SetColumn(track, 1);

        var value = new TextBlock
        {
            Text = "--",
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        value.SetResourceReference(StyleProperty, "TileFoot");
        Grid.SetColumn(value, 2);

        grid.Children.Add(label);
        grid.Children.Add(track);
        grid.Children.Add(value);

        Rows.Children.Add(grid);
        _rows.Add((bar, filled, rest, value));
    }
}
