// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OmniHub.Core.Theming;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// The table a theme switch repaints from is the brushes Theme.xaml actually declares.
///
/// The switch repaints shared brushes in place because their DynamicResource colours measurably do
/// not follow a palette swap on their own. A brush the table does not know about is therefore a
/// brush that keeps the old theme's colour until a restart -- silent, and only visible to somebody
/// who switches theme and looks closely.
/// </summary>
public class ThemeBrushTests
{
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void EveryPaletteBoundBrushInThemeXamlIsRepaintedFromTheColoursItNames()
    {
        var declared = new Dictionary<string, string>();

        foreach (var brush in XDocument.Load(Path.Combine(WpfTestHost.WpfDir, "Theme.xaml")).Descendants()
                     .Where(e => e.Name.LocalName.EndsWith("Brush") && e.Attribute(X + "Key") is not null))
        {
            // The brush's own Color for a solid one, its stops in order for a gradient.
            var colours = brush.DescendantsAndSelf()
                .Select(e => e.Attribute("Color")?.Value ?? "")
                .Select(v => Regex.Match(v, @"\{DynamicResource\s+(\w+)\}"))
                .Where(m => m.Success)
                .Select(m => m.Groups[1].Value);

            string joined = string.Join(",", colours);
            if (joined.Length > 0) declared[brush.Attribute(X + "Key")!.Value] = joined;
        }

        var table = ThemeBrushes.All.ToDictionary(r => r.Brush, r => string.Join(",", r.Colors));

        Assert.Equal(declared.OrderBy(p => p.Key), table.OrderBy(p => p.Key));
    }
}
