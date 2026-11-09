using System.Diagnostics;
using System.IO;
using WatermarkRemover.Pdf;

namespace WatermarkRemover.App.Updates;

/// <summary>One external process holding a file that the update has to replace.</summary>
public sealed record InstallBlocker(string File, int ProcessId, string ProcessName);

/// <summary>
/// Raised before Update.exe runs when another program holds the install files. Carries the
/// file→program list so the dialog can name what to close instead of "try restarting".
/// </summary>
public sealed class InstallLockedException(string detail) : Exception(
    "更新前检查发现安装目录里的文件正被其他程序占用：\n" + detail +
    "\n\n请关闭上面列出的程序（或重启电脑）后再试。")
{
}

/// <summary>
/// 更新前置检查。Update.exe 备份 <c>current</c> 失败时只会说“有进程占用”，日志里连文件名都
/// 没有（真机日志：1.16.24→1.16.26 起连续多次失败，每次都是 10 条 error 32）。这里在交给
/// Update.exe 之前逐个探测安装目录里的文件，用 Restart Manager 找出真正的占用者，把
/// “哪个文件被哪个程序占着”写进日志和弹窗——否则用户只能猜，开发者只能要日志。
/// 本进程自己持有的文件会被忽略：它们随本进程退出自然释放，正是 Update.exe 等待的那批。
/// </summary>
public static class InstallLockProbe
{
    /// <summary>Report cap: a wall of file names helps nobody; the first few name the culprit.</summary>
    private const int MaxReportedFiles = 5;

    /// <summary>Guard against walking something absurd (a junction loop, a wrong root).</summary>
    private const int MaxScannedFiles = 4000;

    /// <summary>
    /// Files under <c>&lt;root&gt;\current</c> that cannot be opened for writing and are held by a
    /// process other than <paramref name="ignoreProcessIds"/> (default: this process).
    /// Never throws: a probe that breaks must not break the update itself.
    /// </summary>
    public static IReadOnlyList<InstallBlocker> Find(
        string? installRoot, IReadOnlyCollection<int>? ignoreProcessIds = null)
    {
        var blockers = new List<InstallBlocker>();
        if (string.IsNullOrEmpty(installRoot)) return blockers;

        string current = Path.Combine(installRoot, "current");
        if (!Directory.Exists(current)) return blockers;

        HashSet<int> ignore = ignoreProcessIds is null
            ? new HashSet<int> { Environment.ProcessId }
            : new HashSet<int>(ignoreProcessIds);

        int scanned = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(current, "*", SearchOption.AllDirectories))
            {
                if (++scanned > MaxScannedFiles) break;
                if (!FileLockInspector.IsLockedForWrite(file)) continue;

                foreach (int pid in FileLockInspector.HolderProcessIds(file))
                {
                    if (ignore.Contains(pid)) continue;
                    blockers.Add(new InstallBlocker(file, pid, DescribeProcess(pid)));
                    if (blockers.Count >= MaxReportedFiles) return blockers;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录读到一半被打断：已经发现的占用仍然有效。
        }

        return blockers;
    }

    /// <summary>Multi-line list for the dialog/app log, e.g. "current\\app.exe ← xxx.exe（pid 123）".</summary>
    public static string Describe(IReadOnlyList<InstallBlocker> blockers, string? installRoot)
    {
        var lines = new List<string>(blockers.Count);
        foreach (InstallBlocker blocker in blockers)
        {
            string file = installRoot is { Length: > 0 }
                ? Path.GetRelativePath(installRoot, blocker.File)
                : blocker.File;
            lines.Add($"{file} ← {blocker.ProcessName}（pid {blocker.ProcessId}）");
        }

        return string.Join("\n", lines);
    }

    private static string DescribeProcess(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return $"{process.ProcessName}.exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "未知程序";
        }
    }
}
