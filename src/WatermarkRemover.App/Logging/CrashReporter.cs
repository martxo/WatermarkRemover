using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using WatermarkRemover.App.Updates;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App.Logging;

/// <summary>
/// Bundles a crash into one zip beside the logs: summary (version, OS, install layout, exception)
/// plus the tail of today's log. The app has no telemetry backend, so this file is what the user
/// attaches to a bug report — or uploads to the cloud folder from 设置 → 日志.
/// Only the newest <see cref="KeepReports"/> are kept; a crash loop must not fill the disk.
/// </summary>
public static class CrashReporter
{
    private const int KeepReports = 10;
    private const int LogTailLines = 400;

    /// <summary>Where crash bundles live (never inside the versioned app folder).</summary>
    public static string Directory { get; } = Path.Combine(AppFolders.Root, "crashes");

    /// <summary>Existing bundles, newest first.</summary>
    public static IReadOnlyList<FileInfo> Reports() => Reports(Directory);

    /// <summary>
    /// Writes one bundle and returns its path (null when even this failed — logging must not
    /// throw from inside a crash handler).
    /// </summary>
    public static string? Capture(Exception? exception, string kind) =>
        Capture(exception, kind, Directory);

    /// <summary>Same as the public overload, into an explicit folder (tests use a temp path).</summary>
    internal static string? Capture(Exception? exception, string kind, string directory)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string staging = Path.Combine(
                Path.GetTempPath(), $"wr-crash-{stamp}-{Guid.NewGuid().ToString("N")[..6]}");
            System.IO.Directory.CreateDirectory(staging);

            File.WriteAllText(
                Path.Combine(staging, "summary.txt"), BuildSummary(exception, kind), new UTF8Encoding(false));
            string? logTail = CopyLogTail(staging);
            if (logTail is not null)
            {
                File.WriteAllText(
                    Path.Combine(staging, "readme.txt"),
                    $"崩溃时间：{DateTime.Now:O}\n类型：{kind}\n" +
                    $"日志片段：{Path.GetFileName(logTail)}（最近 {LogTailLines} 行）\n" +
                    "把这个 zip 发给开发者即可定位问题；不含文档内容。\n",
                    new UTF8Encoding(false));
            }

            // Suffix keeps two crashes in the same second from overwriting each other.
            string zip = Path.Combine(
                directory, $"crash-{stamp}-{Guid.NewGuid().ToString("N")[..6]}.zip");
            ZipFile.CreateFromDirectory(staging, zip, CompressionLevel.Optimal, includeBaseDirectory: false);
            TryDeleteDirectory(staging);
            Prune(directory);
            AppLog.Info($"崩溃报告已生成：{zip}");
            return zip;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"崩溃报告生成失败：{ex.Message}");
            return null;
        }
    }

    private static string BuildSummary(Exception? exception, string kind)
    {
        var text = new StringBuilder();
        text.AppendLine($"崩溃时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"类型：{kind}");
        text.AppendLine($"程序版本：{UpdateService.CurrentVersion}");
        text.AppendLine($"安装信息：{VelopackUpdateService.DescribeInstall()}");
        text.AppendLine($"系统：{RuntimeInformation.OSDescription}（{RuntimeInformation.ProcessArchitecture}）");
        text.AppendLine($"运行时：.NET {Environment.Version}，{Environment.ProcessorCount} 逻辑核");
        text.AppendLine($"用户：{Environment.UserName}@{Environment.MachineName}");
        text.AppendLine($"内存：{AppLog.MemorySnapshot()}");
        text.AppendLine();
        text.AppendLine("异常：");
        text.AppendLine(exception?.ToString() ?? "（无异常对象：进程被外部终止或熔断）");
        return text.ToString();
    }

    /// <summary>Copies the tail of today's log (or yesterday's) into the bundle.</summary>
    private static string? CopyLogTail(string staging)
    {
        try
        {
            string directory = AppFolders.LogDirectory;
            if (!System.IO.Directory.Exists(directory)) return null;

            FileInfo? log = new DirectoryInfo(directory)
                .GetFiles("app-*.log")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (log is null) return null;

            // The logger keeps this file open, so read with full sharing and never write to it.
            using var stream = new FileStream(
                log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new Queue<string>(LogTailLines);
            while (reader.ReadLine() is { } line)
            {
                if (lines.Count == LogTailLines) lines.Dequeue();
                lines.Enqueue(line);
            }

            string target = Path.Combine(staging, Path.GetFileName(log.FullName));
            File.WriteAllLines(target, lines, new UTF8Encoding(false));
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // the summary alone is still useful
        }
    }

    private static void Prune(string directory)
    {
        foreach (FileInfo file in Reports(directory).Skip(KeepReports))
        {
            try { file.Delete(); }
            catch (IOException) { /* a locked old bundle is harmless */ }
        }
    }

    private static IReadOnlyList<FileInfo> Reports(string directory)
    {
        try
        {
            if (!System.IO.Directory.Exists(directory)) return Array.Empty<FileInfo>();
            return new DirectoryInfo(directory)
                .GetFiles("*.zip")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<FileInfo>();
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { System.IO.Directory.Delete(path, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
