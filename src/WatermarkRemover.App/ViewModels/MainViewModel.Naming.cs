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
        // 首屏命名已在扫描途中给出行内结论（已识别：X / 首屏未识别）；这时再写「识别命名中…」
        // 会把用户刚看到的名字盖掉。只要卡片还没挂着识别出的名字（无论首屏有没有跑过），
        // 这条进行中提示都必须在：否则「首屏未识别 → 后台整份 OCR」的几秒空窗毫无反馈
        // （1.16.133 用户报告：桌面出了临时名，后台识别期间没有任何提示）。
        if (namingOn && !job.IsDisplayNameOptimistic) job.SetNamingNote("识别命名中…");
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
                // 首屏命名已经把识别名显示出来了，但 File.Move 没有落盘（被占用/失败）：
                // 显示退回真实文件名并说清楚，不能让卡片一直挂着一个磁盘上不存在的名字。
                if (job.IsDisplayNameOptimistic)
                {
                    job.RevertOptimisticDisplayName("改名失败，已恢复原文件名");
                    AppLog.Warn($"后台命名未落盘：{Path.GetFileName(pdfPath)}，已恢复显示原名");
                    op.Complete("改名失败，已恢复原名");
                    LogScanChain(chainTicks, finalPath, "改名失败，已恢复原名");
                    return;
                }

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
                // 改名是应用自己做的，直接把新路径喂给打开着的预览会话，不等下一次渲染失败
                // 再靠指纹去目录里「找回来」——找回复命中的文件头哈希读不到（看图窗口持锁）
                // 就会给出「候选 0 个」并把预览条永远钉在死路径上。
                job.Strip?.RelocateTo(finalPath);
            });
            op.Complete($"→ {Path.GetFileName(finalPath)}");
            LogScanChain(chainTicks, finalPath, "已改名");
        }
        catch (Exception ex)
        {
            AppLog.Trace("命名：后台自动命名异常，保留时间戳命名", ex);
            job.RevertOptimisticDisplayName();
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
}
