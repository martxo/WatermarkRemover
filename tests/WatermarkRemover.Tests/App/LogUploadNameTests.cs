using WatermarkRemover.App.Logging;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// 设置 → 日志 → 上传到云盘：文件名要带上机器名和时间，多台电脑的日志才会并排落在同一个
/// 文件夹里、两次上传也不会互相覆盖。机器名里可能有空格、中文或斜杠，直接拼进去会变成
/// 非法路径或云端目录结构。
/// </summary>
public class LogUploadNameTests
{
    [Fact]
    public void Machine_name_is_kept_so_uploads_from_several_pcs_stay_apart()
    {
        string name = LogExportService.UploadFileName("DESKTOP-7K2L9Q", new DateTime(2026, 11, 7, 9, 5, 3));

        Assert.Equal("log-DESKTOP-7K2L9Q-20261107-090503.log", name);
    }

    [Fact]
    public void Characters_that_cannot_live_in_a_file_name_are_replaced()
    {
        // 空格 / 反斜杠 / 中文标点都不允许出现在文件名里，替换成 '-' 且不留连续的首尾横线。
        string name = LogExportService.UploadFileName(" 办公 PC \\ 01 ", new DateTime(2026, 11, 7, 9, 5, 3));

        Assert.DoesNotContain(' ', name);
        Assert.DoesNotContain('\\', name);
        Assert.StartsWith("log-", name);
        Assert.EndsWith("-20261107-090503.log", name);
    }

    [Fact]
    public void An_empty_machine_name_still_yields_a_usable_name()
    {
        string name = LogExportService.UploadFileName("   ", new DateTime(2026, 11, 7, 9, 5, 3));

        Assert.Equal("log-pc-20261107-090503.log", name);
    }
}
