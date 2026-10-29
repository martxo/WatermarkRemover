using SkiaSharp;
using WatermarkRemover.Imaging;

namespace WatermarkRemover.Tests.Imaging;

/// <summary>The transfer gallery rotates pages before merging, so orientation and lossless
/// four-turn behaviour are worth pinning down.</summary>
public sealed class ImageRotationTests
{
    [Fact]
    public void Quarter_turn_swaps_dimensions_and_maps_the_corners()
    {
        using var input = new SKBitmap(2, 1);
        input.SetPixel(0, 0, new SKColor(255, 0, 0));   // left red
        input.SetPixel(1, 0, new SKColor(0, 0, 255));   // right blue

        byte[] rotated = ImageRotation.Rotate(Encode(input), 1);

        using SKBitmap? output = SKBitmap.Decode(rotated);
        Assert.NotNull(output);
        Assert.Equal(1, output!.Width);
        Assert.Equal(2, output.Height);

        SKColor top = output.GetPixel(0, 0);
        SKColor bottom = output.GetPixel(0, 1);
        Assert.True(top.Red > 200 && top.Blue < 60, $"top={top}");
        Assert.True(bottom.Blue > 200 && bottom.Red < 60, $"bottom={bottom}");
    }

    [Fact]
    public void Four_turns_are_lossless_and_return_the_original_array()
    {
        using var input = new SKBitmap(3, 2);
        input.SetPixel(0, 0, new SKColor(10, 20, 30));
        byte[] bytes = Encode(input);

        Assert.Same(bytes, ImageRotation.Rotate(bytes, 4));
        Assert.Same(bytes, ImageRotation.Rotate(bytes, 0));
        Assert.Same(bytes, ImageRotation.Rotate(bytes, -4));
    }

    [Fact]
    public void Invalid_bytes_are_returned_untouched()
    {
        byte[] junk = { 1, 2, 3, 4 };
        Assert.Same(junk, ImageRotation.Rotate(junk, 1));
    }

    private static byte[] Encode(SKBitmap bitmap)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
