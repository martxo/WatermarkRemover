using WatermarkRemover.Scan.Core.Models;
using WatermarkRemover.Scan.Core.Pipeline;

namespace WatermarkRemover.Tests.Scan;

/// <summary>
/// The scan path now reorders finished pages instead of raw rasters, so the index-only order must
/// stay identical to <see cref="PageOrderNormalizer.Normalize"/> for every mode.
/// </summary>
public sealed class PageOrderNormalizerIndexTests
{
    private static ScanSettings ManualFlip() => new()
    {
        Duplex = DuplexMode.LongEdge,
        DuplexFeedOrder = DuplexFeedOrder.FrontsThenReversedRears,
        Feed = FeedMode.Adf,
    };

    private static RasterImage Page() => new(1, 1, new byte[4]);

    [Fact]
    public void Manual_flip_pair_order_matches_payload_normalisation()
    {
        var settings = ManualFlip();

        Assert.Equal(new[] { 0, 3, 1, 2 }, PageOrderNormalizer.NormalizeOrder(4, settings));

        var pages = Enumerable.Range(0, 4).Select(_ => Page()).ToList();
        var normalised = PageOrderNormalizer.Normalize(pages, settings);
        Assert.Equal(new[] { pages[0], pages[3], pages[1], pages[2] }, normalised);
    }

    [Fact]
    public void Interleaved_odd_flatbed_and_simplex_batches_keep_arrival_order()
    {
        var interleaved = ManualFlip();
        interleaved.DuplexFeedOrder = DuplexFeedOrder.Interleaved;
        Assert.Equal(new[] { 0, 1, 2 }, PageOrderNormalizer.NormalizeOrder(3, interleaved));

        var odd = ManualFlip();
        Assert.Equal(new[] { 0, 1, 2 }, PageOrderNormalizer.NormalizeOrder(3, odd));

        var flatbed = ManualFlip();
        flatbed.Feed = FeedMode.Flatbed;
        Assert.Equal(new[] { 0, 1, 2, 3 }, PageOrderNormalizer.NormalizeOrder(4, flatbed));

        var simplex = ManualFlip();
        simplex.Duplex = DuplexMode.Off;
        Assert.Equal(new[] { 0, 1 }, PageOrderNormalizer.NormalizeOrder(2, simplex));
    }
}
