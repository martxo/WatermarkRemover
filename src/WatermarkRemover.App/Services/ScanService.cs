using System.IO;
using WatermarkRemover.App.Logging;
using WatermarkRemover.Core.Features.Scan;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Scan.Core.Abstractions;
using WatermarkRemover.Scan.Core.Models;
using WatermarkRemover.Scan.Core.Pipeline;
using WatermarkRemover.Scan.Engine;
using WatermarkRemover.Scan.Engine.BrotherEngine;
using WatermarkRemover.Scan.Pdf;

namespace WatermarkRemover.App.Services;

/// <summary>One scan attempt's outcome; <see cref="Success"/> carries the written PDF.</summary>
public sealed record ScanOutcome(
    bool Success,
    string? OutputPath,
    int PageCount,
    string? SourceName,
    string? Error,
    string? ErrorDetails,
    bool Cancelled = false);

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

    private readonly AppSettings _settings;

    public ScanService(AppSettings settings) => _settings = settings;

    /// <param name="duplex">Page layout chosen on the scan page (single / duplex long / short edge).</param>
    /// <param name="color">Colour mode chosen on the scan page (auto / colour / gray / black-white).</param>
    /// <param name="progress">Human-readable stage text for the scan panel.</param>
    public async Task<ScanOutcome> ScanAsync(
        DuplexMode duplex, ColorMode color, IProgress<string> progress, CancellationToken cancellationToken,
        IProgress<ScannedPageInfo>? pageProgress = null)
    {
        try
        {
            // The engine chain is synchronous COM that fakes asynchrony (Task.FromResult):
            // the Brother WMI probe, WIA.DeviceManager creation and device enumeration all
            // used to run ON the UI thread — the jolt right after pressing 开始扫描. The
            // whole session belongs on a worker thread; progress text still marshals back
            // through the IProgress<string>, and there is no WIA dialog to pin to an STA.
            return await Task.Run(() => RunAsync(duplex, color, progress, cancellationToken, pageProgress));
        }
        catch (OperationCanceledException)
        {
            AppLog.Info("扫描已取消");
            return new ScanOutcome(false, null, 0, null, "扫描已取消", null, Cancelled: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("扫描失败", ex);
            return new ScanOutcome(false, null, 0, null, $"扫描失败：{ex.Message}", ex.ToString());
        }
    }

    private async Task<ScanOutcome> RunAsync(
        DuplexMode duplex, ColorMode color, IProgress<string> progress, CancellationToken cancellationToken,
        IProgress<ScannedPageInfo>? pageProgress)
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
                if (!await source.IsAvailableAsync(cancellationToken)) continue;
                var devices = await source.DiscoverAsync(cancellationToken);
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

        var rawPages = new List<RasterImage>();
        bool cancelled = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = await session.ReadPageAsync(cancellationToken);
                if (page is null) break;

                rawPages.Add(page);
                ReportPage(page, rawPages.Count, pageProgress);
                progress.Report($"正在扫描… 已读取 {rawPages.Count} 页");
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
        }

        if (rawPages.Count == 0)
        {
            return cancelled
                ? new ScanOutcome(false, null, 0, device.Name, "扫描已取消", null, Cancelled: true)
                : new ScanOutcome(false, null, 0, device.Name,
                    "未读取到任何页面，请确认原稿已正确放入。", null);
        }

        progress.Report(cancelled ? "已取消，正在保存已扫描的页面…" : "正在生成 PDF…");
        var ordered = PageOrderNormalizer.Normalize(rawPages, settings);
        var pages = new List<ScannedPage>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            var (sheetIndex, isBackSide) = PageOrderNormalizer.ClassifyPage(index, duplex);
            pages.Add(new ScannedPage
            {
                Image = ordered[index],
                Index = index,
                SheetIndex = sheetIndex,
                IsBackSide = isBackSide,
            });
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
        // as a partial PDF, so the assembly and write deliberately ignore the token.
        byte[] pdf = await Task.Run(() => PdfDocumentBuilder.Build(
            document,
            new PdfBuildOptions { HighCompression = true, Searchable = false, PageNumbers = false },
            new ScanImageEncoder()));
        await Task.Run(() => File.WriteAllBytes(path, pdf));

        AppLog.Info(cancelled
            ? $"扫描已取消，已保留 {pages.Count} 页：{path}（{pdf.Length / 1024.0:0} KB）"
            : $"扫描完成：{path}（{pages.Count} 页，{pdf.Length / 1024.0:0} KB，来源 {device.Name}，版面 {duplex}）");
        return new ScanOutcome(true, path, pages.Count, device.Name, null, null, cancelled);
    }

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
