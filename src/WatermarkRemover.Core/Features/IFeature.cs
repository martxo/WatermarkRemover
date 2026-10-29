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

    /// <summary>Whether this feature can process the given file (typically by extension).</summary>
    bool CanHandle(string filePath);

    /// <summary>
    /// Extensions this feature accepts, lower-case with the dot. The shell's open-file dialog
    /// builds its filter from this list, so the picker can never offer a format the page would
    /// reject. Empty = the dialog falls back to PDFs only.
    /// </summary>
    IReadOnlyList<string> InputExtensions => Array.Empty<string>();

    /// <summary>
    /// Processes one file. Never throws for expected failures — problems are reported through
    /// <see cref="FeatureResult.Status"/>.
    /// </summary>
    FeatureResult Run(string inputPath, FeatureContext context);
}
