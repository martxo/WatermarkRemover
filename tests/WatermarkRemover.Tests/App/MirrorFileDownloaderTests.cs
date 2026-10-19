using WatermarkRemover.App.Updates;
using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.Tests.App;

public class MirrorCandidatesTests
{
    private const string ReleaseFileUrl =
        "https://github.com/martxo/WatermarkRemover/releases/download/v1.15.0/WatermarkRemover-1.15.0-delta.nupkg";

    [Fact]
    public void Github_release_download_tries_direct_then_mirrors_in_order()
    {
        IReadOnlyList<string> candidates = MirrorCandidates.For(ReleaseFileUrl);

        Assert.Equal(3, candidates.Count);
        Assert.Equal(ReleaseFileUrl, candidates[0]);
        Assert.Equal("https://ghfast.top/" + ReleaseFileUrl, candidates[1]);
        Assert.Equal("https://ghproxy.net/" + ReleaseFileUrl, candidates[2]);
    }

    [Fact]
    public void Api_urls_are_never_mirrored()
    {
        // The accelerators only proxy file downloads; the API must go direct.
        IReadOnlyList<string> candidates = MirrorCandidates.For(
            "https://api.github.com/repos/martxo/WatermarkRemover/releases?per_page=10&page=1");

        Assert.Single(candidates);
        Assert.StartsWith("https://api.github.com/", candidates[0]);
    }

    [Theory]
    [InlineData("https://github.com/martxo/WatermarkRemover")]
    [InlineData("https://github.com/martxo/WatermarkRemover/releases")]
    [InlineData("https://example.com/some/file.zip")]
    public void Non_download_urls_go_direct_only(string url)
    {
        Assert.Single(MirrorCandidates.For(url));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        IReadOnlyList<string> candidates = MirrorCandidates.For(
            "HTTPS://GITHUB.COM/martxo/WatermarkRemover/releases/download/v1.15.0/x.nupkg");

        Assert.Equal(3, candidates.Count);
    }
}

public class VelopackProgressMapperTests
{
    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(50, 50.0)]
    [InlineData(100, 100.0)]
    public void Full_download_keeps_velopack_percent_as_is(int percent, double expected)
    {
        var progress = VelopackProgressMapper.Map(percent, isDelta: false, 1000, "全量更新");

        Assert.Equal(UpdateStage.Downloading, progress.Stage);
        Assert.Equal(expected, progress.Percent);
        Assert.Equal("全量更新", progress.SourceDescription);
    }

    [Fact]
    public void Full_download_estimates_bytes_from_package_size()
    {
        var progress = VelopackProgressMapper.Map(50, isDelta: false, 1000, null);

        Assert.Equal(500, progress.BytesReceived);
        Assert.Equal(1000, progress.TotalBytes);
    }

    [Fact]
    public void Unknown_package_size_reports_no_bytes()
    {
        var progress = VelopackProgressMapper.Map(50, isDelta: false, null, null);

        Assert.Equal(0, progress.BytesReceived);
        Assert.Null(progress.TotalBytes);
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(35, 50.0)] // 35/70 of the delta progress = half of the download
    [InlineData(69, 98.57)]
    public void Delta_download_rescales_0_70_onto_the_full_bar(int percent, double expected)
    {
        var progress = VelopackProgressMapper.Map(percent, isDelta: true, 700, "增量更新");

        Assert.Equal(UpdateStage.Downloading, progress.Stage);
        Assert.Equal(expected, progress.Percent!.Value, precision: 2);
    }

    [Theory]
    [InlineData(70)]
    [InlineData(85)]
    [InlineData(100)]
    public void Delta_application_above_70_shows_as_verifying(int percent)
    {
        var progress = VelopackProgressMapper.Map(percent, isDelta: true, 700, "增量更新");

        Assert.Equal(UpdateStage.Verifying, progress.Stage);
    }

    [Fact]
    public void Percent_is_clamped_to_0_100()
    {
        Assert.Equal(0.0, VelopackProgressMapper.Map(-5, isDelta: false, null, null).Percent);
        Assert.Equal(100.0, VelopackProgressMapper.Map(150, isDelta: false, null, null).Percent);
    }
}
