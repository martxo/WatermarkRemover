using System.IO;
using System.Text;
using WatermarkRemover.Core.Logging;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App.Logging;

/// <summary>
/// Reads the daily log files under <see cref="AppFolders.LogDirectory"/> and packages a time
/// range of entries into a self-contained report the user can hand over for remote diagnosis.
/// Parsing and filtering live in Core (<see cref="LogFilter"/>); this class only does I/O and
/// report assembly.
/// </summary>
public static class LogExportService
{
    /// <summary>Entries in [now - range, now), oldest first, across the daily files involved.</summary>
    public static IReadOnlyList<LogEntry> Collect(TimeSpan range)
    {
        DateTime from = DateTime.Now - range;
        DateTime to = DateTime.Now;

        var lines = new List<string>();
        foreach (string file in FilesCovering(from, to))
        {
            try
            {
                lines.AddRange(File.ReadLines(file));
            }
            catch
            {
                // A locked/unreadable file must not break the export; other files still count.
            }
        }

        return LogFilter.FilterByRange(LogFilter.Parse(lines), from, to);
    }

    /// <summary>Daily files whose date can contain entries in [from, to], oldest first.</summary>
    private static IEnumerable<string> FilesCovering(DateTime from, DateTime to)
    {
        if (!Directory.Exists(AppFolders.LogDirectory)) yield break;

        foreach (string file in Directory.EnumerateFiles(AppFolders.LogDirectory, "app-*.log")
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileNameWithoutExtension(file); // app-yyyyMMdd
            if (!DateTime.TryParseExact(
                    name.Length >= 4 ? name[4..] : string.Empty,
                    "yyyyMMdd",
                    null,
                    System.Globalization.DateTimeStyles.None,
                    out DateTime date))
                continue;

            // File date is its midnight; it covers [date, date+1) and overlaps the range when
            // the range starts before that day's end and ends after its start.
            if (date.Date <= to.Date && date.Date >= from.Date.AddDays(-1))
                yield return file;
        }
    }

    /// <summary>
    /// Self-contained report: environment header (version, OS, engine chain — the same fields as
    /// the copyable error report) followed by the raw entries with exception stacks intact.
    /// </summary>
    public static string BuildReport(IReadOnlyList<LogEntry> entries, TimeSpan range)
    {
        var report = new StringBuilder();
        DiagnosticReportBuilder.AppendEnvironmentHeader(report);
        report.AppendLine($"导出时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"时间范围：最近 {FormatRange(range)}（{DateTime.Now - range:yyyy-MM-dd HH:mm:ss} 起）");
        report.AppendLine($"日志条数：{entries.Count}");
        DiagnosticReportBuilder.AppendEngineSection(report);
        report.AppendLine("---- 执行记录 ----");
        foreach (LogEntry entry in entries)
            report.AppendLine(LogFilter.Format(entry));
        return report.ToString();
    }

    /// <summary>Writes the report to a text file (UTF-8).</summary>
    public static void Export(string path, string report) =>
        File.WriteAllText(path, report, Encoding.UTF8);

    private static string FormatRange(TimeSpan range) => range.TotalHours switch
    {
        >= 24 * 6.5 => "7 天",
        >= 24 * 0.5 and < 24 * 2 => "24 小时",
        _ => $"{(int)range.TotalHours} 小时",
    };
}
