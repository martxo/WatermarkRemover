namespace WatermarkRemover.App.ViewModels;

/// <summary>
/// One atomic unit of the inline thumbnail strip: [left gap | page tile | right gap]. The
/// strip's wrap panel can only break BETWEEN units, so a wrapped row always opens with its
/// own plus slot instead of a bare tile — the second row's first page keeps its leading plus
/// and the columns line up across rows. The right slot is filled only on the last unit (the
/// trailing bookend); between two adjacent pages a single plus serves both sides — the
/// earlier page's right edge and the later page's left edge — which is why interior units
/// carry <c>null</c> there (the unit template collapses a null tail — a ContentControl would
/// otherwise instantiate the template for null content and paint a dead second plus). Pure
/// marker: the unit template nests the existing gap and tile templates around these parts.
/// </summary>
public sealed record PdfPreviewStripUnitViewModel(
    PdfPreviewGapViewModel Gap,
    PdfPreviewThumbnailViewModel Tile,
    PdfPreviewGapViewModel? Tail);
