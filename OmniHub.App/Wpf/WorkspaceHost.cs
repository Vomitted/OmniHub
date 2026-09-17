using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using OmniHub.Core.Workspaces;
using UserControl = System.Windows.Controls.UserControl;

namespace OmniHub.App.Wpf;

/// <summary>
/// Draws one workspace: its panels, in order, flowed across a twelve-column grid.
///
/// The common case is a single full-width panel, which is what every default workspace is and
/// therefore what somebody who never opens the editor always sees. That case is hosted directly
/// with no grid around it, so the screens this application already had are not merely equivalent
/// to what they were -- they are the same element in the same place.
/// </summary>
public sealed class WorkspaceHost : UserControl
{
    /// <summary>
    /// Builds the host for a workspace.
    /// </summary>
    /// <param name="resolve">
    /// Turns a panel type into a control, or returns null for a type this build does not know.
    /// Kept as a delegate so the catalogue stays in MainWindow beside the factories it already
    /// owns, rather than becoming a second registry that can disagree with the first.
    /// </param>
    public WorkspaceHost(Workspace workspace, Func<string, UserControl?> resolve)
    {
        Focusable = false;

        var panels = workspace.Panels;

        if (panels.Count == 0)
        {
            Content = EmptyNotice(workspace.Name);
            return;
        }

        // One full-width panel is the shape of every screen this application used to have, and
        // wrapping it in a grid would change its measurement for no benefit.
        if (panels.Count == 1 && panels[0].Span >= WorkspaceLayout.Columns)
        {
            var only = resolve(panels[0].Type) ?? Placeholder(panels[0].Type);
            Detach(only);
            Content = only;
            return;
        }

        var grid = new Grid();
        for (int i = 0; i < WorkspaceLayout.Columns; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int column = 0, row = 0;

        foreach (var panel in panels)
        {
            int span = Math.Clamp(panel.Span, 1, WorkspaceLayout.Columns);

            // Wrap when the panel will not fit on the rest of this row. Flowing rather than
            // placing is what makes an overlap impossible to express -- see PanelPlacement.
            if (column + span > WorkspaceLayout.Columns)
            {
                column = 0;
                row++;
            }

            while (grid.RowDefinitions.Count <= row)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var content = resolve(panel.Type) ?? Placeholder(panel.Type);

            // A control can have one parent. The views are cached and shared between workspaces,
            // so one that is still attached to the workspace being left has to be released before
            // it can be added here -- otherwise switching back and forth throws.
            Detach(content);

            Grid.SetColumn(content, column);
            Grid.SetColumnSpan(content, span);
            Grid.SetRow(content, row);
            grid.Children.Add(content);

            column += span;
            if (column >= WorkspaceLayout.Columns) { column = 0; row++; }
        }

        Content = grid;
    }

    /// <summary>
    /// Releases a control from whatever is currently showing it.
    ///
    /// Only the containers this class puts things in, because those are the only parents it
    /// creates. A control hosted anywhere else is not this class's to take.
    /// </summary>
    private static void Detach(UserControl control)
    {
        switch (VisualTreeHelper.GetParent(control))
        {
            case Grid parent:
                parent.Children.Remove(control);
                break;

            case ContentPresenter presenter when presenter.TemplatedParent is ContentControl host
                                                 && ReferenceEquals(host.Content, control):
                host.Content = null;
                break;
        }

        // The logical parent, for the single-panel case where the host holds it as Content
        // directly rather than through a template.
        if (control.Parent is ContentControl owner && ReferenceEquals(owner.Content, control))
            owner.Content = null;
    }

    /// <summary>
    /// Stands in for a panel type this build does not recognise, naming it.
    ///
    /// A layout written by a later build must open. Skipping the panel silently would leave a
    /// hole the user cannot account for, and refusing the layout would lose the whole arrangement
    /// over one unknown entry -- so it renders as itself, and the saved file keeps the entry so
    /// that a build which understands it still can.
    /// </summary>
    private static UserControl Placeholder(string type) => new()
    {
        Focusable = false,
        Content = new Border
        {
            Margin = new Thickness(8),
            Padding = new Thickness(16, 13, 16, 13),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = Token("PanelAltBrush"),
            BorderBrush = Token("BorderBrush"),
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "PANEL NOT AVAILABLE",
                        FontFamily = new FontFamily("Consolas"),
                        FontSize = 10.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = Token("TextFaintBrush"),
                    },
                    new TextBlock
                    {
                        Margin = new Thickness(0, 6, 0, 0),
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = Token("TextPrimaryBrush"),
                        Text = $"This layout asks for a panel called \"{type}\", which this version "
                             + "does not have. It has been left in place rather than removed, so a "
                             + "version that does have it will still show it.",
                    },
                },
            },
        },
    };

    private static UserControl EmptyNotice(string name) => new()
    {
        Focusable = false,
        Content = new TextBlock
        {
            Margin = new Thickness(26, 24, 26, 24),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Token("TextFaintBrush"),
            Text = $"\"{name}\" has no panels yet. Use EDIT LAYOUT to add some.",
        },
    };

    // Looked up rather than hard-coded, and tolerant of a palette that is missing one: a theme
    // token that has gone away should cost a colour, not the window.
    private static Brush Token(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;
}
