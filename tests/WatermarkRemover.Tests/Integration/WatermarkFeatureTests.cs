using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Features.Naming;
using WatermarkRemover.Core.Features.Ocr;
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

    /// <summary>Naming-enabled feature: stub renderer + stub engine, and an orient delegate that
    /// counts its calls so the tests can pin the "pay for straightening only when starved"
    /// contract (v1.15.34: naming used to orient EVERY page, which users read as a slowdown).</summary>
    private (WatermarkFeature Feature, StubOcrEngine Engine, StubPageRenderer Renderer) CreateNamingFeature(
        AppSettings? settings = null)
    {
        var engine = new StubOcrEngine();
        var renderer = new StubPageRenderer();
        var feature = new WatermarkFeature(
            new PdfDocumentSource(),
            new IWatermarkStrategy[] { new BottomRightBadgeStrategy() },
            renderer, engine,
            orientPage: page =>
            {
                renderer.OrientCalls++;
                return Task.FromResult(page);
            });
        return (feature, engine, renderer);
    }

    private sealed class StubPageRenderer : IPdfPageRenderer
    {
        public int OrientCalls { get; set; }

        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi) =>
            Task.FromResult<IReadOnlyList<byte[]>>(new[] { Array.Empty<byte>() });

        public Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi) => Task.FromResult(Array.Empty<byte>());

        public Task<int> GetPageCountAsync(string pdfPath) => Task.FromResult(1);

        public Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi, bool priority = false) =>
            Task.FromResult(Array.Empty<byte>());
    }

    private sealed class StubOcrEngine : IOcrEngine
    {
        public string[] Lines { get; set; } = Array.Empty<string>();

        public string DisplayName => "stub";

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
            Task.FromResult<IReadOnlyList<string>>(Lines);
    }

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
    public void Naming_skips_the_orientation_pass_for_text_rich_pages()
    {
        // 4 junk lines: text is plentiful, so a failed classification is a classifier gap —
        // straightening cannot help and must not be paid for (this was the v1.15.32 slowdown).
        string input = WriteInput(PdfFixture.CamScannerLike().WithContent("q Q\n").Build(), "rich.pdf");
        var (feature, engine, renderer) = CreateNamingFeature();
        engine.Lines = new[] { "行一", "行二", "行三", "行四" };

        var settings = new AppSettings { AutoNameByContent = true };
        feature.Run(input, FeatureContext.Capture(settings));

        Assert.Equal(0, renderer.OrientCalls);
    }

    [Fact]
    public void Naming_pays_the_orientation_pass_only_for_text_starved_pages()
    {
        // Empty OCR: the lying-down signature — the detector must run exactly once, then the
        // retry OCR still names nothing and the pipeline keeps its original skip behaviour.
        string input = WriteInput(PdfFixture.CamScannerLike().WithContent("q Q\n").Build(), "starved.pdf");
        var (feature, engine, renderer) = CreateNamingFeature();
        engine.Lines = Array.Empty<string>();

        var settings = new AppSettings { AutoNameByContent = true };
        var result = feature.Run(input, FeatureContext.Capture(settings));

        Assert.Equal(1, renderer.OrientCalls);
        Assert.Equal(FeatureStatus.NoChange, result.Status);
        Assert.Null(result.OutputPath);
    }

    [Fact]
    public void Naming_reports_each_stage_through_the_context_progress()
    {
        string input = WriteInput(PdfFixture.CamScannerLike().WithContent("q Q\n").Build(), "stages.pdf");
        var (feature, engine, _) = CreateNamingFeature();
        engine.Lines = Array.Empty<string>(); // force the full stage ladder

        var stages = new List<string>();
        var settings = new AppSettings { AutoNameByContent = true };
        feature.Run(input, FeatureContext.Capture(settings, (_, stage) => stages.Add(stage)));

        Assert.Contains("正在检测水印…", stages);
        Assert.Contains("正在识别文字以命名…", stages);
        Assert.Contains("正在检查页面朝向…", stages);
        Assert.Contains("正在重新识别文字…", stages);
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
