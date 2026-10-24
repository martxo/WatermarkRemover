using System.IO;
using System.Text.Json;
using WatermarkRemover.App.Logging;

namespace WatermarkRemover.App.Services;

/// <summary>
/// One finished job as written to the journal: enough to rebuild the row after a restart — which
/// page it belongs to, which file was produced, when, and whether it reached the netdisk.
/// </summary>
public sealed record TaskJournalEntry(
    string SectionId,
    string InputPath,
    string OutputPath,
    string Summary,
    int ItemCount,
    DateTime ProcessedAt,
    string UploadState,
    string? UploadNote);

/// <summary>
/// On-disk record of finished jobs. The job list itself lives in memory only, so closing the app
/// used to erase the answer to 「今天扫了哪些、传没传」. This keeps the newest
/// <see cref="MaxEntries"/> rows in one small JSON file; the UI restores them at startup.
/// A broken file is ignored: losing the history must never block the app from starting.
/// </summary>
public sealed class TaskJournal
{
    /// <summary>Newest-first ceiling: a normal week fits, and loading it costs nothing.</summary>
    public const int MaxEntries = 200;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    private readonly object _sync = new();

    public TaskJournal(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = directoryPath;
    }

    public string DirectoryPath { get; }

    public string FilePath => Path.Combine(DirectoryPath, "tasks.json");

    public IReadOnlyList<TaskJournalEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return Array.Empty<TaskJournalEntry>();

            var entries = JsonSerializer.Deserialize<List<TaskJournalEntry>>(File.ReadAllText(FilePath), Options);
            return entries is null ? Array.Empty<TaskJournalEntry>() : entries;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"任务记录不可读，已忽略：{FilePath}（{ex.Message}）");
            return Array.Empty<TaskJournalEntry>();
        }
    }

    /// <summary>Rewrites the journal with the newest <see cref="MaxEntries"/> entries. Atomic
    /// (temp + move) so a crash mid-write cannot leave half a JSON file behind.</summary>
    public bool Save(IEnumerable<TaskJournalEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var newest = entries
            .OrderByDescending(e => e.ProcessedAt)
            .Take(MaxEntries)
            .ToList();

        lock (_sync)
        {
            try
            {
                System.IO.Directory.CreateDirectory(DirectoryPath);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(newest, Options));
                File.Move(temp, FilePath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"任务记录保存失败：{FilePath}（{ex.Message}）");
                return false;
            }
        }
    }
}
