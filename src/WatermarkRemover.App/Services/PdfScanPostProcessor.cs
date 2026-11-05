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
    /// 未裁决页的定向兜底：只探测给定的页并一次写盘，绝不碰已裁决页（逐页模式下整份旋转
    /// 会把已正确的页又转歪）。空白/横版/OCR 不可读的页保持原样。
    ///
    /// 探测结果先过整份一致性（<see cref="ReconcileHalfTurns"/>）：一次进纸的所有页共享同一个
    /// 物理朝向，个别页被 OCR 判成相差 180° 是票面误差，不能让它们单独翻反（v1.16.93 实测
    /// 刘健 (3).pdf 第 3/4 页就是这样被兜底转成 90°，整份其余页是 270°）。
    /// </summary>
    /// <returns>实际被转动的页数。</returns>
    public async Task<int> OrientPagesAsync(
        int pageCount, IReadOnlyList<int> pageIndices, int probeDpi)
    {
        if (_ocr is null || pageIndices.Count == 0) return 0;
        var detector = new PdfOrientationDetector(_ocr);
        var verdicts = new List<(int Index, int Steps)>();
        var probed = new List<int>();

        foreach (int index in pageIndices.Distinct())
        {
            if (index < 0 || index >= pageCount) continue;
            if (_cancellationToken.IsCancellationRequested) return 0;

            probed.Add(index);
            byte[] pagePng;
            try
            {
                pagePng = await _cache.GetPageBytesAsync(index, probeDpi, priority: true).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _debug?.Invoke($"兜底摆正：第 {index + 1} 页渲染失败（{ex.Message}），保持原样");
                continue;
            }
            if (_cancellationToken.IsCancellationRequested) return 0;

            if (await Task.Run(() => BlankPageDetector.IsBlank(pagePng)).ConfigureAwait(false)) continue;
            int? steps = await Task.Run(
                () => detector.DetectClockwiseStepsAsync(pagePng, _cancellationToken)).ConfigureAwait(false);
            if (_cancellationToken.IsCancellationRequested) return 0;
            if (steps is null) continue; // OCR 不可读：保持原样
            verdicts.Add((index, steps.Value));
        }

        List<(int Index, int Steps)> turns = ReconcileHalfTurns(verdicts);

        foreach (int index in probed.Where(i => turns.All(t => t.Index != i)))
            _cache.InvalidateKey(index, probeDpi);

        if (turns.Count == 0) return 0;

        await _edits.RotatePagesAsync(turns).ConfigureAwait(false);
        _info?.Invoke($"已定向摆正未裁决页：{Path.GetFileName(_file.Path)}，" +
                      $"{turns.Count}/{probed.Count} 页（{string.Join("、", turns.Select(t => $"第{t.Index + 1}页顺时针{t.Steps * 90}°"))}），单次写盘");
        return turns.Count;
    }

    /// <summary>整份一致性所需的最少同轴页数；不足时逐页保留原判，避免小样本被强行统一。</summary>
    internal const int ConsensusMinPages = 3;

    /// <summary>
    /// 把同一文字轴（0/180 或 90/270）的页判成同一朝向：同轴页的票相差 180° 只可能是 OCR 的
    /// 半圈误差，而不是页面真的各自朝向不同。占比达到 2/3 的朝向作为该轴的整份结论，离群页
    /// 按结论转；票数不足或平票时保留每页自己的判定。不同文字轴互不影响（真实横版表格页
    /// 不会被竖版多数拖走）。返回需要写入的 (页索引, 顺时针转数)，0 转不返回。
    /// </summary>
    internal static List<(int Index, int Steps)> ReconcileHalfTurns(
        IReadOnlyList<(int Index, int Steps)> verdicts)
    {
        var turns = new List<(int Index, int Steps)>();
        foreach (int axis in new[] { 0, 1 })
        {
            List<(int Index, int Steps)> group = verdicts
                .Where(v => ((v.Steps % 4) + 4) % 4 % 2 == axis)
                .ToList();
            if (group.Count == 0) continue;

            int dominant = group.Count >= ConsensusMinPages ? DominantSteps(group) : -1;
            foreach ((int index, int steps) in group)
            {
                int applied = dominant >= 0 ? dominant : steps;
                if (applied != 0) turns.Add((index, applied));
            }
        }

        return turns;
    }

    /// <summary>占比达到 2/3 的唯一最高票；不足或平票返回 -1（不构成整份结论）。</summary>
    private static int DominantSteps(IReadOnlyList<(int Index, int Steps)> group)
    {
        var counts = new Dictionary<int, int>();
        foreach ((_, int steps) in group)
            counts[steps] = counts.GetValueOrDefault(steps) + 1;

        int bestSteps = -1, bestCount = 0;
        bool tied = false;
        foreach ((int steps, int count) in counts)
        {
            if (count > bestCount)
            {
                bestSteps = steps;
                bestCount = count;
                tied = false;
            }
            else if (count == bestCount)
            {
                tied = true;
            }
        }

        return !tied && bestCount >= Math.Ceiling(group.Count * 2.0 / 3.0) ? bestSteps : -1;
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
