using WatermarkRemover.Core.Features.Screenshot;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Tests.Core;

public sealed class ScreenshotNamingTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "wm-shot-" + Guid.NewGuid().ToString("N"));

    public ScreenshotNamingTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void File_name_is_a_sortable_timestamp_with_png_extension()
    {
        string name = ScreenshotNaming.BuildFileName(new DateTime(2026, 10, 31, 14, 35, 12));

        Assert.Equal("截图_20261031_143512.png", name);
        Assert.EndsWith(".png", name);
    }

    [Fact]
    public void A_configured_directory_wins_and_empty_falls_back_to_pictures()
    {
        var settings = new AppSettings { ScreenshotOutputDirectory = " D:\\Shots " };
        Assert.Equal("D:\\Shots", ScreenshotNaming.ResolveDirectory(settings));

        settings.ScreenshotOutputDirectory = string.Empty;
        string fallback = ScreenshotNaming.ResolveDirectory(settings);
        Assert.Contains(ScreenshotNaming.DefaultFolderName, fallback);
        Assert.True(Path.IsPathRooted(fallback));
    }

    [Fact]
    public void A_free_name_is_returned_unchanged()
    {
        Assert.Equal("fresh.png", ScreenshotNaming.EnsureUnique(_directory, "fresh.png"));
    }

    [Fact]
    public void Numbered_collisions_keep_increasing()
    {
        File.WriteAllBytes(Path.Combine(_directory, "shot.png"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(_directory, "shot_2.png"), Array.Empty<byte>());

        Assert.Equal("shot_3.png", ScreenshotNaming.EnsureUnique(_directory, "shot.png"));
    }
}
