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
using WatermarkRemover.Scan.Pdf;

namespace WatermarkRemover.App.Services;

/// <summary>One scan attempt's outcome; <see cref="Success"/> carries the written PDF.
/// <see cref="Warning"/> means the document was saved, but not where it was asked to go — the
/// host shows it verbatim so the user knows what to fix and where the pages are.</summary>
public sealed record ScanOutcome(
    bool Success,
    string? OutputPath,
    int PageCount,
    string? SourceName,
    string? Error,
    string? ErrorDetails,
    bool Cancelled = false,
    string? Warning = null);

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

    public ScanService(AppSettings settings) => _settings = settings;

    /// <param name="duplex">Page layout chosen on the scan page (single / duplex long / short edge).</param>
    /// <param name="color">Colour mode chosen on the scan page (auto / colour / gray / black-white).</param>
    /// <param name="progress">Human-readable stage text for the scan panel.</param>
    public async Task<ScanOutcome> ScanAsync(
        DuplexMode duplex, ColorMode color, IProgress<string> progress, CancellationToken cancellationToken,
        IProgress<ScannedPageInfo>? pageProgress = null)
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
                () => RunAsync(duplex, color, progress, cancellationToken, pageProgress, op));

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
        IProgress<ScannedPageInfo>? pageProgress, OperationScope op)
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
        var encodedPages = new List<EncodedScanPage>();
        var pendingEncodes = new Queue<Task<TimedEncode>>();
        double dpi = PdfDocumentBuilder.ResolveDpi(settings);
        int captured = 0;
        bool cancelled = false;
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
                    EncodedScanPage payload = PdfDocumentBuilder.EncodePage(
                        new ScannedPage { Image = page }, ScanPdfOptions, ThumbnailEncoder, dpi);
                    return new TimedEncode(payload, encode.ElapsedMilliseconds, pageNumber, width, height, readMs);
                }));

                if (pendingEncodes.Count >= 2)
                {
                    TimedEncode done = await pendingEncodes.Dequeue();
                    encodedPages.Add(done.Payload);
                    op.Step($"第 {done.PageNumber} 页",
                        $"{done.Width}×{done.Height}，读取 {done.ReadMs} ms，编码 {done.EncodeMs} ms，" +
                        $"{PayloadKb(done.Payload)} KB");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Keep every page that already reached us: the partial document is assembled below.
            cancelled = true;
        }
        finally
        {
            // Release the device before assembling the PDF, so a cancel stops the hardware now
            // instead of after the (potentially long) document build.
            await session.DisposeAsync();
            op.Step("释放扫描设备", $"已读取 {captured} 页");
            while (pendingEncodes.Count > 0)
            {
                TimedEncode done = await pendingEncodes.Dequeue();
                encodedPages.Add(done.Payload);
                op.Step($"第 {done.PageNumber} 页编码（收尾）",
                    $"{done.Width}×{done.Height}，编码 {done.EncodeMs} ms，{PayloadKb(done.Payload)} KB");
            }
        }

        if (encodedPages.Count == 0)
        {
            return cancelled
                ? new ScanOutcome(false, null, 0, device.Name, "扫描已取消", null, Cancelled: true)
                : new ScanOutcome(false, null, 0, device.Name,
                    "未读取到任何页面，请确认原稿已正确放入。", null);
        }

        progress.Report(cancelled ? "已取消，正在保存已扫描的页面…" : "正在生成 PDF…");

        // Reorder finished payloads (not raw rasters) into reading order.
        IReadOnlyList<int> order = PageOrderNormalizer.NormalizeOrder(encodedPages.Count, settings);
        var pages = new List<ScannedPage>(order.Count);
        var orderedEncoded = new List<EncodedScanPage>(order.Count);
        for (var index = 0; index < order.Count; index++)
        {
            var (sheetIndex, isBackSide) = PageOrderNormalizer.ClassifyPage(index, duplex);
            pages.Add(new ScannedPage
            {
                Index = index,
                SheetIndex = sheetIndex,
                IsBackSide = isBackSide,
            });
            orderedEncoded.Add(encodedPages[order[index]]);
        }
        op.Step("页面排序", $"{pages.Count} 页，{duplex}");

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
        // as a partial PDF, so the assembly and write deliberately ignore the token.
        byte[] pdf = await Task.Run(() => PdfDocumentBuilder.Build(document, ScanPdfOptions, orderedEncoded));
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
        return new ScanOutcome(true, path, pages.Count, device.Name, null, null, cancelled, warning);
    }

    /// <summary>One page's finished payload plus the per-page numbers the log step reports.</summary>
    private sealed record TimedEncode(
        EncodedScanPage Payload, long EncodeMs, int PageNumber, int Width, int Height, long ReadMs);

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
