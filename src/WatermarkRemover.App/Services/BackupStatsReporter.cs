using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Settings;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App.Services;

/// <summary>
/// 备份统计上报：每台装了本软件的电脑每 10 分钟把「桌面备份概况」推到统计页（备份与征信）。
/// 数据来源与桌面端脚本一致：automatic-backup/state.json 里最近在用的那个目标桶 + 当前监控
/// 目录（默认桌面）的实时文件列表，因此页面能看到桌面上每个文件是否已上传、最近 14 天每天
/// 上传了多少。放在应用进程里而不是每台机器手工装计划任务：随软件更新自动覆盖所有电脑，
/// 不需要管理员权限，也不会闪控制台窗口。应用退出后不再上报（视为这台电脑没在用软件）。
/// 失败只写调试日志，静默重试，绝不影响扫描/备份主流程。
/// </summary>
internal sealed class BackupStatsReporter : IDisposable
{
    private const string PushUrl = "http://106.52.57.36/backup-stats/push";
    private const string PushToken = "wmbs-c850bb13004d426667a02d65bf824e0e";
    private const int MaxFiles = 2000;

    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions StateOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public static BackupStatsReporter Shared { get; } = new();

    public void Start()
    {
        _loop ??= Task.Run(() => RunAsync(_cts.Token));
    }

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
                    await PushOnceAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    AppLog.Debug($"备份统计上报失败：{ex.Message}");
                }

                await Task.Delay(Interval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 应用退出，正常结束。
        }
    }

    private static async Task PushOnceAsync(CancellationToken ct)
    {
        string statePath = Path.Combine(AppFolders.Root, "automatic-backup", "state.json");
        Dictionary<string, AutomaticBackupState>? states = TryReadStates(statePath);
        if (states is null || states.Count == 0) return;

        // 键 = "<配置指纹>\n<相对路径>"。活跃桶 = 最近真正上传成功的那个目标，历史指纹的
        // 记录不参与统计，避免换过网盘/目录后页面混入旧数据。
        var buckets = new Dictionary<string, List<(string Path, AutomaticBackupState State)>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in states)
        {
            int separator = pair.Key.IndexOf('\n');
            if (separator <= 0 || separator + 1 >= pair.Key.Length) continue;
            string fingerprint = pair.Key[..separator];
            string relativePath = pair.Key[(separator + 1)..];
            if (!buckets.TryGetValue(fingerprint, out var entries))
            {
                entries = new List<(string, AutomaticBackupState)>();
                buckets[fingerprint] = entries;
            }

            entries.Add((relativePath, pair.Value));
        }

        string? activeFingerprint = null;
        DateTimeOffset newestSuccess = DateTimeOffset.MinValue;
        foreach (var pair in buckets)
        {
            foreach (var entry in pair.Value)
            {
                if (entry.State.LastSuccessUtc <= newestSuccess) continue;
                newestSuccess = entry.State.LastSuccessUtc;
                activeFingerprint = pair.Key;
            }
        }

        if (activeFingerprint is null) return;
        var active = buckets[activeFingerprint];
        var statesByPath = new Dictionary<string, AutomaticBackupState>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in active) statesByPath[entry.Path] = entry.State;

        AppSettings settings = SettingsService.Load();
        string root = settings.AutomaticBackupRootDirectory.Trim();
        if (root.Length == 0)
            root = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        root = Path.TrimEndingDirectorySeparator(root);

        string drive = settings.AutomaticBackupDrive.Trim();
        string remote = settings.AutomaticBackupRemoteDirectory.Trim();
        string target = drive.Length > 0 ? drive + remote : "备份盘/桌面文件备份";

        string rootError = Directory.Exists(root) ? string.Empty : $"监控目录不存在：{root}";
        var desktop = new List<DesktopFile>();
        int unsupported = 0;
        if (rootError.Length == 0)
            ScanDesktop(root, statesByPath, desktop, ref unsupported, ct);

        // 每日成功数：每个文件最后一次成功上传落在哪一天（本地日）。
        var dayCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in active)
        {
            string day = entry.State.LastSuccessUtc.ToLocalTime().Date
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            dayCounts[day] = dayCounts.GetValueOrDefault(day) + 1;
        }

        var days = new List<DayCount>(14);
        for (int offset = 13; offset >= 0; offset--)
        {
            string day = DateTime.Today.AddDays(-offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            days.Add(new DayCount(day, dayCounts.GetValueOrDefault(day)));
        }

        var files = new List<SuccessFile>(active.Count);
        foreach (var entry in active)
        {
            files.Add(new SuccessFile(
                entry.Path,
                entry.State.Length,
                entry.State.LastSuccessUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
        }

        var document = new
        {
            machine = Environment.MachineName,
            device = new
            {
                name = Environment.MachineName,
                os = RuntimeInformation.OSDescription,
                user = Environment.UserName,
            },
            generatedUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            bucket = activeFingerprint[..Math.Min(12, activeFingerprint.Length)],
            target,
            root,
            rootError,
            unsupportedCount = unsupported,
            desktop,
            days,
            files,
        };

        string json = JsonSerializer.Serialize(document, PayloadOptions);
        using var request = new HttpRequestMessage(HttpMethod.Post, PushUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("X-Push-Token", PushToken);
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>列出监控目录里会参与备份的文件，并标记与已上传记录是否完全一致。</summary>
    private static void ScanDesktop(
        string root,
        IReadOnlyDictionary<string, AutomaticBackupState> statesByPath,
        List<DesktopFile> desktop,
        ref int unsupported,
        CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        foreach (string path in Directory.EnumerateFiles(root, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (desktop.Count >= MaxFiles) break;

            try
            {
                if (!AutomaticBackupFileFilter.IsSupported(path))
                {
                    // 快捷方式、程序、临时文件等：只报个数；隐藏的系统文件不吓人。
                    FileAttributes attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                        unsupported++;
                    continue;
                }

                if (!AutomaticBackupFileFilter.TryGetRelativePath(root, path, out string relativePath))
                    continue;

                var info = new FileInfo(path);
                long length = info.Length;
                DateTime lastWriteUtc = info.LastWriteTimeUtc;
                bool uploaded = false;
                string? lastSuccessUtc = null;
                if (statesByPath.TryGetValue(relativePath, out AutomaticBackupState? state)
                    && state.Length == length
                    && state.LastWriteUtc.UtcDateTime.Ticks == lastWriteUtc.Ticks)
                {
                    uploaded = true;
                    lastSuccessUtc = state.LastSuccessUtc
                        .ToUniversalTime()
                        .ToString("o", CultureInfo.InvariantCulture);
                }

                desktop.Add(new DesktopFile(
                    relativePath, length, lastWriteUtc.ToString("o", CultureInfo.InvariantCulture),
                    uploaded, lastSuccessUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个文件读不到不影响整份报表。
            }
        }
    }

    private static Dictionary<string, AutomaticBackupState>? TryReadStates(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var states = JsonSerializer.Deserialize<Dictionary<string, AutomaticBackupState>>(
                File.ReadAllText(path), StateOptions);
            if (states is null) return null;

            var usable = new Dictionary<string, AutomaticBackupState>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in states)
            {
                if (pair.Value is { Length: >= 0 } && !string.IsNullOrWhiteSpace(pair.Value.Sha256))
                    usable[pair.Key] = pair.Value;
            }

            return usable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 备份服务正在原子替换 state.json 时可能短暂读不到，下一轮再报。
            AppLog.Debug($"备份统计：读取 state.json 失败（{ex.GetType().Name}）");
            return null;
        }
    }

    private sealed record DesktopFile(
        string Path, long Length, string LastWriteUtc, bool Uploaded, string? LastSuccessUtc);

    private sealed record DayCount(string Date, int Success);

    private sealed record SuccessFile(string Path, long Length, string LastSuccessUtc);
}
