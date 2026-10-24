using System.Text.RegularExpressions;

namespace WatermarkRemover.Core.Logging;

/// <summary>
/// Pure parsing and filtering of AppLog output: the daily log files contain timestamped header
/// lines plus untimestamped continuation lines that belong to the previous entry (exception
/// stack traces). The header carries optional structured fields — category, operation id and
/// thread — which older files simply omit; both shapes parse. No I/O here — the app layer
/// reads the files, this class turns lines into entries and narrows them by time range.
/// </summary>
public static partial class LogFilter
{
    [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[([A-Z]+)\s*\](?: \[([A-Z]+)\])?(?: \[op=([0-9A-Za-z]+)\])?(?: \[t=(\d+)\])? (.*)$")]
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
        string category = string.Empty;
        string operationId = string.Empty;
        int threadId = 0;
        string message = string.Empty;
        var raw = new List<string>();

        void Flush()
        {
            if (raw.Count == 0) return;
            entries.Add(new LogEntry(
                timestamp, level, category, operationId, threadId, message,
                string.Join(Environment.NewLine, raw)));
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
                category = match.Groups[3].Success ? match.Groups[3].Value : string.Empty;
                operationId = match.Groups[4].Success ? match.Groups[4].Value : string.Empty;
                threadId = match.Groups[5].Success
                    ? int.Parse(match.Groups[5].Value, System.Globalization.CultureInfo.InvariantCulture)
                    : 0;
                message = match.Groups[6].Value;
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
