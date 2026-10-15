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
    /// <summary>Render resolution for the naming OCR pass; low is enough to read labels.</summary>
    private const int NamingRenderDpi = 150;

    private readonly IDocumentSource _source;
    private readonly IReadOnlyList<IWatermarkStrategy> _strategies;
    private readonly IPdfPageRenderer? _namingRenderer;
    private readonly IOcrEngine? _namingEngine;

    public WatermarkFeature(
        IDocumentSource source,
        IEnumerable<IWatermarkStrategy> strategies,
        IPdfPageRenderer? namingRenderer = null,
        IOcrEngine? namingEngine = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _strategies = strategies?.ToList() ?? throw new ArgumentNullException(nameof(strategies));
        if (_strategies.Count == 0)
            throw new ArgumentException("At least one watermark strategy is required.", nameof(strategies));
        _namingRenderer = namingRenderer;
        _namingEngine = namingEngine;
    }

    public string Id => "watermark.badge";

    public string DisplayName => "去水印";

    public bool CanHandle(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase);

    public FeatureResult Run(string inputPath, FeatureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Built per call so the feature always reads the job's settings snapshot.
        var processor = new WatermarkProcessor(
            _source, _strategies, context.Settings,
            context.Settings.AutoNameByContent ? TryAutoName : null);
        ProcessingResult result = processor.Process(inputPath);

        return result.Status switch
        {
            ProcessingStatus.Succeeded => FeatureResult.Success(
                inputPath, result.OutputPath!, $"已去除 {result.Marks.Count} 处水印", result.Marks.Count),
            ProcessingStatus.NoWatermarkFound => FeatureResult.NoChange(inputPath, "未检测到水印"),
            _ => FeatureResult.Fail(inputPath, result.Error ?? "处理失败"),
        };
    }

    /// <summary>
    /// Content-based name for the output file. Renders only the first page, recognises it and
    /// classifies it. Returns null whenever anything goes wrong — removal must never fail
    /// because of naming.
    /// </summary>
    private AutoNameResult? TryAutoName(string pdfPath)
    {
        if (_namingRenderer is null || _namingEngine is null) return null;

        try
        {
            byte[] page = _namingRenderer.RenderFirstPageAsync(pdfPath, NamingRenderDpi)
                .GetAwaiter().GetResult();
            var lines = _namingEngine.RecognizeAsync(page).GetAwaiter().GetResult();
            return SmartNamingService.TryBuildName(lines);
        }
        catch
        {
            return null;
        }
    }
}
