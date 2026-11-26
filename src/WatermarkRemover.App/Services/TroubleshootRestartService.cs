using System.Diagnostics;

namespace WatermarkRemover.App.Services;

/// <summary>
/// 「疑难重启」的执行端。扫描偶发卡死（界面一直「扫描中」、打印机不动、也不报错，连官方
/// 打印/扫描软件一起失灵）时，多半是本软件的桥接子进程或官方软件的用户级进程还占着 USB
/// 设备。这里做用户手动在任务管理器里做的同一件事：结束本软件残留进程和当前登录会话里的
/// 官方打印/扫描软件进程，再由宿主启动一个新实例并退出。
/// 只动当前登录会话的用户进程：BrYNSvc 等打印机服务跑在 SYSTEM 会话，强杀会让服务直接停
/// 掉（需要管理员，且和用户手动恢复的范围一致），不在本功能内。
/// </summary>
internal static class TroubleshootRestartService
{
    /// <summary>命令行开关：新实例先等这个旧进程退出，再去抢单实例互斥体。</summary>
    public const string WaitForProcessArgument = "--restart-after";

    /// <summary>本软件除当前进程外可能残留的进程。</summary>
    private static readonly string[] OwnProcessNames =
    {
        "WatermarkRemover",
        "WatermarkRemover.Scan.Bridge",
    };

    /// <summary>官方打印/扫描软件里与设备占用相关的用户级进程（本机取证）。</summary>
    private static readonly string[] OfficialProcessNames =
    {
        "iPSMonitor",
        "HttpToUsbBridge",
        "BrLogRx",
    };

    /// <summary>官方软件安装根目录：升级换了进程名时仍能按路径命中。</summary>
    private static readonly string[] OfficialInstallRoots =
    {
        @"\Program Files\Brother\",
        @"\Program Files (x86)\Brother\",
        @"\Program Files\Browny02\",
        @"\Program Files (x86)\Browny02\",
    };

    /// <summary>虽然装在官方目录下，但与扫描设备无关，不结束。</summary>
    private static readonly string[] DoNotStopProcessNames =
    {
        "SoftwareUpdateNotificationService",
    };

    /// <summary>结束冲突进程；返回实际结果供调用方写日志、向用户说明。</summary>
    public static TroubleshootRestartResult StopConflictingProcesses(int currentProcessId)
    {
        var stopped = new List<string>();
        var failed = new List<string>();

        int session;
        try
        {
            session = Process.GetCurrentProcess().SessionId;
        }
        catch
        {
            session = -1; // 读不到会话号时退回按进程名/路径判断
        }

        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == currentProcessId) continue;
                if (session >= 0 && process.SessionId != session) continue;

                string name = process.ProcessName;
                if (!ShouldStop(process, name)) continue;

                string label = $"{name}.exe（{process.Id}）";
                try
                {
                    process.Kill(entireProcessTree: true);
                    stopped.Add(label);
                }
                catch (Exception ex)
                {
                    failed.Add($"{label}：{ex.Message}");
                }
            }
            catch (Exception)
            {
                // 枚举/访问权限的竞态：跳过这一个进程，不影响其它进程。
            }
            finally
            {
                process.Dispose();
            }
        }

        return new TroubleshootRestartResult(stopped, failed);
    }

    /// <summary>启动新实例（经由 Shell，不挂在当前进程下面），参数里带上要等待的旧进程号。</summary>
    public static bool TryRelaunch(int previousProcessId, out string? error)
    {
        error = null;
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            error = "找不到当前程序的可执行文件路径。";
            return false;
        }

        string arguments = $"{WaitForProcessArgument} {previousProcessId}";
        if (NetdiskAdminAccess.IsEnabled)
            arguments += " " + NetdiskAdminAccess.AdminArgument;

        try
        {
            using Process? started = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true,
            });
            if (started is null)
            {
                error = "系统没有返回新进程。";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 新实例启动时调用：解析 <see cref="WaitForProcessArgument"/> 并等旧进程真正退出。旧实例
    /// 关闭要停后台服务、释放单实例互斥体，单实例守卫自带的 2 秒等待不够。
    /// </summary>
    public static RestartHandoff? WaitForPreviousInstance(IReadOnlyList<string> args)
    {
        int index = -1;
        for (int i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], WaitForProcessArgument, StringComparison.OrdinalIgnoreCase)) continue;
            index = i;
            break;
        }

        if (index < 0 || index + 1 >= args.Count) return null;
        if (!int.TryParse(args[index + 1], out int processId) || processId <= 0) return null;

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (!IsRunning(processId)) return new RestartHandoff(processId, Exited: true, Forced: false);
            Thread.Sleep(150);
        }

        // 旧实例 15 秒还没退——正是要急救的卡死场景。强制结束它，新实例才能拿到单实例
        // 互斥体（旧进程被结束时会抛出 AbandonedMutexException，守卫按首位实例处理）。
        TryKill(processId);
        return new RestartHandoff(processId, !IsRunning(processId), Forced: true);
    }

    private static void TryKill(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (!process.HasExited) process.Kill(entireProcessTree: false);
        }
        catch
        {
            // 已经退出或无权结束：交给后续单实例守卫决定，不再重试。
        }
    }

    private static bool ShouldStop(Process process, string name)
    {
        if (OwnProcessNames.Any(own => string.Equals(own, name, StringComparison.OrdinalIgnoreCase)))
            return true;

        if (DoNotStopProcessNames.Any(skip => string.Equals(skip, name, StringComparison.OrdinalIgnoreCase)))
            return false;

        string? path = TryGetExecutablePath(process);
        if (path is not null &&
            OfficialInstallRoots.Any(root => path.Contains(root, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // 读不到路径（位数差异/权限）时退回已知进程名。
        return path is null &&
               OfficialProcessNames.Any(official =>
                   string.Equals(official, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? TryGetExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false; // 已退出
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>新实例启动时对旧实例的交接结果。</summary>
internal sealed record RestartHandoff(int ProcessId, bool Exited, bool Forced);

/// <summary>一次「疑难重启」的清理结果；<see cref="Describe"/> 直接进日志与提示。</summary>
internal sealed record TroubleshootRestartResult(
    IReadOnlyList<string> Stopped,
    IReadOnlyList<string> Failed)
{
    public string Describe()
    {
        string stopped = Stopped.Count == 0
            ? "未发现需要结束的进程"
            : $"已结束 {Stopped.Count} 个进程：{string.Join("、", Stopped)}";
        return Failed.Count == 0
            ? stopped
            : $"{stopped}；未能结束：{string.Join("；", Failed)}";
    }
}
