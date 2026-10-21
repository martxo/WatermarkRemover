using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Processing;

namespace WatermarkRemover.Tests.Core.Features;

/// <summary>
/// The dead-end regression (v1.15.33, 2026-10-21): a PDF without watermarks used to finish as
/// NoChange with no output — no preview, no rename, no record. The pipeline now skips only the
/// watermark-removal step and still produces a content-named copy, so the mapping must turn a
/// zero-mark result carrying an output path into a real finished record; the path-less shape
/// (no copy possible, e.g. the copy itself failed) stays a skip.
/// </summary>
public class FeatureResultTests
{
    [Fact]
    public void Zero_mark_result_with_a_named_copy_becomes_a_finished_record()
    {
        var result = ProcessingResult.NoWatermarkFound(@"C:\in\源.pdf", @"C:\out\林孝明执照.pdf");

        var mapped = FeatureResult.From(result, "去水印", "已去除 0 处水印");

        Assert.Equal(FeatureStatus.Succeeded, mapped.Status);
        Assert.Equal(@"C:\out\林孝明执照.pdf", mapped.OutputPath);
    }

    [Fact]
    public void Zero_mark_result_without_an_output_stays_a_skip()
    {
        var result = ProcessingResult.NoWatermarkFound(@"C:\in\源.pdf");

        var mapped = FeatureResult.From(result, "去水印", "已去除 0 处水印");

        Assert.Equal(FeatureStatus.NoChange, mapped.Status);
        Assert.Null(mapped.OutputPath);
    }
}
