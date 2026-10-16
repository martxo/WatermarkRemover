using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Processing;

namespace WatermarkRemover.Tests.Core;

/// <summary>
/// Failure results must carry the full exception dump — that is what the "copy error report"
/// button ships to the developer, so an HRESULT or stack trace must never be flattened to just
/// <c>ex.Message</c>.
/// </summary>
public class FeatureResultErrorDetailsTests
{
    [Fact]
    public void Fail_WithException_CarriesTypeStackAndMessage()
    {
        Exception ex;
        try
        {
            throw new InvalidOperationException("没有注册类 (0x80040154 REGDB_E_CLASSNOTREG)");
        }
        catch (Exception caught)
        {
            ex = caught;
        }

        FeatureResult result = FeatureResult.Fail(@"C:\in.pdf", ex.Message, ex, "OCR 识别");

        Assert.Equal(FeatureStatus.Failed, result.Status);
        Assert.Equal("OCR 识别", result.FeatureName);
        Assert.Contains("0x80040154", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.ErrorDetails);
        Assert.Contains(nameof(InvalidOperationException), result.ErrorDetails);
        Assert.Contains("0x80040154", result.ErrorDetails, StringComparison.OrdinalIgnoreCase);
        // The stack trace must survive so the failing frame is identifiable.
        Assert.Contains(nameof(Fail_WithException_CarriesTypeStackAndMessage), result.ErrorDetails);
    }

    [Fact]
    public void Fail_WithException_IncludesInnerException()
    {
        var outer = new TypeInitializationException("outer", new DllNotFoundException("inner-missing"));
        FeatureResult result = FeatureResult.Fail(@"C:\in.pdf", outer.Message, outer);

        Assert.NotNull(result.ErrorDetails);
        Assert.Contains("inner-missing", result.ErrorDetails);
    }

    [Fact]
    public void Fail_WithoutException_KeepsDetailsNull()
    {
        FeatureResult result = FeatureResult.Fail(@"C:\in.pdf", "输入文件不存在。");

        Assert.Equal("输入文件不存在。", result.Error);
        Assert.Null(result.ErrorDetails);
    }

    [Fact]
    public void FailWithDetails_PassesThroughVerbatim()
    {
        FeatureResult result = FeatureResult.FailWithDetails(
            @"C:\in.pdf", "处理失败", "raw-diagnostic-dump", "去水印");

        Assert.Equal("处理失败", result.Error);
        Assert.Equal("raw-diagnostic-dump", result.ErrorDetails);
        Assert.Equal("去水印", result.FeatureName);
    }

    [Fact]
    public void ProcessingResult_Failed_PropagatesDetails()
    {
        var ex = new InvalidOperationException("boom");
        ProcessingResult result = ProcessingResult.Failed(@"C:\in.pdf", ex.Message, ex.ToString());

        Assert.Equal(ProcessingStatus.Failed, result.Status);
        Assert.Equal("boom", result.Error);
        Assert.NotNull(result.ErrorDetails);
        Assert.Contains(nameof(InvalidOperationException), result.ErrorDetails);
    }

    [Fact]
    public void ProcessingResult_Failed_DefaultsDetailsToNull()
    {
        ProcessingResult result = ProcessingResult.Failed(@"C:\in.pdf", "boom");

        Assert.Null(result.ErrorDetails);
    }
}
