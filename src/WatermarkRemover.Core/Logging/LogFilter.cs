using System.Text.RegularExpressions;

namespace WatermarkRemover.Core.Logging;

/// <summary>
/// Pure parsing and filtering of AppLog output: the daily log files contain timestamped header
/// lines ("2026-10-16 10:00:00.123 [INFO] message") plus untimestamped continuation lines that
/// belong to the previous entry (exception stack traces). No I/O here — the app layer reads the
/// files, this class turns lines into entries and narrows them by time range.
/// </summary>
public static partial class LogFilter
{
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[(\w+)\] (.*)$")]
    private static partial Regex HeaderPattern();

    /// <summary>
    /// Parses log lines into entries. Continuation lines (no timestamp header) attach to the
    /// preceding entry so exception stacks stay together; orphan continuations are dropped.
    /// Input order is preserved — files are read oldest first by the caller.
    /// </summary>
    public static IReadOnlyList<LogEntry> Parse(IEnumerable<string> lines)
    {
        var entries = new List<LogEntry>();
        DateTime timestamp = default;
        string level = string.Empty;
        string message = string.Empty;
        var raw = new List<string>();

        void Flush()
        {
            if (raw.Count == 0) return;
            entries.Add(new LogEntry(timestamp, level, message, string.Join(Environment.NewLine, raw)));
            raw.Clear();
        }

        foreach (string line in lines)
        {
            Match match = HeaderPattern().Match(line);
            if (match.Success)
            {
                Flush();
                timestamp = DateTime.ParseExact(match.Groups[1].Value, "yyyy-MM-dd HH:mm:ss.fff", null);
                level = match.Groups[2].Value;
                message = match.Groups[3].Value;
                raw.Add(line);
            }
            else if (raw.Count > 0)
            {
                raw.Add(line); // continuation of the current entry (stack trace line)
            }
            // else: orphan continuation before any header — nothing to attach it to.
        }

        Flush();
        return entries;
    }

    /// <summary>Entries in [from, to), keeping original order.</summary>
    public static IReadOnlyList<LogEntry> FilterByRange(
        IEnumerable<LogEntry> entries, DateTime from, DateTime to) =>
        entries.Where(e => e.Timestamp >= from && e.Timestamp < to).ToList();

    /// <summary>Restores the original multi-line text of one entry.</summary>
    public static string Format(LogEntry entry) => entry.RawText;
}
