// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OmniHub.Core.Theming;
using Brushes = System.Windows.Media.Brushes;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace OmniHub.App.Wpf.Controls;

/// <summary>
/// A reading set in dots, from <see cref="DotFont"/>: the Dot matrix look's figures. Its text is also
/// its accessible name, since a screen reader cannot read dots.
/// </summary>
public sealed class DotText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(DotText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            (d, e) => AutomationProperties.SetName(d, (string)e.NewValue)));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(DotText),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }

    /// <summary>Centre to centre, in pixels. Each dot fills four fifths of it.</summary>
    public double Pitch { get; init; } = 6;

    public DotText() => SetResourceReference(FillProperty, "TextPrimaryBrush");

    protected override Size MeasureOverride(Size available) => new(DotFont.Columns(Text) * Pitch, DotFont.Rows * Pitch);

    protected override void OnRender(DrawingContext dc)
    {
        double r = Pitch * 0.4;
        foreach (var (column, row) in DotFont.Cells(Text))
            dc.DrawEllipse(Fill, null, new Point((column + 0.5) * Pitch, (row + 0.5) * Pitch), r, r);
    }
}

/// <summary>
/// A field of dots, each lit or not by a function its owner supplies: the Dot matrix look's bars, its
/// five-minute chart and its memory grid are all this. Row 0 is the bottom row.
/// </summary>
public sealed class DotField : FrameworkElement
{
    /// <summary>What a cell shows.</summary>
    public enum Tone { Off, Dim, Lit, Signal }

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(DotField),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How much of the field has lit, left to right, for the entrance. One at rest.</summary>
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    public int Columns { get; init; }
    public int Rows { get; init; } = 1;
    public double Pitch { get; init; } = 10;

    /// <summary>The tone of the cell at a column and a row.</summary>
    public Func<int, int, Tone> Cell { get; set; } = (_, _) => Tone.Off;

    // The look's red, drawn and never written (LookSurfaces.Signal).
    private static readonly Brush SignalBrush = Frozen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(LookSurfaces.Signal)));

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    public DotField() => IsHitTestVisible = false;

    /// <summary>Redraws from <see cref="Cell"/>, after the readings behind it have moved.</summary>
    public void Refresh() => InvalidateVisual();

    /// <summary>Lights column by column, starting <paramref name="delayMs"/> from now; the sweep is the entrance's own length.</summary>
    public void Enter(int delayMs)
    {
        if (!Motion.On) return;
        BeginAnimation(ProgressProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(MotionBudget.EnterMs + 120))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    protected override Size MeasureOverride(Size available) => new(Columns * Pitch, Rows * Pitch);

    protected override void OnRender(DrawingContext dc)
    {
        var brushes = new[]
        {
            (Brush)FindResource("BorderStrongBrush"), (Brush)FindResource("TextFaintBrush"),
            (Brush)FindResource("TextPrimaryBrush"), SignalBrush,
        };
        double r = Pitch * 0.36, lit = Columns * Progress;

        for (int c = 0; c < Columns; c++)
            for (int row = 0; row < Rows; row++)
            {
                var tone = c < lit ? Cell(c, row) : Tone.Off;
                dc.DrawEllipse(brushes[(int)tone], null, new Point((c + 0.5) * Pitch, (Rows - row - 0.5) * Pitch), r, r);
            }
    }
}
