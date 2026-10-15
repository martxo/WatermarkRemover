using System.Text;
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

    public OcrFeature(IPdfPageRenderer renderer, IOcrEngine engine)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public string Id => "ocr.text";

    public string DisplayName => "OCR 识别";

    public bool CanHandle(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase);

    public FeatureResult Run(string inputPath, FeatureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            if (!File.Exists(inputPath))
                return FeatureResult.Fail(inputPath, "输入文件不存在。");

            var pages = _renderer.RenderPagesAsync(inputPath, RenderDpi).GetAwaiter().GetResult();
            if (pages.Count == 0)
                return FeatureResult.Fail(inputPath, "无法从文档渲染出任何页面。");

            var pageTexts = new List<string>(pages.Count);
            foreach (var page in pages)
            {
                var lines = _engine.RecognizeAsync(page).GetAwaiter().GetResult();
                pageTexts.Add(string.Join(Environment.NewLine, lines));
            }

            if (pageTexts.All(string.IsNullOrWhiteSpace))
                return FeatureResult.NoChange(inputPath, "未识别到文字");

            string text = string.Join(Environment.NewLine + Environment.NewLine, pageTexts);
            string outputPath = OutputNameGenerator.GetOutputPath(
                inputPath, context.Settings, suffixOverride: OutputSuffix, extensionOverride: ".txt");

            // UTF-8 with BOM so Notepad and Excel detect the encoding reliably.
            File.WriteAllText(outputPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            return FeatureResult.Success(inputPath, outputPath, $"识别 {pages.Count} 页（{_engine.DisplayName}）", pages.Count);
        }
        catch (Exception ex)
        {
            return FeatureResult.Fail(inputPath, ex.Message);
        }
    }
}
