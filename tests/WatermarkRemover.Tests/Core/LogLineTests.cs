using WatermarkRemover.Core.Logging;

namespace WatermarkRemover.Tests.Core;

/// <summary>
/// The log line's structured prefix is the contract between the file logger and the log parser,
/// so both directions are pinned here: exact layout, optional fields, and a format→parse round trip.
/// </summary>
public class LogLineTests
{
    private static readonly DateTime Stamp = new(2026, 10, 24, 11, 10, 7, 123);

    [Theory]
    [InlineData(LogLevel.Trace, "TRACE")]
    [InlineData(LogLevel.Debug, "DEBUG")]
    [InlineData(LogLevel.Info, "INFO ")]
    [InlineData(LogLevel.Warn, "WARN ")]
    [InlineData(LogLevel.Error, "ERROR")]
    [InlineData(LogLevel.Off, "INFO ")]
    public void Level_text_is_five_characters_wide(LogLevel level, string expected) =>
        Assert.Equal(expected, LogLine.LevelText(level));

    [Fact]
    public void Format_writes_every_structured_field()
    {
        string line = LogLine.Format(
            Stamp, LogLevel.Debug, LogCategory.Scan, "000123", 12, "操作步骤[SCAN]：第 1 页");

        Assert.Equal(
            "2026-10-24 11:10:07.123 [DEBUG] [SCAN] [op=000123] [t=12] 操作步骤[SCAN]：第 1 页",
            line);
    }

    [Fact]
    public void Format_omits_optional_segments_when_absent()
    {
        string line = LogLine.Format(Stamp, LogLevel.Info, null, null, 0, "应用启动 v1.16.13");

        Assert.Equal("2026-10-24 11:10:07.123 [INFO ] 应用启动 v1.16.13", line);
    }

    [Fact]
    public void Formatted_line_round_trips_through_the_parser()
    {
        string line = LogLine.Format(
            Stamp, LogLevel.Warn, LogCategory.Preview, "000124", 15, "慢操作[PREVIEW]：缩略图填充");

        var entry = Assert.Single(LogFilter.Parse(new[] { line }));

        Assert.Equal(Stamp, entry.Timestamp);
        Assert.Equal("WARN", entry.Level);
        Assert.Equal("PREVIEW", entry.Category);
        Assert.Equal("000124", entry.OperationId);
        Assert.Equal(15, entry.ThreadId);
        Assert.Equal("慢操作[PREVIEW]：缩略图填充", entry.Message);
    }

    [Fact]
    public void Legacy_shaped_line_round_trips_without_structured_fields()
    {
        string line = LogLine.Format(Stamp, LogLevel.Error, null, null, 0, "扫描失败");

        var entry = Assert.Single(LogFilter.Parse(new[] { line }));

        Assert.Equal("ERROR", entry.Level);
        Assert.Equal(string.Empty, entry.Category);
        Assert.Equal(string.Empty, entry.OperationId);
        Assert.Equal(0, entry.ThreadId);
        Assert.Equal("扫描失败", entry.Message);
    }
}
