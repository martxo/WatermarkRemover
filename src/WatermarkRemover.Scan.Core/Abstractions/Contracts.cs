using WatermarkRemover.Scan.Core.Models;

namespace WatermarkRemover.Scan.Core.Abstractions;

/// <summary>
/// Produces pages from a device. The target reaches devices through three channels
/// (STI/WIA, USB, network); this prototype models that as pluggable sources.
/// </summary>
public interface IScanSource : IComponent
{
    /// <summary>Cheap probe used to order the source chain without opening a device.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ScanDeviceInfo>> DiscoverAsync(CancellationToken cancellationToken = default);

    Task<IScanSession> OpenAsync(
        ScanDeviceInfo device,
        ScanSettings settings,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// An open device. Pages are pulled one at a time so flatbed (single page) and ADF (many pages)
/// need no different call shape, and so a caller can render/save pages as they arrive.
/// </summary>
public interface IScanSession : IAsyncDisposable
{
    ScanDeviceInfo Device { get; }

    /// <summary>Returns the next page, or null when the feeder is empty / the scan is complete.</summary>
    Task<RasterImage?> ReadPageAsync(CancellationToken cancellationToken = default);
}

/// <summary>Result of one processor pass. Processors may also classify the page, not only transform it.</summary>
public sealed record ProcessorOutcome(RasterImage Image, bool? IsBlank = null, string? Note = null);

/// <summary>
/// Ambient information a processor may need. Kept in one object so new processors can be added
/// without a breaking signature change.
/// </summary>
public sealed class ProcessingContext
{
    public required ScanSettings Settings { get; init; }

    public int PageIndex { get; init; }

    public int SheetIndex { get; init; }

    public bool IsBackSide { get; init; }

    /// <summary>
    /// OCR-backed readability probe for text-direction decisions (the target's 自动图像旋转 mechanism).
    /// Null when no engine is wired up; processors then fall back to image-only heuristics.
    /// </summary>
    public IOrientationProbe? OrientationProbe { get; init; }

    /// <summary>
    /// Per-run plan shared by all pages of one scan: the front of each sheet carries the OCR decision,
    /// backs derive from it mechanically. Null keeps every page independent.
    /// </summary>
    public OrientationPlan? OrientationPlan { get; init; }

    /// <summary>Diagnostic sink. Processors report decisions here instead of swallowing them.</summary>
    public Action<string, Exception?>? Trace { get; init; }

    public void Report(string message, Exception? error = null) => Trace?.Invoke(message, error);
}

/// <summary>
/// Duplex capture relates the two sides of one sheet with a fixed mechanical flip: short-edge flip
/// delivers the back as the front turned 180°, long-edge flip keeps the same orientation (both
/// verified on a DCP-B7658DW). Scoring the content-rich front side and deriving the back avoids
/// per-page OCR guesses on sparse rear pages, which have too little text to score reliably.
/// </summary>
public sealed class OrientationPlan
{
    private readonly Dictionary<int, int> _frontRotations = new();

    /// <summary>Clockwise degrees the front of <paramref name="sheetIndex"/> was turned, or null when undecided.</summary>
    public int? TryGetFrontRotation(int sheetIndex) =>
        _frontRotations.TryGetValue(sheetIndex, out var degrees) ? degrees : null;

    /// <summary>Records the front-side decision (0 when the page was left upright).</summary>
    public void SetFrontRotation(int sheetIndex, int cwDegrees) => _frontRotations[sheetIndex] = cwDegrees;
}

/// <summary>
/// Scores how readable an image is for the active OCR engine. Sideways pages score low because the
/// recogniser sees vertical text lines, so scoring two rotated candidates tells 90° from 270° apart -
/// the application-side stand-in for the target's engine parameter 自动图像旋转
/// (<c>SCN_AUTO_IMAGE_ROTATION</c>,「基于文本方向更改文档的方向」).
/// </summary>
public interface IOrientationProbe
{
    /// <summary>Sum of recognised word confidences, or null when no engine can score.</summary>
    double? Score(RasterImage image);
}

/// <summary>
/// One step of the image pipeline. Order is explicit so the chain is reproducible and testable.
/// </summary>
public interface IImageProcessor : IComponent
{
    /// <summary>Lower runs earlier.</summary>
    int Order { get; }

    /// <summary>Whether this step applies to the given settings (and therefore should be scheduled).</summary>
    bool AppliesTo(ScanSettings settings);

    ProcessorOutcome Process(RasterImage input, ProcessingContext context);
}

/// <summary>Recognises text and word boxes on a page.</summary>
public interface IOcrEngine : IComponent
{
    /// <summary>True when the engine's native prerequisites are present on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>Why the engine is unavailable, for the diagnostics report.</summary>
    string? UnavailableReason { get; }

    Task<OcrResult> RecognizeAsync(
        RasterImage image,
        string language,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Encodes a raster frame to a file format. Implemented by the imaging layer and injected into
/// output targets, so output code never needs a reference to the imaging project.
/// </summary>
public interface IImageEncoder
{
    /// <summary>Formats this encoder can produce, e.g. "png", "jpg", "tif", "bmp".</summary>
    IReadOnlyCollection<string> SupportedFormats { get; }

    byte[] Encode(RasterImage image, string format, int quality = 90);

    /// <summary>
    /// Fits the frame inside a square of <paramref name="maxWidthOrHeight"/> pixels, aspect ratio
    /// preserved. Downscaling only unless <paramref name="upscale"/> is set (used by 高压缩 PDF to
    /// cap the embedded bitmap resolution).
    /// </summary>
    RasterImage Resize(RasterImage image, int maxWidthOrHeight, bool upscale = false);
}

/// <summary>Writes a finished document somewhere.</summary>
public interface IOutputTarget : IComponent
{
    OutputKind Kind { get; }

    Task<OutputResult> WriteAsync(
        ScanDocument document,
        OutputSettings settings,
        CancellationToken cancellationToken = default);
}

/// <summary>Inputs a naming profile may inspect (OCR text plus layout facts).</summary>
public sealed record NamingContext(
    string OcrText,
    IReadOnlyList<OcrWord> Words,
    int PageCount,
    ScanSettings Settings);

/// <summary>
/// Classifies a document from its OCR result and proposes a file name. Adding a new document type
/// is a matter of registering one profile (see docs 特色功能: 智能命名).
/// </summary>
public interface IDocumentTypeProfile : IComponent
{
    /// <summary>Returns a suggestion, or null when the profile does not recognise the document.</summary>
    NamingSuggestion? Evaluate(NamingContext context);
}

/// <summary>
/// A rule that detects conflicting settings and can optionally normalise them, mirroring the
/// target's SCN_SETTING_CONFLICT_* behaviour.
/// </summary>
public interface ISettingConflictRule : IComponent
{
    /// <summary>Returns the conflict description, or null when the settings are consistent.</summary>
    SettingConflict? Evaluate(ScanSettings settings);

    /// <summary>Rewrites settings so the conflict disappears. Called when the user accepts the fix.</summary>
    void Apply(ScanSettings settings);
}

/// <summary>A detected settings conflict.</summary>
public sealed record SettingConflict(
    string RuleId,
    string Title,
    string Message,
    bool AutoFixAvailable,
    string? AutoFixDescription);
