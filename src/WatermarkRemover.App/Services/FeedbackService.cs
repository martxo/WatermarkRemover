using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Recognition;
using WatermarkRemover.App.Settings;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Core.Recognition;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Core.Updates;
using WatermarkRemover.Netdisk;

namespace WatermarkRemover.App.Services;

/// <summary>一次随手反馈的提交结果。</summary>
/// <param name="Ok">是否已送达云端；false 时本机已留存，见 <paramref name="LocalPath"/>。</param>
/// <param name="Message">展示给用户的一句话结果。</param>
/// <param name="LocalPath">本地反馈 JSON 路径（失败凭据）。</param>
internal sealed record FeedbackResult(bool Ok, string Message, string LocalPath);

/// <summary>
/// 随手反馈：每个页面、每条识别记录旁的「反馈」入口，用户只输入一句问题描述即可提交。
/// 提交内容 = 用户文字 + 版本/系统信息 + 该次操作的日志切片（按 [op=操作号] 过滤）+
/// 对应的识别报告行（只有文件名/类型/页数等元数据，绝不含文档内容）。
///
/// 与 Agent 自动化系统的对应关系：这里是「事件插件」——用户的一次点击产生一条事件，
/// 经统一格式（FeedbackRecord）送进云端私有目录 /WatermarkRemover/private/feedback/，
/// 由定时任务轮询拉走处理（见项目 _ops 规则）。先落本地 feedback 目录（断网、未登录
/// 都能提），再尝试上传——与崩溃报告同一条链路，只进账号私有空间。
/// </summary>
internal sealed class FeedbackService
{
    private const string ProviderId = "alipan";
    private const string DriveName = "资源库";

    /// <summary>日志切片上限：一次操作的日志通常几十行，200 行足够覆盖整个任务链。</summary>
    private const int MaxLogLines = 200;

    /// <summary>拿不到操作号时的退化策略：附最近这么多个日志行。</summary>
    private const int TailLogLines = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly AppSettings _settings;
    private readonly ISecretProtector _protector;

    public FeedbackService(AppSettings settings, ISecretProtector protector)
    {
        _settings = settings;
        _protector = protector;
    }

    /// <summary>本地反馈目录（上传成功前反馈一直留在这里）。</summary>
    public static string LocalDirectory => AppFolders.FeedbackDirectory;

    /// <summary>
    /// 提交一条反馈。任何一步失败都不丢反馈：本地 JSON 先落盘，上传失败只影响结果提示。
    /// </summary>
    /// <param name="operationId">识别记录的操作号（六位）；全局入口传 null。</param>
    /// <param name="page">发起页面（预留，当前传 null）。</param>
    public async Task<FeedbackResult> SubmitAsync(
        string? operationId, string? page, string userText, string? contact,
        CancellationToken cancellationToken = default)
    {
        string text = (userText ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return new FeedbackResult(false, "请先输入问题描述。", string.Empty);
        }

        string id = $"fb-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}" +
                    $"-{Guid.NewGuid().ToString("N")[..4]}";
        RecognitionReport? report = FindReport(operationId);
        List<string> logSlice = SliceLogs(operationId);
        FeedbackRecord record = BuildRecord(id, operationId, page, text, contact, report, logSlice);
        string json = JsonSerializer.Serialize(record, JsonOptions);

        // 先落本机：断网、未登录、上传超时都不丢用户刚写的话。
        Directory.CreateDirectory(AppFolders.FeedbackDirectory);
        string localPath = Path.Combine(AppFolders.FeedbackDirectory, $"{id}.json");
        await File.WriteAllTextAsync(localPath, json, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await UploadAsync(id, json, cancellationToken).ConfigureAwait(false);
            AppLog.Info($"随手反馈已提交：{id}（操作 {operationId ?? "未关联"}，日志 {logSlice.Count} 行）");
            return new FeedbackResult(true, "已提交，感谢你的反馈。", localPath);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"随手反馈上传失败，已留存本机：{id}（{ex.Message}）");
            return new FeedbackResult(false, $"已保存到本机（上传失败：{ex.Message}）", localPath);
        }
    }

    /// <summary>组装反馈记录（内部可见以便单测：不碰网盘也能验证内容拼装）。</summary>
    internal static FeedbackRecord BuildRecord(string id, string? operationId, string? page,
        string userText, string? contact, RecognitionReport? report, List<string> logSlice)
    {
        return new FeedbackRecord
        {
            Id = id,
            At = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            AppVersion = AppVersionText(),
            Os = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})",
            Machine = Environment.MachineName,
            OperationId = operationId ?? "",
            Page = page ?? "",
            UserText = userText,
            Contact = (contact ?? string.Empty).Trim(),
            Report = report,
            LogSlice = logSlice,
        };
    }

    /// <summary>
    /// 从日志目录切出与某次操作相关的日志行。每条日志都带 <c>[op=六位操作号]</c> 标记
    /// （LogLine.Format），按号过滤即得该次操作从开始到结束的完整日志——这正是
    /// 「用户指出是哪条记录出的问题，就能直接找到当时日志」的实现。读最近两个日志
    /// 文件（跨零点），最多 <see cref="MaxLogLines"/> 行；拿不到操作号时退化为
    /// 最近 <see cref="TailLogLines"/> 行。<paramref name="logDirectory"/> 供测试注入
    /// 临时目录，生产走 <see cref="AppFolders.LogDirectory"/>。
    /// </summary>
    internal static List<string> SliceLogs(string? operationId, string? logDirectory = null)
    {
        string directory = logDirectory ?? AppFolders.LogDirectory;
        if (!Directory.Exists(directory))
        {
            return new List<string>();
        }

        // app-yyyyMMdd.log 字典序即时间序：取最新两个文件，保持 chronological。
        List<string> lines = new();
        foreach (string file in Directory.EnumerateFiles(directory, "app-*.log")
                     .OrderBy(f => f, StringComparer.Ordinal)
                     .TakeLast(2))
        {
            try
            {
                lines.AddRange(File.ReadLines(file));
            }
            catch (IOException)
            {
                // 日志正在写入/轮换：跳过该文件，另一天或尾部兜底。
            }
        }

        if (string.IsNullOrEmpty(operationId))
        {
            return lines.TakeLast(TailLogLines).ToList();
        }

        string marker = $"[op={operationId}]";
        return lines
            .Where(l => l.Contains(marker, StringComparison.Ordinal))
            .TakeLast(MaxLogLines)
            .ToList();
    }

    /// <summary>按操作号从识别报告 JSONL 里找对应的那次操作（没有号或没找到都返回 null）。</summary>
    internal static RecognitionReport? FindReport(string? operationId)
    {
        if (string.IsNullOrEmpty(operationId))
        {
            return null;
        }

        try
        {
            return new RecognitionReportStore()
                .ReadRecent(200)
                .FirstOrDefault(r => string.Equals(r.OperationId, operationId, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取识别报告失败：{ex.Message}");
            return null;
        }
    }

    private async Task UploadAsync(string id, string json, CancellationToken ct)
    {
        IReadOnlyDictionary<string, string>? values = TryBuildAccountValues()
            ?? throw new InvalidOperationException("尚未登录阿里云盘，反馈只存到了本机。");

        await using var session = await new AliyunDriveProvider()
            .ConnectAsync(new NetdiskCredentials(values))
            .ConfigureAwait(false);
        NetdiskDrive? drive = session.Drives.FirstOrDefault(d => d.Name == DriveName)
            ?? throw new InvalidOperationException("账号没有「资源库」。");
        await session.SwitchDriveAsync(drive.Id, ct).ConfigureAwait(false);
        string folder = await session.EnsureFolderAsync(
            $"{CloudUpdateFormat.RootFolder}/private/feedback", ct).ConfigureAwait(false);
        await session.UploadBytesAsync(folder, $"{id}.json", Encoding.UTF8.GetBytes(json), ct)
            .ConfigureAwait(false);
    }

    /// <summary>账号解析与 CloudUpdateService 一致：设置里保存的账号优先，本机 CLI 登录兜底。</summary>
    private IReadOnlyDictionary<string, string>? TryBuildAccountValues()
    {
        try
        {
            IEnumerable<NetdiskAccountState> accounts = NetdiskAccountPersistence.Decode(
                _settings.NetdiskAccounts, _protector, (message, _) => AppLog.Warn(message));
            NetdiskAccountState? account = accounts.FirstOrDefault(a => a.ProviderId == ProviderId);
            if (account is not null)
            {
                return account.Values;
            }

            AliyunpanCliRuntime.EnsureStableConfig();
            return AliyunpanCliRuntime.TryBuildAccountValues();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取网盘账号失败：{ex.Message}");
            return null;
        }
    }

    private static string AppVersionText()
    {
        Assembly assembly = typeof(FeedbackService).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}

/// <summary>
/// 一条反馈的统一事件格式（Agent 自动化系统里的「事件」）。只追加、不改写；
/// 定时任务拉到本地后按 Id 去重，相同错误签名的多条合并成一轮处理。
/// </summary>
internal sealed class FeedbackRecord
{
    public string Id { get; set; } = "";

    /// <summary>提交时间（本机时区）。</summary>
    public string At { get; set; } = "";

    public string AppVersion { get; set; } = "";

    public string Os { get; set; } = "";

    public string Machine { get; set; } = "";

    /// <summary>关联的识别操作号；全局入口提交时为空。</summary>
    public string OperationId { get; set; } = "";

    /// <summary>发起页面（预留）。</summary>
    public string Page { get; set; } = "";

    /// <summary>用户写的问题描述（唯一的内容字段）。</summary>
    public string UserText { get; set; } = "";

    /// <summary>用户自愿留下的联系方式（选填）。</summary>
    public string Contact { get; set; } = "";

    /// <summary>该次操作的识别报告（元数据：文件名/类型/页数，无文档内容）。</summary>
    public RecognitionReport? Report { get; set; }

    /// <summary>该次操作的日志切片（按 [op=操作号] 过滤）。</summary>
    public List<string> LogSlice { get; set; } = new();
}
