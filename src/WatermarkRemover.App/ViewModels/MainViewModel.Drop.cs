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
    private DropZoneState _dropZoneState = DropZoneState.Idle;
    private string? _rejectHint;

    /// <summary>Current drop-zone state; the XAML binds every visual aspect (outline, tint,
    /// icon, text color) to this, so drag handlers never touch controls directly.</summary>
    public DropZoneState DropZoneState
    {
        get => _dropZoneState;
        private set
        {
            if (SetProperty(ref _dropZoneState, value))
            {
                OnPropertyChanged(nameof(DropHint));
                OnPropertyChanged(nameof(DropZoneIcon));
            }
        }
    }

    /// <summary>Upload glyph for idle/accept; switches to the cancel glyph when rejecting.</summary>
    public string DropZoneIcon => DropZoneState == DropZoneState.Reject ? "\uE711" : "\uE74C";

    /// <summary>State-aware hint; always derives from the selected feature so newly registered
    /// features get the right prompt without touching the view.</summary>
    public string DropHint => DropZoneState switch
    {
        DropZoneState.Accept => $"松开鼠标，立即开始「{SelectedFeature.DisplayName}」",
        DropZoneState.Reject => _rejectHint ?? $"「{SelectedFeature.DisplayName}」不支持这些文件",
        _ => $"把文件拖到这里，松手即开始「{SelectedFeature.DisplayName}」",
    };

    /// <summary>Whether a dropped path is usable: a file the feature handles, or a folder that
    /// may contain some (expanded one level deep on drop).</summary>
    public bool CanAcceptPath(string path) =>
        Directory.Exists(path) || SelectedFeature.CanHandle(path);

    /// <summary>Open-file dialog filter built from what the current feature accepts, so the
    /// picker never offers a format the page would reject (OCR takes photos, watermark does not).</summary>
    public string FilePickerFilter
    {
        get
        {
            IReadOnlyList<string> extensions = SelectedFeature.InputExtensions;
            if (extensions.Count == 0) return "PDF 文件 (*.pdf)|*.pdf";

            string patterns = string.Join(";", extensions.Select(extension => "*" + extension));
            string label = extensions.Count > 1 ? "支持的文件" : "PDF 文件";
            return $"{label} ({patterns})|{patterns}";
        }
    }

    /// <summary>Dialog title matching the filter width.</summary>
    public string FilePickerTitle =>
        SelectedFeature.InputExtensions.Count > 1 ? "选择文件" : "选择 PDF 文件";

    /// <summary>Called by the view on every drag heartbeat. Null = the payload carries no files
    /// at all; otherwise accept when any file matches the selected feature's CanHandle.</summary>
    public void NotifyDragOver(string[]? files)
    {
        if (IsBusy)
        {
            _rejectHint = "正在处理文件，完成后再添加";
            DropZoneState = DropZoneState.Reject;
        }
        else if (files is null)
        {
            _rejectHint = "这里只能拖入文件或文件夹";
            DropZoneState = DropZoneState.Reject;
        }
        else if (files.Any(CanAcceptPath))
        {
            _rejectHint = null;
            DropZoneState = DropZoneState.Accept;
        }
        else
        {
            _rejectHint = $"「{SelectedFeature.DisplayName}」不支持这些文件";
            DropZoneState = DropZoneState.Reject;
        }
    }

    /// <summary>Back to idle when the drag leaves the window or finishes.</summary>
    public void NotifyDragLeave()
    {
        _rejectHint = null;
        DropZoneState = DropZoneState.Idle;
    }

    /// <summary>A folder drop expands one level deep; this ceiling keeps a whole drive from being
    /// swept into the queue by accident.</summary>
    private const int MaxDropFiles = 500;

    /// <summary>Entry point for drag-and-drop and the file picker.</summary>
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        if (IsBusy)
        {
            // Silently swallowing the drop read as "the app is broken"; say why instead.
            _dialogs.Warn("正在处理文件，请等待当前任务完成后再添加。", "请稍候");
            return;
        }

        string[] input = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        (string[] files, int ignored) = ExpandDrop(input);

        if (files.Length == 0)
        {
            // Only worth a prompt when the user dropped something that needed expanding —
            // an unsupported file was already rejected by the drop zone itself.
            if (input.Any(Directory.Exists))
                _dialogs.Info("这个文件夹里没有可处理的文件（只读取第一层）。", "提示");
            else if (input.Any(File.Exists))
                _dialogs.Info($"「{SelectedFeature.DisplayName}」不支持所拖入的文件。", "提示");
            return;
        }

        // A mixed drop used to swallow the rejects without a word; one line accounts for them.
        if (ignored > 0)
            _dialogs.Info(
                $"已忽略 {ignored} 个不支持「{SelectedFeature.DisplayName}」的文件，" +
                $"其余 {files.Length} 个已加入列表。",
                "提示");

        AppLog.Info($"收到 {files.Length} 个待处理文件");
        string previewNames = string.Join("、", files.Take(20).Select(Path.GetFileName));
        AppLog.Debug(files.Length > 20
            ? $"待处理文件：{previewNames}…等 {files.Length} 个"
            : $"待处理文件：{previewNames}");

        var newJobs = files.Select(f =>
        {
            var job = new FileJobViewModel(f, Settings.QuickSuffixes)
            {
                OwnerFeatureId = SelectedFeature.Id,
            };
            WireJob(job);
            return job;
        }).ToList();
        foreach (var job in newJobs) Board.Add(job);
        Board.ResetCompleted();

        await ProcessJobsAsync(newJobs, SelectedFeature).ConfigureAwait(true);
    }

    /// <summary>Expands dropped folders one level and keeps only what the selected feature takes.
    /// Mixed drops are normal, so the count of explicitly dropped files that were rejected comes
    /// back with the result — the caller can say how many were skipped instead of dropping them
    /// silently. Files filtered out of a whole folder are ordinary and not counted.</summary>
    private (string[] Files, int Ignored) ExpandDrop(IEnumerable<string> paths)
    {
        var files = new List<string>();
        int ignored = 0;
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    files.AddRange(Directory.EnumerateFiles(path)
                        .Where(SelectedFeature.CanHandle)
                        .Take(MaxDropFiles));
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"展开文件夹失败：{path}（{ex.Message}）");
                }
            }
            else if (File.Exists(path))
            {
                if (SelectedFeature.CanHandle(path)) files.Add(path);
                else ignored++;
            }
        }

        return (files.Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxDropFiles).ToArray(), ignored);
    }

    private async Task ProcessJobsAsync(IReadOnlyList<FileJobViewModel> jobs, IFeature feature)
    {
        IsBusy = true;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Snapshot once per batch: background threads read this copy, the settings window
            // keeps editing the live object — the two can never interfere.
            var context = FeatureContext.Capture(Settings, (inputPath, stage) =>
            {
                // 工作线程回调：把阶段文案路由到属主行，行内再 marshal 回 UI 线程。
                jobs.FirstOrDefault(j => string.Equals(j.InputPath, inputPath, StringComparison.OrdinalIgnoreCase))
                    ?.ReportStage(stage);
            });
            int maxConcurrency = Math.Clamp(Settings.MaxConcurrency, 1, 8);
            AppLog.Info($"批次处理开始：{jobs.Count} 个文件，并发 {maxConcurrency}");

            // 只让这一批里最后完成的行展开缩略条：批量处理时若每份都展开，会为每一份各渲染
            // 一遍页面再被下一份折叠，白烧 CPU。最后一份正是用户要看的结果，其余保持折叠。
            int pendingRows = jobs.Count;
            await _queue.RunAsync(jobs, feature, context, maxConcurrency, job =>
            {
                Board.MarkCompleted(job);
                if (Interlocked.Decrement(ref pendingRows) == 0) _preview.OpenWhenFinished(job);
            }).ConfigureAwait(true);

            AppLog.Info($"批次处理结束：{jobs.Count} 个文件，并发 {maxConcurrency}，" +
                        $"总耗时 {stopwatch.ElapsedMilliseconds} ms");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
