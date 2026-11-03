using System.Diagnostics;
using System.IO;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Features.Scan;
using WatermarkRemover.Core.Logging;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Scan.Core.Abstractions;
using WatermarkRemover.Scan.Core.Models;
using WatermarkRemover.Scan.Core.Pipeline;
using WatermarkRemover.Scan.Engine;
using WatermarkRemover.Scan.Engine.BrotherEngine;
using WatermarkRemover.Imaging;
using WatermarkRemover.Ocr;
using WatermarkRemover.Ocr.Orientation;
using WatermarkRemover.Pdf;

namespace WatermarkRemover.App.Services;

/// <summary>One scan attempt's outcome; <see cref="Success"/> carries the written PDF.
/// <see cref="Warning"/> means the document was saved, but not where it was asked to go — the
/// host shows it verbatim so the user knows what to fix and where the pages are.
/// <see cref="SkippedBlankPages"/> reports the pages the capture-time pixel probe filtered out
/// (v1.16.83); <see cref="OrientationTurns"/> is the page-1 vote the PDF was assembled with —
/// non-null means the file on disk is already upright and the preview's automatic passes can
/// skip, null keeps the old strip-side auto-orient as the safety net.</summary>
public sealed record ScanOutcome(
    bool Success,
    string? OutputPath,
    int PageCount,
    string? SourceName,
    string? Error,
    string? ErrorDetails,
    bool Cancelled = false,
    string? Warning = null,
    string? Interruption = null,
    int SkippedBlankPages = 0,
    int? OrientationTurns = null,
    bool OrientationPerPage = false,
    int UndecidedPages = 0);

/// <summary>One page just pulled from the device, delivered while the scan is still running.
/// The final PDF does not exist yet; the small PNG lets the row show the page immediately.</summary>
public sealed record ScannedPageInfo(
    int PageNumber, int Width, int Height, byte[] ThumbnailPng, byte[]? NamingImage);

/// <summary>Raised when a scan is started while another scan still holds the scanner gate.
/// Distinct from a failed scan so callers can react with "busy" feedback instead of an error.</summary>
public sealed class ScannerBusyException : InvalidOperationException
{
    public ScannerBusyException()
        : base("扫描仪正在工作中，请等待当前扫描完成。")
    {
    }
}

/// <summary>
/// Drives one scan end to end: pick the first available engine channel (Brother's official engine
/// through the x86 bridge, WIA fallback), pull pages, restore duplex reading order, assemble a
/// high-compression PDF and write it to the scan output directory. Every failure is reported as an
/// outcome — this service never throws for expected problems.
/// </summary>
public sealed class ScanService
{
    /// <summary>Longest edge of the live thumbnail; 256 px is enough for a 64×86 tile.</summary>
    private const int ThumbnailMaxEdge = 256;

    /// <summary>Longest edge of the first page handed to early naming (~214 dpi on A4; the
    /// naming chain itself renders at 220 dpi).</summary>
    private const int NamingImageMaxEdge = 2500;

    private static readonly ScanImageEncoder ThumbnailEncoder = new();

    /// <summary>The scan PDF profile; encoding happens per page at capture time.</summary>
    private static readonly PdfBuildOptions ScanPdfOptions = new()
    {
        HighCompression = true,
        Searchable = false,
        PageNumbers = false,
    };

    private readonly AppSettings _settings;

    /// <summary>盲扫逐页方向分类器（PP-LCNet doc_ori）。模型缺失/加载失败时为 null——
    /// 整个逐页通道静默让位给整份同转（第 1 页裁决），扫描行为与 v1.16.83 P1 完全一致。</summary>
    private readonly DocOrientationClassifier? _orientationClassifier;

    public ScanService(AppSettings settings)
    {
        _settings = settings;
        string reason = "未启用";
        if (settings.PerPageOrientationEnabled &&
            DocOrientationClassifier.TryCreate(out _orientationClassifier, out reason))
        {
            AppLog.Info("盲扫：逐页方向分类器已加载（每页独立裁决 0/90/180/270）");
        }
        else
        {
            _orientationClassifier = null;
            if (settings.PerPageOrientationEnabled)
                AppLog.Info($"盲扫：逐页方向分类器不可用（{reason}），方向走整份同转");
        }
    }

    /// <param name="duplex">Page layout chosen on the scan page (single / duplex long / short edge).</param>
    /// <param name="color">Colour mode chosen on the scan page (auto / colour / gray / black-white).</param>
    /// <param name="progress">Human-readable stage text for the scan panel.</param>
    /// <param name="orientationVote">The host's page-1 orientation vote, started while the scanner
    /// fed the remaining pages; awaited (it is normally already done) before assembly so the turn
    /// lands as /Rotate metadata. Null or a null verdict keeps the preview's auto-orient as before.</param>
    public async Task<ScanOutcome> ScanAsync(
        DuplexMode duplex, ColorMode color, IProgress<string> progress, CancellationToken cancellationToken,
        IProgress<ScannedPageInfo>? pageProgress = null, Task<int?>? orientationVote = null)
    {
        // A scan is long by nature; the slow-operation warning would fire on every healthy run,
        // so the threshold is disabled and the per-page steps below carry the useful numbers.
        using var op = AppLog.BeginOperation(
            LogCategory.Scan, "扫描会话", $"{duplex} / {color}", slowMs: 0);

        try
        {
            // The engine chain is synchronous COM that fakes asynchrony (Task.FromResult):
            // the Brother WMI probe, WIA.DeviceManager creation and device enumeration all
            // used to run ON the UI thread — the jolt right after pressing 开始扫描. The
            // whole session belongs on a worker thread; progress text still marshals back
            // through the IProgress<string>, and there is no WIA dialog to pin to an STA.
            ScanOutcome outcome = await Task.Run(
                () => RunAsync(duplex, color, progress, cancellationToken, pageProgress, orientationVote, op));

            if (outcome.Success)
            {
                op.Complete(outcome.Cancelled
                    ? $"已取消，保留 {outcome.PageCount} 页 → {outcome.OutputPath}"
                    : $"成功，{outcome.PageCount} 页，来源 {outcome.SourceName} → {outcome.OutputPath}");
            }
            else if (outcome.Cancelled)
            {
                op.Complete("已取消，未读取到页面");
            }
            else
            {
                op.Fail(outcome.Error ?? "扫描失败");
            }

            return outcome;
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("扫描已取消");
            op.Complete("已取消");
            return new ScanOutcome(false, null, 0, null, "扫描已取消", null, Cancelled: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("扫描失败", ex);
            op.Fail("扫描失败", ex);
            return new ScanOutcome(false, null, 0, null, $"扫描失败：{ex.Message}", ex.ToString());
        }
    }

    private async Task<ScanOutcome> RunAsync(
        DuplexMode duplex, ColorMode color, IProgress<string> progress, CancellationToken cancellationToken,
        IProgress<ScannedPageInfo>? pageProgress, Task<int?>? orientationVote, OperationScope op)
    {
        // Engine channel chain: Brother official first, WIA compatibility fallback second.
        IScanSource[] sources = { new BrotherEngineScanSource(AppLog.Trace), new WiaScanSource(AppLog.Trace) };

        progress.Report("正在连接扫描仪…");
        IScanSource? chosen = null;
        ScanDeviceInfo? device = null;
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                bool available = await source.IsAvailableAsync(cancellationToken);
                if (!available)
                {
                    op.Step($"探测 {source.DisplayName}", "不可用");
                    continue;
                }

                var devices = await source.DiscoverAsync(cancellationToken);
                op.Step($"探测 {source.DisplayName}",
                    devices.Count > 0 ? $"发现 {devices[0].Name}" : "未发现设备");
                if (devices.Count > 0)
                {
                    chosen = source;
                    device = devices[0];
                    break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppLog.Error($"扫描源 {source.DisplayName} 探测失败", ex);
                op.Note($"探测 {source.DisplayName} 失败：{ex.Message}", LogLevel.Debug);
            }
        }

        if (chosen is null || device is null)
        {
            return new ScanOutcome(false, null, 0, null,
                "未发现扫描仪。请确认设备已开机并连接（USB 或网络），且已安装 Brother iPrint&Scan。", null);
        }

        // Page parameters: resolution/paper/feed are fixed for the first cut (300 dpi, auto paper,
        // continuous scan); colour follows the scan panel. 黑白 keeps text documents fast and small.
        var settings = new ScanSettings
        {
            Color = color,
            Resolution = ScanResolution.Dpi300,
            Paper = PaperSize.Auto,
            Feed = FeedMode.Auto,
            Duplex = duplex,
            ContinuousScan = true,
        };

        progress.Report($"正在启动「{device.Name}」…");
        IScanSession session = await chosen.OpenAsync(device, settings, cancellationToken);
        op.Step("启动扫描会话", $"{device.Name}，{settings.Resolution}，{settings.Color}，{settings.Duplex}");

        // Pages are encoded as they arrive and their raw frames dropped; a thick stack therefore
        // holds N small payloads instead of N ~35 MB bitmaps. A depth-2 queue keeps the encode
        // overlapped with the next page read without ever holding the whole document in memory.
        // (v1.16.83 P2: the blank-page pixel probe rides in the SAME task — the queue never
        // deepens and blank duplex backs skip encoding entirely.)
        var completedPages = new Dictionary<int, TimedEncode>();
        var pendingEncodes = new Queue<Task<TimedEncode>>();
        double dpi = PdfDocumentBuilder.ResolveDpi(settings);
        int captured = 0;
        bool cancelled = false;
        // The device can fail mid-document (paper jam, cable, driver). That must not throw away
        // the pages already pulled off the hardware: the reason is kept and the partial document
        // is assembled and saved below, exactly like a cancel.
        string? readError = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long readStart = Stopwatch.GetTimestamp();
                var page = await session.ReadPageAsync(cancellationToken);
                if (page is null) break;

                captured++;
                long readMs = (long)Stopwatch.GetElapsedTime(readStart).TotalMilliseconds;
                ReportPage(page, captured, pageProgress);
                progress.Report($"正在扫描… 已读取 {captured} 页");

                int pageNumber = captured;
                int width = page.Width;
                int height = page.Height;
                pendingEncodes.Enqueue(Task.Run(() =>
                {
                    var encode = Stopwatch.StartNew();
                    // 一次 256px 探针编码，两处复用：空白像素判定 + 盲扫方向票（分类器/投影基线）。
                    byte[] probe = Probe256(page);
                    bool isBlank = BlankPageDetector.IsBlank(probe);
                    // A blank page enters no encode and no PDF; the FIRST captured page is always
                    // encoded so the all-blank keep-first rule always has something to keep.
                    EncodedScanPage? payload = isBlank && pageNumber > 1
                        ? null
                        : PdfDocumentBuilder.EncodePage(
                            new ScannedPage { Image = page }, ScanPdfOptions, ThumbnailEncoder, dpi);

                    // 盲扫逐页票：与读取重叠、≈毫秒级；任何失败都按弃权处理，绝不波及扫描。
                    (int? classifierTurns, float confidence, bool? baseline) = (null, 0f, (bool?)null);
                    if (!isBlank && _orientationClassifier is { } classifier)
                    {
                        try
                        {
                            DocOrientationClassifier.Vote vote = classifier.Classify(probe);
                            classifierTurns = vote.QuarterTurns;
                            confidence = vote.Confidence;
                            baseline = ProjectionAxisBaseline.Judge(probe)?.IsHorizontalText;
                            // 原始票留痕（Debug 级）：决策层的门槛/分组全靠这些数字，现场排查
                            // 必须能看到每页真实票面——渲染复算与 1bpp 原始帧的票会漂移。
                            AppLog.Debug(
                                $"盲扫：第 {pageNumber} 页原始票 turns={vote.QuarterTurns} conf={confidence:0.000} " +
                                $"baseline={(baseline is null ? "弃权" : baseline.Value ? "横排" : "竖排")} 横版={width > height}");
                        }
                        catch (Exception ex)
                        {
                            AppLog.Debug($"盲扫：第 {pageNumber} 页方向票弃权（{ex.Message}）");
                        }
                    }

                    return new TimedEncode(payload, encode.ElapsedMilliseconds, pageNumber, width, height,
                        readMs, isBlank, classifierTurns, confidence, baseline);
                }));

                if (pendingEncodes.Count >= 2)
                {
                    TimedEncode done = await pendingEncodes.Dequeue();
                    completedPages[done.PageNumber] = done;
                    LogPageStep(done, op);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Keep every page that already reached us: the partial document is assembled below.
            cancelled = true;
        }
        catch (Exception ex)
        {
            readError = ex.Message;
            AppLog.Error($"扫描中断（已读取 {captured} 页），保留页面并继续保存", ex);
            op.Step("读取中断", $"{ex.Message}（已读 {captured} 页）");
        }
        finally
        {
            // Release the device before assembling the PDF, so a cancel stops the hardware now
            // instead of after the (potentially long) document build. A broken device often
            // fails here too — that must not discard the pages already in hand.
            try
            {
                await session.DisposeAsync();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"释放扫描设备失败（已读取 {captured} 页）：{ex.Message}");
            }

            op.Step("释放扫描设备", $"已读取 {captured} 页");
            while (pendingEncodes.Count > 0)
            {
                TimedEncode done;
                try
                {
                    done = await pendingEncodes.Dequeue();
                }
                catch (Exception ex)
                {
                    AppLog.Error("页面编码失败，已跳过该页", ex);
                    op.Step("页面编码失败", ex.Message);
                    continue;
                }
                completedPages[done.PageNumber] = done;
                LogPageStep(done, op, settling: true);
            }
        }

        // P1: the page-1 orientation vote ran while the scanner fed the remaining pages. In
        // 盲扫逐页 mode it is left unconsumed — every page already voted inside its encode task,
        // and the whole-doc verdict would only double-judge page 1. Null (unusable engine / host
        // without the hook) keeps the old preview-side auto-orient as the safety net.
        bool perPageOrientation = _orientationClassifier is not null && _settings.PerPageOrientationEnabled;
        int? orientationTurns = null;
        if (!perPageOrientation && orientationVote is not null)
        {
            try
            {
                orientationTurns = await orientationVote.ConfigureAwait(false);
            }
            catch
            {
                orientationTurns = null; // a failed vote must never fail the scan
            }
            op.Step("方向裁决", orientationTurns is { } turns
                ? $"第 1 页顺时针 {turns * 90}°（扫描中完成，写入 /Rotate）"
                : "无裁决，交由预览自动摆正");
        }

        progress.Report(cancelled
            ? "已取消，正在保存已扫描的页面…"
            : readError is not null ? "扫描中断，正在保存已读取的页面…" : "正在生成 PDF…");

        // Reorder finished captures (not raw rasters) into reading order. The order math uses
        // the FULL captured count — the manual flip-the-stack interleave needs it even when
        // some pages later drop out as blank or failed.
        var byIndex = new TimedEncode?[captured];
        foreach (var done in completedPages.Values) byIndex[done.PageNumber - 1] = done;
        var readingOrder = PageOrderNormalizer.NormalizeOrder(captured, settings)
            .Select(i => byIndex[i])
            .ToList();

        (List<TimedEncode> keptEncodes, int skippedBlanks) = SelectPagesForPdf(readingOrder);
        var kept = keptEncodes.Select(t => t.Payload!).ToList();
        var pages = new List<ScannedPage>(kept.Count);
        for (var index = 0; index < kept.Count; index++)
        {
            var (sheetIndex, isBackSide) = PageOrderNormalizer.ClassifyPage(index, duplex);
            pages.Add(new ScannedPage
            {
                Index = index,
                SheetIndex = sheetIndex,
                IsBackSide = isBackSide,
            });
        }
        op.Step("页面排序", $"{kept.Count} 页（采集 {captured} 页），{duplex}");
        if (skippedBlanks > 0)
            op.Step("空白页过滤", $"采集期判定，跳过 {skippedBlanks} 页空白页");

        // 盲扫逐页裁决（保守决策层）：每页独立票 + 投影基线交叉 + 整份多数 + 短边奇偶约束。
        IReadOnlyList<int>? perPageTurns = null;
        PerPageOrientationDecider.Decision? pageDecision = null;
        if (perPageOrientation && keptEncodes.Count > 0)
        {
            var votes = new List<PerPageOrientationDecider.PageVote>(keptEncodes.Count);
            foreach (TimedEncode t in keptEncodes)
            {
                var (_, isBack) = PageOrderNormalizer.ClassifyPage(t.PageNumber - 1, duplex);
                votes.Add(new PerPageOrientationDecider.PageVote(
                    ClassifierTurns: t.ClassifierTurns,
                    ClassifierConfidence: t.ClassifierConfidence,
                    BaselineIsHorizontal: t.BaselineHorizontal,
                    IsBackSide: isBack,
                    IsLandscape: t.Width > t.Height));
            }

            var binding = duplex switch
            {
                DuplexMode.LongEdge => PerPageOrientationDecider.Binding.LongEdge,
                DuplexMode.ShortEdge => PerPageOrientationDecider.Binding.ShortEdge,
                _ => PerPageOrientationDecider.Binding.None,
            };
            PerPageOrientationDecider.Decision decision =
                PerPageOrientationDecider.Decide(votes, binding);
            pageDecision = decision;
            perPageTurns = decision.Turns;
            op.Step("逐页方向裁决", DescribeDecision(decision));
        }

        if (kept.Count == 0)
        {
            if (readError is not null)
            {
                return new ScanOutcome(false, null, 0, device.Name,
                    $"扫描中断：{readError}", null);
            }

            return cancelled
                ? new ScanOutcome(false, null, 0, device.Name, "扫描已取消", null, Cancelled: true)
                : new ScanOutcome(false, null, 0, device.Name,
                    "未读取到任何页面，请确认原稿已正确放入。", null);
        }

        string directory = OutputDirectoryResolver.Resolve(_settings.ScanOutputDirectory, AppFolders.Root);
        string fileName = ScanFileName.Create(DateTime.Now);
        string path = OutputNameGenerator.FindAvailablePath(directory, fileName);

        var document = new ScanDocument
        {
            Pages = pages,
            Settings = settings,
            BaseName = Path.GetFileNameWithoutExtension(path),
            SourceName = device.Name,
        };
        // Cancellation stops page capture, not the save: the pages already read must still land
        // as a partial PDF, so the assembly and write deliberately ignore the token. The early
        // orientation verdict rides along as /Rotate metadata (v1.16.83 P1); 盲扫逐页 mode
        // overrides it with one turn per page.
        byte[] pdf = await Task.Run(() =>
            PdfDocumentBuilder.Build(document, ScanPdfOptions, kept, orientationTurns ?? 0, perPageTurns));
        op.Step("生成 PDF", $"{pages.Count} 页，{pdf.Length / 1024.0:0} KB");

        // A failed write used to discard the whole document — the pages were already pulled off
        // the hardware and the user had nothing to show for it. Now the PDF lands in a temp
        // recovery folder and the caller tells the user where it is and what to fix.
        string? warning = null;
        try
        {
            await Task.Run(() => File.WriteAllBytes(path, pdf));
            op.Step("写入文件", path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            string recoveryDirectory = Path.Combine(Path.GetTempPath(), "WatermarkRemover", "scan-recovery");
            Directory.CreateDirectory(recoveryDirectory);
            string recoveryPath = OutputNameGenerator.FindAvailablePath(
                recoveryDirectory, Path.GetFileName(path));
            await Task.Run(() => File.WriteAllBytes(recoveryPath, pdf));
            AppLog.Error($"扫描目录写入失败，已存到临时目录：{directory} → {recoveryPath}", ex);
            op.Step("写入文件", $"{directory} 失败，已存到 {recoveryPath}");
            warning = $"无法写入扫描目录：\n{directory}\n\n原因：{ex.Message}\n\n" +
                      $"这份扫描（{pages.Count} 页）已临时保存到：\n{recoveryPath}\n\n" +
                      "请检查目录权限或磁盘空间，并把文件移动到目标目录。";
            path = recoveryPath;
        }

        AppLog.Info(cancelled
            ? $"扫描已取消，已保留 {pages.Count} 页：{path}（{pdf.Length / 1024.0:0} KB）"
            : $"扫描完成：{path}（{pages.Count} 页，{pdf.Length / 1024.0:0} KB，来源 {device.Name}，版面 {duplex}）");
        if (readError is not null)
            AppLog.Warn($"扫描中断但已保存 {pages.Count} 页：{path}，原因：{readError}");
        return new ScanOutcome(true, path, pages.Count, device.Name, null, null, cancelled, warning, readError,
            SkippedBlankPages: skippedBlanks,
            OrientationTurns: perPageTurns is null ? orientationTurns : null,
            OrientationPerPage: perPageTurns is not null,
            UndecidedPages: pageDecision is null
                ? 0
                : pageDecision.Sources.Count(s => s == PerPageOrientationDecider.Provenance.Undecided));
    }

    /// <summary>逐页裁决摘要：转数列表 + 按来源分层计数（上线后日志采样监控按此分层看误翻率）。</summary>
    private static string DescribeDecision(PerPageOrientationDecider.Decision decision)
    {
        var bySource = decision.Sources
            .GroupBy(s => s)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key} {g.Count()}");
        return $"[{string.Join(",", decision.Turns)}]（{string.Join("、", bySource)}）";
    }

    /// <summary>One page's finished capture: the payload (null when the page was blank and not
    /// the first, or when encoding failed), the blank verdict, and the 盲扫 orientation votes.</summary>
    internal sealed record TimedEncode(
        EncodedScanPage? Payload, long EncodeMs, int PageNumber, int Width, int Height, long ReadMs, bool IsBlank,
        int? ClassifierTurns = null, float ClassifierConfidence = 0f, bool? BaselineHorizontal = null);

    /// <summary>
    /// P2's pure core: pages in reading order → what enters the PDF. Blank pages (judged at
    /// capture time from the raw frame) and unencodable pages are skipped; an all-blank capture
    /// keeps its first page rather than turning into nothing. The order of survivors is the
    /// reading order — nothing here can shuffle pages. Returns the surviving captures (payload
    /// plus the 盲扫 votes) so the per-page orientation decision sees exactly the kept pages.
    /// </summary>
    internal static (List<TimedEncode> Kept, int SkippedBlanks) SelectPagesForPdf(
        IReadOnlyList<TimedEncode?> readingOrder)
    {
        var kept = new List<TimedEncode>();
        int skippedBlanks = 0;
        // All-blank keep-first: every entry blank-or-missing means only the first page (always
        // encoded) carries the document.
        bool keepFirstOnly = readingOrder.All(t => t is null or { IsBlank: true });
        foreach (TimedEncode? page in readingOrder)
        {
            if (page is null) continue; // encode task faulted — already logged where it threw
            if (page.Payload is null)
            {
                if (page.IsBlank) skippedBlanks++;
                continue;
            }
            if (keepFirstOnly && kept.Count >= 1) break;
            kept.Add(page);
        }
        return (kept, skippedBlanks);
    }

    /// <summary>
    /// 采集期探针：原始帧降采样到 256 px 的 PNG——空白判定与盲扫方向票共用这一份。
    /// 探针失败返回空数组（空白判定按有内容处理，方向票弃权）：宁可留页，不误删。
    /// </summary>
    private static byte[] Probe256(RasterImage page)
    {
        try
        {
            return ThumbnailEncoder.Encode(ThumbnailEncoder.Resize(page, 256), "png");
        }
        catch
        {
            return Array.Empty<byte>();
        }
    }

    /// <summary>Per-page log step; blank pages say so instead of reporting an encode.</summary>
    private static void LogPageStep(TimedEncode done, OperationScope op, bool settling = false)
    {
        string stage = settling ? $"第 {done.PageNumber} 页编码（收尾）" : $"第 {done.PageNumber} 页";
        op.Step(stage, done.Payload is null
            ? $"{done.Width}×{done.Height}，读取 {done.ReadMs} ms，空白页（编码跳过）"
            : $"{done.Width}×{done.Height}，读取 {done.ReadMs} ms，编码 {done.EncodeMs} ms，" +
              $"{PayloadKb(done.Payload)} KB");
    }

    private static int PayloadKb(EncodedScanPage page) =>
        (page.Ccitt?.Length ?? page.Bytes?.Length ?? 0) / 1024;

    /// <summary>Hands one freshly captured page to the host: thumbnail for the row, and — on the
    /// first page — a naming-resolution image so OCR can start during the rest of the scan.</summary>
    private void ReportPage(RasterImage page, int pageNumber, IProgress<ScannedPageInfo>? pageProgress)
    {
        if (pageProgress is null) return;

        byte[] thumbnail = ThumbnailEncoder.Encode(
            ThumbnailEncoder.Resize(page, ThumbnailMaxEdge), "png");
        byte[]? namingImage = pageNumber == 1 && _settings.AutoNameByContent
            ? ThumbnailEncoder.Encode(
                ThumbnailEncoder.Resize(page, NamingImageMaxEdge), "png")
            : null;
        pageProgress.Report(new ScannedPageInfo(
            pageNumber, page.Width, page.Height, thumbnail, namingImage));
    }
}
