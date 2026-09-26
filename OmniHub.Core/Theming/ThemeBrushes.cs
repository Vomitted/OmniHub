// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Theming;

/// <summary>
/// Which palette colours each shared brush in Theme.xaml is painted with, in gradient-stop order.
///
/// Theme.xaml binds its brushes' colours with DynamicResource, and a live theme switch was meant to
/// re-tint everything through those references. Measured, it does not: pixels sampled from one
/// window before and after a switch from Ember to Midnight showed the pane surfaces following while
/// the window ground, the sidebar, the muted text and every gradient -- rings, bars, the fan -- kept
/// Ember's colours until a restart. A brush shared by hundreds of elements has no single inheritance
/// context, so the palette's change does not reliably reach the references inside it.
///
/// So the switch repaints these brushes in place from this table (ThemeManager.Apply). Every element
/// holding one holds the same instance and follows. ThemeBrushTests holds the table to Theme.xaml
/// brush for brush, so a brush added there without a row here fails the build instead of going stale
/// on the next theme switch.
/// </summary>
public static class ThemeBrushes
{
    public static IReadOnlyList<(string Brush, string[] Colors)> All { get; } = new (string, string[])[]
    {
        ("BackgroundBrush", ["BackgroundColor"]),
        ("PanelBrush", ["PanelColor"]),
        ("PanelAltBrush", ["PanelAltColor"]),
        ("PanelHoverBrush", ["PanelHoverColor"]),
        ("BorderBrush", ["BorderColor"]),
        ("BorderStrongBrush", ["BorderStrongColor"]),
        ("TextPrimaryBrush", ["TextPrimaryColor"]),
        ("TextMutedBrush", ["TextMutedColor"]),
        ("TextFaintBrush", ["TextFaintColor"]),
        ("AccentBrush", ["AccentColor"]),
        ("OnAccentBrush", ["OnAccentColor"]),
        ("AccentBrush2", ["AccentColor2"]),
        ("AccentDimBrush", ["AccentDimColor"]),
        ("AccentSoftBrush", ["AccentSoftColor"]),
        ("WarnBrush", ["WarnColor"]),
        ("DangerBrush", ["DangerColor"]),
        ("GoodBrush", ["GoodColor"]),
        ("CardTopHighlightBrush", ["CardTopHighlightColor"]),
        ("GridLineBrush", ["GridLineColor"]),
        ("MetricCpuBrush", ["MetricCpuColor"]),
        ("MetricMemBrush", ["MetricMemColor"]),
        ("MetricGpuBrush", ["MetricGpuColor"]),
        ("MetricFanBrush", ["MetricFanColor"]),
        ("AppBackgroundGradient", ["PanelColor", "BackgroundColor"]),
        ("SidebarGradient", ["PanelColor", "BackgroundColor"]),
        ("CardGradientBrush", ["PanelAltColor", "PanelColor"]),
        ("PaneEdgeBrush", ["CardTopHighlightColor", "BorderColor"]),
        ("AccentGradientBrush", ["AccentColor", "AccentColor2"]),
        ("PressureGradientBrush", ["GoodColor", "WarnColor"]),
        ("CriticalGradientBrush", ["WarnColor", "DangerColor"]),
        ("GoodGradientBrush", ["GoodColor", "AccentColor2"]),
        ("TrackBrush", ["PanelAltColor"]),
    };
}
