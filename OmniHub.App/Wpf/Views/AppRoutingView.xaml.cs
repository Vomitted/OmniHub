// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using RadioButton = System.Windows.Controls.RadioButton;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OmniHub.Core.Apps;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// Per-application GPU preference (TECHNICAL-v5-ui.md, section 14). The routes are read from the
/// registry when the page opens and after a change made here, held in memory, and narrowed there by
/// the search and the preference filter; the list builds only the rows on screen. It used to rebuild
/// a card and a combo box for every route on every keystroke, and this machine has four hundred.
/// </summary>
public partial class AppRoutingView : UserControl
{
    private List<AppGpuRoute> _routes = new();

    /// <summary>
    /// The filter's state, held rather than read from the controls: the add, remove and preference
    /// paths all re-apply it, and reaching into a control from those is how a rebuild ends up
    /// discarding what was typed.
    /// </summary>
    private string _search = "";
    private AppGpuPreference? _showing;

    public AppRoutingView()
    {
        InitializeComponent();
        Reload();
    }

    public void RefreshList() => Reload();

    private void Reload()
    {
        _routes = GpuAppRouting.GetAll().ToList();
        Apply();
    }

    /// <summary>Narrows the held routes and shows them; nothing here touches the registry.</summary>
    private void Apply()
    {
        var shown = GpuAppRouting.Filter(_routes, _search, _showing);
        AppList.ItemsSource = shown.Select(r => new Row(r, this)).ToList();
        ShowCounts();

        bool narrowed = _search.Trim().Length > 0 || _showing is not null;
        SearchCount.Text = _routes.Count == 0 ? "" : narrowed ? $"{shown.Count} OF {_routes.Count}" : $"{_routes.Count} ROUTED";

        // Two different empty states. "Nothing routed yet" is wrong when there are routes and the
        // filter excluded them all -- it sends someone to add a route they already have.
        EmptyNote.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyNote.Text = _routes.Count == 0
            ? "No applications routed yet. Use \"Detect Running App\" to pick from what is open, or \"Browse\" to select an executable."
            : $"No routed application matches this filter. There are {_routes.Count} routed in total.";
    }

    /// <summary>Each filter with how many routes hold its preference.</summary>
    private void ShowCounts()
    {
        int Count(AppGpuPreference p) => _routes.Count(r => r.Preference == p);
        ShowAllBtn.Content = $"ALL  {_routes.Count}";
        ShowHighBtn.Content = $"HIGH PERFORMANCE  {Count(AppGpuPreference.HighPerformance)}";
        ShowSavingBtn.Content = $"POWER SAVING  {Count(AppGpuPreference.PowerSaving)}";
        ShowAutoBtn.Content = $"WINDOWS DECIDES  {Count(AppGpuPreference.Auto)}";
    }

    /// <summary>
    /// A preference chosen on a row: written, and the counts brought up to date. The row stays where it
    /// is until the filter next changes, even if it no longer matches: a row vanishing under the
    /// pointer as it is clicked reads as the click having failed.
    /// </summary>
    private void Chosen(string path, AppGpuPreference preference)
    {
        GpuAppRouting.SetPreference(path, preference);
        int i = _routes.FindIndex(r => string.Equals(r.ExecutablePath, path, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _routes[i] = _routes[i] with { Preference = preference };
        ShowCounts();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text;
        Apply();
    }

    private void PreferenceFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } || AppList is null) return;   // Checked fires during InitializeComponent
        _showing = Enum.TryParse(tag, out AppGpuPreference p) ? p : null;
        Apply();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Row row) return;
        GpuAppRouting.Remove(row.Path);
        _routes.RemoveAll(r => string.Equals(r.ExecutablePath, row.Path, StringComparison.OrdinalIgnoreCase));
        Apply();
    }

    private void AddAppBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Select an application", Filter = "Executables (*.exe)|*.exe" };
        if (dialog.ShowDialog() != true) return;

        GpuAppRouting.SetPreference(dialog.FileName, AppGpuPreference.HighPerformance);
        Reload();
    }

    private void DetectAppBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new DetectedAppsWindow { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true || picker.SelectedPath is null) return;

        GpuAppRouting.SetPreference(picker.SelectedPath, AppGpuPreference.HighPerformance);
        Reload();
    }

    // The registry can hold a path that is malformed or points at a drive that no longer exists;
    // GetDirectoryName throws on the former. A Store app is named by its ID and has no folder.
    private static string FolderOf(string path)
    {
        try { return System.IO.Path.GetDirectoryName(path) is { Length: > 0 } dir ? dir : "Microsoft Store app"; }
        catch { return path; }
    }

    /// <summary>One route as the list shows it: its name, its folder, and its preference as three choices.</summary>
    public sealed class Row : INotifyPropertyChanged
    {
        private readonly AppRoutingView _owner;
        private AppGpuPreference _preference;

        internal Row(AppGpuRoute route, AppRoutingView owner)
        {
            Path = route.ExecutablePath;
            _preference = route.Preference;
            _owner = owner;
            Name = System.IO.Path.GetFileName(Path) is { Length: > 0 } name ? name : Path;
            Folder = FolderOf(Path);
        }

        public string Path { get; }
        public string Name { get; }
        public string Folder { get; }

        // Setting one of these false is the radio group clearing the old choice; only a choice made matters.
        public bool IsAuto { get => _preference == AppGpuPreference.Auto; set { if (value) Choose(AppGpuPreference.Auto); } }
        public bool IsSaving { get => _preference == AppGpuPreference.PowerSaving; set { if (value) Choose(AppGpuPreference.PowerSaving); } }
        public bool IsHigh { get => _preference == AppGpuPreference.HighPerformance; set { if (value) Choose(AppGpuPreference.HighPerformance); } }

        private void Choose(AppGpuPreference preference)
        {
            if (_preference == preference) return;
            _preference = preference;
            _owner.Chosen(Path, preference);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
