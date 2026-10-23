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

    /// <summary>Segoe MDL2 glyph shown in the sidebar; unknown features get a generic page icon.</summary>
    string Glyph => "\uE7C3";

    /// <summary>One-line capability note shown beside the page title.</summary>
    string Tagline => string.Empty;

    /// <summary>
    /// Which shell page hosts this feature. The ViewLocator maps the value to a DataTemplate, so
    /// a new feature only declares its page kind instead of editing the shell's visual tree.
    /// </summary>
    FeaturePageKind PageKind => FeaturePageKind.Files;

    /// <summary>Whether this feature can process the given file (typically by extension).</summary>
    bool CanHandle(string filePath);

    /// <summary>
    /// Processes one file. Never throws for expected failures — problems are reported through
    /// <see cref="FeatureResult.Status"/>.
    /// </summary>
    FeatureResult Run(string inputPath, FeatureContext context);
}

/// <summary>The shell page a feature is rendered in. Files and Scan also own the job list.</summary>
public enum FeaturePageKind
{
    /// <summary>Drop zone + job list (file processors: watermark, OCR).</summary>
    Files,

    /// <summary>Scanner strip + job list.</summary>
    Scan,

    /// <summary>Full-height search page (no job list).</summary>
    Search,

    /// <summary>Netdisk entry hub + job list.</summary>
    Netdisk,

    /// <summary>Phone receiver panel + job list.</summary>
    Transfer,
}
