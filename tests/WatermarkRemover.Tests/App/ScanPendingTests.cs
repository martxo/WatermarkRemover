using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// 扫描中断时那份半成品 PDF 会记进未完成任务里，所以关掉应用再打开，「继续扫描」仍然在。
/// 这里验证重启后的承接方式：扫描文档恢复成它自己的记录行，而不是挤进横幅的「继续处理」。
/// </summary>
public sealed class ScanPendingTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "wm-scan-pending-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
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

    private TaskJournal NewJournal() => new(Path.Combine(_dir, "journal"));

    private PendingTaskJournal NewPending() => new(Path.Combine(_dir, "journal"));

    private string NewPdf(string name)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[] { 1 });
        return path;
    }

    private PendingTaskEntry ScanEntry(string path) =>
        new("scan.page", path, DateTime.Now) { Kind = "scan" };

    private MainViewModel Restart() =>
        new(new FakeDialogs(), new FakeShell(), NewJournal(), pendingJournal: NewPending());

    [Fact]
    public void A_restart_restores_the_interrupted_scan_row_with_continue_scan()
    {
        string pdf = NewPdf("扫描件-2026.pdf");
        NewJournal().Save(new[]
        {
            new TaskJournalEntry(
                "scan.page", pdf, pdf, "扫描中断，已保存 3 页", 3, DateTime.Now, "None", null),
        });
        NewPending().Save(new[] { ScanEntry(pdf) });

        var vm = Restart();

        // 扫描不是「重新处理」：横幅不为它报数，承接入口就在那行记录上。
        Assert.False(vm.HasPendingTasks);
        var job = Assert.Single(vm.Board.Jobs);
        Assert.True(job.ShowContinueScan);
        Assert.Contains("继续扫描", job.StatusLine);
    }

    [Fact]
    public void A_scan_record_whose_history_row_is_gone_still_comes_back_as_a_row()
    {
        string pdf = NewPdf("半份.pdf");
        NewPending().Save(new[] { ScanEntry(pdf) });

        var vm = Restart();

        var job = Assert.Single(vm.Board.Jobs);
        Assert.Equal(pdf, job.OutputPath);
        Assert.True(job.ShowContinueScan);
    }

    [Fact]
    public void A_scan_record_for_a_deleted_file_is_dropped_on_restart()
    {
        string gone = Path.Combine(_dir, "gone.pdf");
        NewPending().Save(new[] { ScanEntry(gone) });

        var vm = Restart();

        Assert.Empty(vm.Board.Jobs);
        Assert.Empty(NewPending().Load());
    }

    [Fact]
    public void A_scan_record_does_not_hide_the_other_unfinished_files_in_the_banner()
    {
        string scanned = NewPdf("扫描.pdf");
        string waiting = NewPdf("待处理.pdf");
        NewPending().Save(new[]
        {
            ScanEntry(scanned),
            new PendingTaskEntry("watermark.badge", waiting, DateTime.Now),
        });

        var vm = Restart();

        Assert.True(vm.HasPendingTasks);
        Assert.Equal(1, vm.PendingTaskCount); // 只算那个要重新处理的文件
        Assert.Contains("待处理.pdf", vm.PendingTasksText);
    }
}
