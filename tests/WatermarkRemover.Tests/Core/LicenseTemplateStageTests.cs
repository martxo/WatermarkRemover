using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Tests.Core;

public class LicenseTemplateStageTests
{
    private sealed class FakeProbe(params RecognizedLine[] lines) : IRegionProbe
    {
        public Task<IReadOnlyList<RecognizedLine>> RecognizeAsync(byte[] region) =>
            Task.FromResult<IReadOnlyList<RecognizedLine>>(lines);
    }

    private static RecognizedLine Line(string text, RectD? bounds) => new(text, bounds);

    private static string Format(RectD r) =>
        FormattableString.Invariant($"{r.X:0.######},{r.Y:0.######},{r.Width:0.######},{r.Height:0.######}");

    [Theory]
    [InlineData("名    称：张三有限公司", "名称")]
    [InlineData("统一社会信用代码", "统一社会信用代码")]
    [InlineData("统一社会信用代", "统一社会信用代码")] // truncated label (OCR cut the last glyph)
    [InlineData("法定代表人：李四", "法定代表人")]
    [InlineData("执行事务合伙人", "执行事务合伙人")]
    [InlineData("经营者：王五", "经营者")]
    [InlineData("经营范围：许可项目…", "经营范围")]
    public void MatchAnchorAcceptsNoisyLabelForms(string text, string expected)
    {
        Assert.Equal(expected, LicenseTemplateStage.MatchAnchor(text));
    }

    [Theory]
    [InlineData("张三有限公司")]
    [InlineData("有限责任公司")]
    [InlineData("")]
    [InlineData(null)]
    public void MatchAnchorRejectsNonLabelLines(string? text)
    {
        Assert.Null(LicenseTemplateStage.MatchAnchor(text));
    }

    [Fact]
    public async Task CutsOneBandPerLabelRowEndingAtTheNextLabel()
    {
        var probe = new FakeProbe(
            Line("统一社会信用代码 91110000...", new RectD(0.10, 0.10, 0.60, 0.03)),
            Line("名    称：张三有限公司", new RectD(0.10, 0.20, 0.50, 0.03)),
            Line("经营范围：许可项目；一般项目…", new RectD(0.10, 0.30, 0.70, 0.03)),
            Line("页脚备注无标签", new RectD(0.10, 0.95, 0.40, 0.03)));

        IReadOnlyList<LayoutRegion> regions =
            await LicenseTemplateStage.Instance.DetectRegionsAsync(new byte[] { 1 }, probe);

        Assert.Equal(3, regions.Count); // footer line without a label is not a band
        // Band 1: from label 1 top to label 2 top, right edge of page.
        Assert.Equal("0.1,0.1,0.9,0.1", Format(regions[0].Bounds));
        // Band 2: label 2 row.
        Assert.Equal("0.1,0.2,0.9,0.1", Format(regions[1].Bounds));
        // Band 3: last label row runs to the page bottom (wrapped values included).
        Assert.Equal("0.1,0.3,0.9,0.7", Format(regions[2].Bounds));
    }

    [Fact]
    public async Task TwoLabelsOnTheSameRowMergeIntoOneBand()
    {
        var probe = new FakeProbe(
            Line("名称：甲", new RectD(0.50, 0.20, 0.20, 0.04)),
            Line("类型：乙", new RectD(0.10, 0.21, 0.20, 0.04)));

        IReadOnlyList<LayoutRegion> regions =
            await LicenseTemplateStage.Instance.DetectRegionsAsync(new byte[] { 1 }, probe);

        Assert.Single(regions); // one row, one band — no overlapping crops
        Assert.Equal("0.1,0.2,0.9,0.8", Format(regions[0].Bounds)); // left = leftmost label
    }

    [Fact]
    public async Task FallsBackToFullImageWhenNoAnchorMatches()
    {
        var probe = new FakeProbe(
            Line("普通文档第一行", new RectD(0.1, 0.1, 0.5, 0.03)),
            Line("没有任何标签", new RectD(0.1, 0.2, 0.5, 0.03)));

        IReadOnlyList<LayoutRegion> regions =
            await LicenseTemplateStage.Instance.DetectRegionsAsync(new byte[] { 1 }, probe);

        Assert.Single(regions);
        Assert.Equal("0,0,1,1", Format(regions[0].Bounds));
    }

    [Fact]
    public async Task FallsBackToFullImageWhenEngineProvidesNoBounds()
    {
        // Anchor text is present, but the engine cannot position it — no geometry, no bands.
        var probe = new FakeProbe(
            Line("名    称：张三有限公司", null),
            Line("法定代表人：李四", null));

        IReadOnlyList<LayoutRegion> regions =
            await LicenseTemplateStage.Instance.DetectRegionsAsync(new byte[] { 1 }, probe);

        Assert.Single(regions);
        Assert.Equal("0,0,1,1", Format(regions[0].Bounds));
    }
}
