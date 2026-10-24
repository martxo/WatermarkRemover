namespace WatermarkRemover.Core.Logging;

/// <summary>
/// One log entry: the timestamped header line plus any continuation lines (exception stack
/// traces) that follow it. <see cref="RawText"/> preserves the original multi-line text.
/// The structured fields are empty/zero for legacy lines that carried no category, operation or
/// thread — the parser stays backward compatible.
/// </summary>
public sealed record LogEntry(
    DateTime Timestamp,
    string Level,
    string Category,
    string OperationId,
    int ThreadId,
    string Message,
    string RawText);
