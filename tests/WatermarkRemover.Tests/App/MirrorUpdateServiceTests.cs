using System.IO;
using System.Net;
using System.Text;
using WatermarkRemover.App.Updates;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// The 腾讯云镜像 channel end-to-end over a local HttpListener repository: same layout as the
/// real mirror (latest.json / versions.json / history/&lt;v&gt;.json / files/&lt;sha256&gt;), so the
/// tests exercise the real HTTP path — the retention-window gate, the incremental plan and the
/// chunk download + assembly — without touching the network.
/// </summary>
public sealed class MirrorUpdateServiceTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _baseUrl;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"wr-mirror-test-{Guid.NewGuid():N}");
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
    private readonly List<string> _cachedChunks = new();
    private readonly List<string> _served = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _loop;

    public MirrorUpdateServiceTests()
    {
        int port = 20000 + Random.Shared.Next(20000);
        _baseUrl = $"http://127.0.0.1:{port}/wm-update-feed/";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try { _listener.Stop(); } catch { /* best effort */ }
        try { _loop?.Wait(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
        _shutdown.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }

        // StageAsync 写进真实的 %LOCALAPPDATA%\WatermarkRemover\cloud-updates。分块缓存是
        // 内容寻址的、可重新下载的缓存，把本测试下载过的那几个文件删掉即可，不碰别人的缓存。
        foreach (string sha in _cachedChunks)
        {
            try { File.Delete(Path.Combine(MirrorUpdateService.ChunkCacheDirectory, sha)); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Unconfigured_url_means_no_channel()
    {
        var service = new MirrorUpdateService(new AppSettings { UpdateMirrorUrl = string.Empty });
        Assert.False(service.IsConfigured);

        CloudCheckResult check = await service.CheckAsync(new Version(1, 0, 0));
        Assert.Equal(CloudCheckStatus.NotConfigured, check.Status);
    }

    [Fact]
    public async Task Missing_latest_json_is_no_release_not_a_crash()
    {
        // 空仓库：latest.json 404 → NoRelease（调用方改走 GitHub），而不是异常。
        var service = CreateService();
        CloudCheckResult check = await service.CheckAsync(new Version(1, 0, 0));
        Assert.Equal(CloudCheckStatus.NoRelease, check.Status);
    }

    [Fact]
    public async Task Same_version_is_up_to_date_without_any_local_scan()
    {
        var service = CreateService();
        Put("latest.json", Serialize(BuildManifest("1.0.0")));

        CloudCheckResult check = await service.CheckAsync(new Version(1, 0, 0));
        Assert.Equal(CloudCheckStatus.UpToDate, check.Status);
        Assert.Equal(new Version(1, 0, 0), check.Latest);
    }

    [Fact]
    public async Task Unparseable_version_fails_the_check()
    {
        var service = CreateService();
        Put("latest.json", Encoding.UTF8.GetBytes("{\"version\":\"not-a-version\",\"files\":[]}"));

        CloudCheckResult check = await service.CheckAsync(new Version(1, 0, 0));
        Assert.Equal(CloudCheckStatus.Failed, check.Status);
    }

    [Fact]
    public async Task Version_outside_the_retention_window_declines()
    {
        // 镜像只保留最近几个版本：history/1.0.0.json 不在，客户端立刻得到确定答案
        // （NoRelease → GitHub 全量包），而不是下载到一半才 404。
        var service = CreateService();
        Put("latest.json", Serialize(BuildManifest("2.0.0")));
        Put("versions.json", Serialize(new CloudVersionIndex
        {
            Versions = new List<CloudVersionSummary> { new() { Version = "2.0.0" } },
        }));

        CloudCheckResult check = await service.CheckAsync(new Version(1, 0, 0));
        Assert.Equal(CloudCheckStatus.NoRelease, check.Status);
        Assert.Contains("保留窗口", check.Message);
    }

    [Fact]
    public async Task New_version_plans_only_the_changed_file_chunks()
    {
        // 两个真实文件：一个内容不变（整文件哈希相同，一个字节都不下），一个内容变了
        // （只下它的分块）。versions.json 故意 404：它是可选的，不能把检查打成失败。
        var service = CreateService();
        string appDir = AppContext.BaseDirectory;
        string unchanged = PickSmallFile(appDir, 0);
        string changed = PickSmallFile(appDir, 1);

        string remoteDir = Path.Combine(_root, "remote");
        Directory.CreateDirectory(remoteDir);
        File.Copy(Path.Combine(appDir, unchanged), Path.Combine(remoteDir, unchanged));
        byte[] newContent = Encoding.UTF8.GetBytes("这是新版本的文件内容，与旧版本完全不同。");
        File.WriteAllBytes(Path.Combine(remoteDir, changed), newContent);

        // 删除列表里的文件必须真的存在于安装目录，planner 才会把它算进 DeletedFiles
        // （不在的文件没什么可删的）——在测试目录里放一个，用完一定删掉。
        const string removed = "wr-mirror-test-removed.txt";
        string localRemoved = Path.Combine(appDir, removed);
        File.WriteAllText(localRemoved, "这个文件在新版本里被删掉了");
        try
        {
            CloudManifest manifest = CloudUpdatePlanner.BuildManifest(remoteDir, "2.0.0", "镜像测试", null);
            manifest.Deleted = new List<string> { removed };
            Put("latest.json", Serialize(manifest));
            Put("history/1.0.0.json", Encoding.UTF8.GetBytes("{\"version\":\"1.0.0\",\"files\":[]}"));

            CloudCheckResult check = await service.CheckAsync(new Version(1, 0, 0));
            Assert.True(check.Status == CloudCheckStatus.UpdateAvailable,
                $"status={check.Status}, message={check.Message}, " +
                $"bypassed={MirrorUpdateSource.IsBypassed}, served=[{string.Join("; ", _served)}]");
            Assert.NotNull(check.Plan);
            Assert.Equal(new[] { changed }, check.Plan!.ChangedFiles.Select(f => f.Path).ToArray());
            Assert.Contains(removed, check.Plan.DeletedFiles);
            Assert.Equal(
                manifest.Files.First(f => f.Path == changed).Chunks.Select(c => c.Sha256).ToHashSet(StringComparer.Ordinal),
                check.Plan.RequiredChunks.Select(c => c.Sha256).ToHashSet(StringComparer.Ordinal));

            // 下载 + 组装：分块走 HTTP，落进暂存目录，哈希对得上才收工。
            foreach (CloudChunkRef chunk in check.Plan.RequiredChunks)
            {
                Put($"files/{chunk.Sha256}", newContent);
            }

            CloudStageResult staged = await service.StageAsync(
                check, progress: null, CancellationToken.None);
            Assert.Contains(changed, staged.Files);
            Assert.Contains(removed, staged.Deletes);
            string stagedFile = Path.Combine(staged.StagingDirectory, changed);
            Assert.True(File.Exists(stagedFile));
            Assert.Equal(newContent, await File.ReadAllBytesAsync(stagedFile));
            _cachedChunks.AddRange(check.Plan!.RequiredChunks.Select(c => c.Sha256));

            try { Directory.Delete(staged.StagingDirectory, recursive: true); } catch { /* best effort */ }
        }
        finally
        {
            try { File.Delete(localRemoved); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Missing_chunk_fails_the_download_instead_of_hanging()
    {
        var service = CreateService();
        string appDir = AppContext.BaseDirectory;
        string changed = PickSmallFile(appDir, 1);

        string remoteDir = Path.Combine(_root, "remote-missing");
        Directory.CreateDirectory(remoteDir);
        File.WriteAllBytes(Path.Combine(remoteDir, changed),
            Encoding.UTF8.GetBytes("下载会失败：镜像上没有这个分块。"));

        CloudManifest manifest = CloudUpdatePlanner.BuildManifest(remoteDir, "2.0.0", "镜像测试", null);
        Put("latest.json", Serialize(manifest));
        Put("history/1.0.0.json", Encoding.UTF8.GetBytes("{\"version\":\"1.0.0\",\"files\":[]}"));

        CloudCheckResult check = await service.CheckAsync(new Version(1, 0, 0));
        Assert.Equal(CloudCheckStatus.UpdateAvailable, check.Status);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.StageAsync(check, progress: null, CancellationToken.None));

        // 失败路径不能留 .part 半成品（下次检查更新会重复利用缓存目录）。
        string[] leftovers = Directory.Exists(MirrorUpdateService.ChunkCacheDirectory)
            ? Directory.GetFiles(MirrorUpdateService.ChunkCacheDirectory, "*.part", SearchOption.TopDirectoryOnly)
            : Array.Empty<string>();
        Assert.Empty(leftovers);
    }

    private MirrorUpdateService CreateService() =>
        new(new AppSettings { UpdateMirrorUrl = _baseUrl });

    private static CloudManifest BuildManifest(string version)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"wr-mirror-src-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "app.dll"), "payload");
            return CloudUpdatePlanner.BuildManifest(dir, version, "test", null);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Picks the Nth-smallest regular app file (stable across runs; symbols are skipped).</summary>
    private static string PickSmallFile(string appDir, int index) =>
        new DirectoryInfo(appDir).GetFiles()
            .Where(f => f.Length > 0 && f.Length < 512 * 1024 &&
                        (f.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                         f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                         f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f.Length)
            .ThenBy(f => f.Name, StringComparer.Ordinal)
            .ElementAt(index)
            .Name;

    private static byte[] Serialize(object value) =>
        Encoding.UTF8.GetBytes(CloudUpdateFormat.Serialize(value));

    private void Put(string relative, byte[] bytes) =>
        _files[relative.Replace('\\', '/')] = bytes;

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening && !_shutdown.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                return; // listener stopped
            }

            string path = context.Request.Url?.AbsolutePath ?? "/";
            const string prefix = "/wm-update-feed/";
            string relative = path.StartsWith(prefix, StringComparison.Ordinal)
                ? path[prefix.Length..]
                : string.Empty;
            lock (_served)
            {
                _served.Add($"{relative} -> {(_files.ContainsKey(relative) ? 200 : 404)}");
            }
            if (_files.TryGetValue(relative, out byte[]? bytes))
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/octet-stream";
                await context.Response.OutputStream.WriteAsync(bytes, _shutdown.Token).ConfigureAwait(false);
            }
            else
            {
                context.Response.StatusCode = 404;
            }

            context.Response.Close();
        }
    }
}
