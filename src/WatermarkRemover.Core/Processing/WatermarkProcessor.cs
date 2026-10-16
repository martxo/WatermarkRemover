using WatermarkRemover.Core.Documents;
using WatermarkRemover.Core.Features.Naming;
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
    private readonly Func<string, AutoNameResult?>? _autoNamer;

    public WatermarkProcessor(
        IDocumentSource source,
        IEnumerable<IWatermarkStrategy> strategies,
        AppSettings settings,
        Func<string, AutoNameResult?>? autoNamer = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _strategies = strategies?.ToList() ?? throw new ArgumentNullException(nameof(strategies));
        if (_strategies.Count == 0)
            throw new ArgumentException("At least one watermark strategy is required.", nameof(strategies));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _autoNamer = autoNamer;
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

            string outputPath = ResolveOutputPath(inputPath);
            _source.Apply(inputPath, outputPath, edits);

            return ProcessingResult.Succeeded(inputPath, outputPath, marks);
        }
        catch (Exception ex)
        {
            return ProcessingResult.Failed(inputPath, ex.Message, ex.ToString());
        }
    }

    /// <summary>
    /// Picks the output path: content-based naming when the auto-namer can identify the document
    /// (e.g. 林孝明执照.pdf), otherwise the default suffix naming. The auto-namer never throws —
    /// any failure simply falls back to the default, so the main pipeline is never at risk.
    /// </summary>
    private string ResolveOutputPath(string inputPath)
    {
        string directory = _settings.OutputMode == OutputMode.CustomDirectory &&
                           !string.IsNullOrWhiteSpace(_settings.OutputDirectory)
            ? _settings.OutputDirectory
            : Path.GetDirectoryName(inputPath) ?? string.Empty;
        string extension = Path.GetExtension(inputPath);

        AutoNameResult? autoName = null;
        try
        {
            autoName = _autoNamer?.Invoke(inputPath);
        }
        catch
        {
            // Naming is a convenience; never let it fail the watermark removal itself.
        }

        return autoName is not null
            ? OutputNameGenerator.FindAvailablePath(directory, autoName.SuggestedStem + extension)
            : OutputNameGenerator.GetOutputPath(inputPath, _settings);
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
