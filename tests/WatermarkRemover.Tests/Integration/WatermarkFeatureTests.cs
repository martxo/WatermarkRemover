using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Core.Watermarks;
using WatermarkRemover.Pdf;
using WatermarkRemover.Tests.Infrastructure;

namespace WatermarkRemover.Tests.Integration;

/// <summary>
/// End-to-end tests of the feature contract: a hand-built PDF fixture goes in through
/// <see cref="WatermarkFeature"/> and a <see cref="FeatureResult"/> comes out.
/// </summary>
public class WatermarkFeatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wm-feature-" + Guid.NewGuid().ToString("N"));

    public WatermarkFeatureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private WatermarkFeature CreateFeature() =>
        new(new PdfDocumentSource(), new IWatermarkStrategy[] { new BottomRightBadgeStrategy() });

    private string WriteInput(byte[] bytes, string name = "input.pdf")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void CanHandle_only_accepts_pdf_files()
    {
        var feature = CreateFeature();

        Assert.True(feature.CanHandle("C:\\a\\扫描件.PDF"));
        Assert.True(feature.CanHandle("C:\\a\\扫描件.pdf"));
        Assert.False(feature.CanHandle("C:\\a\\照片.jpg"));
        Assert.False(feature.CanHandle("C:\\a\\无扩展名"));
    }

    [Fact]
    public void ProcessingText_keeps_the_watermark_wording()
    {
        Assert.Equal("正在分析并去除水印…", CreateFeature().ProcessingText);
    }

    [Fact]
    public void Run_removes_the_badge_and_reports_a_succeeded_result()
    {
        string input = WriteInput(PdfFixture.CamScannerLike().Build());

        var result = CreateFeature().Run(input, FeatureContext.Capture(new AppSettings()));

        Assert.Equal(FeatureStatus.Succeeded, result.Status);
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(1, result.ItemCount);
        Assert.Contains("已去除", result.Summary);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Run_reports_nochange_for_clean_documents()
    {
        string input = WriteInput(PdfFixture.CamScannerLike().WithContent("q Q\n").Build(), "clean.pdf");

        var result = CreateFeature().Run(input, FeatureContext.Capture(new AppSettings()));

        Assert.Equal(FeatureStatus.NoChange, result.Status);
        Assert.Null(result.OutputPath);
        Assert.Contains("未检测到水印", result.Summary);
    }

    [Fact]
    public void Run_reports_failure_for_a_missing_file()
    {
        var result = CreateFeature().Run(Path.Combine(_dir, "missing.pdf"), FeatureContext.Capture(new AppSettings()));

        Assert.Equal(FeatureStatus.Failed, result.Status);
        Assert.Null(result.OutputPath);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public void Run_uses_the_settings_snapshot_from_the_context()
    {
        // The output suffix must come from the captured context, not from anywhere else.
        var settings = new AppSettings { OutputSuffix = "_ctx" };
        string input = WriteInput(PdfFixture.CamScannerLike().Build());

        var result = CreateFeature().Run(input, FeatureContext.Capture(settings));

        Assert.Equal(FeatureStatus.Succeeded, result.Status);
        Assert.EndsWith("_ctx.pdf", Path.GetFileName(result.OutputPath));
    }
}
