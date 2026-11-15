using System.Diagnostics;
using System.IO;
using System.Text;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.App.Updates;

/// <summary>
/// 腾讯云镜像主更新通道：纯 HTTP、只做增量、不做回退。
///
/// 镜像（nginx 静态目录）与云盘仓库同一套布局——latest.json / versions.json /
/// history/&lt;版本&gt;.json / files/&lt;sha256&gt;——客户端哈希自己的安装目录，只下载变更文件
/// 里本地没有的分块，其余字节全部复用磁盘。发布器只把「保留窗口内各版本 → 最新版」真正需要的
/// 分块放在镜像上，所以服务器上永远只有几十 MB，而不是几个 GB 的历史。
///
/// 保留窗口（默认 3 个版本）之外的老客户端：镜像上没有他们的 history/&lt;版本&gt;.json，
/// 算不出可用的增量集，这里直接判定「无可用发布」，由调用方改走 GitHub 全量安装包——
/// 这是「只保留 3 版 + 只做增量」的固有代价，也是镜像保持瘦身的唯一办法。
///
/// 与已下线的阿里云盘通道的差别：没有账号、没有签名直链、没有 CDN 并发配额，也就没有
/// ExceedMaxConcurrency 403；下载失败（含分块 404）直接抛出，由调用方回退 GitHub。
/// </summary>
public sealed class MirrorUpdateService : IDisposable
{
    /// <summary>清单类小文件：国内直连几十毫秒，10 秒足够判死一台宕机的服务器。</summary>
    private static readonly TimeSpan ManifestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>分块流式下载：大分块在慢网络上要跑满整个 body，给足一分钟。</summary>
    private static readonly TimeSpan ChunkTimeout = TimeSpan.FromSeconds(60);

    /// <summary>四个并发对一台小规格腾讯云 VM 是友好的，也足够喂满家用带宽。</summary>
    private const int DownloadParallelism = 4;

    /// <summary>单个分块的重试次数：直连失败多为超时/连接重置，退避两次值得；404 重试无代价。</summary>
    private const int ChunkAttempts = 3;

    private readonly MirrorUpdateSource? _mirror;

    public MirrorUpdateService(AppSettings settings)
    {
        _mirror = MirrorUpdateSource.TryCreate(settings.UpdateMirrorUrl);
    }

    /// <summary>False when no mirror URL is configured — the caller falls straight back to GitHub.</summary>
    public bool IsConfigured => _mirror is not null;

    public void Dispose() => _mirror?.Dispose();

    // ── check ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Compares the newest mirror manifest with the running install. The plan only needs
    /// <c>latest.json</c> plus the local file hashes: whole-file hash decides "changed", and only
    /// chunks of changed files that the local snapshot does not already contain are downloaded.
    /// <c>history/&lt;当前版本&gt;.json</c> is the retention-window gate — the publisher deletes
    /// everything outside the window, so its absence means this client cannot be served
    /// incrementally and must take the GitHub package instead.
    /// </summary>
    public async Task<CloudCheckResult> CheckAsync(Version current, CancellationToken cancellationToken = default)
    {
        if (_mirror is null)
        {
            return new CloudCheckResult(CloudCheckStatus.NotConfigured,
                "尚未配置更新镜像地址。", current, null, null, null, null, null);
        }

        try
        {
            byte[]? latestBytes = await _mirror.TryGetBytesAsync(
                CloudUpdateFormat.LatestFile, ManifestTimeout, cancellationToken).ConfigureAwait(false);
            CloudManifest? manifest = Parse<CloudManifest>(latestBytes);
            if (manifest is null)
            {
                return new CloudCheckResult(CloudCheckStatus.NoRelease,
                    "更新镜像上没有可用的版本清单，改走 GitHub 兜底。", current, null, null, null, null, null);
            }

            Version? latest = UpdateVersion.ParseReleaseTag(manifest.Version);
            if (latest is null)
            {
                return new CloudCheckResult(CloudCheckStatus.Failed,
                    $"镜像版本号无法识别：{manifest.Version}", current, null, manifest, null, null, null);
            }

            // versions.json 只驱动设置页的版本列表，404 只是"没有这个可选文件"，
            // 镜像本身是好的——不能因此把检查打成失败。
            CloudVersionIndex? versions = Parse<CloudVersionIndex>(await _mirror.TryGetBytesAsync(
                CloudUpdateFormat.VersionsFile, ManifestTimeout, cancellationToken).ConfigureAwait(false));

            if (!UpdateVersion.IsNewer(current, latest))
            {
                return new CloudCheckResult(CloudCheckStatus.UpToDate,
                    $"当前已是最新版本 v{current}（镜像最新 v{latest}）。", current, latest, manifest, null, null, versions);
            }

            // 保留窗口闸门：发布器推送后只保留窗口内各版本的 history 清单，窗口外的老客户端
            // 在这里就能得到确定答案，而不是下载到一半才 404。404 不算镜像故障。
            byte[]? ownHistory = await _mirror.TryGetBytesAsync(
                $"{CloudUpdateFormat.HistoryFolder}/{current}.json", ManifestTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (ownHistory is null)
            {
                return new CloudCheckResult(CloudCheckStatus.NoRelease,
                    $"当前版本 v{current} 不在更新镜像的保留窗口内（镜像只保留最近几个版本），" +
                    "改走 GitHub 兜底更新。", current, latest, manifest, null, null, versions);
            }

            string appDir = CloudApplyService.AppDirectory();
            string[] localPaths = manifest.Files.Select(f => f.Path).Concat(manifest.Deleted).ToArray();
            LocalFileIndex local = await Task.Run(() => BuildLocalIndex(appDir, localPaths), cancellationToken)
                .ConfigureAwait(false);
            CloudUpdatePlan plan = CloudUpdatePlanner.CreatePlan(current.ToString(), manifest, local);

            string message = plan.HasChanges
                ? $"发现新版本 v{latest}：需下载 {plan.ChangedFiles.Count} 个文件 / {FormatBytes(plan.DownloadBytes)}，" +
                  $"另有 {plan.DeletedFiles.Count} 个文件删除。"
                : $"镜像已发布 v{latest}，但文件内容与当前一致。";
            return new CloudCheckResult(CloudCheckStatus.UpdateAvailable, message,
                current, latest, manifest, plan, local, versions);
        }
        catch (Exception ex)
        {
            AppLog.Error("镜像更新检查失败", ex);
            return new CloudCheckResult(CloudCheckStatus.Failed,
                $"连接更新镜像失败：{ex.Message}", current, null, null, null, null, null);
        }
    }

    // ── download + stage ─────────────────────────────────────────────────────

    /// <summary>
    /// Downloads the chunks the local install does not already contain, reassembles the changed
    /// files into a staging directory and verifies every file hash. No fallback channel here:
    /// a dead mirror must fail fast and loudly so the caller switches to GitHub in one session.
    /// </summary>
    public async Task<CloudStageResult> StageAsync(
        CloudCheckResult check,
        IProgress<CloudUpdateProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (_mirror is null)
        {
            throw new InvalidOperationException("更新镜像尚未配置，无法下载更新。");
        }

        if (check.Manifest is null || check.Plan is null || check.LocalIndex is null)
        {
            throw new InvalidOperationException("没有可用的镜像更新计划，请先检查更新。");
        }

        CloudUpdatePlan plan = check.Plan;
        if (!plan.HasChanges)
        {
            return new CloudStageResult(check, string.Empty, Array.Empty<string>(), Array.Empty<string>(), 0, 0);
        }

        Directory.CreateDirectory(ChunkCacheDirectory);
        string staging = Path.Combine(CloudUpdatePaths.Staging, $"{check.Manifest.Version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        long totalBytes = plan.DownloadBytes;
        long[] downloaded = new long[1];
        await Parallel.ForEachAsync(
            plan.RequiredChunks,
            new ParallelOptions { MaxDegreeOfParallelism = DownloadParallelism, CancellationToken = cancellationToken },
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
                    MirrorFetchResult fetched = await _mirror.TryDownloadToFileAsync(
                        $"files/{chunk.Sha256}", ChunkTimeout, temp, token).ConfigureAwait(false);
                    if (fetched == MirrorFetchResult.Missing)
                    {
                        // 镜像上没有这个分块：清单与文件不同步。重试没有意义，直接失败，
                        // 让调用方改走 GitHub 全量包（保留窗口算错时唯一的活路）。
                        throw new InvalidDataException(
                            $"镜像上缺少分块 {chunk.Sha256[..12]}…（清单与文件不同步），本次更新已放弃。");
                    }

                    if (fetched == MirrorFetchResult.Ok) break;

                    if (attempt >= ChunkAttempts)
                    {
                        throw new InvalidDataException(
                            $"下载分块 {chunk.Sha256[..12]}… 失败（已重试 {ChunkAttempts} 次）：" +
                            "更新镜像不可用，本次更新已放弃。");
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(500L << (attempt - 1)), token).ConfigureAwait(false);
                }

                if (new FileInfo(temp).Length != chunk.Size ||
                    !string.Equals(CloudHash.Sha256HexFile(temp), chunk.Sha256, StringComparison.OrdinalIgnoreCase))
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
        AppLog.Info($"镜像更新暂存完成：v{check.Manifest.Version}，下载 {FormatBytes(downloaded[0])}，" +
                    $"变更 {plan.ChangedFiles.Count} 个文件、删除 {plan.DeletedFiles.Count} 个。");

        return new CloudStageResult(check, staging,
            plan.ChangedFiles.Select(f => LocalFileIndex.Normalize(f.Path)).ToList(),
            plan.DeletedFiles.ToList(),
            downloaded[0],
            plan.ChangedBytes);
    }

    // ── apply ────────────────────────────────────────────────────────────────

    /// <summary>Hands the staged files to the detached apply script (see CloudApplyService).</summary>
    public void ApplyAndRestart(CloudStageResult staged, string direction, string note) =>
        CloudApplyService.ApplyAndRestart(staged, direction, note);

    /// <summary>The background script's verdict, folded into the install history at startup.</summary>
    public static CloudApplyResult? ReconcileLastApply() => CloudApplyService.ReconcileLastApply();

    public static IReadOnlyList<CloudUpdateHistoryEntry> ReadHistory() => CloudApplyService.ReadHistory();

    /// <summary>Where staging, chunk cache, backups and the install log live (never inside the app dir).</summary>
    public static string ChunkCacheDirectory => CloudUpdatePaths.ChunkCache;

    // ── small helpers ────────────────────────────────────────────────────────

    private static LocalFileIndex BuildLocalIndex(string appDir, IEnumerable<string> paths) =>
        LocalFileIndexService.Build(appDir, paths);

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
