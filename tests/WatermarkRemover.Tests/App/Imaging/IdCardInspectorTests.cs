using SkiaSharp;
using WatermarkRemover.App.Imaging;
using WatermarkRemover.Core.Features.Ocr;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// The 身份证 pre-compose pass: one OCR call per photo decides which side is the front (so it
/// lands on top) and supplies the 姓名 for the file name. No page-level OCR happens afterwards.
/// </summary>
public class IdCardInspectorTests
{
    private static byte[] Image(int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Returns the canned line lists in call order (one call per photo).</summary>
    private sealed class QueueOcr(params IReadOnlyList<string>[] results) : IOcrEngine
    {
        private int _index;

        public string DisplayName => "测试 OCR";

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
            Task.FromResult(results[Math.Min(_index++, results.Length - 1)]);
    }

    private static readonly string[] FrontLines =
    {
        "姓名 李文亨", "性别 男", "民族 汉", "出生 1990年1月1日",
        "住址 某省某市某路1号", "公民身份号码 110101199001011234",
    };

    private static readonly string[] BackLines =
    {
        "中华人民共和国", "居民身份证", "签发机关 某市公安局", "有效期限 2020.01.01-2040.01.01",
    };

    [Fact]
    public async Task The_back_photo_is_moved_below_the_front_and_the_name_names_the_file()
    {
        byte[] back = Image(200, 120);
        byte[] front = Image(200, 120);
        var engine = new QueueOcr(BackLines, FrontLines);

        IdCardPlan plan = await IdCardInspector.PrepareAsync(new[] { back, front }, engine);

        Assert.Same(front, plan.OrderedImages[0]);
        Assert.Same(back, plan.OrderedImages[1]);
        Assert.Equal("李文亨", plan.Name);
        Assert.True(plan.LooksLikeIdCard);
    }

    [Fact]
    public async Task Two_licence_photos_are_not_confirmed_as_an_id_card()
    {
        byte[] first = Image(200, 120);
        byte[] second = Image(200, 120);
        var engine = new QueueOcr(
            new[] { "营业执照", "名称 某某公司", "统一社会信用代码 91450900MA..." },
            new[] { "营业执照", "法定代表人 李伟", "经营范围 一般项目…" });

        IdCardPlan plan = await IdCardInspector.PrepareAsync(new[] { first, second }, engine);

        Assert.False(plan.LooksLikeIdCard);
        Assert.Null(plan.Name);
        Assert.Same(first, plan.OrderedImages[0]);
    }

    [Fact]
    public async Task A_correctly_ordered_pair_stays_put()
    {
        byte[] front = Image(200, 120);
        byte[] back = Image(200, 120);
        var engine = new QueueOcr(FrontLines, BackLines);

        IdCardPlan plan = await IdCardInspector.PrepareAsync(new[] { front, back }, engine);

        Assert.Same(front, plan.OrderedImages[0]);
        Assert.Same(back, plan.OrderedImages[1]);
        Assert.Equal("李文亨", plan.Name);
    }

    [Fact]
    public async Task A_merged_row_stops_the_name_at_the_next_label()
    {
        byte[] front = Image(200, 120);
        byte[] back = Image(200, 120);
        var engine = new QueueOcr(
            new[] { "姓名李文亨性别男民族汉", "公民身份号码110101199001011234" },
            BackLines);

        IdCardPlan plan = await IdCardInspector.PrepareAsync(new[] { front, back }, engine);

        Assert.Equal("李文亨", plan.Name);
    }

    [Fact]
    public async Task An_unrecognisable_pair_keeps_its_order_and_has_no_name()
    {
        byte[] first = Image(200, 120);
        byte[] second = Image(200, 120);
        var engine = new QueueOcr(
            new[] { "签发机关 某市公安局" },
            new[] { "有效期限 2020.01.01-2040.01.01" });

        IdCardPlan plan = await IdCardInspector.PrepareAsync(new[] { first, second }, engine);

        Assert.Same(first, plan.OrderedImages[0]);
        Assert.Same(second, plan.OrderedImages[1]);
        Assert.Null(plan.Name);
        Assert.False(plan.LooksLikeIdCard);
    }

    [Fact]
    public async Task A_label_row_without_a_value_does_not_become_a_name()
    {
        byte[] front = Image(200, 120);
        byte[] back = Image(200, 120);
        var engine = new QueueOcr(
            new[] { "姓名 性别 民族", "公民身份号码 110101199001011234" },
            BackLines);

        IdCardPlan plan = await IdCardInspector.PrepareAsync(new[] { front, back }, engine);

        Assert.Null(plan.Name);
    }
}
