namespace WatermarkRemover.Core.Features.Ocr;

/// <summary>
/// Turns PDF pages into images for OCR. Implementations are swappable (Windows PDF renderer
/// today, PDFium later) — the feature above this interface never changes.
/// </summary>
public interface IPdfPageRenderer
{
    /// <summary>Renders every page of the PDF to an encoded image (PNG) at the requested DPI.</summary>
    Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi);
}
