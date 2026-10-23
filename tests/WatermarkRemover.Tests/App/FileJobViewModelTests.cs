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
}
