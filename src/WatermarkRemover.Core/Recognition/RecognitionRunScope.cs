using WatermarkRemover.Core.Features.Layout;

namespace WatermarkRemover.Core.Recognition;

/// <summary>
/// Per-task ambient collector for one recognition run. The pipeline is a shared singleton, so
/// the only way to tell "which job is this call for" is the async execution context: the job
/// queue opens a scope inside each task's body and the pipeline records every page it
/// recognises into <see cref="Current"/>. When no scope is open (unit tests, probes) recording
/// is a no-op and pipeline output stays byte-identical to the pre-scope behaviour.
/// </summary>
public sealed class RecognitionRunScope : IDisposable
{
    /// <summary>Report size guard: at most this many lines are kept per page.</summary>
    private const int MaxLinesPerPage = 500;

    /// <summary>Report size guard: a single line is clipped to this many characters.</summary>
    private const int MaxLineChars = 200;

    private static readonly AsyncLocal<RecognitionRunScope?> Ambient = new();

    private readonly List<RecordedPage> _pages = new();

    /// <summary>Ambient value before this scope was opened — restored on dispose (nesting-safe).</summary>
    private readonly RecognitionRunScope? _previous;

    private RecognitionRunScope(string inputPath, string featureName)
    {
        InputPath = inputPath;
        FeatureName = featureName;
        _previous = Ambient.Value;
    }

    /// <summary>Scope of the current async flow, or null when no run is being collected.</summary>
    public static RecognitionRunScope? Current => Ambient.Value;

    /// <summary>File the run was started for (job input path).</summary>
    public string InputPath { get; }

    /// <summary>Display name of the feature driving the run (OCR 识别 / 去除水印 …).</summary>
    public string FeatureName { get; }

    /// <summary>Pages recorded so far, in recognition order.</summary>
    public IReadOnlyList<RecordedPage> Pages => _pages;

    /// <summary>Opens a collection scope for one job; disposing clears the ambient value.</summary>
    public static RecognitionRunScope Begin(string inputPath, string featureName)
    {
        var scope = new RecognitionRunScope(inputPath, featureName);
        Ambient.Value = scope;
        return scope;
    }

    /// <summary>
    /// Records one recognised page. Line count and line length are clipped here so the JSONL
    /// report can never balloon on pathological pages.
    /// </summary>
    public void RecordPage(int? widthPx, int? heightPx, string? orientation,
        string layoutStage, string engineName,
        IReadOnlyList<LayoutRegion> regions, IReadOnlyList<RecognizedLine> lines)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(lines);

        int keep = Math.Min(lines.Count, MaxLinesPerPage);
        var clipped = new List<RecognizedLine>(keep);
        for (int i = 0; i < keep; i++)
        {
            RecognizedLine line = lines[i];
            clipped.Add(line.Text.Length > MaxLineChars
                ? line with { Text = line.Text[..MaxLineChars] }
                : line);
        }

        _pages.Add(new RecordedPage(_pages.Count + 1, widthPx, heightPx, orientation,
            layoutStage, engineName, regions.ToArray(), clipped));
    }

    /// <summary>Restores the ambient value (the scope's predecessor, or null at the top).</summary>
    public void Dispose()
    {
        if (Ambient.Value == this) Ambient.Value = _previous;
    }
}

/// <summary>Everything the pipeline saw on one page, in full-page normalized coordinates.</summary>
public sealed record RecordedPage(
    int Index,
    int? WidthPx,
    int? HeightPx,
    string? Orientation,
    string LayoutStage,
    string EngineName,
    IReadOnlyList<LayoutRegion> Regions,
    IReadOnlyList<RecognizedLine> Lines);
