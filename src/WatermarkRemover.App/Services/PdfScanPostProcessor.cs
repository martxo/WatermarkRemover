using System.Collections.Concurrent;
using System.IO;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Imaging;
using WatermarkRemover.Ocr;
using WatermarkRemover.Pdf;
using WatermarkRemover.Pdf.Preview;

namespace WatermarkRemover.App.Services;

/// <summary>Outcome of one auto-orientation probe.</summary>
public readonly record struct AutoOrientResult(bool OcrAvailable, int Steps, bool Applied);

/// <summary>
/// The scan-specific passes that run after a document is loaded: drop blank backs and turn a
/// sideways first page. It is UI-free product policy — pixel verdicts, OCR votes and in-place page
/// edits only — while the preview view model owns the observable state and rebuilds its tiles.
///
/// Every pass is silent on failure by contract: a scan must never be held hostage by a helper.
/// </summary>
public sealed class PdfScanPostProcessor
{
    private readonly PdfPageRenderCache _cache;
    private readonly PdfDocumentFile _file;
    private readonly PdfPageEditService _edits;
    private readonly IOcrEngine? _ocr;
    private readonly CancellationToken _cancellationToken;
    private readonly Action<string>? _debug;
    private readonly Action<string>? _info;

    public PdfScanPostProcessor(
        PdfPageRenderCache cache,
        PdfDocumentFile file,
        IOcrEngine? ocr,
        CancellationToken cancellationToken = default,
        Action<string>? debug = null,
        Action<string>? info = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _file = file ?? throw new ArgumentNullException(nameof(file));
        _edits = new PdfPageEditService(cache, file);
        _ocr = ocr;
        _cancellationToken = cancellationToken;
        _debug = debug;
        _info = info;
    }

    public bool HasOcr => _ocr is not null;

    /// <summary>Blank verdict for one page; an unreadable page counts as content so the
    /// orientation probe still gets its chance.</summary>
    public async Task<bool> IsPageBlankAsync(int pageIndex, int dpi)
    {
        try
        {
            byte[] pagePng = await _cache.GetPageBytesAsync(pageIndex, dpi, priority: true).ConfigureAwait(false);
            return await Task.Run(() => BlankPageDetector.IsBlank(pagePng)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _debug?.Invoke($"空白判定失败：第 {pageIndex + 1} 页，按有内容处理（{ex.Message}）");
            return false;
        }
    }

    /// <summary>Judges every page from pixels (no OCR). Pages are rendered at display DPI, which
    /// is also the raster the tiles need, so the pass feeds the strip instead of duplicating work.</summary>
    public async Task<IReadOnlyList<int>> FindBlankPagesAsync(
        int pageCount,
        int dpi,
        IProgress<(int Index, bool Blank)>? progress = null)
    {
        var blankPages = new List<int>();
        for (int i = 0; i < pageCount; i++)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            bool blank = await IsPageBlankAsync(i, dpi).ConfigureAwait(false);
            if (blank) blankPages.Add(i);
            progress?.Report((i, blank));
        }
        return blankPages;
    }

    /// <summary>
    /// Deletes the judged blank pages in one in-place save. An all-blank scan keeps its first
    /// sheet rather than turning into nothing. Returns how many pages were removed.
    /// </summary>
    public async Task<int> DeleteBlankPagesAsync(int pageCount, IReadOnlyList<int> blankPages)
    {
        var toDelete = blankPages.ToList();
        if (toDelete.Count >= pageCount) toDelete.Remove(0);
        if (toDelete.Count == 0) return 0;

        return await _edits.DeletePagesAsync(toDelete).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders page one, lets the OCR engine vote on the four quarter turns, and turns the whole
    /// document in one save when the verdict is decisive. Returns what happened; reporting it to
    /// the user is the view model's job.
    /// </summary>
    public async Task<AutoOrientResult> AutoOrientAsync(int pageCount, int probeDpi)
    {
        if (_ocr is null || pageCount <= 0) return new AutoOrientResult(false, 0, false);

        byte[] pagePng = await _cache.GetPageBytesAsync(0, probeDpi, priority: true).ConfigureAwait(false);
        var detector = new PdfOrientationDetector(_ocr);
        int? verdict = await Task.Run(
            () => detector.DetectClockwiseStepsAsync(pagePng, _cancellationToken)).ConfigureAwait(false);
        _cache.InvalidateKey(0, probeDpi); // probe raster is never displayed
        _cancellationToken.ThrowIfCancellationRequested();

        if (verdict is null) return new AutoOrientResult(false, 0, false);
        int steps = verdict.Value;
        if (steps == 0) return new AutoOrientResult(true, 0, false);

        await _edits.RotateAllAsync(steps).ConfigureAwait(false);
        return new AutoOrientResult(true, steps, true);
    }

    /// <summary>
    /// Auto-orientation scoped to pages an insert just brought in: probe every new page, collect
    /// the crooked ones, then apply all turns in ONE in-place save. A page the engine cannot read
    /// (blank back, unusable OCR) stays untouched.
    /// </summary>
    public async Task<int> OrientPagesAsync(int firstIndex, int count, int pageCount, int probeDpi)
    {
        if (_ocr is null || count <= 0) return 0;
        var detector = new PdfOrientationDetector(_ocr);
        var turns = new List<(int Index, int Steps)>();
        var probed = new List<int>();

        for (int index = firstIndex; index < firstIndex + count && index < pageCount; index++)
        {
            if (_cancellationToken.IsCancellationRequested) return 0;

            byte[] pagePng = await _cache.GetPageBytesAsync(index, probeDpi, priority: true).ConfigureAwait(false);
            if (_cancellationToken.IsCancellationRequested) return 0;

            probed.Add(index);
            // Blank inserted sheets (a misfeed, or the back of a single-sided insert) have no
            // direction to vote on; the pixel check keeps them from paying four OCR passes.
            if (await Task.Run(() => BlankPageDetector.IsBlank(pagePng)).ConfigureAwait(false)) continue;

            int? steps = await Task.Run(
                () => detector.DetectClockwiseStepsAsync(pagePng, _cancellationToken)).ConfigureAwait(false);
            if (_cancellationToken.IsCancellationRequested) return 0;
            if (steps is null) break; // unusable OCR: keep the pages as scanned
            if (steps != 0) turns.Add((index, steps.Value));
        }

        // Probe rasters are never displayed (tiles and canvas read display DPI); unturned pages'
        // entries are pure ballast, turned pages lose their whole key set below.
        foreach (int index in probed.Where(i => turns.All(t => t.Index != i)))
            _cache.InvalidateKey(index, probeDpi);

        if (turns.Count == 0) return 0;

        await _edits.RotatePagesAsync(turns).ConfigureAwait(false);

        _info?.Invoke($"已摆正插入页：{Path.GetFileName(_file.Path)}，{turns.Count}/{count} 页需旋转" +
                      $"（{string.Join("、", turns.Select(t => $"第{t.Index + 1}页顺时针{t.Steps * 90}°"))}），单次写盘");
        return turns.Count;
    }
}
