using System.Windows;
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
    };

    public const string DefaultId = "Midnight";

    public static ThemeDefinition Current { get; private set; } = All[0];

    /// <summary>Raised after the palette is swapped, so windows can repaint their frames.</summary>
    public static event Action<ThemeDefinition>? ThemeChanged;

    public static ThemeDefinition Resolve(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    public static void Apply(string? id)
    {
        var theme = Resolve(id);
        var dict = new ResourceDictionary { Source = new Uri(theme.Source, UriKind.Relative) };

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
        ThemeChanged?.Invoke(theme);
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
