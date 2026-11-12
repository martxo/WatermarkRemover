using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// 1.16.133 用户反馈三项：①扫描时底部「已完成 0/19」被当成卡死的页数；②后台识别命名期间
/// 没有任何提示；③资源管理器里改名/删除后记录行变成死路径。这里把三处行为钉死。
/// </summary>
public class ScanFeedbackTests
{
    private sealed class FakeDialogs : IDialogService
    {
        public bool Confirm(string message, string title) => true;
        public bool ConfirmYesNo(string message, string title) => true;
        public void Info(string message, string title) { }
        public void Warn(string message, string title) { }
        public void Error(string message, string title) { }
    }

    [Fact]
    public void While_a_scan_runs_the_footer_points_at_the_record_row()
    {
        // 扫描是"一条记录扫到底才完成"的单任务，批次计数器整个扫描期间纹丝不动，
        // 读起来像卡死的页数。扫描进行中必须换成明确的指向语。
        var board = new JobBoard(new FakeDialogs())
        {
            IsBusy = true,
            ScanInProgress = true,
        };
        board.SetSection("scan.page", "扫描");
        board.Add(new FileJobViewModel(@"C:\t\扫描20261111-093002.pdf", Array.Empty<string>())
        {
            OwnerFeatureId = "scan.page",
        });

        Assert.Equal("正在扫描…（进度见记录行）", board.StatusText);

        board.ScanInProgress = false;
        Assert.Equal("处理中… 已完成 0/1", board.StatusText);
    }

    [Fact]
    public void An_external_rename_follows_through_to_paths_and_card()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wr-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string original = Path.Combine(dir, "扫描20261111-093002.pdf");
            File.WriteAllText(original, "x");
            var job = new FileJobViewModel(original, Array.Empty<string>())
            {
                OwnerFeatureId = "scan.page",
            };
            job.Apply(FeatureResult.Success(original, original, "扫描 7 页", 7));

            string renamed = Path.Combine(dir, "苏玉欣.pdf");
            File.WriteAllText(renamed, "x"); // 模拟外部改名后磁盘上的真实文件
            job.ApplyExternalRename(renamed, renameInput: true, renameOutput: true);

            Assert.Equal(renamed, job.InputPath);
            Assert.Equal(renamed, job.OutputPath);
            Assert.Equal("苏玉欣.pdf", job.DisplayName);
            Assert.True(File.Exists(job.OutputPath));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Watcher_reports_rename_delete_and_create_from_explorer_style_operations()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wr-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string first = Path.Combine(dir, "a.pdf");
        string second = Path.Combine(dir, "b.pdf");
        string fresh = Path.Combine(dir, "c.txt");
        File.WriteAllText(first, "x");

        using var watcher = new OutputFileWatcher(_ => { });
        var renamed = new TaskCompletionSource<(string Old, string New)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deleted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var created = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.FileRenamed += (o, n) => renamed.TrySetResult((o, n));
        watcher.FileDeleted += p => deleted.TrySetResult(p);
        watcher.FileCreated += p => created.TrySetResult(p);
        watcher.SetDirectories(new[] { dir });

        File.Move(first, second);
        var rename = await renamed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(first, rename.Old);
        Assert.Equal(second, rename.New);

        // 全新文件（不是 Move 的落点，也不是既有文件的覆写）才触发 Created。
        File.WriteAllText(fresh, "y");
        Assert.Equal(fresh, await created.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        File.Delete(second);
        Assert.Equal(second, await deleted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task An_atomic_replace_write_does_not_look_like_a_deletion()
    {
        // 预览条保存 = File.Move(temp, 目标, overwrite) → FSW 发 Deleted(目标) + Renamed(临时→目标)。
        // 同窗口内目标"被删又回来"是内容替换，不能给行挂「已被移走或删除」误报（1.16.137：
        // 每次保存都误标，彭红联/赵丽娜等扫描记录全部中招）。
        string dir = Path.Combine(Path.GetTempPath(), "wr-replace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string target = Path.Combine(dir, "彭红联.pdf");
        string temp = Path.Combine(dir, "temp.pdf");
        File.WriteAllText(target, "old");

        using var watcher = new OutputFileWatcher(_ => { });
        var deletedFired = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renamedFired = new TaskCompletionSource<(string Old, string New)>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.FileDeleted += p => deletedFired.TrySetResult(p);
        watcher.FileRenamed += (o, n) => renamedFired.TrySetResult((o, n));
        watcher.SetDirectories(new[] { dir });

        // 模拟应用的原子替换：删除目标 + 把同目录临时文件改名到目标（同一 400ms 窗口内）。
        File.Delete(target);
        File.WriteAllText(temp, "new");
        File.Move(temp, target);

        var rename = await renamedFired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(target, rename.New);

        // 给足去抖窗口 + 富余时间：删除事件必须被对消掉。
        await Task.Delay(1500);
        Assert.False(deletedFired.Task.IsCompleted, "替换写入被误报为删除");

        Assert.True(File.Exists(target));
    }

    [Fact]
    public void Watcher_ignores_directories_that_do_not_exist()
    {
        using var watcher = new OutputFileWatcher(_ => { });
        watcher.SetDirectories(new[] { Path.Combine(Path.GetTempPath(), "wr-missing-" + Guid.NewGuid().ToString("N")) });
        // 不存在目录被跳过且不抛出；随后给一个真实目录能正常接管。
        string dir = Path.Combine(Path.GetTempPath(), "wr-watch-ok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            watcher.SetDirectories(new[] { dir });
            string file = Path.Combine(dir, "a.txt");
            File.WriteAllText(file, "x");
            File.Delete(file); // 无异常即通过（真实事件解绑与否不在此断言）
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
