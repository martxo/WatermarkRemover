namespace WatermarkRemover.Core.Features.Ocr;

/// <summary>
/// Turns PDF pages into images for OCR. Implementations are swappable (Windows PDF renderer
/// today, PDFium later) — the feature above this interface never changes.
/// </summary>
public interface IPdfPageRenderer
{
    /// <summary>Renders every page of the PDF to an encoded image (PNG) at the requested DPI.</summary>
    Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi);

    /// <summary>
    /// Renders just the first page — enough for content-based naming, much cheaper than
    /// rendering a whole document.
    /// </summary>
    Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi);

    /// <summary>Total page count — the preview needs it before rendering any single page.</summary>
    Task<int> GetPageCountAsync(string pdfPath);

    /// <summary>
    /// Renders one page (0-based) — the preview shows a single page at a time. High-priority
    /// callers (orientation probes, blank-page scans, the on-screen canvas) jump ahead of the
    /// strip's low-priority tile fill in the process-wide render queue.
    /// </summary>
    Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi, bool priority = false);
}
