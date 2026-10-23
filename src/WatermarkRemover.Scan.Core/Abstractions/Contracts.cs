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


