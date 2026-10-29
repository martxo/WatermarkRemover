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

internal sealed class CloudApplyPlan
{
    public string AppDir { get; set; } = string.Empty;
    public string StagingDir { get; set; } = string.Empty;
    public string BackupDir { get; set; } = string.Empty;
    public string ResultPath { get; set; } = string.Empty;
    public string ExePath { get; set; } = string.Empty;
    public string FromVersion { get; set; } = string.Empty;
    public string ToVersion { get; set; } = string.Empty;
    public string Direction { get; set; } = "update";
    public string Note { get; set; } = string.Empty;
    public int WaitPid { get; set; }
    public List<string> Files { get; set; } = new();
    public List<string> Deletes { get; set; } = new();
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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AppSettings _settings;
    private readonly ISecretProtector _protector;
    private readonly NetdiskAccountState? _account;

    public CloudUpdateService(AppSettings settings, ISecretProtector protector)
    {
        _settings = settings;
        _protector = protector;
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
    public static string StateDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WatermarkRemover", "cloud-updates");

    public static string ChunkCacheDirectory => Path.Combine(StateDirectory, "chunks");

    private static string StagingRoot => Path.Combine(StateDirectory, "staging");
    private static string BackupRoot => Path.Combine(StateDirectory, "backup");
    private static string LocalIndexPath => Path.Combine(StateDirectory, "local-index.json");
    private static string HistoryPath => Path.Combine(StateDirectory, "history.json");
    private static string ResultPath => Path.Combine(StateDirectory, "last-apply.json");

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

            string appDir = AppDirectory();
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

            string appDir = AppDirectory();
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
        string staging = Path.Combine(StagingRoot, $"{check.Manifest.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        // Remote chunk ids are only listed when something actually has to be fetched. A listing
        // failure is not fatal: every chunk can still be fetched by path through the CLI.
        Dictionary<string, string> remote = new(StringComparer.Ordinal);
        if (plan.RequiredChunks.Count > 0 && !string.IsNullOrEmpty(bundle.ChunksFolderId))
        {
            progress?.Report(new CloudUpdateProgress(CloudUpdateStage.Downloading, 0, 0, plan.DownloadBytes,
                $"正在读取云端分块列表（缺少 {plan.RequiredChunks.Count} 个）…"));
            long listTicks = Stopwatch.GetTimestamp();
            try
            {
                foreach (NetdiskFile item in await bundle.Session.ListAllAsync(bundle.ChunksFolderId, cancellationToken)
                             .ConfigureAwait(false))
                {
                    if (!item.IsFolder && item.Name.Length == 64)
                    {
                        remote[item.Name.ToLowerInvariant()] = item.Id;
                    }
                }
            }
            catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
            {
                AppLog.Warn($"云更新：读取分块列表失败（耗时 {ElapsedMs(listTicks)} ms），" +
                            $"改用 CLI 逐个下载：{ex.Message}");
            }
        }

        long totalBytes = plan.DownloadBytes;
        long downloaded = 0;
        foreach (CloudChunkRef chunk in plan.RequiredChunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string cachePath = Path.Combine(ChunkCacheDirectory, chunk.Sha256);
            if (File.Exists(cachePath) && new FileInfo(cachePath).Length == chunk.Size)
            {
                downloaded += chunk.Size;
                ReportProgress(progress, downloaded, totalBytes, null);
                continue;
            }

            string temp = cachePath + ".part";
            bool fetched = false;
            if (remote.TryGetValue(chunk.Sha256, out string? fileId))
            {
                long apiTicks = Stopwatch.GetTimestamp();
                try
                {
                    using var chunkTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    chunkTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                    await bundle.Session.DownloadAsync(fileId, temp, null, chunkTimeout.Token).ConfigureAwait(false);
                    fetched = true;
                }
                catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
                {
                    // The elapsed time is what separates "CDN stalled" from "endpoint rejected".
                    AppLog.Warn($"云更新：接口下载分块 {chunk.Sha256[..12]}… 失败" +
                                $"（耗时 {ElapsedMs(apiTicks)} ms），改用 CLI：{ex.Message}");
                    try { File.Delete(temp); } catch { /* best effort */ }
                }
            }

            if (!fetched)
            {
                long cliTicks = Stopwatch.GetTimestamp();
                try
                {
                    await CloudDownloader.DownloadChunkViaCliAsync(
                        $"/{CloudUpdateFormat.RootFolder}/{CloudUpdateFormat.ChunksFolder}/{chunk.Sha256}",
                        temp, StateDirectory, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!IsCallerCancellation(ex, cancellationToken))
                {
                    try { File.Delete(temp); } catch { /* best effort */ }
                    throw new InvalidDataException(
                        $"下载分块 {chunk.Sha256[..12]}… 失败（CLI 耗时 {ElapsedMs(cliTicks)} ms）：{ex.Message}", ex);
                }

                AppLog.Debug($"云更新：CLI 下载分块 {chunk.Sha256[..12]}… 耗时 {ElapsedMs(cliTicks)} ms");
            }
            if (new FileInfo(temp).Length != chunk.Size || !string.Equals(HashFile(temp), chunk.Sha256, StringComparison.Ordinal))
            {
                File.Delete(temp);
                throw new InvalidDataException($"分块 {chunk.Sha256[..12]}… 校验失败，已放弃本次更新。");
            }

            File.Move(temp, cachePath, overwrite: true);
            downloaded += chunk.Size;
            ReportProgress(progress, downloaded, totalBytes, null);
        }

        progress?.Report(new CloudUpdateProgress(CloudUpdateStage.Assembling, 100, downloaded, totalBytes,
            $"正在组装 {plan.ChangedFiles.Count} 个文件…"));
        string appDir = AppDirectory();
        foreach (CloudManifestFile file in plan.ChangedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = LocalFileIndex.Normalize(file.Path);
            string target = LocalFileIndex.ToLocalPath(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CloudStagingAssembler.AssembleFile(
                file, appDir, check.LocalIndex, ChunkCacheDirectory, target);
        }

        progress?.Report(new CloudUpdateProgress(CloudUpdateStage.Ready, 100, downloaded, totalBytes, null));
        AppLog.Info($"云更新暂存完成：v{check.Manifest.Version}，下载 {FormatBytes(downloaded)}，" +
                    $"变更 {plan.ChangedFiles.Count} 个文件、删除 {plan.DeletedFiles.Count} 个。");

        return new CloudStageResult(check, staging,
            plan.ChangedFiles.Select(f => LocalFileIndex.Normalize(f.Path)).ToList(),
            plan.DeletedFiles.ToList(),
            downloaded,
            plan.ChangedBytes);
    }

    // ── apply + rollback ─────────────────────────────────────────────────────

    /// <summary>
    /// Schedules the staged files to replace the install after this process exits. The caller
    /// shuts the application down right after this returns; the script relaunches it.
    /// </summary>
    public void ApplyAndRestart(CloudStageResult staged, string direction, string note)
    {
        if (string.IsNullOrEmpty(staged.StagingDirectory) || !Directory.Exists(staged.StagingDirectory))
        {
            throw new InvalidOperationException("暂存目录不存在，无法应用更新。");
        }

        string appDir = AppDirectory();
        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("无法确定当前程序路径。");
        string guid = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(StateDirectory);
        string backup = Path.Combine(BackupRoot, guid);
        Directory.CreateDirectory(backup);

        var plan = new CloudApplyPlan
        {
            AppDir = appDir,
            StagingDir = staged.StagingDirectory,
            BackupDir = backup,
            ResultPath = ResultPath,
            ExePath = exe,
            FromVersion = staged.Check.Current.ToString(),
            ToVersion = staged.Check.Latest?.ToString() ?? staged.Check.Manifest?.Version ?? string.Empty,
            Direction = direction,
            Note = note,
            WaitPid = Environment.ProcessId,
            Files = staged.Files.ToList(),
            Deletes = staged.Deletes.ToList(),
        };

        string planPath = Path.Combine(StateDirectory, $"apply-{guid}.json");
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan, JsonOptions), new UTF8Encoding(false));

        AppendHistory(new CloudUpdateHistoryEntry
        {
            At = DateTimeOffset.Now.ToString("O"),
            FromVersion = plan.FromVersion,
            ToVersion = plan.ToVersion,
            Direction = direction,
            Note = note,
            Status = "应用中",
            FileCount = plan.Files.Count,
            Bytes = staged.DownloadedBytes,
        });

        string scriptPath = Path.Combine(StateDirectory, $"apply-{guid}.ps1");
        File.WriteAllText(scriptPath, BuildApplyScript(), new UTF8Encoding(true));

        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -PlanPath \"{planPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (Process.Start(start) is null)
        {
            throw new InvalidOperationException("无法启动更新脚本。");
        }

        AppLog.Info($"云更新已交给后台脚本：{plan.FromVersion} → {plan.ToVersion}（{direction}），" +
                    $"{plan.Files.Count} 个文件、下载 {FormatBytes(staged.DownloadedBytes)}。");
    }

    private static string BuildApplyScript() => """
        param([string]$PlanPath)
        $ErrorActionPreference = 'Stop'
        $plan = Get-Content -LiteralPath $PlanPath -Raw -Encoding UTF8 | ConvertFrom-Json
        function Copy-WithRetry([string]$Source, [string]$Target) {
            for ($i = 0; $i -lt 6; $i++) {
                try {
                    Copy-Item -LiteralPath $Source -Destination $Target -Force
                    return
                } catch {
                    if ($i -eq 5) { throw }
                    Start-Sleep -Milliseconds 500
                }
            }
        }
        $deadline = (Get-Date).AddMinutes(3)
        while ((Get-Process -Id $plan.waitPid -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline)) {
            Start-Sleep -Milliseconds 400
        }
        Start-Sleep -Milliseconds 400

        New-Item -ItemType Directory -Force -Path $plan.backupDir | Out-Null
        $ok = $true
        $msg = ''
        try {
            foreach ($rel in $plan.files) {
                $target = Join-Path $plan.appDir $rel
                if (Test-Path -LiteralPath $target) {
                    $bak = Join-Path $plan.backupDir $rel
                    New-Item -ItemType Directory -Force -Path (Split-Path $bak -Parent) | Out-Null
                    Copy-WithRetry $target $bak
                }
                $src = Join-Path $plan.stagingDir $rel
                New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
                Copy-WithRetry $src $target
            }
            foreach ($rel in $plan.deletes) {
                $target = Join-Path $plan.appDir $rel
                if (Test-Path -LiteralPath $target) {
                    $bak = Join-Path $plan.backupDir $rel
                    New-Item -ItemType Directory -Force -Path (Split-Path $bak -Parent) | Out-Null
                    Copy-WithRetry $target $bak
                    Remove-Item -LiteralPath $target -Force
                }
            }

            # Keep Velopack's install metadata in step with the files we just replaced; otherwise
            # the next startup sees sq.version older than the running assembly.
            $sq = Join-Path $plan.appDir 'sq.version'
            if (Test-Path -LiteralPath $sq) {
                Copy-WithRetry $sq (Join-Path $plan.backupDir 'sq.version')
                $xml = Get-Content -LiteralPath $sq -Raw
                $xml = [regex]::Replace($xml, '<version>[^<]+</version>', "<version>$($plan.toVersion)</version>")
                [IO.File]::WriteAllText($sq, $xml, [Text.UTF8Encoding]::new($false))
            }
        } catch {
            $ok = $false
            $msg = $_.Exception.Message
            try {
                foreach ($rel in $plan.files) {
                    $target = Join-Path $plan.appDir $rel
                    $bak = Join-Path $plan.backupDir $rel
                    if (Test-Path -LiteralPath $bak) {
                        Copy-Item -LiteralPath $bak -Destination $target -Force
                    } else {
                        Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
                    }
                }
                foreach ($rel in $plan.deletes) {
                    $bak = Join-Path $plan.backupDir $rel
                    if (Test-Path -LiteralPath $bak) {
                        Copy-Item -LiteralPath $bak -Destination (Join-Path $plan.appDir $rel) -Force
                    }
                }
                $bakSq = Join-Path $plan.backupDir 'sq.version'
                if (Test-Path -LiteralPath $bakSq) {
                    Copy-Item -LiteralPath $bakSq -Destination (Join-Path $plan.appDir 'sq.version') -Force
                }
            } catch { }
        }

        $result = @{
            success = $ok
            message = $msg
            toVersion = $plan.toVersion
            fromVersion = $plan.fromVersion
            finishedAt = (Get-Date).ToString('O')
        } | ConvertTo-Json
        [IO.File]::WriteAllText($plan.resultPath, $result, [Text.UTF8Encoding]::new($false))
        Remove-Item -LiteralPath $plan.stagingDir -Recurse -Force -ErrorAction SilentlyContinue
        Start-Process -FilePath $plan.exePath
        Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        """;

    /// <summary>
    /// Called once at startup: folds the background script's result into the install history,
    /// deletes the consumed result file and prunes old staging/backup folders.
    /// </summary>
    public static CloudApplyResult? ReconcileLastApply()
    {
        CloudApplyResult? result = null;
        try
        {
            if (File.Exists(ResultPath))
            {
                result = JsonSerializer.Deserialize<CloudApplyResult>(File.ReadAllText(ResultPath), JsonOptions);
                File.Delete(ResultPath);
            }

            if (result is not null)
            {
                var history = ReadHistory();
                var entry = history.LastOrDefault(e => e.Status == "应用中");
                if (entry is not null)
                {
                    entry.Status = result.Success ? "成功" : "已还原";
                    entry.Message = result.Message;
                    WriteHistory(history);
                }

                AppLog.Info(result.Success
                    ? $"云更新完成：v{result.FromVersion} → v{result.ToVersion}"
                    : $"云更新失败，已还原 v{result.FromVersion}：{result.Message}");
            }

            CleanupStagingAndBackups(result?.Success == true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取云更新结果失败：{ex.Message}");
        }

        return result;
    }

    public static IReadOnlyList<CloudUpdateHistoryEntry> ReadHistory()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return Array.Empty<CloudUpdateHistoryEntry>();
            return JsonSerializer.Deserialize<List<CloudUpdateHistoryEntry>>(File.ReadAllText(HistoryPath), JsonOptions)
                ?? new List<CloudUpdateHistoryEntry>();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取云更新记录失败：{ex.Message}");
            return Array.Empty<CloudUpdateHistoryEntry>();
        }
    }

    private static void AppendHistory(CloudUpdateHistoryEntry entry)
    {
        var history = ReadHistory().ToList();
        history.Add(entry);
        if (history.Count > 30) history = history.Skip(history.Count - 30).ToList();
        WriteHistory(history);
    }

    private static void WriteHistory(IEnumerable<CloudUpdateHistoryEntry> history)
    {
        Directory.CreateDirectory(StateDirectory);
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(history, JsonOptions), new UTF8Encoding(false));
    }

    private static void CleanupStagingAndBackups(bool applySucceeded)
    {
        try
        {
            if (Directory.Exists(StagingRoot))
            {
                foreach (string dir in Directory.GetDirectories(StagingRoot))
                {
                    try { Directory.Delete(dir, recursive: true); } catch { /* in use / best effort */ }
                }
            }

            if (!applySucceeded || !Directory.Exists(BackupRoot)) return;
            foreach (string dir in Directory.GetDirectories(BackupRoot)
                         .OrderByDescending(Directory.GetLastWriteTimeUtc)
                         .Skip(CloudUpdateFormat.DefaultKeep))
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"清理云更新缓存失败：{ex.Message}");
        }
    }

    // ── local index (hash cache) ─────────────────────────────────────────────

    private static LocalFileIndex BuildLocalIndex(string appDir, IEnumerable<string> paths)
    {
        var cache = ReadLocalIndexCache();
        string[] wanted = paths.Select(LocalFileIndex.Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var index = new LocalFileIndex();
        bool dirty = false;

        foreach (string relative in wanted)
        {
            string path = LocalFileIndex.ToLocalPath(appDir, relative);
            if (!File.Exists(path)) continue;

            var info = new FileInfo(path);
            if (cache.TryGetValue(relative, out CachedFile? cached) &&
                cached.Size == info.Length &&
                cached.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks)
            {
                index.Files[relative] = new LocalFileRecord(relative, cached.Size, cached.Sha256,
                    cached.Chunks.Select(c => new LocalChunkRef(relative, c.Offset, c.Length)).ToList());
                continue;
            }

            byte[] bytes = File.ReadAllBytes(path);
            var chunks = ContentChunker.Split(bytes);
            index.Files[relative] = new LocalFileRecord(relative, bytes.Length, Hash(bytes),
                chunks.Select(c => new LocalChunkRef(relative, c.Offset, c.Length)).ToList());
            cache[relative] = new CachedFile
            {
                Size = bytes.Length,
                LastWriteUtcTicks = info.LastWriteTimeUtc.Ticks,
                Sha256 = index.Files[relative].Sha256,
                Chunks = chunks.Select(c => new CachedChunk(c.Sha256, c.Offset, c.Length)).ToList(),
            };
            dirty = true;
        }

        // Build the global chunk → location map once, from every file (cached or fresh).
        index.Chunks.Clear();
        foreach (LocalFileRecord record in index.Files.Values)
        {
            foreach (LocalChunkRef chunk in record.Chunks)
            {
                string sha = ChunkHashAt(record, chunk, appDir, cache);
                if (sha.Length > 0) index.Chunks[sha] = chunk;
            }
        }

        if (dirty) WriteLocalIndexCache(cache);
        return index;
    }

    private static string ChunkHashAt(
        LocalFileRecord record, LocalChunkRef chunk, string appDir, Dictionary<string, CachedFile> cache)
    {
        if (cache.TryGetValue(record.Path, out CachedFile? cached))
        {
            CachedChunk? match = cached.Chunks.FirstOrDefault(c => c.Offset == chunk.Offset && c.Length == chunk.Length);
            if (match is not null) return match.Sha256;
        }

        // Cache miss after a fresh scan is not expected; re-chunk the file to recover the hash.
        string path = LocalFileIndex.ToLocalPath(appDir, record.Path);
        byte[] bytes = File.ReadAllBytes(path);
        var chunks = ContentChunker.Split(bytes);
        CachedChunk? found = chunks.Select(c => new CachedChunk(c.Sha256, c.Offset, c.Length))
            .FirstOrDefault(c => c.Offset == chunk.Offset && c.Length == chunk.Length);
        return found?.Sha256 ?? string.Empty;
    }

    private static Dictionary<string, CachedFile> ReadLocalIndexCache()
    {
        try
        {
            if (!File.Exists(LocalIndexPath)) return new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
            return JsonSerializer.Deserialize<Dictionary<string, CachedFile>>(
                       File.ReadAllText(LocalIndexPath), JsonOptions)
                   ?? new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, CachedFile>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void WriteLocalIndexCache(Dictionary<string, CachedFile> cache)
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            File.WriteAllText(LocalIndexPath, JsonSerializer.Serialize(cache, JsonOptions), new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"写入本地文件索引缓存失败：{ex.Message}");
        }
    }

    private sealed class CachedFile
    {
        public long Size { get; set; }
        public long LastWriteUtcTicks { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public List<CachedChunk> Chunks { get; set; } = new();
    }

    private sealed class CachedChunk
    {
        public CachedChunk()
        {
        }

        public CachedChunk(string sha256, int offset, int length)
        {
            Sha256 = sha256;
            Offset = offset;
            Length = length;
        }

        public string Sha256 { get; set; } = string.Empty;
        public int Offset { get; set; }
        public int Length { get; set; }
    }

    // ── cloud downloads: API first, bundled aliyunpan CLI as fallback ─────────

    private static bool IsCallerCancellation(Exception ex, CancellationToken cancellationToken) =>
        ex is OperationCanceledException && cancellationToken.IsCancellationRequested;

    /// <summary>Wall time of one download attempt, so a fallback line says how long it burned.</summary>
    private static long ElapsedMs(long startTicks) =>
        (long)Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;

    /// <summary>Manifest/chunk download: signed CDN first, bundled CLI as the fallback hop.</summary>
    private Task<byte[]?> TryDownloadCloudBytesAsync(
        AliyunDriveSessionBundle bundle,
        string? folderId,
        string name,
        string remotePath,
        CancellationToken cancellationToken) =>
        CloudDownloader.TryDownloadBytesAsync(
            bundle.Session, folderId, name, remotePath, StateDirectory, cancellationToken);

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

    private static string AppDirectory() =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    private static T? Parse<T>(byte[]? bytes) where T : class =>
        bytes is null ? null : CloudUpdateFormat.TryParse<T>(Encoding.UTF8.GetString(bytes));

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

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
