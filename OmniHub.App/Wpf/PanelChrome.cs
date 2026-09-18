// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;

namespace OmniHub.App.Wpf;

/// <summary>
/// The card a panel sits in.
///
/// Shared because the corner radius is a palette token and the panels were hard-coding it. The
/// eight themes range from square corners to twelve pixels, and the one in force here is four, so
/// a panel with a hand-written radius of ten was visibly rounder than every card beside it -- the
/// exact class of defect the theme tokens exist to prevent, introduced by the panels that were
/// supposed to be the customizable part.
/// </summary>
public static class PanelChrome
{
    /// <summary>Wraps panel content in a card that follows the palette.</summary>
    public static Border Card(UIElement content) => new()
    {
        Margin = new Thickness(6),
        Padding = new Thickness(16, 13, 16, 13),
        CornerRadius = Radius(),
        BorderThickness = new Thickness(1),
        Background = Brush("PanelBrush"),
        BorderBrush = Brush("BorderBrush"),
        VerticalAlignment = System.Windows.VerticalAlignment.Top,
        Child = content,
    };

    /// <summary>
    /// The palette's medium radius, or the reference design value when a palette has not been
    /// loaded yet. A theme without the token costs a corner, not the panel.
    /// </summary>
    public static CornerRadius Radius() =>
        System.Windows.Application.Current?.TryFindResource("RadiusMd") is CornerRadius radius
            ? radius
            : new CornerRadius(10);

    public static Brush Brush(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Transparent;

    public static T? Token<T>(string key) where T : class =>
        System.Windows.Application.Current?.TryFindResource(key) as T;
}
