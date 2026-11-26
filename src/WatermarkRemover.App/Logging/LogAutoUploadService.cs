using System.IO;
using System.Security.Cryptography;
using System.Text;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Logging;

namespace WatermarkRemover.App.Logging;

/// <summary>
/// 每 30 分钟把本机当天的日志整份上传到网盘「/操作日志/」，文件名按机器名+日期区分，同名覆盖，
/// 云端每台设备每天只有一份，不会堆成几百个碎片。只在已配置网盘账号时工作：没有账号静默跳过，
/// 上传失败只写一条 WARN，绝不影响扫描/自动备份主流程。
/// 内容没变化时跳过上传，避免空闲的机器每半小时重复推送同一份文件。
/// </summary>
internal sealed class LogAutoUploadService : IDisposable
{
    /// <summary>云端目录（网盘根目录下），与自动备份的 /桌面文件备份 分开，避免混在一起。</summary>
    internal const string RemoteDirectory = "/操作日志";

    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private readonly NetdiskTransferService _transfer;
    private readonly Func<NetdiskUploadTarget?> _targetProvider;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private string _lastFingerprint = string.Empty;
    private DateTime _lastFingerprintDay;

    public LogAutoUploadService(
        NetdiskTransferService transfer,
        Func<NetdiskUploadTarget?> targetProvider)
    {
        _transfer = transfer ?? throw new ArgumentNullException(nameof(transfer));
        _targetProvider = targetProvider ?? throw new ArgumentNullException(nameof(targetProvider));
    }

    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    public void Dispose()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(FirstDelay, ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await UploadOnceAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // 网络/授权问题由下一轮自动重试；这里只留一条线索。
                    AppLog.Warn($"日志自动上传失败：{ex.Message}");
                }

                await Task.Delay(Interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 应用退出，正常结束。
        }
    }

    private async Task UploadOnceAsync(CancellationToken ct)
    {
        NetdiskUploadTarget? target = _targetProvider();
        if (target is null) return;

        DateTime dayStart = DateTime.Today;
        IReadOnlyList<LogEntry> entries = LogExportService.CollectSince(dayStart);
        if (entries.Count == 0) return;

        // 指纹只看日志条目本身（报告头部的导出时间每次都不同，不能参与比较）。
        string fingerprint = Fingerprint(entries);
        if (dayStart == _lastFingerprintDay && fingerprint == _lastFingerprint) return;

        string report = LogExportService.BuildReport(entries, $"今天 00:00 起（{entries.Count} 条）");
        byte[] bytes = Encoding.UTF8.GetBytes(report);
        string name = BuildFileName(Environment.MachineName, DateTime.Now);
        string tempDirectory = Path.Combine(
            Path.GetTempPath(), "wm-log-upload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string temp = Path.Combine(tempDirectory, name);
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            await _transfer.UploadFileAsync(target, temp, ct, overwrite: true).ConfigureAwait(false);
            _lastFingerprint = fingerprint;
            _lastFingerprintDay = dayStart;
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    private static string Fingerprint(IReadOnlyList<LogEntry> entries)
    {
        var text = new StringBuilder(entries.Count * 64);
        foreach (LogEntry entry in entries)
            text.Append(entry.RawText).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string BuildFileName(string machineName, DateTime at)
    {
        var safe = new StringBuilder(machineName.Length);
        foreach (char c in machineName)
            safe.Append(char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-');

        string name = safe.ToString().Trim('-');
        if (name.Length == 0) name = "pc";
        return $"操作日志-{name}-{at:yyyyMMdd}.log";
    }
}
