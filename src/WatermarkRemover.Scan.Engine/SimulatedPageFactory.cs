using WatermarkRemover.Scan.Core.Models;

namespace WatermarkRemover.Scan.Engine;

/// <summary>
/// Renders deterministic synthetic document pages straight into a BGRA buffer (no imaging library
/// required), which keeps the simulated source working even where the native Skia binaries cannot
/// load. Every page is 300 dpi A4 (2480x3508).
/// </summary>
internal static class SimulatedPageFactory
{
    private const int Width = 2480;
    private const int Height = 3508;

    /// <summary>
    /// Page 0 carries a colour block at the top (exercises auto colour detection), page 1 a light grey
    /// background (exercises background removal) and page 2+ stays blank white (exercises blank page
    /// detection). Pages 0 and 1 also get evenly spaced black bars imitating text lines.
    /// </summary>
    public static RasterImage Create(int pageIndex)
    {
        var pixels = new byte[Width * Height * 4];
        Array.Fill(pixels, (byte)0xFF); // Opaque white: B=G=R=A=255.

        if (pageIndex == 0)
        {
            // Red block (BGRA: B=0, G=0, R=255) so colour / auto-colour processing has something to see.
            FillRect(pixels, 120, 80, 900, 320, 0, 0, 255, 255);
            DrawTextLines(pixels, 600, 3200);
        }
        else if (pageIndex == 1)
        {
            // Light grey paper to give background removal something to work with.
            FillRect(pixels, 0, 0, Width, Height, 205, 205, 205, 255);
            DrawTextLines(pixels, 600, 3200);
        }

        // pageIndex >= 2 stays blank white.
        return new RasterImage(Width, Height, pixels);
    }

    /// <summary>Draws black bars of varying width to imitate ragged-right text lines.</summary>
    private static void DrawTextLines(byte[] pixels, int top, int bottom)
    {
        const int lineHeight = 14;
        const int lineGap = 34;
        const int left = 180;

        var y = top;
        var seed = 0x5F3A;
        while (y + lineHeight < bottom)
        {
            seed = unchecked((seed * 1103515245 + 12345) & 0x7FFFFFFF);
            var lineWidth = 1200 + (seed % 900);
            FillRect(pixels, left, y, left + lineWidth, y + lineHeight, 0, 0, 0, 255);
            y += lineGap;
        }
    }

    /// <summary>Fills an axis-aligned rectangle with a BGRA colour, clamped to the page bounds.</summary>
    private static void FillRect(byte[] pixels, int x0, int y0, int x1, int y1, byte b, byte g, byte r, byte a)
    {
        var startX = Math.Max(0, x0);
        var startY = Math.Max(0, y0);
        var endX = Math.Min(Width, x1);
        var endY = Math.Min(Height, y1);

        for (var y = startY; y < endY; y++)
        {
            var row = y * Width;
            for (var x = startX; x < endX; x++)
            {
                var i = (row + x) * 4;
                pixels[i] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
                pixels[i + 3] = a;
            }
        }
    }
}
