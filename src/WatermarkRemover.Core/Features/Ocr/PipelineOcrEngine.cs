using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Geometry;
using WatermarkRemover.Core.Logging;
using WatermarkRemover.Core.Recognition;

namespace WatermarkRemover.Core.Features.Ocr;

/// <summary>
/// Composes the two user-selectable pipeline stages — a layout block (how the page is split)
/// and a text engine (how each split is read) — behind the plain <see cref="IOcrEngine"/> the
/// features already consume, so OcrFeature / WatermarkFeature need no changes. A passthrough
/// block short-circuits to direct recognition (byte-identical to the pre-pipeline behaviour);
/// any other block cuts the page into regions, recognises each crop in reading order and
/// concatenates the lines. A failing layout model degrades to the full image instead of
/// breaking recognition.
/// When a <see cref="RecognitionRunScope"/> is open (one job run), the same single recognition
/// pass also carries coordinates — crop-local bounds are mapped back to full-page space and
/// every return path records the page into the scope for the recognition report. With no
/// scope open (unit tests, probes) behaviour and output are unchanged, byte for byte.
/// </summary>
public sealed class PipelineOcrEngine : IOcrEngine, IRegionProbe, IBoundedOcrEngine
{
    /// <summary>Each crop is grown by 2% on every side so glyphs on region borders are not clipped.</summary>
    private const double Padding = 0.02;

    /// <summary>Process-wide recognition-call counter: IOcrEngine carries no page number, and the
    /// call order equals the page order, so 管线调用n correlates with the feature's 第n页识别 line.</summary>
    private static int _callCount;

    private readonly ILayoutStage _stage;
    private readonly IOcrEngine _inner;
    private readonly Func<byte[], RectD, byte[]>? _crop;
    private readonly Action<string, Exception?>? _trace;

    /// <param name="stage">The chosen layout block.</param>
    /// <param name="inner">The chosen text engine; also the recogniser used for anchor probes.</param>
    /// <param name="crop">Cuts a normalized region out of the encoded image (App-side codec).
    /// Required by non-passthrough blocks; when null the pipeline recognises the full image.</param>
    /// <param name="trace">Per-stage timing lines for performance analysis; null disables them.</param>
    public PipelineOcrEngine(
        ILayoutStage stage,
        IOcrEngine inner,
        Func<byte[], RectD, byte[]>? crop = null,
        Action<string, Exception?>? trace = null)
    {
        _stage = stage ?? throw new ArgumentNullException(nameof(stage));
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _crop = crop;
        _trace = trace;
    }

    public string DisplayName =>
        _stage.IsPassthrough ? _inner.DisplayName : $"{_stage.Name} + {_inner.DisplayName}";

    public async Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes)
    {
        var timer = new StageTimer(_trace, $"管线调用{Interlocked.Increment(ref _callCount)}");
        RecognitionRunScope? scope = RecognitionRunScope.Current;
        // Report collection wants coordinates; plain runs keep the exact pre-scope code path.
        bool withBounds = scope is not null && _inner is IBoundedOcrEngine;

        IReadOnlyList<RecognizedLine> lines;
        IReadOnlyList<LayoutRegion> regions = Array.Empty<LayoutRegion>();

        if (_stage.IsPassthrough || _crop is null)
        {
            lines = await ReadAsync(imageBytes, withBounds);
            timer.Stage("整图直通识别", $"{lines.Count} 行（{_inner.DisplayName}）");
        }
        else
        {
            regions = await DetectRegionsSafeAsync(imageBytes, timer);
            regions = DropPageCovering(regions);
            if (regions.Count == 0 || (regions.Count == 1 && IsFullImage(regions[0])))
            {
                timer.Emit("无可切分区域，回退整图");
                lines = await ReadAsync(imageBytes, withBounds);
                timer.Stage("整图识别", $"{lines.Count} 行（{_inner.DisplayName}）");
                // The fallback reads the full image — record no split rather than the dead regions.
                regions = Array.Empty<LayoutRegion>();
            }
            else
            {
                regions = ReadingOrderSorter.Sort(regions);
                var collected = new List<RecognizedLine>();
                // Detectors emit overlapping boxes (a "title" over an "abandon" over the same area), so
                // the same text can be cropped twice — recognise each region once, then drop lines that
                // were already produced by an earlier region. Duplicates inside a single crop are left
                // untouched: there they are real text, not an overlap artifact.
                var emitted = new HashSet<string>();
                int droppedDuplicates = 0;
                foreach (LayoutRegion region in regions)
                {
                    RectD rect = Expand(region.Bounds, Padding);
                    byte[] crop = _crop(imageBytes, rect);
                    IReadOnlyList<RecognizedLine> regionLines = await ReadAsync(crop, withBounds);
                    foreach (RecognizedLine line in regionLines)
                    {
                        if (!emitted.Contains(line.Text.Trim()))
                            collected.Add(withBounds ? line with { Bounds = MapToPage(line.Bounds, rect) } : line);
                        else
                            droppedDuplicates++;
                    }
                    foreach (RecognizedLine line in regionLines)
                        emitted.Add(line.Text.Trim());
                }
                lines = collected;

                // One aggregated line per page: N regions in a multi-region document would otherwise
                // flood the log and drown the feature-level stages (the agreed analysis granularity).
                timer.Stage("区域识别",
                    $"{regions.Count} 区，去重丢弃 {droppedDuplicates} 行，得 {lines.Count} 行（{_inner.DisplayName}）");
                timer.Emit($"本页合计 {timer.ElapsedMs} ms");
            }
        }

        if (scope is not null)
            TryRecordPage(scope, imageBytes, regions, lines, timer);

        var texts = new List<string>(lines.Count);
        foreach (RecognizedLine line in lines) texts.Add(line.Text);
        return texts;
    }

    /// <summary>
    /// The anchor probe: a full-image pass through the inner engine, carrying per-line bounds
    /// when the engine supports positioning (<see cref="IBoundedOcrEngine"/>).
    /// </summary>
    Task<IReadOnlyList<RecognizedLine>> IRegionProbe.RecognizeAsync(byte[] imageBytes) =>
        _inner is IBoundedOcrEngine bounded
            ? bounded.RecognizeWithBoundsAsync(imageBytes)
            : RecognizeWithoutBoundsAsync(imageBytes);

    /// <summary>
    /// Whole-image line bounds, deliberately bypassing the layout stage: callers of this path
    /// (orientation detection) need the page's own geometry, which region splitting would destroy.
    /// Fails loudly when the inner engine cannot position text — callers degrade to plain text.
    /// </summary>
    public Task<IReadOnlyList<RecognizedLine>> RecognizeWithBoundsAsync(byte[] imageBytes) =>
        _inner is IBoundedOcrEngine bounded
            ? bounded.RecognizeWithBoundsAsync(imageBytes)
            : throw new InvalidOperationException("当前 OCR 引擎无法提供文字位置信息。");

    /// <summary>One recognition pass: with coordinates when requested and supported, otherwise
    /// the plain text path wrapped with null bounds (identical to the pre-scope behaviour).</summary>
    private Task<IReadOnlyList<RecognizedLine>> ReadAsync(byte[] imageBytes, bool withBounds) =>
        withBounds && _inner is IBoundedOcrEngine bounded
            ? bounded.RecognizeWithBoundsAsync(imageBytes)
            : RecognizeWithoutBoundsAsync(imageBytes);

    /// <summary>Feeds one page into the report scope. Recording must never fail a recognition.</summary>
    private void TryRecordPage(RecognitionRunScope scope, byte[] imageBytes,
        IReadOnlyList<LayoutRegion> regions, IReadOnlyList<RecognizedLine> lines, StageTimer timer)
    {
        try
        {
            ImageSize.TryGetSize(imageBytes, out int width, out int height);
            scope.RecordPage(
                width > 0 ? width : null,
                height > 0 ? height : null,
                ImageSize.OrientationOf(width, height),
                _stage.Name,
                _inner.DisplayName,
                regions,
                lines);
        }
        catch (Exception ex)
        {
            timer.Emit($"记录识别页失败（不影响识别结果）：{ex.Message}");
        }
    }

    /// <summary>Maps a crop-local normalized box back into full-page normalized space, clamped.</summary>
    private static RectD? MapToPage(RectD? local, RectD cropRect)
    {
        if (local is not { } b) return null;

        double x = Math.Clamp(cropRect.X + b.X * cropRect.Width, 0, 1);
        double y = Math.Clamp(cropRect.Y + b.Y * cropRect.Height, 0, 1);
        double right = Math.Clamp(cropRect.X + (b.X + b.Width) * cropRect.Width, 0, 1);
        double lower = Math.Clamp(cropRect.Y + (b.Y + b.Height) * cropRect.Height, 0, 1);
        return new RectD(x, y, Math.Max(0, right - x), Math.Max(0, lower - y));
    }

    /// <summary>A layout failure must not break recognition — fall back to the full image.</summary>
    private async Task<IReadOnlyList<LayoutRegion>> DetectRegionsSafeAsync(byte[] imageBytes, StageTimer timer)
    {
        try
        {
            IReadOnlyList<LayoutRegion> regions = await _stage.DetectRegionsAsync(imageBytes, this);
            timer.Stage("版面检测", $"{_stage.Name}，检出 {regions.Count} 区");
            return regions;
        }
        catch (Exception ex)
        {
            timer.Emit($"版面检测失败，回退整图（已耗时 {timer.ElapsedMs} ms）：{ex.Message}");
            return Array.Empty<LayoutRegion>();
        }
    }

    private async Task<IReadOnlyList<RecognizedLine>> RecognizeWithoutBoundsAsync(byte[] imageBytes)
    {
        IReadOnlyList<string> texts = await _inner.RecognizeAsync(imageBytes);
        var lines = new RecognizedLine[texts.Count];
        for (int i = 0; i < texts.Count; i++)
            lines[i] = new RecognizedLine(texts[i], null);
        return lines;
    }

    private static bool IsFullImage(LayoutRegion region) =>
        region.Bounds.X <= 0.001 && region.Bounds.Y <= 0.001 &&
        region.Bounds.Width >= 0.999 && region.Bounds.Height >= 0.999;

    /// <summary>
    /// Some detectors emit a page-level box (e.g. PP-DocLayout's "image"/"table" spanning the
    /// whole page). When finer regions exist, keeping it would re-OCR every smaller crop's
    /// content and duplicate lines, so it is dropped — unless nothing else was detected, in
    /// which case the page-level box still carries the text.
    /// </summary>
    private static IReadOnlyList<LayoutRegion> DropPageCovering(IReadOnlyList<LayoutRegion> regions)
    {
        if (regions.Count <= 1) return regions;
        var kept = regions
            .Where(r => r.Bounds.Width < 0.85 || r.Bounds.Height < 0.9)
            .ToList();
        return kept.Count > 0 ? kept : regions;
    }

    /// <summary>Grows a normalized region by <paramref name="pad"/> per side, clamped to the image.
    /// Note: RectD.Bottom/Top follow PDF conventions (bottom-left origin), while layout regions
    /// use image space — so compute the lower edge from Y + Height explicitly.</summary>
    private static RectD Expand(RectD r, double pad)
    {
        double dx = r.Width * pad, dy = r.Height * pad;
        double x = Math.Max(0, r.X - dx);
        double y = Math.Max(0, r.Y - dy);
        double right = Math.Min(1, r.X + r.Width + dx);
        double lower = Math.Min(1, r.Y + r.Height + dy);
        return new RectD(x, y, right - x, lower - y);
    }
}
