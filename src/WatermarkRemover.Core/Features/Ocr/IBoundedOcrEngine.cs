using WatermarkRemover.Core.Features.Layout;

namespace WatermarkRemover.Core.Features.Ocr;

/// <summary>
/// Optional engine capability: also report where each recognised line sits in the image
/// (normalized coordinates). The pipeline's region probe forwards this to anchor-based layout
/// blocks — engines without the capability still work everywhere, they just cannot drive the
/// anchor block (its probes then find no anchors and degrade to the full image).
/// </summary>
public interface IBoundedOcrEngine : IOcrEngine
{
    Task<IReadOnlyList<RecognizedLine>> RecognizeWithBoundsAsync(byte[] imageBytes);
}
