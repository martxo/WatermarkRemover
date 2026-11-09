using WatermarkRemover.App.Updates;
using WatermarkRemover.Pdf;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// 更新前置检查：Update.exe 只会说“有进程占用”，不说谁。真机日志（2026-11-07）里连续两个月
/// 每天失败一次、每次都是备份 current 时 error 32，却没有任何文件名。这里的合同：找到占用
/// 文件的外部进程并报出文件名；本进程自己持有的文件不算——它们随退出释放，正是 Update.exe
/// 要等的那批。
/// </summary>
public class InstallLockProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "wm-install-" + Guid.NewGuid().ToString("N"));

    public InstallLockProbeTests() => Directory.CreateDirectory(Path.Combine(_root, "current"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void A_file_held_by_another_process_is_named_with_its_file_and_owner()
    {
        string file = Path.Combine(_root, "current", "held.dll");
        File.WriteAllText(file, "x");
        using var hold = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        // 测试里“别的进程”就是测试宿主自己：把忽略列表清空，就等于把它当成外部占用者。
        IReadOnlyList<InstallBlocker> blockers = InstallLockProbe.Find(
            _root, ignoreProcessIds: Array.Empty<int>());

        InstallBlocker blocker = Assert.Single(blockers);
        Assert.Equal(file, blocker.File);
        Assert.Equal(Environment.ProcessId, blocker.ProcessId);
        Assert.Contains("pid", InstallLockProbe.Describe(blockers, _root));
    }

    [Fact]
    public void The_apps_own_files_are_ignored_because_they_close_on_exit()
    {
        string file = Path.Combine(_root, "current", "WatermarkRemover.exe");
        File.WriteAllText(file, "x");
        using var hold = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.Empty(InstallLockProbe.Find(_root));
    }

    [Fact]
    public void An_unlocked_install_reports_nothing()
    {
        File.WriteAllText(Path.Combine(_root, "current", "free.dll"), "x");

        Assert.Empty(InstallLockProbe.Find(_root));
    }

    [Fact]
    public void A_missing_install_root_is_not_an_error()
    {
        Assert.Empty(InstallLockProbe.Find(Path.Combine(_root, "nope")));
        Assert.Empty(InstallLockProbe.Find(null));
    }

    [Fact]
    public void The_report_path_is_relative_to_the_install_root()
    {
        var blockers = new[]
        {
            new InstallBlocker(
                Path.Combine(_root, "current", "BrotherEngine", "bridge.exe"), 42, "bridge.exe"),
        };

        string text = InstallLockProbe.Describe(blockers, _root);

        Assert.Contains(Path.Combine("current", "BrotherEngine", "bridge.exe"), text);
        Assert.Contains("bridge.exe（pid 42）", text);
    }

    [Fact]
    public void Holder_ids_name_the_process_that_keeps_the_file_open()
    {
        string file = Path.Combine(_root, "current", "held.bin");
        File.WriteAllText(file, "x");
        using var hold = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.True(FileLockInspector.IsLockedForWrite(file));
        Assert.Contains(Environment.ProcessId, FileLockInspector.HolderProcessIds(file));
    }
}
