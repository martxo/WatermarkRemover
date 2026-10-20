namespace WatermarkRemover.Core.Features.Ocr;

using WatermarkRemover.Core.Features.Layout;

/// <summary>
/// Capability adapter over <see cref="IOcrEngine"/>: callers that benefit from line bounds
/// (naming drops 竖排 label-column strips by their box shape) go through here and simply get
/// plain lines when the resolved engine cannot report boxes.
/// </summary>
public static class OcrEngineExtensions
{
    /// <summary>Recognises a page, keeping line bounds whenever the engine can report them.</summary>
    public static async Task<IReadOnlyList<RecognizedLine>> RecognizeBoundedOrDefaultAsync(
        this IOcrEngine engine, byte[] imageBytes)
    {
        if (engine is IBoundedOcrEngine bounded)
            return await bounded.RecognizeWithBoundsAsync(imageBytes).ConfigureAwait(false);

        IReadOnlyList<string> texts = await engine.RecognizeAsync(imageBytes).ConfigureAwait(false);
        return texts.Select(text => new RecognizedLine(text, null)).ToList();
    }
}
