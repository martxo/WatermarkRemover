using System.Text.Json;
using System.Text.RegularExpressions;

namespace WatermarkRemover.Core.Features.Transfer;

/// <summary>
/// Pulls a CamScanner share link (received as text over the 「手机传文件」 flow) down to page
/// images. The full chain runs against <c>cs8.intsig.net</c> — the sync CDN host the share page
/// itself loads data from: unlike <c>api-share.camscanner.com</c> it has no WAF fingerprinting,
/// so plain HttpClient works (verified live 2026-10; the original-file download API stays behind
/// a login wall, but the per-page previews are anonymous, high resolution and watermark-free).
/// </summary>
public sealed class CamScannerShareClient
{
    /// <summary>Preview fetch resolution; 2480 px ≈ A4 at 300 dpi.</summary>
    private const int PagePixel = 2480;

    /// <summary>CamScanner's sync CDN host — carries both the share-info query and the page
    /// previews, and (unlike api-share.camscanner.com) has no WAF fingerprinting.</summary>
    private const string SyncBase = "https://cs8.intsig.net/sync";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static readonly HttpClient Redirecting = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static readonly HttpClient Redirectless =
        new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Document metadata needed to rebuild the PDF.</summary>
    public sealed record ShareDocumentInfo(string Title, IReadOnlyList<string> PageFileNames);

    /// <summary>Extracts a CamScanner share link from received text; the first match wins.
    /// Accepts short links (link.camscanner.com/…) and direct page URLs (/s/{eid}/{sid}).</summary>
    public static bool TryExtractShareLink(string? text, out string link)
    {
        link = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (Match match in Regex.Matches(text, @"https?://\S+"))
        {
            var candidate = match.Value.TrimEnd("。，！？；：、）】」》>,.;:!?)\"'".ToCharArray());
            if (!candidate.Contains("camscanner.com", StringComparison.OrdinalIgnoreCase)) continue;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) continue;
            if (!uri.Host.EndsWith("camscanner.com", StringComparison.OrdinalIgnoreCase)) continue;

            if (uri.Host.Equals("link.camscanner.com", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.StartsWith("/s/", StringComparison.Ordinal))
            {
                link = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>Output PDF file name for a share (title sanitized; falls back to 分享文档).</summary>
    public static string SuggestFileName(ShareDocumentInfo info) =>
        LanTransferServer.SanitizeFileName(string.IsNullOrWhiteSpace(info.Title) ? "分享文档" : info.Title) + ".pdf";

    /// <summary>Follows a short link to the /s/{eid}/{sid} page URL (≤3 hops) and splits the ids.</summary>
    public async Task<(string Eid, string Sid)> ResolveAsync(string link, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(link);
        for (var hop = 0; hop < 3; hop++)
        {
            if (uri.Host.Equals("link.camscanner.com", StringComparison.OrdinalIgnoreCase))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd(UserAgent);
                using var response = await Redirectless.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.Headers.Location is null)
                    throw new InvalidOperationException("短链没有返回跳转地址，可能已失效");
                uri = new Uri(uri, response.Headers.Location);
                continue;
            }

            return ParseSharePath(uri)
                ?? throw new InvalidOperationException($"不是可识别的分享链接：{uri.Host}{uri.AbsolutePath}");
        }

        throw new InvalidOperationException("短链跳转次数过多，请在手机上直接复制分享页地址");
    }

    /// <summary>Splits a /s/{eid}/{sid} page URL into its two share identifiers.</summary>
    internal static (string Eid, string Sid)? ParseSharePath(Uri url)
    {
        var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 3 ? (segments[^2], segments[^1]) : null;
    }

    /// <summary>Reads the share's document metadata (title + per-page file names).</summary>
    public async Task<ShareDocumentInfo> QueryAsync(string eid, string sid, CancellationToken cancellationToken = default)
    {
        string url =
            $"{SyncBase}/query_share_info_with_link?link={Uri.EscapeDataString(sid)}" +
            $"&encrypt_id={Uri.EscapeDataString(eid)}&platform=web&dir=0";
        string json = await HttpGetStringAsync(url, cancellationToken).ConfigureAwait(false);

        try
        {
            return ParseShareInfo(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("分享信息返回了无法解析的内容", ex);
        }
    }

    internal static ShareDocumentInfo ParseShareInfo(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var errorCode = root.TryGetProperty("error_code", out var code) ? code.GetInt32() : 0;
        if (errorCode != 0)
        {
            var message = root.TryGetProperty("error_msg", out var msg) ? msg.GetString() : null;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                ? "分享已失效或需要提取码"
                : $"分享读取失败：{message}");
        }

        // The share API occasionally answers error_code=0 with a body that lacks the usual
        // data payload (rate limiting / session probes). surface the body snippet instead of
        // letting GetProperty throw an opaque KeyNotFoundException.
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException(UnexpectedPayload(json));

        string title = string.Empty;
        if (data.TryGetProperty("share_info", out var shareInfo) && shareInfo.ValueKind == JsonValueKind.Object &&
            shareInfo.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String)
        {
            title = titleElement.GetString() ?? string.Empty;
        }

        if (!data.TryGetProperty("doc_info", out var docInfo) || docInfo.ValueKind != JsonValueKind.Object ||
            !docInfo.TryGetProperty("page_list", out var pageList) || pageList.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(UnexpectedPayload(json));
        }

        var names = pageList.EnumerateArray()
            .Select(p => p.TryGetProperty("file_name", out var fn) ? fn.GetString() : null)
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .ToList();
        if (names.Count == 0)
            throw new InvalidOperationException("分享内容为空或已过期");

        return new ShareDocumentInfo(title, names);
    }

    /// <summary>Friendly message for a structurally unexpected (but valid JSON) share payload;
    /// carries a snippet of the body so the status line / log shows what actually came back.</summary>
    private static string UnexpectedPayload(string json)
    {
        string snippet = json.Length <= 160 ? json : json[..160] + "…";
        return $"分享读取失败：服务返回了意外的内容格式（{snippet}）";
    }

    /// <summary>Downloads one page preview as JPEG bytes.</summary>
    public async Task<byte[]> DownloadPageAsync(string eid, string sid, string fileName, CancellationToken cancellationToken = default)
    {
        var normalizedName = fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".jpg";
        string url =
            $"{SyncBase}/share/download_resize_jpg?encrypt_id={Uri.EscapeDataString(eid)}" +
            $"&file_name={Uri.EscapeDataString(normalizedName)}&pixel={PagePixel}" +
            $"&sid={Uri.EscapeDataString(sid)}&dir=0";
        byte[] bytes = await HttpGetBytesAsync(url, cancellationToken).ConfigureAwait(false);

        return bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xD8
            ? bytes
            : throw new InvalidOperationException("页面预览下载失败（返回的不是图片）");
    }

    private static async Task<string> HttpGetStringAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> HttpGetBytesAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, ct).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Referrer = new Uri("https://www.camscanner.com/");
        var response = await Redirecting.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? response
            : throw new InvalidOperationException($"服务器返回 {(int)response.StatusCode}");
    }
}
