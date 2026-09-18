// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using OmniHub.Core.Optimize;
using OmniHub.Core.Theming;
using System.Windows.Media;
using Application = System.Windows.Application;
using Window = System.Windows.Window;

namespace OmniHub.App.Wpf;

public sealed record ThemeDefinition(string Id, string DisplayName, string Description, string Source);

/// <summary>
/// Swaps the active colour palette at runtime.
///
/// How this works: every brush in Theme.xaml binds its Color with {DynamicResource}, not
/// {StaticResource}. A StaticResource is resolved once when the dictionary loads and then
/// baked in, which is why a single-file theme cannot be changed without restarting.
/// DynamicResource keeps the lookup live, so replacing the palette dictionary re-tints every
/// brush -- and therefore every control bound to those brushes -- in place, with no rebuild
/// of the visual tree and no restart.
///
/// Palettes deliberately contain ONLY colours. Fonts, radii and the brush definitions stay
/// in Theme.xaml, so adding a theme is a short list of colour values and cannot accidentally
/// redefine a control's geometry.
/// </summary>
public static class ThemeManager
{
    // A palette carries shape and density as well as colour: RadiusSm, RadiusMd and
    // CardPadding live in the palette files, so switching theme changes how sharp the
    // corners are and how much air a card has, not just its hue. Two themes that differ
    // only in accent are two themes nobody can tell apart in the picker, which is exactly
    // what happened when OLED Black and Midnight were given the same ramp.
    //
    // The descriptions name the shape deliberately, because that is the part a colour
    // swatch cannot show.
    public static readonly IReadOnlyList<ThemeDefinition> All = new[]
    {
        new ThemeDefinition("Midnight", "Midnight", "Blue-tinted black, rounded and roomy.",
            "Wpf/Palettes/Midnight.xaml"),
        new ThemeDefinition("OledBlack", "OLED Black", "True #000000, sharp and tight. Pixels off on an OLED panel.",
            "Wpf/Palettes/OledBlack.xaml"),
        new ThemeDefinition("Graphite", "Graphite", "Neutral grey, steel accent. Readings are the only colour.",
            "Wpf/Palettes/Graphite.xaml"),
        new ThemeDefinition("Ember", "Ember", "Warm black, heat orange, softer corners.",
            "Wpf/Palettes/Ember.xaml"),
        new ThemeDefinition("Nord", "Nord", "Cool slate and frost. The calmest and airiest.",
            "Wpf/Palettes/Nord.xaml"),
        new ThemeDefinition("Terminal", "Terminal", "Green phosphor, square corners, console density.",
            "Wpf/Palettes/Terminal.xaml"),
        new ThemeDefinition("Sandstone", "Sandstone", "Warm grey and gold. Softest corners, most air.",
            "Wpf/Palettes/Sandstone.xaml"),
        new ThemeDefinition("Mono", "Mono", "Greyscale, fully square, dense. A reading is the only hue.",
            "Wpf/Palettes/Mono.xaml"),

        // Last, and only meaningful once somebody has built one. Its source is never read: a
        // custom palette is constructed rather than loaded, which is why the path is empty rather
        // than pointing at a file that does not exist.
        new ThemeDefinition(CustomId, "Custom", "Yours. Four colours, the rest derived and checked.", ""),
    };

    /// <summary>The id a palette built in the application takes.</summary>
    public const string CustomId = "Custom";

    public const string DefaultId = "Midnight";

    public static ThemeDefinition Current { get; private set; } = All[0];

    /// <summary>Raised after the palette is swapped, so windows can repaint their frames.</summary>
    public static event Action<ThemeDefinition>? ThemeChanged;

    public static ThemeDefinition Resolve(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>
    /// The palette somebody built, when they have built one.
    ///
    /// Held rather than written to a file. A palette shipped with the application is a compiled
    /// resource, and a user palette cannot be one, so the alternative was writing XAML to disk and
    /// parsing it back -- a file format, a parse failure to handle and a second way for a palette
    /// to be malformed, in exchange for nothing. Its four colours live in settings.json and the
    /// dictionary is built from them.
    /// </summary>
    public static CustomPalette? Custom { get; private set; }

    /// <summary>
    /// Remembers a built palette without switching to it.
    ///
    /// Separate from applying because startup has to restore the palette before it knows which
    /// theme is selected: a saved theme of "Custom" with nothing to resolve to would fall back to
    /// the default, and the user would find their theme quietly reset on every launch.
    /// </summary>
    public static void SetCustom(CustomPalette palette) => Custom = palette;

    /// <summary>Applies a palette built in the application, and remembers it as the current one.</summary>
    public static void ApplyCustom(CustomPalette palette)
    {
        SetCustom(palette);
        Apply(CustomId);
    }

    public static void Apply(string? id)
    {
        var theme = Resolve(id);

        // A custom palette has no file behind it. Everything downstream -- the insert-before-remove
        // swap, the density pass, the window frame -- works on the dictionary rather than on where
        // it came from, so this is the only place that has to know the difference.
        var dict = string.Equals(id, CustomId, StringComparison.OrdinalIgnoreCase) && Custom is { } custom
            ? BuildCustomDictionary(custom)
            : new ResourceDictionary { Source = new Uri(theme.Source, UriKind.Relative) };

        var merged = Application.Current.Resources.MergedDictionaries;

        // Identify the outgoing palette by a key only palettes define, rather than by index.
        // Index-based removal breaks as soon as anything else is merged in, and silently
        // leaving two palettes merged means the last one wins in ways that are hard to trace.
        var existing = merged.FirstOrDefault(d => d.Contains("CardTopHighlightColor"));

        // Insert before removing: with DynamicResource, a moment where no dictionary supplies
        // the colour keys would resolve to nothing and flash the WPF defaults.
        merged.Insert(0, dict);
        if (existing is not null) merged.Remove(existing);

        Current = theme;

        // Re-derived from the new palette, not carried over. Density is a multiplier on the
        // palette's own figures, so the numbers it produces are only valid for the palette they
        // were computed from -- carrying Sandstone's scaled padding onto Terminal would be a
        // fourth density nobody picked.
        ApplyDensity(CurrentDensity);

        ThemeChanged?.Invoke(theme);
    }

    /// <summary>The density in force. Normal until something says otherwise.</summary>
    public static UiDensity CurrentDensity { get; private set; } = UiDensity.Normal;

    private static ResourceDictionary? _densityOverride;

    /// <summary>
    /// Scales the palette's spacing and figure size.
    ///
    /// Applied as a dictionary merged after everything else rather than by editing the palette,
    /// so the palette stays the palette: switching theme re-reads its own numbers and applies the
    /// multiplier again, and switching density back to Normal removes the override entirely
    /// rather than trying to undo an arithmetic operation.
    /// </summary>
    public static void ApplyDensity(UiDensity density)
    {
        CurrentDensity = density;

        var merged = Application.Current?.Resources.MergedDictionaries;
        if (merged is null) return;

        if (_densityOverride is not null) merged.Remove(_densityOverride);
        _densityOverride = null;

        if (density == UiDensity.Normal) return;   // the palette, untouched

        // The palette's own figures, read from the palette rather than from the live resources --
        // which may still hold a previous override and would compound.
        var palette = merged.FirstOrDefault(d => d.Contains("CardTopHighlightColor"));
        if (palette is null) return;

        var scaled = new ResourceDictionary();

        if (palette["CardPadding"] is Thickness padding)
        {
            scaled["CardPadding"] = new Thickness(
                Density.Padding(padding.Left, density), Density.Padding(padding.Top, density),
                Density.Padding(padding.Right, density), Density.Padding(padding.Bottom, density));
        }

        if (palette["TrackHeight"] is double track)
            scaled["TrackHeight"] = Density.TrackHeight(track, density);

        if (palette["MetricValueSize"] is double figure)
            scaled["MetricValueSize"] = Density.FontSize(figure, density);

        if (scaled.Count == 0) return;

        // Last wins for a duplicate key, which is the whole mechanism.
        merged.Add(scaled);
        _densityOverride = scaled;
    }

    /// <summary>
    /// Turns a built palette into the dictionary the rest of the application expects.
    ///
    /// The shape keys come from the palette too, because a palette here carries shape as well as
    /// colour -- that is what stops two themes being two names for one look. Everything except the
    /// corner radius is taken from the default palette's values rather than invented: somebody
    /// picking four colours has not expressed an opinion about track height.
    /// </summary>
    private static ResourceDictionary BuildCustomDictionary(CustomPalette palette)
    {
        var dict = new ResourceDictionary();

        foreach (var (key, hex) in palette.Build())
        {
            // Through the Core parser rather than a WPF converter. It is the same code that reads
            // the hex somebody typed into the editor, so a value that survived validation cannot
            // fail here -- and it sidesteps ColorConverter, which is ambiguous in a project that
            // references both WPF and WinForms.
            if (Rgb.Parse(hex) is { } c) dict[key] = Color.FromRgb(c.R, c.G, c.B);
        }

        int radius = Math.Clamp(palette.RadiusMd, 0, 24);

        dict["RadiusMd"] = new CornerRadius(radius);
        dict["RadiusSm"] = new CornerRadius(Math.Max(0, radius - 1));

        // Not offered in the editor, so they match the default rather than being invented.
        dict["CardPadding"] = new Thickness(14);
        dict["HeadingFont"] = new FontFamily("Cascadia Mono, Consolas, Courier New");
        dict["TrackHeight"] = 5.0;
        dict["MetricValueSize"] = 30.0;

        return dict;
    }

    /// <summary>Repaints a window's OS-drawn frame to match the active palette.</summary>
    public static void ApplyToWindowFrame(Window window)
    {
        var caption = LookupColor("CaptionColor", Colors.Black);
        var text = LookupColor("CaptionTextColor", Colors.White);
        var border = LookupColor("BorderColor", Colors.Black);
        DwmTheme.Apply(window, caption, text, border);
    }

    private static Color LookupColor(string key, Color fallback) =>
        Application.Current.TryFindResource(key) is Color c ? c : fallback;
}
