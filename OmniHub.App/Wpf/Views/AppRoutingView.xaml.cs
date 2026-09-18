// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using ComboBox = System.Windows.Controls.ComboBox;
using Button = System.Windows.Controls.Button;
using Microsoft.Win32;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OmniHub.Core.Apps;

namespace OmniHub.App.Wpf.Views;

public partial class AppRoutingView : UserControl
{
    public AppRoutingView()
    {
        InitializeComponent();
        Refresh();
    }

    private void AddAppBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select an application",
            Filter = "Executables (*.exe)|*.exe",
        };
        if (dialog.ShowDialog() != true) return;

        GpuAppRouting.SetPreference(dialog.FileName, AppGpuPreference.HighPerformance);
        Refresh();
    }

    private void DetectAppBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new DetectedAppsWindow { Owner = System.Windows.Window.GetWindow(this) };
        if (picker.ShowDialog() != true || picker.SelectedPath is null) return;

        GpuAppRouting.SetPreference(picker.SelectedPath, AppGpuPreference.HighPerformance);
        Refresh();
    }

    public void RefreshList() => Refresh();

    // The registry can hold a path that is malformed or points at a drive that no longer
    // exists; GetDirectoryName throws on the former. A row that cannot show its folder is
    // still a row worth showing, so this degrades to the raw string rather than failing the
    // whole list.
    private static string SafeDirectory(string path)
    {
        try { return System.IO.Path.GetDirectoryName(path) ?? path; }
        catch { return path; }
    }

    /// <summary>
    /// Current filter text. Held rather than read from the box inside Refresh, because Refresh
    /// also runs from the add, remove and preference handlers, and reaching into a control from
    /// those paths is how a rebuild ends up silently discarding what was typed.
    /// </summary>
    private string _filter = "";

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _filter = SearchBox.Text.Trim();
        Refresh();
    }

    private void Refresh()
    {
        var routes = GpuAppRouting.GetAll();
        int total = routes.Count;

        // Matched against the whole path, not just the file name. Two builds of the same game
        // share an executable, and "steamapps" or a folder name is often the only thing that
        // tells the rows apart -- which is exactly why the list shows the folder underneath.
        if (_filter.Length > 0)
            routes = routes
                .Where(r => r.ExecutablePath.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

        SearchCount.Text = _filter.Length == 0
            ? (total == 0 ? "" : $"{total} ROUTED")
            : $"{routes.Count} OF {total}";
        var panel = new StackPanel();

        foreach (var route in routes)
        {
            var row = new Border
            {
                Style = (Style)FindResource("CardBorderStyle"),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12),
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Filename plus the containing folder. Two builds of the same game share an
            // executable name, and a list showing "game.exe" twice gives no way to tell the
            // rows apart. The full path is the tooltip rather than the label so the row stays
            // readable at a glance.
            var nameStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            nameStack.Children.Add(new TextBlock
            {
                Text = System.IO.Path.GetFileName(route.ExecutablePath),
                Style = (Style)FindResource("BodyText"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            nameStack.Children.Add(new TextBlock
            {
                Text = SafeDirectory(route.ExecutablePath),
                Style = (Style)FindResource("MutedText"),
                FontSize = 10.5,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = route.ExecutablePath,
            });
            Grid.SetColumn(nameStack, 0);

            var combo = new ComboBox
            {
                Width = 168,
                Height = 32,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("OmniComboBoxStyle"),
                ItemsSource = new[] { AppGpuPreference.Auto, AppGpuPreference.PowerSaving, AppGpuPreference.HighPerformance },
                SelectedItem = route.Preference,
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is AppGpuPreference pref)
                    GpuAppRouting.SetPreference(route.ExecutablePath, pref);
            };
            Grid.SetColumn(combo, 1);

            // MinHeight, not Height: FlatButtonStyle measures 35.29px, so the 30 that used to be
            // here cut five pixels off the bottom of the button.
            var removeBtn = new Button { Content = "Remove", Width = 80, MinHeight = 36, Style = (Style)FindResource("FlatButtonStyle") };
            removeBtn.Click += (_, _) => { GpuAppRouting.Remove(route.ExecutablePath); Refresh(); };
            Grid.SetColumn(removeBtn, 2);

            grid.Children.Add(nameStack);
            grid.Children.Add(combo);
            grid.Children.Add(removeBtn);
            row.Child = grid;
            panel.Children.Add(row);
        }

        if (routes.Count == 0)
        {
            // Button labels are named exactly as they now read in the header. This text has
            // already gone stale once after a rename, and an empty state that points at a
            // button that does not exist is worse than no empty state.
            var emptyCard = new Border { Style = (Style)FindResource("CardBorderStyle"), Padding = new Thickness(20) };
            emptyCard.Child = new TextBlock
            {
                // Two different empty states, and telling them apart matters. "Nothing routed
                // yet, use Browse" is actively wrong when there ARE routes and the filter simply
                // excluded them all -- it sends someone to add a route they already have. The
                // comment above this already warned that a stale empty state is worse than none.
                Text = _filter.Length > 0
                    ? $"No routed application matches “{_filter}”. There are {total} routed in total."
                    : "No applications routed yet. Use \"Detect Running App\" to pick from what is open, or \"Browse\" to select an executable.",
                Style = (Style)FindResource("MutedText"),
                TextWrapping = TextWrapping.Wrap,
            };
            panel.Children.Add(emptyCard);
        }

        AppList.Content = panel;
    }
}
