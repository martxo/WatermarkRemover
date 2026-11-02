using WatermarkRemover.App.Logging;

namespace WatermarkRemover.App.Services;

/// <summary>
/// 晾衣绳（最近截图）触发/收回探针：默认关闭，设置环境变量 <c>WMR_SHELF_PROBE=1</c> 后随
/// AppLog 输出到当日日志（<c>[shelf-probe]</c> 前缀，Debug 级）。用途是实证定位「弹出慢、
/// 收回慢、切到其他应用不收回」：热区命中与持续时长、进入/离开、窗口激活/失焦、Topmost、
/// 定时器启停与实际轮询间隔全部落盘，定位根因不靠猜测。
/// </summary>
internal static class ShelfProbe
{
    /// <summary>探针开关。读一次即可：开关在进程启动前设好，运行中不需要热切换。</summary>
    public static readonly bool Enabled = ReadSwitch();

    private static bool ReadSwitch()
    {
        string? raw = Environment.GetEnvironmentVariable("WMR_SHELF_PROBE");
        if (string.IsNullOrWhiteSpace(raw)) return false;
        raw = raw.Trim();
        return raw == "1"
            || raw.Equals("true", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("on", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public static void Log(string message)
    {
        if (!Enabled) return;
        AppLog.Debug($"[shelf-probe] {message}");
    }
}
