using WatermarkRemover.Core.Recognition;
using WatermarkRemover.Core.Recognition.Review;

namespace WatermarkRemover.Tests.Core.Recognition;

/// <summary>
/// Deterministic review rules over a finished report — most importantly anchor_split, the
/// failure shape where 「经营者」 gets spread across rows and name extraction breaks.
/// </summary>
public class RuleBasedReviewerTests
{
    private static readonly ClassificationReport License = new("business-license", "执照", 10, 0);

    private static RecognitionReport Report(
        ClassificationReport? classification = null,
        IReadOnlyList<LineReport>? lines = null,
        string? personName = null) => new()
    {
        Classification = classification,
        PersonName = personName,
        Pages = new[]
        {
            new PageReport(1, null, null, null, Array.Empty<RegionReport>(),
                lines ?? Array.Empty<LineReport>()),
        },
    };

    [Fact]
    public void Anchor_split_fires_once_with_fragment_evidence()
    {
        // 整体拼得出「经营者」，但没有任何单行包含它 —— 姓名提取会失败的那类版面。
        var report = Report(License, new[]
        {
            new LineReport("营业执照", 0.1, 0.1, 0.4, 0.05),
            new LineReport("经", 0.1, 0.2, 0.05, 0.05),
            new LineReport("营者：林孝明", 0.16, 0.2, 0.3, 0.05),
        }, personName: "林孝明");

        IReadOnlyList<ReviewIssue> issues = new RuleBasedReviewer().Review(report);

        ReviewIssue issue = Assert.Single(issues);
        Assert.Equal(RuleBasedReviewer.AnchorSplit, issue.RuleId);
        Assert.Equal(ReviewSeverity.Warning, issue.Severity);
        Assert.Contains("「经营者」", issue.Message);
        Assert.Contains("2 行", issue.Message);
        Assert.Equal("行2「经」[0.1,0.2]、行3「营者：林孝明」[0.16,0.2]", issue.Evidence);
    }

    [Fact]
    public void Missing_name_on_a_classified_report_is_an_error()
    {
        // 判成了执照却没有姓名 —— 自动命名会失败，必须以 Error 冒出来。
        var report = Report(new ClassificationReport("business-license", "执照", 10, 2), new[]
        {
            new LineReport("营业执照", 0.1, 0.1, 0.4, 0.05),
            new LineReport("统一社会信用代码", 0.1, 0.2, 0.5, 0.05),
        });

        ReviewIssue issue = Assert.Single(new RuleBasedReviewer().Review(report));

        Assert.Equal(RuleBasedReviewer.NameMissing, issue.RuleId);
        Assert.Equal(ReviewSeverity.Error, issue.Severity);
        Assert.Contains("执照", issue.Message);
        Assert.Equal("得分 10，次名 2", issue.Evidence);
    }

    [Fact]
    public void Low_margin_classification_is_flagged_as_info()
    {
        var report = Report(new ClassificationReport("business-license", "执照", 8, 6), new[]
        {
            new LineReport("营业执照", 0.1, 0.1, 0.4, 0.05),
        }, personName: "张三");

        ReviewIssue issue = Assert.Single(new RuleBasedReviewer().Review(report));

        Assert.Equal(RuleBasedReviewer.ClassifyLowMargin, issue.RuleId);
        Assert.Equal(ReviewSeverity.Info, issue.Severity);
        Assert.Equal("得分 8，次名 6", issue.Evidence);
    }

    [Fact]
    public void Unrecognised_text_is_flagged_as_unclassified_info()
    {
        var report = Report(classification: null, lines: new[]
        {
            new LineReport("随便一段没有特征的文字", 0.1, 0.1, 0.5, 0.05),
        });

        ReviewIssue issue = Assert.Single(new RuleBasedReviewer().Review(report));

        Assert.Equal(RuleBasedReviewer.Unclassified, issue.RuleId);
        Assert.Equal(ReviewSeverity.Info, issue.Severity);
        Assert.Contains("随便一段没有特征的文字", issue.Evidence);
    }

    [Fact]
    public void Text_without_coordinates_is_flagged_as_missing_bounds()
    {
        // 引擎没给坐标：字段细分区域数据整块缺失，报告要如实标注。
        var report = Report(License, new[]
        {
            new LineReport("营业执照", null, null, null, null),
            new LineReport("统一社会信用代码 91350200MA1234567X", null, null, null, null),
        }, personName: "林孝明");

        ReviewIssue issue = Assert.Single(new RuleBasedReviewer().Review(report));

        Assert.Equal(RuleBasedReviewer.NoBounds, issue.RuleId);
        Assert.Equal(ReviewSeverity.Info, issue.Severity);
        Assert.Contains("坐标", issue.Message);
    }

    [Fact]
    public void A_healthy_report_produces_no_issues()
    {
        var report = Report(new ClassificationReport("business-license", "执照", 12, 1), new[]
        {
            new LineReport("营业执照", 0.1, 0.1, 0.4, 0.05),
            new LineReport("经营者：林孝明", 0.1, 0.2, 0.4, 0.05),
        }, personName: "林孝明");

        Assert.Empty(new RuleBasedReviewer().Review(report));
    }
}
