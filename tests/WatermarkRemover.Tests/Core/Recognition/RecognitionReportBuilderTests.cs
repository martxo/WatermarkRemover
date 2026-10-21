using System.Text.Json;
using System.Text.Json.Serialization;
using WatermarkRemover.Core.Features.Layout;
using WatermarkRemover.Core.Geometry;
using WatermarkRemover.Core.Recognition;
using WatermarkRemover.Core.Recognition.Review;

namespace WatermarkRemover.Tests.Core.Recognition;

/// <summary>
/// End-to-end report assembly from recorded pages: classification with scores, field
/// observations, name suggestion, review issues and the JSONL-friendly rounding rules.
/// </summary>
public class RecognitionReportBuilderTests
{
    /// <summary>One healthy business-licence page (real-shape OCR output).</summary>
    private static RecognitionRunScope RecordLicenseScope()
    {
        var scope = RecognitionRunScope.Begin(@"D:\docs\扫描全能王.pdf", "OCR 识别");
        scope.RecordPage(600, 800, "竖版", "整图直通", "微信OCR",
            Array.Empty<LayoutRegion>(),
            new[]
            {
                new RecognizedLine("营业执照", new RectD(0.10, 0.05, 0.30, 0.04)),
                new RecognizedLine("统一社会信用代码", new RectD(0.10, 0.10, 0.40, 0.04)),
                new RecognizedLine("经 营 者：林孝明", new RectD(0.10, 0.50, 0.40, 0.04)),
                new RecognizedLine("经营范围许可项目:道路货物运输", new RectD(0.10, 0.60, 0.80, 0.04)),
            });
        return scope;
    }

    /// <summary>The known failure shape: 「经营者」 split over two rows.</summary>
    private static RecognitionRunScope RecordSplitLabelScope()
    {
        var scope = RecognitionRunScope.Begin(@"D:\docs\分割标签.pdf", "OCR 识别");
        scope.RecordPage(600, 800, "竖版", "整图直通", "微信OCR",
            Array.Empty<LayoutRegion>(),
            new[]
            {
                new RecognizedLine("营业执照", new RectD(0.1, 0.05, 0.3, 0.04)),
                new RecognizedLine("统一社会信用代码", new RectD(0.1, 0.10, 0.4, 0.04)),
                new RecognizedLine("经", new RectD(0.1, 0.50, 0.05, 0.04)),
                new RecognizedLine("营者：林孝明", new RectD(0.16, 0.50, 0.3, 0.04)),
            });
        return scope;
    }

    [Fact]
    public void Build_produces_complete_report_for_one_license_page()
    {
        using RecognitionRunScope scope = RecordLicenseScope();

        RecognitionReport report = RecognitionReportBuilder.Build(scope);

        Assert.Equal("1", report.SchemaVersion);
        Assert.Equal(@"D:\docs\扫描全能王.pdf", report.InputPath);
        Assert.Equal("OCR 识别", report.FeatureName);
        Assert.Equal("微信OCR", report.EngineName);
        Assert.Equal("整图直通", report.LayoutStageName);
        Assert.Equal(1, report.PageCount);

        PageReport page = Assert.Single(report.Pages);
        Assert.Equal(600, page.WidthPx);
        Assert.Equal(800, page.HeightPx);
        Assert.Equal("竖版", page.Orientation);
        Assert.Equal(4, page.Lines.Count);

        ClassificationReport cls = Assert.IsType<ClassificationReport>(report.Classification);
        Assert.Equal("business-license", cls.TypeId);
        Assert.Equal("执照", cls.TypeShortName);
        Assert.Equal(12, cls.Score);
        // 统一社会信用代码 is a real enterprise-credit-report anchor too (weight 2), so the
        // license fixture lights it up as runner-up. Classification is margin-driven (12 vs 2)
        // — the runner-up is recorded for the training corpus, not a failure condition.
        Assert.Equal(2, cls.RunnerUp);

        FieldObservation field = report.Fields.Single(f => f.Label == "经营者");
        Assert.Equal("林孝明", field.Value);
        Assert.NotNull(field.LabelBox);
        Assert.NotNull(field.ValueBox);

        Assert.Equal("林孝明", report.PersonName);
        Assert.Equal("林孝明执照", report.SuggestedStem);
        Assert.Empty(report.Issues); // healthy page: classification, name and bounds all present
    }

    [Fact]
    public void Split_label_report_serializes_severity_as_string_and_round_trips()
    {
        using RecognitionRunScope scope = RecordSplitLabelScope();
        RecognitionReport report = RecognitionReportBuilder.Build(scope);
        Assert.Single(report.Issues); // exactly the anchor_split warning

        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        string json = JsonSerializer.Serialize(report, options);

        Assert.Contains("\"Warning\"", json);
        Assert.Contains("\"business-license\"", json);

        RecognitionReport? back = JsonSerializer.Deserialize<RecognitionReport>(json, options);
        Assert.NotNull(back);
        Assert.Equal("business-license", back!.Classification!.TypeId);
        Assert.Equal("林孝明", back.PersonName); // default encoder escapes CJK: assert after round trip
        Assert.Equal("林孝明执照", back.SuggestedStem);
        ReviewIssue issue = Assert.Single(back.Issues);
        Assert.Equal(RuleBasedReviewer.AnchorSplit, issue.RuleId);
        Assert.Equal(ReviewSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void Coordinates_are_rounded_to_four_decimal_places()
    {
        var pages = new[]
        {
            new RecordedPage(1, 100, 100, "横版", "整图直通", "微信OCR",
                new[]
                {
                    new LayoutRegion(
                        new RectD(0.123456789, 0.987654321, 0.512345678, 0.411111111),
                        "text", 0.87654321f),
                },
                new[]
                {
                    new RecognizedLine("任意文字",
                        new RectD(0.123456789, 0.987654321, 0.512345678, 0.411111111)),
                    new RecognizedLine("无坐标行", null),
                }),
        };

        RecognitionReport report = RecognitionReportBuilder.Build(@"D:\a.pdf", "OCR 识别", pages);

        PageReport page = Assert.Single(report.Pages);
        RegionReport region = Assert.Single(page.Regions);
        Assert.Equal(0.1235, region.X, 6);
        Assert.Equal(0.9877, region.Y, 6);
        Assert.Equal(0.5123, region.W, 6);
        Assert.Equal(0.4111, region.H, 6);
        Assert.Equal("text", region.Kind);
        Assert.Equal(0.8765, region.Score, 6);

        LineReport bounded = page.Lines[0];
        Assert.Equal(0.1235, bounded.X!.Value, 6);
        Assert.Equal(0.9877, bounded.Y!.Value, 6);
        Assert.Equal(0.5123, bounded.W!.Value, 6);
        Assert.Equal(0.4111, bounded.H!.Value, 6);

        // A line the engine positioned nowhere stays fully null instead of a fake zero box.
        LineReport unbounded = Assert.Single(page.Lines, l => l.Text == "无坐标行");
        Assert.Null(unbounded.X);
        Assert.Null(unbounded.Y);
        Assert.Null(unbounded.W);
        Assert.Null(unbounded.H);
    }

    [Fact]
    public void Engine_order_lines_are_reordered_so_split_anchor_still_reviewed()
    {
        // Real-machine shape (扫描全能王.pdf): same-row fragments arrive in detection order
        // (者/营/经 scrambled), so the reviewer's join only sees the label once the report has
        // reordered rows visually (top-down rows, left-to-right within a row).
        var scope = RecognitionRunScope.Begin(@"D:\docs\乱序.pdf", "OCR 识别");
        scope.RecordPage(2338, 1652, "横版", "营业执照模板定位", "微信OCR",
            Array.Empty<LayoutRegion>(),
            new[]
            {
                new RecognizedLine("者林孝明", new RectD(0.2185, 0.50, 0.06, 0.02)),
                new RecognizedLine("营业执照", new RectD(0.34, 0.28, 0.20, 0.10)),
                new RecognizedLine("营", new RectD(0.1843, 0.50, 0.02, 0.02)),
                new RecognizedLine("统一社会信用代码", new RectD(0.16, 0.28, 0.12, 0.02)),
                new RecognizedLine("经", new RectD(0.1569, 0.50, 0.02, 0.02)),
            });

        RecognitionReport report = RecognitionReportBuilder.Build(scope);

        Assert.Contains(report.Issues, i => i.RuleId == RuleBasedReviewer.AnchorSplit);

        PageReport page = Assert.Single(report.Pages);
        Assert.Equal(new[] { "统一社会信用代码", "营业执照", "经", "营", "者林孝明" },
            page.Lines.Select(l => l.Text));
    }

    [Fact]
    public void Build_with_no_pages_yields_an_empty_skeleton()
    {
        RecognitionReport report = RecognitionReportBuilder.Build(
            @"D:\a.pdf", "OCR 识别", Array.Empty<RecordedPage>());

        Assert.Equal(0, report.PageCount);
        Assert.Empty(report.Pages);
        Assert.Equal(string.Empty, report.EngineName);
        Assert.Equal(string.Empty, report.LayoutStageName);
        Assert.Null(report.Classification);
        Assert.Empty(report.Fields);
        Assert.Null(report.PersonName);
        Assert.Null(report.SuggestedStem);
        Assert.Empty(report.Issues); // no text recorded → nothing for the reviewer to say
    }
}
