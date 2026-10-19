using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Recognition;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App.Recognition;

/// <summary>
/// Appends recognition reports as JSONL — one report per line — to reports\recognition-yyyyMMdd.jsonl,
/// the structured counterpart to the free-text log (90-day retention: reports are inspection and
/// training corpus, far longer than the 7-day logs). Appends are process-locked so concurrent
/// job threads never interleave a line. Never throws: a failing store degrades to an error log.
/// </summary>
public sealed class RecognitionReportStore : IRecognitionReportStore
{
    public const int RetentionDays = 90;

    private const string FilePrefix = "recognition-";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        // Keep the line compact: absent coordinates read back as null defaults.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object ProcessSync = new();

    private readonly string _directory;
    private bool _purged;

    /// <param name="directory">Target directory; defaults to <see cref="AppFolders.ReportDirectory"/>. Tests pass a temp dir.</param>
    public RecognitionReportStore(string? directory = null)
    {
        _directory = directory ?? AppFolders.ReportDirectory;
    }

    public string Append(RecognitionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        try
        {
            PurgeOldOnce();

            string path = Path.Combine(_directory, FilePrefix + report.Timestamp.ToString("yyyyMMdd") + ".jsonl");
            string json = JsonSerializer.Serialize(report, JsonOptions);
            lock (ProcessSync)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(path, json + Environment.NewLine);
            }
            return path;
        }
        catch (Exception ex)
        {
            AppLog.Error($"识别报告写入失败：{report.InputPath}", ex);
            return string.Empty;
        }
    }

    public IReadOnlyList<RecognitionReport> ReadRecent(int maxLines)
    {
        if (maxLines <= 0) return Array.Empty<RecognitionReport>();

        try
        {
            if (!Directory.Exists(_directory)) return Array.Empty<RecognitionReport>();

            var results = new List<RecognitionReport>(Math.Min(maxLines, 64));
            foreach (string file in Directory.GetFiles(_directory, FilePrefix + "*.jsonl")
                         .OrderByDescending(f => f, StringComparer.Ordinal))
            {
                string[] lines = File.ReadAllLines(file);
                for (int i = lines.Length - 1; i >= 0 && results.Count < maxLines; i--)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    if (JsonSerializer.Deserialize<RecognitionReport>(lines[i], JsonOptions) is { } report)
                        results.Add(report);
                }
                if (results.Count >= maxLines) break;
            }
            return results;
        }
        catch
        {
            return Array.Empty<RecognitionReport>();
        }
    }

    /// <summary>Removes report files older than the retention window; runs once per store instance.</summary>
    public void PurgeOld()
    {
        lock (ProcessSync)
        {
            if (_purged) return;
            _purged = true;
        }

        try
        {
            if (!Directory.Exists(_directory)) return;

            DateTime cutoff = DateTime.Today.AddDays(-RetentionDays);
            foreach (string file in Directory.GetFiles(_directory, FilePrefix + "*.jsonl"))
            {
                // recognition-yyyyMMdd → same yyyyMMdd parsing AppLog.PurgeOldLogs uses.
                string name = Path.GetFileNameWithoutExtension(file); // recognition-yyyyMMdd
                string stamp = name.Length > FilePrefix.Length ? name[FilePrefix.Length..] : string.Empty;
                if (!DateTime.TryParseExact(stamp, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out DateTime date))
                    continue;

                if (date < cutoff) File.Delete(file);
            }
        }
        catch
        {
            // Best effort per sweep.
        }
    }

    private void PurgeOldOnce()
    {
        if (_purged) return;
        PurgeOld();
    }
}
