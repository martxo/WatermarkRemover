using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Ocr;
using WatermarkRemover.App.Presentation;
using WatermarkRemover.Pdf.Rendering;
using WatermarkRemover.Ocr;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.Settings;
using WatermarkRemover.App.Updates;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Features.Naming;
using WatermarkRemover.Core.Features.Netdisk;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Features.Scan;
using WatermarkRemover.Core.Features.Search;
using WatermarkRemover.Core.Features.Transfer;
using WatermarkRemover.Core.Logging;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Core.Watermarks;
using WatermarkRemover.Index;
using WatermarkRemover.Pdf;

namespace WatermarkRemover.App.ViewModels;

public sealed partial class MainViewModel
{
    private OutputFileWatcher? _outputWatcher;

    /// <summary>外部删除时挂在行上的说明文本；对账与清除逻辑按它精确匹配。</summary>
    internal const string FileMissingNote = "文件已被移走或删除（记录保留）";

    /// <summary>
    /// Follows external file operations like a code editor follows renames: the record rows hold
    /// absolute paths, and without this a rename in Explorer turned every open/delete/upload on
    /// that row into a dead path. Headless hosts (journals not injected) never start it, so the
    /// test suite touches no real filesystem watchers.
    /// </summary>
    private void InitializeOutputWatcher()
    {
        if (_journal is null || _pendingJournal is null) return;

        _outputWatcher = new OutputFileWatcher(message => AppLog.Trace(message));
        _outputWatcher.FileRenamed += OnWatchedFileRenamed;
        _outputWatcher.FileDeleted += OnWatchedFileDeleted;
        _outputWatcher.FileCreated += OnWatchedFileCreated;
        Board.Jobs.CollectionChanged += (_, _) => RefreshWatchedDirectories();
        RefreshWatchedDirectories();

        // 启动对账：1.16.137 的替换误报可能在上一会话给行留下「已被移走」标记，而文件实际
        // 都在。恢复完成后按磁盘现状清掉这条残留。
        foreach (var job in Board.Jobs)
        {
            if (job.NamingNote == FileMissingNote &&
                ((job.OutputPath is { } o && File.Exists(o)) ||
                 (job.InputPath is { } i && File.Exists(i))))
            {
                job.SetNamingNote(null);
            }
        }

        AppLog.Info("文件监视已开启：资源管理器里的改名/删除会同步到记录行");
    }

    /// <summary>Distinct directories of every record's file. Records are few, so recompute the
    /// whole set on list changes instead of tracking per-row lifetimes.</summary>
    private void RefreshWatchedDirectories()
    {
        if (_outputWatcher is null) return;

        var dirs = new List<string>();
        foreach (var job in Board.Jobs)
        {
            AddWatchDir(dirs, job.OutputPath);
            AddWatchDir(dirs, job.InputPath);
        }

        _outputWatcher.SetDirectories(dirs);
    }

    private static void AddWatchDir(List<string> dirs, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        string? dir = Path.GetDirectoryName(path);
        if (dir is null || dirs.Contains(dir, StringComparer.OrdinalIgnoreCase)) return;
        dirs.Add(dir);
    }

    private void OnWatchedFileRenamed(string oldPath, string newPath) => RunOnUi(() =>
    {
        int updated = 0;
        foreach (var job in Board.Jobs.ToList())
        {
            bool input = Eq(job.InputPath, oldPath);
            bool output = Eq(job.OutputPath, oldPath);
            if (input || output)
            {
                job.ApplyExternalRename(newPath, renameInput: input, renameOutput: output);
                updated++;
                continue;
            }

            // 原子替换（预览条保存 = 临时名→记录路径）：新路径命中就说明文件回来了，
            // 清掉可能挂着的「已被移走或删除」误报标记。
            if (Eq(job.OutputPath, newPath) || Eq(job.InputPath, newPath))
                job.SetNamingNote(null);
        }

        // 继续扫描等未完成登记里存的是绝对路径：文件改名时登记路径必须同步更新，否则
        // 重启后「继续扫描」在旧路径上找不到文件。
        bool pendingTouched = false;
        for (int i = 0; i < _pending.Count; i++)
        {
            if (!Eq(_pending[i].InputPath, oldPath)) continue;
            _pending[i] = _pending[i] with { InputPath = newPath };
            pendingTouched = true;
        }
        if (pendingTouched) _pendingJournal?.Save(_pending);

        if (updated > 0)
        {
            ScheduleJournalSave();
            AppLog.Info($"外部改名已同步：{Path.GetFileName(oldPath)} → {Path.GetFileName(newPath)}" +
                        $"（{updated} 条记录）");
        }
        RefreshWatchedDirectories();
    });

    private void OnWatchedFileDeleted(string path) => RunOnUi(() =>
    {
        foreach (var job in Board.Jobs)
        {
            if (Eq(job.OutputPath, path) || Eq(job.InputPath, path))
            {
                // 预览条的编辑会写回记录路径：文件被外部删掉后，一条静默的持久说明
                // 比按钮点了才报错更早说清现状。文件重新出现时（created/renamed）清掉。
                job.SetNamingNote(FileMissingNote);
            }
        }
    });

    private void OnWatchedFileCreated(string path) => RunOnUi(() =>
    {
        foreach (var job in Board.Jobs)
        {
            if ((Eq(job.OutputPath, path) || Eq(job.InputPath, path)) && File.Exists(path))
                job.SetNamingNote(null);
        }
    });

    private static bool Eq(string? left, string? right) => string.Equals(
        left, right, StringComparison.OrdinalIgnoreCase);

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
