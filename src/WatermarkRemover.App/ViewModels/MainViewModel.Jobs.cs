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
    /// <summary>Footer gate for 「上传本页全部」: an account exists and something is left to send.</summary>
    public bool CanUploadBatch => Netdisk.HasAccounts && Board.Jobs.Any(IsUploadCandidate);

    public string UploadBatchHint => Netdisk.HasAccounts
        ? $"把本页已完成的文件上传到「{Netdisk.PrimaryAccountName}」"
        : "先在「设置 → 网盘设置」中添加网盘账号";

    /// <summary>A row the batch upload may take: finished, not uploaded yet, file still on disk.</summary>
    private bool IsUploadCandidate(FileJobViewModel job) =>
        job.OwnerFeatureId == _selectedFeature.Id
        && job.State == JobState.Succeeded
        && job.Upload is not (UploadState.Uploaded or UploadState.Uploading)
        && job.OutputPath is not null
        && File.Exists(job.OutputPath);
    // --- 任务记录（对账） ----------------------------------------------

    /// <summary>Set while the journal is being read back so the row inserts of the restore pass
    /// do not each trigger a write of the same partial snapshot.</summary>
    private bool _restoringJournal;

    /// <summary>Connects a fresh row to the host: rename / chips are actions, and the three
    /// journal-relevant properties keep the on-disk record in step with the list.</summary>
    private void WireJob(FileJobViewModel job)
    {
        job.RenameRequested += OnJobRenameRequested;
        job.SuffixToggleRequested += OnJobSuffixToggleRequested;
        job.UploadRequested += OnJobUploadRequested;
        job.RetryRequested += OnJobRetryRequested;
        job.ContinueScanRequested += OnJobContinueScanRequested;
        job.PinRequested += (_, _) => OnScreenshotPinRequested(job);
        job.OcrRequested += (_, _) => OnJobOcrRequested(job);
        job.ViewRequested += (_, _) => OnScreenshotViewRequested(job);
        // 行里的文件被改名/改名后移动时，未完成记录也要跟着换路径；否则重启后它会指向一个
        // 已经不存在的路径，扫描就白登记了。这里按行跟踪前后路径，覆盖命名、手动改名和
        // 预览自己另存的副本三种改名来源。
        string? trackedPath = job.OutputPath;
        job.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(FileJobViewModel.OutputPath)) return;
            FollowPendingRename(trackedPath, job.OutputPath);
            trackedPath = job.OutputPath;
        };
        job.PropertyChanged += OnJobPropertyChanged;
    }

    /// <summary>Keeps the unfinished record on the same document after a rename or move.</summary>
    private void FollowPendingRename(string? from, string? to)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to)) return;
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return;

        bool changed = false;
        for (int i = 0; i < _pending.Count; i++)
        {
            if (!string.Equals(_pending[i].InputPath, from, StringComparison.OrdinalIgnoreCase)) continue;
            _pending[i] = _pending[i] with { InputPath = to };
            changed = true;
        }

        if (!changed) return;
        _pendingJournal?.Save(_pending);
        RaisePendingChanged();
    }

    private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(FileJobViewModel.State)
            or nameof(FileJobViewModel.OutputPath)
            or nameof(FileJobViewModel.Upload))
        {
            ScheduleJournalSave();
            // A row just finished (or was uploaded): the footer batch gate changes with it.
            OnPropertyChanged(nameof(CanUploadBatch));
        }
    }

    /// <summary>
    /// Reads the journal back into the board: after a restart the list still answers 「今天扫了哪些、
    /// 传没传」. Rows whose file is gone are dropped — a record that cannot be opened, renamed or
    /// uploaded only misleads.
    /// </summary>
    private void RestoreJournal()
    {
        if (_journal is null) return;

        var knownSections = _composition.Registry.Features.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var restored = new List<FileJobViewModel>();

        _restoringJournal = true;
        try
        {
            foreach (var entry in _journal.Load().OrderBy(e => e.ProcessedAt))
            {
                // 「PDF 工具」合并后 ocr.text 不再是独立 section：历史记录迁入工作台，否则会被
                // 当作未知 section 整批丢弃。
                string sectionId = entry.SectionId == "ocr.text" ? "watermark.badge" : entry.SectionId;
                if (!knownSections.Contains(sectionId)) continue;
                if (string.IsNullOrWhiteSpace(entry.OutputPath) || !File.Exists(entry.OutputPath))
                    continue;

                var job = new FileJobViewModel(entry.InputPath, Settings.QuickSuffixes)
                {
                    OwnerFeatureId = sectionId,
                };
                WireJob(job);

                job.Restore(
                    entry.InputPath, entry.OutputPath, entry.Summary, entry.ItemCount, entry.ProcessedAt);
                job.SetUploadState(
                    Enum.TryParse<UploadState>(entry.UploadState, out var upload) ? upload : UploadState.None,
                    entry.UploadNote);
                restored.Add(job);
            }

            Board.Restore(restored);
        }
        finally
        {
            _restoringJournal = false;
        }

        if (restored.Count > 0)
            AppLog.Info($"任务记录：恢复 {restored.Count} 条已完成记录");
        OnPropertyChanged(nameof(CanUploadBatch));
    }

    /// <summary>Writes the journal after the current change; cheap enough to run per record.</summary>
    private void ScheduleJournalSave()
    {
        if (_journal is null || _restoringJournal) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            SaveJournal();
            return;
        }

        // Worker threads finish in bursts: queue one save and let it snapshot the latest list.
        if (Interlocked.Exchange(ref _journalSaveQueued, 1) == 1) return;
        dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _journalSaveQueued, 0);
            SaveJournal();
        });
    }

    /// <summary>Snapshots every finished row (upload state included) — UI thread only.</summary>
    private void SaveJournal()
    {
        if (_journal is null) return;

        var entries = Board.Jobs
            .Where(j => j.State == JobState.Succeeded && !string.IsNullOrEmpty(j.OutputPath))
            .Select(j => j.ToJournalEntry())
            .ToList();

        if (!_journal.Save(entries)) return;
        AppLog.Trace($"任务记录：写入 {entries.Count} 条", null);
    }
    private void OnJobRenameRequested(object? sender, string desiredName)
    {
        if (sender is FileJobViewModel job) _renamer.Rename(job, desiredName);
    }

    /// <summary>行上的「识别文字」：截图行走结果弹窗流程，「PDF 工具」行就地跑 OCR 管线。</summary>
    private void OnJobOcrRequested(FileJobViewModel job)
    {
        if (job.OwnerFeatureId == "screenshot.page") OnScreenshotOcrRequested(job);
        else _ = RunRowOcrAsync(job);
    }

    private void OnJobSuffixToggleRequested(object? sender, string suffix)
    {
        if (sender is FileJobViewModel job) _renamer.ApplySuffix(job, suffix);
    }
    // --- 上传到网盘 ----------------------------------------------------

    private void OnJobUploadRequested(object? sender, EventArgs e)
    {
        if (sender is FileJobViewModel job) _ = UploadJobAsync(job);
    }

    /// <summary>One row → netdisk. Every outcome lands as a row state + note; the click never
    /// dies silently, even with no account configured.</summary>
    private async Task UploadJobAsync(FileJobViewModel job)
    {
        if (job.OutputPath is null || !File.Exists(job.OutputPath))
        {
            job.SetUploadState(UploadState.Failed, "文件已不存在，无法上传");
            return;
        }

        if (!Netdisk.HasAccounts)
        {
            _dialogs.Warn("还没有配置网盘账号。请先在「设置 → 网盘设置」中添加一个账号。", "上传到网盘");
            return;
        }

        job.SetUploadState(UploadState.Uploading);
        RecordUploadPending(new[] { job.OutputPath! });
        var failures = await Netdisk.UploadFilesAsync(new[] { job.OutputPath });
        ApplyUploadResult(new[] { job }, failures);
    }

    /// <summary>Footer 「上传本页全部」: one connection for every candidate row of the section.</summary>
    private async Task UploadVisibleAsync()
    {
        if (!Netdisk.HasAccounts)
        {
            _dialogs.Warn("还没有配置网盘账号。请先在「设置 → 网盘设置」中添加一个账号。", "上传到网盘");
            return;
        }

        var targets = Board.Jobs.Where(IsUploadCandidate).ToList();
        if (targets.Count == 0) return;

        foreach (var job in targets) job.SetUploadState(UploadState.Uploading);
        RecordUploadPending(targets.Select(j => j.OutputPath!).ToList());

        IReadOnlyList<(string Path, string Error)> failures;
        try
        {
            failures = await Netdisk.UploadFilesAsync(targets.Select(j => j.OutputPath!).ToList());
        }
        catch (Exception ex)
        {
            // The coordinator reports per-file failures; a throw here is unexpected, and a row
            // stuck in 「上传中…」 would never recover, so fail them all explicitly.
            AppLog.Error("批量上传异常", ex);
            failures = targets.Select(j => (j.OutputPath!, ex.Message)).ToList();
        }

        ApplyUploadResult(targets, failures);

        if (failures.Count == 0)
            _dialogs.Info($"已上传 {targets.Count} 个文件到「{Netdisk.PrimaryAccountName}」。", "上传到网盘");
        else
            _dialogs.Warn(
                $"{failures.Count} 个文件上传失败（第一个：{failures[0].Error}）。\n\n" +
                "失败的文件保留了「上传失败」标记，可单独点击重试。", "上传到网盘");
    }

    private void ApplyUploadResult(
        IReadOnlyList<FileJobViewModel> jobs, IReadOnlyList<(string Path, string Error)> failures)
    {
        var failed = failures.ToDictionary(f => f.Path, f => f.Error, StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            if (job.OutputPath is not null && failed.TryGetValue(job.OutputPath, out string? error))
            {
                job.SetUploadState(UploadState.Failed, error);
                AppLog.Warn($"网盘上传失败：{job.OutputPath}（{error}）");
            }
            else
            {
                job.SetUploadState(UploadState.Uploaded, $"已上传到「{Netdisk.PrimaryAccountName}」");
                AppLog.Info($"网盘上传成功：{job.OutputPath}");
                if (job.OutputPath is { } uploaded) ClearPending(uploaded);
            }
        }

        ScheduleJournalSave();
        OnPropertyChanged(nameof(CanUploadBatch));
    }

    // --- 任务中心（未完成任务 / 停止 / 继续） --------------------------

    /// <summary>Reads the unfinished-file record back at startup; missing files are dropped.</summary>
    private void RestorePending()
    {
        if (_pendingJournal is null) return;

        _pending.Clear();
        foreach (var loaded in _pendingJournal.Load())
        {
            // 「PDF 工具」合并：ocr.text 登记迁入工作台 section，未完成记录才不会丢。
            var entry = loaded.SectionId == "ocr.text"
                ? loaded with { SectionId = "watermark.badge" }
                : loaded;
            if (!File.Exists(entry.InputPath)) continue;
            if (!_composition.Registry.Features.Any(f => f.Id == entry.SectionId)) continue;
            _pending.Add(entry);
        }

        // 扫描的中断记录不进横幅：它的承接方式是「扫描」页里那行记录上的「继续扫描」，
        // 所以先把这些文档恢复成可继续的行，后续由行自己负责追加。
        RestoreScanRows();

        _pendingJournal.Save(_pending);
        RaisePendingChanged();
        if (UnfinishedTaskCount > 0)
            AppLog.Info($"任务中心：{UnfinishedTaskCount} 个文件在退出时未处理完，可继续");
    }

    /// <summary>
    /// 重启后把「扫描中断、还有原稿没扫完」的文档恢复成可继续的行：优先复用任务记录里已经恢复
    /// 出来的那一行（同一份 PDF），记录被清过就地补一行。这样关掉应用再打开，「继续扫描」还在。
    /// </summary>
    private void RestoreScanRows()
    {
        foreach (var entry in _pending.Where(IsScanEntry).ToList())
        {
            FileJobViewModel? job = Board.Jobs.FirstOrDefault(j =>
                j.OwnerFeatureId == entry.SectionId &&
                j.OutputPath is { } output &&
                string.Equals(output, entry.InputPath, StringComparison.OrdinalIgnoreCase));

            if (job is null)
            {
                job = new FileJobViewModel(entry.InputPath, Settings.QuickSuffixes)
                {
                    OwnerFeatureId = entry.SectionId,
                };
                WireJob(job);
                job.Restore(entry.InputPath, entry.InputPath, "扫描中断", 0, entry.QueuedAt);
                Board.Add(job);
                Board.MarkCompleted(job);
            }

            job.AllowContinueScan();
            job.ReportContinuation(
                $"上次扫描中断，已保存的页都在 {Path.GetFileName(entry.InputPath)} 里；" +
                "点「继续扫描」把剩余原稿追加进来");
            AppLog.Info($"任务中心：扫描文档可继续 → {entry.InputPath}");
        }
    }

    /// <summary>
    /// 扫描中断/取消但保留了页面：把这份半成品登记到未完成任务里，关掉应用再打开时
    /// 「继续扫描」仍然可用。同一份文档只留一条最新记录。
    /// </summary>
    private void RecordScanPending(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        _pending.RemoveAll(e => IsScanEntry(e) &&
            string.Equals(e.InputPath, path, StringComparison.OrdinalIgnoreCase));
        _pending.Add(new PendingTaskEntry("scan.page", path, DateTime.Now) { Kind = "scan" });
        _pendingJournal?.Save(_pending);
        RaisePendingChanged();
        AppLog.Info($"任务中心：扫描中断记录已保存，可跨重启继续 → {path}");
    }

    private static bool IsScanEntry(PendingTaskEntry entry) =>
        string.Equals(entry.Kind, "scan", StringComparison.OrdinalIgnoreCase);

    /// <summary>横幅负责的未完成文件数；扫描文档由它自己的记录行承接，不计入这里。</summary>
    private int UnfinishedTaskCount => _pending.Count(e => !IsScanEntry(e));

    /// <summary>Snapshots the batch into the pending file before the first job starts.</summary>
    private void RecordPending(IEnumerable<FileJobViewModel> jobs, string kind = "process")
    {
        var batch = jobs.ToList();
        var paths = batch.Select(j => j.InputPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _pending.RemoveAll(e => paths.Contains(e.InputPath));
        _pending.AddRange(batch.Select(j => new PendingTaskEntry(
            j.OwnerFeatureId ?? SelectedFeature.Id, j.InputPath, DateTime.Now) { Kind = kind }));

        _pendingJournal?.Save(_pending);
        RaisePendingChanged();
    }

    /// <summary>Marks the files of an upload batch, so an interrupted upload can be continued.</summary>
    private void RecordUploadPending(IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (list.Count == 0) return;

        var set = list.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _pending.RemoveAll(e => set.Contains(e.InputPath));
        _pending.AddRange(list.Select(p => new PendingTaskEntry(
            SelectedFeature.Id, p, DateTime.Now) { Kind = "upload" }));

        _pendingJournal?.Save(_pending);
        RaisePendingChanged();
    }

    /// <summary>One file finished (or was stopped): it no longer belongs to the unfinished set.</summary>
    private void ClearPending(string path)
    {
        // Called from the queue's worker threads: the list and the banner are UI state.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ClearPending(path));
            return;
        }

        if (_pending.RemoveAll(e => string.Equals(
                e.InputPath, path, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return;
        }

        _pendingJournal?.Save(_pending);
        RaisePendingChanged();
    }

    /// <summary>Stops the running batch; only files that have not started are cancelled.</summary>
    private void StopBatch()
    {
        if (_batchCancellation is null || _isStopping) return;

        _isStopping = true;
        StopBatchCommand.RaiseCanExecuteChanged();
        AppLog.Info("任务中心：用户停止当前批次，未开始的文件标记为已停止");
        _batchCancellation.Cancel();
    }

    /// <summary>The single stop button: a running scan first, then a running batch.</summary>
    private void StopCurrent()
    {
        if (IsScanning)
        {
            _scanLoopStopped = true;
            _scan.CancelScan();
        }

        if (_batchCancellation is not null) StopBatch();
    }

    /// <summary>Drops the interrupted-run record without touching the files. 扫描文档的那条
    /// 记录跟着它自己的行走，不在这里被一并丢掉。</summary>
    private void DiscardPending()
    {
        int count = UnfinishedTaskCount;
        if (count == 0) return;

        AppLog.Info($"任务中心：丢弃 {count} 个未完成任务记录");
        _pending.RemoveAll(e => !IsScanEntry(e));
        _pendingJournal?.Save(_pending);
        RaisePendingChanged();
    }

    /// <summary>Processes every file the last run left behind, section by section.</summary>
    private async Task ResumePendingAsync()
    {
        if (IsBusy || _pending.Count == 0) return;

        await ResumeUploadsAsync(_pending.Where(e => e.Kind == "upload").ToList()).ConfigureAwait(true);

        // 扫描文档不能被"重新处理"：它等的是一轮新扫描，承接入口在它自己的行上。
        foreach (var group in _pending
            .Where(e => e.Kind != "upload" && !IsScanEntry(e))
            .GroupBy(e => e.SectionId).ToList())
        {
            IFeature? feature = _composition.Registry.Features.FirstOrDefault(f => f.Id == group.Key);
            if (feature is null) continue;

            var jobs = new List<FileJobViewModel>();
            foreach (var entry in group)
            {
                if (!File.Exists(entry.InputPath)) continue;

                var job = new FileJobViewModel(entry.InputPath, Settings.QuickSuffixes)
                {
                    OwnerFeatureId = feature.Id,
                };
                WireJob(job);
                Board.Add(job);
                jobs.Add(job);
            }

            if (jobs.Count == 0) continue;
            await ProcessJobsAsync(jobs, feature).ConfigureAwait(true);
        }

        // Files deleted while the app was closed fall out here.
        _pending.RemoveAll(e => !File.Exists(e.InputPath));
        _pendingJournal?.Save(_pending);
        RaisePendingChanged();
    }

    /// <summary>Continues the uploads an interrupted run left behind; failures stay pending.</summary>
    private async Task ResumeUploadsAsync(IReadOnlyList<PendingTaskEntry> entries)
    {
        var paths = entries.Select(e => e.InputPath).Where(File.Exists).ToList();
        if (paths.Count == 0) return;

        if (!Netdisk.HasAccounts)
        {
            _dialogs.Warn(
                "上次还有文件没有上传完成，但当前没有配置网盘账号。请在「设置 → 网盘设置」添加账号后再继续。",
                "继续上传");
            return;
        }

        IsBusy = true;
        try
        {
            var failures = await Netdisk.UploadFilesAsync(paths).ConfigureAwait(true);
            foreach (string path in paths)
            {
                bool failed = failures.Any(f =>
                    string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
                if (!failed) ClearPending(path);
            }

            AppLog.Info($"任务中心：继续上传 {paths.Count} 个文件，失败 {failures.Count} 个");
        }
        catch (Exception ex)
        {
            AppLog.Error("任务中心：继续上传失败", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RaisePendingChanged()
    {
        OnPropertyChanged(nameof(HasPendingTasks));
        OnPropertyChanged(nameof(PendingTaskCount));
        OnPropertyChanged(nameof(PendingTasksText));
        OnPropertyChanged(nameof(ShowPendingBanner));
        ResumePendingCommand.RaiseCanExecuteChanged();
        DiscardPendingCommand.RaiseCanExecuteChanged();
    }
}

