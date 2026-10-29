using System.IO.Compression;
using WatermarkRemover.App.Logging;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// A crash bundle is the only diagnostic channel this product has (no telemetry backend), so the
/// zip must always be produced and must say what happened.
/// </summary>
public sealed class CrashReporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wm-crash-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_captured_crash_produces_a_zip_with_a_summary()
    {
        string? zip = CrashReporter.Capture(
            new InvalidOperationException("boom"), "单元测试", _dir);

        Assert.NotNull(zip);
        Assert.True(File.Exists(zip));

        using ZipArchive archive = ZipFile.OpenRead(zip!);
        ZipArchiveEntry? summary = archive.GetEntry("summary.txt");
        Assert.NotNull(summary);

        using var reader = new StreamReader(summary!.Open());
        string text = reader.ReadToEnd();
        Assert.Contains("单元测试", text);
        Assert.Contains("boom", text);
        Assert.Contains("程序版本", text);
    }

    [Fact]
    public void Older_bundles_beyond_the_keep_limit_are_removed()
    {
        for (int i = 0; i < 12; i++)
        {
            string? zip = CrashReporter.Capture(new Exception($"drop {i}"), "单元测试", _dir);
            Assert.NotNull(zip);
            File.SetLastWriteTimeUtc(zip!, DateTime.UtcNow.AddMinutes(-i));
        }

        // The newest 10 survive; the two oldest are pruned.
        Assert.Equal(10, Directory.GetFiles(_dir, "*.zip").Length);
    }
}
