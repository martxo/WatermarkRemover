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
    /// <summary>Gap between documents in continuous mode — long enough to swap the sheet stack.</summary>
    private static readonly TimeSpan ContinuousScanPause = TimeSpan.FromMilliseconds(1200);

    /// <summary>Set by 「取消扫描」 and by quitting: stops the chain even while no session runs.</summary>
    private bool _scanLoopStopped;

    /// <summary>
    /// Runs one scan (or, in continuous mode, a chain of them) with the layout chosen on the scan
    /// page. The row exists from the start and updates per page (扫描中 · 已读 N 页); once the PDF
    /// lands the same row is adopted as the completed record, so rename / suffix chips / delete
    /// all keep working unchanged.
    /// </summary>
    private async Task RunScanAsync()
    {
        // IsScanning covers the whole chain including its pause, so it is the re-entry guard:
        // without it a second click could start a parallel loop on top of the productive one.
        if (IsBusy || IsScanning) return;

        _scanLoopStopped = false;
        // One IsScanning span covers the whole chain, not just a single pass: 「取消扫描」 then owns
        // the pause between documents too, and nothing else can grab the scanner in the gap.
        IsScanning = true;
        try
        {
            while (true)
            {
                if (!await RunScanPassAsync().ConfigureAwait(true)) return;
                if (!ContinuousScan || _scanLoopStopped) return;

                AppLog.Info("连续扫描：等待下一份原稿");
                await Task.Delay(ContinuousScanPause).ConfigureAwait(true);
                if (_scanLoopStopped) return;
            }
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>One scanner session; true means "the document landed, another may follow".</summary>
    private async Task<bool> RunScanPassAsync()
    {
        using var op = AppLog.BeginOperation(LogCategory.Scan, "扫描流程", slowMs: 0);

        // Stamped here so the naming pass can report scan→rename as one number when it ends.
        long chainTicks = Stopwatch.GetTimestamp();
        FileJobViewModel job = AddScanningJob();
        bool scanFinished = false;
        Task<AutoNameResult?>? earlyNaming = null;
        // Every scan message (connecting / opening session / reading page N) lands on the row
        // itself; that is the only status surface for a scan.
        var stageProgress = new Progress<string>(job.ReportStage);
        var pageProgress = new Progress<ScannedPageInfo>(page =>
        {
            job.AddLiveThumbnail(page.ThumbnailPng);
            AppLog.Debug($"扫描页到达：第 {page.PageNumber} 页，" +
                         $"{page.Width}×{page.Height}，缩略图 {page.ThumbnailPng.Length / 1024} KB");

            // Page 1 carries a naming-resolution image: OCR it now, while the scanner keeps
            // feeding the remaining pages, instead of after the finished PDF.
            if (!scanFinished && earlyNaming is null && page.NamingImage is { Length: > 0 } namingImage)
            {
                AppLog.Debug($"扫描页到达：第 {page.PageNumber} 页附带命名图，" +
                             $"{namingImage.Length / 1024} KB，开始首屏命名");
                earlyNaming = Task.Run(() => _naming.TrySuggestFromImageAsync(namingImage));
            }
        });

        ScanOutcome outcome;
        try
        {
            outcome = await _scan.ScanOnceAsync(stageProgress, pageProgress).ConfigureAwait(true);
        }
        catch (ScannerBusyException ex)
        {
            Board.Remove(job);
            op.Complete($"未开始：{ex.Message}");
            _dialogs.Info(ex.Message, "提示");
            return false;
        }
        finally
        {
            scanFinished = true;
        }

        op.Step("扫描设备", outcome.Success
            ? $"{outcome.PageCount} 页，来源 {outcome.SourceName}"
            : outcome.Error ?? "扫描失败");

        if (!outcome.Success)
        {
            if (outcome.Cancelled && outcome.PageCount == 0)
            {
                AppLog.Info("扫描已取消（未读取到页面）");
                Board.Remove(job);
                op.Complete("已取消，未读取到页面");
                return false;
            }

            if (ContinuousScan && outcome.PageCount == 0 && !outcome.Cancelled)
            {
                // An empty feeder is how a continuous batch normally ends — not a red failure.
                AppLog.Info("连续扫描结束：未读取到页面");
                Board.Remove(job);
                op.Complete("连续扫描结束：未读取到页面");
                _dialogs.Info("连续扫描结束：没有读取到页面。", "连续扫描");
                return false;
            }

            AppLog.Error($"扫描失败：{outcome.Error}");
            job.Apply(FeatureResult.FailWithDetails(
                job.InputPath, outcome.Error ?? "扫描失败", outcome.ErrorDetails));
            job.ClearLiveThumbnails();
            op.Fail(outcome.Error ?? "扫描失败");
            _dialogs.Error(outcome.Error ?? "扫描失败", "扫描失败");
            return false;
        }

        // The PDF is both input and output of the record; adopting the real path keeps the
        // row's file name (delete prompt, diagnostics) consistent with what landed on disk.
        string path = outcome.OutputPath!;
        job.AdoptSource(path);
        string summary = outcome.Cancelled
            ? $"已取消，保留 {outcome.PageCount} 页"
            : outcome.Interruption is { Length: > 0 }
                ? $"扫描中断，已保存 {outcome.PageCount} 页"
                : $"扫描 {outcome.PageCount} 页";
        job.Apply(FeatureResult.Success(path, path, summary, outcome.PageCount));
        // A device failure mid-document (paper jam, cable) used to lose every page already
        // pulled off the hardware. The partial PDF is saved now, and the row offers
        // 「继续扫描」 so the rest is appended to this same document instead of a new file.
        if (outcome.Interruption is { Length: > 0 } interruption)
        {
            job.AllowContinueScan();
            job.ReportContinuation(
                $"扫描中断（{interruption}），已保存 {outcome.PageCount} 页；点「继续扫描」接着扫");
            AppLog.Warn($"扫描中断但已保存 {outcome.PageCount} 页：{path}，原因：{interruption}");
            op.Step("扫描中断", $"已保存 {outcome.PageCount} 页，可继续扫描");
        }
        else if (outcome.Cancelled && outcome.PageCount > 0)
        {
            job.AllowContinueScan();
        }
        Board.MoveToTop(job);
        op.Step("接收落盘", $"{Path.GetFileName(path)}，{outcome.PageCount} 页");

        // The document exists but not where it was meant to: say so now, while the page count is
        // still in front of the user, instead of leaving a file silently stranded in %TEMP%.
        if (outcome.Warning is { Length: > 0 } warning)
        {
            AppLog.Warn($"扫描保存异常：{warning}");
            _dialogs.Warn(warning, "扫描保存");
        }

        // The scan that just landed unfolds its own strip, seeded with the pages shown live, so
        // the user sees the result without clicking 预览. Older rows fold (accordion), keeping
        // just one document's pixels resident.
        var livePages = job.LiveThumbnails.ToArray();
        _preview.Open(job, livePages);
        job.ClearLiveThumbnails();
        op.Step("记录已展开", $"{Path.GetFileName(path)}，{livePages.Length} 页缩略图");

        // Naming may already have finished from page 1 while the scan continued; the rename
        // still waits for the PDF (and the strip's writes) before moving the file.
        _ = RenameScanInBackgroundAsync(job, path, earlyNaming, chainTicks);
        op.Step("启动后台命名", earlyNaming is null ? "无首屏命名结果" : "复用首屏命名结果");

        op.Complete(summary);
        // 「取消扫描」 with pages kept means the user is done: never roll into another document.
        // An interrupted session is the same — the next sheet stack belongs on this same
        // document via 「继续扫描」, not a fresh one.
        return !outcome.Cancelled && outcome.Interruption is not { Length: > 0 };
    }

    /// <summary>A row that exists for the whole scan: live page count first, the finished PDF
    /// record once <see cref="FileJobViewModel.AdoptSource"/> hands it the real path.</summary>
    private FileJobViewModel AddScanningJob()
    {
        var job = new FileJobViewModel("扫描中", Settings.QuickSuffixes)
        {
            OwnerFeatureId = "scan.page",
        };
        WireJob(job);
        job.MarkProcessing("正在准备扫描…");
        Board.ResetCompleted();
        Board.Add(job);
        Board.MoveToTop(job);
        return job;
    }

    /// <summary>
    /// Fire-and-forget content naming for a fresh scan: the row is already visible under its
    /// timestamp name; when the chain lands a confident name the file moves and the row follows
    /// (the open strip re-finds its file by fingerprint when it next needs the path). Naming
    /// must never fail the scan — every exception lands in the log, the timestamp name stays.
    /// </summary>
    private async Task RenameScanInBackgroundAsync(
        FileJobViewModel job,
        string pdfPath,
        Task<AutoNameResult?>? earlyNaming = null,
        long chainTicks = 0)
    {
        using var op = AppLog.BeginOperation(
            LogCategory.Naming, "后台命名", Path.GetFileName(pdfPath));
        // The row says "完成" the moment the PDF lands, so naming needs its own line or the
        // work (and its failure) is completely invisible.
        bool namingOn = Settings.AutoNameByContent;
        if (namingOn) job.SetNamingNote("识别命名中…");
        try
        {
            // The open strip auto-orients / rewrites the file right after landing; a rename
            // (File.Move) during that write throws, so drain the strip's busy passes first.
            // The wait is another component's work, not naming time — keep it out of the
            // slow-operation verdict so a genuine ocr slowdown stays visible.
            long settleTicks = Stopwatch.GetTimestamp();
            await (job.Strip?.WaitForSettledAsync() ?? Task.CompletedTask).ConfigureAwait(false);
            op.Exclude((long)Stopwatch.GetElapsedTime(settleTicks).TotalMilliseconds);
            op.Step("等待预览稳定");

            // Use the page-1 suggestion when it produced one. Otherwise fall back to probing the
            // PDF — but not when page 1 was already probed and the settings pin naming to it.
            AutoNameResult? suggestion = earlyNaming is null
                ? null
                : await earlyNaming.ConfigureAwait(false);
            string finalPath;
            if (suggestion is not null)
            {
                finalPath = await _naming.ApplyAsync(pdfPath, suggestion).ConfigureAwait(false);
                op.Step("复用首屏命名", Path.GetFileName(finalPath));
            }
            else if (earlyNaming is not null && Settings.NameFromFirstPageOnly)
            {
                finalPath = pdfPath;
                op.Step("PDF 探测", "首屏已探测且设置仅用首页，跳过整页探测");
            }
            else
            {
                finalPath = await _naming.TryRenameAsync(pdfPath).ConfigureAwait(false);
                op.Step("PDF 探测", Path.GetFileName(finalPath));
            }

            if (string.Equals(finalPath, pdfPath, StringComparison.OrdinalIgnoreCase))
            {
                if (namingOn) job.SetNamingNote("未识别到标题，保留时间戳名");
                op.Complete("无更名");
                LogScanChain(chainTicks, finalPath, "保留时间戳名");
                return;
            }

            AppLog.Info($"后台命名完成：{Path.GetFileName(pdfPath)} → {Path.GetFileName(finalPath)}");
            op.Step("落盘", Path.GetFileName(finalPath));

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                job.SetNamingNote(null);
                op.Complete($"→ {Path.GetFileName(finalPath)}");
                LogScanChain(chainTicks, finalPath, "已改名");
                return;
            }

            dispatcher.Invoke(() =>
            {
                job.UpdateRenamed(finalPath);
                job.SetNamingNote(null);
            });
            op.Complete($"→ {Path.GetFileName(finalPath)}");
            LogScanChain(chainTicks, finalPath, "已改名");
        }
        catch (Exception ex)
        {
            AppLog.Trace("命名：后台自动命名异常，保留时间戳命名", ex);
            if (namingOn) job.SetNamingNote("命名失败，保留时间戳名");
            op.Fail("后台自动命名异常，保留时间戳命名", ex);
            LogScanChain(chainTicks, pdfPath, "命名失败，保留时间戳名");
        }
    }

    /// <summary>
    /// One line per finished document tying the three operations together — scan start → PDF on
    /// disk → name resolved. Their own log lines only share an op id, which is hard to read when
    /// a user reports "this scan took ten seconds even though each step looks fine".
    /// </summary>
    private static void LogScanChain(long chainTicks, string finalPath, string outcome)
    {
        if (chainTicks == 0) return;
        long totalMs = (long)Stopwatch.GetElapsedTime(chainTicks).TotalMilliseconds;
        AppLog.Info($"扫描链路：{Path.GetFileName(finalPath)}，{outcome}，" +
                    $"扫描开始到命名落盘共 {totalMs} ms");
    }
    /// <summary>Persists the scan layout choice immediately — the scan page has no save button.
    /// A no-op when the value already matches, so the initial radio sync never writes.</summary>
    public void ApplyScanDuplex(int duplex)
    {
        if (Settings.ScanDuplex == duplex) return;

        Settings.ScanDuplex = duplex;
        if (SettingsService.Save(Settings))
            AppLog.Info($"扫描版面已保存：{duplex}");
        else
            AppLog.Error("扫描版面保存失败（目录不可写？）");

        OnPropertyChanged(nameof(IsScanSimplex));
        OnPropertyChanged(nameof(IsScanDuplexLongEdge));
        OnPropertyChanged(nameof(IsScanDuplexShortEdge));
    }

    // Radio bindings for the scan strip (rendered from Page.Scan): selecting persists at once.
    public bool IsScanSimplex
    {
        get => Settings.ScanDuplex == 0;
        set { if (value) ApplyScanDuplex(0); }
    }

    public bool IsScanDuplexLongEdge
    {
        get => Settings.ScanDuplex == 1;
        set { if (value) ApplyScanDuplex(1); }
    }

    public bool IsScanDuplexShortEdge
    {
        get => Settings.ScanDuplex == 2;
        set { if (value) ApplyScanDuplex(2); }
    }

    /// <summary>连续扫描: chain documents without touching the button again. Edited in 设置 →
    /// 扫描 and persisted with the rest of that page; the scan loop always reads the live value.</summary>
    public bool ContinuousScan => Settings.ContinuousScan;
    /// <summary>A failed scan row's 「重新扫描」: drop the dead row and start a fresh session with
    /// the same settings — the file never existed, so there is nothing to reprocess.</summary>
    private void OnJobRetryRequested(object? sender, EventArgs e)
    {
        if (sender is not FileJobViewModel job || !IsIdle || IsScanning) return;

        AppLog.Info("扫描失败记录：重新扫描");
        Board.Remove(job);
        _ = RunScanAsync();
    }

    /// <summary>
    /// An interrupted scan row's 「继续扫描」: another session whose pages are appended to the
    /// row's existing PDF, so the document stays one file across a paper jam or a cancel.
    /// </summary>
    private void OnJobContinueScanRequested(object? sender, EventArgs e)
    {
        if (sender is not FileJobViewModel job) return;
        if (IsScanning)
        {
            job.ReportContinuation("扫描仪正在使用，等这次结束后再继续");
            return;
        }

        if (job.OutputPath is not { } path || !File.Exists(path))
        {
            job.ReportContinuation("原文件已被移动或删除，无法继续扫描");
            return;
        }
        _ = ContinueScanAsync(job, path);
    }

    /// <summary>
    /// One more scan pass for an existing record: the pages are appended to that record's PDF
    /// (one in-place save), the row's page total is refreshed, and the open strip grows in place.
    /// A pass that produces nothing leaves the document untouched and says why.
    /// </summary>
    private async Task ContinueScanAsync(FileJobViewModel job, string target)
    {
        using var op = AppLog.BeginOperation(LogCategory.Scan, "继续扫描", Path.GetFileName(target));

        IsScanning = true;
        try
        {
            ScanOutcome outcome;
            try
            {
                outcome = await _scan.ScanOnceAsync(
                    new Progress<string>(job.ReportContinuation)).ConfigureAwait(true);
            }
            catch (ScannerBusyException ex)
            {
                op.Complete($"未开始：{ex.Message}");
                job.ReportContinuation("扫描仪正忙，稍后再试");
                return;
            }

            if (!outcome.Success || string.IsNullOrEmpty(outcome.OutputPath))
            {
                string reason = outcome.Error ?? "没有读取到页面";
                op.Complete($"未追加：{reason}");
                job.ReportContinuation($"继续扫描失败（{reason}），原文件未改动");
                return;
            }

            string scanned = outcome.OutputPath;
            // The open strip may still be writing its own passes (blank cleanup, auto-orient);
            // appending under that write would race the file swap.
            if (job.Strip is { } settling)
                await settling.WaitForSettledAsync().ConfigureAwait(true);

            // Background naming may have moved the file while the scanner ran: append to where
            // the row's document actually is now, not the path captured at click time.
            if (job.OutputPath is { } current && File.Exists(current))
                target = current;
            if (!File.Exists(target))
            {
                op.Complete("未追加：原文件已被移动或删除");
                job.ReportContinuation(
                    $"原文件已被移动或删除，无法继续扫描。新扫的 {outcome.PageCount} 页在 {scanned}");
                return;
            }

            int added;
            bool savedAsCopy = false;
            try
            {
                PdfInsertResult insert = await Task.Run(
                    () => PdfPageInserter.AppendDetailed(target, scanned)).ConfigureAwait(true);
                added = insert.AddedPages;
                if (insert.SavedAsCopy)
                {
                    target = insert.PdfPath;
                    savedAsCopy = true;
                    // An open strip still points at the original; re-point it before refreshing.
                    job.Strip?.RelocateTo(target);
                }
            }
            catch (Exception ex)
            {
                op.Fail("追加扫描页失败", ex);
                // The freshly scanned pages still exist as their own file; say where they are
                // instead of silently dropping them.
                job.ReportContinuation(
                    $"追加失败：{ex.Message}。新扫的 {outcome.PageCount} 页在 {scanned}");
                return;
            }

            // The scan file's pages now live inside the target: remove the intermediate so the
            // output folder does not collect half-documents.
            try { File.Delete(scanned); }
            catch (IOException) { /* a leftover file beats losing pages */ }

            op.Step("追加页面", $"新增 {added} 页 → {Path.GetFileName(target)}");

            int total = await _composition.PdfRenderer.GetPageCountAsync(target).ConfigureAwait(true);
            string summary = outcome.Cancelled
                ? $"已取消，共保留 {total} 页"
                : outcome.Interruption is { Length: > 0 }
                    ? $"扫描中断，已保存 {total} 页"
                    : $"扫描 {total} 页";
            if (savedAsCopy)
            {
                summary += $"；原文件被其他程序占用，已另存为 {Path.GetFileName(target)}";
            }

            job.Apply(FeatureResult.Success(target, target, summary, total));
            Board.MoveToTop(job);
            ScheduleJournalSave();

            // The row is already finished, so Apply's State setter did not notify listeners; the
            // status line is this pass's own surface.
            if (outcome.Interruption is { Length: > 0 } interruption)
            {
                job.ReportContinuation(
                    $"又中断（{interruption}），已保存 {total} 页；可再点「继续扫描」");
                op.Complete($"中断，共 {total} 页");
            }
            else if (outcome.Cancelled)
            {
                job.ReportContinuation($"已取消，已保存 {total} 页；可再点「继续扫描」接着扫");
                op.Complete($"已取消，共 {total} 页");
            }
            else
            {
                job.ReportContinuation($"已续扫 {added} 页，共 {total} 页");
                op.Complete($"新增 {added} 页，共 {total} 页");
            }

            if (job.Strip is { } strip)
            {
                try
                {
                    await strip.AppendScannedPagesAsync(added).ConfigureAwait(true);
                    op.Step("刷新预览", $"新增 {added} 页");
                }
                catch (Exception ex)
                {
                    // The file is already correct; only the on-screen strip lags behind.
                    op.Note($"预览刷新失败：{ex.Message}", LogLevel.Debug);
                }
            }
        }
        finally
        {
            IsScanning = false;
        }
    }
    /// <summary>
    /// Window close guard: a scan or a batch still running means unsaved work. Returns false when
    /// the user chooses to stay; on accept, the scanner session is cancelled so the device is not
    /// left running behind a dying process.
    /// </summary>
    public bool ConfirmExit()
    {
        if (!IsBusy && !IsScanning) return true;

        string what = IsScanning ? "正在扫描" : "正在处理文件";
        bool leave = _dialogs.Confirm(
            $"{what}，现在退出会丢失尚未完成的内容。\n\n确定要退出吗？", "退出确认");
        if (!leave) return false;

        AppLog.Info("退出应用：用户确认中断进行中的任务");
        _scanLoopStopped = true;
        _scan.CancelScan();
        return true;
    }
}
