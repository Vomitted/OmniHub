using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OmniHub.Core.Hardware;
using OmniHub.Core.Telemetry;
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
    ///
    /// Taken from Core rather than declared again here. The band below this strip answers the
    /// same question over time and has to use the same threshold, and two copies of a number
    /// that must agree is how they come to disagree.
    /// </summary>
    private const double BindingPercent = LimitHistory.BindingPercent;

    /// <summary>
    /// A colour per constraint, so one limit keeps the same colour in the band and the legend.
    ///
    /// Theme brushes rather than literals: the palette carries eight variants and a hard-coded
    /// colour would be the one element on screen that ignores the user's choice of theme.
    /// </summary>
    private static readonly Dictionary<string, string> BandBrushes = new()
    {
        ["Sustained power"] = "MetricCpuBrush",
        ["Boost power"] = "MetricMemBrush",
        ["Core current (EDC)"] = "AccentBrush",
        ["Core current (TDC)"] = "MetricGpuBrush",
        ["Temperature"] = "DangerBrush",
        [LimitHistory.Unconstrained] = "BorderBrush",
    };

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

    // ------------------------------------------------------------ the same question over time

    /// <summary>
    /// Draws what has been binding across the window, as a band on a real time axis.
    ///
    /// The strip above says what is holding the machine back at this instant. This says what has
    /// been holding it back, which is the question somebody actually arrives with -- and the
    /// answer, "you were current-limited for forty per cent of the last hour", is one nothing in
    /// this class of tool reports.
    ///
    /// Drawn with proportional grid columns rather than a custom control. A band is a row of
    /// rectangles whose widths are durations, which is what star sizing already does; a Canvas
    /// with measure and arrange overrides would be a lot of code to reimplement that.
    ///
    /// ponytail: one column per segment, bounded by the ring at 1,440. Move to a drawn Visual if
    /// a band ever gets slow enough to notice.
    /// </summary>
    public void ShowHistory(LimitHistory history, TimeSpan window)
    {
        var segments = LimitHistory.Segments(history.Since(DateTime.UtcNow - window));

        Band.ColumnDefinitions.Clear();
        Band.Children.Clear();
        Legend.Children.Clear();

        // Nothing yet is its own state. An empty band under a caption reads as a machine that
        // was measured and found to be doing nothing, which is not what it means.
        if (segments.Count == 0)
        {
            HistorySection.Visibility = Visibility.Collapsed;
            return;
        }

        HistorySection.Visibility = Visibility.Visible;
        BandNote.Text = LimitHistory.Describe(segments, window);

        for (int i = 0; i < segments.Count; i++)
        {
            // A real gap gets a column of its own rather than being closed up. Compressing it
            // would slide everything after it leftwards and quietly redraw when things happened.
            // Sub-threshold gaps are the moment between two samples at a change of limit, which
            // is not an outage and is absorbed.
            if (i > 0 && segments[i].FromUtc - segments[i - 1].ToUtc > LimitHistory.MaxGap)
                AddBandColumn(segments[i].FromUtc - segments[i - 1].ToUtc, null,
                              $"Not measured\n{segments[i - 1].ToUtc.ToLocalTime():HH:mm:ss} to "
                              + $"{segments[i].FromUtc.ToLocalTime():HH:mm:ss}");

            AddBandColumn(segments[i].Duration, segments[i].Name,
                          $"{segments[i].Name}\n{segments[i].FromUtc.ToLocalTime():HH:mm:ss} to "
                          + $"{segments[i].ToUtc.ToLocalTime():HH:mm:ss}");
        }

        foreach (var share in LimitHistory.Shares(segments))
            Legend.Children.Add(LegendChip(share));
    }

    /// <summary>One stretch of the band. A null name is an unmeasured gap, left empty.</summary>
    private void AddBandColumn(TimeSpan width, string? name, string tooltip)
    {
        Band.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(Math.Max(width.TotalSeconds, 0.001), GridUnitType.Star),
        });

        var block = new Border { ToolTip = tooltip };

        if (name is not null)
            block.SetResourceReference(BackgroundProperty,
                BandBrushes.TryGetValue(name, out string? key) ? key : "BorderBrush");

        Grid.SetColumn(block, Band.ColumnDefinitions.Count - 1);
        Band.Children.Add(block);
    }

    /// <summary>A dot and a percentage. Colour on the dot, never on the words beside it.</summary>
    private UIElement LegendChip(LimitShare share)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 14, 4),
        };

        var dot = new Border
        {
            Width = 9,
            Height = 9,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        dot.SetResourceReference(BackgroundProperty,
            BandBrushes.TryGetValue(share.Name, out string? key) ? key : "BorderBrush");

        var text = new TextBlock { Text = $"{share.Name}  {share.Fraction:0}%" };
        text.SetResourceReference(StyleProperty, "TileFoot");

        row.Children.Add(dot);
        row.Children.Add(text);
        return row;
    }
}
