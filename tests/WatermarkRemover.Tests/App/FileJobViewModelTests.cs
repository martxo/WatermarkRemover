using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// A scan starts as a pathless placeholder row and adopts the real PDF once the scan finishes.
/// The file name must follow that adoption so later prompts and diagnostics use the real file.
/// </summary>
public class FileJobViewModelTests
{
    [Fact]
    public void Adopting_a_source_updates_the_file_name_and_the_terminal_result()
    {
        var job = new FileJobViewModel("扫描中", Array.Empty<string>())
        {
            OwnerFeatureId = "scan.page",
        };
        job.MarkProcessing("正在准备扫描…");

        job.AdoptSource(@"C:\t\scan-1.pdf");
        job.Apply(FeatureResult.Success(@"C:\t\scan-1.pdf", @"C:\t\scan-1.pdf", "扫描 2 页", 2));

        Assert.Equal(@"C:\t\scan-1.pdf", job.InputPath);
        Assert.Equal("scan-1.pdf", job.FileName);
        Assert.Equal("scan-1.pdf", job.DisplayName);
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(2, job.ItemCount);
    }

    [Fact]
    public void Status_line_prefers_a_failure_then_the_naming_note_then_the_detail()
    {
        var job = new FileJobViewModel(@"C:\t\a.pdf", Array.Empty<string>());

        job.MarkProcessing("正在识别水印…");
        Assert.Equal("正在识别水印…", job.StatusLine);

        job.SetNamingNote("识别命名中…");
        Assert.Equal("识别命名中…", job.StatusLine);

        job.Apply(FeatureResult.Fail(@"C:\t\a.pdf", "处理失败"));
        Assert.Equal("处理失败", job.StatusLine); // failure outranks a lingering note

        job.SetNamingNote(null);
        Assert.Equal("处理失败", job.StatusLine);
    }

    [Fact]
    public void A_numbered_rename_note_takes_the_same_line()
    {
        var job = new FileJobViewModel(@"C:\t\a.pdf", Array.Empty<string>());
        job.Apply(FeatureResult.Success(@"C:\t\a.pdf", @"C:\t\a_处理完成.pdf", "已处理", 1));

        job.ShowTransientNameNote("同名已存在，已命名为 a_处理完成 (2).pdf");

        Assert.StartsWith("同名已存在", job.StatusLine);
        Assert.Equal("同名已存在，已命名为 a_处理完成 (2).pdf", job.NamingNote);
    }
}
