// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Theming;

/// <summary>
/// The materials a look sets over the palette (TECHNICAL-v5-ui.md, section 13): the ground, the panel
/// and border tones, the corners. The palette keeps the component colours and the text; these decide
/// what everything stands on, so a look is the same stuff on every page and not only on its Dashboard.
///
/// Data here rather than in the application so the one thing that can go wrong with it -- text that
/// stops being readable on a translucent panel -- is held by a test.
/// </summary>
public static class LookSurfaces
{
    /// <summary>
    /// One look's materials. Colours are #AARRGGBB; a translucent panel is drawn over
    /// <see cref="Ground"/>, or with the blur on, over whatever the acrylic shows.
    /// </summary>
    /// <param name="BlurPanel">
    /// The opaque tone a panel is made from when the blur is on, drawn at <see cref="PanelOpacity"/>.
    /// Frosted's white glass cannot be it: white over a light acrylic takes the faintest text below
    /// 4.5 : 1, so under the blur its panels are a warm dark glass instead.
    /// </param>
    /// <param name="TextPrimary">
    /// The look's own text, or null to keep the palette's. Frosted sets its own: on white glass over a
    /// lit room the palettes' faint greys fell to 2.1 : 1, and the reference sets its type in warm
    /// whites for exactly that reason.
    /// </param>
    public sealed record Material(
        string Ground, string Panel, string PanelAlt, string PanelHover, string Border, string BorderStrong,
        string BlurPanel, int Radius, int RadiusSmall, double TrackHeight, string? Accent,
        string? TextPrimary = null, string? TextMuted = null, string? TextFaint = null, string? OnAccent = null);

    /// <summary>After "Smart Home Dashboard — Designed for Modern Living": white glass over a warm room.</summary>
    public static readonly Material Frosted = new(
        Ground: "#FF1C1A17", Panel: "#12FFFFFF", PanelAlt: "#1AFFFFFF", PanelHover: "#21FFFFFF",
        Border: "#26FFFFFF", BorderStrong: "#40FFFFFF", BlurPanel: "#FF1E1B18",
        Radius: 26, RadiusSmall: 14, TrackHeight: 8, Accent: null,
        TextPrimary: "#FFF6F4F1", TextMuted: "#FFE6E2DC", TextFaint: "#FFD2CDC6");

    /// <summary>
    /// After "New Nothing OS 4.0 Concept": greys, one red. Its own greys for text too: a palette's
    /// tinted whites beside one pure red read as a second accent, and a light palette someone built
    /// would put dark text on these tiles.
    /// </summary>
    public static readonly Material DotMatrix = new(
        Ground: "#FF0A0A0A", Panel: "#FF1A1A1A", PanelAlt: "#FF222222", PanelHover: "#FF2A2A2A",
        Border: "#FF262626", BorderStrong: "#FF333333", BlurPanel: "#FF1A1A1A",
        Radius: 30, RadiusSmall: 18, TrackHeight: 8, Accent: "#FFFF6166", OnAccent: "#FF000000",
        TextPrimary: "#FFF2F2F2", TextMuted: "#FFBDBDBD", TextFaint: "#FF9A9A9A");

    /// <summary>
    /// Dot matrix's red at full strength, for what it draws -- a live dot, the newest column of the
    /// chart, the hottest dot of a bar -- and never for text: 3.9 : 1 on a tile is over the 3 : 1 a
    /// graphic needs and under the 4.5 text does, which is why the look's accent is a lighter red.
    /// </summary>
    public const string Signal = "#FFE8262C";

    /// <summary>
    /// After "Tesla Smart App" by Gavrisov Dmitri: a charcoal ground, surfaces a shade lighter that the
    /// window raises with a gradient edge rather than a blurred shadow, the reference's blue.
    /// </summary>
    public static readonly Material Neumorphic = new(
        Ground: "#FF1C1E21", Panel: "#FF23262A", PanelAlt: "#FF282B30", PanelHover: "#FF2E3137",
        Border: "#FF30343A", BorderStrong: "#FF3B4047", BlurPanel: "#FF202327",
        Radius: 32, RadiusSmall: 18, TrackHeight: 8, Accent: "#FF3AA0FF",
        TextPrimary: "#FFEEF1F5", TextMuted: "#FFA7ADB6", TextFaint: "#FF8E959F", OnAccent: "#FF000000");

    /// <summary>After the "Whoop app redesign concept" by Taras Migulko: pure black, near-black cards.</summary>
    public static readonly Material Athletic = new(
        Ground: "#FF000000", Panel: "#FF141414", PanelAlt: "#FF1B1B1B", PanelHover: "#FF232323",
        Border: "#FF1C1C1C", BorderStrong: "#FF2C2C2C", BlurPanel: "#FF141414",
        Radius: 18, RadiusSmall: 12, TrackHeight: 10, Accent: null,
        TextPrimary: "#FFFFFFFF", TextMuted: "#FFA0A0A0", TextFaint: "#FF8E8E8E");

    /// <summary>After "Tesla Concept" by yuhang: black, iOS's grouped greys, a legible system blue.</summary>
    public static readonly Material Companion = new(
        Ground: "#FF000000", Panel: "#FF141416", PanelAlt: "#FF1C1C1E", PanelHover: "#FF252528",
        Border: "#FF1C1C1E", BorderStrong: "#FF2C2C2E", BlurPanel: "#FF141416",
        Radius: 22, RadiusSmall: 12, TrackHeight: 8, Accent: "#FF409CFF",
        TextPrimary: "#FFFFFFFF", TextMuted: "#FFAEAEB2", TextFaint: "#FF8E8E93", OnAccent: "#FF000000");

    /// <summary>Every look with its own materials, for the tests that hold them.</summary>
    public static readonly Material[] All = { Frosted, DotMatrix, Neumorphic, Athletic, Companion };

    /// <summary>
    /// The glass's strength when nobody has chosen one: 70 % of the ground and 90 % of each panel left
    /// over the material, about where section 13's fixed figures were.
    /// </summary>
    public const double DefaultBlurStrength = 0.30;

    /// <summary>
    /// With the blur on, how much of the ground is left over the material, from a faint frost at
    /// strength 0 to nearly clear glass at 1. The lift in <see cref="Overrides"/> keeps every text
    /// colour readable across the whole range (TECHNICAL-v5-ui.md, section 14).
    /// </summary>
    public static double GroundOpacity(double strength) => 0.92 - 0.72 * Math.Clamp(strength, 0, 1);

    /// <summary>
    /// How opaque every panel is at a strength: always more than the ground, so a tile stays a tile, and
    /// never under 75 %, where Dot matrix's drawn red would fall below the 3 : 1 a graphic needs.
    /// </summary>
    public static double PanelOpacity(double strength) => 0.96 - 0.21 * Math.Clamp(strength, 0, 1);

    /// <summary>
    /// The darkest and the lightest a dark-mode acrylic settles at behind the window: over a pure white
    /// window it measured #929292 on this machine (TECHNICAL-v5-ui.md, section 16).
    /// </summary>
    public static readonly Rgb[] AcrylicBackdrops = { new(0x00, 0x00, 0x00), new(0x92, 0x92, 0x92) };

    /// <summary>
    /// The same for Mica and Mica Alt, which sample the wallpaper rather than the windows: #1F2024 at
    /// most on this machine's wallpaper, taken as #303030 to leave room for a brighter one.
    /// </summary>
    public static readonly Rgb[] MicaBackdrops = { new(0x00, 0x00, 0x00), new(0x30, 0x30, 0x30) };

    /// <summary>What a material -- Windows' backdrop type, 2 Mica, 3 Acrylic, 4 Mica Alt -- can put behind the glass.</summary>
    public static Rgb[] Backdrop(int material) => material is 2 or 4 ? MicaBackdrops : AcrylicBackdrops;

    /// <summary>The frosted room's own ground, darkest and lightest, for Frosted with the blur off.</summary>
    public static readonly Rgb[] FrostedRoom = { new(0x16, 0x14, 0x12), new(0x26, 0x22, 0x1E) };

    /// <summary>The palette keys text is drawn on, in the order the palette tests name them.</summary>
    public static readonly string[] SurfaceKeys = { "BackgroundColor", "PanelColor", "PanelAltColor", "PanelHoverColor" };

    /// <summary>
    /// Every colour the application draws as text somewhere. The metric and status colours are here
    /// because they genuinely are set as Foreground, not only as fills (ContrastTests says where).
    /// </summary>
    public static readonly string[] TextKeys =
    {
        "TextPrimaryColor", "TextMutedColor", "TextFaintColor",
        "AccentColor", "GoodColor", "WarnColor", "DangerColor",
        "MetricCpuColor", "MetricMemColor", "MetricGpuColor", "MetricFanColor",
    };

    /// <summary>
    /// Every colour a look and the blur set over the palette, as key and #AARRGGBB. The application
    /// applies exactly this, and the tests read exactly this, so the two cannot drift apart.
    /// <paramref name="palette"/> gives the palette's own colour for a key.
    ///
    /// A look keeps the palette's colours for everything it does not set itself, but its glass is
    /// lighter than any palette's panel, and a palette's red that reads at 5 : 1 on near-black falls
    /// to 3.5 on it. So each text colour is lifted toward white, in the steps a custom palette uses,
    /// until it reads on the lightest surface the setting can put under it -- the palette's hue, as
    /// little changed as the glass allows.
    /// </summary>
    public static Dictionary<string, string> Overrides(Material? look, double? blur, Func<string, string> palette, int material = 3)
    {
        var solid = Build(look, null, palette, material);
        if (blur is not { } s) return solid;

        // The strength spans only as clear as the colours allow unchanged on this material, and the glass
        // starts from the colours as they are without it -- so the slider never moves one (section 16).
        var o = Build(look, s * Clarity(look, material, palette), palette, material, keep: solid);
        foreach (var (key, value) in solid) o.TryAdd(key, value);
        return o;
    }

    /// <summary>
    /// The clearest the glass can go, 0 to 1 of the strength scale, with every text colour still reading
    /// unchanged on the lightest surface the material can put under it -- so turning the glass up never
    /// moves a colour.
    /// </summary>
    public static double Clarity(Material? look, int material, Func<string, string> palette)
    {
        var baseline = Build(look, null, palette, material);
        string Base(string key) => baseline.TryGetValue(key, out var v) ? v : palette(key);

        bool Fits(double t)
        {
            var o = new Dictionary<string, string>(baseline);
            Glass(o, look, t, palette);
            string Get(string key) => o.TryGetValue(key, out var v) ? v : palette(key);
            var lightest = Surfaces(look, t, Get, material).MaxBy(s => Contrast.RelativeLuminance(s.Colour)).Colour;
            return TextKeys.All(k => { var c = Rgb.Parse(Base(k))!.Value; return CustomPalette.Readable(c, lightest) == c; });
        }

        // Scanned up from the frost rather than bisected: the ground lightens as the glass clears but the
        // panels, less opaque, darken, so the lightest surface is not monotonic in the strength.
        const int Steps = 40;
        for (int i = 0; i <= Steps; i++)
            if (!Fits(i / (double)Steps)) return Math.Max(0, i - 1) / (double)Steps;
        return 1;
    }

    /// <summary>Text colours, and the accent pair's partners, that the glass must leave as they are without it.</summary>
    private static readonly string[] Kept = TextKeys.Append("AccentColor2").Append("OnAccentColor").ToArray();

    private static Dictionary<string, string> Build(Material? look, double? blur, Func<string, string> palette, int material,
        Dictionary<string, string>? keep = null)
    {
        var o = new Dictionary<string, string>();
        if (look is null && blur is null) return o;   // the palette, untouched

        if (look is not null)
        {
            o["BackgroundColor"] = look.Ground;
            o["PanelColor"] = look.Panel;
            o["PanelAltColor"] = look.PanelAlt;
            o["PanelHoverColor"] = look.PanelHover;
            o["BorderColor"] = look.Border;
            o["BorderStrongColor"] = look.BorderStrong;
            o["CaptionColor"] = look.Ground;
            if (look.TextPrimary is { } primary) o["TextPrimaryColor"] = primary;
            if (look.TextMuted is { } muted) o["TextMutedColor"] = muted;
            if (look.TextFaint is { } faint) o["TextFaintColor"] = faint;
            if (look.Accent is { } accent)
            {
                // The palettes' own ratios: the dim tone at 0x38, the soft at 0x1E.
                o["AccentColor"] = accent;
                o["AccentColor2"] = accent;
                o["AccentDimColor"] = WithOpacity(accent, 0x38 / 255.0);
                o["AccentSoftColor"] = WithOpacity(accent, 0x1E / 255.0);
                o["OnAccentColor"] = look.OnAccent ?? "#FF000000";
            }
        }

        if (blur is { } strength) Glass(o, look, strength, palette);
        if (keep is not null)
            foreach (string key in Kept)
                if (keep.TryGetValue(key, out var kept)) o[key] = kept;

        string Get(string key) => o.TryGetValue(key, out var v) ? v : palette(key);
        var lightest = Surfaces(look, blur, Get, material).MaxBy(s => Contrast.RelativeLuminance(s.Colour)).Colour;

        bool accentLifted = false;
        foreach (string key in TextKeys)
        {
            var colour = Rgb.Parse(Get(key))!.Value;
            var lifted = CustomPalette.Readable(colour, lightest);
            if (lifted == colour) continue;
            o[key] = Opaque(lifted);
            accentLifted |= key == "AccentColor";
        }

        // A lifted accent takes its gradient partner with it, and the text on the pair is chosen again.
        if (accentLifted && look?.Accent is null)
        {
            o["AccentColor2"] = Opaque(CustomPalette.Readable(Rgb.Parse(palette("AccentColor2"))!.Value, lightest));
            o["OnAccentColor"] = Opaque(CustomPalette.OnAccent(Rgb.Parse(o["AccentColor"])!.Value));
        }

        return o;
    }

    /// <summary>The ground and every panel made translucent for a strength, over what a look has set.</summary>
    private static void Glass(Dictionary<string, string> o, Material? look, double strength, Func<string, string> palette)
    {
        o["BackgroundColor"] = WithOpacity(o.GetValueOrDefault("BackgroundColor") ?? palette("BackgroundColor"), GroundOpacity(strength));
        foreach (string key in SurfaceKeys.Skip(1))
        {
            // A look's lighter tones made solid over its dark glass first, so a raised panel is
            // still raised; a palette's own tones as they are.
            string solid = look is null
                ? palette(key)
                : Over(o[key], Rgb.Parse(look.BlurPanel)!.Value).ToString();
            o[key] = WithOpacity(key == "PanelColor" && look is not null ? look.BlurPanel : solid, PanelOpacity(strength));
        }
    }

    /// <summary>
    /// Every surface text can land on under a setting: the ground over whatever is beneath it, the room
    /// itself where it shows, each panel tone over each of those, and the active chips' soft accent over
    /// the ground and the two panel tones -- the palette tests' surfaces, with the look's materials.
    /// </summary>
    public static List<(string Name, Rgb Colour)> Surfaces(Material? look, double? blur, Func<string, string> get, int material = 3)
    {
        var beneath = Beneath(look, blur, material);
        var grounds = beneath.Length == 0
            ? new List<Rgb> { Rgb.Parse(get("BackgroundColor"))!.Value }
            : beneath.Select(b => Over(get("BackgroundColor"), b)).Concat(blur is not null ? Array.Empty<Rgb>() : beneath).ToList();

        var surfaces = grounds.Select(g => ("the ground", g)).ToList();
        foreach (var ground in grounds)
            foreach (string key in SurfaceKeys.Skip(1))
                surfaces.Add((key, Over(get(key), ground)));

        foreach (var (name, under) in surfaces.Where(s => s.Item1 is "the ground" or "PanelColor" or "PanelAltColor").ToList())
            surfaces.Add(($"AccentSoft over {name}", Over(get("AccentSoftColor"), under)));

        return surfaces;
    }

    /// <summary>
    /// What the window's ground is drawn over: the acrylic's extremes under the blur, Frosted's room
    /// without it, and nothing -- the ground is opaque -- otherwise.
    /// </summary>
    public static Rgb[] Beneath(Material? look, double? blur, int material = 3) =>
        blur is not null ? Backdrop(material) : ReferenceEquals(look, Frosted) ? FrostedRoom : Array.Empty<Rgb>();

    private static string Opaque(Rgb c) => $"#FF{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>A translucent #AARRGGBB colour composited over an opaque one.</summary>
    public static Rgb Over(string argb, Rgb under, double? opacity = null)
    {
        string s = argb.TrimStart('#');
        double a = opacity ?? Convert.ToByte(s[..2], 16) / 255.0;
        var c = Rgb.Parse(argb)!.Value;
        byte Mix(byte f, byte b) => (byte)Math.Round(f * a + b * (1 - a));
        return new Rgb(Mix(c.R, under.R), Mix(c.G, under.G), Mix(c.B, under.B));
    }

    /// <summary>The same colour at another opacity, as #AARRGGBB.</summary>
    public static string WithOpacity(string argb, double opacity)
    {
        var c = Rgb.Parse(argb)!.Value;
        return $"#{(byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255):X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
