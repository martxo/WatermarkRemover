using System.Diagnostics;
using System.IO;

namespace WatermarkRemover.App.Updates;

/// <summary>
/// Swaps the running application for a downloaded update and restarts it. Handles both update
/// package shapes: a bare executable (pre-1.7 clients) and a zip containing the executable plus
/// the <c>Models\</c> OCR model folder (v1.7+). A running single-file executable cannot overwrite
/// itself, so a detached cmd script waits for the current PID to disappear, applies the package,
/// restarts the app and deletes itself.
/// </summary>
public static class UpdateApplier
{
    /// <summary>
    /// Schedules the replacement of the running app from a downloaded package, to take effect once
    /// this process exits, then relaunches the app.
    /// </summary>
    public static void ApplyAndRestart(string downloadedPackagePath)
    {
        string currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序的可执行文件路径。");

        if (string.Equals(
                Path.GetFullPath(downloadedPackagePath),
                Path.GetFullPath(currentExe),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("更新文件路径与当前程序相同，拒绝执行。");
        }

        int currentPid = Environment.ProcessId;
        string appDir = Path.GetDirectoryName(currentExe)!;
        bool isZip = string.Equals(
            Path.GetExtension(downloadedPackagePath), ".zip", StringComparison.OrdinalIgnoreCase);

        // The package is unpacked next to the app directory; extracting it into a staging folder
        // first keeps a half-applied update from leaving the app dir inconsistent.
        string stagingDir = Path.Combine(Path.GetTempPath(), $"wr-staging-{Guid.NewGuid():N}");
        string applyCommands = isZip
            ? BuildZipApplyCommands(downloadedPackagePath, stagingDir, currentExe, appDir)
            : BuildExeApplyCommands(downloadedPackagePath, currentExe);

        string scriptPath = Path.Combine(Path.GetTempPath(), $"wr-update-{Guid.NewGuid():N}.cmd");
        string script =
            "@echo off\r\n" +
            ":wait\r\n" +
            $"tasklist /FI \"PID eq {currentPid}\" | find \"{currentPid}\" >nul\r\n" +
            "if not errorlevel 1 (\r\n" +
            "  ping -n 2 127.0.0.1 >nul\r\n" +
            "  goto wait\r\n" +
            ")\r\n" +
            applyCommands +
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

    /// <summary>Legacy shape: the package is the executable itself.</summary>
    private static string BuildExeApplyCommands(string downloadedExePath, string currentExe) =>
        $"move /y \"{downloadedExePath}\" \"{currentExe}\" >nul 2>&1\r\n" +
        "if errorlevel 1 (\r\n" +
        $"  copy /y \"{downloadedExePath}\" \"{currentExe}\" >nul 2>&1\r\n" +
        ")\r\n";

    /// <summary>
    /// Zip shape: unpack to a staging folder, then copy the executable and every other shipped
    /// file (the Models\ OCR models, anything else the package carries) over the app directory.
    /// Copy rather than move so a failed step can still fall back on the previous copy attempt.
    /// </summary>
    private static string BuildZipApplyCommands(string zipPath, string stagingDir, string currentExe, string appDir) =>
        $"mkdir \"{stagingDir}\" >nul 2>&1\r\n" +
        $"powershell -NoProfile -ExecutionPolicy Bypass -Command \"Expand-Archive -Force -Path '{zipPath}' -DestinationPath '{stagingDir}'\" >nul 2>&1\r\n" +
        $"move /y \"{stagingDir}\\{Path.GetFileName(currentExe)}\" \"{currentExe}\" >nul 2>&1\r\n" +
        "if errorlevel 1 (\r\n" +
        $"  copy /y \"{stagingDir}\\{Path.GetFileName(currentExe)}\" \"{currentExe}\" >nul 2>&1\r\n" +
        ")\r\n" +
        $"xcopy /e /i /y \"{stagingDir}\\*\" \"{appDir}\\\" >nul 2>&1\r\n" +
        $"rmdir /s /q \"{stagingDir}\" >nul 2>&1\r\n" +
        $"del \"{zipPath}\" >nul 2>&1\r\n";
}
