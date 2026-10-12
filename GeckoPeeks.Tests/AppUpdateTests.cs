using GeckoPeeks.App;
using Xunit;

namespace GeckoPeeks.Tests;

public class AppUpdateTests
{
    [Theory]
    [InlineData("v1.0.12", 1, 0, 12)]
    [InlineData("1.0.11", 1, 0, 11)]
    [InlineData("V2.3.4", 2, 3, 4)]
    public void ParseTag_reads_release_tags(string tag, int major, int minor, int build)
    {
        var version = AppUpdate.ParseTag(tag);
        Assert.NotNull(version);
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1")]
    [InlineData(null)]
    public void ParseTag_rejects_noise(string? tag) => Assert.Null(AppUpdate.ParseTag(tag));

    [Fact]
    public void IsNewer_compares_three_parts_only()
    {
        Assert.True(AppUpdate.IsNewer(new Version(1, 0, 12), new Version(1, 0, 11)));
        Assert.False(AppUpdate.IsNewer(new Version(1, 0, 11), new Version(1, 0, 11)));
        Assert.False(AppUpdate.IsNewer(new Version(1, 0, 10), new Version(1, 0, 11)));
        Assert.True(AppUpdate.IsNewer(new Version(1, 1, 0), new Version(1, 0, 99)));
    }

    [Fact]
    public void PickOffer_returns_the_exe_when_the_tag_is_newer()
    {
        var json = """
            {
              "tag_name": "v1.0.12",
              "html_url": "https://github.com/gecko-of-shadow/gecko-peeks/releases/tag/v1.0.12",
              "assets": [
                {
                  "name": "notes.txt",
                  "browser_download_url": "https://example.com/notes.txt",
                  "size": 10
                },
                {
                  "name": "GeckoPeeks.exe",
                  "browser_download_url": "https://example.com/GeckoPeeks.exe",
                  "size": 71651194
                }
              ]
            }
            """;

        var offer = AppUpdate.PickOffer(json, new Version(1, 0, 11));
        Assert.NotNull(offer);
        Assert.Equal(new Version(1, 0, 12), offer!.Version);
        Assert.Equal("v1.0.12", offer.Tag);
        Assert.Equal("https://example.com/GeckoPeeks.exe", offer.DownloadUrl);
        Assert.Equal(71651194, offer.SizeBytes);
    }

    [Fact]
    public void PickOffer_ignores_the_same_or_older_tag()
    {
        var json = """
            {
              "tag_name": "v1.0.11",
              "html_url": "https://github.com/gecko-of-shadow/gecko-peeks/releases/tag/v1.0.11",
              "assets": [
                {
                  "name": "GeckoPeeks.exe",
                  "browser_download_url": "https://example.com/GeckoPeeks.exe",
                  "size": 1
                }
              ]
            }
            """;

        Assert.Null(AppUpdate.PickOffer(json, new Version(1, 0, 11)));
        Assert.Null(AppUpdate.PickOffer(json, new Version(1, 0, 12)));
    }

    [Fact]
    public void PickOffer_needs_the_GeckoPeeks_exe_asset()
    {
        var json = """
            {
              "tag_name": "v9.9.9",
              "html_url": "https://example.com",
              "assets": [
                { "name": "README.md", "browser_download_url": "https://example.com/README.md", "size": 1 }
              ]
            }
            """;

        Assert.Null(AppUpdate.PickOffer(json, new Version(1, 0, 0)));
    }

    [Fact]
    public void FormatVersion_is_three_parts() =>
        Assert.Equal("1.0.12", AppUpdate.FormatVersion(new Version(1, 0, 12)));
}
