using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Features.Naming;
using WatermarkRemover.Core.Geometry;
using WatermarkRemover.Core.Recognition.Review;

namespace WatermarkRemover.Core.Recognition;

/// <summary>
/// Pure aggregation entry point: turns the pages a run recorded into one complete
/// <see cref="RecognitionReport"/> — classification with scores, field observations, name
/// suggestion and review issues. No IO, no logging; persistence and log summaries are the
/// job queue's concern.
/// </summary>
public static class RecognitionReportBuilder
{
    private static readonly IRecognitionReportReviewer DefaultReviewer = new RuleBasedReviewer();

    /// <summary>Coordinate precision written into reports (JSONL size guard).</summary>
    private const int Precision = 4;

    public static RecognitionReport Build(RecognitionRunScope scope, DateTimeOffset? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return Build(scope.InputPath, scope.FeatureName, scope.Pages, timestamp);
    }

    public static RecognitionReport Build(string inputPath, string featureName,
        IReadOnlyList<RecordedPage> pages, DateTimeOffset? timestamp = null,
        IRecognitionReportReviewer? reviewer = null)
    {
        ArgumentNullException.ThrowIfNull(pages);

        RecordedPage? first = pages.Count > 0 ? pages[0] : null;
        var orderedPages = pages.Select(p => (Page: p, Lines: InReportOrder(p.Lines))).ToList();

        var pageReports = orderedPages.Select(t => ToPageReport(t.Page, t.Lines)).ToList();

        List<RecognizedLine> lines = orderedPages.SelectMany(t => t.Lines).ToList();
        string text = string.Join("\n", lines.Select(l => l.Text));
        (DocumentProfile? profile, int score, int runnerUp) = DocumentClassifier.ClassifyDetailed(text);

        IReadOnlyList<FieldObservation> fields = orderedPages
            .SelectMany(t => LicenseFieldExtractor.Extract(t.Lines))
            .Select(RoundField)
            .ToList();

        string? personName = profile is null
            ? null
            : NameExtractor.Extract(lines.Select(l => l.Text).ToList(), profile);

        var report = new RecognitionReport
        {
            Timestamp = timestamp ?? DateTimeOffset.Now,
            InputPath = inputPath,
            FeatureName = featureName,
            EngineName = first?.EngineName ?? "",
            LayoutStageName = first?.LayoutStage ?? "",
            PageCount = pages.Count,
            Pages = pageReports,
            Classification = profile is null
                ? null
                : new ClassificationReport(profile.TypeId, profile.TypeShortName, score, runnerUp),
            Fields = fields,
            PersonName = personName,
            SuggestedStem = personName is null ? null : personName + profile!.TypeShortName,
        };

        return report with { Issues = (reviewer ?? DefaultReviewer).Review(report) };
    }

    /// <summary>
    /// Report line order per page: bounded lines in visual reading order (the reviewer joins
    /// lines in array order to detect split labels, so engine/region order would both hide real
    /// splits and read unnaturally in the stored corpus), lines without usable bounds appended
    /// in their original order so no recorded text is ever dropped.
    /// </summary>
    private static IReadOnlyList<RecognizedLine> InReportOrder(IReadOnlyList<RecognizedLine> lines)
    {
        IReadOnlyList<RecognizedLine> ordered = LicenseFieldExtractor.InReadingOrder(lines);
        if (ordered.Count == lines.Count) return ordered;
        return ordered.Concat(lines.Where(l => l.Bounds is not { } b || b.Width <= 0 || b.Height <= 0))
            .ToList();
    }

    private static PageReport ToPageReport(RecordedPage page, IReadOnlyList<RecognizedLine> lines) => new(
        page.Index,
        page.WidthPx,
        page.HeightPx,
        page.Orientation,
        page.Regions.Select(r => new RegionReport(
            Round(r.Bounds.X), Round(r.Bounds.Y), Round(r.Bounds.Width), Round(r.Bounds.Height),
            r.Kind, Round(r.Score))).ToList(),
        lines.Select(l => l.Bounds is { } b
            ? new LineReport(l.Text, Round(b.X), Round(b.Y), Round(b.Width), Round(b.Height))
            : new LineReport(l.Text, null, null, null, null)).ToList());

    private static FieldObservation RoundField(FieldObservation f) =>
        f with { LabelBox = Round(f.LabelBox), ValueBox = Round(f.ValueBox) };

    private static RectD? Round(RectD? box) => box is { } b
        ? new RectD(Round(b.X), Round(b.Y), Round(b.Width), Round(b.Height))
        : null;

    private static double Round(double value) => Math.Round(value, Precision);
}
