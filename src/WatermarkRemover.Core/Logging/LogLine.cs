using System.Text;

namespace WatermarkRemover.Core.Logging;

/// <summary>
/// One log line's layout, shared by the app's file logger and the log parser. The structured
/// prefix is fixed-width for the level and optional for the rest, so old files (timestamp +
/// level + message only) keep parsing while new lines carry the correlation fields:
///
/// <code>
/// 2026-10-24 11:10:07.123 [INFO ] [SCAN] [op=000123] [t=12] 消息
/// </code>
///
/// Pure formatting only — no I/O — so the layout is unit-testable.
/// </summary>
public static class LogLine
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";

    /// <summary>Five-character level text; INFO/WARN get a trailing space so columns line up.</summary>
    public static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO ",
        LogLevel.Warn => "WARN ",
        LogLevel.Error => "ERROR",
        _ => "INFO ",
    };

    /// <summary>
    /// Builds one header line. Category, operation id and thread are omitted when absent so the
    /// line shape degrades cleanly to the pre-observability format.
    /// </summary>
    public static string Format(
        DateTime timestamp,
        LogLevel level,
        string? category,
        string? operationId,
        int threadId,
        string message)
    {
        var sb = new StringBuilder(160);
        sb.Append(timestamp.ToString(TimestampFormat));

        sb.Append(" [").Append(LevelText(level)).Append(']');

        if (!string.IsNullOrWhiteSpace(category))
            sb.Append(" [").Append(category).Append(']');

        if (!string.IsNullOrWhiteSpace(operationId))
            sb.Append(" [op=").Append(operationId).Append(']');

        if (threadId > 0)
            sb.Append(" [t=").Append(threadId).Append(']');

        sb.Append(' ').Append(message);
        return sb.ToString();
    }
}
