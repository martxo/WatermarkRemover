using WatermarkRemover.Core.Logging;

namespace WatermarkRemover.Tests.Core;

public class LogFilterTests
{
    private static readonly DateTime From = new(2026, 10, 16, 10, 0, 0);
    private static readonly DateTime To = new(2026, 10, 16, 11, 0, 0);

    [Fact]
    public void ParsesHeaderLineFields()
    {
        var entries = LogFilter.Parse(new[]
        {
            "2026-10-16 10:00:00.123 [INFO] 应用启动 v1.9.0",
        });

        var entry = Assert.Single(entries);
        Assert.Equal(new DateTime(2026, 10, 16, 10, 0, 0, 123), entry.Timestamp);
        Assert.Equal("INFO", entry.Level);
        Assert.Equal("应用启动 v1.9.0", entry.Message);
    }

    [Fact]
    public void MergesContinuationLinesIntoPreviousEntry()
    {
        var entries = LogFilter.Parse(new[]
        {
            "2026-10-16 10:00:01.000 [ERROR] 命名：渲染失败",
            "System.InvalidOperationException: boom",
            "   at WatermarkRemover.App.Foo()",
        });

        var entry = Assert.Single(entries);
        Assert.Equal("命名：渲染失败", entry.Message);
        Assert.Contains("System.InvalidOperationException: boom", entry.RawText);
        Assert.Contains("at WatermarkRemover.App.Foo()", entry.RawText);
    }

    [Fact]
    public void DropsOrphanContinuationBeforeFirstHeader()
    {
        var entries = LogFilter.Parse(new[]
        {
            "System.InvalidOperationException: orphan",
            "2026-10-16 10:00:00.000 [INFO] first real entry",
        });

        var entry = Assert.Single(entries);
        Assert.Equal("first real entry", entry.Message);
    }

    [Fact]
    public void FiltersByRangeHalfOpenInterval()
    {
        var entries = LogFilter.Parse(new[]
        {
            "2026-10-16 09:59:59.999 [INFO] too early",
            "2026-10-16 10:00:00.000 [INFO] at boundary",
            "2026-10-16 10:30:00.000 [INFO] inside",
            "2026-10-16 11:00:00.000 [INFO] at end",
        });

        var filtered = LogFilter.FilterByRange(entries, From, To);

        Assert.Equal(new[] { "at boundary", "inside" }, filtered.Select(e => e.Message).ToArray());
    }

    [Fact]
    public void KeepsOriginalOrderAcrossMixedEntries()
    {
        var entries = LogFilter.Parse(new[]
        {
            "2026-10-15 23:00:00.000 [INFO] previous day",
            "2026-10-16 10:05:00.000 [ERROR] later",
            "  stack line",
            "2026-10-16 10:01:00.000 [INFO] earlier",
        });

        var filtered = LogFilter.FilterByRange(
            entries, new DateTime(2026, 10, 16, 0, 0, 0), new DateTime(2026, 10, 17, 0, 0, 0));

        Assert.Equal(new[] { "later", "earlier" }, filtered.Select(e => e.Message).ToArray());
    }

    [Fact]
    public void EmptyInputReturnsEmpty()
    {
        Assert.Empty(LogFilter.Parse(Array.Empty<string>()));
        Assert.Empty(LogFilter.FilterByRange(Array.Empty<LogEntry>(), From, To));
    }

    [Fact]
    public void FormatRestoresRawText()
    {
        var entries = LogFilter.Parse(new[]
        {
            "2026-10-16 10:00:01.000 [ERROR] 命名：渲染失败",
            "System.InvalidOperationException: boom",
        });

        string raw = LogFilter.Format(Assert.Single(entries));
        Assert.Contains("命名：渲染失败", raw);
        Assert.Contains("System.InvalidOperationException: boom", raw);
    }
}
