using SkiaSharp;
using WatermarkRemover.App.Imaging;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// Generic document cutting and page packing for phone images: background-based crop (no
/// document-type presets), white scans left alone in auto mode, aspect-preserving row/column/
/// grid packs, and one file split into consecutive pages when the selection exceeds a pack's
/// capacity.
/// </summary>
public class ImageComposeTests
{
    private static byte[] Page(int width, int height, SKColor background, SKColor? ink = null)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(background);
            if (ink is { } color)
            {
                using var paint = new SKPaint { Color = color };
                canvas.DrawRect(100, 80, 120, 90, paint);
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static (int Width, int Height) Decode(byte[] bytes)
    {
        using var bitmap = SKBitmap.Decode(bytes) ?? throw new InvalidOperationException("不是图片");
        return (bitmap.Width, bitmap.Height);
    }

    private static SKColor PixelAt(byte[] bytes, int x, int y)
    {
        using var bitmap = SKBitmap.Decode(bytes) ?? throw new InvalidOperationException("不是图片");
        return bitmap.GetPixel(x, y);
    }

    private static bool IsReddish(SKColor c) => c.Red > 180 && c.Green < 120 && c.Blue < 120;

    private static bool IsBlueish(SKColor c) => c.Blue > 180 && c.Red < 120 && c.Green < 120;

    private static bool IsWhite(SKColor c) => c.Red > 240 && c.Green > 240 && c.Blue > 240;

    // --- DocumentCropper ---

    [Fact]
    public void Auto_crop_cuts_a_photo_down_to_its_document()
    {
        byte[] photo = Page(400, 300, new SKColor(70, 70, 70), SKColors.White);

        byte[] cropped = DocumentCropper.Crop(photo, new CropSettings(CropMode.Auto, PaddingRatio: 0));

        var (width, height) = Decode(cropped);
        Assert.Equal(120, width);
        Assert.Equal(90, height);
    }

    [Fact]
    public void Auto_crop_leaves_a_white_page_untouched()
    {
        // Scanned/exported pages are already cropped; cutting their margins would change the
        // page geometry the downstream pipeline (naming, watermark) relies on.
        byte[] page = Page(400, 300, SKColors.White, SKColors.Black);

        byte[] result = DocumentCropper.Crop(page, new CropSettings(CropMode.Auto));

        Assert.Same(page, result);
    }

    [Fact]
    public void Content_crop_cuts_a_white_page_when_explicitly_chosen()
    {
        byte[] page = Page(400, 300, SKColors.White, SKColors.Black);

        byte[] cropped = DocumentCropper.Crop(page, new CropSettings(CropMode.Content, PaddingRatio: 0));

        var (width, height) = Decode(cropped);
        Assert.Equal(120, width);
        Assert.Equal(90, height);
    }

    [Fact]
    public void An_undecodable_image_comes_back_untouched()
    {
        byte[] junk = { 1, 2, 3, 4 };

        Assert.Same(junk, DocumentCropper.Crop(junk));
    }

    // --- ImagePageComposer ---

    [Fact]
    public void Row_packs_two_documents_side_by_side_on_one_tight_page()
    {
        byte[] left = Page(400, 250, new SKColor(245, 245, 245));
        byte[] right = Page(400, 250, new SKColor(235, 240, 250));

        byte[] page = ImagePageComposer.ComposePage(
            new[] { left, right }, ComposeLayoutCatalog.Row, new CropSettings(CropMode.None));

        // 400 + gap 9 + 400 + margins 9 on both sides; height 250 + 18.
        var (width, height) = Decode(page);
        Assert.Equal(827, width);
        Assert.Equal(268, height);

        // The gap between the two documents is white, not a stretched image edge.
        SKColor gap = PixelAt(page, 413, 134);
        Assert.True(gap.Red >= 240 && gap.Green >= 240 && gap.Blue >= 240, $"gap was {gap}");
    }

    [Fact]
    public void Column_stacks_two_documents_top_to_bottom()
    {
        byte[] first = Page(300, 200, new SKColor(245, 245, 245));
        byte[] second = Page(300, 200, new SKColor(235, 240, 250));

        byte[] page = ImagePageComposer.ComposePage(
            new[] { first, second }, ComposeLayoutCatalog.Column, new CropSettings(CropMode.None));

        var (width, height) = Decode(page);
        Assert.Equal(316, width);   // 300 + 8 + 8
        Assert.Equal(424, height);  // 200 + gap 8 + 200 + margins 8 on both sides
    }

    [Fact]
    public void Single_pack_gives_one_page_per_image()
    {
        byte[] a = Page(400, 300, SKColors.White);
        byte[] b = Page(320, 480, SKColors.White);
        byte[] c = Page(200, 200, SKColors.White);

        IReadOnlyList<byte[]> pages = ImagePageComposer.ComposePages(
            new[] { a, b, c }, ComposeLayoutCatalog.Single, new CropSettings(CropMode.None));

        Assert.Equal(3, pages.Count);
        Assert.Equal((400, 300), Decode(pages[0]));
        Assert.Equal((320, 480), Decode(pages[1]));
        Assert.Equal((200, 200), Decode(pages[2]));
    }

    [Fact]
    public void Grid_packs_four_documents_into_cells_with_white_gutters()
    {
        var images = Enumerable.Range(0, 4)
            .Select(i => Page(200, 300, i % 2 == 0 ? SKColors.White : new SKColor(240, 240, 240)))
            .ToArray();

        byte[] page = ImagePageComposer.ComposePage(
            images, ComposeLayoutCatalog.Grid, new CropSettings(CropMode.None));

        var (width, height) = Decode(page);
        Assert.Equal(424, width);   // 2 × 200 + gap 8 + 2 × margin 8
        Assert.Equal(624, height);  // 2 × 300 + gap 8 + 2 × margin 8
    }

    [Fact]
    public void Pages_are_scaled_down_below_the_long_side_cap()
    {
        byte[] left = Page(3000, 2000, SKColors.White);
        byte[] right = Page(3000, 2000, SKColors.White);

        byte[] page = ImagePageComposer.ComposePage(
            new[] { left, right }, ComposeLayoutCatalog.Row, new CropSettings(CropMode.None));

        var (width, height) = Decode(page);
        Assert.True(Math.Max(width, height) <= ImagePageComposer.DefaultMaxLongSide,
            $"long side was {Math.Max(width, height)}");
        Assert.True(width > height);
    }

    [Fact]
    public void A_selection_larger_than_the_pack_capacity_becomes_consecutive_pages()
    {
        var images = Enumerable.Range(0, 3)
            .Select(_ => Page(300, 200, SKColors.White))
            .ToArray();

        IReadOnlyList<byte[]> pages = ImagePageComposer.ComposePages(
            images, ComposeLayoutCatalog.Row, new CropSettings(CropMode.None));

        Assert.Equal(2, pages.Count); // 2 + 1, one file
    }

    // --- 身份证 A4 template ---

    [Fact]
    public void Id_card_template_prints_two_cards_at_real_size_on_an_a4_page()
    {
        byte[] front = Page(1600, 1010, SKColors.Red);
        byte[] back = Page(1600, 1010, SKColors.Blue);

        byte[] page = ImagePageComposer.ComposePage(
            new[] { front, back }, ComposeLayoutCatalog.IdCardA4, new CropSettings(CropMode.None));

        // A4 portrait at 300 dpi.
        Assert.Equal((2480, 3508), Decode(page));

        // Cards are 85.6×54 mm → 1011×638 px, centred (left 734). Each face is centred in its
        // own half: upper-half centre y=877, lower-half centre y=2631.
        Assert.True(IsReddish(PixelAt(page, 1240, 877)), "front card missing");
        Assert.True(IsBlueish(PixelAt(page, 1240, 2631)), "back card missing");
        Assert.True(IsWhite(PixelAt(page, 1240, 1700)), "gap between the cards is not white");

        // Real-size outline: card paint must stop where the 85.6 mm box ends.
        Assert.True(IsWhite(PixelAt(page, 724, 877)));
        Assert.True(IsReddish(PixelAt(page, 744, 877)));
        Assert.True(IsReddish(PixelAt(page, 1736, 877)));
        Assert.True(IsWhite(PixelAt(page, 1756, 877)));
    }

    [Fact]
    public void A_portrait_id_photo_is_rotated_into_the_landscape_card()
    {
        byte[] portrait = Page(1010, 1600, SKColors.Red);

        byte[] page = ImagePageComposer.ComposePage(
            new[] { portrait }, ComposeLayoutCatalog.IdCardA4, new CropSettings(CropMode.None));

        // Filled edge to edge means the photo was turned; without rotation it would be a narrow
        // portrait island in the middle of the card.
        Assert.True(IsReddish(PixelAt(page, 744, 877)));
        Assert.True(IsReddish(PixelAt(page, 1736, 877)));
    }

    [Fact]
    public void Suggest_picks_the_id_template_for_two_card_shaped_images()
    {
        Assert.Equal(ComposeLayoutCatalog.IdCardA4,
            ComposeLayoutCatalog.Suggest(new[] { (1600, 1010), (1600, 1010) }));

        Assert.Equal(ComposeLayoutCatalog.Row,
            ComposeLayoutCatalog.Suggest(new[] { (2480, 1754), (2480, 1754) }));

        Assert.Equal(ComposeLayoutCatalog.IdCardA4,
            ComposeLayoutCatalog.Suggest(new[] { (800, 600), (800, 600) }, titleSaysIdCard: true));

        Assert.Equal(ComposeLayoutCatalog.Single,
            ComposeLayoutCatalog.Suggest(new[] { (1600, 1010) }));
    }
}
