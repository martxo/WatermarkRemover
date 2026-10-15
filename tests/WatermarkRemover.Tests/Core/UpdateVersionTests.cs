using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.Tests.Core;

public class UpdateVersionTests
{
    [Theory]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("V1.2.3", 1, 2, 3)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2", 1, 2, 0)]
    [InlineData("  v2.0.1  ", 2, 0, 1)]
    public void Parses_common_release_tag_forms(string tag, int major, int minor, int build)
    {
        var version = UpdateVersion.ParseReleaseTag(tag);

        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("release-1")]
    [InlineData("v1.2.3-beta")] // prereleases are never offered
    public void Rejects_tags_it_cannot_compare_reliably(string? tag)
    {
        Assert.Null(UpdateVersion.ParseReleaseTag(tag));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.0", "1.1.0", true)]
    [InlineData("1.0.0", "2.0.0", true)]
    [InlineData("1.1.0", "1.1.0", false)] // same version: no self-update ping-pong
    [InlineData("1.1.0", "1.0.9", false)]
    [InlineData("1.1.0", "1.1", false)] // 1.1 == 1.1.0
    public void IsNewer_compares_strictly(string current, string candidate, bool expected)
    {
        Assert.Equal(expected, UpdateVersion.IsNewer(
            UpdateVersion.ParseReleaseTag(current)!,
            UpdateVersion.ParseReleaseTag(candidate)!));
    }
}
