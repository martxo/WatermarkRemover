using System.Text;
using WatermarkRemover.Core.Features.Naming;

namespace WatermarkRemover.Core.Recognition.Review;

/// <summary>
/// Deterministic review of a finished report: catches the failure shapes the pipeline is known
/// to produce — most importantly a field label split across lines ("经"/"营"/"者" each on its
/// own row), which is exactly what breaks name extraction on business licences.
/// </summary>
public sealed class RuleBasedReviewer : IRecognitionReportReviewer
{
    public const string AnchorSplit = "anchor_split";
    public const string NameMissing = "name_missing";
    public const string ClassifyLowMargin = "classify_low_margin";
    public const string Unclassified = "unclassified";
    public const string NoBounds = "no_bounds";

    public IReadOnlyList<ReviewIssue> Review(RecognitionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var issues = new List<ReviewIssue>();
        List<LineReport> lines = report.Pages.SelectMany(p => p.Lines).ToList();
        bool hasText = lines.Any(l => !string.IsNullOrWhiteSpace(l.Text));
        bool hasBounds = lines.Any(l => l.X.HasValue);

        if (report.Classification is { } split) ReviewAnchorSplit(report, lines, issues);

        if (report.Classification is { } cls)
        {
            if (report.PersonName is null)
            {
                issues.Add(new ReviewIssue(NameMissing, ReviewSeverity.Error,
                    $"已判定为「{cls.TypeShortName}」但未能提取到姓名",
                    $"得分 {cls.Score}，次名 {cls.RunnerUp}"));
            }

            if (cls.Score - cls.RunnerUp < 3)
            {
                issues.Add(new ReviewIssue(ClassifyLowMargin, ReviewSeverity.Info,
                    $"分类「{cls.TypeShortName}」与次名得分差距小于 3，类型判定可能不稳",
                    $"得分 {cls.Score}，次名 {cls.RunnerUp}"));
            }
        }
        else if (hasText)
        {
            issues.Add(new ReviewIssue(Unclassified, ReviewSeverity.Info,
                "识别到文字但未能判定文档类型（绝对分或与次名的区分度不足）",
                Preview(lines)));
        }

        if (hasText && !hasBounds)
        {
            issues.Add(new ReviewIssue(NoBounds, ReviewSeverity.Info,
                "识别引擎未返回坐标，字段细分区域数据缺失（Fields 为空）", Preview(lines)));
        }

        return issues;
    }

    /// <summary>
    /// anchor_split: the label is present in the whitespace-stripped text as a whole, yet no
    /// single line contains it — i.e. the label glyphs are spread over several rows.
    /// Evidence names every fragment line with its coordinates.
    /// </summary>
    private static void ReviewAnchorSplit(RecognitionReport report, List<LineReport> lines, List<ReviewIssue> issues)
    {
        DocumentProfile? profile = DocumentProfileCatalog.All
            .FirstOrDefault(p => p.TypeId == report.Classification!.TypeId);
        if (profile is null || lines.Count == 0) return;

        var stripped = lines.Select(l => RemoveWhitespace(l.Text)).ToArray();
        var sb = new StringBuilder();
        var lineOf = new List<int>(stripped.Sum(s => s.Length));
        for (int i = 0; i < stripped.Length; i++)
        {
            for (int j = 0; j < stripped[i].Length; j++)
            {
                sb.Append(stripped[i][j]);
                lineOf.Add(i);
            }
        }
        string joined = sb.ToString();

        foreach (string label in profile.NameLabels)
        {
            if (Array.Exists(stripped, s => s.Contains(label, StringComparison.Ordinal))) continue;

            int at = joined.IndexOf(label, StringComparison.Ordinal);
            if (at < 0) continue;

            var fragments = new SortedSet<int>();
            for (int k = at; k < at + label.Length; k++) fragments.Add(lineOf[k]);

            string evidence = string.Join("、",
                fragments.Select(idx => $"行{idx + 1}「{lines[idx].Text}」{Coords(lines[idx])}"));
            issues.Add(new ReviewIssue(AnchorSplit, ReviewSeverity.Warning,
                $"标签「{label}」被分割到 {fragments.Count} 行（整体可拼出但无单行匹配），姓名提取可能失败",
                evidence));
        }
    }

    private static string RemoveWhitespace(string text) =>
        new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static string Coords(LineReport line) =>
        line.X is { } x && line.Y is { } y ? $"[{x:0.####},{y:0.####}]" : "[无坐标]";

    private static string Preview(IReadOnlyList<LineReport> lines)
    {
        var first = lines.Where(l => !string.IsNullOrWhiteSpace(l.Text)).Take(2)
            .Select(l => l.Text.Length > 40 ? l.Text[..40] + "…" : l.Text);
        return string.Join(" / ", first);
    }
}
