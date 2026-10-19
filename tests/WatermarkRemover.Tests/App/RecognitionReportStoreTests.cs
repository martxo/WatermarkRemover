using WatermarkRemover.App.Recognition;
using WatermarkRemover.Core.Recognition;
using WatermarkRemover.Core.Recognition.Review;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// JSONL report persistence: one report per line, newest file first, 90-day retention and
/// never-throws degradation (a failing store must not take a job down).
/// </summary>
public class RecognitionReportStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "wmr-report-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Temp cleanup is best effort.
        }
    }

    private static RecognitionReport MakeReport(DateTimeOffset timestamp, string input = @"D:\doc\扫描件.pdf") => new()
    {
        Timestamp = timestamp,
        InputPath = input,
        FeatureName = "OCR 识别",
        EngineName = "微信OCR",
        LayoutStageName = "整图直通",
        PageCount = 1,
        Classification = new ClassificationReport("business-license", "执照", 12, 0),
        PersonName = "林孝明",
        SuggestedStem = "林孝明执照",
        Issues = new[]
        {
            new ReviewIssue(RuleBasedReviewer.AnchorSplit, ReviewSeverity.Warning, "标签被分割", "行2「经」"),
        },
    };

    [Fact]
    public void Append_writes_one_jsonl_line_and_reads_it_back()
    {
        var store = new RecognitionReportStore(_dir);

        string path = store.Append(MakeReport(new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.FromHours(8))));

        Assert.StartsWith(Path.Combine(_dir, "recognition-20261018"), path);
        Assert.True(File.Exists(path));
        string line = Assert.Single(File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)));
        Assert.Contains("\"Warning\"", line); // enum as readable string, not a number

        RecognitionReport back = Assert.Single(store.ReadRecent(10));
        Assert.Equal("business-license", back.Classification!.TypeId);
        Assert.Equal("林孝明", back.PersonName);
        Assert.Equal("林孝明执照", back.SuggestedStem);
        Assert.Equal(1, back.PageCount);
        Assert.Equal(ReviewSeverity.Warning, Assert.Single(back.Issues).Severity);
    }

    [Fact]
    public void Read_recent_returns_newest_file_first()
    {
        var store = new RecognitionReportStore(_dir);
        store.Append(MakeReport(new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero), @"D:\older.pdf"));
        store.Append(MakeReport(new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero), @"D:\newer.pdf"));

        IReadOnlyList<RecognitionReport> reports = store.ReadRecent(10);

        Assert.Equal(2, reports.Count);
        Assert.Equal(@"D:\newer.pdf", reports[0].InputPath);
        Assert.Equal(@"D:\older.pdf", reports[1].InputPath);
    }

    [Fact]
    public void Append_purges_files_past_retention_but_keeps_recent_ones()
    {
        Directory.CreateDirectory(_dir);
        string oldFile = Path.Combine(_dir,
            $"recognition-{DateTime.Today.AddDays(-(RecognitionReportStore.RetentionDays + 1)):yyyyMMdd}.jsonl");
        string recentFile = Path.Combine(_dir,
            $"recognition-{DateTime.Today.AddDays(-1):yyyyMMdd}.jsonl");
        File.WriteAllText(oldFile, "{}" + Environment.NewLine);
        File.WriteAllText(recentFile, "{}" + Environment.NewLine);

        var store = new RecognitionReportStore(_dir);
        store.Append(MakeReport(DateTimeOffset.Now));

        Assert.False(File.Exists(oldFile));  // 91 天前：超出 90 天保留期
        Assert.True(File.Exists(recentFile)); // 昨天：仍在窗口内
    }

    [Fact]
    public void Append_swallows_io_failure_and_returns_empty()
    {
        // A file sitting exactly where the directory should be — Directory.CreateDirectory throws.
        string filePath = Path.Combine(Path.GetTempPath(), "wmr-report-notadir-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(filePath, "x");
        try
        {
            var store = new RecognitionReportStore(filePath);

            Assert.Equal(string.Empty, store.Append(MakeReport(DateTimeOffset.Now)));
            Assert.Empty(store.ReadRecent(10));
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public void Read_recent_on_missing_directory_returns_empty()
    {
        var store = new RecognitionReportStore(Path.Combine(_dir, "never-created"));

        Assert.Empty(store.ReadRecent(10));
        Assert.Empty(store.ReadRecent(0));
    }
}
