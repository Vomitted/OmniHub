// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.IO;
using OmniHub.Core.Workspaces;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// The layout model, which is the one part of the new shell that can be asserted.
///
/// Most of these are about what must not happen rather than what must. This file decides whether
/// there is a window at all, on a machine that has recorded nine unexpected shutdowns in six days,
/// so every way it can be malformed has to end somewhere usable.
/// </summary>
public class WorkspaceLayoutTests
{
    private static string NewFile() =>
        Path.Combine(
            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "omnihub-ws-" + Guid.NewGuid().ToString("N"))).FullName,
            "workspaces.json");

    private static void Cleanup(string file)
    {
        try { Directory.Delete(Path.GetDirectoryName(file)!, true); } catch { }
    }

    /// <summary>
    /// The compatibility promise: the shipped layout is the seven screens this application had.
    ///
    /// Asserted by key rather than by count, because the failure worth catching is a screen
    /// quietly going missing in the move, not the number changing.
    /// </summary>
    [Fact]
    public void TheDefaultsReproduceTheTabsThisApplicationHad()
    {
        var types = WorkspaceLayout.Defaults().Workspaces.SelectMany(w => w.Panels).Select(p => p.Type).ToArray();

        Assert.Equal(
            new[] { "dashboard", "fans", "performance", "power", "system", "diagnostics", "settings" },
            types);
    }

    [Fact]
    public void ALayoutSurvivesTheRoundTrip()
    {
        string file = NewFile();
        try
        {
            var original = new WorkspaceLayout(WorkspaceLayout.CurrentVersion, new[]
            {
                new Workspace("Gaming", new[]
                {
                    new PanelPlacement("dashboard", Span: 8),
                    new PanelPlacement("fans", Span: 4),
                }),
                new Workspace("Quiet", new[] { new PanelPlacement("settings") }),
            });

            original.Save(file);
            var read = WorkspaceLayout.Load(file);

            Assert.Equal(2, read.Workspaces.Count);
            Assert.Equal("Gaming", read.Workspaces[0].Name);
            Assert.Equal(8, read.Workspaces[0].Panels[0].Span);
            Assert.Equal(4, read.Workspaces[0].Panels[1].Span);
            Assert.Equal("settings", read.Workspaces[1].Panels[0].Type);
        }
        finally { Cleanup(file); }
    }

    /// <summary>
    /// A panel type this build does not know is kept, not dropped.
    ///
    /// The window renders it as a named placeholder. Dropping it here instead would mean that
    /// opening a layout in an older build and closing it destroyed the arrangement -- silently,
    /// and permanently, because the save writes back what the load produced.
    /// </summary>
    [Fact]
    public void AnUnknownPanelTypeSurvivesBeingReadAndWrittenBack()
    {
        string file = NewFile();
        try
        {
            File.WriteAllText(file,
                """
                { "version": 1, "workspaces": [
                    { "Name": "Mine", "Panels": [ { "Type": "something-from-a-later-build", "Span": 6 } ] } ] }
                """);

            var read = WorkspaceLayout.Load(file);
            Assert.Equal("something-from-a-later-build", read.Workspaces[0].Panels[0].Type);

            read.Save(file);
            Assert.Equal("something-from-a-later-build", WorkspaceLayout.Load(file).Workspaces[0].Panels[0].Type);
        }
        finally { Cleanup(file); }
    }

    /// <summary>A file written by a later build opens rather than being refused.</summary>
    [Fact]
    public void ALayoutFromALaterVersionStillOpens()
    {
        string file = NewFile();
        try
        {
            File.WriteAllText(file,
                """{ "version": 99, "workspaces": [ { "Name": "Later", "Panels": [ { "Type": "fans" } ] } ] }""");

            var read = WorkspaceLayout.Load(file);

            Assert.Equal(99, read.Version);
            Assert.Equal("fans", read.Workspaces[0].Panels[0].Type);
        }
        finally { Cleanup(file); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json at all")]
    [InlineData("""{ "version": 1, "workspaces": null }""")]
    [InlineData("""{ "version": 1, "workspaces": [] }""")]
    public void AnythingUnreadableFallsBackToTheDefaults(string contents)
    {
        string file = NewFile();
        try
        {
            File.WriteAllText(file, contents);

            // Not merely "does not throw": it has to come back with a usable set of screens,
            // because a switcher with no workspaces has no way back to a workspace.
            Assert.Equal(WorkspaceLayout.Defaults().Workspaces.Count, WorkspaceLayout.Load(file).Workspaces.Count);
        }
        finally { Cleanup(file); }
    }

    [Fact]
    public void AMissingFileIsTheDefaultsRatherThanAnError()
    {
        string file = NewFile();
        try { Assert.NotEmpty(WorkspaceLayout.Load(file).Workspaces); }
        finally { Cleanup(file); }
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(13, WorkspaceLayout.Columns)]
    [InlineData(400, WorkspaceLayout.Columns)]
    [InlineData(7, 7)]
    public void APanelCannotBeWiderOrNarrowerThanTheGrid(int span, int expected)
    {
        var normalised = new WorkspaceLayout(1, new[]
        {
            new Workspace("W", new[] { new PanelPlacement("fans", span) }),
        }).Normalised();

        Assert.Equal(expected, normalised.Workspaces[0].Panels[0].Span);
    }

    [Fact]
    public void TwoWorkspacesCannotShareAName()
    {
        // The switcher shows names, so two of them leave the user unable to tell which they are
        // editing -- and the editor writes back to whichever it thinks it is on.
        var normalised = new WorkspaceLayout(1, new[]
        {
            new Workspace("Cooling", new[] { new PanelPlacement("fans") }),
            new Workspace("Cooling", new[] { new PanelPlacement("dashboard") }),
            new Workspace("Cooling", new[] { new PanelPlacement("settings") }),
        }).Normalised();

        Assert.Equal(new[] { "Cooling", "Cooling 2", "Cooling 3" },
                     normalised.Workspaces.Select(w => w.Name).ToArray());
    }

    [Fact]
    public void ANamelessWorkspaceGetsAName()
    {
        var normalised = new WorkspaceLayout(1, new[]
        {
            new Workspace("   ", new[] { new PanelPlacement("fans") }),
        }).Normalised();

        Assert.Equal("Workspace", normalised.Workspaces[0].Name);
    }

    /// <summary>
    /// An empty workspace is kept, because it is what the user has just added and not yet filled.
    /// </summary>
    [Fact]
    public void AWorkspaceWithNoPanelsIsStillAWorkspace()
    {
        var normalised = new WorkspaceLayout(1, new[]
        {
            Workspace.Empty("New"),
            new Workspace("Fans", new[] { new PanelPlacement("fans") }),
        }).Normalised();

        Assert.Equal(2, normalised.Workspaces.Count);
        Assert.Empty(normalised.Workspaces[0].Panels);
    }

    /// <summary>A panel with no type is not a panel, and cannot be rendered as anything.</summary>
    [Fact]
    public void APanelWithNoTypeIsDropped()
    {
        var normalised = new WorkspaceLayout(1, new[]
        {
            new Workspace("W", new[] { new PanelPlacement("  "), new PanelPlacement("fans") }),
        }).Normalised();

        Assert.Single(normalised.Workspaces[0].Panels);
        Assert.Equal("fans", normalised.Workspaces[0].Panels[0].Type);
    }

    /// <summary>
    /// A save that is interrupted leaves the previous layout, not a truncated one.
    ///
    /// Tested by leaving a stale temporary file in place and saving over it, which is the state an
    /// interrupted write leaves behind. The machine this runs on hangs often enough for that to be
    /// an ordinary occurrence rather than a hypothetical.
    /// </summary>
    [Fact]
    public void AStaleTemporaryFileFromAnInterruptedSaveDoesNotBlockTheNextOne()
    {
        string file = NewFile();
        try
        {
            WorkspaceLayout.Defaults().Save(file);
            File.WriteAllText(file + ".tmp", "half a fi");

            var replacement = new WorkspaceLayout(1, new[] { new Workspace("Only", new[] { new PanelPlacement("fans") }) });
            replacement.Save(file);

            var read = WorkspaceLayout.Load(file);
            Assert.Single(read.Workspaces);
            Assert.Equal("Only", read.Workspaces[0].Name);
        }
        finally { Cleanup(file); }
    }
}
