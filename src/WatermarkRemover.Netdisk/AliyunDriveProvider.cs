using System.Text;
using WatermarkRemover.Core.Netdisk;

namespace WatermarkRemover.Netdisk;

/// <summary>
/// 阿里云盘 product implementation. Stateless description + connection factory: the credential
/// bag supplied at connect time carries both the app's own settings (App ID / Secret / 回调地址)
/// and the token pair, so one class serves every configured account. Registered automatically
/// through RegisterDiscovered — adding another product means adding one class like this one.
/// </summary>
public sealed class AliyunDriveProvider : INetdiskProvider, INetdiskOAuthFlow
{
    public const string AppIdKey = "app_id";
    public const string AppSecretKey = "app_secret";
    public const string RedirectUriKey = "redirect_uri";

    private readonly HttpClient _http;
    private readonly Func<DateTimeOffset>? _clock;

    public AliyunDriveProvider()
        : this(new HttpClient())
    {
    }

    internal AliyunDriveProvider(HttpClient http, Func<DateTimeOffset>? clock = null)
    {
        _http = http;
        _clock = clock;
    }

    public string Id => "alipan";

    public string DisplayName => "阿里云盘";

    /// <summary>
    /// All fields optional individually — a usable account needs either a pasted access token or
    /// an app credential pair plus a refresh token; ConnectAsync enforces the combination.
    /// </summary>
    public IReadOnlyList<NetdiskCredentialField> CredentialFields { get; } = new[]
    {
        new NetdiskCredentialField(
            AppIdKey, "App ID（应用标识）", NetdiskFieldKind.Text, Required: false,
            Hint: "阿里云盘开放平台应用的 Client ID；授权登录与自动续期需要"),
        new NetdiskCredentialField(
            AppSecretKey, "App Secret（应用密钥）", NetdiskFieldKind.Secret, Required: false,
            Hint: "与 App ID 配套，仅保存在本机"),
        new NetdiskCredentialField(
            RedirectUriKey, "回调地址", NetdiskFieldKind.Text, Required: false,
            Hint: "需与开放平台应用配置一致，如 http://localhost"),
        new NetdiskCredentialField(
            AliyunTokenService.AccessTokenKey, "Access Token", NetdiskFieldKind.Secret, Required: false,
            Hint: "与 Refresh Token 至少填写一项"),
        new NetdiskCredentialField(
            AliyunTokenService.RefreshTokenKey, "Refresh Token", NetdiskFieldKind.Secret, Required: false,
            Hint: "填写后可自动续期，需同时填写 App ID 与 App Secret"),
    };

    public INetdiskOAuthFlow OAuth => this;

    public async Task<INetdiskSession> ConnectAsync(
        NetdiskCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(credentials.Get(AliyunTokenService.AccessTokenKey)) &&
            string.IsNullOrEmpty(credentials.Get(AliyunTokenService.RefreshTokenKey)))
        {
            throw new NetdiskAuthException("请先填写 access_token 或 refresh_token。", "MissingCredentials");
        }

        if (!string.IsNullOrEmpty(credentials.Get(AliyunTokenService.RefreshTokenKey)) &&
            (string.IsNullOrEmpty(credentials.Get(AppIdKey)) || string.IsNullOrEmpty(credentials.Get(AppSecretKey))))
        {
            throw new NetdiskAuthException(
                "refresh_token 自动续期需要同时填写 App ID 与 App Secret。",
                "MissingAppCredentials");
        }

        var tokens = CreateTokenService(credentials);
        return await AliyunDriveSession
            .OpenAsync(_http, tokens, credentials, _clock, cancellationToken)
            .ConfigureAwait(false);
    }

    public Uri BuildAuthorizeUrl(NetdiskCredentials appCredentials, string state)
    {
        var clientId = Require(appCredentials, AppIdKey, "请先在网盘设置中填写 App ID。");
        var redirectUri = Require(appCredentials, RedirectUriKey, "请先在网盘设置中填写回调地址。");

        var query = new StringBuilder()
            .Append("response_type=code")
            .Append("&client_id=").Append(Uri.EscapeDataString(clientId))
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri))
            .Append("&scope=").Append(Uri.EscapeDataString(Net.AlipanApi.DefaultScope))
            .Append("&state=").Append(Uri.EscapeDataString(state));
        return new Uri(Net.AlipanApi.AuthorizeUrl + "?" + query);
    }

    public async Task<NetdiskCredentials> ExchangeCodeAsync(
        NetdiskCredentials appCredentials,
        string code,
        string state,
        CancellationToken cancellationToken = default)
    {
        _ = state; // anti-forgery check belongs to the caller that generated the state.
        var tokens = CreateTokenService(appCredentials);
        // The app credentials become the bag's base so the whole account persists as one entry.
        return await tokens.ExchangeCodeAsync(appCredentials, code, cancellationToken).ConfigureAwait(false);
    }

    private AliyunTokenService CreateTokenService(NetdiskCredentials credentials) =>
        new(
            _http,
            credentials.Get(AppIdKey) ?? string.Empty,
            credentials.Get(AppSecretKey) ?? string.Empty,
            _clock);

    private static string Require(NetdiskCredentials credentials, string key, string message)
    {
        var value = credentials.Get(key);
        return string.IsNullOrEmpty(value)
            ? throw new NetdiskAuthException(message, "MissingAppCredentials")
            : value;
    }
}
