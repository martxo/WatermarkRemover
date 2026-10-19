using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Geometry;
using WatermarkRemover.Core.Recognition;

namespace WatermarkRemover.Tests.Core.Recognition;

/// <summary>
/// Field label + value extraction over full-page recognised lines — the structured data the
/// recognition report stores ("经营者在哪、值是什么" with both boxes).
/// </summary>
public class LicenseFieldExtractorTests
{
    [Fact]
    public void Same_line_label_yields_value_with_shared_box()
    {
        var box = new RectD(0.1, 0.2, 0.4, 0.05);

        IReadOnlyList<FieldObservation> fields = LicenseFieldExtractor.Extract(
            new[] { new RecognizedLine("经营者：林孝明", box) });

        FieldObservation field = Assert.Single(fields);
        Assert.Equal("经营者", field.Label);
        Assert.Equal("林孝明", field.Value);
        Assert.Equal(box, field.LabelBox);
        Assert.Equal(box, field.ValueBox);
    }

    [Theory]
    [InlineData("经　营　者 林孝明", "林孝明")] // 全角空格插进标签
    [InlineData("经 营 者：王小明", "王小明")]   // 半角空格 + 冒号
    public void Label_with_inserted_spaces_still_captures_the_value(string line, string expected)
    {
        IReadOnlyList<FieldObservation> fields = LicenseFieldExtractor.Extract(
            new[] { new RecognizedLine(line, new RectD(0.1, 0.2, 0.4, 0.05)) });

        Assert.Equal(expected, Assert.Single(fields).Value);
    }

    [Fact]
    public void Wrapped_value_appends_continuation_lines_and_unions_their_boxes()
    {
        // 输入顺序刻意打乱：读取顺序按 y 坐标重排；标签行之后、下一个标签行之前的行都算续行。
        RecognizedLine[] lines =
        {
            new("以及相关配套服务", new RectD(0.1, 0.2, 0.5, 0.05)),
            new("登记机关", new RectD(0.1, 0.3, 0.5, 0.05)),
            new("经营范围许可项目", new RectD(0.1, 0.1, 0.5, 0.05)),
            new("一般项目：服装零售", new RectD(0.1, 0.15, 0.5, 0.05)),
        };

        IReadOnlyList<FieldObservation> fields = LicenseFieldExtractor.Extract(lines);

        Assert.Equal(2, fields.Count);

        FieldObservation wrapped = fields[0];
        Assert.Equal("经营范围", wrapped.Label);
        Assert.Equal("许可项目一般项目：服装零售以及相关配套服务", wrapped.Value);
        Assert.Equal(new RectD(0.1, 0.1, 0.5, 0.05), wrapped.LabelBox);
        Assert.NotNull(wrapped.ValueBox);
        RectD box = wrapped.ValueBox!.Value;
        Assert.Equal(0.1, box.X, 6);
        Assert.Equal(0.1, box.Y, 6);
        Assert.Equal(0.5, box.Width, 6);
        Assert.Equal(0.15, box.Height, 6); // 标签行顶 → 最后续行底

        FieldObservation next = fields[1];
        Assert.Equal("登记机关", next.Label);
        Assert.Equal(string.Empty, next.Value); // 标签独占一行，后面没有内容
        Assert.Equal(new RectD(0.1, 0.3, 0.5, 0.05), next.ValueBox);
    }

    [Fact]
    public void Label_split_across_lines_yields_nothing()
    {
        // 「经营者」被 OCR 拆到两行：字段提取拿不到值，交给复核器的 anchor_split 规则报告。
        IReadOnlyList<FieldObservation> fields = LicenseFieldExtractor.Extract(new[]
        {
            new RecognizedLine("经", new RectD(0.1, 0.1, 0.05, 0.05)),
            new RecognizedLine("营者：林孝明", new RectD(0.16, 0.1, 0.3, 0.05)),
        });

        Assert.Empty(fields);
    }

    [Fact]
    public void Two_labels_side_by_side_yield_two_observations()
    {
        var box = new RectD(0.05, 0.4, 0.9, 0.06);

        IReadOnlyList<FieldObservation> fields = LicenseFieldExtractor.Extract(
            new[] { new RecognizedLine("名称：厦门某公司　类型：个体工商户", box) });

        Assert.Equal(2, fields.Count);
        Assert.Equal(("名称", "厦门某公司"), (fields[0].Label, fields[0].Value));
        Assert.Equal(("类型", "个体工商户"), (fields[1].Label, fields[1].Value));
        Assert.Equal(box, fields[0].LabelBox);
        Assert.Equal(box, fields[1].ValueBox);
    }

    [Fact]
    public void Lines_without_usable_bounds_yield_nothing()
    {
        Assert.Empty(LicenseFieldExtractor.Extract(Array.Empty<RecognizedLine>()));

        // 无坐标行与零尺寸框都进不了读取顺序 —— 没有位置就没有字段区域数据。
        Assert.Empty(LicenseFieldExtractor.Extract(new[]
        {
            new RecognizedLine("经营者：林孝明", null),
            new RecognizedLine("名称：厦门某公司", new RectD(0, 0, 0, 0)),
        }));
    }
}
