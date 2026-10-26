namespace WatermarkRemover.App.ViewModels;

/// <summary>
/// One atomic unit of the inline thumbnail strip: [left gap | page tile]. The strip's wrap
/// panel can only break BETWEEN units, so a wrapped row always opens with its own plus slot
/// instead of a bare tile — the second row's first page keeps its leading plus and the
/// columns line up across rows. Between two adjacent pages a single plus serves both sides —
/// the earlier page's right edge and the later page's left edge.
///
/// Every unit is exactly one gap plus one tile wide, the last page included. That equality is
/// a layout invariant, not a coincidence: the wrap panel packs whole units, so a unit that is
/// even 14px wider than its siblings is bumped to the next row whenever a row has room for a
/// tile but not for tile+14 — and the row keeps a cell-sized hole where the page should have
/// been. A trailing "append after the last page" slot used to widen exactly that last unit and
/// did this (the 2026-10-22 report); the slot is now a SEPARATE item after the units (see
/// <see cref="PdfPreviewGapViewModel"/> and <c>RebuildStripItems</c>), so no page unit's width
/// ever changes. Anything added to this unit must not affect its wrap width.
/// </summary>
public sealed record PdfPreviewStripUnitViewModel(
    PdfPreviewGapViewModel Gap,
    PdfPreviewThumbnailViewModel Tile);
