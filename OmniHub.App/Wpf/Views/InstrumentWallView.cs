// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OmniHub.Core.Hardware;
using OmniHub.Core.Telemetry;

// This project sets both UseWPF and UseWindowsForms (WinForms is needed only for the tray icon),
// so a dozen everyday type names exist twice. The csproj already aliases Brush, FontFamily,
// Point and Orientation globally; these are the remaining ones this file needs.
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Button = System.Windows.Controls.Button;
using UserControl = System.Windows.Controls.UserControl;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// One screen with everything on it.
///
/// The workspaces interface shows roughly six numbers on an eleven-hundred pixel window and pages
/// between screens to show six more. That is a comfortable way to present a control panel and a
/// poor way to present an instrument, and it is why two rounds of visual work, a palette and then
/// a type scale, both landed as "it still looks the same": neither changed how much was on
/// screen, which was the thing actually being complained about.
///
/// This is the other answer. Every reading the machine exposes, at once, one per row, with its
/// recent history beside it and the route it came from named. Nothing is paged and nothing is
/// behind a tab.
///
/// Built from <see cref="Metrics.All"/> rather than written out, which is what keeps it honest as
/// it gets denser. A row exists because a metric is defined, its number comes from MetricSource,
/// and a reading MetricSource has nothing for renders as "unavailable" rather than as a dash that
/// could be mistaken for a zero. Adding a metric in Core adds a row here; nobody can add a row
/// with no reading behind it.
/// </summary>
public sealed class InstrumentWallView : UserControl, IDisposable
{
    private readonly MetricSource _metrics;
    private readonly HardwareContext _ctx;
    private readonly List<Row> _rows = new();
    private readonly TextBlock _limit = new();
    private readonly TextBlock _state = new();

    /// <summary>One metric's row: the parts that change every tick, held so they can be updated.</summary>
    private sealed record Row(MetricDefinition Metric, TextBlock Value, TextBlock Unit, Polyline Trace, TextBlock Range);

    private readonly Action? _leave;

    /// <param name="leave">
    /// How to get out of here. Required in practice even though it is nullable, because this view
    /// hides the sidebar and the sidebar is the only route to Settings: without it, choosing this
    /// interface is a one-way door and the only way back is editing settings.json by hand. That
    /// is exactly what happened the first time it shipped.
    /// </param>
    public InstrumentWallView(HardwareContext ctx, MetricSource metrics, Action? leave = null)
    {
        _ctx = ctx;
        _metrics = metrics;
        _leave = leave;

        Content = Build();

        _metrics.Updated += OnUpdated;
        OnUpdated();
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;
    private FontFamily Font(string key) => TryFindResource(key) as FontFamily ?? new FontFamily("Consolas");

    private UIElement Build()
    {
        var page = new Grid { Margin = new Thickness(22, 16, 22, 16) };
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        page.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- identity line -------------------------------------------------
        //
        // One line rather than a header block. This interface is for somebody who wants the
        // numbers; the machine's name is context, not the subject, and giving it a 26px heading
        // would spend the densest part of the window on the least changing fact on it.
        var identity = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        identity.Children.Add(new TextBlock
        {
            Text = "OMNIHUB",
            FontFamily = Font("MonoFont"),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("TextPrimaryBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        identity.Children.Add(Divider());
        identity.Children.Add(new TextBlock
        {
            Text = $"{_ctx.Model.Manufacturer} {_ctx.Model.Product}".Trim(),
            FontFamily = Font("UiFont"),
            FontSize = 12,
            Foreground = Brush("TextMutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 380,
        });
        identity.Children.Add(Divider());
        identity.Children.Add(new TextBlock
        {
            Text = $"board {_ctx.Model.BaseboardProduct}",
            FontFamily = Font("MonoFont"),
            FontSize = 11,
            Foreground = Brush("TextFaintBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        identity.Children.Add(Divider());

        _state.FontFamily = Font("UiFont");
        _state.FontSize = 12;
        _state.Foreground = Brush("TextMutedBrush");
        _state.VerticalAlignment = VerticalAlignment.Center;
        identity.Children.Add(_state);

        var identityRow = new Grid();
        Grid.SetColumn(identity, 0);
        identityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        identityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identityRow.Children.Add(identity);

        // The way back. Small, and permanently on screen, because this interface has no sidebar
        // and the sidebar is where Settings lives: without this control, picking this interface
        // traps you in it.
        var back = new Button
        {
            Content = "WORKSPACES",
            Height = 26,
            Padding = new Thickness(11, 0, 11, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Style = TryFindResource("FlatButtonStyle") as Style,
            ToolTip = "Switch back to the sidebar interface. Also in Settings, Interface.",
        };
        back.Click += (_, _) => _leave?.Invoke();
        Grid.SetColumn(back, 1);
        identityRow.Children.Add(back);

        Grid.SetRow(identityRow, 0);
        page.Children.Add(identityRow);

        // ---- what is limiting the machine ----------------------------------
        //
        // Above the table rather than inside it, because it is the one line explaining every other
        // number on the screen: a clock that is low and a clock that is low BECAUSE the package hit
        // its power limit are different facts, and only one of them is worth acting on.
        var limitBar = new Border
        {
            Background = Brush("PanelAltBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(11, 7, 11, 7),
            Margin = new Thickness(0, 0, 0, 12),
        };
        _limit.FontFamily = Font("UiFont");
        _limit.FontSize = 12;
        _limit.Foreground = Brush("TextPrimaryBrush");
        limitBar.Child = _limit;
        Grid.SetRow(limitBar, 1);
        page.Children.Add(limitBar);

        // ---- the wall ------------------------------------------------------
        //
        // Two columns of rows rather than one, so thirteen readings and their history fit above
        // the fold on a 740px window instead of half of them being a scroll away. The point of
        // this interface is that nothing needs looking for.
        var wall = new Grid();
        wall.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        wall.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        wall.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var left = new StackPanel();
        var right = new StackPanel();
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        wall.Children.Add(left);
        wall.Children.Add(right);

        left.Children.Add(ColumnHeader());
        right.Children.Add(ColumnHeader());

        var all = Metrics.All;
        int half = (all.Count + 1) / 2;
        for (int i = 0; i < all.Count; i++)
            (i < half ? left : right).Children.Add(BuildRow(all[i]));

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = wall,
        };
        Grid.SetRow(scroll, 2);
        page.Children.Add(scroll);

        return page;
    }

    private TextBlock Divider() => new()
    {
        Text = "   ·   ",
        FontFamily = Font("MonoFont"),
        FontSize = 11,
        Foreground = Brush("TextFaintBrush"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The column headings, which is where a 10px monospace capital actually belongs.</summary>
    private UIElement ColumnHeader()
    {
        var grid = RowGrid();

        void Head(string text, int column, TextAlignment align = TextAlignment.Left)
        {
            var t = new TextBlock
            {
                Text = text,
                FontFamily = Font("MonoFont"),
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("TextFaintBrush"),
                TextAlignment = align,
            };
            Grid.SetColumn(t, column);
            grid.Children.Add(t);
        }

        Head("READING", 0);
        Head("NOW", 1, TextAlignment.Right);
        Head("RECENT", 3);
        Head("RANGE", 4, TextAlignment.Right);

        var wrap = new StackPanel();
        wrap.Children.Add(grid);
        wrap.Children.Add(new Border { Height = 1, Background = Brush("BorderBrush"), Margin = new Thickness(0, 3, 0, 5) });
        return wrap;
    }

    /// <summary>
    /// The row shape, defined once so the header and every row line up without anybody
    /// maintaining two sets of widths that have to agree.
    /// </summary>
    private static Grid RowGrid()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // label
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });                   // value
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });                   // unit
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });                   // sparkline
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });                   // range
        return g;
    }

    private UIElement BuildRow(MetricDefinition metric)
    {
        var grid = RowGrid();
        grid.Margin = new Thickness(0, 3, 0, 3);

        var label = new TextBlock
        {
            Text = metric.Label,
            FontFamily = Font("UiFont"),
            FontSize = 12.5,
            Foreground = Brush("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // The source, under the label, in the faintest text on screen. This project's first rule
        // is that a reading names where it came from, and on a screen showing every reading at
        // once that has to be per row: "the SMU power table" and "the kernel tick counters" fail
        // differently and are fixed by different things.
        var source = new TextBlock
        {
            Text = metric.Source,
            FontFamily = Font("UiFont"),
            FontSize = 10,
            Foreground = Brush("TextFaintBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var labelStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        labelStack.Children.Add(label);
        labelStack.Children.Add(source);
        Grid.SetColumn(labelStack, 0);
        grid.Children.Add(labelStack);

        var value = new TextBlock
        {
            FontFamily = Font("MonoFont"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextPrimaryBrush"),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);

        var unit = new TextBlock
        {
            Text = metric.Unit,
            FontFamily = Font("MonoFont"),
            FontSize = 10,
            Foreground = Brush("TextMutedBrush"),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(unit, 2);
        grid.Children.Add(unit);

        var trace = new Polyline
        {
            Stroke = Brush("AccentBrush"),
            StrokeThickness = 1.4,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var traceHost = new Border
        {
            Height = 20,
            Width = 88,
            Child = trace,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Grid.SetColumn(traceHost, 3);
        grid.Children.Add(traceHost);

        var range = new TextBlock
        {
            FontFamily = Font("MonoFont"),
            FontSize = 10,
            Foreground = Brush("TextFaintBrush"),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(range, 4);
        grid.Children.Add(range);

        _rows.Add(new Row(metric, value, unit, trace, range));
        return grid;
    }

    private void OnUpdated()
    {
        _state.Text = _ctx.FanBackend.Tier switch
        {
            Core.Vendors.VendorTier.Verified => "fan control available",
            Core.Vendors.VendorTier.Reading => "readable only",
            _ => "no vendor interface",
        };

        _limit.Text = _metrics.BindingLimitName is { } binding
            ? $"Limited by {binding}."
            : "Nothing is limiting the processor right now.";

        foreach (Row row in _rows)
            Update(row);
    }

    private void Update(Row row)
    {
        double? now = _metrics.Value(row.Metric.Key);

        if (now is null)
        {
            // Not a dash and not a zero. A dash beside a unit reads as a measurement of nothing,
            // and this application's whole premise is that a reading nobody took is reported as
            // one nobody took.
            row.Value.Text = "unavailable";
            row.Value.FontSize = 10;
            row.Value.Foreground = Brush("TextFaintBrush");
            row.Unit.Visibility = Visibility.Collapsed;
            row.Trace.Points = new PointCollection();
            row.Range.Text = "";
            return;
        }

        row.Value.FontSize = 15;
        row.Value.Text = now.Value.ToString(row.Metric.Format, CultureInfo.InvariantCulture);
        row.Unit.Visibility = Visibility.Visible;

        // Colour on the figure, never on the words beside it. Threshold colouring of a numeric
        // readout is data visualisation; a tinted label is decoration pretending to be a warning.
        row.Value.Foreground =
            row.Metric.HotAt is { } hot && now >= hot ? Brush("DangerBrush")
            : row.Metric.WarnAt is { } warn && now >= warn ? Brush("WarnBrush")
            : Brush("TextPrimaryBrush");

        var history = _metrics.History(row.Metric.Key);
        var taken = history.Samples.Where(s => s is not null).Select(s => s!.Value).ToList();
        row.Range.Text = taken.Count > 1
            ? taken.Min().ToString(row.Metric.Format, CultureInfo.InvariantCulture) + " to "
              + taken.Max().ToString(row.Metric.Format, CultureInfo.InvariantCulture)
            : "";

        // Segments rather than one polyline: a stretch with no samples is a gap, and joining
        // across it would draw a line through time nobody measured.
        var longest = history.Segments(88, 20).OrderByDescending(s => s.Count).FirstOrDefault();
        row.Trace.Points = longest is null
            ? new PointCollection()
            : new PointCollection(longest.Select(p => new Point(p.X, p.Y)));
    }

    public void Dispose() => _metrics.Updated -= OnUpdated;
}
