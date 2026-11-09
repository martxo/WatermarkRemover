using WatermarkRemover.App.Updates;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// Update.exe 的日志是"包下好了却没换上去"的唯一证据来源。解析必须只取最后一次运行：机器上
/// 每次更新都会追加一段，早先成功会话里的 [WARN] 不能冒充这次失败的原因。
/// </summary>
public sealed class VelopackUpdaterLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wm-updater-log-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public VelopackUpdaterLogTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "velopack_WatermarkRemover.log");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public void Only_the_last_update_exe_session_is_returned()
    {
        File.WriteAllLines(_path, new[]
        {
            "[update:111] [10:00:00] [INFO] Applying package 1.16.120 to current: 1.16.119",
            "[update:111] [10:00:05] [ERROR] old failure from a previous run",
            "[update:222] [11:00:00] [INFO] Applying package 1.16.126 to current: 1.16.125",
            "[lib-csharp:333] [11:00:01] [Information] Completed hook, exiting...",
            "[update:222] [11:00:02] [WARN] Failed to replace current dir (file is locked)",
        });

        var session = VelopackUpdaterLog.LastSession(_path);

        Assert.Equal(3, session.Count);
        Assert.StartsWith("[update:222]", session[0]);
        Assert.Contains(session, line => line.Contains("Completed hook"));
        Assert.Contains(session, line => line.Contains("file is locked"));
    }

    [Fact]
    public void The_last_error_of_that_session_wins()
    {
        File.WriteAllLines(_path, new[]
        {
            "[update:222] [11:00:00] [WARN] first problem",
            "[update:222] [11:00:01] [ERROR] real reason: access denied",
            "[update:222] [11:00:02] [INFO] giving up",
        });

        Assert.Contains("access denied", VelopackUpdaterLog.LastFailure(_path));
    }

    [Fact]
    public void A_session_without_errors_falls_back_to_the_last_warning()
    {
        File.WriteAllLines(_path, new[]
        {
            "[update:222] [11:00:00] [WARN] waiting for process to exit",
            "[update:222] [11:00:02] [INFO] continuing",
        });

        Assert.Contains("waiting for process", VelopackUpdaterLog.LastFailure(_path));
    }

    [Fact]
    public void An_unreadable_log_reports_no_failure_instead_of_throwing()
    {
        Assert.Empty(VelopackUpdaterLog.LastSession(Path.Combine(_dir, "missing.log")));
        Assert.Null(VelopackUpdaterLog.LastFailure(Path.Combine(_dir, "missing.log")));
        Assert.Contains("没有失败记录", VelopackUpdaterLog.Describe(Path.Combine(_dir, "missing.log")));
        Assert.Contains("没有更新程序的日志", VelopackUpdaterLog.Describe(null));
    }

    [Fact]
    public void The_explanation_carries_the_verbatim_reason_and_the_log_path()
    {
        File.WriteAllLines(_path, new[]
        {
            "[update:222] [11:00:00] [ERROR] directory is not writable",
        });

        string text = VelopackUpdaterLog.Describe(_path);

        Assert.Contains("directory is not writable", text);
        Assert.Contains(_path, text);
    }
}
