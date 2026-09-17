using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OmniHub.Core.Telemetry;
using Brushes = System.Windows.Media.Brushes;
using UserControl = System.Windows.Controls.UserControl;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// One reading, at the size of a card: its name, its figure, and where it has been.
///
/// This is the panel that makes a workspace worth arranging. Until now a workspace could only hold
/// a whole tab, so building one by hand gained nothing over the seven that shipped; a single
/// reading is the unit somebody actually wants three of, side by side, while a game runs.
///
/// Colour lands on the numeral and nowhere else. The label, the unit and the footnote stay the
/// colour they always are -- a figure crossing a threshold is data visualisation, a sentence
/// turning amber is decoration pretending to be a warning.
/// </summary>
public sealed class MetricPanel : UserControl
{
    private const double SparkWidth = 200, SparkHeight = 30;

    private readonly string _key;
    private readonly MetricSource _source;

    private readonly TextBlock _value = new();
    private readonly TextBlock _foot = new();
    private readonly System.Windows.Shapes.Path _spark = new();

    public MetricPanel(string key, MetricSource source)
    {
        _key = key;
        _source = source;
        Focusable = false;

        var metric = Metrics.Find(key);
        var mono = Token<FontFamily>("MonoFont") ?? new FontFamily("Consolas");

        _value.FontFamily = mono;
        _value.FontSize = 34;
        _value.FontWeight = FontWeights.Bold;
        _value.Text = Metrics.Unavailable;

        _foot.FontFamily = mono;
        _foot.FontSize = 9.5;
        _foot.Margin = new Thickness(0, 6, 0, 0);
        _foot.Foreground = Brush("TextFaintBrush");
        _foot.TextWrapping = TextWrapping.Wrap;

        _spark.Stroke = Brush("AccentBrush");
        _spark.StrokeThickness = 1.4;
        _spark.Height = SparkHeight;
        _spark.Margin = new Thickness(0, 10, 0, 0);
        _spark.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        _spark.SnapsToDevicePixels = true;

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            // The catalogue's own label, so a panel and the overlay never disagree about what a
            // reading is called. An unknown key still gets a heading, naming itself.
            Text = metric?.Label ?? key.ToUpperInvariant(),
            FontFamily = mono,
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("TextFaintBrush"),
        });
        stack.Children.Add(_value);
        stack.Children.Add(_spark);
        stack.Children.Add(_foot);

        Content = PanelChrome.Card(stack);

        // Paired on Loaded/Unloaded rather than subscribed once, the same as DashboardView and
        // FansView: switching workspaces rebuilds the container and re-parents this control, and a
        // constructor-time subscription would leak a handler on every switch.
        Loaded += (_, _) => { _source.Updated += Refresh; Refresh(); };
        Unloaded += (_, _) => _source.Updated -= Refresh;

        SizeChanged += (_, _) => DrawSpark();
    }

    private void Refresh()
    {
        double? value = _source.Value(_key);

        _value.Text = Metrics.Text(_key, value);
        _value.Foreground = Metrics.LevelOf(_key, value) switch
        {
            MetricLevel.Hot => Brush("DangerBrush"),
            MetricLevel.Warn => Brush("WarnBrush"),
            _ => Brush("TextPrimaryBrush"),
        };

        // The limit is a percentage of something, and which something is the whole point of it.
        // A bare "97%" is the number without the finding.
        _foot.Text = _key switch
        {
            "limit" when _source.BindingLimitName is { } name => name.ToUpperInvariant(),
            "limit" => "NO SMU READING",
            _ when value is null => "NO READING",
            _ => "",
        };

        DrawSpark();
    }

    /// <summary>
    /// Draws the recent window.
    ///
    /// Scaled to its own range rather than to an absolute one, and gaps break the line rather than
    /// being drawn through -- both decided in Sparkline, which is where they can be tested.
    /// </summary>
    private void DrawSpark()
    {
        double width = _spark.ActualWidth > 1 ? _spark.ActualWidth : SparkWidth;

        var geometry = new PathGeometry();

        foreach (var segment in _source.History(_key).Segments(width, SparkHeight))
        {
            var figure = new PathFigure { StartPoint = new Point(segment[0].X, segment[0].Y), IsFilled = false };

            for (int i = 1; i < segment.Count; i++)
                figure.Segments.Add(new LineSegment(new Point(segment[i].X, segment[i].Y), isStroked: true));

            geometry.Figures.Add(figure);
        }

        geometry.Freeze();
        _spark.Data = geometry;
    }

    // Looked up rather than hard-coded, and tolerant of a palette missing one: a theme token that
    // has gone away should cost a colour, not the panel.
    private static Brush Brush(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;

    private static T? Token<T>(string key) where T : class =>
        System.Windows.Application.Current?.TryFindResource(key) as T;
}
