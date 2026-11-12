using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Processing;

namespace WatermarkRemover.Tests.Core.Features;

/// <summary>
/// The dead-end regression: a zero-mark run must never finish as a path-less NoChange — that
/// shape has no preview, no rename, no record (v1.15.33 originally; v1.16.136 again after the
/// "clean files produce no copy" rule removed the named-copy path). Both shapes now finish as a
/// real record: with the copy's path when one was produced, otherwise by adopting the input.
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
    public void Zero_mark_result_without_an_output_adopts_the_input()
    {
        var result = ProcessingResult.NoWatermarkFound(@"C:\in\源.pdf");

        var mapped = FeatureResult.From(result, "去水印", "已去除 0 处水印");

        Assert.Equal(FeatureStatus.Succeeded, mapped.Status);
        Assert.Equal(@"C:\in\源.pdf", mapped.OutputPath);
    }
}
