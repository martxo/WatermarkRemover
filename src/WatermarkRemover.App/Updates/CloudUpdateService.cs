using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.Settings;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Core.Updates;
using WatermarkRemover.Netdisk;

namespace WatermarkRemover.App.Updates;

public enum CloudCheckStatus
{
    NotConfigured,
    NoRelease,
    UpToDate,
    UpdateAvailable,
    Failed,
}

/// <summary>One cloud check: the remote manifest, the local diff and the retained version index.</summary>
public sealed record CloudCheckResult(
    CloudCheckStatus Status,
    string Message,
    Version Current,
    Version? Latest,
    CloudManifest? Manifest,
    CloudUpdatePlan? Plan,
    LocalFileIndex? LocalIndex,
    CloudVersionIndex? Versions)
{
    public bool UpdateAvailable => Status == CloudCheckStatus.UpdateAvailable;
}

public enum CloudUpdateStage
{
    Checking,
    Downloading,
    Assembling,
    Ready,
}

public sealed record CloudUpdateProgress(
    CloudUpdateStage Stage,
    double? Percent,
    long Bytes,
    long? TotalBytes,
    string? Message);

/// <summary>Staging output: files to copy and paths to delete, ready for the applier.</summary>
public sealed record CloudStageResult(
    CloudCheckResult Check,
    string StagingDirectory,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Deletes,
    long DownloadedBytes,
    long ChangedBytes);

/// <summary>Where a portable login pack was written locally and in the private cloud folder.</summary>
public sealed record CloudLoginPackResult(string LocalPath, string CloudPath);

public sealed class CloudUpdateHistoryEntry
{
    public string At { get; set; } = string.Empty;
    public string FromVersion { get; set; } = string.Empty;
    public string ToVersion { get; set; } = string.Empty;
    public string Direction { get; set; } = "update";
    public string Note { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int FileCount { get; set; }
    public long Bytes { get; set; }
}

public sealed class CloudApplyResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string ToVersion { get; set; } = string.Empty;
    public string FromVersion { get; set; } = string.Empty;
    public string FinishedAt { get; set; } = string.Empty;
}

/// <summary>
/// 阿里云盘 file-level update channel. The repository holds one manifest per retained version and
/// content-addressed chunks shared by all versions; a client hashes its own install, reuses chunks
/// already on disk, downloads only the missing ones, then a detached PowerShell script replaces the
/// files after the app exits and restores the backup if anything fails.
/// </summary>
public sealed class CloudUpdateService
{
    private const string ProviderId = "alipan";
    private const string DriveName = "资源库";

    private readonly AppSettings _settings;
    private readonly ISecretProtector _protector;
    private readonly NetdiskAccountState? _account;
    private readonly MirrorUpdateSource? _mirror;

    public CloudUpdateService(AppSettings settings, ISecretProtector protector)
    {
        _settings = settings;
        _protector = protector;
        // The HTTP mirror is tried before every netdisk read; a failed fetch falls back
        // transparently, and the breaker inside it short-circuits an unreachable mirror.
        _mirror = MirrorUpdateSource.TryCreate(settings.UpdateMirrorUrl);
        // A login pack dropped beside the app or into the stable data folder is imported before
        // the account is resolved, so a fresh machine starts already logged in.
        AliyunpanCliRuntime.EnsureStableConfig();
        TryAutoImportLoginPack();

        IEnumerable<NetdiskAccountState> accounts = NetdiskAccountPersistence.Decode(
            settings.NetdiskAccounts, protector, (message, ex) => AppLog.Warn(message));
        _account = accounts.FirstOrDefault(a => a.ProviderId == ProviderId) ?? TryCreateCliAccount();
    }

    /// <summary>
    /// Fallback for machines where the app has no saved netdisk account but the local aliyunpan
    /// CLI is already logged in (this is how the publishing machine is set up). The CLI owns the
    /// credential lifecycle, so no app account is needed for cloud updates.
    /// </summary>
    private static NetdiskAccountState? TryCreateCliAccount()
    {
        try
        {
            IReadOnlyDictionary<string, string>? values = AliyunpanCliRuntime.TryBuildAccountValues();
            return values is null ? null : new NetdiskAccountState(ProviderId, "本机 aliyunpan", values);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取本机 aliyunpan 登录信息失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Exports the current CLI login as a portable pack: a local copy in the stable data folder
    /// and a private copy under /WatermarkRemover/private/ on the account's 资源库. The pack is
    /// encrypted with <paramref name="passphrase"/> — a pack holds live tokens and must never sit
    /// in the cloud (or a Downloads folder) in the clear.
    /// </summary>
    public async Task<CloudLoginPackResult> ExportLoginPackAsync(
        string passphrase, CancellationToken cancellationToken = default)
    {
        if (_account is null)
        {
            throw new InvalidOperationException("尚未登录阿里云盘，无法导出登录状态。");
        }

        AliyunpanCliRuntime.EnsureStableConfig();
        string raw = AliyunpanCliRuntime.TryReadRawConfig()
            ?? throw new InvalidOperationException("未找到本机 aliyunpan 登录配置，请先完成扫码登录。");
        string pack = LoginPack.Protect(LoginPack.Create(raw), passphrase);

        Directory.CreateDirectory(AppFolders.Root);
        string localPath = Path.Combine(AppFolders.Root, LoginPack.FileName);
        File.WriteAllText(localPath, pack, new UTF8Encoding(false));

        await using AliyunDriveSessionBundle bundle = await OpenAsync(createFolders: true, cancellationToken)
            .ConfigureAwait(false);
        if (bundle.RootFolderId is null)
        {
            throw new InvalidOperationException("云端更新目录不存在，无法上传登录状态。");
        }

        string privateFolder = await bundle.Session
            .EnsureFolderAsync($"{CloudUpdateFormat.RootFolder}/private", cancellationToken)
            .ConfigureAwait(false);
        await bundle.Session.UploadBytesAsync(
                privateFolder, LoginPack.FileName, Encoding.UTF8.GetBytes(pack), cancellationToken)
            .ConfigureAwait(false);

        string cloudPath = $"/{CloudUpdateFormat.RootFolder}/private/{LoginPack.FileName}";
        AppLog.Info($"登录状态已导出：{localPath}，云端 {cloudPath}");
        return new CloudLoginPackResult(localPath, cloudPath);
    }

    /// <summary>
    /// Uploads local crash bundles to /WatermarkRemover/private/crashes. Opt-in only (设置 → 日志):
    /// the app never sends diagnostics on its own, and the files contain no document content.
    /// </summary>
    public async Task<int> UploadCrashReportsAsync(
        IReadOnlyList<string> localPaths, CancellationToken cancellationToken = default)
    {
        if (_account is null)
        {
            throw new InvalidOperationException("尚未登录阿里云盘，无法上传崩溃报告。");
        }

        if (localPaths.Count == 0) return 0;

        await using AliyunDriveSessionBundle bundle = await OpenAsync(createFolders: true, cancellationToken)
            .ConfigureAwait(false);
        if (bundle.RootFolderId is null)
        {
            throw new InvalidOperationException("云端更新目录不存在，无法上传崩溃报告。");
        }

        string folder = await bundle.Session
            .EnsureFolderAsync($"{CloudUpdateFormat.RootFolder}/private/crashes", cancellationToken)
            .ConfigureAwait(false);

        int uploaded = 0;
        foreach (string path in localPaths)
        {
            byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            await bundle.Session
                .UploadBytesAsync(folder, Path.GetFileName(path), bytes, cancellationToken)
                .ConfigureAwait(false);
            uploaded++;
        }

        AppLog.Info($"崩溃报告已上传 {uploaded} 个：/{CloudUpdateFormat.RootFolder}/private/crashes");
        return uploaded;
    }

    /// <summary>
    /// Uploads one log report to /WatermarkRemover/private/logs. Opt-in only (设置 → 日志 → 上传到云盘):
    /// the bytes are exactly what 「导出日志文件」 would write, so the log of another PC can be read
    /// from any machine logged into the same account instead of being copied off that PC by hand.
    /// </summary>
    public async Task<string> UploadLogAsync(
        string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        if (_account is null)
        {
            throw new InvalidOperationException("尚未登录阿里云盘，无法上传日志。");
        }

        await using AliyunDriveSessionBundle bundle = await OpenAsync(createFolders: true, cancellationToken)
            .ConfigureAwait(false);
        if (bundle.RootFolderId is null)
        {
            throw new InvalidOperationException("云端更新目录不存在，无法上传日志。");
        }

        string folder = await bundle.Session
            .EnsureFolderAsync($"{CloudUpdateFormat.RootFolder}/private/logs", cancellationToken)
            .ConfigureAwait(false);
        await bundle.Session.UploadBytesAsync(folder, fileName, content, cancellationToken)
            .ConfigureAwait(false);

        string cloudPath = $"/{CloudUpdateFormat.RootFolder}/private/logs/{fileName}";
        AppLog.Info($"日志已上传：{cloudPath}");
        return cloudPath;
    }

    /// <summary>
    /// Imports a pack into the stable CLI config used by the app. Encrypted packs need the passphrase
    /// chosen at export; legacy plaintext packs import without one.
    /// </summary>
    public static string ImportLoginPack(string packPath, string? passphrase = null)
    {
        string text = File.ReadAllText(packPath);
        string json = LoginPack.Unprotect(text, passphrase);
        string stable = AliyunpanCliRuntime.ImportConfig(json);
        AppLog.Info($"已导入阿里云盘登录状态：{packPath}");
        return stable;
    }

    /// <summary>
    /// Startup import: a login-pack.json beside the executable or in the stable data folder is
    /// applied when no CLI login exists yet (a fresh machine). Encrypted packs cannot be opened
    /// without a passphrase, so they are left for the settings page (which can ask for it).
    /// </summary>
    public static bool TryAutoImportLoginPack()
    {
        if (File.Exists(AliyunpanCliRuntime.StableConfigPath)) return false;

        foreach (string candidate in LoginPackCandidates())
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                if (LoginPack.IsProtected(File.ReadAllText(candidate)))
                {
                    AppLog.Info($"检测到已加密的登录包（{candidate}），需在设置→网盘设置里输入口令导入。");
                    continue;
                }

                ImportLoginPack(candidate);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"自动导入登录状态失败（{candidate}）：{ex.Message}");
            }
        }

        return false;
    }

    private static IEnumerable<string> LoginPackCandidates()
    {
        var paths = new List<string>
        {
            Path.Combine(AppFolders.Root, LoginPack.FileName),
            Path.Combine(AppContext.BaseDirectory, LoginPack.FileName),
        };
        if (Environment.ProcessPath is { } processPath && Path.GetDirectoryName(processPath) is { } exeDir)
        {
            paths.Add(Path.Combine(exeDir, LoginPack.FileName));
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public bool IsConfigured => _account is not null;

    public string AccountName => _account?.Name ?? string.Empty;

    /// <summary>Where staging, chunk cache, backups and the install log live (never inside the app dir).</summary>
    public static string StateDirectory => CloudUpdatePaths.State;

    public static string ChunkCacheDirectory => CloudUpdatePaths.ChunkCache;

    // ── check ────────────────────────────────────────────────────────────────

    /// <summary>Compares the newest cloud manifest with the running install.</summary>
    public async Task<CloudCheckResult> CheckAsync(Version current, CancellationToken cancellationToken = default)
    {
        if (_account is null)
        {
            return new CloudCheckResult(CloudCheckStatus.NotConfigured,
                "尚未登录阿里云盘，请在「设置 → 网盘设置」扫码登录一次。", current, null, null, null, null, null);
        }

        try
        {
            await using AliyunDriveSessionBundle bundle = await OpenAsync(createFolders: false, cancellationToken)
                .ConfigureAwait(false);
            if (bundle.RootFolderId is null)
            {
                return new CloudCheckResult(CloudCheckStatus.NoRelease,
                    $"云端「{CloudUpdateFormat.RootFolder}」目录还不存在（尚未发布过云更新）。", current, null, null, null, null, null);
            }

            byte[]? latestBytes = await TryDownloadCloudBytesAsync(
                bundle, bundle.RootFolderId, CloudUpdateFormat.LatestFile,
                $"/{CloudUpdateFormat.RootFolder}/{CloudUpdateFormat.LatestFile}", cancellationToken)
                .ConfigureAwait(false);
            CloudManifest? manifest = Parse<CloudManifest>(latestBytes);
            if (manifest is null)
            {
                return new CloudCheckResult(CloudCheckStatus.NoRelease,
                    "云端还没有可用的版本清单。", current, null, null, null, null, null);
            }

            CloudVersionIndex? versions = Parse<CloudVersionIndex>(
                await TryDownloadCloudBytesAsync(
                    bundle, bundle.RootFolderId, CloudUpdateFormat.VersionsFile,
                    $"/{CloudUpdateFormat.RootFolder}/{CloudUpdateFormat.VersionsFile}", cancellationToken)
                    .ConfigureAwait(false));

            Version? latest = UpdateVersion.ParseReleaseTag(manifest.Version);
            if (latest is null)
            {
                return new CloudCheckResult(CloudCheckStatus.Failed,
                    $"云端版本号无法识别：{manifest.Version}", current, null, manifest, null, null, versions);
            }

            string appDir = CloudApplyService.AppDirectory();
            string[] localPaths = manifest.Files.Select(f => f.Path).Concat(manifest.Deleted).ToArray();
            LocalFileIndex local = await Task.Run(() => BuildLocalIndex(appDir, localPaths), cancellationToken)
                .ConfigureAwait(false);
            CloudUpdatePlan plan = CloudUpdatePlanner.CreatePlan(current.ToString(), manifest, local);

            bool newer = UpdateVersion.IsNewer(current, latest);
            if (!newer)
            {
                return new CloudCheckResult(CloudCheckStatus.UpToDate,
                    $"当前已是最新版本 v{current}（云端最新 v{latest}）。", current, latest, manifest, plan, local, versions);
            }

            string message = plan.HasChanges
                ? $"发现新版本 v{latest}：需下载 {plan.ChangedFiles.Count} 个文件 / {FormatBytes(plan.DownloadBytes)}，" +
                  $"另有 {plan.DeletedFiles.Count} 个文件删除。"
                : $"云端已发布 v{latest}，但文件内容与当前一致。";
            return new CloudCheckResult(CloudCheckStatus.UpdateAvailable, message,
                current, latest, manifest, plan, local, versions);
        }
        catch (Exception ex)
        {
            AppLog.Error("云更新检查失败", ex);
            return new CloudCheckResult(CloudCheckStatus.Failed,
                $"连接阿里云盘失败：{ex.Message}", current, null, null, null, null, null);
        }
    }

    /// <summary>
    /// Builds a plan that goes to an older retained version. Files introduced after that version
    /// must be removed, so the plan's delete list is widened by the current version's manifest.
    /// </summary>
    public async Task<CloudCheckResult> CheckVersionAsync(
        Version current, string targetVersion, CancellationToken cancellationToken = default)
    {
        if (_account is null)
        {
            return new CloudCheckResult(CloudCheckStatus.NotConfigured,
                "尚未登录阿里云盘，请先在「设置 → 网盘设置」扫码登录一次。", current, null, null, null, null, null);
        }

        try
        {
            await using AliyunDriveSessionBundle bundle = await OpenAsync(createFolders: false, cancellationToken)
                .ConfigureAwait(false);
            if (bundle.RootFolderId is null || string.IsNullOrEmpty(bundle.HistoryFolderId))
            {
                return new CloudCheckResult(CloudCheckStatus.Failed, "云端更新目录不存在。", current, null, null, null, null, null);
            }

            CloudManifest? target = Parse<CloudManifest>(await TryDownloadCloudBytesAsync(
                bundle, bundle.HistoryFolderId, $"{targetVersion}.json",
                $"/{CloudUpdateFormat.RootFolder}/{CloudUpdateFormat.HistoryFolder}/{targetVersion}.json",
                cancellationToken).ConfigureAwait(false));
            Version? targetParsed = UpdateVersion.ParseReleaseTag(target?.Version);
            if (target is null || targetParsed is null)
            {
                return new CloudCheckResult(CloudCheckStatus.Failed,
                    $"云端没有 v{targetVersion} 的历史清单。", current, null, null, null, null, null);
            }

            CloudManifest? currentManifest = Parse<CloudManifest>(await TryDownloadCloudBytesAsync(
                bundle, bundle.RootFolderId, CloudUpdateFormat.LatestFile,
                $"/{CloudUpdateFormat.RootFolder}/{CloudUpdateFormat.LatestFile}", cancellationToken)
                .ConfigureAwait(false));
            if (currentManifest is null || !string.Equals(currentManifest.Version, current.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                currentManifest = Parse<CloudManifest>(await TryDownloadCloudBytesAsync(
                    bundle, bundle.HistoryFolderId, $"{current}.json",
                    $"/{CloudUpdateFormat.RootFolder}/{CloudUpdateFormat.HistoryFolder}/{current}.json",
                    cancellationToken).ConfigureAwait(false));
            }

            if (currentManifest is not null)
            {
                var targetPaths = target.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var removed = currentManifest.Files
                    .Select(f => f.Path)
                    .Where(p => !targetPaths.Contains(p))
                    .ToList();
                target.Deleted = target.Deleted
                    .Concat(removed)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            string appDir = CloudApplyService.AppDirectory();
            string[] localPaths = target.Files.Select(f => f.Path).Concat(target.Deleted).ToArray();
            LocalFileIndex local = await Task.Run(() => BuildLocalIndex(appDir, localPaths), cancellationToken)
                .ConfigureAwait(false);
            CloudUpdatePlan plan = CloudUpdatePlanner.CreatePlan(current.ToString(), target, local);
            CloudVersionIndex? versions = Parse<CloudVersionIndex>(await TryDownloadCloudBytesAsync(
                bundle, bundle.RootFolderId, CloudUpdateFormat.VersionsFile,
                $"/{CloudUpdateFormat.RootFolder}/{CloudUpdateFormat.VersionsFile}", cancellationToken)
                .ConfigureAwait(false));

            return new CloudCheckResult(CloudCheckStatus.UpdateAvailable,
                $"将回滚到 v{targetVersion}：需下载 {plan.ChangedFiles.Count} 个文件 / {FormatBytes(plan.DownloadBytes)}。",
                current, targetParsed, target, plan, local, versions);
        }
        catch (Exception ex)
        {
            AppLog.Error($"云更新回滚检查 v{targetVersion} 失败", ex);
            return new CloudCheckResult(CloudCheckStatus.Failed, $"连接阿里云盘失败：{ex.Message}",
                current, null, null, null, null, null);
        }
    }

    // ── download + stage ─────────────────────────────────────────────────────

    /// <summary>
    /// Downloads the chunks the local install does not already contain, reassembles the changed
    /// files into a staging directory and verifies every file hash.
    /// </summary>
    public async Task<CloudStageResult> StageAsync(
        CloudCheckResult check,
        IProgress<CloudUpdateProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (check.Manifest is null || check.Plan is null || check.LocalIndex is null)
        {
            throw new InvalidOperationException("没有可用的云更新计划，请先检查更新。");
        }

        CloudUpdatePlan plan = check.Plan;
        if (!plan.HasChanges)
        {
            return new CloudStageResult(check, string.Empty, Array.Empty<string>(), Array.Empty<string>(), 0, 0);
        }

        await using AliyunDriveSessionBundle bundle = await OpenAsync(createFolders: false, cancellationToken)
            .ConfigureAwait(false);
        if (bundle.RootFolderId is null)
        {
            throw new InvalidOperationException("云端更新目录不存在。");
        }

        Directory.CreateDirectory(ChunkCacheDirectory);
        string staging = Path.Combine(CloudUpdatePaths.Staging, $"{check.Manifest.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        // The manifest stores chunk hashes, not drive file ids, so the ids come from ONE folder
        // listing. That request used to be single-shot: when it was throttled or timed out the
        // map stayed empty and EVERY chunk dropped into the bundled CLI, which is what turned a
        // 10 MB update into minutes (one process, 40-75 s, per chunk). It is now retried, and a
        // chunk that is still absent afterwards is a broken cloud copy, not a slow download.
        Dictionary<string, string> remote = new(StringComparer.Ordinal);
        if (plan.RequiredChunks.Count > 0 && !string.IsNullOrEmpty(bundle.ChunksFolderId))
        {
            remote = await ReadChunkIdsAsync(bundle.Session, bundle.ChunksFolderId,
                plan.RequiredChunks.Count, plan.DownloadBytes, progress, cancellationToken).ConfigureAwait(false);

            List<CloudChunkRef> missing = plan.RequiredChunks
                .Where(chunk => !remote.ContainsKey(chunk.Sha256)).ToList();
            if (missing.Count > 0)
            {
                // A publish that finished while this check was running can leave the listing one
                // write behind. One fresh read decides; a second miss is a real gap.
                AppLog.Warn($"云更新：分块列表缺少 {missing.Count} 个分块，重新读取一次列表");
                remote = await ReadChunkIdsAsync(bundle.Session, bundle.ChunksFolderId,
                    plan.RequiredChunks.Count, plan.DownloadBytes, progress, cancellationToken).ConfigureAwait(false);

                missing = plan.RequiredChunks
                    .Where(chunk => !remote.ContainsKey(chunk.Sha256)).ToList();
                if (missing.Count > 0)
                {
                    throw new InvalidDataException(
                        $"云端缺少 {missing.Count} 个更新分块（例如 {missing[0].Sha256[..12]}…），" +
                        "本次更新已放弃，请稍后重新检查更新。");
                }
            }
        }

        long totalBytes = plan.DownloadBytes;
        // Two at a time, not three: the CDN answers extra concurrent transfers on one account
        // with a 403 "ExceedMaxConcurrency" (app-20261112.log 17:48). Such a refusal is
        // transient, so it is retried with a freshly signed link — never handed to the CLI,
        // whose per-process startup (40-75 s) was the real cost of every update.
        const int ApiDownloadAttempts = 4;

        /// <summary>
        /// Attempts spent on a CDN concurrency rejection before giving the channel up. The
        /// quota belongs to the egress IP, not to us, so a third try cannot succeed where two
        /// just failed — the GitHub fallback is the real answer (CloudUpdateCoordinator).
        /// </summary>
        const int ThrottleDownloadAttempts = 2;
        long[] downloaded = new long[1];
        await Parallel.ForEachAsync(
            plan.RequiredChunks,
            new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancellationToken },
            async (chunk, token) =>
        {
            string cachePath = Path.Combine(ChunkCacheDirectory, chunk.Sha256);
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length == chunk.Size)
            {
                ReportProgress(progress, Interlocked.Add(ref downloaded[0], chunk.Size), totalBytes, null);
                return;
            }

            string temp = cachePath + ".part";
            for (int attempt = 1; ; attempt++)
            {
                long apiTicks = Stopwatch.GetTimestamp();
                try
                {
                    // 镜像优先（files/<sha256>，与云盘同布局）：直连无 CDN 限流。镜像失败时
                    // TryDownloadToFileAsync 已把 .part 清掉，这里直接走云盘通道。
                    if (_mirror is not null &&
                        await _mirror.TryDownloadToFileAsync($"files/{chunk.Sha256}", temp, token).ConfigureAwait(false))
                    {
                        break;
                    }

                    // No extra total deadline here: DownloadAsync already bounds the link lookup
                    // (90 s) and treats 30 s without bytes as stalled.
                    await bundle.Session.DownloadAsync(remote[chunk.Sha256], temp, null, token).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (!IsCallerCancellation(ex, token))
                {
                    try { File.Delete(temp); } catch { /* best effort */ }

                    // A CDN concurrency rejection is a quota problem, not a transient fault:
                    // on a shared egress IP (hotel/office NAT, 出差外地) the quota is consumed by
                    // other Aliyun users, so EVERY retry bounces back in ~200 ms no matter how
                    // long we wait. Two quick tries prove it is persistent, then fail so the
                    // caller can switch to the GitHub channel instead of parking the user here.
                    bool throttled = IsConcurrencyThrottle(ex);
                    int maxAttempts = throttled ? ThrottleDownloadAttempts : ApiDownloadAttempts;
                    if (attempt >= maxAttempts)
                    {
                        throw new InvalidDataException(
                            throttled
                                ? $"下载分块 {chunk.Sha256[..12]}… 被阿里云盘 CDN 持续限流" +
                                  $"（HTTP 403 ExceedMaxConcurrency，重试 {maxAttempts} 次均被拒）：" +
                                  "当前网络出口的并发配额已被占满，换网络或稍后再试。"
                                : $"下载分块 {chunk.Sha256[..12]}… 失败（已重试 {ApiDownloadAttempts} 次，" +
                                  $"最后一次耗时 {ElapsedMs(apiTicks)} ms）：{ex.Message}", ex);
                    }

                    long delayMs = throttled ? 2000L * attempt : 500L << (attempt - 1);
                    AppLog.Warn($"云更新：下载分块 {chunk.Sha256[..12]}… 第 {attempt} 次失败" +
                                $"（耗时 {ElapsedMs(apiTicks)} ms），{delayMs} ms 后重试：{ex.Message}");
                    await Task.Delay(TimeSpan.FromMilliseconds(delayMs), token).ConfigureAwait(false);
                }
            }

            if (new FileInfo(temp).Length != chunk.Size ||
                !string.Equals(CloudHash.Sha256HexFile(temp), chunk.Sha256, StringComparison.Ordinal))
            {
                File.Delete(temp);
                throw new InvalidDataException($"分块 {chunk.Sha256[..12]}… 校验失败，已放弃本次更新。");
            }

            File.Move(temp, cachePath, overwrite: true);
            ReportProgress(progress, Interlocked.Add(ref downloaded[0], chunk.Size), totalBytes, null);
        }).ConfigureAwait(false);

        progress?.Report(new CloudUpdateProgress(CloudUpdateStage.Assembling, 100, downloaded[0], totalBytes,
            $"正在组装 {plan.ChangedFiles.Count} 个文件…"));
        string appDir = CloudApplyService.AppDirectory();
        foreach (CloudManifestFile file in plan.ChangedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = LocalFileIndex.Normalize(file.Path);
            string target = LocalFileIndex.ToLocalPath(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CloudStagingAssembler.AssembleFile(
                file, appDir, check.LocalIndex, ChunkCacheDirectory, target);
        }

        progress?.Report(new CloudUpdateProgress(CloudUpdateStage.Ready, 100, downloaded[0], totalBytes, null));
        AppLog.Info($"云更新暂存完成：v{check.Manifest.Version}，下载 {FormatBytes(downloaded[0])}，" +
                    $"变更 {plan.ChangedFiles.Count} 个文件、删除 {plan.DeletedFiles.Count} 个。");

        return new CloudStageResult(check, staging,
            plan.ChangedFiles.Select(f => LocalFileIndex.Normalize(f.Path)).ToList(),
            plan.DeletedFiles.ToList(),
            downloaded[0],
            plan.ChangedBytes);
    }

    // ── apply + rollback ─────────────────────────────────────────────────────

    /// <summary>Hands the staged files to the detached apply script (see CloudApplyService).</summary>
    public void ApplyAndRestart(CloudStageResult staged, string direction, string note) =>
        CloudApplyService.ApplyAndRestart(staged, direction, note);

    /// <summary>The background script's verdict, folded into the install history at startup.</summary>
    public static CloudApplyResult? ReconcileLastApply() => CloudApplyService.ReconcileLastApply();

    public static IReadOnlyList<CloudUpdateHistoryEntry> ReadHistory() => CloudApplyService.ReadHistory();

    // local index (hash cache)

    /// <summary>Hashes the given files, reusing the on-disk cache (see LocalFileIndexService).</summary>
    private static LocalFileIndex BuildLocalIndex(string appDir, IEnumerable<string> paths) =>
        LocalFileIndexService.Build(appDir, paths);

    // ── cloud downloads: API first, bundled aliyunpan CLI as fallback ─────────

    /// <summary>
    /// Reads the chunks folder once, retrying the whole listing: it is a single request and is
    /// exactly as throttle-prone as any other (the first 403 "ExceedMaxConcurrency" arrives on
    /// whichever call is in flight). Handing a failed listing to per-chunk fallbacks is what
    /// made every chunk pay a CLI process startup.
    /// </summary>
    private static async Task<Dictionary<string, string>> ReadChunkIdsAsync(
        INetdiskSession session,
        string folderId,
        int required,
        long totalBytes,
        IProgress<CloudUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        const int attempts = 4;
        Exception? last = null;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            progress?.Report(new CloudUpdateProgress(CloudUpdateStage.Downloading, 0, 0, totalBytes,
                $"正在读取云端分块列表（缺少 {required} 个）…"));
            long ticks = Stopwatch.GetTimestamp();
            try
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (NetdiskFile item in await session.ListAllAsync(folderId, cancellationToken)
                             .ConfigureAwait(false))
                {
                    if (!item.IsFolder && item.Name.Length == 64)
                    {
                        map[item.Name.ToLowerInvariant()] = item.Id;
                    }
                }

                if (map.Count > 0) return map;
                last = new InvalidDataException("云端分块目录为空。");
            }
            catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
            {
                last = ex;
            }

            AppLog.Warn($"云更新：读取分块列表第 {attempt}/{attempts} 次失败" +
                        $"（耗时 {ElapsedMs(ticks)} ms）：{last?.Message}");
            if (attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidDataException($"读取云端分块列表失败：{last?.Message}", last);
    }

    /// <summary>
    /// True for the CDN's concurrency rejection (HTTP 403, RequestDeniedByCallback /
    /// ExceedMaxConcurrency). Distinguished from ordinary network faults because retrying it
    /// on the same egress IP is pointless — see <see cref="StageAsync"/>.
    /// </summary>
    private static bool IsConcurrencyThrottle(Exception ex) =>
        ex.Message.Contains("RequestDeniedByCallback", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("ExceedMaxConcurrency", StringComparison.OrdinalIgnoreCase);

    private static bool IsCallerCancellation(Exception ex, CancellationToken cancellationToken) =>
        ex is OperationCanceledException && cancellationToken.IsCancellationRequested;

    /// <summary>Wall time of one download attempt, so a fallback line says how long it burned.</summary>
    private static long ElapsedMs(long startTicks) =>
        (long)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;

    /// <summary>Manifest download: HTTP mirror first, signed CDN with CLI fallback after it.</summary>
    private async Task<byte[]?> TryDownloadCloudBytesAsync(
        AliyunDriveSessionBundle bundle,
        string? folderId,
        string name,
        string remotePath,
        CancellationToken cancellationToken)
    {
        // 镜像优先：同一目录布局（latest.json / versions.json / history/…），直连国内服务器，
        // 没有 CDN 限流和签名直链往返。失败（超时/非 2xx）静默回退云盘。
        if (_mirror is not null)
        {
            byte[]? mirrored = await _mirror.TryGetBytesAsync(name, cancellationToken).ConfigureAwait(false);
            if (mirrored is not null) return mirrored;
        }

        return await CloudDownloader.TryDownloadBytesAsync(
            bundle.Session, folderId, name, remotePath, StateDirectory, cancellationToken).ConfigureAwait(false);
    }

    // ── connection helpers ───────────────────────────────────────────────────

    private async Task<AliyunDriveSessionBundle> OpenAsync(bool createFolders, CancellationToken cancellationToken)
    {
        var provider = new AliyunDriveProvider();
        INetdiskSession session = await provider.ConnectAsync(_account!.ToCredentials(), cancellationToken)
            .ConfigureAwait(false);
        try
        {
            NetdiskDrive? drive = session.Drives.FirstOrDefault(d => d.Name == DriveName);
            if (drive is not null) await session.SwitchDriveAsync(drive.Id, cancellationToken).ConfigureAwait(false);

            string? rootId = createFolders
                ? await session.EnsureFolderAsync(CloudUpdateFormat.RootFolder, cancellationToken).ConfigureAwait(false)
                : await session.FindFolderAsync(CloudUpdateFormat.RootFolder, cancellationToken).ConfigureAwait(false);
            string? chunksId = rootId is null
                ? null
                : createFolders
                    ? await session.EnsureFolderAsync($"{CloudUpdateFormat.RootFolder}/{CloudUpdateFormat.ChunksFolder}", cancellationToken).ConfigureAwait(false)
                    : await session.FindFolderAsync($"{CloudUpdateFormat.RootFolder}/{CloudUpdateFormat.ChunksFolder}", cancellationToken).ConfigureAwait(false);
            string? historyId = rootId is null
                ? null
                : createFolders
                    ? await session.EnsureFolderAsync($"{CloudUpdateFormat.RootFolder}/{CloudUpdateFormat.HistoryFolder}", cancellationToken).ConfigureAwait(false)
                    : await session.FindFolderAsync($"{CloudUpdateFormat.RootFolder}/{CloudUpdateFormat.HistoryFolder}", cancellationToken).ConfigureAwait(false);

            PersistRotatedCredentials(session.CurrentCredentials);
            return new AliyunDriveSessionBundle(session, rootId, chunksId ?? string.Empty, historyId ?? string.Empty);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void PersistRotatedCredentials(NetdiskCredentials credentials)
    {
        // CLI-hosted credentials are renewed by the CLI's own config; there is no app account to
        // write back.
        if (string.Equals(_account?.Values.GetValueOrDefault(AliyunpanCliRuntime.CliLoginKey),
                "true", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var accounts = NetdiskAccountPersistence.Decode(_settings.NetdiskAccounts, _protector);
            int index = accounts.FindIndex(a => a.ProviderId == ProviderId && a.Name == _account!.Name);
            if (index < 0) return;
            if (accounts[index].Values.SequenceEqual(credentials.Values)) return;

            accounts[index] = new NetdiskAccountState(ProviderId, _account!.Name, credentials.Values);
            _settings.NetdiskAccounts = NetdiskAccountPersistence.Encode(
                accounts, new INetdiskProvider[] { new AliyunDriveProvider() }, _protector);
            SettingsService.Save(_settings);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"保存网盘续期令牌失败：{ex.Message}");
        }
    }

    private sealed class AliyunDriveSessionBundle : IAsyncDisposable
    {
        public AliyunDriveSessionBundle(INetdiskSession session, string? rootFolderId, string chunksFolderId, string historyFolderId)
        {
            Session = session;
            RootFolderId = rootFolderId;
            ChunksFolderId = chunksFolderId;
            HistoryFolderId = historyFolderId;
        }

        public INetdiskSession Session { get; }
        public string? RootFolderId { get; }
        public string ChunksFolderId { get; }
        public string HistoryFolderId { get; }

        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    // ── small helpers ────────────────────────────────────────────────────────

    private static T? Parse<T>(byte[]? bytes) where T : class =>
        bytes is null ? null : CloudUpdateFormat.TryParse<T>(Encoding.UTF8.GetString(bytes));

    private static void ReportProgress(IProgress<CloudUpdateProgress>? progress, long done, long total, string? message)
    {
        double? percent = total > 0 ? Math.Min(100, done * 100.0 / total) : null;
        progress?.Report(new CloudUpdateProgress(CloudUpdateStage.Downloading, percent, done, total, message));
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / 1073741824.0:F2} GB",
        >= 1L << 20 => $"{bytes / 1048576.0:F1} MB",
        >= 1L << 10 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B",
    };
}
