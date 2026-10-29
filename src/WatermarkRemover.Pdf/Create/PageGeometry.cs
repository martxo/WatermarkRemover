using WatermarkRemover.Scan.Core.Models;

namespace WatermarkRemover.Pdf;

/// <summary>
/// Shared page geometry helpers: pixel&lt;-&gt;point conversion at a given DPI and a paper-size table.
/// PDF pages are sized from the scanned pixels, printing needs the physical paper size, so both live
/// here instead of being duplicated in the targets.
/// </summary>
internal static class PageGeometry
{
    /// <summary>Points per inch (PostScript/PDF convention).</summary>
    public const double PointsPerInch = 72.0;

    /// <summary>Hundredths of an inch, the unit System.Drawing.Printing uses.</summary>
    public const double UnitsPerInch = 100.0;

    // width x height in inches. Auto / long-paper choices have no fixed size and are omitted.
    private static readonly Dictionary<PaperSize, (double Width, double Height)> PaperInches = new()
    {
        [PaperSize.A3] = (11.69, 16.54),
        [PaperSize.A4] = (8.27, 11.69),
        [PaperSize.A5] = (5.83, 8.27),
        [PaperSize.A6] = (4.13, 5.83),
        [PaperSize.B4] = (9.84, 13.90),
        [PaperSize.B5] = (6.93, 9.84),
        [PaperSize.B6] = (4.92, 6.93),
        [PaperSize.Letter] = (8.5, 11.0),
        [PaperSize.Legal] = (8.5, 14.0),
        [PaperSize.Executive] = (7.25, 10.5),
        [PaperSize.Folio] = (8.5, 13.0),
        [PaperSize.Ledger] = (11.0, 17.0),
        [PaperSize.HalfLetter] = (5.5, 8.5),
        [PaperSize.IndiaLegal] = (8.5, 13.5),
        [PaperSize.MexicoLegal] = (8.5, 13.39),
        [PaperSize.JapaneseHagaki] = (3.94, 5.83),
        [PaperSize.BusinessCard60x90] = (2.36, 3.54),
        [PaperSize.BusinessCard90x60] = (3.54, 2.36),
        [PaperSize.IndexCard13x20] = (5.12, 7.87),
        [PaperSize.Photo10x15] = (3.94, 5.91),
        [PaperSize.Photo13x18] = (5.12, 7.09),
        [PaperSize.Photo9x13] = (3.54, 5.12),
    };

    /// <summary>Converts a pixel length to points: pt = px * 72 / dpi.</summary>
    public static double PixelsToPoints(int pixels, double dpi) =>
        PixelsToPoints((double)pixels, dpi);

    /// <summary>Converts a fractional pixel length (for example an OCR word box) to points.</summary>
    public static double PixelsToPoints(double pixels, double dpi) =>
        pixels <= 0 || dpi <= 0 ? 0 : pixels * PointsPerInch / dpi;

    /// <summary>Converts a point length back to pixels: px = pt * dpi / 72.</summary>
    public static double PointsToPixels(double points, double dpi) =>
        points <= 0 || dpi <= 0 ? 0 : points * dpi / PointsPerInch;

    /// <summary>Looks up the physical size of a paper choice in inches.</summary>
    public static bool TryGetInches(PaperSize size, out double widthInches, out double heightInches)
    {
        if (PaperInches.TryGetValue(size, out var found))
        {
            widthInches = found.Width;
            heightInches = found.Height;
            return true;
        }

        widthInches = 0;
        heightInches = 0;
        return false;
    }
}
