namespace WatermarkRemover.Scan.Core.Models;

/// <summary>
/// One captured page. A duplex sheet produces two pages sharing the same <see cref="SheetIndex"/>;
/// <see cref="IsBackSide"/> marks the rear one so downstream ordering and page numbering stay correct.
/// </summary>
public sealed class ScannedPage
{
    /// <summary>Source raster. Null when the capture path already encoded this page and dropped
    /// the raw frame; the PDF builder then consumes the encoded payload instead.</summary>
    public RasterImage? Image { get; init; }

    /// <summary>Zero-based position of the page in the final document order.</summary>
    public int Index { get; set; }

    /// <summary>Zero-based physical sheet number; both sides of a duplex sheet share it.</summary>
    public int SheetIndex { get; init; }

    public bool IsBackSide { get; init; }

    /// <summary>True when <see cref="BlankRemovalLevel"/> logic classified this page as blank.</summary>
    public bool IsBlank { get; set; }

    /// <summary>True when the page is a separator inserted because of a split setting.</summary>
    public bool IsSeparatorStart { get; set; }

    /// <summary>OCR text for this page, when the workflow ran an engine. Null when not recognised.</summary>
    public OcrResult? Ocr { get; set; }

    /// <summary>Word boxes in page pixel space, kept so a searchable PDF text layer can be emitted.</summary>
    public IReadOnlyList<OcrWord> Words { get; set; } = Array.Empty<OcrWord>();

    /// <summary>Human readable note added by the pipeline (processor that rejected/handled the page).</summary>
    public string? Note { get; set; }
}
