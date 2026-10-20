namespace WatermarkRemover.Core.Netdisk;

/// <summary>
/// One netdisk product (阿里云盘, 百度网盘, 115 …) plugged into the unified 网盘 entry point.
/// Implementations are stateless description + connection factories; credentials are supplied at
/// connect time so one class serves every account. Adding a product means adding one class in the
/// WatermarkRemover.Netdisk project — <see cref="NetdiskProviderCatalog.RegisterDiscovered"/> picks
/// it up with no wiring.
/// </summary>
public interface INetdiskProvider
{
    /// <summary>Stable machine identifier, e.g. "alipan".</summary>
    string Id { get; }

    /// <summary>Human-readable product name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Credential fields this product needs (API key, secret, token …). The settings UI builds its
    /// form from this list, so a new product gets its own form without settings-page changes.
    /// </summary>
    IReadOnlyList<NetdiskCredentialField> CredentialFields { get; }

    /// <summary>OAuth authorization flow when the product supports it; null = paste tokens directly.</summary>
    INetdiskOAuthFlow? OAuth { get; }

    /// <summary>
    /// Whether the settings UI should offer the assisted scan login (CLI-hosted OAuth). Default
    /// false; a product opts in by overriding.
    /// </summary>
    bool SupportsCliLogin => false;

    /// <summary>Opens a working session against the product. Throws <see cref="NetdiskAuthException"/> on bad credentials.</summary>
    Task<INetdiskSession> ConnectAsync(NetdiskCredentials credentials, CancellationToken cancellationToken = default);
}

/// <summary>One storage space of an account (阿里云盘: 备份盘/资源库/相册; single-drive products: one entry).</summary>
public sealed record NetdiskDrive(string Id, string Name);

/// <summary>
/// A live connection to one account of one product. File operations follow the shared vocabulary
/// (folder ids are opaque product strings; the root folder is the empty string) so the UI works
/// against any product unchanged.
/// </summary>
public interface INetdiskSession : IAsyncDisposable
{
    NetdiskAccountInfo Account { get; }

    /// <summary>Latest credentials after token rotation, for the persistence layer to write back.</summary>
    NetdiskCredentials CurrentCredentials { get; }

    /// <summary>Storage spaces the account exposes; all operations run against <see cref="CurrentDriveId"/>.</summary>
    IReadOnlyList<NetdiskDrive> Drives { get; }

    /// <summary>The drive id every operation currently targets (first drive by default).</summary>
    string CurrentDriveId { get; }

    /// <summary>Points subsequent operations at another drive of the same account.</summary>
    Task SwitchDriveAsync(string driveId, CancellationToken cancellationToken = default);

    Task<NetdiskQuota> GetQuotaAsync(CancellationToken cancellationToken = default);

    Task<NetdiskPage> ListAsync(
        string parentFolderId,
        int pageSize = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches files by name within the current drive. Page shape matches ListAsync. Products
    /// without a search capability throw NotSupportedException via the default implementation.
    /// </summary>
    Task<NetdiskPage> SearchAsync(
        string keyword,
        int pageSize = 100,
        string? cursor = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("该网盘不支持搜索。");

    /// <summary>
    /// Name of one folder/file by id, used to label where search results live. Returns null when
    /// the product cannot resolve it (UI then shows no location for the entry).
    /// </summary>
    Task<string?> GetNodeNameAsync(string nodeId, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    Task<NetdiskFile> CreateFolderAsync(
        string parentFolderId,
        string name,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(IReadOnlyList<string> fileIds, CancellationToken cancellationToken = default);

    Task<NetdiskFile> UploadAsync(
        string parentFolderId,
        string localPath,
        IProgress<NetdiskTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task DownloadAsync(
        string fileId,
        string localPath,
        IProgress<NetdiskTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Browser-based authorization (OAuth2 code flow) for products that support it.</summary>
public interface INetdiskOAuthFlow
{
    /// <summary>
    /// Builds the consent URL. <paramref name="appCredentials"/> is the settings form's current
    /// bag (client id, redirect uri …) so one flow object serves every configured app.
    /// </summary>
    Uri BuildAuthorizeUrl(NetdiskCredentials appCredentials, string state);

    /// <summary>
    /// Exchanges the authorization code for tokens. The returned bag keeps the app credentials so
    /// the whole account can be persisted as one entry. <paramref name="state"/> is echoed for the
    /// caller's anti-forgery check.
    /// </summary>
    Task<NetdiskCredentials> ExchangeCodeAsync(
        NetdiskCredentials appCredentials,
        string code,
        string state,
        CancellationToken cancellationToken = default);
}
