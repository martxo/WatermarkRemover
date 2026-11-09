using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WatermarkRemover.Core.Netdisk;

namespace WatermarkRemover.Netdisk;

/// <summary>
/// 天翼云盘 (personal cloud, cloud.189.cn) session over its private web API. Authorization is a
/// browser Cookie pasted once by the user (the web login now requires a slider captcha, so replaying
/// the login form is not dependable). Endpoints and payload shapes follow AList's 189 driver,
/// which is the maintained reference for this API:
///   list    GET  /api/open/file/listFiles.action
///   folder  POST /api/open/file/createFolder.action
///   tasks   POST /api/open/batch/createBatchTask.action   (DELETE / MOVE)
///   link    GET  /api/portal/getFileInfo.action
///   upload  GET  upload.cloud.189.cn/person/...           (AES + HMAC + RSA signed)
/// </summary>
public sealed class TianyiDriveSession : INetdiskSession
{
    public const string RootDriveId = "root";

    /// <summary>Personal cloud root folder id used by the web client.</summary>
    private const string RootFolderId = "-11";

    /// <summary>Family drives are exposed as <c>family:&lt;familyId&gt;</c>.</summary>
    private const string FamilyDrivePrefix = "family:";

    /// <summary>Family files/folders carry this prefix so ids never collide with personal nodes.</summary>
    private const string FamilyNodePrefix = "f:";

    /// <summary>AppKey the web 家庭共享 sub-app uses when trading sessionKey for an accessToken.</summary>
    private const string FamilyAppKey = "600100422";

    private const string FamilyApiBase = "https://api.cloud.189.cn";

    private const int MaxPageSize = 60;
    private const long SliceSize = 10 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly HttpClient _transferHttp;
    private readonly string _cookie;
    private readonly Dictionary<string, (string Name, bool IsFolder)> _nodes = new(StringComparer.Ordinal);
    private readonly List<NetdiskDrive> _drives = new() { new NetdiskDrive(RootDriveId, "我的空间") };
    private readonly Dictionary<string, string> _familyRoots = new(StringComparer.Ordinal);
    private string _sessionKey = string.Empty;
    private string _webSessionKey = string.Empty;
    private string _accessToken = string.Empty;
    private string? _currentFamilyId;
    private string _rsaPublicKey = string.Empty;
    private string _rsaKeyId = string.Empty;
    private DateTimeOffset _rsaExpiresAt;

    public TianyiDriveSession(
        HttpClient http,
        string cookie,
        NetdiskCredentials credentials,
        NetdiskAccountInfo account)
    {
        _http = http;
        _cookie = cookie;
        CurrentCredentials = credentials;
        Account = account;
        // Part PUTs go to OSS-style hosts and must NOT carry the 天翼 cookie, but they still use
        // the IPv4-preferring handler so a mixed IPv4/IPv6 path cannot break large transfers.
        _transferHttp = new HttpClient(TianyiHttp.CreateHandler())
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
    }

    public NetdiskAccountInfo Account { get; }

    public NetdiskCredentials CurrentCredentials { get; }

    public IReadOnlyList<NetdiskDrive> Drives => _drives;

    public string CurrentDriveId => _currentFamilyId is null
        ? RootDriveId
        : FamilyDrivePrefix + _currentFamilyId;

    public Task SwitchDriveAsync(string driveId, CancellationToken cancellationToken = default)
    {
        if (string.Equals(driveId, RootDriveId, StringComparison.Ordinal))
        {
            _currentFamilyId = null;
            return Task.CompletedTask;
        }

        if (driveId.StartsWith(FamilyDrivePrefix, StringComparison.Ordinal))
        {
            var familyId = driveId[FamilyDrivePrefix.Length..];
            if (!_drives.Any(d => string.Equals(d.Id, driveId, StringComparison.Ordinal)))
            {
                throw new NotSupportedException("未知的天翼云盘家庭空间。");
            }

            _currentFamilyId = familyId;
            return Task.CompletedTask;
        }

        throw new NotSupportedException("未知的天翼云盘存储空间。");
    }

    /// <summary>Validates the cookie, reads a display name and discovers the 家庭空间 list.</summary>
    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        var page = await ListAsync(string.Empty, 1, null, cancellationToken).ConfigureAwait(false);
        _ = page; // any authenticated answer is enough; errors throw below.
        await DiscoverFamiliesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<NetdiskQuota> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var doc = await GetJsonAsync(
                "https://cloud.189.cn/api/portal/getUserSizeInfo.action?needClassification=true",
                cancellationToken).ConfigureAwait(false);
            // The portal answers both spaces in one payload; show the one being browsed.
            var block = _currentFamilyId is null
                ? doc.RootElement
                : doc.RootElement.TryGetProperty("familyCapacityInfo", out var family)
                    ? family
                    : doc.RootElement;
            var used = FindLong(block, "usedSize", "used", "usedBytes");
            var total = FindLong(block, "totalSize", "total", "totalBytes", "capacity")
                        ?? FindLong(doc.RootElement, "totalSize", "total");
            return new NetdiskQuota(used ?? 0, total ?? 0);
        }
        catch (Exception ex) when (ex is NetdiskException or JsonException or HttpRequestException)
        {
            // Capacity is cosmetic; a shape change must not break the session.
            return new NetdiskQuota(0, 0);
        }
    }

    public async Task<NetdiskPage> ListAsync(
        string parentFolderId,
        int pageSize = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var pageNum = int.TryParse(cursor, out var parsed) ? parsed : 1;
        var limit = Math.Clamp(pageSize, 1, MaxPageSize);

        JsonDocument doc;
        if (_currentFamilyId is { } familyId)
        {
            var folderId = string.IsNullOrEmpty(parentFolderId)
                ? await EnsureFamilyRootAsync(familyId, cancellationToken).ConfigureAwait(false)
                : UnwrapNodeId(parentFolderId);
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["familyId"] = familyId,
                ["folderId"] = folderId,
                ["fileType"] = "0",
                ["mediaAttr"] = "0",
                ["iconOption"] = "5",
                ["pageNum"] = pageNum.ToString(CultureInfo.InvariantCulture),
                ["pageSize"] = limit.ToString(CultureInfo.InvariantCulture),
                ["orderBy"] = "lastOpTime",
                ["descending"] = "true",
            };
            doc = await SignedGetAsync(
                FamilyApiBase + "/open/family/file/listFiles.action", parameters, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var folderId = string.IsNullOrEmpty(parentFolderId) ? RootFolderId : parentFolderId;
            var url = "https://cloud.189.cn/api/open/file/listFiles.action" +
                      $"?noCache={TianyiCrypto.RandomCacheBuster()}&pageSize={limit}&pageNum={pageNum}" +
                      $"&mediaType=0&folderId={Uri.EscapeDataString(folderId)}&iconOption=5" +
                      "&orderBy=lastOpTime&descending=true";
            doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        }

        using (doc)
        {
            var items = new List<NetdiskFile>();
            if (doc.RootElement.TryGetProperty("fileListAO", out var listAo) &&
                listAo.ValueKind == JsonValueKind.Object)
            {
                if (listAo.TryGetProperty("folderList", out var folders) && folders.ValueKind == JsonValueKind.Array)
                {
                    foreach (var folder in folders.EnumerateArray())
                    {
                        AppendFolder(items, folder, parentFolderId);
                    }
                }

                if (listAo.TryGetProperty("fileList", out var files) && files.ValueKind == JsonValueKind.Array)
                {
                    foreach (var file in files.EnumerateArray())
                    {
                        AppendFile(items, file, parentFolderId);
                    }
                }
            }

            var next = items.Count >= limit ? (pageNum + 1).ToString(CultureInfo.InvariantCulture) : null;
            return new NetdiskPage(items, next);
        }
    }

    public Task<NetdiskPage> SearchAsync(
        string keyword,
        int pageSize = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("天翼云盘暂不支持搜索，请按目录浏览。");

    public Task<string?> GetNodeNameAsync(string nodeId, CancellationToken cancellationToken = default) =>
        Task.FromResult(_nodes.TryGetValue(nodeId, out var node) ? node.Name : null);

    public async Task<NetdiskFile> CreateFolderAsync(
        string parentFolderId,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (_currentFamilyId is { } familyId)
        {
            var parent = string.IsNullOrEmpty(parentFolderId)
                ? await EnsureFamilyRootAsync(familyId, cancellationToken).ConfigureAwait(false)
                : UnwrapNodeId(parentFolderId);
            var form = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["familyId"] = familyId,
                ["parentId"] = parent,
                ["folderName"] = name,
                ["relativePath"] = string.Empty,
            };
            using var response = await SignedPostFormAsync(
                FamilyApiBase + "/open/family/file/createFolder.action", form, cancellationToken)
                .ConfigureAwait(false);
            var newId = FirstLong(response.RootElement, "fileId", "id")
                ?.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(newId))
            {
                var wrapped = WrapNodeId(newId);
                _nodes[wrapped] = (name, true);
                return new NetdiskFile(wrapped, name, true, 0, DateTimeOffset.Now, parentFolderId);
            }
        }
        else
        {
            var folderId = string.IsNullOrEmpty(parentFolderId) ? RootFolderId : parentFolderId;
            using (await PostFormAsync(
                       "https://cloud.189.cn/api/open/file/createFolder.action",
                       new Dictionary<string, string>
                       {
                           ["parentFolderId"] = folderId,
                           ["folderName"] = name,
                       },
                       cancellationToken).ConfigureAwait(false))
            {
                // The API returns no id: read the parent back and match by name.
            }
        }

        var created = await FindChildAsync(parentFolderId, name, isFolder: true, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new NetdiskException(
                $"文件夹「{name}」已创建，但未能读回，请刷新列表查看。", "TianyiReadbackMissing");
        return created;
    }

    public async Task DeleteAsync(IReadOnlyList<string> fileIds, CancellationToken cancellationToken = default)
    {
        foreach (var id in fileIds)
        {
            var (name, isFolder) = ResolveNode(id);
            await CreateBatchTaskAsync("DELETE", string.Empty, id, name, isFolder, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task MoveAsync(
        IReadOnlyList<string> fileIds,
        string targetFolderId,
        CancellationToken cancellationToken = default)
    {
        var target = string.IsNullOrEmpty(targetFolderId)
            ? _currentFamilyId is { } familyId
                ? await EnsureFamilyRootAsync(familyId, cancellationToken).ConfigureAwait(false)
                : RootFolderId
            : targetFolderId;
        foreach (var id in fileIds)
        {
            var (name, isFolder) = ResolveNode(id);
            await CreateBatchTaskAsync("MOVE", target, id, name, isFolder, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public Task<NetdiskFile> UploadAsync(
        string parentFolderId,
        string localPath,
        IProgress<NetdiskTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => UploadAsAsync(parentFolderId, localPath, Path.GetFileName(localPath), false, progress, cancellationToken);

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

        if (_currentFamilyId is not null)
        {
            throw new NetdiskException(
                "天翼云盘暂不支持直接上传到家庭空间，请切换到「我的空间」上传。",
                "TianyiFamilyUploadUnsupported");
        }

        var size = new FileInfo(localPath).Length;
        if (size == 0)
        {
            throw new NetdiskException("天翼云盘暂不支持上传空文件。", "TianyiEmptyUpload");
        }

        var folderId = string.IsNullOrEmpty(parentFolderId) ? RootFolderId : parentFolderId;
        if (overwrite)
        {
            var existing = await FindChildAsync(parentFolderId, name, isFolder: null, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                await DeleteAsync(new[] { existing.Id }, cancellationToken).ConfigureAwait(false);
            }
        }

        progress?.Report(new NetdiskTransferProgress(0, size));
        await UploadSlicesAsync(folderId, localPath, size, progress, cancellationToken).ConfigureAwait(false);

        // The upload API returns the upload-file id, not the final node id: read the folder back.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var found = await FindChildAsync(parentFolderId, name, isFolder: false, cancellationToken)
                .ConfigureAwait(false);
            if (found is not null)
            {
                progress?.Report(new NetdiskTransferProgress(size, size));
                return found;
            }

            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new NetdiskTransferProgress(size, size));
        return new NetdiskFile(string.Empty, name, IsFolder: false, size, DateTimeOffset.Now, parentFolderId);
    }

    public async Task DownloadAsync(
        string fileId,
        string localPath,
        IProgress<NetdiskTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? downloadUrl;
        if (_currentFamilyId is { } familyId)
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["familyId"] = familyId,
                ["fileId"] = UnwrapNodeId(fileId),
            };
            using var info = await SignedGetAsync(
                FamilyApiBase + "/open/family/file/getFileDownloadUrl.action", parameters, cancellationToken)
                .ConfigureAwait(false);
            downloadUrl = FirstString(info.RootElement, "fileDownloadUrl", "downloadUrl");
        }
        else
        {
            var url = "https://cloud.189.cn/api/portal/getFileInfo.action?fileId=" + Uri.EscapeDataString(fileId);
            using var info = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
            downloadUrl = FirstString(info.RootElement, "downloadUrl", "fileDownloadUrl");
        }

        if (string.IsNullOrEmpty(downloadUrl))
        {
            throw new NetdiskException("天翼云盘未返回下载地址。", "TianyiNoDownloadUrl");
        }

        var finalUrl = await ResolveDownloadUrlAsync(downloadUrl, cancellationToken).ConfigureAwait(false);
        var temp = Path.Combine(Path.GetTempPath(), $"wr-tianyi-{Guid.NewGuid():N}");
        try
        {
            using (var response = await _transferHttp
                       .GetAsync(finalUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = File.Create(temp);
                var buffer = new byte[81920];
                long copied = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    copied += read;
                    progress?.Report(new NetdiskTransferProgress(copied, total));
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(localPath))!);
            File.Move(temp, localPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { /* best effort */ }
        }
    }

    public ValueTask DisposeAsync()
    {
        _transferHttp.Dispose();
        return ValueTask.CompletedTask;
    }

    // --- upload pipeline -----------------------------------------------------

    private async Task UploadSlicesAsync(
        string parentFolderId,
        string localPath,
        long size,
        IProgress<NetdiskTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var sessionKey = await GetSessionKeyAsync(cancellationToken).ConfigureAwait(false);
        var (_, keyId) = await GetRsaKeyAsync(cancellationToken).ConfigureAwait(false);

        using var init = await UploadRequestAsync(
            "/person/initMultiUpload",
            new Dictionary<string, string>
            {
                ["parentFolderId"] = parentFolderId,
                ["fileName"] = Uri.EscapeDataString(Path.GetFileName(localPath)),
                ["fileSize"] = size.ToString(CultureInfo.InvariantCulture),
                ["sliceSize"] = SliceSize.ToString(CultureInfo.InvariantCulture),
                ["lazyCheck"] = "1",
            },
            sessionKey, keyId, cancellationToken).ConfigureAwait(false);
        var uploadFileId = init.RootElement.TryGetProperty("data", out var data) &&
                           data.TryGetProperty("uploadFileId", out var uploadIdProp)
            ? uploadIdProp.GetString()
            : null;
        if (string.IsNullOrEmpty(uploadFileId))
        {
            throw new NetdiskException("天翼云盘上传初始化失败。", "TianyiUploadInitFailed");
        }

        var partCount = (long)Math.Ceiling(size / (double)SliceSize);
        var partMd5s = new List<string>((int)partCount);
        using var wholeMd5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        await using var stream = File.OpenRead(localPath);
        var buffer = new byte[SliceSize];

        for (long part = 1; part <= partCount; part++)
        {
            var want = (int)Math.Min(SliceSize, size - (part - 1) * SliceSize);
            var read = 0;
            while (read < want)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read, want - read), cancellationToken)
                    .ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                read += n;
            }

            var slice = buffer.AsSpan(0, read).ToArray();
            wholeMd5.AppendData(slice);
            var sliceMd5 = MD5.HashData(slice);
            var md5Base64 = Convert.ToBase64String(sliceMd5);
            partMd5s.Add(Convert.ToHexString(sliceMd5));

            using var urls = await UploadRequestAsync(
                "/person/getMultiUploadUrls",
                new Dictionary<string, string>
                {
                    ["partInfo"] = $"{part}-{md5Base64}",
                    ["uploadFileId"] = uploadFileId,
                },
                sessionKey, keyId, cancellationToken).ConfigureAwait(false);

            var partInfo = ReadPart(urls.RootElement, "partNumber_" + part);
            if (partInfo is null)
            {
                throw new NetdiskException("天翼云盘未返回上传分片地址。", "TianyiUploadUrlMissing");
            }

            using var put = new HttpRequestMessage(HttpMethod.Put, partInfo.Value.Url)
            {
                Content = new ByteArrayContent(slice),
            };
            foreach (var (header, value) in ParseHeaders(partInfo.Value.Headers))
            {
                // Content-Type/Content-MD5 are content headers: attaching them to the request
                // headers silently drops them, and the OSS-style signature then mismatches.
                if (!put.Content.Headers.TryAddWithoutValidation(header, value))
                {
                    put.Headers.TryAddWithoutValidation(header, value);
                }
            }

            using var putResponse = await _transferHttp.SendAsync(put, cancellationToken).ConfigureAwait(false);
            if (!putResponse.IsSuccessStatusCode)
            {
                var detail = await putResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (detail.Length > 300)
                {
                    detail = detail[..300];
                }

                throw new NetdiskException(
                    $"天翼云盘分片 {part} 上传失败（HTTP {(int)putResponse.StatusCode}）：{detail}",
                    "TianyiUploadPartFailed");
            }

            progress?.Report(new NetdiskTransferProgress(Math.Min(size, part * SliceSize), size));
        }

        var fileMd5 = Convert.ToHexString(wholeMd5.GetHashAndReset()).ToLowerInvariant();
        var sliceMd5All = size > SliceSize
            ? Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(string.Join("\n", partMd5s))))
                .ToLowerInvariant()
            : fileMd5;

        using var commit = await UploadRequestAsync(
            "/person/commitMultiUploadFile",
            new Dictionary<string, string>
            {
                ["uploadFileId"] = uploadFileId,
                ["fileMd5"] = fileMd5,
                ["sliceMd5"] = sliceMd5All,
                ["lazyCheck"] = "1",
                ["opertype"] = "3",
            },
            sessionKey, keyId, cancellationToken).ConfigureAwait(false);
        _ = commit;
    }

    /// <summary>Signed upload call: AES-ECB params + HMAC-SHA1 + RSA-wrapped key, as the client does.</summary>
    private async Task<JsonDocument> UploadRequestAsync(
        string uri,
        IReadOnlyDictionary<string, string> form,
        string sessionKey,
        string keyId,
        CancellationToken cancellationToken)
    {
        var (publicKey, _) = await GetRsaKeyAsync(cancellationToken).ConfigureAwait(false);
        var secret = TianyiCrypto.RandomSecretKey();
        var aesKey = Encoding.UTF8.GetBytes(secret[..16]);
        var encrypted = TianyiCrypto.AesEcbEncrypt(
            Encoding.UTF8.GetBytes(TianyiCrypto.BuildQuery(form)), aesKey);
        var paramsHex = Convert.ToHexString(encrypted).ToLowerInvariant();
        var date = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var signature = TianyiCrypto.HmacSha1Hex(
            $"SessionKey={sessionKey}&Operate=GET&RequestURI={uri}&Date={date}&params={paramsHex}", secret);

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"https://upload.cloud.189.cn{uri}?params={paramsHex}");
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=UTF-8");
        request.Headers.TryAddWithoutValidation("SessionKey", sessionKey);
        request.Headers.TryAddWithoutValidation("Signature", signature);
        request.Headers.TryAddWithoutValidation("X-Request-Date", date);
        request.Headers.TryAddWithoutValidation("X-Request-ID", Guid.NewGuid().ToString());
        request.Headers.TryAddWithoutValidation(
            "EncryptionText", TianyiCrypto.RsaEncryptBase64(publicKey, secret));
        request.Headers.TryAddWithoutValidation("PkId", keyId);

        using var response = await _transferHttp.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new NetdiskException(
                $"天翼云盘上传接口失败（HTTP {(int)response.StatusCode}）。", "TianyiUploadHttpFailed");
        }

        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("code", out var code) &&
            code.ValueKind == JsonValueKind.String &&
            !string.Equals(code.GetString(), "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            var message = FirstString(doc.RootElement, "msg") ?? code.GetString();
            throw new NetdiskException($"天翼云盘上传失败：{message}", "TianyiUploadRejected");
        }

        return JsonDocument.Parse(text);
    }

    private async Task<string> GetSessionKeyAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_sessionKey))
        {
            return _sessionKey;
        }

        using var doc = await GetJsonAsync(
            "https://cloud.189.cn/v2/getUserBriefInfo.action", cancellationToken).ConfigureAwait(false);
        _sessionKey = FirstString(doc.RootElement, "sessionKey")
            ?? throw new NetdiskException("天翼云盘未返回上传会话密钥。", "TianyiNoSessionKey");
        return _sessionKey;
    }

    private async Task<(string PublicKey, string KeyId)> GetRsaKeyAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_rsaPublicKey) && DateTimeOffset.UtcNow < _rsaExpiresAt)
        {
            return (_rsaPublicKey, _rsaKeyId);
        }

        using var doc = await GetJsonAsync(
            "https://cloud.189.cn/api/security/generateRsaKey.action", cancellationToken).ConfigureAwait(false);
        _rsaPublicKey = FirstString(doc.RootElement, "pubKey")
            ?? throw new NetdiskException("天翼云盘未返回上传公钥。", "TianyiNoRsaKey");
        _rsaKeyId = FirstString(doc.RootElement, "pkId") ?? string.Empty;
        _rsaExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10);
        return (_rsaPublicKey, _rsaKeyId);
    }

    private static (string Url, string Headers)? ReadPart(JsonElement root, string key)
    {
        if (!root.TryGetProperty("uploadUrls", out var urls) ||
            !urls.TryGetProperty(key, out var part) ||
            !part.TryGetProperty("requestURL", out var url))
        {
            return null;
        }

        var header = part.TryGetProperty("requestHeader", out var headers) ? headers.GetString() : null;
        return (url.GetString() ?? string.Empty, header ?? string.Empty);
    }

    private static IEnumerable<(string Name, string Value)> ParseHeaders(string raw)
    {
        // The API returns this as a URL-encoded query string: decode first (the '&' separators and
        // '=' are encoded too), then split — splitting before decoding leaves one unusable pair
        // and the OSS-style part PUT fails with SignatureDoesNotMatch.
        foreach (var pair in Uri.UnescapeDataString(raw).Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            yield return (pair[..index], pair[(index + 1)..]);
        }
    }

    // --- API helpers ---------------------------------------------------------

    private (string Name, bool IsFolder) ResolveNode(string id)
    {
        if (_nodes.TryGetValue(id, out var node))
        {
            return node;
        }

        throw new NetdiskException(
            "天翼云盘需要该文件/文件夹的名称才能执行此操作，请先刷新列表。", "TianyiNodeUnknown");
    }

    private async Task<NetdiskFile?> FindChildAsync(
        string parentFolderId,
        string name,
        bool? isFolder,
        CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var page = await ListAsync(parentFolderId, MaxPageSize, cursor, cancellationToken).ConfigureAwait(false);
            var match = page.Items.FirstOrDefault(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase) &&
                (isFolder is null || item.IsFolder == isFolder));
            if (match is not null)
            {
                return match;
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);

        return null;
    }

    private async Task CreateBatchTaskAsync(
        string type,
        string targetFolderId,
        string fileId,
        string fileName,
        bool isFolder,
        CancellationToken cancellationToken)
    {
        // Keep Chinese file names literal inside taskInfos (the reference client sends raw UTF-8,
        // and the API echoes/uses these names); the form encoder still percent-encodes them.
        var infos = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object>
            {
                ["fileId"] = UnwrapNodeId(fileId),
                ["fileName"] = fileName,
                ["isFolder"] = isFolder ? 1 : 0,
            },
        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        if (_currentFamilyId is { } familyId)
        {
            var form = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = type,
                ["taskInfos"] = infos,
                ["familyId"] = familyId,
            };
            if (!string.IsNullOrEmpty(targetFolderId))
            {
                form["targetFolderId"] = UnwrapNodeId(targetFolderId);
            }

            using var familyResponse = await SignedPostFormAsync(
                FamilyApiBase + "/open/batch/createBatchTask.action", form, cancellationToken)
                .ConfigureAwait(false);
            await WaitBatchTaskAsync(type, FirstString(familyResponse.RootElement, "taskId"), true, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        using var response = await PostFormAsync(
            "https://cloud.189.cn/api/open/batch/createBatchTask.action",
            new Dictionary<string, string>
            {
                ["type"] = type,
                ["targetFolderId"] = targetFolderId,
                ["taskInfos"] = infos,
            },
            cancellationToken).ConfigureAwait(false);
        await WaitBatchTaskAsync(type, FirstString(response.RootElement, "taskId"), false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Batch tasks (move/delete) run asynchronously; returning as soon as the id arrives made the
    /// next call race the task — a folder created and immediately deleted stayed behind. Poll
    /// 4 = finished, 2 = conflict (the same state the reference client waits for).
    /// </summary>
    private async Task WaitBatchTaskAsync(
        string type,
        string? taskId,
        bool family,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            return;
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            var form = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["type"] = type,
                ["taskId"] = taskId,
            };

            using var doc = family
                ? await SignedPostFormAsync(FamilyApiBase + "/open/batch/checkBatchTask.action", form, cancellationToken)
                    .ConfigureAwait(false)
                : await PostFormAsync("https://cloud.189.cn/api/open/batch/checkBatchTask.action", form, cancellationToken)
                    .ConfigureAwait(false);

            var status = FirstLong(doc.RootElement, "taskStatus");
            if (status == 4)
            {
                return;
            }

            if (status == 2)
            {
                throw new NetdiskException("天翼云盘操作有文件冲突（同名文件已存在）。", "TianyiBatchConflict");
            }

            await Task.Delay(400, cancellationToken).ConfigureAwait(false);
        }
    }

    // --- 家庭空间 (family cloud) ----------------------------------------------

    /// <summary>
    /// Discovers the 家庭空间 list. Best effort: an account without a family (or a changed
    /// response shape) simply keeps the personal drive only.
    /// </summary>
    private async Task DiscoverFamiliesAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var doc = await SignedGetAsync(
                FamilyApiBase + "/open/family/manage/getFamilyList.action",
                new Dictionary<string, string>(StringComparer.Ordinal),
                cancellationToken).ConfigureAwait(false);

            if (!doc.RootElement.TryGetProperty("familyInfoResp", out var infos) ||
                infos.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var info in infos.EnumerateArray())
            {
                var id = FirstLong(info, "familyId")?.ToString(CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var driveId = FamilyDrivePrefix + id;
                if (_drives.Any(d => string.Equals(d.Id, driveId, StringComparison.Ordinal)))
                {
                    continue;
                }

                var name = FirstString(info, "remarkName");
                _drives.Add(new NetdiskDrive(
                    driveId,
                    string.IsNullOrWhiteSpace(name) ? "家庭空间" : $"家庭空间（{name}）"));
            }
        }
        catch (Exception ex) when (ex is NetdiskException or JsonException or HttpRequestException)
        {
            // Family is optional; the personal space is already usable.
        }
    }

    /// <summary>sessionKey is the web session in the family APIs' vocabulary.</summary>
    private async Task<string> GetWebSessionKeyAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_webSessionKey))
        {
            return _webSessionKey;
        }

        using var doc = await GetJsonAsync(
            "https://cloud.189.cn/api/portal/v2/getUserBriefInfo.action?noCache=" +
            TianyiCrypto.RandomCacheBuster(), cancellationToken).ConfigureAwait(false);
        _webSessionKey = FirstString(doc.RootElement, "sessionKey")
            ?? throw new NetdiskException("天翼云盘未返回会话标识。", "TianyiNoSessionKey");
        return _webSessionKey;
    }

    /// <summary>Trades the web sessionKey for the token the family APIs sign with.</summary>
    private async Task<string> GetFamilyAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_accessToken))
        {
            return _accessToken;
        }

        var sessionKey = await GetWebSessionKeyAsync(cancellationToken).ConfigureAwait(false);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            .ToString(CultureInfo.InvariantCulture);
        var signature = BuildFamilySignature(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["sessionKey"] = sessionKey,
            ["Timestamp"] = timestamp,
            ["AppKey"] = FamilyAppKey,
        });

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            FamilyApiBase + "/open/oauth2/getAccessTokenBySsKey.action?sessionKey=" +
            Uri.EscapeDataString(sessionKey));
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=UTF-8");
        request.Headers.TryAddWithoutValidation("AppKey", FamilyAppKey);
        request.Headers.TryAddWithoutValidation("Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("Signature", signature);
        request.Headers.TryAddWithoutValidation("Sign-Type", "1");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfAuthRedirect(response.StatusCode);
        ThrowIfApiError(text, "getAccessTokenBySsKey");

        using var doc = JsonDocument.Parse(text);
        var token = FirstString(doc.RootElement, "accessToken");
        if (string.IsNullOrEmpty(token))
        {
            throw new NetdiskException("天翼云盘家庭空间未返回访问令牌。", "TianyiFamilyNoToken");
        }

        _accessToken = token;
        return token;
    }

    /// <summary>GET with the family sub-app's signature: md5 over sorted k=v pairs.</summary>
    private async Task<JsonDocument> SignedGetAsync(
        string url,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var query = string.Join("&", parameters.Select(p =>
            $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        var fullUrl = parameters.Count == 0
            ? url
            : url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + query;

        for (var attempt = 0; ; attempt++)
        {
            var accessToken = await GetFamilyAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, fullUrl);
            AddFamilyHeaders(request, accessToken, parameters);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfAuthRedirect(response.StatusCode);

            if (attempt == 0 && IsExpiredFamilyToken(text))
            {
                _accessToken = string.Empty;
                _webSessionKey = string.Empty;
                continue;
            }

            ThrowIfApiError(text, fullUrl);
            return JsonDocument.Parse(text);
        }
    }

    /// <summary>POST (form) with the same family signature, used by folder creation and batch tasks.</summary>
    private async Task<JsonDocument> SignedPostFormAsync(
        string url,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var accessToken = await GetFamilyAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new FormUrlEncodedContent(form),
            };
            AddFamilyHeaders(request, accessToken, form);

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfAuthRedirect(response.StatusCode);

            if (attempt == 0 && IsExpiredFamilyToken(text))
            {
                _accessToken = string.Empty;
                _webSessionKey = string.Empty;
                continue;
            }

            ThrowIfApiError(text, url);
            return JsonDocument.Parse(text);
        }
    }

    private static bool IsExpiredFamilyToken(string text) =>
        text.Contains("InvalidAccessToken", StringComparison.Ordinal) ||
        text.Contains("InvalidSessionKey", StringComparison.Ordinal);

    private static void AddFamilyHeaders(
        HttpRequestMessage request,
        string accessToken,
        IReadOnlyDictionary<string, string> parameters)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            .ToString(CultureInfo.InvariantCulture);
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AccessToken"] = accessToken,
            ["Timestamp"] = timestamp,
        };
        foreach (var (key, value) in parameters)
        {
            pairs[key] = value;
        }

        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=UTF-8");
        request.Headers.TryAddWithoutValidation("AccessToken", accessToken);
        request.Headers.TryAddWithoutValidation("Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("Signature", BuildFamilySignature(pairs));
        request.Headers.TryAddWithoutValidation("Sign-Type", "1");
    }

    private static string BuildFamilySignature(IReadOnlyDictionary<string, string> parameters)
    {
        var joined = string.Join("&", parameters
            .Select(pair => $"{pair.Key}={pair.Value}")
            .OrderBy(pair => pair, StringComparer.Ordinal));
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// The family space's virtual root only carries a path; files live in its 「家庭云」 folder,
    /// whose id has to be read from that path before the first listing.
    /// </summary>
    private async Task<string> EnsureFamilyRootAsync(string familyId, CancellationToken cancellationToken)
    {
        if (_familyRoots.TryGetValue(familyId, out var cached))
        {
            return cached;
        }

        var root = string.Empty;
        try
        {
            using var doc = await SignedGetAsync(
                FamilyApiBase + "/open/family/file/listFiles.action",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["familyId"] = familyId,
                    ["folderId"] = string.Empty,
                    ["fileType"] = "0",
                    ["mediaAttr"] = "0",
                    ["iconOption"] = "5",
                    ["pageNum"] = "1",
                    ["pageSize"] = "1",
                    ["needPath"] = "true",
                },
                cancellationToken).ConfigureAwait(false);

            if (doc.RootElement.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.Array)
            {
                string? last = null;
                foreach (var entry in path.EnumerateArray())
                {
                    var id = FirstLong(entry, "fileId")?.ToString(CultureInfo.InvariantCulture);
                    if (string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    last = id;
                    if (string.Equals(FirstString(entry, "fileName"), "家庭云", StringComparison.Ordinal))
                    {
                        root = id;
                        break;
                    }
                }

                if (root.Length == 0)
                {
                    root = last ?? string.Empty;
                }
            }
        }
        catch (Exception ex) when (ex is NetdiskException or JsonException or HttpRequestException)
        {
            root = string.Empty;
        }

        _familyRoots[familyId] = root;
        return root;
    }

    /// <summary>Family node ids are prefixed so they can never collide with personal ids.</summary>
    private string WrapNodeId(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return string.Empty;
        }

        return _currentFamilyId is null ? id : FamilyNodePrefix + id;
    }

    private static string UnwrapNodeId(string id) =>
        id.StartsWith(FamilyNodePrefix, StringComparison.Ordinal)
            ? id[FamilyNodePrefix.Length..]
            : id;

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddApiHeaders(request);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfAuthRedirect(response.StatusCode);
        ThrowIfApiError(text, url);
        return JsonDocument.Parse(text);
    }

    private async Task<JsonDocument> PostFormAsync(
        string url,
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form),
        };
        AddApiHeaders(request);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfAuthRedirect(response.StatusCode);
        ThrowIfApiError(text, url);
        return JsonDocument.Parse(text);
    }

    /// <summary>A redirect / 401 / 403 means the cookie no longer authenticates, not a bad request.</summary>
    private static void ThrowIfAuthRedirect(HttpStatusCode status)
    {
        if ((int)status is 301 or 302 or 303 or 307 or 308 or 401 or 403)
        {
            throw new NetdiskAuthException(
                "天翼云盘登录状态已过期：请重新登录 cloud.189.cn，复制新的 Cookie 后更新账号。",
                "TianyiSessionExpired");
        }
    }

    private void AddApiHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Accept", "application/json;charset=UTF-8");
        request.Headers.TryAddWithoutValidation("Referer", "https://cloud.189.cn/");
        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Cookie", _cookie);
    }

    /// <summary>Turns the API's two error envelopes (errorCode / res_code) into typed exceptions.</summary>
    private static void ThrowIfApiError(string text, string url)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new NetdiskException("天翼云盘返回了空响应。", "TianyiEmptyResponse");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new NetdiskException("天翼云盘返回了无法解析的内容。", "TianyiBadJson");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var errorCode = FirstString(root, "errorCode");
            if (!string.IsNullOrEmpty(errorCode))
            {
                var message = FirstString(root, "errorMsg") ?? errorCode;
                if (string.Equals(errorCode, "InvalidSessionKey", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NetdiskAuthException(
                        "天翼云盘登录状态已过期：请重新登录 cloud.189.cn，复制新的 Cookie 后更新账号。",
                        "TianyiSessionExpired");
                }

                throw new NetdiskException($"天翼云盘操作失败：{message}", errorCode);
            }

            if (root.TryGetProperty("res_code", out var resCode))
            {
                if (resCode.ValueKind == JsonValueKind.Number && resCode.GetInt32() != 0)
                {
                    var message = FirstString(root, "res_message") ?? "未知错误";
                    throw new NetdiskException($"天翼云盘操作失败：{message}", "TianyiApiError");
                }

                // Family endpoints answer with a string code (e.g. "InvalidAccessToken").
                if (resCode.ValueKind == JsonValueKind.String &&
                    resCode.GetString() is { Length: > 0 } code &&
                    !string.Equals(code, "0", StringComparison.Ordinal))
                {
                    if (string.Equals(code, "InvalidSessionKey", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new NetdiskAuthException(
                            "天翼云盘登录状态已过期：请重新登录 cloud.189.cn，复制新的 Cookie 后更新账号。",
                            "TianyiSessionExpired");
                    }

                    var message = FirstString(root, "res_message") ?? code;
                    throw new NetdiskException($"天翼云盘操作失败：{message}", code);
                }
            }
        }
    }

    private async Task<string> ResolveDownloadUrlAsync(string url, CancellationToken cancellationToken)
    {
        var current = url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url;
        if (current.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            current = "https://" + current[7..];
        }

        using var noRedirect = new HttpClient(TianyiHttp.CreateHandler(allowAutoRedirect: false))
        {
            Timeout = TimeSpan.FromMinutes(1),
        };
        for (var hop = 0; hop < 3; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("Cookie", _cookie);
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            using var response = await noRedirect.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var isRedirect = response.StatusCode is HttpStatusCode.Found
                or HttpStatusCode.MovedPermanently
                or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.SeeOther;
            if (isRedirect && response.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri ? location.ToString() : new Uri(new Uri(current), location).ToString();
                continue;
            }

            return current;
        }

        return current;
    }

    private void AppendFolder(List<NetdiskFile> items, JsonElement folder, string parentId)
    {
        var id = WrapNodeId(FirstLong(folder, "id")?.ToString(CultureInfo.InvariantCulture));
        var name = FirstString(folder, "name");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
        {
            return;
        }

        _nodes[id] = (name!, true);
        items.Add(new NetdiskFile(id!, name!, true, 0, ParseTime(FirstString(folder, "lastOpTime")), parentId));
    }

    private void AppendFile(List<NetdiskFile> items, JsonElement file, string parentId)
    {
        var id = WrapNodeId(FirstLong(file, "id")?.ToString(CultureInfo.InvariantCulture));
        var name = FirstString(file, "name");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
        {
            return;
        }

        _nodes[id] = (name!, false);
        items.Add(new NetdiskFile(
            id!,
            name!,
            false,
            FirstLong(file, "size", "fileSize") ?? 0,
            ParseTime(FirstString(file, "lastOpTime")),
            parentId));
    }

    private static DateTimeOffset? ParseTime(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return DateTime.TryParseExact(
            value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            ? new DateTimeOffset(local, TimeSpan.FromHours(8))
            : null;
    }

    private static string? FirstString(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static long? FirstLong(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    /// <summary>Depth-first search for the first matching numeric field (capacity shapes vary).</summary>
    private static long? FindLong(JsonElement element, params string[] keys)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number > 0)
                {
                    return number;
                }

                if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed) && parsed > 0)
                {
                    return parsed;
                }
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object &&
                FindLong(property.Value, keys) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
