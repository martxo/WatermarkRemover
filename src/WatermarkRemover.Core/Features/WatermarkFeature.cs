using WatermarkRemover.Core.Documents;
using WatermarkRemover.Core.Processing;
using WatermarkRemover.Core.Watermarks;

namespace WatermarkRemover.Core.Features;

/// <summary>
/// The original capability of the app: removes detected watermark marks from PDFs by delegating
/// to <see cref="WatermarkProcessor"/>. Exists purely to express the pipeline through the
/// feature contract — no logic of its own.
/// </summary>
public sealed class WatermarkFeature : IFeature
{
    private readonly IDocumentSource _source;
    private readonly IReadOnlyList<IWatermarkStrategy> _strategies;

    public WatermarkFeature(IDocumentSource source, IEnumerable<IWatermarkStrategy> strategies)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _strategies = strategies?.ToList() ?? throw new ArgumentNullException(nameof(strategies));
        if (_strategies.Count == 0)
            throw new ArgumentException("At least one watermark strategy is required.", nameof(strategies));
    }

    public string Id => "watermark.badge";

    public string DisplayName => "去水印";

    public bool CanHandle(string filePath) =>
        string.Equals(Path.GetExtension(filePath), ".pdf", StringComparison.OrdinalIgnoreCase);

    public FeatureResult Run(string inputPath, FeatureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Built per call so the feature always reads the job's settings snapshot.
        var processor = new WatermarkProcessor(_source, _strategies, context.Settings);
        ProcessingResult result = processor.Process(inputPath);

        return result.Status switch
        {
            ProcessingStatus.Succeeded => FeatureResult.Success(
                inputPath, result.OutputPath!, $"已去除 {result.Marks.Count} 处水印", result.Marks.Count),
            ProcessingStatus.NoWatermarkFound => FeatureResult.NoChange(inputPath, "未检测到水印"),
            _ => FeatureResult.Fail(inputPath, result.Error ?? "处理失败"),
        };
    }
}
