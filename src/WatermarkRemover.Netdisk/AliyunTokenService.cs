using System.Globalization;
using System.Text;
using System.Text.Json;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Netdisk.Net;

namespace WatermarkRemover.Netdisk;

/// <summary>
/// Token lifecycle for one 阿里云盘 app: code exchange, proactive refresh before expiry and
/// single-flight rotation (refresh tokens are single-use, so concurrent callers must share one
/// rotation). Rotation results are only adopted when the old and new refresh tokens agree on their
/// JWT <c>sub</c> — a mismatch means the response belongs to a different account and is rejected.
/// </summary>
public sealed class AliyunTokenService
{
    public const string AccessTokenKey = "access_token";
    public const string RefreshTokenKey = "refresh_token";
    public const string ExpiresAtKey = "expires_at";

    /// <summary>Refresh this long before the token actually expires.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private string? _consumedRefreshToken;
    private NetdiskCredentials? _refreshResult;

    public AliyunTokenService(
        HttpClient http,
        string clientId,
        string clientSecret,
        Func<DateTimeOffset>? clock = null)
    {
        _http = http;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Whether the access token should be rotated before the next request.</summary>
    internal static bool NeedsRefresh(NetdiskCredentials credentials, DateTimeOffset now, TimeSpan margin)
    {
        // No token at all → must go through the refresh grant.
        if (string.IsNullOrEmpty(credentials.Get(AccessTokenKey)))
        {
            return true;
        }

        // A pasted token without expiry is used as-is; an invalid one surfaces as a rejected request.
        var raw = credentials.Get(ExpiresAtKey);
        if (string.IsNullOrEmpty(raw) ||
            !DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt))
        {
            return false;
        }

        return now >= expiresAt - margin;
    }

    /// <summary>Reads the <c>sub</c> claim of a JWT without validating the signature; null when not a JWT.</summary>
    internal static string? ReadJwtSub(string token)
    {
        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return AlipanJson.TryGetString(doc.RootElement, "sub");
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Rotates the access token only when <see cref="NeedsRefresh"/> says so.</summary>
    public async Task<NetdiskCredentials> EnsureFreshAsync(
        NetdiskCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        if (!NeedsRefresh(credentials, _clock(), RefreshMargin))
        {
            return credentials;
        }

        return await RefreshAsync(credentials, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Forces a rotation through the refresh grant. Concurrent callers that arrive with the same
    /// refresh token generation share a single upstream call instead of burning single-use tokens.
    /// </summary>
    public async Task<NetdiskCredentials> RefreshAsync(
        NetdiskCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var refreshToken = credentials.Get(RefreshTokenKey);
        if (string.IsNullOrEmpty(refreshToken))
        {
            throw new NetdiskAuthException("缺少 refresh_token，无法续期访问令牌，请重新授权。", "MissingRefreshToken");
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_refreshResult is not null &&
                string.Equals(_consumedRefreshToken, refreshToken, StringComparison.Ordinal))
            {
                return _refreshResult;
            }

            var result = await RequestTokenAsync(
                new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken },
                credentials,
                cancellationToken).ConfigureAwait(false);

            _consumedRefreshToken = refreshToken;
            _refreshResult = result;
            return result;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Exchanges an authorization code for the initial token pair.</summary>
    public async Task<NetdiskCredentials> ExchangeCodeAsync(
        NetdiskCredentials appCredentials,
        string code,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new NetdiskAuthException("授权码为空，无法完成授权。", "MissingCode");
        }

        return await RequestTokenAsync(
            new Dictionary<string, string> { ["grant_type"] = "authorization_code", ["code"] = code },
            appCredentials,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<NetdiskCredentials> RequestTokenAsync(
        Dictionary<string, string> grant,
        NetdiskCredentials baseCredentials,
        CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, string>(grant)
        {
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
        };

        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _http
            .PostAsync(AlipanApi.BaseUrl + AlipanApi.AccessTokenPath, content, cancellationToken)
            .ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Every token endpoint failure is an authorization problem by definition.
            var mapped = AlipanJson.MapError((int)response.StatusCode, text, "获取访问令牌失败。");
            throw mapped as NetdiskAuthException
                ?? new NetdiskAuthException(mapped.Message, mapped.ProviderCode, mapped);
        }

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var accessToken = AlipanJson.TryGetString(root, "access_token")
            ?? throw new NetdiskAuthException("授权响应缺少 access_token。", "MalformedTokenResponse");
        var newRefreshToken = AlipanJson.TryGetString(root, "refresh_token");
        var expiresIn = AlipanJson.TryGetInt64(root, "expires_in") ?? 7200;

        var oldRefreshToken = baseCredentials.Get(RefreshTokenKey);
        if (!string.IsNullOrEmpty(newRefreshToken) && !string.IsNullOrEmpty(oldRefreshToken))
        {
            var oldSub = ReadJwtSub(oldRefreshToken);
            var newSub = ReadJwtSub(newRefreshToken);
            if (oldSub is not null && newSub is not null && !string.Equals(oldSub, newSub, StringComparison.Ordinal))
            {
                throw new NetdiskAuthException(
                    "刷新令牌所属账号不一致（sub 校验失败），已拒绝本次轮换结果。",
                    "RefreshTokenSubjectMismatch");
            }
        }

        var values = new Dictionary<string, string>(baseCredentials.Values)
        {
            [AccessTokenKey] = accessToken,
            [ExpiresAtKey] = _clock().AddSeconds(expiresIn).ToString("O", CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(newRefreshToken))
        {
            values[RefreshTokenKey] = newRefreshToken;
        }

        return new NetdiskCredentials(values);
    }
}
