using System.Diagnostics;
using System.IO;

namespace WatermarkRemover.App.Updates;

/// <summary>Swaps the running executable for a downloaded update and restarts the app.</summary>
public static class UpdateApplier
{
    /// <summary>
    /// Schedules the replacement of the running exe, to take effect once this process exits, then
    /// relaunches the app. A running single-file executable cannot overwrite itself, so a detached
    /// cmd script waits for the current PID to disappear, moves the downloaded file into place,
    /// restarts the app and deletes itself.
    /// </summary>
    public static void ApplyAndRestart(string downloadedExePath)
    {
        string currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序的可执行文件路径。");

        if (string.Equals(
                Path.GetFullPath(downloadedExePath),
                Path.GetFullPath(currentExe),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("更新文件路径与当前程序相同，拒绝执行。");
        }

        int currentPid = Environment.ProcessId;
        string scriptPath = Path.Combine(Path.GetTempPath(), $"wr-update-{Guid.NewGuid():N}.cmd");
        string script =
            "@echo off\r\n" +
            ":wait\r\n" +
            $"tasklist /FI \"PID eq {currentPid}\" | find \"{currentPid}\" >nul\r\n" +
            "if not errorlevel 1 (\r\n" +
            "  ping -n 2 127.0.0.1 >nul\r\n" +
            "  goto wait\r\n" +
            ")\r\n" +
            $"move /y \"{downloadedExePath}\" \"{currentExe}\" >nul 2>&1\r\n" +
            "if errorlevel 1 (\r\n" +
            $"  copy /y \"{downloadedExePath}\" \"{currentExe}\" >nul 2>&1\r\n" +
            ")\r\n" +
            $"start \"\" \"{currentExe}\"\r\n" +
            "del \"%~f0\"\r\n";

        File.WriteAllText(scriptPath, script);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
        });
        if (process == null)
            throw new InvalidOperationException("无法启动更新脚本。");
    }
}
