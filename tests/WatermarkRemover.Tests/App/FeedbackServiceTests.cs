using System.IO;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// 随手反馈的记录拼装与日志切片。日志切片按 [op=操作号] 精确切出该次操作的全部
/// 日志行——这是「用户指出哪条记录出的问题就能找到当时日志」的保证。测试注入临时
/// 日志目录，不碰真实日志、也不受并行测试写入干扰。
/// </summary>
public class FeedbackServiceTests
{
    [Fact]
    public void SliceLogsKeepsOnlyLinesOfTheGivenOperation()
    {
        string directory = WriteTempLog(new[]
        {
            "[INFO][APP][op=111111] 主线程 操作开始[APP]：识别",
            "[INFO][APP][op=222222] 主线程 操作开始[APP]：另一次识别",
            "[DEBUG][APP][op=111111] 主线程 操作步骤[APP]：版面分析 12 ms",
            "[INFO][APP][op=111111] 主线程 操作完成[APP]：识别 480 ms",
        });

        try
        {
            List<string> slice = FeedbackService.SliceLogs("111111", directory);

            Assert.Equal(3, slice.Count);
            Assert.All(slice, line => Assert.Contains("[op=111111]", line));
            Assert.DoesNotContain(slice, line => line.Contains("[op=222222]"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SliceLogsWithoutOperationIdFallsBackToTail()
    {
        // 全局入口（不关联具体操作）时退化到最近 30 行：写 3 行就全量返回。
        string directory = WriteTempLog(new[] { "第一行", "第二行", "第三行" });

        try
        {
            List<string> slice = FeedbackService.SliceLogs(null, directory);

            Assert.Equal(3, slice.Count);
            Assert.Contains("第三行", slice);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BuildRecordCarriesContextWithoutDocumentContent()
    {
        var report = new WatermarkRemover.Core.Recognition.RecognitionReport
        {
            InputPath = @"D:\docs\合同.pdf",
            OperationId = "483917",
        };

        FeedbackRecord record = FeedbackService.BuildRecord(
            "fb-20261114-101500-ab12", "483917", "reports", "  扫描件方向识别错了  ",
            "  微信:someone  ", report, new List<string> { "[op=483917] 操作完成" });

        Assert.Equal("fb-20261114-101500-ab12", record.Id);
        Assert.Equal("483917", record.OperationId);
        Assert.Equal("reports", record.Page);
        // BuildRecord 只拼装不加工：描述原样入账，联系方式按隐私惯例 trim。
        Assert.Equal("  扫描件方向识别错了  ", record.UserText);
        Assert.Equal("微信:someone", record.Contact);
        Assert.Equal("483917", record.Report!.OperationId);
        Assert.Equal(new[] { "[op=483917] 操作完成" }, record.LogSlice);
        Assert.False(string.IsNullOrWhiteSpace(record.AppVersion));
        Assert.False(string.IsNullOrWhiteSpace(record.Os));
        Assert.Equal(Environment.MachineName, record.Machine);
    }

    /// <summary>建一个只含一个 app-*.log 的临时目录，返回目录路径（调用方负责删除）。</summary>
    private static string WriteTempLog(string[] lines)
    {
        string directory = Path.Combine(
            Path.GetTempPath(), $"wm-feedback-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, "app-20261114.log"), lines);
        return directory;
    }
}
