using System.IO;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Naming;
using WatermarkRemover.App.Ocr;
using WatermarkRemover.Pdf.Rendering;
using WatermarkRemover.Ocr;
using WatermarkRemover.Imaging;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Features.Naming;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Logging;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App.Services;

/// <summary>
/// Content-based naming for a freshly produced PDF: render → orient → fixed-region fast path →
/// OCR → <see cref="SmartNamingService"/>, optionally moving the file to the suggested name.
/// The suggestion step is also exposed on its own so a scan can start naming from its first page
/// while the remaining pages are still being pulled.
/// </summary>
public sealed class ContentNamingService
{
    /// <summary>Render resolution for the naming OCR pass. 220 dpi: at 150 the value cells of
    /// watermark-pressed documents (credit report header tables) came back empty — only the labels
    /// survived, so no name could be extracted.</summary>
    private const int NamingRenderDpi = 220;

    /// <summary>Cap on the pages probed for naming: a duplex/multi-sheet scan packs several
    /// document faces into one PDF and the type may only sit on a later page, but a long
    /// unrecognisable document must not hold the job row hostage with serial OCR passes.</summary>
    private const int MaxNamingProbePages = 4;

    /// <summary>Type short name for the fixed-region credit report locator's naming result.</summary>
    private static readonly string CreditReportTypeShortName =
        DocumentProfileCatalog.All.First(p => p.TypeId == "credit-report").TypeShortName;

    private readonly AppSettings _settings;
    private readonly IPdfPageRenderer _renderer;
    private readonly IOcrEngine _ocr;

    public ContentNamingService(AppSettings settings, IPdfPageRenderer renderer, IOcrEngine ocr)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _ocr = ocr ?? throw new ArgumentNullException(nameof(ocr));
    }

    /// <summary>
    /// Returns the path to file into the job list: the renamed target when a page was confidently
    /// recognised, otherwise <paramref name="pdfPath"/> unchanged.
    /// </summary>
    public async Task<string> TryRenameAsync(string pdfPath)
    {
        if (!_settings.AutoNameByContent) return pdfPath;

        AutoNameResult? autoName = await TrySuggestAsync(pdfPath).ConfigureAwait(true);
        return autoName is null
            ? pdfPath
            : await ApplyAsync(pdfPath, autoName).ConfigureAwait(true);
    }

    /// <summary>
    /// The same content naming scans get, applied to a file that arrived from a phone. PDFs go
    /// through the page probe; images go through the single-image probe, so a photo of a document
    /// lands under its recognised name instead of 「IMG_2031.jpg」. Anything else keeps its name —
    /// and every failure keeps the received name too, never losing the file.
    /// </summary>
    public async Task<string> TryRenameReceivedAsync(string path)
    {
        if (!_settings.AutoNameByContent) return path;

        string extension = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (extension == ".pdf")
                return await TryRenameAsync(path).ConfigureAwait(true);

            if (extension is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp")
            {
                long length = new FileInfo(path).Length;
                if (length == 0 || length > MaxReceivedImageBytes) return path;

                byte[] image = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
                AutoNameResult? suggestion = await TrySuggestFromImageAsync(image).ConfigureAwait(true);
                return suggestion is null
                    ? path
                    : await ApplyAsync(path, suggestion).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Trace("命名：接收文件自动命名异常，保留原文件名", ex);
        }

        return path;
    }

    /// <summary>Cap on an image loaded whole for naming — phone originals are a few MB; anything
    /// far larger is not a document photo and must not be pulled into memory.</summary>
    private const long MaxReceivedImageBytes = 50L * 1024 * 1024;

    /// <summary>
    /// Suggests a name by probing the PDF's pages (one page, or up to four when the settings allow
    /// it). Returns null when nothing was recognised; never throws for expected failures.
    /// </summary>
    public async Task<AutoNameResult?> TrySuggestAsync(string pdfPath)
    {
        if (!_settings.AutoNameByContent) return null;

        using var op = AppLog.BeginOperation(
            LogCategory.Naming, "自动命名", Path.GetFileName(pdfPath));

        try
        {
            int pageCount = await _renderer.GetPageCountAsync(pdfPath).ConfigureAwait(true);
            op.Step("读取页数", $"{pageCount} 页");
            int probeLimit = _settings.NameFromFirstPageOnly ? 1 : MaxNamingProbePages;
            for (int pageIndex = 0; pageIndex < pageCount && pageIndex < probeLimit; pageIndex++)
            {
                byte[] image = await _renderer.RenderPageAsync(pdfPath, pageIndex, NamingRenderDpi, priority: true).ConfigureAwait(true);
                op.Step($"第 {pageIndex + 1}/{pageCount} 页渲染",
                    $"{NamingRenderDpi}dpi，{image.Length / 1024} KB");
                AutoNameResult? suggestion = await SuggestFromPageAsync(image, pageIndex, pageCount, op).ConfigureAwait(true);
                if (suggestion is not null)
                {
                    op.Complete($"识别为 {suggestion.SuggestedStem}");
                    return suggestion;
                }
            }
            op.Complete($"未识别出可信名称（探测 {Math.Min(pageCount, probeLimit)} 页）");
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Trace("命名：自动命名过程异常，保留时间戳命名", ex);
            op.Fail("自动命名过程异常，保留时间戳命名", ex);
            return null;
        }
    }

    /// <summary>
    /// Suggests a name from one already-captured page image (PNG). Used while a scan is still
    /// running, so naming overlaps the remaining pages instead of waiting for the PDF.
    /// </summary>
    public async Task<AutoNameResult?> TrySuggestFromImageAsync(byte[] imageBytes)
    {
        if (!_settings.AutoNameByContent || imageBytes.Length == 0) return null;

        using var op = AppLog.BeginOperation(
            LogCategory.Naming, "首屏自动命名", $"{imageBytes.Length / 1024} KB");

        try
        {
            AutoNameResult? suggestion = await SuggestFromPageAsync(imageBytes, pageIndex: 0, pageCount: 1, op)
                .ConfigureAwait(true);
            op.Complete(suggestion is null ? "未识别出可信名称" : $"识别为 {suggestion.SuggestedStem}");
            return suggestion;
        }
        catch (Exception ex)
        {
            AppLog.Trace("命名：首屏自动命名异常，保留时间戳命名", ex);
            op.Fail("首屏自动命名异常，保留时间戳命名", ex);
            return null;
        }
    }

    /// <summary>Moves <paramref name="pdfPath"/> to the suggested stem (never overwriting).</summary>
    public async Task<string> ApplyAsync(string pdfPath, AutoNameResult autoName)
    {
        ArgumentNullException.ThrowIfNull(autoName);

        using var op = AppLog.BeginOperation(
            LogCategory.Naming, "命名落盘", Path.GetFileName(pdfPath));

        try
        {
            string directory = Path.GetDirectoryName(pdfPath)!;
            string target = OutputNameGenerator.FindAvailablePath(
                directory, autoName.SuggestedStem + Path.GetExtension(pdfPath));
            if (string.Equals(target, pdfPath, StringComparison.OrdinalIgnoreCase))
            {
                op.Complete("名称未变化");
                return pdfPath;
            }

            // The strip may still hold the file for a page write (e.g. the user rotated a page
            // while the naming OCR ran); a short backoff beats losing the good name.
            int attempts = 0;
            for (int attempt = 1; ; attempt++)
            {
                attempts = attempt;
                try
                {
                    File.Move(pdfPath, target);
                    break;
                }
                catch (IOException) when (attempt < 3)
                {
                    await Task.Delay(attempt * 400).ConfigureAwait(false);
                }
            }
            AppLog.Info($"扫描自动命名：{Path.GetFileName(pdfPath)} → {Path.GetFileName(target)}");
            op.Complete($"→ {Path.GetFileName(target)}（尝试 {attempts} 次）");
            return target;
        }
        catch (Exception ex)
        {
            AppLog.Trace("命名：自动命名落盘异常，保留时间戳命名", ex);
            op.Fail("自动命名落盘异常，保留时间戳命名", ex);
            return pdfPath;
        }
    }

    /// <summary>Orientation + fixed-region fast path + full OCR for one page image.</summary>
    private async Task<AutoNameResult?> SuggestFromPageAsync(
        byte[] image, int pageIndex, int pageCount, OperationScope op)
    {
        // A blank page cannot name anything, but it used to pay the orientation vote (four OCR
        // passes) plus a full-page OCR — the slow "空白页也在识别" the scanner reported. The pixel
        // verdict is cheap and lets the next probe page start immediately.
        if (BlankPageDetector.IsBlank(image))
        {
            AppLog.Trace($"命名：第 {pageIndex + 1}/{pageCount} 页为空白页，跳过识别", null);
            op.Step($"第 {pageIndex + 1}/{pageCount} 页空白", "跳过");
            return null;
        }

        // 摆正后再识别：ADF 躺倒页在方向检测完成前就进命名链会取错字（横版画布守卫不受影响）。
        image = await new PdfOrientationDetector(_ocr).OrientAsync(image).ConfigureAwait(true);
        AppLog.Trace($"命名：第 {pageIndex + 1}/{pageCount} 页渲染 {image.Length / 1024} KB", null);
        op.Step($"第 {pageIndex + 1}/{pageCount} 页方向校正", $"{image.Length / 1024} KB");

        // Fast path for fixed-layout types: locate the name cell geometrically and
        // recognise only that crop — no full-page OCR, no text-order guessing.
        string? located = await CreditReportNameLocator
            .TryLocateNameAsync(image, _ocr, AppLog.Trace)
            .ConfigureAwait(true);
        op.Step($"第 {pageIndex + 1}/{pageCount} 页固定区域定位",
            located is null ? "未命中" : $"命中 {located}");
        if (located is not null)
        {
            AutoNameResult result = AutoNameResult.Create(located, CreditReportTypeShortName);
            AppLog.Trace($"命名：自动命名成功 → {result.SuggestedStem}", null);
            return result;
        }

        IReadOnlyList<RecognizedLine> lines = await _ocr
            .RecognizeBoundedOrDefaultAsync(image).ConfigureAwait(true);
        AppLog.Trace(
            $"命名：第 {pageIndex + 1}/{pageCount} 页 OCR 识别 {lines.Count} 行：{string.Join(" / ", lines.Select(l => l.Text))}",
            null);
        op.Step($"第 {pageIndex + 1}/{pageCount} 页整页 OCR", $"{lines.Count} 行");

        // The top-band probe can miss a label that full-page OCR sees (2-up spreads, small
        // header tables). Reuse the full-page bounds for the same cell read before giving up
        // on the fixed layout — no second band OCR needed.
        string? fromLines = await CreditReportNameLocator
            .TryReadFromLinesAsync(image, lines, _ocr, AppLog.Trace)
            .ConfigureAwait(true);
        op.Step($"第 {pageIndex + 1}/{pageCount} 页整页坐标定位",
            fromLines is null ? "未命中" : $"命中 {fromLines}");
        if (fromLines is not null)
        {
            AutoNameResult fromGeometry = AutoNameResult.Create(fromLines, CreditReportTypeShortName);
            AppLog.Trace($"命名：自动命名成功 → {fromGeometry.SuggestedStem}", null);
            return fromGeometry;
        }

        AutoNameResult? built = SmartNamingService.TryBuildName(lines, AppLog.Trace);
        op.Step($"第 {pageIndex + 1}/{pageCount} 页文本规则命名",
            built is null ? "未识别" : built.SuggestedStem);
        return built;
    }
}
