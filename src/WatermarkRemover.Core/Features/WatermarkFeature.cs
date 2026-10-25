using WatermarkRemover.Core.Documents;
using WatermarkRemover.Core.Features.Naming;
using WatermarkRemover.Core.Features.Ocr;
using WatermarkRemover.Core.Processing;
using WatermarkRemover.Core.Watermarks;

namespace WatermarkRemover.Core.Features;

/// <summary>
/// The original capability of the app: removes detected watermark marks from PDFs by delegating
/// to <see cref="WatermarkProcessor"/>. Exists purely to express the pipeline through the
/// feature contract — no logic of its own. Optionally recognises the document content so the
/// output file can be auto-named (e.g. 林孝明执照.pdf); naming failures never affect removal.
/// </summary>
public sealed class WatermarkFeature : IFeature
{
    public string Glyph => "\uE75C"; // Erase

    public string Tagline => "拖入 PDF → 去水印 · 智能命名 · 页面管理";

    /// <summary>Render resolution for the naming OCR pass; low is enough to read labels.</summary>
    private const int NamingRenderDpi = 150;

    private readonly IDocumentSource _source;
    private readonly IReadOnlyList<IWatermarkStrategy> _strategies;
    private readonly IPdfPageRenderer? _namingRenderer;
    private readonly IOcrEngine? _namingEngine;
    private readonly Func<byte[], Task<byte[]>>? _orientPage;
    private readonly Action<string, Exception?>? _trace;

    public WatermarkFeature(
        IDocumentSource source,
        IEnumerable<IWatermarkStrategy> strategies,
        IPdfPageRenderer? namingRenderer = null,
        IOcrEngine? namingEngine = null,
        Func<byte[], Task<byte[]>>? orientPage = null,
        Action<string, Exception?>? trace = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _strategies = strategies?.ToList() ?? throw new ArgumentNullException(nameof(strategies));
        if (_strategies.Count == 0)
            throw new ArgumentException("At least one watermark strategy is required.", nameof(strategies));
        _namingRenderer = namingRenderer;
        _namingEngine = namingEngine;
        _orientPage = orientPage;
        _trace = trace;
    }

    public string Id => "watermark.badge";

    public string DisplayName => "去水印";

    public string ProcessingText => "正在分析并去除水印…";

    public bool CanHandle(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<string> InputExtensions => new[] { ".pdf" };

    public FeatureResult Run(string inputPath, FeatureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 状态分站：把每个阶段如实推给记录行，避免"检测水印 / 识别文字 / 检查朝向"混成一团。
        void Stage(string text) => context.Progress?.Invoke(inputPath, text);

        Stage("正在检测水印…");
        // Built per call so the feature always reads the job's settings snapshot.
        var processor = new WatermarkProcessor(
            _source, _strategies, context.Settings,
            context.Settings.AutoNameByContent ? _ => TryAutoName(inputPath, Stage) : null);
        ProcessingResult result = processor.Process(inputPath);

        return FeatureResult.From(result, DisplayName, $"已去除 {result.Marks.Count} 处水印");
    }

    /// <summary>
    /// Content-based name for the output file. Renders only the first page and OCRs it as-is —
    /// upright documents (the overwhelming majority) name themselves right there, at the old
    /// speed. Only a text-starved page (too few lines — the signature of a lying-down scan)
    /// pays for the orientation pass and a second OCR; a text-rich page that still fails to
    /// classify is simply a type the classifier doesn't know, and straightening won't help.
    /// Returns null whenever anything goes wrong — removal must never fail because of naming —
    /// but every skip is reported through the trace callback so a non-renaming machine leaves
    /// evidence in the log.
    /// </summary>
    private AutoNameResult? TryAutoName(string pdfPath, Action<string> stage)
    {
        if (_namingRenderer is null || _namingEngine is null)
        {
            _trace?.Invoke("命名：未配置识别能力（渲染器或 OCR 引擎缺失），跳过自动命名", null);
            return null;
        }

        try
        {
            byte[] page = _namingRenderer.RenderFirstPageAsync(pdfPath, NamingRenderDpi)
                .GetAwaiter().GetResult();

            stage("正在识别文字以命名…");
            var lines = _namingEngine.RecognizeBoundedOrDefaultAsync(page).GetAwaiter().GetResult();
            var named = SmartNamingService.TryBuildName(lines, _trace);
            if (named is not null) return named;

            if (lines.Count >= 3 || _orientPage is null)
            {
                _trace?.Invoke(
                    $"命名：首页识别 {lines.Count} 行仍无法分类，跳过自动命名（文字充足，摆正无益）", null);
                return null;
            }

            // 摆正后再识别：导入的 PDF 不走扫描的自动调正，躺倒页 OCR 读不出有效文字
            // （与扫描命名链 ContentNamingService 同一条摆正实现）。
            stage("正在检查页面朝向…");
            byte[] upright = _orientPage(page).GetAwaiter().GetResult();
            // No turn applied (already upright, or a blank page the detector refuses to judge):
            // the retry would OCR the identical pixels again, so skip it. Sideways pages still
            // get their second reading because the detector returns turned bytes.
            if (ReferenceEquals(upright, page))
            {
                _trace?.Invoke("命名：无需摆正，跳过重复识别", null);
                return null;
            }
            stage("正在重新识别文字…");
            var retry = _namingEngine.RecognizeBoundedOrDefaultAsync(upright).GetAwaiter().GetResult();
            _trace?.Invoke(
                $"命名：摆正后识别 {retry.Count} 行：{string.Join(" / ", retry.Select(l => l.Text))}",
                null);
            return SmartNamingService.TryBuildName(retry, _trace);
        }
        catch (Exception ex)
        {
            _trace?.Invoke("命名：识别过程异常，跳过自动命名", ex);
            return null;
        }
    }
}
