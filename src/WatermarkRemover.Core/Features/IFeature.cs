namespace WatermarkRemover.Core.Features;

/// <summary>
/// One capability of the application (watermark removal, OCR, cloud upload, …). Features are
/// registered in a <see cref="FeatureRegistry"/> and selected per input file; everything above
/// this interface (queue, naming, logging, UI) is feature-agnostic.
/// </summary>
public interface IFeature
{
    /// <summary>Stable machine identifier, e.g. "watermark.badge".</summary>
    string Id { get; }

    /// <summary>Human-readable name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Hint shown on the job card while this feature processes a file. Features override it for
    /// a precise wording (OCR, scan…); the default keeps newly registered features correct
    /// without touching the queue.
    /// </summary>
    string ProcessingText => $"正在处理「{DisplayName}」…";

    /// <summary>Whether this feature can process the given file (typically by extension).</summary>
    bool CanHandle(string filePath);

    /// <summary>
    /// Processes one file. Never throws for expected failures — problems are reported through
    /// <see cref="FeatureResult.Status"/>.
    /// </summary>
    FeatureResult Run(string inputPath, FeatureContext context);
}
