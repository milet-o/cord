using Cord.App.Updating;

namespace Cord.App.Tests;

public sealed class AppVersionTests
{
    [Fact]
    public void CurrentVersionIsPreOneAndFormattedCorrectly()
    {
        Assert.Equal("0.1.9", AppVersion.Current);
        Assert.Equal("v0.1.9", AppVersion.Display);
        Assert.True(Version.TryParse(AppVersion.Current, out var parsed));
        Assert.Equal(0, parsed.Major);
    }

    [Fact]
    public void ChangelogMatchesCurrentVersionAndHasValidEntries()
    {
        Assert.Equal(10, AppChangelog.Entries.Count);
        Assert.Equal(AppVersion.Display, AppChangelog.Entries[0].Version);
        Assert.Equal(AppChangelog.Entries.Count, AppChangelog.Entries.Select(e => e.Version).Distinct(StringComparer.Ordinal).Count());
        foreach (var entry in AppChangelog.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Version));
            Assert.False(string.IsNullOrWhiteSpace(entry.DateTime));
            Assert.False(string.IsNullOrWhiteSpace(entry.Title));
            Assert.NotEmpty(entry.Changes);
        }
    }

    [Theory]
    [InlineData("owner/cord", "owner/cord")]
    [InlineData(" owner/cord/ ", "owner/cord")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("onlyowner", null)]
    [InlineData("a/b/c", null)]
    public void NormalizeRepository_ValidatesOwnerRepoFormat(string? raw, string? expected)
    {
        Assert.Equal(expected, AppVersion.NormalizeRepository(raw));
    }

    [Theory]
    [InlineData("v0.2.0", "0.1.9", true)]
    [InlineData("0.1.10", "0.1.9", true)]
    [InlineData("1.0.0", "0.9.9", true)]
    [InlineData("0.1.9", "0.1.9", false)]
    [InlineData("0.1.9", "0.1.9-alpha", true)]
    [InlineData("0.1.9-beta.2", "0.1.9-beta.1", true)]
    [InlineData("0.1.8", "0.1.9", false)]
    [InlineData("invalid", "0.1.9", false)]
    public void SemanticVersion_IsNewerHandlesPreReleasesAndMinorBumps(string candidate, string current, bool expectedNewer)
    {
        Assert.Equal(expectedNewer, SemanticVersion.IsNewer(candidate, current));
    }

    [Fact]
    public void GitHubUpdateService_ParsesReleasesSkipsDraftsSortsAndSelectsWindowsZip()
    {
        const string json = """
        [
          {
            "tag_name": "v0.1.9",
            "name": "Cord v0.1.9",
            "draft": false,
            "prerelease": true,
            "published_at": "2026-11-26T18:00:00Z",
            "html_url": "https://github.com/user/cord/releases/tag/v0.1.9",
            "body": "### Highlights\n- **Guide**: Added quick-start guide\n* `Update`: Live notes",
            "assets": [
              { "name": "Cord-v0.1.9-linux-x64.tar.gz", "browser_download_url": "https://example.com/linux.tar.gz", "size": 100 },
              { "name": "Cord-v0.1.9-win-x64.zip", "browser_download_url": "https://example.com/win.zip", "size": 200 }
            ]
          },
          {
            "tag_name": "v0.2.0-draft",
            "name": "Draft",
            "draft": true,
            "prerelease": true,
            "published_at": "2026-11-27T18:00:00Z",
            "body": "Should be ignored"
          },
          {
            "tag_name": "v0.1.10",
            "name": "Cord v0.1.10",
            "draft": false,
            "prerelease": false,
            "published_at": "2026-11-28T18:00:00Z",
            "html_url": "https://github.com/user/cord/releases/tag/v0.1.10",
            "body": "- Faster audio polling",
            "assets": [
              { "name": "Cord-v0.1.10-win-x64.zip", "browser_download_url": "https://example.com/v0110-win.zip", "size": 250 }
            ]
          }
        ]
        """;

        var releases = GitHubUpdateService.ParseReleasesJson(json);
        Assert.Equal(2, releases.Count);
        Assert.Equal("0.1.10", releases[0].Version);
        Assert.Equal("0.1.9", releases[1].Version);

        var winAsset = GitHubUpdateService.SelectWindowsZipAsset(releases[1].Assets);
        Assert.NotNull(winAsset);
        Assert.Equal("Cord-v0.1.9-win-x64.zip", winAsset!.Name);

        var bullets = GitHubUpdateService.ParseBodyToBullets(releases[1].Body);
        Assert.Equal(["Highlights", "Guide: Added quick-start guide", "Update: Live notes"], bullets);
    }
}
