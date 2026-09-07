using OmniHub.Core.Update;

namespace OmniHub.Tests;

/// <summary>
/// The update checker's identification rule.
///
/// This repository publishes releases for two different applications. OmniControl Suite's
/// v9.0.0 is numerically the highest tag on it and is what GitHub's own /releases/latest
/// returns, so a naive check would offer every OmniHub user an unrelated program as an
/// upgrade -- and, because 9.0.0 beats any OmniHub version for years, would keep offering it
/// forever. These tests exist to make that specific mistake impossible to reintroduce.
/// </summary>
public class UpdateCheckTests
{
    /// <summary>The real shape of the repository's release list, trimmed to what is read.</summary>
    private const string BothApplications = """
    [
      {
        "tag_name": "v9.0.0",
        "name": "OmniControl Suite v9.0.0 - Ground-Up Rebuild",
        "body": "Completely rebuilt application architecture.",
        "published_at": "2026-08-24T16:28:51Z",
        "prerelease": false,
        "assets": [
          { "name": "OmniControlSuite.exe",
            "browser_download_url": "https://example.invalid/OmniControlSuite.exe",
            "size": 8388608 }
        ]
      },
      {
        "tag_name": "v1.0.0",
        "name": "OmniHub v1.0.0",
        "body": "Fan curve control with a safety floor.",
        "published_at": "2026-09-06T20:10:35Z",
        "prerelease": false,
        "assets": [
          { "name": "OmniHub-v1.0.0-win-x64.zip",
            "browser_download_url": "https://example.invalid/OmniHub-v1.0.0-win-x64.zip",
            "size": 66354534 }
        ]
      }
    ]
    """;

    [Fact]
    public void OmniControlSuite_IsNotTreatedAsAnOmniHubRelease()
    {
        var releases = UpdateCheck.ParseReleases(BothApplications);

        Assert.Single(releases);
        Assert.Equal("v1.0.0", releases[0].Tag);
        Assert.DoesNotContain(releases, r => r.Tag == "v9.0.0");
    }

    /// <summary>
    /// The consequence that matters: 9.0.0 is higher than any plausible OmniHub version, so if
    /// it ever leaked through, every user would be told to upgrade -- to the wrong program.
    /// </summary>
    [Fact]
    public void AForeignHigherVersion_IsNeverOfferedAsAnUpdate()
    {
        var releases = UpdateCheck.ParseReleases(BothApplications);

        Assert.Null(UpdateCheck.NewerThan(releases, new Version(1, 0, 0)));
    }

    [Fact]
    public void ParsedRelease_CarriesTheFieldsTheUiShows()
    {
        var r = UpdateCheck.ParseReleases(BothApplications)[0];

        Assert.Equal(new Version(1, 0, 0), r.Version);
        Assert.Equal("OmniHub v1.0.0", r.Title);
        Assert.Equal("Fan curve control with a safety floor.", r.Notes);
        Assert.Equal(66354534, r.DownloadSize);
        Assert.Equal("https://example.invalid/OmniHub-v1.0.0-win-x64.zip", r.DownloadUrl);
        Assert.False(r.IsPrerelease);
        Assert.Equal(2026, r.PublishedAt.Year);
    }

    [Fact]
    public void ANewerOmniHubRelease_IsOffered()
    {
        var json = BothApplications.Replace("v1.0.0", "v1.4.0");
        var releases = UpdateCheck.ParseReleases(json);

        var update = UpdateCheck.NewerThan(releases, new Version(1, 0, 0));

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 4, 0), update!.Version);
    }

    /// <summary>
    /// Someone on a stable build has not asked to be moved onto a beta. Opting in is a decision
    /// for a person, not a default for an updater.
    /// </summary>
    [Fact]
    public void Prereleases_AreNotOfferedToStableUsers()
    {
        string json = """
        [
          { "tag_name": "v2.0.0-beta.1", "name": "OmniHub v2.0.0 beta", "body": "", "prerelease": true,
            "published_at": "2026-09-07T00:00:00Z",
            "assets": [ { "name": "OmniHub-v2.0.0-beta.1-win-x64.zip",
                          "browser_download_url": "https://example.invalid/b.zip", "size": 1 } ] }
        ]
        """;

        var releases = UpdateCheck.ParseReleases(json);

        Assert.Single(releases);                                             // still in the changelog
        Assert.Null(UpdateCheck.NewerThan(releases, new Version(1, 0, 0)));  // but never offered
    }

    /// <summary>A release with no downloadable build is not an update anyone can take.</summary>
    [Fact]
    public void ReleaseWithoutAnOmniHubAsset_IsSkipped()
    {
        string json = """
        [
          { "tag_name": "v1.5.0", "name": "Notes only", "body": "", "prerelease": false,
            "published_at": "2026-09-07T00:00:00Z", "assets": [] }
        ]
        """;

        Assert.Empty(UpdateCheck.ParseReleases(json));
    }

    [Fact]
    public void MalformedEntry_DoesNotHideTheValidOnes()
    {
        string json = """
        [
          { "tag_name": null },
          { "not_a_release": true },
          { "tag_name": "v1.2.0", "name": "OmniHub v1.2.0", "body": "ok", "prerelease": false,
            "published_at": "2026-09-07T00:00:00Z",
            "assets": [ { "name": "OmniHub-v1.2.0-win-x64.zip",
                          "browser_download_url": "https://example.invalid/a.zip", "size": 10 } ] }
        ]
        """;

        var releases = UpdateCheck.ParseReleases(json);

        Assert.Single(releases);
        Assert.Equal("v1.2.0", releases[0].Tag);
    }

    [Fact]
    public void GarbageResponse_YieldsNoReleasesRatherThanThrowing()
    {
        Assert.Empty(UpdateCheck.ParseReleases("<html>rate limited</html>"));
        Assert.Empty(UpdateCheck.ParseReleases(""));
        Assert.Empty(UpdateCheck.ParseReleases("{\"message\":\"Not Found\"}"));
    }

    [Fact]
    public void ReleasesAreOrderedNewestFirst_ForTheChangelog()
    {
        string json = """
        [
          { "tag_name": "v1.0.0", "name": "a", "body": "", "prerelease": false,
            "published_at": "2026-01-01T00:00:00Z",
            "assets": [ { "name": "OmniHub-a.zip", "browser_download_url": "https://example.invalid/a.zip", "size": 1 } ] },
          { "tag_name": "v1.10.0", "name": "b", "body": "", "prerelease": false,
            "published_at": "2026-02-01T00:00:00Z",
            "assets": [ { "name": "OmniHub-b.zip", "browser_download_url": "https://example.invalid/b.zip", "size": 1 } ] },
          { "tag_name": "v1.2.0", "name": "c", "body": "", "prerelease": false,
            "published_at": "2026-03-01T00:00:00Z",
            "assets": [ { "name": "OmniHub-c.zip", "browser_download_url": "https://example.invalid/c.zip", "size": 1 } ] }
        ]
        """;

        var tags = UpdateCheck.ParseReleases(json).Select(r => r.Tag).ToArray();

        // 1.10 above 1.2: version order, not the string order a lexical sort would give.
        Assert.Equal(new[] { "v1.10.0", "v1.2.0", "v1.0.0" }, tags);
    }

    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("V2.0", 2, 0, 0)]
    [InlineData("v3", 3, 0, 0)]
    [InlineData("v1.4.0-rc.2", 1, 4, 0)]
    public void TagsParseToVersions(string tag, int major, int minor, int patch)
    {
        Assert.Equal(new Version(major, minor, patch), UpdateCheck.ParseVersion(tag));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nightly")]
    [InlineData("v")]
    public void NonVersionTags_ParseToNull(string tag)
    {
        // Null rather than 0.0.0: a named tag should be dropped, not sorted in as ancient.
        Assert.Null(UpdateCheck.ParseVersion(tag));
    }

    [Fact]
    public void SameOrOlderRelease_IsNotAnUpdate()
    {
        var releases = UpdateCheck.ParseReleases(BothApplications);
        Assert.Null(UpdateCheck.NewerThan(releases, new Version(1, 0, 0)));
        Assert.Null(UpdateCheck.NewerThan(releases, new Version(2, 0, 0)));
    }
}
