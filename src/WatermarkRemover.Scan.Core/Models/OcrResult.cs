namespace WatermarkRemover.Scan.Core.Models;

/// <summary>
/// A single recognised word together with its bounding box in page pixel coordinates.
/// Keeping boxes (not just the text) is what makes an OCR text layer over the scanned image possible.
/// </summary>
public sealed record OcrWord(string Text, int X, int Y, int Width, int Height, float Confidence);

/// <summary>Result of running one page through <c>IOcrEngine</c>.</summary>
public sealed class OcrResult
{
    public required string EngineId { get; init; }

    public required string EngineDisplayName { get; init; }

    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<OcrWord> Words { get; init; } = Array.Empty<OcrWord>();

    public float AverageConfidence { get; init; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    public static OcrResult Empty(string engineId, string displayName) => new()
    {
        EngineId = engineId,
        EngineDisplayName = displayName,
        Text = string.Empty,
    };
}
