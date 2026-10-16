namespace WatermarkRemover.Core.Logging;

/// <summary>
/// One log entry: the timestamped header line plus any continuation lines (exception stack
/// traces) that follow it. <see cref="RawText"/> preserves the original multi-line text.
/// </summary>
public sealed record LogEntry(DateTime Timestamp, string Level, string Message, string RawText);
