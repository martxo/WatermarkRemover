using System.Text;
using WatermarkRemover.Core.Logging;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Core.Features.Ocr;

/// <summary>
/// Extracts the text of a PDF: render each page to an image, run it through the
/// <see cref="IOcrEngine"/>, write the text next to the input as <c>&lt;name&gt;_ocr.txt</c>.
/// The engine and the renderer are injected, so both are replaceable without touching this class.
/// </summary>
public sealed class OcrFeature : IFeature
{
    /// <summary>Suffix of the generated text file.</summary>
    public const string OutputSuffix = "_ocr";

    /// <summary>Render resolution. 200 DPI keeps even A3 pages inside OCR size limits.</summary>
    private const int RenderDpi = 200;

    private readonly IPdfPageRenderer _renderer;
    private readonly IOcrEngine _engine;
    private readonly Action<string, Exception?>? _trace;

    public OcrFeature(IPdfPageRenderer renderer, IOcrEngine engine, Action<string, Exception?>? trace = null)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _trace = trace;
    }

    public string Id => "ocr.text";

    public string DisplayName => "OCR 识别";

    public string ProcessingText => "正在渲染页面并识别文字…";

    public bool CanHandle(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase);

    public FeatureResult Run(string inputPath, FeatureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // One linear per-stage timeline (渲染/逐页识别/写出/总计) for performance analysis.
        var timer = new StageTimer(_trace, Path.GetFileName(inputPath));
        try
        {
            if (!File.Exists(inputPath))
                return FeatureResult.Fail(inputPath, "输入文件不存在。");

            var pages = _renderer.RenderPagesAsync(inputPath, RenderDpi).GetAwaiter().GetResult();
            timer.Stage("渲染页面", $"{pages.Count} 页 {RenderDpi}dpi");
            if (pages.Count == 0)
            {
                timer.Emit($"无页面可识别（已耗时 {timer.ElapsedMs} ms）");
                return FeatureResult.Fail(inputPath, "无法从文档渲染出任何页面。");
            }

            var pageTexts = new List<string>(pages.Count);
            foreach (var page in pages)
            {
                var lines = _engine.RecognizeAsync(page).GetAwaiter().GetResult();
                timer.Stage($"第{pageTexts.Count + 1}页识别", $"{lines.Count} 行（{_engine.DisplayName}）");
                pageTexts.Add(string.Join(Environment.NewLine, lines));
            }

            if (pageTexts.All(string.IsNullOrWhiteSpace))
            {
                timer.Emit($"未识别到文字（已耗时 {timer.ElapsedMs} ms）");
                return FeatureResult.NoChange(inputPath, "未识别到文字");
            }

            string text = string.Join(Environment.NewLine + Environment.NewLine, pageTexts);
            string outputPath = OutputNameGenerator.GetOutputPath(
                inputPath, context.Settings, suffixOverride: OutputSuffix, extensionOverride: ".txt");

            // UTF-8 with BOM so Notepad and Excel detect the encoding reliably.
            File.WriteAllText(outputPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            timer.Stage("写出文本", $"{new FileInfo(outputPath).Length} 字节");
            timer.Emit($"总计 {timer.ElapsedMs} ms（{pages.Count} 页，{_engine.DisplayName}）");

            return FeatureResult.Success(inputPath, outputPath, $"识别 {pages.Count} 页（{_engine.DisplayName}）", pages.Count);
        }
        catch (Exception ex)
        {
            // Keep whatever stages already completed: an aborted run is exactly when the
            // per-stage numbers are needed for diagnosis.
            timer.Emit($"中断（已耗时 {timer.ElapsedMs} ms）：{ex.Message}");
            return FeatureResult.Fail(inputPath, ex.Message, ex, DisplayName);
        }
    }
}
