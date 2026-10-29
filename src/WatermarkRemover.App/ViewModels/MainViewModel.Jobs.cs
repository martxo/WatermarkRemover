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
        job.PropertyChanged += OnJobPropertyChanged;
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
                if (!knownSections.Contains(entry.SectionId)) continue;
                if (string.IsNullOrWhiteSpace(entry.OutputPath) || !File.Exists(entry.OutputPath))
                    continue;

                var job = new FileJobViewModel(entry.InputPath, Settings.QuickSuffixes)
                {
                    OwnerFeatureId = entry.SectionId,
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
            }
        }

        ScheduleJournalSave();
        OnPropertyChanged(nameof(CanUploadBatch));
    }
}
