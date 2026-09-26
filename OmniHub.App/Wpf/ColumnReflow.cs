// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;

namespace OmniHub.App.Wpf;

/// <summary>
/// Puts a page's side column underneath its main one when the window is too narrow for both.
///
/// Every console page is the same three-column grid -- the instrument, a ten-pixel gutter, the
/// side panes -- with a spare row for the side panes to fall into. At 150% display scaling a laptop
/// screen is 1280 px wide, and the window's minimum width is narrower than two columns need, so
/// below each page's own threshold the side panes stack rather than squeeze the numbers.
/// </summary>
internal static class ColumnReflow
{
    private const double Gutter = 10;

    public static void Apply(double width, double below, ColumnDefinition gutter, ColumnDefinition side,
                             double sideWidth, FrameworkElement content) =>
        Apply(width, below, gutter, side, new GridLength(sideWidth), content);

    /// <summary>The same, for a page of two equal columns: pass a star width for the second.</summary>
    public static void Apply(double width, double below, ColumnDefinition gutter, ColumnDefinition side,
                             GridLength sideWidth, FrameworkElement content)
    {
        bool narrow = width < below;

        gutter.Width = new GridLength(narrow ? 0 : Gutter);
        side.Width = narrow ? new GridLength(0) : sideWidth;
        Grid.SetColumn(content, narrow ? 0 : 2);
        Grid.SetRow(content, narrow ? 1 : 0);
        content.Margin = narrow ? new Thickness(0, Gutter, 0, 0) : new Thickness(0);
    }
}
