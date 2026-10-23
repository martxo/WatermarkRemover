using System.IO;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Naming;
using WatermarkRemover.App.Ocr;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Features.Naming;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App.Services;

/// <summary>
/// Content-based rename for a freshly produced PDF (a scan, or a share-link upload): render →
/// orient → fixed-region fast path → OCR → <see cref="SmartNamingService"/>, then move the file.
/// Any failure returns the original path — naming must never fail the operation that produced it.
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
    /// recognised, otherwise <paramref name="pdfPath"/> unchanged. Pages are probed in order up to
    /// <see cref="MaxNamingProbePages"/> (or one when the settings pin first-page naming) because
    /// the first face of a duplex scan is often a blank back or a different document.
    /// </summary>
    public async Task<string> TryRenameAsync(string pdfPath)
    {
        if (!_settings.AutoNameByContent) return pdfPath;

        try
        {
            int pageCount = await _renderer.GetPageCountAsync(pdfPath).ConfigureAwait(true);
            int probeLimit = _settings.NameFromFirstPageOnly ? 1 : MaxNamingProbePages;
            AutoNameResult? autoName = null;
            for (int pageIndex = 0; pageIndex < pageCount && pageIndex < probeLimit && autoName is null; pageIndex++)
            {
                byte[] image = await _renderer.RenderPageAsync(pdfPath, pageIndex, NamingRenderDpi, priority: true).ConfigureAwait(true);
                // 摆正后再识别：ADF 躺倒页在方向检测完成前就进命名链会取错字（横版画布守卫不受影响）。
                image = await new PdfOrientationDetector(_ocr).OrientAsync(image).ConfigureAwait(true);
                AppLog.Trace($"命名：第 {pageIndex + 1}/{pageCount} 页渲染 {image.Length / 1024} KB", null);

                // Fast path for fixed-layout types: locate the name cell geometrically and
                // recognise only that crop — no full-page OCR, no text-order guessing.
                string? located = await CreditReportNameLocator
                    .TryLocateNameAsync(image, _ocr, AppLog.Trace)
                    .ConfigureAwait(true);
                if (located is not null)
                {
                    autoName = AutoNameResult.Create(located, CreditReportTypeShortName);
                    AppLog.Trace($"命名：自动命名成功 → {autoName.SuggestedStem}", null);
                    continue;
                }

                IReadOnlyList<RecognizedLine> lines = await _ocr
                    .RecognizeBoundedOrDefaultAsync(image).ConfigureAwait(true);
                AppLog.Trace(
                    $"命名：第 {pageIndex + 1}/{pageCount} 页 OCR 识别 {lines.Count} 行：{string.Join(" / ", lines.Select(l => l.Text))}",
                    null);
                autoName = SmartNamingService.TryBuildName(lines, AppLog.Trace);
            }
            if (autoName is null) return pdfPath;

            string directory = Path.GetDirectoryName(pdfPath)!;
            string target = OutputNameGenerator.FindAvailablePath(directory, autoName.SuggestedStem + Path.GetExtension(pdfPath));
            if (string.Equals(target, pdfPath, StringComparison.OrdinalIgnoreCase)) return pdfPath;

            // The strip may still hold the file for a page write (e.g. the user rotated a page
            // while the naming OCR ran); a short backoff beats losing the good name.
            for (int attempt = 1; ; attempt++)
            {
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
            return target;
        }
        catch (Exception ex)
        {
            AppLog.Trace("命名：扫描自动命名过程异常，保留时间戳命名", ex);
            return pdfPath;
        }
    }
}
