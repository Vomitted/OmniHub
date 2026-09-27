// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OmniHub.Core.Theming;

namespace OmniHub.App.Wpf.Controls;

/// <summary>
/// How the interface moves, in one place (TECHNICAL-v5-ui.md, section 12).
///
/// Quick, because this user found a slow pass slow: every entrance inside <see cref="MotionBudget"/>.
/// Honest, because motion must not stand for a reading: nothing here animates a figure through values
/// the hardware did not give. Off entirely when Windows' own "Animation effects" is off. Loops run only
/// while their element is on screen, at 30 frames a second.
///
/// Entrances animate from their start values with key frames and <see cref="FillBehavior.Stop"/>, never
/// by writing a start value into the element: an entrance abandoned half way -- the page left before it
/// finished -- then falls back to the element's own resting value instead of leaving it invisible.
/// </summary>
internal static class Motion
{
    /// <summary>
    /// Whether anything moves: Windows' own "Animation effects", read in one place for the whole
    /// application. <see cref="ForReview"/> overrides it for the snapshot tool only, whose sandbox
    /// reports the setting off part way through a run and so could never film an entrance.
    /// </summary>
    public static bool On => ForReview ?? SystemParameters.ClientAreaAnimation;

    internal static bool? ForReview;

    private static readonly IEasingFunction Out = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction Breathe = Frozen(new SineEase { EasingMode = EasingMode.EaseInOut });

    private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>Fades and rises into place, starting <paramref name="delayMs"/> from now.</summary>
    public static void Enter(UIElement e, int delayMs = 0, double rise = 14)
    {
        if (!On) return;
        var total = Ms(delayMs + MotionBudget.EnterMs);

        e.BeginAnimation(UIElement.OpacityProperty, Held(0, 1, delayMs, total));
        if (Shift(e) is { } shift) shift.BeginAnimation(TranslateTransform.YProperty, Held(rise, 0, delayMs, total));
    }

    /// <summary>Enters each in turn, the whole inside the entrance budget.</summary>
    public static void Stagger(IReadOnlyList<UIElement> items)
    {
        for (int i = 0; i < items.Count; i++) Enter(items[i], MotionBudget.Delay(i, items.Count));
    }

    /// <summary>
    /// Draws in from the left: a clip that widens across the element. For curves, whose left end is
    /// the oldest reading, so they appear in the order they were measured.
    /// </summary>
    public static void Reveal(FrameworkElement e, int delayMs = 0, int ms = 420)
    {
        if (!On || e.ActualWidth <= 0 || e.ActualHeight <= 0) return;
        double w = e.ActualWidth, h = e.ActualHeight;

        var clip = new RectangleGeometry(new Rect(0, -40, 0, h + 80));
        var widen = new RectAnimation(new Rect(0, -40, 0, h + 80), new Rect(0, -40, w, h + 80), Ms(ms))
        {
            BeginTime = Ms(delayMs), EasingFunction = Out, FillBehavior = FillBehavior.Stop,
        };
        widen.Completed += (_, _) => { if (ReferenceEquals(e.Clip, clip)) e.Clip = null; };
        e.Clip = clip;
        clip.BeginAnimation(RectangleGeometry.RectProperty, widen);
    }

    /// <summary>A quick dip and return, for content that has just been replaced in place.</summary>
    public static void Refresh(UIElement e)
    {
        if (!On) return;
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.25, 1, Ms(200)) { EasingFunction = Out, FillBehavior = FillBehavior.Stop });
    }

    /// <summary>Eases a property to a value, or sets it outright when motion is off or nothing is on screen.</summary>
    public static void To(IAnimatable target, DependencyProperty property, double to, bool visible, int ms = 220)
    {
        if (On && visible)
            target.BeginAnimation(property, new DoubleAnimation(to, Ms(ms)) { EasingFunction = Out });
        else
        {
            target.BeginAnimation(property, null);
            ((DependencyObject)target).SetValue(property, to);
        }
    }

    /// <summary>Lifts a few pixels under the pointer.</summary>
    public static void Lift(FrameworkElement e, double by = 3)
    {
        if (Shift(e) is not { } shift) return;
        e.MouseEnter += (_, _) => { if (On) shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-by, Ms(160)) { EasingFunction = Out }); };
        e.MouseLeave += (_, _) => shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Ms(200)) { EasingFunction = Out });
    }

    /// <summary>Opacity breathing between 1 and <paramref name="low"/>, while the element is on screen.</summary>
    public static void Pulse(FrameworkElement e, double low = 0.35, int periodMs = 1600) =>
        Loop(e, e, UIElement.OpacityProperty, 1, low, periodMs, autoReverse: true);

    /// <summary>
    /// A property swinging between two values, or turning from one to the other and starting again,
    /// for as long as <paramref name="host"/> is on screen.
    /// </summary>
    public static void Loop(FrameworkElement host, IAnimatable target, DependencyProperty property,
                            double from, double to, int periodMs, bool autoReverse)
    {
        // Started and stopped by visibility, first: a hidden window runs nothing (AmbientMotionTests).
        DoubleAnimation? loop = null;
        void Sync() => target.BeginAnimation(property, On && host.IsVisible ? loop : null);
        host.IsVisibleChanged += (_, _) => Sync();

        loop = new DoubleAnimation(from, to, Ms(autoReverse ? periodMs / 2.0 : periodMs))
        {
            AutoReverse = autoReverse, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = autoReverse ? Breathe : null,
        };
        Timeline.SetDesiredFrameRate(loop, 30);
        loop.Freeze();
        Sync();
    }

    // Held at the start value until the delay has passed, then eased to the end.
    private static DoubleAnimationUsingKeyFrames Held(double from, double to, int delayMs, TimeSpan total)
    {
        var frames = new DoubleAnimationUsingKeyFrames { Duration = total, FillBehavior = FillBehavior.Stop };
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        if (delayMs > 0) frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(Ms(delayMs))));
        frames.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(total), Out));
        return frames;
    }

    // The element's own translation, made once. An element already carrying another transform keeps
    // it and fades without rising, rather than losing what it had.
    private static TranslateTransform? Shift(UIElement e)
    {
        if (e.RenderTransform is TranslateTransform { IsFrozen: false } t) return t;
        if (e.RenderTransform is not null && e.RenderTransform != Transform.Identity) return null;
        var shift = new TranslateTransform();
        e.RenderTransform = shift;
        return shift;
    }
}
