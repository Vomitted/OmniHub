// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Theming;

/// <summary>
/// A palette somebody built, described by the few colours worth choosing.
///
/// Four colours and a corner radius, not thirty. A palette file has thirty keys, and asking anyone
/// to pick thirty colours is not a feature, it is a chore that ends in an unreadable theme -- which
/// is what the audit over the shipped palettes exists to prevent, so building an editor that
/// produces them would be an odd way to spend the effort.
///
/// The rest are derived, and derived deliberately rather than guessed: the muted and faint text are
/// the chosen text fading toward the chosen ground, the borders are the panel moving toward the
/// text, and the status colours are nudged until they are readable on the ground somebody actually
/// picked instead of being fixed values that happen to work on a dark one.
/// </summary>
public sealed record CustomPalette(
    string Name,
    Rgb Background,
    Rgb Panel,
    Rgb Accent,
    Rgb TextPrimary,
    int RadiusMd = 10)
{
    // Canonical status hues, before they are made readable on whatever ground was chosen.
    private static readonly Rgb GoodSeed = new(0x3F, 0xCF, 0x8E);
    private static readonly Rgb WarnSeed = new(0xE8, 0xB3, 0x39);
    private static readonly Rgb DangerSeed = new(0xE5, 0x5B, 0x5B);

    private static readonly Rgb CpuSeed = new(0x4F, 0x9C, 0xF5);
    private static readonly Rgb MemSeed = new(0xA9, 0x7B, 0xEF);
    private static readonly Rgb GpuSeed = new(0x3F, 0xC2, 0xC9);

    /// <summary>Every colour key a palette file defines, as the hex a palette file writes.</summary>
    public IReadOnlyDictionary<string, string> Build()
    {
        var text = TextPrimary;
        var ground = Background;

        return new Dictionary<string, string>
        {
            ["BackgroundColor"] = ground.ToString(),
            ["PanelColor"] = Panel.ToString(),
            ["PanelAltColor"] = Mix(Panel, text, 0.05).ToString(),
            ["PanelHoverColor"] = Mix(Panel, text, 0.10).ToString(),

            ["BorderColor"] = Mix(Panel, text, 0.14).ToString(),
            ["BorderStrongColor"] = Mix(Panel, text, 0.26).ToString(),

            ["TextPrimaryColor"] = text.ToString(),

            // Measured, not chosen. The eight palettes built by hand fade their muted text 0.32 to
            // 0.40 of the way to the ground and their faint text 0.34 to 0.42, and all eight pass
            // the audit. A first guess of 0.56 for the faint text looked reasonable and scored
            // 3.88 against a threshold of 4.5, which would have made the editor refuse every
            // palette anybody built -- including the shipped default expressed in its own terms.
            //
            // The most conservative of the eight rather than the average, because all eight are
            // dark: the lightest ground among them has a luminance of 0.006. The same fade behaves
            // differently on a light ground, where the surfaces step down toward the text instead
            // of up, and only the cautious end of the measured range survives both.
            //
            // They also sit closer together than the names suggest, which is what the working
            // palettes do. Pushing faint further is precisely what makes a footnote unreadable.
            ["TextMutedColor"] = Mix(text, ground, 0.32).ToString(),
            ["TextFaintColor"] = Mix(text, ground, 0.34).ToString(),

            ["AccentColor"] = Accent.ToString(),
            ["AccentDimColor"] = Mix(Accent, ground, 0.45).ToString(),
            ["AccentSoftColor"] = Mix(Accent, ground, 0.78).ToString(),

            // Whichever of near-black and near-white reads better ON the accent, decided by the
            // same rule everything else is measured with rather than assumed to be white. All
            // eight shipped palettes answer near-black here, because every accent in them is
            // bright -- and two controls were painting themselves white on it until recently.
            ["OnAccentColor"] = OnAccent(Accent).ToString(),

            // Moved until they pass on the chosen ground. Fixed values would be a guard that only
            // works on the dark palettes they were picked against, and the point of letting
            // somebody choose a ground is that they might not choose a dark one.
            ["GoodColor"] = Readable(GoodSeed, ground).ToString(),
            ["WarnColor"] = Readable(WarnSeed, ground).ToString(),
            ["DangerColor"] = Readable(DangerSeed, ground).ToString(),

            ["MetricCpuColor"] = Readable(CpuSeed, ground).ToString(),
            ["MetricMemColor"] = Readable(MemSeed, ground).ToString(),
            ["MetricGpuColor"] = Readable(GpuSeed, ground).ToString(),

            ["CardTopHighlightColor"] = Mix(Panel, text, 0.08).ToString(),
            ["GridLineColor"] = Mix(Panel, text, 0.12).ToString(),

            ["CaptionColor"] = ground.ToString(),
            ["CaptionTextColor"] = text.ToString(),
        };
    }

    /// <summary>
    /// Whatever about this palette would be unreadable, named.
    ///
    /// Empty means it can be saved. Each entry names the two colours, what they scored and what
    /// they needed, because "this palette is unreadable" tells somebody to give up and naming the
    /// pair tells them which one to change.
    ///
    /// Measured with the same rule and the same threshold as the audit over the shipped palettes,
    /// so a palette built here is held to exactly what the eight built by hand are held to.
    /// </summary>
    public IReadOnlyList<string> Problems()
    {
        var built = Build();
        var problems = new List<string>();

        Rgb Of(string key) => Rgb.Parse(built[key]) ?? Background;

        // Only the pairs that actually meet on screen. Checking every colour against every surface
        // would refuse palettes over combinations nothing ever draws.
        foreach (string surfaceKey in new[] { "BackgroundColor", "PanelColor", "PanelAltColor" })
        {
            var surface = Of(surfaceKey);

            foreach (string textKey in new[] { "TextPrimaryColor", "TextMutedColor", "TextFaintColor" })
            {
                var foreground = Of(textKey);

                if (!Contrast.IsReadable(foreground, surface))
                    problems.Add(Contrast.Describe(textKey, foreground, surfaceKey, surface));
            }
        }

        var accent = Of("AccentColor");
        var onAccent = Of("OnAccentColor");

        if (!Contrast.IsReadable(onAccent, accent))
            problems.Add(Contrast.Describe("OnAccentColor", onAccent, "AccentColor", accent));

        return problems;
    }

    /// <summary>Whether this palette can be saved at all.</summary>
    public bool IsReadable => Problems().Count == 0;

    /// <summary>
    /// One colour a fraction of the way toward another.
    ///
    /// Plain channel interpolation. Not perceptually uniform, which matters for a gradient and does
    /// not matter here: these are single steps between two colours somebody already chose, and
    /// every result is measured afterwards rather than trusted.
    /// </summary>
    internal static Rgb Mix(Rgb from, Rgb to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);

        static byte Step(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);

        return new Rgb(Step(from.R, to.R, amount), Step(from.G, to.G, amount), Step(from.B, to.B, amount));
    }

    /// <summary>
    /// The colour moved toward white or black until it is readable on a surface.
    ///
    /// Direction chosen by which side the surface sits on: a colour on a dark ground has to get
    /// lighter, one on a light ground darker. Stepped rather than solved, because the contrast
    /// curve is not linear in channel space and twenty steps lands close enough for a colour
    /// nobody specified exactly.
    ///
    /// Returns its best effort if it never passes, and the caller reports that rather than this
    /// pretending otherwise. A seed that cannot be made readable on a given ground is a real
    /// answer, and hiding it would be the guard doing nothing while looking like a guard.
    /// </summary>
    internal static Rgb Readable(Rgb colour, Rgb on)
    {
        if (Contrast.IsReadable(colour, on)) return colour;

        var target = Contrast.RelativeLuminance(on) > 0.5 ? new Rgb(0, 0, 0) : new Rgb(255, 255, 255);
        var best = colour;

        for (int step = 1; step <= 20; step++)
        {
            var candidate = Mix(colour, target, step / 20.0);
            best = candidate;

            if (Contrast.IsReadable(candidate, on)) return candidate;
        }

        return best;
    }

    /// <summary>Near-black or near-white, whichever reads better on the accent.</summary>
    internal static Rgb OnAccent(Rgb accent)
    {
        var dark = new Rgb(0x0B, 0x0B, 0x0B);
        var light = new Rgb(0xF5, 0xF5, 0xF5);

        return Contrast.Ratio(dark, accent) >= Contrast.Ratio(light, accent) ? dark : light;
    }
}
