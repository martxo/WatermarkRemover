namespace WatermarkRemover.Core.Features.Ocr;

/// <summary>
/// A text-recognition engine. Implementations are swappable (system OCR today, a local PP-OCR
/// model later) — the feature above this interface never changes.
/// </summary>
public interface IOcrEngine
{
    /// <summary>
    /// Recognises text in an encoded image (PNG/BMP/JPEG). Returns the recognised lines in
    /// reading order; an image with no text yields an empty list.
    /// </summary>
    /// <exception cref="InvalidOperationException">When the engine is unusable on this machine.</exception>
    Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes);
}
