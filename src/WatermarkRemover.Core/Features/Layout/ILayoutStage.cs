namespace WatermarkRemover.Core.Features.Layout;

/// <summary>
/// Full-image text pass injected by <c>PipelineOcrEngine</c> so anchor-based stages can locate
/// labels (with bounds when the engine supports positioning) before cutting value regions.
/// </summary>
public interface IRegionProbe
{
    Task<IReadOnlyList<RecognizedLine>> RecognizeAsync(byte[] imageBytes);
}

/// <summary>
/// One interchangeable building block of the recognition pipeline's layout stage: splits a page
/// into regions that the pipeline then recognises separately, in reading order. New stages plug
/// in by implementing this interface and registering a factory — settings UI, selection and
/// fallback follow automatically.
/// </summary>
public interface ILayoutStage
{
    /// <summary>Human-readable stage name, surfaced in job summaries and the settings page.</summary>
    string Name { get; }

    /// <summary>
    /// True for a no-op stage (single full-image region). The pipeline then skips cropping
    /// entirely — output is byte-identical to recognising the page directly — and the composed
    /// display name collapses to the inner engine's.
    /// </summary>
    bool IsPassthrough => false;

    /// <summary>
    /// Splits the image into regions in normalized coordinates. An empty result means
    /// "give up" and the pipeline falls back to a single full-image region.
    /// </summary>
    Task<IReadOnlyList<LayoutRegion>> DetectRegionsAsync(byte[] imageBytes, IRegionProbe probe);
}
