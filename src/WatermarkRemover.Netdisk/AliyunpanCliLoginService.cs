using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Netdisk.Net;

namespace WatermarkRemover.Netdisk;

/// <summary>One login session: the browser authorize URL plus the server-side ticket id.</summary>
public sealed record AliyunpanCliLoginTicket(string TicketId, Uri AuthorizeUrl);

/// <summary>The openapi token once the user finished the browser authorization.</summary>
public sealed record AliyunpanCliLoginResult(string AccessToken, DateTimeOffset ExpiresAt);

/// <summary>
/// 阿里云盘 login without the CLI binary: replays the exact HTTP dance the tickstep/aliyunpan
/// CLI performs — create a login ticket on the author's server, open the same authorize URL in
/// the browser (一次授权 + 一次扫码), then poll the token endpoint until the authorization lands
/// there. Polling replaces the CLI's "press Enter" step, so no console and no timing guesswork.
/// </summary>
public sealed class AliyunpanCliLoginService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>The ticket page itself says links are valid for 5 minutes.</summary>
    private static readonly TimeSpan TicketLifetime = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;

    public AliyunpanCliLoginService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Creates the ticket and builds the authorize URL the browser must open.</summary>
    public async Task<AliyunpanCliLoginTicket> BeginAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var url = new StringBuilder(AlipanApi.TickstepWebHost)
            .Append(AlipanApi.TickstepQrCreatePath)
            .Append("?ip=127.0.0.1&os=windows&arch=amd64&version=v0.4.0&key=")
            .Append(Uri.EscapeDataString(deviceId));
        var response = await _http.GetAsync(url.ToString(), cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        ThrowIfError(root, "创建登录会话失败。");

        var tokenUrl = root.TryGetProperty("data", out var data) &&
                       data.TryGetProperty("tokenUrl", out var tokenUrlProp)
            ? tokenUrlProp.GetString()
            : null;
        var ticketId = ExtractTicketId(tokenUrl)
            ?? throw new NetdiskAuthException("登录会话响应缺少 ticketId。", "MalformedLoginTicket");

        return new AliyunpanCliLoginTicket(ticketId, BuildAuthorizeUrl(ticketId));
    }

    /// <summary>
    /// Polls the token endpoint until the browser authorization reaches it. A non-zero code means
    /// "not yet" and keeps polling; network hiccups are retried within the ticket lifetime.
    /// </summary>
    public async Task<AliyunpanCliLoginResult> WaitForTokenAsync(
        string ticketId,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + TicketLifetime;
        var url = $"{AlipanApi.TickstepWebHost}{AlipanApi.TickstepTokenLoginPath}/{Uri.EscapeDataString(ticketId)}/login";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new NetdiskAuthException(
                    "登录会话已超时（5 分钟），请重新扫码登录。", "LoginTicketExpired");
            }

            string text;
            try
            {
                using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
                text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       && !cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt64() == 0 &&
                root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("openapi", out var openToken) &&
                openToken.TryGetProperty("accessToken", out var accessTokenProp))
            {
                var accessToken = accessTokenProp.GetString();
                if (!string.IsNullOrEmpty(accessToken))
                {
                    var expiredSeconds = openToken.TryGetProperty("expired", out var expiredProp) &&
                                         expiredProp.ValueKind == JsonValueKind.Number
                        ? expiredProp.GetInt64()
                        : 0;
                    return new AliyunpanCliLoginResult(
                        accessToken, DateTimeOffset.FromUnixTimeSeconds(expiredSeconds));
                }
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Same authorize URL shape the CLI prints (client id + ticket-bound callback).</summary>
    public static Uri BuildAuthorizeUrl(string ticketId)
    {
        var query = new StringBuilder(AlipanApi.AuthorizeUrl)
            .Append("?client_id=").Append(AlipanApi.CliClientId)
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(
                $"{AlipanApi.TickstepWebHost}/auth/tickstep/aliyunpan/token/openapi/{ticketId}/auth2"))
            .Append("&scope=").Append(Uri.EscapeDataString(AlipanApi.CliScope));
        return new Uri(query.ToString());
    }

    private static string? ExtractTicketId(string? tokenUrl)
    {
        if (string.IsNullOrEmpty(tokenUrl) || !Uri.TryCreate(tokenUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return System.Web.HttpUtility.ParseQueryString(uri.Query).Get("tokenId");
    }

    private static void ThrowIfError(JsonElement root, string fallbackMessage)
    {
        if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.GetInt64() != 0)
        {
            var message = root.TryGetProperty("msg", out var msg) ? msg.GetString() : null;
            throw new NetdiskAuthException(
                string.IsNullOrEmpty(message) ? fallbackMessage : $"{fallbackMessage}{message}",
                "LoginTicketCreateFailed");
        }
    }
}
