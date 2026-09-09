using System.Windows;
using System.Windows.Controls;

// Aliased per file, as every other view here does: this project sets both UseWindowsForms and
// UseWPF, so these names exist in both stacks and a bare reference is ambiguous.
using UserControl = System.Windows.Controls.UserControl;
using RadioButton = System.Windows.Controls.RadioButton;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// One sidebar destination hosting several screens behind a segmented selector.
///
/// The sidebar had grown a tab per screen rather than a tab per subject, so CPU tuning and GPU
/// power -- two halves of one decision about how much the machine is allowed to draw -- sat in
/// different places, and app GPU routing sat apart from the Windows-side settings it belongs
/// with.
///
/// This groups them without moving any content. Each screen stays the view it already was, with
/// its own layout, its own subscriptions and its own lifecycle; only the route to it changes.
/// That is deliberate: merging their markup would mean rewriting two large views at once in
/// order to change where a click lands, and the screens themselves were not what needed fixing.
/// </summary>
public partial class GroupView : UserControl, IDisposable
{
    private readonly List<(string Label, Func<UserControl> Make)> _sections;
    private readonly Dictionary<string, UserControl> _built = new();

    public GroupView(params (string Label, Func<UserControl> Make)[] sections)
    {
        InitializeComponent();
        _sections = sections.ToList();

        bool first = true;
        foreach (var (label, _) in _sections)
        {
            var pill = new RadioButton
            {
                Content = label.ToUpperInvariant(),
                GroupName = "GroupSection",
                Height = 32,
                MinWidth = 120,
                Margin = new Thickness(first ? 0 : 3, 0, 0, 0),
                Style = (Style)FindResource("PillRadioStyle"),
                Tag = label,
            };
            pill.Checked += SectionChecked;
            Selector.Children.Add(pill);
            first = false;
        }

        // Show the first section. Setting IsChecked raises Checked, which does the work.
        if (Selector.Children.Count > 0 && Selector.Children[0] is RadioButton opening)
            opening.IsChecked = true;

        // Then build the others while nothing is happening, for the same reason MainWindow
        // prewarms its tabs: lazy construction is worth having on the launch path and not worth
        // paying for on a click. Queued one per idle callback so each yields to input.
        foreach (var (label, _) in _sections)
        {
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                new Action(() => Resolve(label)));
        }
    }

    /// <summary>
    /// The screen currently shown, so MainWindow's card stagger can find the panel it needs.
    /// Without this the animation silently stops on grouped tabs, because a GroupView is a Grid
    /// rather than the ScrollViewer that check expects.
    /// </summary>
    public UserControl? CurrentSection => Host.Content as UserControl;

    private void SectionChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string label }) return;
        Host.Content = Resolve(label);
    }

    /// <summary>
    /// Builds a section the first time it is asked for, then reuses it.
    ///
    /// Lazy for the same reason the tabs themselves are: opening this destination should not pay
    /// for the screen nobody picked. Cached rather than rebuilt because these views hold live
    /// subscriptions and per-screen state, and because TuningView owns a running controller.
    /// </summary>
    private UserControl? Resolve(string label)
    {
        if (_built.TryGetValue(label, out var existing)) return existing;

        var section = _sections.FirstOrDefault(s => s.Label == label);
        if (section.Make is null) return null;

        UserControl view;
        try
        {
            view = section.Make();
        }
        catch (Exception ex)
        {
            // Same reasoning as MainWindow.ResolveView: this process holds fan control, and
            // losing it to an exception out of a click handler is worse than losing one screen.
            view = new UserControl
            {
                Content = new TextBlock
                {
                    Margin = new Thickness(26, 20, 26, 20),
                    TextWrapping = TextWrapping.Wrap,
                    Text = $"The {label} screen could not be opened.\n\n{ex.Message}\n\n"
                         + "Everything else, including fan control, is still running.",
                },
            };
        }

        _built[label] = view;
        return view;
    }

    /// <summary>
    /// Disposes the screens this owns.
    ///
    /// MainWindow.Cleanup disposes whatever in its view dictionary is IDisposable, and with these
    /// screens now behind a container they are no longer in that dictionary. Without forwarding,
    /// TuningView's adaptive controller would be left running after shutdown, which is precisely
    /// the "controller still steering power limits after the window is gone" case its own Dispose
    /// exists to prevent.
    /// </summary>
    public void Dispose()
    {
        foreach (var view in _built.Values.OfType<IDisposable>())
        {
            try { view.Dispose(); } catch { }
        }

        _built.Clear();
    }
}
