// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Brushes = System.Windows.Media.Brushes;

namespace OmniHub.App.Wpf.Controls;

/// <summary>
/// The machine drawn as a hero, lid up, with a reading on its screen: Frosted's, Neumorphic's and
/// Companion's (TECHNICAL-v5-ui.md, section 15). One vector drawing in fixed colours -- it is an object
/// in the scene, not a surface of the palette -- scaled to the room it has. The screen stays near-black
/// under every finish, so the white figure on it reads the same whatever is around it.
/// </summary>
public sealed class LaptopArt : Viewbox
{
    /// <summary>What the laptop is made of: warm under Frosted's lamp, graphite with a blue edge, silver on black.</summary>
    public enum Finish { Warm, Graphite, Silver }

    /// <summary>The figure on the screen.</summary>
    public TextBlock Reading { get; }

    /// <summary>The lid, which opens as the page arrives.</summary>
    public Border Lid { get; }

    public LaptopArt(Finish finish, double figureSize = 54, string? caption = "processor")
    {
        var (lidTop, lidBottom, deckTop, deckBottom, edge, glow) = finish switch
        {
            Finish.Graphite => (Rgb(0x3A, 0x3E, 0x44), Rgb(0x16, 0x18, 0x1B), Rgb(0x2E, 0x31, 0x36), Rgb(0x1A, 0x1C, 0x1F), Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x3A, 0x0A, 0x84, 0xFF)),
            Finish.Silver => (Rgb(0x4A, 0x4D, 0x52), Rgb(0x1B, 0x1C, 0x1F), Rgb(0x3E, 0x40, 0x44), Rgb(0x26, 0x27, 0x2A), Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x30, 0x40, 0x7C, 0xC8)),
            _ => (Rgb(0x2A, 0x28, 0x25), Rgb(0x15, 0x14, 0x12), Rgb(0x3A, 0x37, 0x33), Rgb(0x1C, 0x1A, 0x18), Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x55, 0xFF, 0xD7, 0x9A)),
        };

        // The screen: near-black, a soft light in its upper half that breathes while the page is shown.
        var light = new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = Frozen(new RadialGradientBrush
            {
                Center = new Point(0.5, 0.35), GradientOrigin = new Point(0.5, 0.35), RadiusX = 0.8, RadiusY = 0.9,
                GradientStops = { new GradientStop(glow, 0), new GradientStop(Color.FromArgb(0, glow.R, glow.G, glow.B), 1) },
            }),
        };
        Motion.Loop(light, light, OpacityProperty, 1, 0.55, 4200, autoReverse: true);

        Reading = new TextBlock
        {
            FontSize = figureSize, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
            FontFamily = new FontFamily("Segoe UI Variable Display Light, Segoe UI Light, Segoe UI"), FontWeight = FontWeights.Light,
        };
        var label = new TextBlock { Text = caption ?? "", Visibility = caption is null ? Visibility.Collapsed : Visibility.Visible, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Frozen(new SolidColorBrush(Rgb(0xC6, 0xC6, 0xC6))) };
        var screen = new Grid
        {
            Children =
            {
                new Border { CornerRadius = new CornerRadius(7), Background = Frozen(new SolidColorBrush(Rgb(0x0B, 0x0C, 0x0E))) },
                light,
                new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Reading, label } },
            },
        };

        Lid = new Border
        {
            Width = 250, Height = 160, CornerRadius = new CornerRadius(12), Padding = new Thickness(9),
            BorderThickness = new Thickness(1.5), BorderBrush = Frozen(new SolidColorBrush(edge)),
            Background = Frozen(new LinearGradientBrush(lidTop, lidBottom, 90)),
            Child = screen, RenderTransformOrigin = new Point(0.5, 1), RenderTransform = new ScaleTransform(1, 1),
        };

        var deck = new Path
        {
            Data = Geometry.Parse("M 0,0 L 290,0 L 280,12 Q 278,14 272,14 L 18,14 Q 12,14 10,12 Z"),
            Fill = Frozen(new LinearGradientBrush(deckTop, deckBottom, 90)),
            Stroke = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF))), StrokeThickness = 1,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var shadow = new Ellipse
        {
            Width = 300, Height = 22, Margin = new Thickness(0, -4, 0, 0),
            Fill = Frozen(new RadialGradientBrush(Color.FromArgb(0x70, 0, 0, 0), Color.FromArgb(0, 0, 0, 0))),
        };

        MaxWidth = 360;
        Child = new StackPanel { Width = 300, Children = { new Border { Child = Lid, HorizontalAlignment = HorizontalAlignment.Center }, deck, shadow } };
    }

    /// <summary>The lid opening as the page arrives, inside the motion budget.</summary>
    public void Open()
    {
        if (!Motion.On || Lid.RenderTransform is not ScaleTransform lift) return;
        lift.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.15, 1, TimeSpan.FromMilliseconds(OmniHub.Core.Theming.MotionBudget.EnterMs + 160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop,
        });
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static T Frozen<T>(T brush) where T : Freezable { brush.Freeze(); return brush; }
}
