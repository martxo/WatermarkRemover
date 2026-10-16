using WatermarkRemover.Core.Content;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Processing;
using WatermarkRemover.Core.Watermarks;

namespace WatermarkRemover.Tests.Core;

public class FeatureResultFromTests
{
    [Fact]
    public void FromSucceededCarriesOutputSummaryAndCount()
    {
        var marks = new[]
        {
            new WatermarkMark(0, "mark", Array.Empty<ContentOperation>(), Array.Empty<string>(), Array.Empty<int>()),
            new WatermarkMark(1, "mark", Array.Empty<ContentOperation>(), Array.Empty<string>(), Array.Empty<int>()),
        };
        var processing = ProcessingResult.Succeeded("in.pdf", "out.pdf", marks);

        var result = FeatureResult.From(processing, "去水印", "已去除 2 处水印");

        Assert.Equal(FeatureStatus.Succeeded, result.Status);
        Assert.Equal("out.pdf", result.OutputPath);
        Assert.Equal("已去除 2 处水印", result.Summary);
        Assert.Equal(2, result.ItemCount);
    }

    [Fact]
    public void FromNoWatermarkBecomesNoChange()
    {
        var processing = ProcessingResult.NoWatermarkFound("in.pdf");

        var result = FeatureResult.From(processing, "去水印", "ignored");

        Assert.Equal(FeatureStatus.NoChange, result.Status);
        Assert.Equal("未检测到水印", result.Summary);
    }

    [Fact]
    public void FromFailedKeepsErrorAndDetails()
    {
        var processing = ProcessingResult.Failed("in.pdf", "解析失败", "stack trace here");

        var result = FeatureResult.From(processing, "去水印", "ignored");

        Assert.Equal(FeatureStatus.Failed, result.Status);
        Assert.Equal("解析失败", result.Error);
        Assert.Equal("stack trace here", result.ErrorDetails);
        Assert.Equal("去水印", result.FeatureName);
    }

    [Fact]
    public void FromFailedWithoutMessageUsesFallback()
    {
        var processing = ProcessingResult.Failed("in.pdf", error: null!);

        var result = FeatureResult.From(processing, "去水印", "ignored");

        Assert.Equal("处理失败", result.Error);
    }
}
