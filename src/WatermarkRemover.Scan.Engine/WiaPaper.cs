using WatermarkRemover.Scan.Core.Models;

namespace WatermarkRemover.Scan.Engine;

/// <summary>
/// Converts the prototype's <see cref="PaperSize"/> into physical inches so the WIA scan area can be
/// expressed as pixel extents. Values are the standard ISO / US dimensions in portrait orientation;
/// the long-paper entries are approximations, used only when the driver offers that choice.
/// </summary>
internal static class WiaPaper
{
    /// <summary>Returns the media size in inches; false for <see cref="PaperSize.Auto"/>.</summary>
    public static bool TryGetInches(PaperSize paper, out double widthInches, out double heightInches)
    {
        (widthInches, heightInches) = paper switch
        {
            PaperSize.A3 => (11.69, 16.54),
            PaperSize.A4 => (8.27, 11.69),
            PaperSize.A5 => (5.83, 8.27),
            PaperSize.A6 => (4.13, 5.83),
            PaperSize.B4 => (9.84, 13.90),
            PaperSize.B5 => (7.17, 10.12),
            PaperSize.B6 => (4.92, 6.93),
            PaperSize.Letter => (8.50, 11.00),
            PaperSize.Legal => (8.50, 14.00),
            PaperSize.Executive => (7.25, 10.50),
            PaperSize.Folio => (8.50, 13.00),
            PaperSize.Ledger => (11.00, 17.00),
            PaperSize.HalfLetter => (5.50, 8.50),
            PaperSize.IndiaLegal => (8.50, 11.69),
            PaperSize.MexicoLegal => (8.50, 13.40),
            PaperSize.JapaneseHagaki => (3.94, 5.83),
            PaperSize.BusinessCard60x90 => (2.36, 3.54),
            PaperSize.BusinessCard90x60 => (3.54, 2.36),
            PaperSize.IndexCard13x20 => (5.00, 8.00),
            PaperSize.Photo10x15 => (3.94, 5.91),
            PaperSize.Photo13x18 => (5.12, 7.09),
            PaperSize.Photo9x13 => (3.54, 5.12),
            PaperSize.LongPaperNarrowWidth => (8.50, 34.00),
            PaperSize.LongPaperNormalWidth => (11.00, 34.00),
            _ => (0.0, 0.0),
        };

        return widthInches > 0 && heightInches > 0;
    }
}
