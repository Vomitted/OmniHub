// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OmniHub.Core.Telemetry;
using UserControl = System.Windows.Controls.UserControl;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// Every reading this machine gives, with its value and where it came from.
///
/// A table rather than a wall of cards, because the question it answers is not "how hot is it" --
/// the cards do that better -- but "what can this application actually see, and which of it is
/// working right now". Twelve rows, the ones that answered and the ones that did not, side by side
/// with the route each took.
///
/// The source column is the point. A figure that looks wrong is only actionable once you know
/// whether it came from the SMU power table, the NVIDIA driver or a kernel tick counter, because
/// those fail differently and are fixed by different things.
/// </summary>
public sealed class ReadingsTablePanel : UserControl
{
    private readonly MetricSource _source;
    private readonly Dictionary<string, TextBlock> _values = new();

    public ReadingsTablePanel(MetricSource source)
    {
        _source = source;
        Focusable = false;

        var mono = PanelChrome.Token<FontFamily>("MonoFont") ?? new FontFamily("Consolas");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        Header(grid, mono, 0, "READING", "VALUE", "SOURCE");

        int row = 1;
        foreach (var metric in Metrics.All)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Cell(grid, row, 0, new TextBlock
            {
                Text = metric.Label,
                FontFamily = mono,
                FontSize = 11,
                Foreground = PanelChrome.Brush("TextPrimaryBrush"),
            });

            var value = new TextBlock
            {
                Text = Metrics.Unavailable,
                FontFamily = mono,
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = PanelChrome.Brush("TextPrimaryBrush"),
            };
            _values[metric.Key] = value;
            Cell(grid, row, 1, value);

            Cell(grid, row, 2, new TextBlock
            {
                Text = metric.Source,
                FontFamily = mono,
                FontSize = 10,
                Foreground = PanelChrome.Brush("TextFaintBrush"),
                TextWrapping = TextWrapping.Wrap,
            });

            row++;
        }

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "WHAT THIS MACHINE REPORTS",
            FontFamily = mono,
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            Foreground = PanelChrome.Brush("TextFaintBrush"),
            Margin = new Thickness(0, 0, 0, 12),
        });
        stack.Children.Add(grid);

        Content = PanelChrome.Card(stack);

        Loaded += (_, _) => { _source.Updated += Refresh; Refresh(); };
        Unloaded += (_, _) => _source.Updated -= Refresh;
    }

    private void Refresh()
    {
        foreach (var metric in Metrics.All)
        {
            if (!_values.TryGetValue(metric.Key, out var cell)) continue;

            double? value = _source.Value(metric.Key);
            cell.Text = Metrics.Text(metric.Key, value);

            // A row that did not answer is dimmed rather than hidden. The absence is the finding:
            // "GPU power, unavailable" is what tells you the card is asleep or the driver is not
            // answering, and a table that quietly dropped the row would say nothing at all.
            cell.Foreground = value is null
                ? PanelChrome.Brush("TextFaintBrush")
                : Metrics.LevelOf(metric.Key, value) switch
                {
                    MetricLevel.Hot => PanelChrome.Brush("DangerBrush"),
                    MetricLevel.Warn => PanelChrome.Brush("WarnBrush"),
                    _ => PanelChrome.Brush("TextPrimaryBrush"),
                };
        }
    }

    private static void Header(Grid grid, FontFamily mono, int row, params string[] titles)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int column = 0; column < titles.Length; column++)
        {
            Cell(grid, row, column, new TextBlock
            {
                Text = titles[column],
                FontFamily = mono,
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = PanelChrome.Brush("TextFaintBrush"),
            });
        }
    }

    private static void Cell(Grid grid, int row, int column, UIElement content)
    {
        var host = new Border { Padding = new Thickness(0, 4, 12, 4), Child = (UIElement)content };

        Grid.SetRow(host, row);
        Grid.SetColumn(host, column);
        grid.Children.Add(host);
    }
}
