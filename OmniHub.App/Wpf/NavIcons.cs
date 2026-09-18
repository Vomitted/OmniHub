// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using Binding = System.Windows.Data.Binding;
using Rectangle = System.Windows.Shapes.Rectangle;
using RadioButton = System.Windows.Controls.RadioButton;

namespace OmniHub.App.Wpf;

/// <summary>
/// The sidebar icons, one per panel type.
///
/// These were written inline in MainWindow.xaml when there were seven fixed nav items. The
/// switcher is built from a saved layout now, so they have to be produced on demand instead --
/// and they do have to be produced, because dropping them and putting the keyboard shortcut in
/// front of each name instead made the sidebar visibly worse.
///
/// Built in code rather than kept as resources. A resource is a single instance and an element
/// has one parent, so two workspaces leading with the same panel would contend for one Canvas;
/// the fix for that is x:Shared="False", which is only honoured in a compiled resource dictionary
/// and is rejected by every test in this suite that loads a dictionary at runtime. A factory has
/// neither problem: each call is a new object.
///
/// The stroke binds to the nav item's own foreground rather than taking a colour, which is what
/// makes the icon follow selection and hover through the style's triggers. Shapes do not inherit
/// Foreground the way text does, so the binding is the mechanism.
/// </summary>
public static class NavIcons
{
    public static UIElement For(string panelType) => panelType switch
    {
        "dashboard" => Group(
            Box(1, 1), Box(9, 1), Box(1, 9), Box(9, 9)),

        "fans" => Group(
            Ring(6, 6, 4),
            Stroke(8, 6, 8, 1), Stroke(10, 9, 14, 12), Stroke(6, 9, 2, 12)),

        "performance" => Group(
            Stroke(3, 2, 3, 14), Dot(1, 9, 4),
            Stroke(8, 2, 8, 14), Dot(6, 4, 4),
            Stroke(13, 2, 13, 14), Dot(11, 7, 4)),

        "power" => Group(
            Box(1, 4, 11, 8, radius: 1),
            Fill(12, 6.5, 2.5, 3)),

        "system" => Group(
            Stroke(2, 13, 6, 9), Stroke(6, 9, 9, 11), Stroke(9, 11, 13, 5),
            Dot(11.5, 1.5, 3.5)),

        "diagnostics" => Group(
            Stroke(1, 14, 15, 14),
            Stroke(2, 11, 6, 11), Stroke(6, 11, 9, 4), Stroke(9, 4, 12, 8), Stroke(12, 8, 15, 8)),

        "settings" => Group(
            Stroke(1, 4, 15, 4), Dot(9, 2, 4),
            Stroke(1, 8, 15, 8), Dot(3, 6, 4),
            Stroke(1, 12, 15, 12), Dot(7, 10, 4)),

        // A workspace leading with something this build does not know, or with nothing yet.
        // Four marks in a square: a grid of things, which is what a workspace is.
        _ => Group(
            Dot(2, 2, 3.5), Dot(10, 2, 3.5), Dot(2, 10, 3.5), Dot(10, 10, 3.5)),
    };

    private static Canvas Group(params UIElement[] parts)
    {
        var canvas = new Canvas { Width = 16, Height = 16, Margin = new Thickness(0, 0, 10, 0) };
        foreach (var part in parts) canvas.Children.Add(part);
        return canvas;
    }

    /// <summary>The nav item's foreground, so the icon tracks selection and hover.</summary>
    private static Binding Inherited() => new("Foreground")
    {
        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(RadioButton) },
    };

    private static T Outlined<T>(T shape) where T : Shape
    {
        shape.StrokeThickness = 1.3;
        shape.SetBinding(Shape.StrokeProperty, Inherited());
        return shape;
    }

    private static T Solid<T>(T shape) where T : Shape
    {
        shape.SetBinding(Shape.FillProperty, Inherited());
        return shape;
    }

    private static UIElement Stroke(double x1, double y1, double x2, double y2) =>
        Outlined(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, StrokeStartLineCap = PenLineCap.Round });

    private static UIElement Box(double left, double top, double width = 6, double height = 6, double radius = 0)
    {
        var box = Outlined(new Rectangle { Width = width, Height = height, RadiusX = radius, RadiusY = radius });
        Place(box, left, top);
        return box;
    }

    private static UIElement Fill(double left, double top, double width, double height)
    {
        var box = Solid(new Rectangle { Width = width, Height = height });
        Place(box, left, top);
        return box;
    }

    private static UIElement Ring(double left, double top, double size)
    {
        var ring = Outlined(new Ellipse { Width = size, Height = size });
        Place(ring, left, top);
        return ring;
    }

    private static UIElement Dot(double left, double top, double size)
    {
        var dot = Solid(new Ellipse { Width = size, Height = size });
        Place(dot, left, top);
        return dot;
    }

    private static void Place(UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
    }
}
