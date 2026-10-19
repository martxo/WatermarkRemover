namespace WatermarkRemover.Scan.Core.Models;

/// <summary>
/// Paper size choices. Values mirror the enumerations observed in the target's scan and print
/// setting tables (SCN_DOCUMENT_SIZE_*, PRT_PAPER_SIZE_*).
/// </summary>
public enum PaperSize
{
    Auto,
    A3,
    A4,
    A5,
    A6,
    B4,
    B5,
    B6,
    Letter,
    Legal,
    Executive,
    Folio,
    Ledger,
    HalfLetter,
    IndiaLegal,
    MexicoLegal,
    JapaneseHagaki,
    BusinessCard60x90,
    BusinessCard90x60,
    IndexCard13x20,
    Photo10x15,
    Photo13x18,
    Photo9x13,
    LongPaperNarrowWidth,
    LongPaperNormalWidth,
}

/// <summary>
/// Resolution choices. The target exposes 100..1200 dpi (SCN_RESOLUTION_*).
/// </summary>
public enum ScanResolution
{
    Dpi100 = 100,
    Dpi150 = 150,
    Dpi200 = 200,
    Dpi300 = 300,
    Dpi400 = 400,
    Dpi600 = 600,
    Dpi1200 = 1200,
}

/// <summary>
/// Colour depth choices. Mirrors SCN_COLOR_SETTING_* (auto / 24 bit / error diffusion grey /
/// true grey / black and white).
/// </summary>
public enum ColorMode
{
    Auto,
    Color24,
    GrayDiffusion,
    TrueGray,
    BlackWhite,
}

/// <summary>Duplex modes, mirroring SCN_DUPLEX_* / PRT_DUPLEX_*.</summary>
public enum DuplexMode
{
    Off,
    LongEdge,
    ShortEdge,
}

/// <summary>How a duplex batch delivers its images, which decides whether page-order
/// normalisation applies. Device-driven duplex — the Brother ADK DUPLEX_* channel and WIA's
/// HandlingDuplex alike — walks each sheet once and transfers both sides back to back, i.e.
/// interleaved reading order. A manual flip-the-stack workflow instead yields all fronts
/// first, then the rears in reverse, and needs interleaving to restore reading order.</summary>
public enum DuplexFeedOrder
{
    Interleaved,
    FrontsThenReversedRears,
}

/// <summary>Where the paper comes from. Flatbed cannot duplex.</summary>
public enum FeedMode
{
    Auto,
    Flatbed,
    Adf,
}

/// <summary>Blank page removal sensitivity (SCN_BLANK_REMOVAL_RANGE_*).</summary>
public enum BlankRemovalLevel
{
    Off,
    Low,
    Middle,
    High,
}

/// <summary>Print quality, mirroring PRT_QUALITY_*.</summary>
public enum PrintQuality
{
    Fast,
    Normal,
    High,
    Photo,
    Eco,
}

/// <summary>Pages-per-sheet layout, mirroring PRT_LAYOUT_*.</summary>
public enum PageLayout
{
    OneInOne = 1,
    TwoInOne = 2,
    FourInOne = 4,
}

/// <summary>
/// Compression codec for multi-page TIFF output, mirroring SCN_COMPRESSION_TYPE_*.
/// G3/G4 are fax codecs: they only carry 1-bit pages, so colour/grayscale input is thresholded
/// down to black &amp; white when one of them is selected (same trade-off as the target).
/// </summary>
public enum TiffCompressionMode
{
    None,
    G3,
    G4,
    LZW,
}
