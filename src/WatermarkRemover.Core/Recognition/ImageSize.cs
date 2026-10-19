namespace WatermarkRemover.Core.Recognition;

/// <summary>
/// Pixel size of a rendered page image, read from the PNG IHDR chunk — the report's source for
/// orientation (竖版/横版) since the code base has no rotation detection. Pure functions with
/// full tolerance: anything that is not a well-formed PNG simply reports "unknown".
/// </summary>
public static class ImageSize
{
    private static ReadOnlySpan<byte> PngSignature => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>Reads width/height from a PNG's IHDR. False for non-PNG, truncated or zero-sized input.</summary>
    public static bool TryGetSize(byte[]? png, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (png is null || png.Length < 24) return false;

        if (!png.AsSpan(0, 8).SequenceEqual(PngSignature)) return false;
        // 8 sig + 4 length + 4 chunk type — IHDR must be first; width/height are big-endian.
        if (png[12] != 'I' || png[13] != 'H' || png[14] != 'D' || png[15] != 'R') return false;

        width = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        return width > 0 && height > 0;
    }

    /// <summary>竖版 / 横版 / 方形 from pixel size, or null when the size is unknown.</summary>
    public static string? OrientationOf(int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        if (width > height) return "横版";
        if (height > width) return "竖版";
        return "方形";
    }
}
