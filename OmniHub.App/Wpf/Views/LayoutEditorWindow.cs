using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using OmniHub.Core.Workspaces;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ListBox = System.Windows.Controls.ListBox;
using TextBox = System.Windows.Controls.TextBox;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// An explicit mode for rearranging the interface.
///
/// A mode rather than making the panels draggable wherever they sit, because the thing somebody
/// does ninety-nine times out of a hundred is read a number off a screen, and a layout that can be
/// destroyed by a slightly long click while doing that is worse than one that cannot be changed at
/// all. Nothing here touches the running layout until the layout is saved, so backing out is free.
///
/// Built in code rather than in XAML: it is two lists and some buttons, and a second .xaml file to
/// hold that is more ceremony than the window is worth.
/// </summary>
public sealed class LayoutEditorWindow : Window
{
    private sealed class PanelRow
    {
        public required string Type { get; init; }
        public required string Name { get; init; }
        public int Span { get; set; }
        public override string ToString() => $"{Name}   ({Span}/{WorkspaceLayout.Columns} wide)";
    }

    private sealed class WorkspaceRow
    {
        public required string Name { get; set; }
        public required ObservableCollection<PanelRow> Panels { get; init; }
        public override string ToString() => Name;
    }

    private readonly IReadOnlyList<(string Key, string Name)> _catalogue;
    private readonly ObservableCollection<WorkspaceRow> _workspaces = new();

    private readonly ListBox _workspaceList = new() { Width = 220 };
    private readonly ListBox _panelList = new();
    private readonly ComboBox _panelPicker = new();
    private readonly ComboBox _spanPicker = new();

    /// <summary>The edited layout. Only meaningful when the dialog returned true.</summary>
    public WorkspaceLayout Result { get; private set; } = WorkspaceLayout.Defaults();

    public LayoutEditorWindow(WorkspaceLayout layout, IReadOnlyList<(string Key, string Name)> catalogue)
    {
        _catalogue = catalogue;

        Title = "Edit layout";
        Width = 900;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Token("BackgroundBrush");

        foreach (var w in layout.Workspaces) _workspaces.Add(ToRow(w));

        Content = BuildBody();

        _workspaceList.ItemsSource = _workspaces;
        _workspaceList.SelectionChanged += (_, _) => ShowSelectedWorkspace();
        if (_workspaces.Count > 0) _workspaceList.SelectedIndex = 0;
    }

    private WorkspaceRow ToRow(Workspace w) => new()
    {
        Name = w.Name,
        Panels = new ObservableCollection<PanelRow>(
            w.Panels.Select(p => new PanelRow { Type = p.Type, Name = DisplayName(p.Type), Span = p.Span })),
    };

    /// <summary>
    /// The catalogue's name for a panel, or the raw key marked as unavailable.
    ///
    /// A layout from a later build can name panels this one does not have, and the editor has to
    /// show them rather than hide them -- they are still in the file, and they will still be
    /// written back.
    /// </summary>
    private string DisplayName(string type)
    {
        foreach (var (key, name) in _catalogue)
            if (key == type) return name;

        return $"{type}  (not available in this version)";
    }

    private UIElement BuildBody()
    {
        foreach (var (key, name) in _catalogue) _panelPicker.Items.Add($"{name}|{key}");
        if (_panelPicker.Items.Count > 0) _panelPicker.SelectedIndex = 0;
        _panelPicker.Width = 320;

        // Only widths that divide twelve evenly. A seven-column panel is expressible and leaves a
        // five-column hole beside it that nothing fits, so it is not offered.
        foreach (int span in new[] { 12, 6, 4, 3 })
            _spanPicker.Items.Add(span == 12 ? "12/12  (full width)" : $"{span}/12");
        _spanPicker.SelectedIndex = 0;
        _spanPicker.Width = 150;

        var root = new Grid { Margin = new Thickness(18) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // --- workspaces -------------------------------------------------
        var left = new DockPanel { Margin = new Thickness(0, 0, 18, 0) };

        var leftLabel = Label("WORKSPACES");
        DockPanel.SetDock(leftLabel, Dock.Top);
        left.Children.Add(leftLabel);

        var workspaceButtons = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        workspaceButtons.Children.Add(Row(Action("Add", AddWorkspace), Action("Rename", RenameWorkspace)));
        workspaceButtons.Children.Add(Row(Action("Move up", () => MoveWorkspace(-1)), Action("Move down", () => MoveWorkspace(1))));
        workspaceButtons.Children.Add(Row(Action("Remove", RemoveWorkspace)));
        DockPanel.SetDock(workspaceButtons, Dock.Bottom);
        left.Children.Add(workspaceButtons);

        left.Children.Add(_workspaceList);

        Grid.SetColumn(left, 0);
        root.Children.Add(left);

        // --- panels in the selected workspace ---------------------------
        var right = new DockPanel();

        var rightLabel = Label("PANELS IN THIS WORKSPACE");
        DockPanel.SetDock(rightLabel, Dock.Top);
        right.Children.Add(rightLabel);

        var panelButtons = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panelButtons.Children.Add(Row(_panelPicker, _spanPicker, Action("Add panel", AddPanel)));
        panelButtons.Children.Add(Row(
            Action("Move up", () => MovePanel(-1)),
            Action("Move down", () => MovePanel(1)),
            Action("Change width", CyclePanelWidth),
            Action("Remove", RemovePanel)));
        DockPanel.SetDock(panelButtons, Dock.Bottom);
        right.Children.Add(panelButtons);

        right.Children.Add(_panelList);

        Grid.SetColumn(right, 1);
        root.Children.Add(right);

        // --- commit -----------------------------------------------------
        var footer = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        footer.Children.Add(Action("Reset to defaults", ResetToDefaults));
        footer.Children.Add(Action("Cancel", () => { DialogResult = false; }));
        footer.Children.Add(Action("Save layout", Commit, primary: true));

        Grid.SetRow(footer, 1);
        Grid.SetColumnSpan(footer, 2);
        root.Children.Add(footer);

        return root;
    }

    // ---------- workspace commands ----------

    private WorkspaceRow? Selected => _workspaceList.SelectedItem as WorkspaceRow;

    private void ShowSelectedWorkspace() => _panelList.ItemsSource = Selected?.Panels;

    private void AddWorkspace()
    {
        _workspaces.Add(new WorkspaceRow { Name = "New workspace", Panels = new ObservableCollection<PanelRow>() });
        _workspaceList.SelectedIndex = _workspaces.Count - 1;
    }

    private void RenameWorkspace()
    {
        if (Selected is not { } workspace) return;

        if (Prompt("Name for this workspace", workspace.Name) is { } name && !string.IsNullOrWhiteSpace(name))
        {
            workspace.Name = name.Trim();
            Refresh(_workspaces, workspace, _workspaceList);
        }
    }

    private void RemoveWorkspace()
    {
        if (Selected is not { } workspace) return;

        // The last one cannot go. A switcher with nothing in it has no way back to anything, and
        // the model replaces an empty layout with the defaults on the next load, which would look
        // like the editor silently undid the removal.
        if (_workspaces.Count <= 1)
        {
            System.Windows.MessageBox.Show(
                "There has to be at least one workspace.",
                "Cannot remove", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int at = _workspaces.IndexOf(workspace);
        _workspaces.Remove(workspace);
        _workspaceList.SelectedIndex = Math.Clamp(at, 0, _workspaces.Count - 1);
    }

    private void MoveWorkspace(int delta)
    {
        if (Selected is not { } workspace) return;

        int from = _workspaces.IndexOf(workspace);
        int to = from + delta;
        if (to < 0 || to >= _workspaces.Count) return;

        _workspaces.Move(from, to);
        _workspaceList.SelectedIndex = to;
    }

    // ---------- panel commands ----------

    private void AddPanel()
    {
        if (Selected is not { } workspace) return;
        if (_panelPicker.SelectedItem is not string entry) return;

        string key = entry[(entry.LastIndexOf('|') + 1)..];
        int span = _spanPicker.SelectedIndex switch { 1 => 6, 2 => 4, 3 => 3, _ => WorkspaceLayout.Columns };

        workspace.Panels.Add(new PanelRow { Type = key, Name = DisplayName(key), Span = span });
        _panelList.SelectedIndex = workspace.Panels.Count - 1;
    }

    private void RemovePanel()
    {
        if (Selected is { } workspace && _panelList.SelectedItem is PanelRow panel)
            workspace.Panels.Remove(panel);
    }

    private void MovePanel(int delta)
    {
        if (Selected is not { } workspace || _panelList.SelectedItem is not PanelRow panel) return;

        int from = workspace.Panels.IndexOf(panel);
        int to = from + delta;
        if (to < 0 || to >= workspace.Panels.Count) return;

        workspace.Panels.Move(from, to);
        _panelList.SelectedIndex = to;
    }

    private void CyclePanelWidth()
    {
        if (Selected is not { } workspace || _panelList.SelectedItem is not PanelRow panel) return;

        panel.Span = panel.Span switch { 12 => 6, 6 => 4, 4 => 3, _ => 12 };
        Refresh(workspace.Panels, panel, _panelList);
    }

    private void ResetToDefaults()
    {
        if (System.Windows.MessageBox.Show(
                "Replace every workspace with the seven this application ships with?",
                "Reset layout", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        _workspaces.Clear();
        foreach (var w in WorkspaceLayout.Defaults().Workspaces) _workspaces.Add(ToRow(w));
        _workspaceList.SelectedIndex = 0;
    }

    private void Commit()
    {
        Result = new WorkspaceLayout(
            WorkspaceLayout.CurrentVersion,
            _workspaces.Select(w => new Workspace(
                w.Name,
                w.Panels.Select(p => new PanelPlacement(p.Type, p.Span)).ToArray())).ToArray())
            .Normalised();

        DialogResult = true;
    }

    // ---------- small helpers ----------

    /// <summary>
    /// Makes a list redraw one item whose ToString has changed.
    ///
    /// These rows are plain objects rather than INotifyPropertyChanged models, which is the right
    /// trade for a dialog that lives for a few seconds -- but it does mean a changed name or width
    /// has to be announced by taking the item out and putting it back.
    /// </summary>
    private static void Refresh<T>(ObservableCollection<T> items, T item, ListBox list)
    {
        int at = items.IndexOf(item);
        if (at < 0) return;

        items.RemoveAt(at);
        items.Insert(at, item);
        list.SelectedIndex = at;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Consolas"),
        FontSize = 10.5,
        FontWeight = FontWeights.Bold,
        Foreground = Token("TextFaintBrush"),
        Margin = new Thickness(0, 0, 0, 8),
    };

    private Button Action(string text, Action click, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            Height = 36,
            MinWidth = 96,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(12, 0, 12, 0),
        };

        if (TryFindResource(primary ? "PrimaryButtonStyle" : "FlatButtonStyle") is Style style)
            button.Style = style;

        button.Click += (_, _) => click();
        return button;
    }

    private static StackPanel Row(params UIElement[] children)
    {
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

        foreach (var child in children)
        {
            if (child is FrameworkElement element and not Button)
                element.Margin = new Thickness(0, 0, 6, 6);

            row.Children.Add(child);
        }

        return row;
    }

    /// <summary>
    /// A one-line text prompt. WPF has no input box, and a whole dialog class for one string is
    /// more than renaming a workspace is worth.
    /// </summary>
    private string? Prompt(string title, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 12), MinWidth = 320 };

        var window = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = Token("BackgroundBrush"),
        };

        var ok = new Button { Content = "OK", Height = 36, MinWidth = 96, IsDefault = true };
        var cancel = new Button { Content = "Cancel", Height = 36, MinWidth = 96, Margin = new Thickness(6, 0, 0, 0), IsCancel = true };

        if (TryFindResource("PrimaryButtonStyle") is Style primary) ok.Style = primary;
        if (TryFindResource("FlatButtonStyle") is Style flat) cancel.Style = flat;

        ok.Click += (_, _) => { window.DialogResult = true; };

        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        window.Content = panel;

        window.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };

        return window.ShowDialog() == true ? box.Text : null;
    }

    // Looked up rather than hard-coded, and tolerant of a palette missing one: a theme token that
    // has gone away should cost a colour, not the dialog.
    private static Brush Token(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
}
