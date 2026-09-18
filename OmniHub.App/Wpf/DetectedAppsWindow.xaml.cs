// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Input;
using Window = System.Windows.Window;
using OmniHub.Core.Apps;

namespace OmniHub.App.Wpf;

public partial class DetectedAppsWindow : Window
{
    public string? SelectedPath { get; private set; }

    public DetectedAppsWindow()
    {
        InitializeComponent();

        // GetVisibleApps() enumerates every running process and opens each one's
        // MainModule -- on a system with many processes this can take well over a
        // second, the same class of UI-thread-blocking mistake already fixed
        // elsewhere in this app for BIOS calls. Scan off-thread and populate once
        // done, rather than freezing the window on open.
        Loaded += (_, _) =>
        {
            Task.Run(RunningAppDetector.GetVisibleApps).ContinueWith(t =>
            {
                Dispatcher.Invoke(() => AppList.ItemsSource = t.Result);
            }, TaskScheduler.Default);
        };
    }

    private void Select_Click(object sender, RoutedEventArgs e) => TryAccept();

    private void AppList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => TryAccept();

    private void TryAccept()
    {
        if (AppList.SelectedItem is DetectedApp app)
        {
            SelectedPath = app.ExecutablePath;
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
