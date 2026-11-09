using System.IO;

namespace WatermarkRemover.App.Updates;

/// <summary>
/// Update.exe 自己的日志（<c>%LOCALAPPDATA%\velopack\velopack_&lt;app&gt;.log</c>）是唯一记录
/// "为什么这次没能把文件换上去"的地方——目录不可写、文件被占用、解包中断都在这里。C# 侧只知道
/// "重启后还是旧版本"，所以以前的失败提示只能罗列猜测原因。这里读取最近一次 Update.exe 运行，
/// 把它的 ERROR/WARN 原文交给对话框和程序日志，让下一台出问题的机器一次就能说明白。
/// </summary>
internal static class VelopackUpdaterLog
{
    private const string FolderName = "velopack";

    /// <summary>最近一次 Update.exe 运行的完整日志路径；从未运行过时返回 null。</summary>
    public static string? Locate()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);
            if (!Directory.Exists(dir)) return null;

            string own = Path.Combine(dir, $"velopack_{UpdateConfig.VelopackPackId}.log");
            if (File.Exists(own)) return own;

            // Velopack 也会写一份共用的 velopack.log（不含应用名）；优先本应用的，其次最新的一份。
            string[] candidates = Directory.GetFiles(dir, "velopack*.log");
            return candidates
                       .Where(path => Path.GetFileName(path)
                           .Contains(UpdateConfig.VelopackPackId, StringComparison.OrdinalIgnoreCase))
                       .OrderByDescending(File.GetLastWriteTimeUtc)
                       .FirstOrDefault()
                   ?? candidates.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 最后一次 Update.exe 会话的行（按 <c>[update:pid]</c> 前缀切分，允许中间夹着应用钩子进程的
    /// <c>[lib-csharp:pid]</c> 输出）。文件读不到时返回空列表。
    /// </summary>
    public static IReadOnlyList<string> LastSession(string path, int maxLines = 400)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }

        var session = new List<string>();
        string? pid = null;
        for (int i = lines.Length - 1; i >= 0 && session.Count < maxLines; i--)
        {
            string line = lines[i];
            int start = line.IndexOf("[update:", StringComparison.Ordinal);
            if (start >= 0)
            {
                int end = line.IndexOf(']', start);
                string candidate = end > start ? line[start..(end + 1)] : line[start..];
                if (pid is null)
                {
                    pid = candidate;
                }
                else if (!string.Equals(pid, candidate, StringComparison.Ordinal))
                {
                    break; // 上一次 Update.exe 运行的尾部，与本会话无关
                }
            }

            session.Insert(0, line);
        }

        return session;
    }

    /// <summary>
    /// 最近一次运行里最该被看到的一行：优先最后一条 ERROR，其次最后一条 WARN。
    /// 没有任何失败行时返回 null（比如日志被清空过）。
    /// </summary>
    public static string? LastFailure(string path)
    {
        IReadOnlyList<string> session = LastSession(path);
        return session.LastOrDefault(IsError) ?? session.LastOrDefault(IsWarn);
    }

    /// <summary>把 Update.exe 的失败原因写成一段可放进对话框的文字。</summary>
    public static string Describe(string? path)
    {
        if (path is null)
            return "• 本机没有更新程序的日志（Update.exe 可能从未运行），无法给出具体原因。";

        string? failure = LastFailure(path);
        return failure is null
            ? $"• 更新程序日志里没有失败记录：{path}"
            : $"• 更新程序报告：{failure}\n• 完整日志：{path}";
    }

    private static bool IsError(string line) => line.Contains("[ERROR]", StringComparison.Ordinal);

    private static bool IsWarn(string line) => line.Contains("[WARN]", StringComparison.Ordinal);
}
