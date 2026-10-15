using WatermarkRemover.Core.Documents;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Core.Watermarks;

namespace WatermarkRemover.Core.Processing;

/// <summary>
/// Orchestrates the pipeline: extract → parse → detect → rewrite → write. Detection is pure
/// analysis; all document mutation happens through <see cref="IDocumentSource.Apply"/>.
/// </summary>
public sealed class WatermarkProcessor
{
    private readonly IDocumentSource _source;
    private readonly IReadOnlyList<IWatermarkStrategy> _strategies;
    private readonly AppSettings _settings;

    public WatermarkProcessor(
        IDocumentSource source,
        IEnumerable<IWatermarkStrategy> strategies,
        AppSettings settings)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _strategies = strategies?.ToList() ?? throw new ArgumentNullException(nameof(strategies));
        if (_strategies.Count == 0)
            throw new ArgumentException("At least one watermark strategy is required.", nameof(strategies));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Processes one file synchronously. Never throws for expected failures.</summary>
    public ProcessingResult Process(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
            return ProcessingResult.Failed(inputPath ?? string.Empty, "输入文件路径为空。");
        if (!File.Exists(inputPath))
            return ProcessingResult.Failed(inputPath, "输入文件不存在。");

        try
        {
            var raw = _source.Extract(inputPath);
            var analysis = Analyze(raw);

            var marks = _strategies
                .SelectMany(s => s.Detect(analysis))
                .ToList();

            if (marks.Count == 0)
                return ProcessingResult.NoWatermarkFound(inputPath);

            var edits = BuildEdits(raw, analysis, marks);

            string outputPath = OutputNameGenerator.GetOutputPath(inputPath, _settings);
            _source.Apply(inputPath, outputPath, edits);

            return ProcessingResult.Succeeded(inputPath, outputPath, marks);
        }
        catch (Exception ex)
        {
            return ProcessingResult.Failed(inputPath, ex.Message);
        }
    }

    /// <summary>Parses raw pages into an analysis document. Kept separate for testability.</summary>
    public static DocumentAnalysis Analyze(RawDocument raw)
    {
        var pages = new List<PageAnalysis>(raw.Pages.Count);
        foreach (var rawPage in raw.Pages)
        {
            var content = Core.Content.ParsedContentStream.Parse(rawPage.ContentStream);
            var interpreter = new Core.Content.ContentInterpreter();
            var drawn = interpreter.Interpret(content, rawPage.Index);

            var annotations = rawPage.Annotations
                .Select(a => new LinkAnnotation(a.Index, a.Bounds, a.Uri))
                .ToList();

            pages.Add(new PageAnalysis(
                rawPage.Index,
                rawPage.MediaBox,
                content,
                drawn,
                rawPage.XObjectNames,
                annotations));
        }

        return new DocumentAnalysis(pages);
    }

    private IReadOnlyList<PageEdit> BuildEdits(
        RawDocument raw,
        DocumentAnalysis analysis,
        IReadOnlyList<WatermarkMark> marks)
    {
        var edits = new List<PageEdit>();

        foreach (var rawPage in raw.Pages)
        {
            var pageMarks = marks.Where(m => m.PageIndex == rawPage.Index).ToList();
            if (pageMarks.Count == 0) continue;

            var operations = pageMarks.SelectMany(m => m.Operations).Distinct().ToList();
            var resources = pageMarks.SelectMany(m => m.ResourceNames).Distinct().ToList();
            var annotations = _settings.RemoveLinkedAnnotations
                ? pageMarks.SelectMany(m => m.AnnotationIndices).Distinct().ToList()
                : new List<int>();

            var pageAnalysis = analysis.Pages[rawPage.Index];
            byte[] newContent = Core.Documents.ContentRewriter.DeleteOperations(pageAnalysis.Content, operations);

            edits.Add(new PageEdit(rawPage.Index, newContent, resources, annotations));
        }

        return edits;
    }
}
