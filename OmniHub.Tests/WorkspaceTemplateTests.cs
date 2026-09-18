// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Telemetry;
using OmniHub.Core.Workspaces;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// The starting workspaces, checked against the catalogues they draw from.
///
/// The failure worth catching is a template naming a panel that does not exist -- it renders as a
/// placeholder saying this version does not have it, which is correct behaviour for a layout from
/// a later build and embarrassing for one this build shipped.
/// </summary>
public class WorkspaceTemplateTests
{
    /// <summary>Panels that are not a metric or a chart, which have no catalogue to check against.</summary>
    private static readonly string[] Fixed =
    {
        PanelKeys.Limits, PanelKeys.FanCurve, PanelKeys.Readings,
    };

    private static bool Resolves(string key)
    {
        if (key.StartsWith(PanelKeys.MetricPrefix, StringComparison.Ordinal))
            return Metrics.Find(key[PanelKeys.MetricPrefix.Length..]) is not null;

        if (key.StartsWith(PanelKeys.ChartPrefix, StringComparison.Ordinal))
            return ChartSubjects.Find(key[PanelKeys.ChartPrefix.Length..]) is not null;

        return Fixed.Contains(key);
    }

    [Fact]
    public void EveryTemplatePanelIsAPanelThatExists()
    {
        foreach (var template in WorkspaceTemplates.All)
            foreach (var panel in template.Panels)
                Assert.True(Resolves(panel.Type), $"{template.Name} asks for \"{panel.Type}\", which nothing builds");
    }

    [Fact]
    public void EveryTemplateHasPanelsAndAName()
    {
        foreach (var template in WorkspaceTemplates.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(template.Name));
            Assert.NotEmpty(template.Panels);
        }
    }

    [Fact]
    public void TemplateNamesAreDistinct() =>
        Assert.Equal(WorkspaceTemplates.All.Count,
                     WorkspaceTemplates.All.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());

    [Fact]
    public void EveryRowInATemplateFillsTheGridExactly()
    {
        // Panels flow and wrap, so a row that adds up to eleven leaves a column of empty space and
        // one that adds up to thirteen pushes a panel onto its own line. Either is a template that
        // looks like a mistake, because it is one.
        foreach (var template in WorkspaceTemplates.All)
        {
            int column = 0;

            foreach (var panel in template.Panels)
            {
                Assert.InRange(panel.Span, 1, WorkspaceLayout.Columns);

                column += panel.Span;
                Assert.True(column <= WorkspaceLayout.Columns,
                            $"{template.Name}: a row overflows at \"{panel.Type}\"");

                if (column == WorkspaceLayout.Columns) column = 0;
            }

            Assert.True(column == 0, $"{template.Name}: the last row leaves {WorkspaceLayout.Columns - column} columns empty");
        }
    }

    [Fact]
    public void ATemplateSurvivesBeingSavedAndReadBack()
    {
        // Templates are added to a layout and written to disk like anything else, and normalising
        // must not rename or re-space them.
        var layout = new WorkspaceLayout(WorkspaceLayout.CurrentVersion, WorkspaceTemplates.All).Normalised();

        Assert.Equal(WorkspaceTemplates.All.Count, layout.Workspaces.Count);

        for (int i = 0; i < layout.Workspaces.Count; i++)
        {
            Assert.Equal(WorkspaceTemplates.All[i].Name, layout.Workspaces[i].Name);
            Assert.Equal(WorkspaceTemplates.All[i].Panels.Count, layout.Workspaces[i].Panels.Count);
        }
    }

    [Fact]
    public void FindIsCaseInsensitiveAndMissesCleanly()
    {
        Assert.NotNull(WorkspaceTemplates.Find("gaming"));
        Assert.Null(WorkspaceTemplates.Find("something else"));
    }
}
