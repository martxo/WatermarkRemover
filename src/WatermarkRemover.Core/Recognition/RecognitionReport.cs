using WatermarkRemover.Core.Geometry;

namespace WatermarkRemover.Core.Recognition;

/// <summary>Severity of one review finding, ordered least to most severe.</summary>
public enum ReviewSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// Structured record of one recognition run: what the pipeline saw (pages, orientation,
/// regions, lines), what the system concluded (classification, field observations, name) and
/// what review found suspicious. Stored as JSONL — one report per line — as an inspection and
/// future training corpus, separate from the free-text log stream.
/// </summary>
public sealed record RecognitionReport
{
    public string SchemaVersion { get; init; } = "1";

    public DateTimeOffset Timestamp { get; init; }

    public string InputPath { get; init; } = "";

    public string FeatureName { get; init; } = "";

    /// <summary>Inner recognition engine used (微信OCR / PP-OCR / Windows 原生 …).</summary>
    public string EngineName { get; init; } = "";

    /// <summary>Layout building block used (整图直通 / DocLayout-YOLO …).</summary>
    public string LayoutStageName { get; init; } = "";

    public int PageCount { get; init; }

    public IReadOnlyList<PageReport> Pages { get; init; } = Array.Empty<PageReport>();

    public ClassificationReport? Classification { get; init; }

    /// <summary>Field labels with value text and boxes, in page then reading order.</summary>
    public IReadOnlyList<FieldObservation> Fields { get; init; } = Array.Empty<FieldObservation>();

    public string? PersonName { get; init; }

    public string? SuggestedStem { get; init; }

    public IReadOnlyList<ReviewIssue> Issues { get; init; } = Array.Empty<ReviewIssue>();
}

/// <summary>One recognised page; region/line coordinates are normalized 0..1 (origin top-left).</summary>
public sealed record PageReport(
    int Index,
    int? WidthPx,
    int? HeightPx,
    string? Orientation,
    IReadOnlyList<RegionReport> Regions,
    IReadOnlyList<LineReport> Lines);

public sealed record RegionReport(double X, double Y, double W, double H, string Kind, double Score);

/// <summary>One text line; coordinates are null when the engine reports no positions.</summary>
public sealed record LineReport(string Text, double? X, double? Y, double? W, double? H);

/// <summary>Document type verdict; RunnerUp is the second-best profile score (margin basis).</summary>
public sealed record ClassificationReport(string TypeId, string TypeShortName, int Score, int RunnerUp);

/// <summary>
/// One field label with the value text that belongs to it and the boxes locating both
/// (page-normalized). This is the "经营者出现在哪里、值是什么" data the report exists for.
/// </summary>
public sealed record FieldObservation(string Label, RectD? LabelBox, string Value, RectD? ValueBox);

public sealed record ReviewIssue(string RuleId, ReviewSeverity Severity, string Message, string Evidence);
