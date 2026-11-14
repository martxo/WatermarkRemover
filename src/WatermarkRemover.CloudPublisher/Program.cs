using System.Globalization;
using System.IO;
using System.Text;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Core.Updates;
using WatermarkRemover.Netdisk;

// 阿里云盘增量发布工具.
//
//   dotnet run --project src/WatermarkRemover.CloudPublisher -- \
//       --version 1.16.36 --source d:\code\publish-staging --note "修复身份证排序"
//
// Layout written into the account's 资源库:
//   /WatermarkRemover/latest.json          newest full manifest
//   /WatermarkRemover/versions.json        retained-version index (newest first, keep 5)
//   /WatermarkRemover/history/<version>.json
//   /WatermarkRemover/files/<sha256>       content-addressed chunks (shared by all versions)
//
// Only chunks absent from the repository are uploaded, so a typical code-only release ships a
// couple of megabytes instead of the 236 MB full Velopack package.

var options = ParseArgs(args);
var ct = CancellationToken.None;
if (options is null)
{
    Console.Error.WriteLine(
        "用法: WatermarkRemover.CloudPublisher --version <版本> --source <发布目录> [--note 说明] [--keep 5] [--dry-run]\n" +
        "      --compare <上一版目录>  只计算增量大小，不连接云盘\n" +
        "      --probe <文件>          上传该文件的第 1 个分块，验证云端上传链路\n" +
        "      --export-login          导出私有登录包并上传到 /WatermarkRemover/private/\n" +
        "      --import-login <文件>   把私有登录包导入本机稳定配置\n" +
        "      --probe-move            验证云盘移动接口（创建/上传/移动/删除临时文件）\n" +
        "      --latest-file <文件>    使用本地 latest.json（下载接口不可用时）\n" +
        "      --versions-file <文件>  使用本地 versions.json\n" +
        "      --no-gc                 跳过历史清单下载与无引用分块清理\n" +
        "      --seed-mirror <目录>    把云盘更新仓库整体拉到本地目录并同步到腾讯云镜像\n" +
        "                                （断点续传：本地/镜像上已存在且大小一致的文件都跳过）");
    return 2;
}

// Import a portable login pack into the stable config (same path the app uses).
if (options.ImportLogin is { } importFile)
{
    if (!File.Exists(importFile))
    {
        Console.Error.WriteLine($"登录状态文件不存在：{importFile}");
        return 2;
    }

    string text = await File.ReadAllTextAsync(importFile, ct);
    string json = LoginPack.Unprotect(text, options.LoginPassphrase);
    string stable = AliyunpanCliRuntime.ImportConfig(json);
    Console.WriteLine($"登录状态已导入：{stable}");
    return 0;
}

// Seed mode: pull the entire cloud update repository (manifests + every chunk) into a local
// directory and mirror it to the Tencent Cloud server. Resumable by design — a local file
// with the right size and a mirrored file with the right size are both skipped, so killing
// the run halfway and restarting continues instead of starting over. This is what backfills
// the mirror with the historical chunks a client needs to walk from any older version; the
// per-release PushMirrorAsync below only ever ships the new chunks of one version.
if (options.SeedMirror is { } seedDir)
{
    Directory.CreateDirectory(seedDir);
    var seedValues = BuildCliCredentials();
    await using var seedSession = await new AliyunDriveProvider()
        .ConnectAsync(new NetdiskCredentials(seedValues));
    NetdiskDrive seedDrive = seedSession.Drives.FirstOrDefault(d => d.Name == "资源库")
        ?? throw new InvalidOperationException("账号没有「资源库」，无法拉取云更新仓库。");
    await seedSession.SwitchDriveAsync(seedDrive.Id, ct);

    await SeedMirrorAsync(seedSession, seedDir, ct);

    // Mirror credentials stay in the environment (WM_MIRROR_PASS); unset = local seed only.
    if (Environment.GetEnvironmentVariable("WM_MIRROR_PASS") is { Length: > 0 } seedPass)
    {
        await MirrorPushTreeAsync(
            Environment.GetEnvironmentVariable("WM_MIRROR_HOST") ?? "106.52.57.36",
            Environment.GetEnvironmentVariable("WM_MIRROR_USER") ?? "ubuntu",
            seedPass,
            Environment.GetEnvironmentVariable("WM_MIRROR_DIR") ?? "/var/www/wm-updates",
            seedDir, ct);
    }

    return 0;
}

// Private login pack: export the CLI config (device-local paths stripped) and upload it into the
// account-only /WatermarkRemover/private folder. Never goes into a GitHub release.
if (options.ExportLogin)
{
    var loginValues = BuildCliCredentials();
    await using var loginSession = await new AliyunDriveProvider()
        .ConnectAsync(new NetdiskCredentials(loginValues));
    NetdiskDrive loginDrive = loginSession.Drives.FirstOrDefault(d => d.Name == "资源库")
        ?? throw new InvalidOperationException("账号没有资源库。");
    await loginSession.SwitchDriveAsync(loginDrive.Id);

    string raw = AliyunpanCliRuntime.TryReadRawConfig()
        ?? throw new InvalidOperationException("未找到阿里云盘登录配置，请先登录。");
    if (string.IsNullOrWhiteSpace(options.LoginPassphrase))
    {
        Console.Error.WriteLine(
            "导出登录状态需要口令：--login-passphrase <口令> 或环境变量 WM_LOGIN_PACK_KEY。\n" +
            "登录包带账号令牌，禁止明文上传。");
        return 2;
    }

    string pack = LoginPack.Protect(LoginPack.Create(raw), options.LoginPassphrase);
    string localDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WatermarkRemover");
    Directory.CreateDirectory(localDir);
    string localPath = Path.Combine(localDir, LoginPack.FileName);
    await File.WriteAllTextAsync(localPath, pack, ct);

    string privateFolder = await loginSession.EnsureFolderAsync(
        $"{CloudUpdateFormat.RootFolder}/private", ct);
    await loginSession.UploadBytesAsync(
        privateFolder, LoginPack.FileName, System.Text.Encoding.UTF8.GetBytes(pack), ct);

    Console.WriteLine($"登录状态已导出：{localPath}");
    Console.WriteLine($"云端私有文件：/{CloudUpdateFormat.RootFolder}/private/{LoginPack.FileName}");
    Console.WriteLine("文件已用口令加密。另一台电脑用同一口令导入（设置→网盘设置→导入登录状态文件）。");
    return 0;
}

// Move probe: creates a temporary file and folders, moves the file, verifies and deletes it.
if (options.ProbeMove)
{
    var moveValues = BuildCliCredentials();
    await using var moveSession = await new AliyunDriveProvider()
        .ConnectAsync(new NetdiskCredentials(moveValues));
    NetdiskDrive moveDrive = moveSession.Drives.FirstOrDefault(d => d.Name == "资源库")
        ?? throw new InvalidOperationException("账号没有资源库。");
    await moveSession.SwitchDriveAsync(moveDrive.Id);

    string baseFolder = await moveSession.EnsureFolderAsync(
        $"{CloudUpdateFormat.RootFolder}/private/probe-move");
    string srcFolder = await moveSession.EnsureFolderAsync(
        $"{CloudUpdateFormat.RootFolder}/private/probe-move/src");
    string dstFolder = await moveSession.EnsureFolderAsync(
        $"{CloudUpdateFormat.RootFolder}/private/probe-move/dst");

    string temp = Path.Combine(Path.GetTempPath(), $"wr-move-{Guid.NewGuid():N}.txt");
    await File.WriteAllTextAsync(temp, "move probe " + DateTimeOffset.UtcNow.ToString("O"), ct);
    try
    {
        await moveSession.UploadAsAsync(srcFolder, temp, "probe-move.txt", overwrite: true, null, ct);
        NetdiskFile file = await moveSession.FindChildAsync(srcFolder, "probe-move.txt")
            ?? throw new InvalidOperationException("移动探针：上传后找不到文件。");
        await moveSession.MoveAsync(new[] { file.Id }, dstFolder, ct);
        NetdiskFile? moved = await moveSession.FindChildAsync(dstFolder, "probe-move.txt");
        if (moved is null)
        {
            throw new InvalidOperationException("移动探针：目标目录没有找到文件。");
        }

        await moveSession.DeleteAsync(new[] { moved.Id }, ct);
        await moveSession.DeleteAsync(new[] { srcFolder, dstFolder, baseFolder }, ct);
        Console.WriteLine("移动探针成功：文件已从 src 移到 dst，并已清理。");
        return 0;
    }
    finally
    {
        try { File.Delete(temp); } catch { /* best effort */ }
    }
}

// One-chunk upload probe: exercises the exact blob upload path on a real file without touching
// any version manifest. Useful when a release upload fails at the object-store PUT.
if (options.ProbeFile is { } probeFile)
{
    if (!File.Exists(probeFile))
    {
        Console.Error.WriteLine($"探针文件不存在：{probeFile}");
        return 2;
    }

    var probeValues = BuildCliCredentials();
    await using var probeSession = await new AliyunDriveProvider()
        .ConnectAsync(new NetdiskCredentials(probeValues));
    NetdiskDrive probeDrive = probeSession.Drives.FirstOrDefault(d => d.Name == "资源库")
        ?? throw new InvalidOperationException("账号没有资源库。");
    await probeSession.SwitchDriveAsync(probeDrive.Id);
    string probeChunks = await probeSession.EnsureFolderAsync(
        $"{CloudUpdateFormat.RootFolder}/{CloudUpdateFormat.ChunksFolder}");

    byte[] probeBytes = await File.ReadAllBytesAsync(probeFile);
    var probeChunk = ContentChunker.Split(probeBytes)[0];
    string probeTemp = Path.Combine(Path.GetTempPath(), probeChunk.Sha256);
    await SliceToFileAsync(probeFile, probeChunk.Offset, probeChunk.Length, probeTemp, ct);
    try
    {
        await probeSession.UploadAsAsync(probeChunks, probeTemp, probeChunk.Sha256, overwrite: false);
        Console.WriteLine($"探针上传成功：{probeFile} 的第 1 块（{probeChunk.Length} B，{probeChunk.Sha256[..12]}…）");

        byte[]? downloaded = await probeSession.DownloadBytesAsync(probeChunks, probeChunk.Sha256);
        if (downloaded is null || downloaded.Length != probeChunk.Length ||
            !string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(downloaded)).ToLowerInvariant(),
                probeChunk.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("探针下载校验失败。");
        }

        Console.WriteLine($"探针下载成功并校验一致：{downloaded.Length} B");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"探针上传失败：{ex}");
        return 1;
    }
    finally
    {
        File.Delete(probeTemp);
    }
}

// Local-only estimate: how much of this release would actually be uploaded compared to another
// published output. Used to verify the incremental story before touching the cloud.
if (options.CompareSource is { } previousDir)
{
    if (!Directory.Exists(previousDir))
    {
        Console.Error.WriteLine($"对比目录不存在：{previousDir}");
        return 2;
    }

    CloudManifest oldManifest = CloudUpdatePlanner.BuildManifest(previousDir, "old", string.Empty, null);
    var sources = new Dictionary<string, (string Path, int Offset, int Length)>(StringComparer.Ordinal);
    CloudManifest newManifest = CloudUpdatePlanner.BuildManifest(
        options.Source, options.Version, string.Empty, null, chunkSources: sources);
    var oldChunks = oldManifest.AllChunkHashes().ToHashSet(StringComparer.Ordinal);
    var added = newManifest.AllChunkHashes().Where(sha => !oldChunks.Contains(sha)).ToList();
    long bytes = added.Sum(sha => (long)sources[sha].Length);
    Console.WriteLine($"对比：{previousDir} → {options.Source}");
    Console.WriteLine($"  发布体积 {FormatBytes(newManifest.TotalBytes)}，仅需上传 {added.Count} 个分块 / {FormatBytes(bytes)}");
    Console.WriteLine($"  复用 {newManifest.AllChunkHashes().Count() - added.Count}/{newManifest.AllChunkHashes().Count()} 个分块");
    return 0;
}

const string DriveName = "资源库";
string rootPath = CloudUpdateFormat.RootFolder;
string chunksPath = $"{rootPath}/{CloudUpdateFormat.ChunksFolder}";
string historyPath = $"{rootPath}/{CloudUpdateFormat.HistoryFolder}";

Console.WriteLine($"[1/6] 读取本机 aliyunpan 登录信息…");
string cli = AliyunpanCliRuntime.LocateExecutable()
    ?? throw new InvalidOperationException("未找到 aliyunpan 命令行工具，请先安装并登录。");
if (!AliyunpanCliRuntime.TryReadConfig(AliyunpanCliRuntime.ConfigPathFor(cli), out string? deviceId, out var token) ||
    string.IsNullOrEmpty(token.AccessToken))
{
    throw new InvalidOperationException("读取 aliyunpan 配置失败，请先在命令行执行 aliyunpan login。");
}

var values = new Dictionary<string, string>(StringComparer.Ordinal)
{
    [AliyunpanCliRuntime.CliLoginKey] = "true",
    [AliyunpanCliRuntime.CliDeviceIdKey] = deviceId ?? string.Empty,
    [AliyunTokenService.AccessTokenKey] = token.AccessToken,
    [AliyunTokenService.ExpiresAtKey] = token.ExpiredAt.ToString("O", CultureInfo.InvariantCulture),
};

await using var session = await new AliyunDriveProvider().ConnectAsync(new NetdiskCredentials(values));
NetdiskDrive drive = session.Drives.FirstOrDefault(d => d.Name == DriveName)
    ?? throw new InvalidOperationException($"账号没有「{DriveName}」，无法发布云更新。");
await session.SwitchDriveAsync(drive.Id, ct);
Console.WriteLine($"      账号 {session.Account.DisplayName} · 网盘 {drive.Name}");

Console.WriteLine("[2/6] 确认云盘目录…");
string rootId = await session.EnsureFolderAsync(rootPath, ct);
string chunksId = await session.EnsureFolderAsync(chunksPath, ct);
string historyId = await session.EnsureFolderAsync(historyPath, ct);

CloudManifest? latest = options.LatestFile is { } latestPath
    ? ReadManifestFile(latestPath)
    : await DownloadManifestAsync(session, rootId, CloudUpdateFormat.LatestFile);
CloudVersionIndex index = options.VersionsFile is { } versionsPath
    ? ReadIndexFile(versionsPath) ?? new CloudVersionIndex()
    : await DownloadIndexAsync(session, rootId) ?? new CloudVersionIndex();

// Re-publishing the same version diffs against the version below it, so the recorded change
// summary describes what actually differs from the previous installable version.
CloudManifest? previous;
if (latest is not null &&
    !string.Equals(latest.Version, options.Version, StringComparison.OrdinalIgnoreCase))
{
    previous = latest;
}
else
{
    // Offline manifests (--latest-file/--versions-file) + --no-gc keep a flaky download hop
    // out of the publish path; the change summary simply counts every file on a re-publish.
    previous = options.NoGc
        ? null
        : await DownloadManifestAsync(session, historyId, index.Versions.Skip(1).FirstOrDefault()?.Version);
}

Console.WriteLine("[3/6] 计算清单与分块…");
var chunkSources = new Dictionary<string, (string Path, int Offset, int Length)>(StringComparer.Ordinal);
CloudManifest manifest = CloudUpdatePlanner.BuildManifest(
    options.Source, options.Version, options.Note, previous, chunkSources: chunkSources);
Console.WriteLine(
    $"      文件 {manifest.Files.Count} 个 / {FormatBytes(manifest.TotalBytes)}，分块 {manifest.AllChunkHashes().Count()} 个");

var remoteChunks = new HashSet<string>(StringComparer.Ordinal);
foreach (NetdiskFile item in await session.ListAllAsync(chunksId, ct))
{
    if (!item.IsFolder && IsChunkName(item.Name)) remoteChunks.Add(item.Name.ToLowerInvariant());
}

var newChunks = manifest.AllChunkHashes()
    .Where(sha => !remoteChunks.Contains(sha))
    .Distinct(StringComparer.Ordinal)
    .ToList();
long newChunkBytes = newChunks.Sum(sha => (long)chunkSources[sha].Length);
Console.WriteLine($"      远端已有分块 {remoteChunks.Count} 个；本次需上传 {newChunks.Count} 个 / {FormatBytes(newChunkBytes)}");

var summary = new CloudVersionSummary
{
    Version = manifest.Version,
    PublishedAt = manifest.PublishedAt,
    Note = manifest.Note,
    FileCount = manifest.Files.Count,
    TotalBytes = manifest.TotalBytes,
    ChangedFiles = CountChanged(previous, manifest),
    DeletedFiles = manifest.Deleted.Count,
    NewChunks = newChunks.Count,
    NewBytes = newChunkBytes,
};

if (options.DryRun)
{
    Console.WriteLine("[dry-run] 不上传、不删除。");
    PrintSummary(summary);
    return 0;
}

Console.WriteLine("[4/6] 上传新分块…");
string tempDir = Path.Combine(Path.GetTempPath(), "wr-cloud-publish", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(tempDir);
try
{
    int done = 0;
    await Parallel.ForEachAsync(
        newChunks,
        new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct },
        async (sha, token) =>
        {
            (string path, int offset, int length) = chunkSources[sha];
            string temp = Path.Combine(tempDir, sha);
            await SliceToFileAsync(path, offset, length, temp, token);

            // The session retries the HTTP calls; this outer retry covers a whole part sequence
            // failing (e.g. the upload URL task expired) without aborting the release.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await session.UploadAsAsync(chunksId, temp, sha, overwrite: false, null, token);
                    break;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException && attempt < 2)
                {
                    Console.WriteLine($"      分块 {sha[..8]} 上传失败，重试：{ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), token);
                }
            }

            File.Delete(temp);

            int finished = Interlocked.Increment(ref done);
            if (finished % 10 == 0 || finished == newChunks.Count)
            {
                Console.WriteLine($"      已上传 {finished}/{newChunks.Count}");
            }
        });
}
finally
{
    try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
}

Console.WriteLine("[5/6] 写入版本清单…");
// 清单是唯一会被覆写的云端文件，不能覆盖上传：CDN 按下载 URL 缓存，同 file_id 覆盖后
// 边缘节点仍在发旧内容，客户端会把新版本误判为"已是最新"（1.16.135 实证：云端清单已是
// 135，1.16.133 客户端检查更新仍显示最新）。先删除再上传得到新 file_id，签名 URL 不会
// 命中任何旧缓存。分块文件内容寻址（名字即哈希），不存在此问题。
await UploadMutableManifestAsync(session, rootId, CloudUpdateFormat.LatestFile,
    Encoding.UTF8.GetBytes(CloudUpdateFormat.Serialize(manifest)), ct);
await session.UploadBytesAsync(historyId, $"{manifest.Version}.json",
    Encoding.UTF8.GetBytes(CloudUpdateFormat.Serialize(manifest)), ct);

index.Versions.RemoveAll(v => string.Equals(v.Version, manifest.Version, StringComparison.OrdinalIgnoreCase));
index.Versions.Insert(0, summary);
var dropped = index.Versions.Skip(options.Keep).ToList();
index.Versions = index.Versions.Take(options.Keep).ToList();
index.Keep = options.Keep;
index.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");

await UploadMutableManifestAsync(session, rootId, CloudUpdateFormat.VersionsFile,
    Encoding.UTF8.GetBytes(CloudUpdateFormat.Serialize(index)), ct);

Console.WriteLine("[6/6] 清理超出保留数量的旧版本…");
foreach (CloudVersionSummary old in dropped)
{
    await session.DeleteByNameAsync(historyId, $"{old.Version}.json", ct);
    Console.WriteLine($"      已删除历史 {old.Version}");
}

if (index.Versions.Count > 1 && !options.NoGc)
{
    var retainedChunks = new HashSet<string>(manifest.AllChunkHashes(), StringComparer.Ordinal);
    bool complete = true;
    foreach (CloudVersionSummary version in index.Versions.Skip(1))
    {
        CloudManifest? old = await DownloadManifestAsync(session, historyId, version.Version);
        if (old is null)
        {
            complete = false;
            break;
        }

        retainedChunks.UnionWith(old.AllChunkHashes());
    }

    if (complete)
    {
        var orphanSet = remoteChunks
            .Where(name => !retainedChunks.Contains(name))
            .ToHashSet(StringComparer.Ordinal);
        if (orphanSet.Count > 0)
        {
            var ids = (await session.ListAllAsync(chunksId, ct))
                .Where(i => !i.IsFolder && orphanSet.Contains(i.Name.ToLowerInvariant()))
                .Select(i => i.Id)
                .ToList();
            if (ids.Count > 0)
            {
                await session.DeleteAsync(ids, ct);
            }

            Console.WriteLine($"      清理无引用分块 {ids.Count} 个");
        }
    }
    else
    {
        Console.WriteLine("      部分历史清单缺失，跳过无引用分块清理（避免误删）。");
    }
}

PrintSummary(summary);

// ── 更新镜像推送（腾讯云广州服务器，nginx 静态目录）─────────────────────────
// 发布完云盘后立刻同步镜像，客户端（镜像优先）下一章检查更新即可直连下载。
// 凭据只从环境变量读，绝不进代码/仓库：
//   WM_MIRROR_HOST / WM_MIRROR_USER / WM_MIRROR_PASS / WM_MIRROR_DIR
// 未设 WM_MIRROR_PASS 时静默跳过（没配镜像的机器照常只发云盘）。
if (Environment.GetEnvironmentVariable("WM_MIRROR_PASS") is { Length: > 0 } mirrorPass)
{
    string mirrorHost = Environment.GetEnvironmentVariable("WM_MIRROR_HOST") ?? "106.52.57.36";
    string mirrorUser = Environment.GetEnvironmentVariable("WM_MIRROR_USER") ?? "ubuntu";
    string mirrorDir = Environment.GetEnvironmentVariable("WM_MIRROR_DIR") ?? "/var/www/wm-updates";
    await PushMirrorAsync(mirrorHost, mirrorUser, mirrorPass, mirrorDir, previous, chunkSources,
        manifest, index, ct);
}

return 0;

/// <summary>
/// 把本次发布的清单 +「客户端实际需要的分块」SFTP 推送到腾讯云镜像。与云盘同样的目录布局
/// （latest.json / versions.json / history/&lt;版本&gt;.json / files/&lt;sha256&gt;），
/// 客户端零适配。推送失败只告警不中断——云盘发布已经成功，镜像下次发布会补齐。
///
/// 推送集 = 新版本清单 − 上一版本清单（M_L − M_prev），不是 newChunks（云端没有的新分块）：
/// 早在云盘里被旧版本引用过的分块，发布器不会重传（newChunks 跳过它），但新版本重新引用
/// 它时镜像上没有，客户端只能 404 回退云盘——1.16.154 实证：153 客户端下 154 的 18 个分块，
/// 17 个走镜像、33108779… 就这样漏掉回退了阿里云盘。逐版本差集在多次发布间自动累加，
/// 正好覆盖任意旧版本客户端到最新版的全部需求。上一版清单拿不到时（首发、重发同版本、
/// --no-gc、历史清单缺失）退化为推全量 M_L：多传不少传，客户端要的是「镜像上有」。
/// </summary>
static async Task PushMirrorAsync(
    string host, string user, string pass, string dir,
    CloudManifest? previousManifest,
    Dictionary<string, (string path, int offset, int length)> chunkSources,
    CloudManifest manifest, CloudVersionIndex index, CancellationToken ct)
{
    List<string> mirrorChunks = previousManifest is null
        ? manifest.AllChunkHashes().Distinct(StringComparer.Ordinal).ToList()
        : manifest.AllChunkHashes()
            .Except(previousManifest.AllChunkHashes(), StringComparer.Ordinal)
            .ToList();
    Console.WriteLine($"      [mirror] 推送 {mirrorChunks.Count} 个分块（新版本 − 上一版本差集）");
    Console.WriteLine($"[mirror] 推送更新镜像 {user}@{host}:{dir} …");
    using var sftp = new Renci.SshNet.SftpClient(host, user, pass);
    sftp.Connect();
    Console.WriteLine($"      [mirror] 已连接，工作目录 {sftp.WorkingDirectory}");
    try
    {
        MirrorEnsureDir(sftp, dir);
        // SSH.NET 的 UploadFile 不建父目录：history/ 和 files/ 必须显式确保存在，
        // 否则 history/<版本>.json 一上传就 SftpPathNotFoundException（前两个文件在
        // dir 根下所以看起来是"成功的"）。
        MirrorEnsureDir(sftp, $"{dir}/{CloudUpdateFormat.HistoryFolder}");
        MirrorEnsureDir(sftp, $"{dir}/{CloudUpdateFormat.ChunksFolder}");

        async Task PutAsync(string remote, byte[] bytes)
        {
            Console.WriteLine($"      [mirror] 上传 {remote} ({bytes.Length} B)…");
            using var ms = new MemoryStream(bytes);
            sftp.UploadFile(ms, remote, true);
            await Task.CompletedTask;
        }

        byte[] latest = Encoding.UTF8.GetBytes(CloudUpdateFormat.Serialize(manifest));
        byte[] versions = Encoding.UTF8.GetBytes(CloudUpdateFormat.Serialize(index));
        byte[] history = latest;
        await PutAsync($"{dir}/latest.json", latest);
        await PutAsync($"{dir}/versions.json", versions);
        await PutAsync($"{dir}/history/{manifest.Version}.json", history);
        Console.WriteLine($"      latest.json / versions.json / history/{manifest.Version}.json 已推送");

        string chunkDir = $"{dir}/{CloudUpdateFormat.ChunksFolder}";
        int done = 0;
        await Parallel.ForEachAsync(mirrorChunks,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (sha, token) =>
            {
                (string path, int offset, int length) = chunkSources[sha];
                string temp = Path.Combine(Path.GetTempPath(), "wr-mirror", sha);
                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                await SliceToFileAsync(path, offset, length, temp, token);
                try
                {
                    using var fs = File.OpenRead(temp);
                    sftp.UploadFile(fs, $"{chunkDir}/{sha}", true);
                }
                finally
                {
                    File.Delete(temp);
                }
                int finished = Interlocked.Increment(ref done);
                if (finished % 20 == 0 || finished == mirrorChunks.Count)
                {
                    Console.WriteLine($"      分块 {finished}/{mirrorChunks.Count}");
                }
            });
        Console.WriteLine($"[mirror] 完成：{mirrorChunks.Count} 个分块 + 3 个清单");
    }
    catch (Exception ex)
    {
        // 镜像推送失败不影响云盘发布结果：客户端会回退云盘，下次发布自动补齐。
        Console.WriteLine($"      [mirror] 推送失败（不影响云盘发布）：{ex.GetType().Name}: {ex.Message}");
        if (ex.InnerException is not null)
        {
            Console.WriteLine($"      [mirror]   内部异常：{ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
        }
    }
    finally
    {
        sftp.Disconnect();
    }
}

/// <summary>mkdir -p over SFTP（协议没有递归建目录）。</summary>
static void MirrorEnsureDir(Renci.SshNet.SftpClient sftp, string path)
{
    string current = string.Empty;
    foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
    {
        current += "/" + part;
        try { sftp.CreateDirectory(current); } catch (Exception) { /* 已存在 */ }
    }
}

/// <summary>
/// 种子下载：把云盘更新仓库整体拉到 <paramref name="localDir"/>（布局与云端一致：
/// latest.json / versions.json / history/&lt;版本&gt;.json / files/&lt;sha256&gt;）。
/// 已存在且大小与云端一致的文件跳过，中断后重跑自动续传。云端 latest.json 每篇发布都是
/// 删后重传的新 file_id，这里每次全量重拉，保证本地种子永远跟得上最新发布状态。
/// </summary>
static async Task SeedMirrorAsync(INetdiskSession session, string localDir, CancellationToken ct)
{
    string filesDir = Path.Combine(localDir, CloudUpdateFormat.ChunksFolder);
    string historyDir = Path.Combine(localDir, CloudUpdateFormat.HistoryFolder);
    Directory.CreateDirectory(filesDir);
    Directory.CreateDirectory(historyDir);

    string rootId = await session.EnsureFolderAsync(CloudUpdateFormat.RootFolder, ct);
    string filesId = await session.EnsureFolderAsync(
        $"{CloudUpdateFormat.RootFolder}/{CloudUpdateFormat.ChunksFolder}", ct);
    string historyId = await session.EnsureFolderAsync(
        $"{CloudUpdateFormat.RootFolder}/{CloudUpdateFormat.HistoryFolder}", ct);

    var wanted = new List<(string Id, string LocalPath, long Size)>();
    async Task CollectAsync(string folderId, string directory)
    {
        foreach (NetdiskFile item in await session.ListAllAsync(folderId, ct).ConfigureAwait(false))
        {
            if (item.IsFolder) continue;
            if (directory == filesDir && !IsChunkName(item.Name)) continue;
            wanted.Add((item.Id,
                Path.Combine(directory, directory == filesDir ? item.Name.ToLowerInvariant() : item.Name),
                item.Size));
        }
    }

    await CollectAsync(rootId, localDir);
    await CollectAsync(historyId, historyDir);
    await CollectAsync(filesId, filesDir);

    var missing = wanted
        .Where(w => !File.Exists(w.LocalPath) || new FileInfo(w.LocalPath).Length != w.Size)
        .ToList();
    Console.WriteLine($"[seed] 云端仓库 {wanted.Count} 个文件，本地缺 {missing.Count} 个 / {FormatBytes(missing.Sum(m => m.Size))}");

    if (missing.Count == 0)
    {
        Console.WriteLine("[seed] 本地仓库已完整，无需下载。");
        return;
    }

    int done = 0;
    await Parallel.ForEachAsync(missing,
        new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct },
        async (item, token) =>
        {
            string temp = Path.Combine(Path.GetTempPath(), "wr-seed", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        await session.DownloadAsync(item.Id, temp, null, token);
                        break;
                    }
                    catch (Exception ex) when (
                        (ex is HttpRequestException or IOException or TimeoutException || ex.Message.Contains("RequestDeniedByCallback", StringComparison.OrdinalIgnoreCase))
                        && attempt < 3)
                    {
                        // 共享出口 IP 上 CDN 偶发 403 限流：退避后重试， chunk 下载本身是幂等的。
                        Console.WriteLine($"      分块下载第 {attempt + 1} 次失败，退避重试：{ex.Message}");
                        await Task.Delay(TimeSpan.FromSeconds(3 * (attempt + 1)), token);
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(item.LocalPath)!);
                File.Move(temp, item.LocalPath, overwrite: true);
            }
            finally
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }

            int finished = Interlocked.Increment(ref done);
            if (finished % 50 == 0 || finished == missing.Count)
            {
                Console.WriteLine($"      已下载 {finished}/{missing.Count}");
            }
        });

    Console.WriteLine($"[seed] 下载完成：{missing.Count} 个文件 → {localDir}");
}

/// <summary>
/// 把本地种子目录整树推送到腾讯云镜像。目录布局与云端一一对应，相对路径即远端路径。
/// 远端已存在且大小一致的文件跳过——中断重跑不重推已传完的分块。
/// </summary>
static async Task MirrorPushTreeAsync(
    string host, string user, string pass, string dir, string localRoot, CancellationToken ct)
{
    Console.WriteLine($"[seed][mirror] 整树推送 {localRoot} → {user}@{host}:{dir} …");
    using var sftp = new Renci.SshNet.SftpClient(host, user, pass);
    sftp.Connect();
    try
    {
        MirrorEnsureDir(sftp, dir);
        MirrorEnsureDir(sftp, $"{dir}/{CloudUpdateFormat.HistoryFolder}");
        MirrorEnsureDir(sftp, $"{dir}/{CloudUpdateFormat.ChunksFolder}");

        var files = Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories).ToList();
        int pushed = 0, skipped = 0;
        await Parallel.ForEachAsync(files,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            (local, token) =>
            {
                string remote = $"{dir}/{Path.GetRelativePath(localRoot, local).Replace('\\', '/')}";
                long length = new FileInfo(local).Length;
                if (sftp.Exists(remote))
                {
                    try
                    {
                        if (sftp.GetAttributes(remote).Size == length)
                        {
                            Interlocked.Increment(ref skipped);
                            return ValueTask.CompletedTask;
                        }
                    }
                    catch (Exception)
                    {
                        // 属性取不到就重传：UploadFile overwrite=true 是幂等的。
                    }
                }

                using var fs = File.OpenRead(local);
                sftp.UploadFile(fs, remote, true);
                int finished = Interlocked.Increment(ref pushed);
                if (finished % 100 == 0 || finished == files.Count)
                {
                    Console.WriteLine($"      已推送 {finished}/{files.Count}");
                }

                return ValueTask.CompletedTask;
            });

        Console.WriteLine($"[seed][mirror] 完成：推送 {pushed} 个，跳过 {skipped} 个（仓库共 {files.Count} 个文件）");
    }
    finally
    {
        sftp.Disconnect();
    }
}

static CloudManifest? ManifestFromBytes(byte[]? bytes) =>
    bytes is null ? null : CloudUpdateFormat.TryParse<CloudManifest>(Encoding.UTF8.GetString(bytes));

/// <summary>
/// 清单类可变文件专用：先删除旧文件再上传。覆盖上传保留 file_id，而阿里云盘 CDN 按下载
/// URL 缓存内容，覆盖后边缘节点会继续发旧清单，客户端把新版本误判为"已是最新"
/// （1.16.135 发布实证）。删除使旧 file_id 失效，新上传得到新 file_id 与全新签名 URL。
/// </summary>
static async Task UploadMutableManifestAsync(
    INetdiskSession session, string folderId, string name, byte[] bytes, CancellationToken ct)
{
    await session.DeleteByNameAsync(folderId, name, ct).ConfigureAwait(false);
    await session.UploadBytesAsync(folderId, name, bytes, ct).ConfigureAwait(false);
}

static CloudManifest? ReadManifestFile(string path) =>
    ManifestFromBytes(File.ReadAllBytes(path));

static CloudVersionIndex? ReadIndexFile(string path) =>
    CloudUpdateFormat.TryParse<CloudVersionIndex>(File.ReadAllText(path));

static async Task<CloudManifest?> DownloadManifestAsync(
    INetdiskSession session, string folderId, string? name)
{
    if (string.IsNullOrEmpty(name)) return null;
    string fileName = name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.json";
    byte[]? bytes = await session.DownloadBytesAsync(folderId, fileName);
    return ManifestFromBytes(bytes);
}

static async Task<CloudVersionIndex?> DownloadIndexAsync(INetdiskSession session, string rootId)
{
    byte[]? bytes = await session.DownloadBytesAsync(rootId, CloudUpdateFormat.VersionsFile);
    return bytes is null ? null : CloudUpdateFormat.TryParse<CloudVersionIndex>(Encoding.UTF8.GetString(bytes));
}

static int CountChanged(CloudManifest? previous, CloudManifest current)
{
    if (previous is null) return current.Files.Count;
    var old = previous.Files.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.OrdinalIgnoreCase);
    return current.Files.Count(f =>
        !old.TryGetValue(f.Path, out string? sha) ||
        !string.Equals(sha, f.Sha256, StringComparison.OrdinalIgnoreCase));
}

static Dictionary<string, string> BuildCliCredentials()
{
    string cliPath = AliyunpanCliRuntime.LocateExecutable()
        ?? throw new InvalidOperationException("未找到 aliyunpan 命令行工具，请先安装并登录。");
    if (!AliyunpanCliRuntime.TryReadConfig(AliyunpanCliRuntime.ConfigPathFor(cliPath), out string? deviceId, out var token) ||
        string.IsNullOrEmpty(token.AccessToken))
    {
        throw new InvalidOperationException("读取 aliyunpan 配置失败，请先在命令行执行 aliyunpan login。");
    }

    return new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AliyunpanCliRuntime.CliLoginKey] = "true",
        [AliyunpanCliRuntime.CliDeviceIdKey] = deviceId ?? string.Empty,
        [AliyunTokenService.AccessTokenKey] = token.AccessToken,
        [AliyunTokenService.ExpiresAtKey] = token.ExpiredAt.ToString("O", CultureInfo.InvariantCulture),
    };
}

static async Task SliceToFileAsync(string path, int offset, int length, string target, CancellationToken ct)
{
    await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    source.Seek(offset, SeekOrigin.Begin);
    var buffer = new byte[length];
    int filled = 0;
    while (filled < length)
    {
        int read = await source.ReadAsync(buffer.AsMemory(filled, length - filled), ct);
        if (read == 0) throw new IOException($"读取 {path} 的 {offset}+{length} 分块时提前结束。");
        filled += read;
    }

    await File.WriteAllBytesAsync(target, buffer, ct);
}

static bool IsChunkName(string name) =>
    name.Length == 64 && name.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));

static void PrintSummary(CloudVersionSummary summary)
{
    Console.WriteLine();
    Console.WriteLine($"发布完成：v{summary.Version}");
    Console.WriteLine($"  文件 {summary.FileCount} 个 / {FormatBytes(summary.TotalBytes)}");
    Console.WriteLine($"  变更文件 {summary.ChangedFiles} 个，删除 {summary.DeletedFiles} 个");
    Console.WriteLine($"  本次上传 {summary.NewChunks} 个分块 / {FormatBytes(summary.NewBytes)}");
}

static string FormatBytes(long bytes) => bytes switch
{
    >= 1L << 30 => $"{bytes / 1073741824.0:F2} GB",
    >= 1L << 20 => $"{bytes / 1048576.0:F1} MB",
    >= 1L << 10 => $"{bytes / 1024.0:F0} KB",
    _ => $"{bytes} B",
};

static PublishOptions? ParseArgs(string[] arguments)
{
    string? version = null;
    string? source = null;
    string? compare = null;
    string? probe = null;
    string? importLogin = null;
    string? latestFile = null;
    string? versionsFile = null;
    string? loginPassphrase = null;
    string? seedMirror = null;
    string note = string.Empty;
    int keep = CloudUpdateFormat.DefaultKeep;
    bool dryRun = false;
    bool exportLogin = false;
    bool probeMove = false;
    bool noGc = false;

    for (int i = 0; i < arguments.Length; i++)
    {
        switch (arguments[i])
        {
            case "--version":
                version = ++i < arguments.Length ? arguments[i] : null;
                break;
            case "--source":
                source = ++i < arguments.Length ? arguments[i] : null;
                break;
            case "--note":
                note = ++i < arguments.Length ? arguments[i] : string.Empty;
                break;
            case "--compare":
                compare = ++i < arguments.Length ? arguments[i] : null;
                break;
            case "--probe":
                probe = ++i < arguments.Length ? arguments[i] : null;
                break;
            case "--keep":
                if (++i < arguments.Length && int.TryParse(arguments[i], out int k)) keep = Math.Max(1, k);
                break;
            case "--dry-run":
                dryRun = true;
                break;
            case "--export-login":
                exportLogin = true;
                break;
            case "--import-login":
                importLogin = ++i < arguments.Length ? arguments[i] : null;
                break;
            case "--probe-move":
                probeMove = true;
                break;
            case "--latest-file":
                latestFile = ++i < arguments.Length ? arguments[i] : null;
                break;
            case "--versions-file":
                versionsFile = ++i < arguments.Length ? arguments[i] : null;
                break;
            case "--no-gc":
                noGc = true;
                break;
            case "--login-passphrase":
                loginPassphrase = ++i < arguments.Length ? arguments[i] : null;
                break;
            case "--seed-mirror":
                seedMirror = ++i < arguments.Length ? arguments[i] : null;
                break;
        }
    }

    // The passphrase may also arrive through the environment, which keeps it out of shell history.
    loginPassphrase ??= Environment.GetEnvironmentVariable("WM_LOGIN_PACK_KEY");

    bool toolMode = exportLogin || probeMove ||
                    !string.IsNullOrWhiteSpace(probe) || !string.IsNullOrWhiteSpace(importLogin) ||
                    !string.IsNullOrWhiteSpace(seedMirror);
    if (!toolMode &&
        (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(source) || !Directory.Exists(source)))
    {
        return null;
    }

    return new PublishOptions(
        version?.Trim().TrimStart('v', 'V') ?? string.Empty,
        source is null ? string.Empty : Path.GetFullPath(source),
        note.Trim(), keep, dryRun,
        compare is null ? null : Path.GetFullPath(compare),
        probe is null ? null : Path.GetFullPath(probe),
        exportLogin,
        importLogin is null ? null : Path.GetFullPath(importLogin),
        probeMove,
        latestFile is null ? null : Path.GetFullPath(latestFile),
        versionsFile is null ? null : Path.GetFullPath(versionsFile),
        noGc,
        loginPassphrase,
        seedMirror is null ? null : Path.GetFullPath(seedMirror));
}

internal sealed record PublishOptions(
    string Version,
    string Source,
    string Note,
    int Keep,
    bool DryRun,
    string? CompareSource,
    string? ProbeFile,
    bool ExportLogin,
    string? ImportLogin,
    bool ProbeMove,
    string? LatestFile,
    string? VersionsFile,
    bool NoGc,
    string? LoginPassphrase,
    string? SeedMirror);
