namespace WatermarkRemover.Core.Features.Ocr;

/// <summary>
/// A text-recognition engine. Implementations are swappable (system OCR today, a local PP-OCR
/// model later) — the feature above this interface never changes.
/// </summary>
public interface IOcrEngine
{
    /// <summary>Human-readable engine name, surfaced in job summaries so users know which
    /// recogniser produced the text (e.g. 微信OCR vs Windows 原生 OCR).</summary>
    string DisplayName { get; }

    /// <summary>
    /// Recognises text in an encoded image (PNG/BMP/JPEG). Returns the recognised lines in
    /// reading order; an image with no text yields an empty list.
    /// </summary>
    /// <exception cref="InvalidOperationException">When the engine is unusable on this machine.</exception>
    Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes);
}
