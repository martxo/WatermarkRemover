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

    /// <summary>Raster formats the feature accepts as a single "page". The loader turns each one
    /// into the same PNG the PDF renderer produces, so the engines see one kind of input.</summary>
    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".webp" };

    private readonly IPdfPageRenderer _renderer;
    private readonly IOcrEngine _engine;
    private readonly Action<string, Exception?>? _trace;

    /// <summary>Loads a photo/scan as OCR-ready PNG bytes; null = this build cannot read images,
    /// and <see cref="CanHandle"/> keeps rejecting them rather than failing at run time.</summary>
    private readonly Func<string, byte[]>? _imageLoader;

    public OcrFeature(
        IPdfPageRenderer renderer,
        IOcrEngine engine,
        Action<string, Exception?>? trace = null,
        Func<string, byte[]>? imageLoader = null)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _trace = trace;
        _imageLoader = imageLoader;
    }

    public string Id => "ocr.text";

    public string DisplayName => "OCR 识别";

    public bool CanHandle(string filePath)
    {
        string extension = Path.GetExtension(filePath);
        if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase)) return true;
        return _imageLoader is not null && IsImage(extension);
    }

    public IReadOnlyList<string> InputExtensions =>
        _imageLoader is null
            ? new[] { ".pdf" }
            : new[] { ".pdf" }.Concat(ImageExtensions).ToArray();

    private static bool IsImage(string extension) =>
        ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase);

    public FeatureResult Run(string inputPath, FeatureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // One linear per-stage timeline (渲染/逐页识别/写出/总计) for performance analysis.
        var timer = new StageTimer(_trace, Path.GetFileName(inputPath));
        bool isImage = IsImage(Path.GetExtension(inputPath));
        try
        {
            if (!File.Exists(inputPath))
                return FeatureResult.Fail(inputPath, "输入文件不存在。");
            if (isImage && _imageLoader is null)
                return FeatureResult.Fail(inputPath, "当前构建不支持图片输入。");

            IReadOnlyList<byte[]> pages;
            if (isImage)
            {
                // A dropped photo is one page: no PDF round-trip, no renderer work.
                pages = new[] { _imageLoader!(inputPath) };
                timer.Stage("读取图片", $"{pages[0].Length / 1024} KB");
            }
            else
            {
                pages = _renderer.RenderPagesAsync(inputPath, RenderDpi).GetAwaiter().GetResult();
                timer.Stage("渲染页面", $"{pages.Count} 页 {RenderDpi}dpi");
            }

            if (pages.Count == 0)
            {
                timer.Emit($"无页面可识别（已耗时 {timer.ElapsedMs} ms）");
                return FeatureResult.Fail(inputPath, "无法从文档渲染出任何页面。");
            }

            var pageTexts = new List<string>(pages.Count);
            foreach (var page in pages)
            {
                var lines = _engine.RecognizeAsync(page).GetAwaiter().GetResult();
                string stage = isImage ? "图片识别" : $"第{pageTexts.Count + 1}页识别";
                timer.Stage(stage, $"{lines.Count} 行（{_engine.DisplayName}）");
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
            timer.Emit($"总计 {timer.ElapsedMs} ms（{(isImage ? "1 张图片" : $"{pages.Count} 页")}，" +
                       $"{_engine.DisplayName}）");

            string summary = isImage
                ? $"识别图片（{_engine.DisplayName}）"
                : $"识别 {pages.Count} 页（{_engine.DisplayName}）";
            return FeatureResult.Success(inputPath, outputPath, summary, pages.Count);
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
