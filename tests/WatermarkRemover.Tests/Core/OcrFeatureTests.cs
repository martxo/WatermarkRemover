using System.Text;
using WatermarkRemover.App.Presentation;
using WatermarkRemover.Core.Features;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Tests.Core;

public class OcrFeatureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wm-ocr-" + Guid.NewGuid().ToString("N"));

    public OcrFeatureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class FakeRenderer(int pageCount) : IPdfPageRenderer
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi)
        {
            Calls++;
            var pages = new List<byte[]>();
            for (int i = 0; i < pageCount; i++) pages.Add(new byte[] { 0x89, (byte)'P' });
            return Task.FromResult<IReadOnlyList<byte[]>>(pages);
        }

        public Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi) =>
            Task.FromResult(new byte[] { 0x89, (byte)'P' });

        public Task<int> GetPageCountAsync(string pdfPath) => Task.FromResult(pageCount);

        public Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi, bool priority = false) =>
            Task.FromResult(new byte[] { 0x89, (byte)'P' });
    }

    private sealed class FakeEngine(IReadOnlyList<string> lines) : IOcrEngine
    {
        public string DisplayName => "测试引擎";

        public Task<IReadOnlyList<string>> RecognizeAsync(byte[] imageBytes) =>
            Task.FromResult(lines);
    }

    private sealed class ThrowingRenderer : IPdfPageRenderer
    {
        public Task<IReadOnlyList<byte[]>> RenderPagesAsync(string pdfPath, int dpi) =>
            throw new InvalidOperationException("渲染失败");

        public Task<byte[]> RenderFirstPageAsync(string pdfPath, int dpi) =>
            throw new InvalidOperationException("渲染失败");

        public Task<int> GetPageCountAsync(string pdfPath) =>
            throw new InvalidOperationException("渲染失败");

        public Task<byte[]> RenderPageAsync(string pdfPath, int pageIndex, int dpi, bool priority = false) =>
            throw new InvalidOperationException("渲染失败");
    }

    private string WriteInput(string name = "scan.pdf")
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[] { 0x25, 0x50 }); // "%P"
        return path;
    }

    [Fact]
    public void CanHandle_accepts_pdf_only_without_an_image_loader()
    {
        var feature = new OcrFeature(new FakeRenderer(0), new FakeEngine(Array.Empty<string>()));

        Assert.True(feature.CanHandle("C:\\a\\doc.PDF"));
        Assert.False(feature.CanHandle("C:\\a\\doc.jpg")); // no loader = cannot read images
        Assert.Equal(new[] { ".pdf" }, feature.InputExtensions);
    }

    [Fact]
    public void Images_are_accepted_and_the_picker_offers_them_when_a_loader_is_wired()
    {
        var feature = new OcrFeature(
            new FakeRenderer(0), new FakeEngine(Array.Empty<string>()),
            imageLoader: _ => new byte[] { 0x89 });

        Assert.True(feature.CanHandle("C:\\a\\照片.JPG"));
        Assert.True(feature.CanHandle("C:\\a\\照片.png"));
        Assert.True(feature.CanHandle("C:\\a\\doc.pdf"));
        Assert.False(feature.CanHandle("C:\\a\\notes.txt"));
        Assert.Contains(".jpg", feature.InputExtensions);
        Assert.Contains(".pdf", feature.InputExtensions);
    }

    [Fact]
    public void An_image_is_recognised_without_rendering_a_pdf_page()
    {
        var renderer = new FakeRenderer(9);
        var loaded = new List<string>();
        var feature = new OcrFeature(
            renderer, new FakeEngine(new[] { "照片里的字" }),
            imageLoader: path =>
            {
                loaded.Add(path);
                return new byte[] { 0x89, 0x50 };
            });
        string input = Path.Combine(_dir, "photo.jpg");
        File.WriteAllBytes(input, new byte[] { 0xFF, 0xD8 });

        FeatureResult result = feature.Run(input, FeatureContext.Capture(new AppSettings()));

        Assert.Equal(FeatureStatus.Succeeded, result.Status);
        Assert.Equal(0, renderer.Calls); // the PDF channel was never touched
        Assert.Equal(new[] { input }, loaded);
        Assert.EndsWith("_ocr.txt", Path.GetFileName(result.OutputPath));
        Assert.Contains("照片里的字", File.ReadAllText(result.OutputPath!, Encoding.UTF8));
    }

    [Fact]
    public void ProcessingText_describes_recognition_not_watermark_removal()
    {
        var feature = new OcrFeature(new FakeRenderer(1), new FakeEngine(Array.Empty<string>()));

        string text = FeaturePresentationCatalog.ProcessingText(feature);
        Assert.Contains("识别", text);
        Assert.DoesNotContain("水印", text);
    }

    [Fact]
    public void Trace_emits_stage_lines_including_total()
    {
        var lines = new List<string>();
        var feature = new OcrFeature(
            new FakeRenderer(2), new FakeEngine(new[] { "第一页内容" }), (message, _) => lines.Add(message));
        string input = WriteInput("trace.pdf");

        var result = feature.Run(input, FeatureContext.Capture(new AppSettings()));

        Assert.Equal(FeatureStatus.Succeeded, result.Status);
        Assert.Contains(lines, l => l.Contains("环节耗时[trace.pdf]：渲染页面") && l.Contains("2 页 200dpi"));
        Assert.Contains(lines, l => l.Contains("：第1页识别"));
        Assert.Contains(lines, l => l.Contains("：第2页识别"));
        Assert.Contains(lines, l => l.Contains("：写出文本"));
        Assert.Contains(lines, l => l.Contains("：总计") && l.Contains("2 页"));
        // Every stage line carries its cumulative timing so logs are analysable standalone
        // (the free-form 总计 line is the only exception).
        Assert.All(lines.Where(l => !l.Contains("：总计")), l => Assert.Contains(" ms（累计", l));
    }

    [Fact]
    public void Throwing_renderer_still_emits_elapsed_line()
    {
        var lines = new List<string>();
        var feature = new OcrFeature(
            new ThrowingRenderer(), new FakeEngine(Array.Empty<string>()), (message, _) => lines.Add(message));
        string input = WriteInput("boom.pdf");

        var result = feature.Run(input, FeatureContext.Capture(new AppSettings()));

        Assert.Equal(FeatureStatus.Failed, result.Status);
        Assert.Contains(lines, l => l.Contains("中断（已耗时") && l.Contains("渲染失败"));
    }

    [Fact]
    public async Task Run_writes_one_text_file_with_all_page_text()
    {
        string input = WriteInput();
        var feature = new OcrFeature(new FakeRenderer(2), new FakeEngine(new[] { "第一页内容" }));

        FeatureResult result = await Task.Run(() => feature.Run(input, FeatureContext.Capture(new AppSettings())));

        Assert.Equal(FeatureStatus.Succeeded, result.Status);
        Assert.True(File.Exists(result.OutputPath));
        Assert.EndsWith("_ocr.txt", Path.GetFileName(result.OutputPath));
        Assert.Equal(2, result.ItemCount);
        Assert.Contains("识别 2 页", result.Summary);

        string text = File.ReadAllText(result.OutputPath!, Encoding.UTF8);
        Assert.Contains("第一页内容", text);
    }

    [Fact]
    public async Task Run_reports_nochange_when_no_text_is_recognised()
    {
        string input = WriteInput();
        var feature = new OcrFeature(new FakeRenderer(1), new FakeEngine(Array.Empty<string>()));

        FeatureResult result = await Task.Run(() => feature.Run(input, FeatureContext.Capture(new AppSettings())));

        Assert.Equal(FeatureStatus.NoChange, result.Status);
        Assert.Null(result.OutputPath);
        Assert.Contains("未识别到文字", result.Summary);
    }

    [Fact]
    public async Task Run_reports_renderer_failures_as_failed()
    {
        string input = WriteInput();
        var feature = new OcrFeature(new ThrowingRenderer(), new FakeEngine(Array.Empty<string>()));

        FeatureResult result = await Task.Run(() => feature.Run(input, FeatureContext.Capture(new AppSettings())));

        Assert.Equal(FeatureStatus.Failed, result.Status);
        Assert.Contains("渲染失败", result.Error);
    }

    [Fact]
    public async Task Run_fails_for_a_missing_file()
    {
        var feature = new OcrFeature(new FakeRenderer(1), new FakeEngine(new[] { "x" }));

        FeatureResult result = await Task.Run(() =>
            feature.Run(Path.Combine(_dir, "missing.pdf"), FeatureContext.Capture(new AppSettings())));

        Assert.Equal(FeatureStatus.Failed, result.Status);
    }

    [Fact]
    public async Task Run_respects_a_custom_output_directory()
    {
        string input = WriteInput();
        string custom = Path.Combine(_dir, "custom");
        Directory.CreateDirectory(custom);
        var settings = new AppSettings { OutputMode = OutputMode.CustomDirectory, OutputDirectory = custom };

        var feature = new OcrFeature(new FakeRenderer(1), new FakeEngine(new[] { "文本" }));
        FeatureResult result = await Task.Run(() => feature.Run(input, FeatureContext.Capture(settings)));

        Assert.True(File.Exists(result.OutputPath));
        Assert.StartsWith(custom, result.OutputPath);
    }
}
