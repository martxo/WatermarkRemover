using PdfSharp.Drawing;
using PdfSharp.Pdf;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features.Scan;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// 扫描页的第二种输入：已有的 PDF 拖进来不经过任何"处理"，直接成为一条可编辑记录
/// （缩略图里旋转 / 删除 / 重排）。这类记录必须关掉扫描专用的自动摆正与删空白页，
/// 否则用户只是想把文件拖进来编辑，原文档却被悄悄改写了。
/// </summary>
public class ScanDropTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "wm-scan-drop-" + Guid.NewGuid().ToString("N"));

    public ScanDropTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private sealed class FakeDialogs : IDialogService
    {
        public bool Confirm(string message, string title) => true;
        public bool ConfirmYesNo(string message, string title) => true;
        public void Info(string message, string title)
        {
        }

        public void Warn(string message, string title)
        {
        }

        public void Error(string message, string title)
        {
        }
    }

    private sealed class FakeShell : IShellOpenService
    {
        public bool TryOpenDirectory(string directory, out string? error)
        {
            error = null;
            return true;
        }
    }

    private static MainViewModel ScanPage()
    {
        var vm = new MainViewModel(new FakeDialogs(), new FakeShell());
        vm.SelectedFeature = new ScanFeature();
        return vm;
    }

    private string WritePdf(string name)
    {
        string path = Path.Combine(_dir, name);
        using (var document = new PdfDocument())
        {
            PdfPage page = document.AddPage();
            page.Width = XUnit.FromPoint(300);
            page.Height = XUnit.FromPoint(400);
            document.Save(path);
        }
        return path;
    }

    [Fact]
    public void The_scan_page_accepts_pdfs_and_rejects_other_files()
    {
        var feature = new ScanFeature();

        Assert.True(feature.CanHandle(@"C:\t\doc.pdf"));
        Assert.True(feature.CanHandle(@"C:\t\doc.PDF"));
        Assert.False(feature.CanHandle(@"C:\t\photo.png"));
        Assert.Equal(new[] { ".pdf" }, feature.InputExtensions);
    }

    [Fact]
    public async Task A_dropped_pdf_becomes_a_finished_editable_record()
    {
        MainViewModel vm = ScanPage();
        string pdf = WritePdf("existing.pdf");

        await vm.AddFilesAsync(new[] { pdf });

        FileJobViewModel job = Assert.Single(
            vm.Board.Jobs.Where(j => j.OwnerFeatureId == "scan.page"));
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(pdf, job.OutputPath); // 编辑的就是这份文件本身
        Assert.Equal(pdf, job.InputPath);
        Assert.True(job.SuppressAutoPostProcess, "扫描专用自动通道必须关掉");
    }

    [Fact]
    public async Task A_folder_drop_picks_the_pdfs_inside_it()
    {
        MainViewModel vm = ScanPage();
        WritePdf("a.pdf");
        WritePdf("b.pdf");
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "ignored");

        await vm.AddFilesAsync(new[] { _dir });

        Assert.Equal(2, vm.Board.Jobs.Count(j => j.OwnerFeatureId == "scan.page"));
    }

    [Fact]
    public async Task A_non_pdf_drop_is_refused_without_a_record()
    {
        MainViewModel vm = ScanPage();
        string text = Path.Combine(_dir, "note.txt");
        File.WriteAllText(text, "x");

        await vm.AddFilesAsync(new[] { text });

        Assert.Empty(vm.Board.Jobs.Where(j => j.OwnerFeatureId == "scan.page"));
    }
}
