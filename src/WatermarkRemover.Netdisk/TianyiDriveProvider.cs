using WatermarkRemover.Core.Netdisk;

namespace WatermarkRemover.Netdisk;

/// <summary>
/// 天翼云盘 (personal cloud, cloud.189.cn). Authorization is a browser Cookie pasted once: the web
/// login now uses a slider captcha, so there is no dependable password flow to automate. The
/// credential is a single secret field, stored through the same DPAPI path as every other product.
/// The session exposes 我的空间 and, when the account belongs to one, 家庭空间 as separate drives.
/// </summary>
public sealed class TianyiDriveProvider : INetdiskProvider
{
    public const string CookieKey = "cookie";

    public string Id => "tianyi";

    public string DisplayName => "天翼云盘";

    public IReadOnlyList<NetdiskCredentialField> CredentialFields { get; } = new[]
    {
        new NetdiskCredentialField(
            CookieKey,
            "Cookie（登录凭证）",
            NetdiskFieldKind.Secret,
            Required: true,
            Hint: "浏览器登录 cloud.189.cn 后，从开发者工具 Network 里复制请求的 Cookie 整串粘贴到这里；" +
                  "Cookie 失效时重新复制一次即可"),
    };

    public INetdiskOAuthFlow? OAuth => null;

    public async Task<INetdiskSession> ConnectAsync(
        NetdiskCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        var cookie = credentials.Get(CookieKey);
        if (string.IsNullOrWhiteSpace(cookie))
        {
            throw new NetdiskAuthException(
                "请先在网盘设置中粘贴天翼云盘的 Cookie。", "TianyiMissingCookie");
        }

        if (!cookie.Contains('=', StringComparison.Ordinal))
        {
            throw new NetdiskAuthException(
                "Cookie 格式不正确，应形如 SESSION_KEY=...; ...（整串粘贴）。", "TianyiBadCookie");
        }

        var http = new HttpClient(TianyiHttp.CreateHandler(allowAutoRedirect: false))
        {
            Timeout = TimeSpan.FromMinutes(2),
        };
        var session = new TianyiDriveSession(
            http, cookie.Trim(), credentials, new NetdiskAccountInfo("tianyi", "天翼云盘用户"));

        await session.ProbeAsync(cancellationToken).ConfigureAwait(false);
        return session;
    }
}
