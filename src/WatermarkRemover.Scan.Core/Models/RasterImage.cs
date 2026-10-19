using System.Text.Json.Serialization;

namespace WatermarkRemover.Scan.Core.Models;

/// <summary>
/// A decoded raster frame. The pipeline moves this type between scan sources, image processors
/// and output targets, so it deliberately carries raw pixels rather than a framework bitmap.
/// Pixels are always 32-bit BGRA, top-left origin, row-major, no padding between rows.
/// </summary>
public sealed class RasterImage
{
    public RasterImage(int width, int height, byte[] bgra)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(bgra);

        var expected = width * height * 4;
        if (bgra.Length != expected)
        {
            throw new ArgumentException(
                $"BGRA buffer must be {expected} bytes for {width}x{height}, got {bgra.Length}.", nameof(bgra));
        }

        Width = width;
        Height = height;
        Bgra = bgra;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>32-bit BGRA pixel data, <see cref="Width"/> * <see cref="Height"/> * 4 bytes.</summary>
    [JsonIgnore]
    public byte[] Bgra { get; }

    public int Stride => Width * 4;

    public RasterImage WithPixels(byte[] bgra) => new(Width, Height, bgra);
}
