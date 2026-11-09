using System.Globalization;
using System.Text.Json;
using WatermarkRemover.Core.Netdisk;

namespace WatermarkRemover.Netdisk;

/// <summary>One storage space of the account (我的文件 / a 团队文件 space).</summary>
public sealed record DingTalkSpace(string SpaceId, string Name);

/// <summary>Space inventory for one DingTalk account.</summary>
public sealed record DingTalkSpaces(string MySpaceId, IReadOnlyList<DingTalkSpace> TeamSpaces)
{
    public static DingTalkSpaces Default { get; } = new(string.Empty, Array.Empty<DingTalkSpace>());
}

/// <summary>
/// DingTalk Drive (钉盘) session over the local dws CLI.
///
/// 钉盘 exposes three top-level areas, mirroring the DingTalk client: 我的文件 (one personal
/// space), 团队文件 (org spaces), 聊天文件 (one file space per conversation). They are modelled as
/// three drives here. Only 我的文件 has AWS-style flat ids; folder ids from 团队文件/聊天文件 are
/// prefixed with their context ("team:&lt;spaceId&gt;:&lt;nodeId&gt;", "chat:&lt;cid&gt;:&lt;nodeId&gt;")
/// so one folder id is enough to resolve the --space-id every CLI call needs.
/// </summary>
public sealed class DingTalkDriveSession : INetdiskSession
{
    public const string MyDriveId = "my";
    public const string TeamDriveId = "team";
    public const string ChatDriveId = "chat";

    /// <summary>dws +list caps a page at 50; +search at 30.</summary>
    private const int MaxListPage = 50;
    private const int MaxSearchPage = 30;

    /// <summary>File transfer keeps the CLI's own 5-minute floor and then some.</summary>
    private const int TransferTimeoutSeconds = 1800;

    private readonly DwsCliRunner _runner;
    private readonly DingTalkSpaces _spaces;
    private readonly Dictionary<string, string> _chatSpaceCache = new(StringComparer.Ordinal);
    private string _driveId = MyDriveId;

    public DingTalkDriveSession(
        DwsCliRunner runner,
        NetdiskCredentials credentials,
        NetdiskAccountInfo account,
        DingTalkSpaces? spaces = null)
    {
        _runner = runner;
        CurrentCredentials = credentials;
        Account = account;
        _spaces = spaces ?? DingTalkSpaces.Default;
    }

    public NetdiskAccountInfo Account { get; }

    public NetdiskCredentials CurrentCredentials { get; }

    public IReadOnlyList<NetdiskDrive> Drives { get; } = new[]
    {
        new NetdiskDrive(MyDriveId, "我的文件"),
        new NetdiskDrive(TeamDriveId, "团队文件"),
        new NetdiskDrive(ChatDriveId, "聊天文件"),
    };

    public string CurrentDriveId => _driveId;

    public Task SwitchDriveAsync(string driveId, CancellationToken cancellationToken = default)
    {
        if (Drives.All(d => !string.Equals(d.Id, driveId, StringComparison.Ordinal)))
        {
            throw new NotSupportedException($"钉钉云盘没有存储空间「{driveId}」。");
        }

        _driveId = driveId;
        return Task.CompletedTask;
    }

    /// <summary>Enterprise quota — the number the DingTalk client shows (used / total for the org).</summary>
    public async Task<NetdiskQuota> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        using var doc = await RunJsonAsync(new[] { "drive", "quota" }, cancellationToken).ConfigureAwait(false);
        var payload = DwsJson.Payload(doc.RootElement);
        var used = DwsJson.FirstLong(payload, "usedBytes", "usedSize", "used");
        var total = DwsJson.FirstLong(payload, "totalBytes", "totalSize", "total", "capacity");
        return new NetdiskQuota(used ?? 0, total ?? 0);
    }

    public async Task<NetdiskPage> ListAsync(
        string parentFolderId,
        int pageSize = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(pageSize, 1, MaxListPage).ToString(CultureInfo.InvariantCulture);
        switch (_driveId)
        {
            case TeamDriveId when string.IsNullOrEmpty(parentFolderId):
                return new NetdiskPage(
                    _spaces.TeamSpaces
                        .Select(space => new NetdiskFile(
                            $"team:{space.SpaceId}", space.Name, IsFolder: true, Size: 0, ModifiedAt: null))
                        .ToList(),
                    NextCursor: null);

            case TeamDriveId:
            {
                var (spaceId, nodeId) = ParseContextId(parentFolderId, "team");
                var page = await ListSpaceAsync(spaceId, nodeId, limit, cursor, cancellationToken)
                    .ConfigureAwait(false);
                return MapPage(page, rawId => $"team:{spaceId}:{rawId}");
            }

            case ChatDriveId when string.IsNullOrEmpty(parentFolderId):
                return await ListChatConversationsAsync(limit, cursor, cancellationToken).ConfigureAwait(false);

            case ChatDriveId:
            {
                var (conversationId, nodeId) = ParseContextId(parentFolderId, "chat");
                var spaceId = await ResolveChatSpaceAsync(conversationId, cancellationToken).ConfigureAwait(false);
                var page = await ListSpaceAsync(spaceId, nodeId, limit, cursor, cancellationToken)
                    .ConfigureAwait(false);
                return MapPage(page, rawId => $"chat:{conversationId}:{rawId}");
            }

            default:
                return await ListSpaceAsync(_spaces.MySpaceId, parentFolderId, limit, cursor, cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    public async Task<NetdiskPage> SearchAsync(
        string keyword,
        int pageSize = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (_driveId != MyDriveId)
        {
            throw new NotSupportedException("「团队文件」「聊天文件」暂不支持跨空间搜索，请进入具体空间后浏览。");
        }

        var args = new List<string>
        {
            "drive", "+search", "--query", keyword,
            "--limit", Math.Clamp(pageSize, 1, MaxSearchPage).ToString(CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(cursor))
        {
            args.Add("--cursor");
            args.Add(cursor);
        }

        using var doc = await RunJsonAsync(args, cancellationToken).ConfigureAwait(false);
        return ToPage(doc.RootElement);
    }

    public async Task<string?> GetNodeNameAsync(string nodeId, CancellationToken cancellationToken = default)
    {
        var raw = StripContext(nodeId);
        using var doc = await RunJsonAsync(
            new[] { "drive", "info", "--node", raw }, cancellationToken).ConfigureAwait(false);
        var payload = DwsJson.Payload(doc.RootElement);
        return DwsJson.FirstString(payload, "name", "fileName", "title");
    }

    public async Task<NetdiskFile> CreateFolderAsync(
        string parentFolderId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var (spaceId, contextKey, folderId) = ResolveTarget(parentFolderId, "新建文件夹");
        var args = new List<string> { "drive", "+create-folder", "--name", name };
        AppendSpace(args, spaceId);
        if (!string.IsNullOrEmpty(folderId))
        {
            args.Add("--folder");
            args.Add(folderId);
        }

        using var doc = await RunJsonAsync(args, cancellationToken).ConfigureAwait(false);
        var payload = DwsJson.Payload(doc.RootElement);
        var id = DwsJson.NodeId(payload)
            ?? throw new NetdiskException("钉钉云盘未返回新建文件夹的 ID。", "DwsNoNodeId");
        return new NetdiskFile(EncodeId(contextKey, id), name, IsFolder: true, Size: 0,
            ModifiedAt: DateTimeOffset.Now, ParentId: parentFolderId);
    }

    public async Task DeleteAsync(IReadOnlyList<string> fileIds, CancellationToken cancellationToken = default)
    {
        foreach (var id in fileIds)
        {
            // +delete moves into the recycle bin; the management window asks for confirmation first.
            await _runner.RunAsync(
                new[] { "drive", "+delete", "--node", StripContext(id) },
                new DwsRunOptions(Yes: true),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public Task MoveAsync(
        IReadOnlyList<string> fileIds,
        string targetFolderId,
        CancellationToken cancellationToken = default)
    {
        if (_driveId != MyDriveId)
        {
            throw new NotSupportedException(
                "「团队文件」「聊天文件」暂不支持移动，请到钉钉客户端或进入「我的文件」后操作。");
        }

        return MoveCoreAsync(fileIds, targetFolderId, cancellationToken);
    }

    private async Task MoveCoreAsync(
        IReadOnlyList<string> fileIds,
        string targetFolderId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(targetFolderId))
        {
            throw new NotSupportedException("钉钉云盘移动必须指定目标文件夹。");
        }

        foreach (var id in fileIds)
        {
            await _runner.RunAsync(
                new[] { "drive", "+move", "--node", StripContext(id), "--folder", StripContext(targetFolderId) },
                new DwsRunOptions(Yes: true),
                cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<NetdiskFile> UploadAsync(
        string parentFolderId,
        string localPath,
        IProgress<NetdiskTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var name = Path.GetFileName(localPath);
        return UploadAsAsync(parentFolderId, localPath, name, overwrite: false, progress, cancellationToken);
    }

    public async Task<NetdiskFile> UploadAsAsync(
        string parentFolderId,
        string localPath,
        string name,
        bool overwrite,
        IProgress<NetdiskTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localPath))
        {
            throw new FileNotFoundException("上传的本地文件不存在。", localPath);
        }

        var (spaceId, contextKey, folderId) = ResolveTarget(parentFolderId, "上传文件");
        var args = new List<string> { "drive", "+upload" };
        if (overwrite)
        {
            // The CLI's overwrite mode replaces one explicit node: find it by name in the target.
            var existing = await FindChildAsync(parentFolderId, name, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                throw new NetdiskException(
                    $"钉钉云盘中找不到要覆盖的「{name}」。", "DwsOverwriteTargetMissing");
            }

            args.Add("--node");
            args.Add(StripContext(existing.Id));
        }

        args.Add("--file");
        args.Add(Path.GetFileName(localPath));
        if (!string.Equals(name, Path.GetFileName(localPath), StringComparison.Ordinal))
        {
            args.Add("--file-name");
            args.Add(name);
        }

        AppendSpace(args, spaceId);
        if (!string.IsNullOrEmpty(folderId))
        {
            args.Add("--folder");
            args.Add(folderId);
        }

        var size = new FileInfo(localPath).Length;
        progress?.Report(new NetdiskTransferProgress(0, size));
        var result = await _runner.RunAsync(
            args,
            new DwsRunOptions(
                WorkingDirectory: Path.GetDirectoryName(Path.GetFullPath(localPath)),
                Yes: true,
                TimeoutSeconds: TransferTimeoutSeconds,
                ProcessTimeout: TimeSpan.FromMinutes(35)),
            cancellationToken).ConfigureAwait(false);
        using var doc = DwsCliRunner.ParseJsonOrThrow(result, "上传文件");
        var payload = DwsJson.Payload(doc.RootElement);
        var id = DwsJson.NodeId(payload)
            ?? throw new NetdiskException("钉钉云盘未返回上传文件的节点 ID。", "DwsNoNodeId");
        progress?.Report(new NetdiskTransferProgress(size, size));
        return new NetdiskFile(EncodeId(contextKey, id), name, IsFolder: false, size,
            DateTimeOffset.Now, parentFolderId);
    }

    public async Task DownloadAsync(
        string fileId,
        string localPath,
        IProgress<NetdiskTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(localPath))
        {
            var existing = new FileInfo(localPath).Length;
            progress?.Report(new NetdiskTransferProgress(existing, existing));
            return;
        }

        var (spaceId, rawId) = await ResolveFileAsync(fileId, cancellationToken).ConfigureAwait(false);
        var args = new List<string> { "drive", "+download", "--node", rawId, "--output", Path.GetFileName(localPath) };
        AppendSpace(args, spaceId);

        // +download refuses to overwrite and wants a working-directory-relative output path, so
        // the transfer lands in a fresh temp folder and is moved into place afterwards — a failed
        // download never leaves a half-written file at the user-chosen path.
        var tempDirectory = Directory.CreateTempSubdirectory("wr-dingtalk-dl-");
        try
        {
            var result = await _runner.RunAsync(
                args,
                new DwsRunOptions(
                    WorkingDirectory: tempDirectory.FullName,
                    TimeoutSeconds: TransferTimeoutSeconds,
                    ProcessTimeout: TimeSpan.FromMinutes(35)),
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new NetdiskException(
                    $"下载失败：{DwsCliRunner.DescribeError(result)}", "DwsDownloadFailed");
            }

            var downloaded = Path.Combine(tempDirectory.FullName, Path.GetFileName(localPath));
            if (!File.Exists(downloaded))
            {
                throw new NetdiskException(
                    "钉钉云盘下载已完成，但本地文件未生成。", "DwsDownloadMissing");
            }

            progress?.Report(new NetdiskTransferProgress(new FileInfo(downloaded).Length, null));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localPath))!);
            File.Move(downloaded, localPath, overwrite: true);
        }
        finally
        {
            try { Directory.Delete(tempDirectory.FullName, recursive: true); }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>
    /// Discovers the account's spaces: 我的文件 (mySpace) and every 团队文件 space (orgSpace,
    /// paginated). 聊天文件 needs no discovery — its root is the conversation list.
    /// </summary>
    public static async Task<DingTalkSpaces> DiscoverSpacesAsync(
        DwsCliRunner runner,
        CancellationToken cancellationToken = default)
    {
        var my = await ListSpacesAsync(runner, "mySpace", cancellationToken).ConfigureAwait(false);
        var team = await ListSpacesAsync(runner, "orgSpace", cancellationToken).ConfigureAwait(false);
        return new DingTalkSpaces(
            my.FirstOrDefault()?.SpaceId ?? string.Empty,
            team);
    }

    private static async Task<List<DingTalkSpace>> ListSpacesAsync(
        DwsCliRunner runner,
        string type,
        CancellationToken cancellationToken)
    {
        var spaces = new List<DingTalkSpace>();
        string? cursor = null;
        for (var page = 0; page < 5; page++)
        {
            var args = new List<string>
            {
                "wiki", "space", "list", "--type", type, "--limit", "50",
            };
            if (!string.IsNullOrEmpty(cursor))
            {
                args.Add("--cursor");
                args.Add(cursor);
            }

            var result = await runner.RunAsync(args, DwsRunOptions.Default, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new NetdiskException(
                    $"读取钉盘空间失败：{DwsCliRunner.DescribeError(result)}", "DwsSpaceListFailed");
            }

            using var doc = JsonDocument.Parse(result.StdOut);
            var root = doc.RootElement;
            if (!root.TryGetProperty("result", out var payload) ||
                !payload.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            foreach (var item in items.EnumerateArray())
            {
                var id = DwsJson.FirstString(item, "spaceId");
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                spaces.Add(new DingTalkSpace(id!, DwsJson.FirstString(item, "spaceName") ?? "未命名空间"));
            }

            cursor = DwsJson.FirstString(payload, "nextToken", "nextCursor");
            if (string.IsNullOrEmpty(cursor))
            {
                break;
            }
        }

        return spaces;
    }

    /// <summary>Lists conversations for the 聊天文件 drive root (groups only; p2p has no file space API).</summary>
    private async Task<NetdiskPage> ListChatConversationsAsync(
        string limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var args = new List<string>
        {
            "chat", "+chat-list", "--types", "group", "--page-size", limit,
        };
        if (!string.IsNullOrEmpty(cursor))
        {
            args.Add("--page-token");
            args.Add(cursor);
        }

        using var doc = await RunJsonAsync(args, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        var items = new List<NetdiskFile>();
        if (root.TryGetProperty("chats", out var chats) && chats.ValueKind == JsonValueKind.Array)
        {
            foreach (var chat in chats.EnumerateArray())
            {
                var conversationId = DwsJson.FirstString(chat, "openConversationId");
                if (string.IsNullOrEmpty(conversationId))
                {
                    continue;
                }

                items.Add(new NetdiskFile(
                    $"chat:{conversationId}",
                    DwsJson.FirstString(chat, "name") ?? "未命名会话",
                    IsFolder: true,
                    Size: 0,
                    ModifiedAt: null));
            }
        }

        var hasMore = DwsJson.FirstBool(root, "hasMore");
        var next = root.TryGetProperty("nextCursor", out var nextProp) &&
                   nextProp.ValueKind == JsonValueKind.Number
            ? nextProp.GetInt64().ToString(CultureInfo.InvariantCulture)
            : null;
        return new NetdiskPage(items, hasMore ? next : null);
    }

    /// <summary>Resolves one conversation's file space id (extension.newCSpaceIdIM).</summary>
    private async Task<string> ResolveChatSpaceAsync(string conversationId, CancellationToken cancellationToken)
    {
        if (_chatSpaceCache.TryGetValue(conversationId, out var cached))
        {
            return cached;
        }

        using var doc = await RunJsonAsync(
            new[] { "chat", "+conversation-info", "--group", conversationId },
            cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        var spaceId = root.TryGetProperty("result", out var result) &&
                      result.TryGetProperty("conversationInfo", out var info) &&
                      info.TryGetProperty("extension", out var extension)
            ? DwsJson.FirstString(extension, "newCSpaceIdIM")
            : null;
        if (string.IsNullOrEmpty(spaceId))
        {
            throw new NetdiskException(
                "该会话没有可访问的聊天文件空间。", "DwsChatSpaceMissing");
        }

        _chatSpaceCache[conversationId] = spaceId!;
        return spaceId!;
    }

    private async Task<NetdiskPage> ListSpaceAsync(
        string spaceId,
        string folderId,
        string limit,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var args = new List<string> { "drive", "+list", "--limit", limit };
        AppendSpace(args, spaceId);
        if (!string.IsNullOrEmpty(folderId))
        {
            args.Add("--folder");
            args.Add(folderId);
        }

        if (!string.IsNullOrEmpty(cursor))
        {
            args.Add("--cursor");
            args.Add(cursor);
        }

        using var doc = await RunJsonAsync(args, cancellationToken).ConfigureAwait(false);
        return ToPage(doc.RootElement);
    }

    /// <summary>Walks one folder (bounded) to find a child by exact name, for overwrite uploads.</summary>
    private async Task<NetdiskFile?> FindChildAsync(
        string parentFolderId,
        string name,
        CancellationToken cancellationToken)
    {
        string? cursor = null;
        for (var page = 0; page < 5; page++)
        {
            var result = await ListAsync(parentFolderId, MaxListPage, cursor, cancellationToken)
                .ConfigureAwait(false);
            var match = result.Items.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }

            if (result.NextCursor is not { } next)
            {
                return null;
            }

            cursor = next;
        }

        return null;
    }

    /// <summary>Space to use for a target folder; throws for the synthetic roots of 团队/聊天.</summary>
    private (string SpaceId, string ContextKey, string FolderId) ResolveTarget(
        string parentFolderId,
        string action)
    {
        switch (_driveId)
        {
            case TeamDriveId:
                if (string.IsNullOrEmpty(parentFolderId))
                {
                    throw new NetdiskException(
                        $"请先进入具体的团队文件夹再{action}。", "DwsTeamRootAction");
                }

                var (spaceId, nodeId) = ParseContextId(parentFolderId, "team");
                return (spaceId, spaceId, nodeId);

            case ChatDriveId:
                if (string.IsNullOrEmpty(parentFolderId))
                {
                    throw new NetdiskException(
                        $"请先进入具体的会话文件夹再{action}。", "DwsChatRootAction");
                }

                var (conversationId, chatNodeId) = ParseContextId(parentFolderId, "chat");
                // Resolved lazily by the caller's first command; the cache is warm once listed.
                var chatSpace = _chatSpaceCache.TryGetValue(conversationId, out var cached)
                    ? cached
                    : throw new NetdiskException(
                        "聊天文件空间尚未解析，请重新进入该会话后再操作。", "DwsChatSpaceNotResolved");
                return (chatSpace, conversationId, chatNodeId);

            default:
                return (_spaces.MySpaceId, string.Empty, parentFolderId);
        }
    }

    /// <summary>Resolution for an existing file id (download needs its space).</summary>
    private async Task<(string SpaceId, string RawId)> ResolveFileAsync(
        string fileId,
        CancellationToken cancellationToken)
    {
        if (fileId.StartsWith("team:", StringComparison.Ordinal))
        {
            var (spaceId, raw) = ParseContextId(fileId, "team");
            return (spaceId, raw);
        }

        if (fileId.StartsWith("chat:", StringComparison.Ordinal))
        {
            var (conversationId, raw) = ParseContextId(fileId, "chat");
            var spaceId = await ResolveChatSpaceAsync(conversationId, cancellationToken).ConfigureAwait(false);
            return (spaceId, raw);
        }

        return (_spaces.MySpaceId, fileId);
    }

    private static (string Context, string RawId) ParseContextId(string id, string prefix)
    {
        // team:<spaceId>[:<node>] / chat:<conversationId>[:<node>]
        var parts = id.Split(':', 3);
        if (parts.Length < 2 || !string.Equals(parts[0], prefix, StringComparison.Ordinal))
        {
            throw new NetdiskException($"无法识别的节点 ID：{id}", "DwsBadNodeId");
        }

        return (parts[1], parts.Length == 3 ? parts[2] : string.Empty);
    }

    private string EncodeId(string contextKey, string rawId) => _driveId switch
    {
        TeamDriveId => $"team:{contextKey}:{rawId}",
        ChatDriveId => $"chat:{contextKey}:{rawId}",
        _ => rawId,
    };

    private static string StripContext(string id)
    {
        if (id.StartsWith("team:", StringComparison.Ordinal) ||
            id.StartsWith("chat:", StringComparison.Ordinal))
        {
            var parts = id.Split(':', 3);
            return parts.Length == 3 ? parts[2] : string.Empty;
        }

        return id;
    }

    private static void AppendSpace(List<string> args, string spaceId)
    {
        if (!string.IsNullOrEmpty(spaceId))
        {
            args.Add("--space-id");
            args.Add(spaceId);
        }
    }

    private static NetdiskPage MapPage(NetdiskPage page, Func<string, string> encode) =>
        new(page.Items
                .Select(item => item with
                {
                    Id = encode(item.Id),
                    ParentId = item.ParentId is null ? null : encode(item.ParentId),
                })
                .ToList(),
            page.NextCursor);

    private delegate NetdiskFile ItemFactory(string id, string name, bool isFolder, long size, DateTimeOffset? modifiedAt, string? parentId);

    private static NetdiskPage ToPage(JsonElement root)
    {
        var items = new List<NetdiskFile>();
        if (DwsJson.FirstArray(root, "files", "items", "list", "entries") is { } array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var id = DwsJson.NodeId(item);
                var name = DwsJson.FirstString(item, "name", "fileName", "title");
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                {
                    continue;
                }

                items.Add(new NetdiskFile(
                    id!,
                    name!,
                    DwsJson.IsFolder(item),
                    DwsJson.FirstLong(item, "size", "fileSize", "sizeBytes") ?? 0,
                    DwsJson.ModifiedAt(item),
                    DwsJson.FirstString(item, "parentId", "parentFolderId", "folderId")));
            }
        }

        var payload = DwsJson.Payload(root);
        var hasMore = DwsJson.FirstBool(payload, "hasMore", "has_more");
        var nextCursor = DwsJson.FirstString(payload, "nextCursor", "nextToken", "nextPageToken", "next_page_token");
        return new NetdiskPage(items, hasMore && !string.IsNullOrEmpty(nextCursor) ? nextCursor : null);
    }

    private async Task<JsonDocument> RunJsonAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(args, DwsRunOptions.Default, cancellationToken).ConfigureAwait(false);
        var operation = args[0] switch
        {
            "drive" when args.Count > 1 && args[1] == "+search" => "搜索",
            "drive" when args.Count > 1 && args[1] == "+list" => "读取钉盘",
            "chat" when args.Count > 1 && args[1] == "+chat-list" => "读取聊天列表",
            "chat" => "读取会话信息",
            _ => "读取钉盘",
        };
        return DwsCliRunner.ParseJsonOrThrow(result, operation);
    }
}
