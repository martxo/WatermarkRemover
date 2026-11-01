using System.IO;
using System.Text.Json;
using WatermarkRemover.App.Logging;

namespace WatermarkRemover.App.Services;

/// <summary>One file that was queued but not finished when the app stopped.</summary>
public sealed record PendingTaskEntry(string SectionId, string InputPath, DateTime QueuedAt)
{
    /// <summary>"process" = 本地处理；"upload" = 上传网盘；"scan" = 扫描中断、还有原稿没扫完。
    /// 旧文件没有这个字段，按处理对待。</summary>
    public string Kind { get; init; } = "process";
}

/// <summary>
/// The unfinished half of the job list. The history journal only keeps <em>finished</em> rows, so a
/// batch that was interrupted (crash, update, user exit mid-batch) simply disappeared and the user
/// had to re-drop the files. This file is written when a batch starts and shrinks as each row
/// finishes; at startup the remainder is offered as 「继续处理」.
/// Atomic (temp + move) and fault tolerant: an unreadable file must never block startup.
/// </summary>
public sealed class PendingTaskJournal
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    private readonly object _sync = new();

    public PendingTaskJournal(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = directoryPath;
    }

    public string DirectoryPath { get; }

    public string FilePath => Path.Combine(DirectoryPath, "pending.json");

    public IReadOnlyList<PendingTaskEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return Array.Empty<PendingTaskEntry>();

            var entries = JsonSerializer.Deserialize<List<PendingTaskEntry>>(
                File.ReadAllText(FilePath), Options);
            return entries is null ? Array.Empty<PendingTaskEntry>() : entries;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"未完成任务记录不可读，已忽略：{FilePath}（{ex.Message}）");
            return Array.Empty<PendingTaskEntry>();
        }
    }

    /// <summary>Replaces the file with <paramref name="entries"/>; an empty list removes it.</summary>
    public bool Save(IEnumerable<PendingTaskEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var list = entries.ToList();
        lock (_sync)
        {
            try
            {
                if (list.Count == 0)
                {
                    if (File.Exists(FilePath)) File.Delete(FilePath);
                    return true;
                }

                Directory.CreateDirectory(DirectoryPath);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(list, Options));
                File.Move(temp, FilePath, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"未完成任务记录保存失败：{FilePath}（{ex.Message}）");
                return false;
            }
        }
    }
}
