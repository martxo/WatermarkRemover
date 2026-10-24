using WatermarkRemover.Scan.Core.Models;

namespace WatermarkRemover.Scan.Core.Pipeline;

/// <summary>
/// Whether a duplex batch needs its pages reordered depends on who walked the paper:
/// <list type="bullet">
/// <item>Device-driven duplex (Brother ADK DUPLEX_* and WIA HandlingDuplex) transfers both
/// sides of every sheet back to back in one pass, so the stream already IS reading order
/// and must be passed through untouched — reordering it shreds the document (verified
/// against the vendor app on a real duplex job, 2026-10-19).</item>
/// <item>A manual flip-the-stack workflow scans all fronts first, then re-feeds the turned
/// stack so every rear arrives in reverse; interleaving fronts and rears restores reading
/// order there.</item>
/// </list>
/// The feed order is therefore an explicit setting on <see cref="ScanSettings"/>, not an
/// assumption this class makes.
/// </summary>
public static class PageOrderNormalizer
{
    /// <summary>
    /// Returns pages in reading order. Reordering applies only to front-then-reversed-rears
    /// batches: interleaved device duplex, flatbed runs and simplex runs already arrive in
    /// order, and an odd page count cannot be split into halves.
    /// </summary>
    public static IReadOnlyList<RasterImage> Normalize(
        IReadOnlyList<RasterImage> rawPages,
        ScanSettings settings)
    {
        IReadOnlyList<int> order = NormalizeOrder(rawPages.Count, settings);
        var ordered = new List<RasterImage>(order.Count);
        foreach (int index in order)
            ordered.Add(rawPages[index]);
        return ordered;
    }

    /// <summary>
    /// The same reading order as <see cref="Normalize"/>, expressed as source indexes. Lets a
    /// caller that has already encoded (or otherwise released) its page rasters reorder the
    /// finished payloads without keeping every raw bitmap alive.
    /// </summary>
    public static IReadOnlyList<int> NormalizeOrder(int pageCount, ScanSettings settings)
    {
        if (settings.Duplex == DuplexMode.Off ||
            settings.DuplexFeedOrder != DuplexFeedOrder.FrontsThenReversedRears ||
            settings.Feed == FeedMode.Flatbed ||
            pageCount < 2 ||
            pageCount % 2 != 0)
        {
            var identity = new int[pageCount];
            for (var i = 0; i < pageCount; i++) identity[i] = i;
            return identity;
        }

        var sheetCount = pageCount / 2;
        var order = new List<int>(pageCount);
        for (var sheet = 0; sheet < sheetCount; sheet++)
        {
            order.Add(sheet);                     // front of sheet N
            order.Add(pageCount - 1 - sheet);     // rear of sheet N, last first
        }

        return order;
    }

    /// <summary>Classifies a normalised page position into sheet index and side.</summary>
    public static (int SheetIndex, bool IsBackSide) ClassifyPage(int pageIndex, DuplexMode duplex) =>
        duplex == DuplexMode.Off
            ? (pageIndex, false)
            : (pageIndex / 2, pageIndex % 2 == 1);
}
