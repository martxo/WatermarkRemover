using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.Tests.Core;

/// <summary>
/// The numbers a user reads off the update progress window ("45.2 MB / 122.0 MB · 3.1 MB/s · 约需 25 秒")
/// are formatted here — a pure function, so every degradation path is testable without WPF.
/// </summary>
public class UpdateProgressFormatterTests
{
    [Fact]
    public void FormatBytes_ScalesUnits()
    {
        Assert.Equal("812 B", ProgressTextFormatter.FormatBytes(812));
        Assert.Equal("1.0 KB", ProgressTextFormatter.FormatBytes(1024));
        Assert.Equal("45.2 MB", ProgressTextFormatter.FormatBytes((long)(45.2 * 1024 * 1024)));
        Assert.Equal("1.2 GB", ProgressTextFormatter.FormatBytes((long)(1.234 * 1024 * 1024 * 1024)));
    }

    [Fact]
    public void FormatBytes_NeverReturnsNegative()
    {
        Assert.Equal("0 B", ProgressTextFormatter.FormatBytes(-5));
    }

    [Fact]
    public void FormatSpeed_AppendsPerSecond()
    {
        Assert.Equal("3.1 MB/s", ProgressTextFormatter.FormatSpeed(3.1 * 1024 * 1024));
    }

    [Fact]
    public void TryFormatEta_EstimatesSecondsMinutesHours()
    {
        Assert.Equal("约需 25 秒", ProgressTextFormatter.TryFormatEta(0, 100 * 1024 * 1024, 4.0 * 1024 * 1024));
        Assert.Equal("约需 3 分钟", ProgressTextFormatter.TryFormatEta(0, 100 * 1024 * 1024, 600.0 * 1024));
        Assert.Equal("约需 3 小时", ProgressTextFormatter.TryFormatEta(0, 1024L * 1024 * 1024, 128.0 * 1024));
    }

    [Fact]
    public void TryFormatEta_DegradesWhenDataUnreliable()
    {
        // Unknown total: no honest estimate.
        Assert.Null(ProgressTextFormatter.TryFormatEta(50, null, 1024 * 1024.0));
        // Unknown or too-slow speed: drop the estimate instead of guessing.
        Assert.Null(ProgressTextFormatter.TryFormatEta(50, 100, null));
        Assert.Null(ProgressTextFormatter.TryFormatEta(50, 100, ProgressTextFormatter.MinSpeedBytesPerSecond - 1));
        // Already complete.
        Assert.Null(ProgressTextFormatter.TryFormatEta(100, 100, 1024 * 1024.0));
    }

    [Fact]
    public void FormatDownloadDetail_ComposesSections()
    {
        long received = (long)(45.2 * 1024 * 1024);
        long total = (long)(122.0 * 1024 * 1024);
        string text = ProgressTextFormatter.FormatDownloadDetail(received, total, 3.1 * 1024 * 1024);

        Assert.Contains("已下载 45.2 MB / 122.0 MB", text);
        Assert.Contains("3.1 MB/s", text);
        Assert.Contains("约需", text);
    }

    [Fact]
    public void FormatDownloadDetail_UnknownTotalStillShowsReceivedBytes()
    {
        string text = ProgressTextFormatter.FormatDownloadDetail(1024 * 1024, null, null);

        Assert.Contains("已下载 1.0 MB", text);
        Assert.DoesNotContain("/", text);
        Assert.DoesNotContain("约需", text);
    }

    [Fact]
    public void UpdateProgressRecord_HoldsNullableSections()
    {
        var progress = new UpdateProgress(UpdateStage.Downloading, null, 123, null, null, "官方下载");

        Assert.Null(progress.Percent);
        Assert.Null(progress.TotalBytes);
        Assert.Null(progress.SpeedBytesPerSecond);
        Assert.Equal("官方下载", progress.SourceDescription);
    }
}
