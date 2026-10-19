using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Netdisk.Net;

namespace WatermarkRemover.Netdisk;

/// <summary>
/// A live connection to one 阿里云盘 account. Every request goes through one authorize-send path
/// that proactively refreshes the access token and, when the platform rejects it mid-session,
/// rotates once and replays the request before failing. Folder ids follow the shared vocabulary:
/// the empty string is the drive root (the API itself wants the literal "root").
/// </summary>
public sealed class AliyunDriveSession : INetdiskSession
{
    private readonly HttpClient _http;
    private readonly AliyunTokenService _tokens;
    private readonly Func<DateTimeOffset> _clock;
    private NetdiskCredentials _credentials;
    private string _driveId = string.Empty;

    internal AliyunDriveSession(
        HttpClient http,
        AliyunTokenService tokens,
        NetdiskCredentials credentials,
        Func<DateTimeOffset>? clock = null)
    {
        _http = http;
        _tokens = tokens;
        _credentials = credentials;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public NetdiskAccountInfo Account { get; private set; } = new(string.Empty, string.Empty);

    public NetdiskCredentials CurrentCredentials => _credentials;

    /// <summary>Identifies the account and resolves the drive id; called by the provider at connect time.</summary>
    internal static async Task<AliyunDriveSession> OpenAsync(
        HttpClient http,
        AliyunTokenService tokens,
        NetdiskCredentials credentials,
        Func<DateTimeOffset>? clock,
        CancellationToken cancellationToken)
    {
        var session = new AliyunDriveSession(http, tokens, credentials, clock);

        var user = await session
            .SendAsync(HttpMethod.Get, AlipanApi.UserInfoPath, null, cancellationToken)
            .ConfigureAwait(false);
        session.Account = AlipanJson.ParseUserInfo(user);

        var drive = await session
            .SendAsync(HttpMethod.Post, AlipanApi.DriveInfoPath, new Dictionary<string, object?>(), cancellationToken)
            .ConfigureAwait(false);
        session._driveId = AlipanJson.TryGetString(drive, "default_drive_id", "drive_id")
            ?? throw new NetdiskException("getDriveInfo 响应缺少 default_drive_id，无法定位网盘。", "MissingDriveId");

        return session;
    }

    public async Task<NetdiskQuota> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            HttpMethod.Post,
            AlipanApi.SpaceInfoPath,
            new Dictionary<string, object?> { ["drive_id"] = _driveId },
            cancellationToken).ConfigureAwait(false);
        return AlipanJson.ParseSpaceInfo(response);
    }

    public async Task<NetdiskPage> ListAsync(
        string parentFolderId,
        int pageSize = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["drive_id"] = _driveId,
            ["parent_file_id"] = NormalizeParent(parentFolderId),
            ["limit"] = pageSize,
            ["order_by"] = "name",
            ["order_direction"] = "ASC",
        };
        if (!string.IsNullOrEmpty(cursor))
        {
            body["marker"] = cursor;
        }

        var response = await SendAsync(HttpMethod.Post, AlipanApi.ListPath, body, cancellationToken).ConfigureAwait(false);
        return AlipanJson.ParseListPage(response);
    }

    public async Task<NetdiskFile> CreateFolderAsync(
        string parentFolderId,
        string name,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["drive_id"] = _driveId,
            ["parent_file_id"] = NormalizeParent(parentFolderId),
            ["name"] = name,
            ["type"] = "folder",
            ["check_name_mode"] = "refuse",
        };
        var response = await SendAsync(HttpMethod.Post, AlipanApi.CreatePath, body, cancellationToken).ConfigureAwait(false);
        return AlipanJson.ParseFileNode(response);
    }

    /// <summary>Moves entries to the recycle bin; the API takes one file id per call.</summary>
    public async Task DeleteAsync(IReadOnlyList<string> fileIds, CancellationToken cancellationToken = default)
    {
        foreach (var fileId in fileIds)
        {
            await SendAsync(
                HttpMethod.Post,
                AlipanApi.TrashPath,
                new Dictionary<string, object?> { ["drive_id"] = _driveId, ["file_id"] = fileId },
                cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<NetdiskFile> UploadAsync(
        string parentFolderId,
        string localPath,
        IProgress<NetdiskTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(localPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("待上传的文件不存在。", localPath);
        }

        var parts = AlipanUpload.PlanParts(file.Length, AlipanUpload.DefaultPartSize);
        var createBody = new Dictionary<string, object?>
        {
            ["drive_id"] = _driveId,
            ["parent_file_id"] = NormalizeParent(parentFolderId),
            ["name"] = file.Name,
            ["type"] = "file",
            ["size"] = file.Length,
            ["check_name_mode"] = "auto_rename",
            ["part_info_list"] = parts
                .Select(p => new Dictionary<string, object?> { ["part_number"] = p.PartNumber, ["part_size"] = p.Length })
                .ToList(),
        };

        var create = await SendAsync(HttpMethod.Post, AlipanApi.CreatePath, createBody, cancellationToken).ConfigureAwait(false);
        var fileId = AlipanJson.TryGetString(create, "file_id")
            ?? throw new NetdiskException("create 响应缺少 file_id，无法创建上传任务。", "MalformedCreateResponse");

        // 秒传 / 已存在: nothing to transfer, the create response already is the file node.
        if (AlipanJson.TryGetBool(create, "exist") == true || AlipanJson.TryGetBool(create, "rapid_upload") == true)
        {
            progress?.Report(new NetdiskTransferProgress(file.Length, file.Length));
            return AlipanJson.ParseFileNode(create);
        }

        var uploadId = AlipanJson.TryGetString(create, "upload_id")
            ?? throw new NetdiskException("create 响应缺少 upload_id，无法上传文件分片。", "MalformedCreateResponse");
        var partUrls = AlipanJson.ParsePartInfoList(create);

        using var stream = File.OpenRead(localPath);
        long transferred = 0;
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = partUrls.TryGetValue(part.PartNumber, out var provided)
                ? provided
                : await RenewUploadUrlAsync(fileId, uploadId, part.PartNumber, cancellationToken).ConfigureAwait(false);
            await UploadPartAsync(stream, part, url, fileId, uploadId, cancellationToken).ConfigureAwait(false);
            transferred += part.Length;
            progress?.Report(new NetdiskTransferProgress(transferred, file.Length));
        }

        var complete = await SendAsync(
            HttpMethod.Post,
            AlipanApi.CompletePath,
            new Dictionary<string, object?> { ["drive_id"] = _driveId, ["file_id"] = fileId, ["upload_id"] = uploadId },
            cancellationToken).ConfigureAwait(false);

        var result = AlipanJson.ParseFileNode(complete);
        return string.IsNullOrEmpty(result.Id)
            ? new NetdiskFile(fileId, file.Name, false, file.Length, _clock())
            : result;
    }

    public async Task DownloadAsync(
        string fileId,
        string localPath,
        IProgress<NetdiskTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var link = await SendAsync(
            HttpMethod.Post,
            AlipanApi.DownloadUrlPath,
            new Dictionary<string, object?> { ["drive_id"] = _driveId, ["file_id"] = fileId, ["expire_sec"] = 900 },
            cancellationToken).ConfigureAwait(false);
        var url = AlipanJson.TryGetString(link, "url")
            ?? throw new NetdiskException("getDownloadUrl 响应缺少 url。", "MalformedDownloadUrlResponse");

        using var response = await _http
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw AlipanJson.MapError((int)response.StatusCode, text, "下载文件失败。");
        }

        var totalBytes = response.Content.Headers.ContentLength;
        await using var output = File.Create(localPath);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[81920];
        long transferred = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            transferred += read;
            progress?.Report(new NetdiskTransferProgress(transferred, totalBytes));
        }
    }

    public ValueTask DisposeAsync()
    {
        // The shared HttpClient is owned by the provider; nothing session-scoped to release.
        return ValueTask.CompletedTask;
    }

    private static string NormalizeParent(string? parentFolderId) =>
        string.IsNullOrEmpty(parentFolderId) ? AlipanApi.RootFolderId : parentFolderId;

    /// <summary>
    /// The one authorize-send path: refresh proactively, attach the bearer token, and on a
    /// token-rejected response rotate once and replay before surfacing the failure.
    /// </summary>
    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            _credentials = await _tokens.EnsureFreshAsync(_credentials, cancellationToken).ConfigureAwait(false);

            using var request = new HttpRequestMessage(method, AlipanApi.BaseUrl + path);
            if (body is not null)
            {
                request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            }

            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _credentials.Get(AliyunTokenService.AccessTokenKey));

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return ParseRoot(text);
            }

            var error = AlipanJson.MapError((int)response.StatusCode, text, $"网盘接口 {path} 调用失败。");
            if (attempt == 0 && AlipanJson.IsTokenRejected(error.ProviderCode))
            {
                _credentials = await _tokens.RefreshAsync(_credentials, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (AlipanJson.IsTokenRejected(error.ProviderCode))
            {
                throw new NetdiskAuthException("访问令牌刷新后仍被拒绝，请重新授权网盘账号。", error.ProviderCode, error);
            }

            throw error;
        }
    }

    private async Task UploadPartAsync(
        FileStream stream,
        AlipanPart part,
        string initialUrl,
        string fileId,
        string uploadId,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[part.Length];
        stream.Seek(part.Offset, SeekOrigin.Begin);
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = await stream
                .ReadAsync(buffer.AsMemory(filled, buffer.Length - filled), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            filled += read;
        }

        var url = initialUrl;
        for (var attempt = 0; ; attempt++)
        {
            using var content = new ByteArrayContent(buffer, 0, filled);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var response = await _http.PutAsync(url, content, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            // upload_url expires after ~15 minutes; renew once and replay before failing the part.
            if (attempt == 0)
            {
                url = await RenewUploadUrlAsync(fileId, uploadId, part.PartNumber, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw AlipanJson.MapError((int)response.StatusCode, text, $"分片 {part.PartNumber} 上传失败。");
        }
    }

    private async Task<string> RenewUploadUrlAsync(
        string fileId,
        string uploadId,
        int partNumber,
        CancellationToken cancellationToken)
    {
        var body = new Dictionary<string, object?>
        {
            ["drive_id"] = _driveId,
            ["file_id"] = fileId,
            ["upload_id"] = uploadId,
            ["part_info_list"] = new[] { new Dictionary<string, object?> { ["part_number"] = partNumber } },
        };
        var response = await SendAsync(HttpMethod.Post, AlipanApi.GetUploadUrlPath, body, cancellationToken).ConfigureAwait(false);
        return AlipanJson.ParsePartInfoList(response).TryGetValue(partNumber, out var url)
            ? url
            : throw new NetdiskException($"getUploadUrl 响应缺少分片 {partNumber} 的上传链接。", "MissingUploadUrl");
    }

    private static JsonElement ParseRoot(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
