using WatermarkRemover.Core.Recognition;

namespace WatermarkRemover.Tests.Core.Recognition;

public class ImageSizeTests
{
    [Fact]
    public void ReadsSizeFromPngIhdr()
    {
        byte[] png = PngHeader.Build(800, 600);

        bool ok = ImageSize.TryGetSize(png, out int width, out int height);

        Assert.True(ok);
        Assert.Equal(800, width);
        Assert.Equal(600, height);
    }

    [Theory]
    [InlineData(null)]                       // no bytes at all
    [InlineData(new byte[] { 1, 2, 3 })]     // truncated
    public void RejectsNullOrTruncatedInput(byte[]? png)
    {
        Assert.False(ImageSize.TryGetSize(png, out int width, out int height));
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    [Fact]
    public void RejectsNonPngData()
    {
        // JPEG magic instead of the PNG signature.
        byte[] jpeg = new byte[24];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        jpeg[2] = 0xFF;
        Assert.False(ImageSize.TryGetSize(jpeg, out _, out _));

        // Right signature, but the first chunk is not IHDR.
        byte[] noIhdr = PngHeader.Build(100, 100);
        noIhdr[12] = (byte)'I';
        noIhdr[13] = (byte)'D';
        noIhdr[14] = (byte)'A';
        noIhdr[15] = (byte)'T';
        Assert.False(ImageSize.TryGetSize(noIhdr, out _, out _));

        // Well-formed header declaring a zero dimension is rejected as unknown.
        Assert.False(ImageSize.TryGetSize(PngHeader.Build(0, 100), out _, out _));
    }

    [Theory]
    [InlineData(100, 200, "竖版")]
    [InlineData(200, 100, "横版")]
    [InlineData(100, 100, "方形")]
    [InlineData(0, 100, null)]
    [InlineData(100, -1, null)]
    public void OrientationFollowsPixelSize(int width, int height, string? expected)
    {
        Assert.Equal(expected, ImageSize.OrientationOf(width, height));
    }
}
