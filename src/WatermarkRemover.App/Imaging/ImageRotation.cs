using SkiaSharp;

namespace WatermarkRemover.App.Imaging;

/// <summary>
/// Quarter-turn rotation for page images in the 手机传文件 gallery. Rotation always re-encodes
/// from the original bytes, so repeated turns never accumulate JPEG artefacts and four turns
/// return the untouched original.
/// </summary>
public static class ImageRotation
{
    /// <summary>Rotates by <paramref name="quarterTurns"/> × 90° clockwise (negative = left).</summary>
    public static byte[] Rotate(byte[] source, int quarterTurns)
    {
        int turns = ((quarterTurns % 4) + 4) % 4;
        if (turns == 0) return source;

        using SKBitmap? input = TryDecode(source);
        if (input is null) return source;

        bool swap = turns is 1 or 3;
        using var rotated = new SKBitmap(
            swap ? input.Height : input.Width,
            swap ? input.Width : input.Height);
        using var canvas = new SKCanvas(rotated);

        switch (turns)
        {
            case 1:
                canvas.Translate(rotated.Width, 0);
                canvas.RotateDegrees(90);
                break;
            case 2:
                canvas.Translate(rotated.Width, rotated.Height);
                canvas.RotateDegrees(180);
                break;
            case 3:
                canvas.Translate(0, rotated.Height);
                canvas.RotateDegrees(270);
                break;
        }

        canvas.DrawBitmap(input, 0, 0);
        canvas.Flush();

        using SKImage image = SKImage.FromBitmap(rotated);
        using SKData? data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray() ?? source;
    }

    private static SKBitmap? TryDecode(byte[] source)
    {
        try
        {
            return SKBitmap.Decode(source);
        }
        catch (Exception)
        {
            // Not an image: the caller keeps the original bytes and the merge will surface it.
            return null;
        }
    }
}
