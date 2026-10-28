namespace WatermarkRemover.Netdisk.Net;

/// <summary>
/// Endpoint surface of the 阿里云盘开放平台 (openapi.alipan.com) used by AliyunDriveProvider.
/// Kept in one place so a platform URL change is a one-line fix and the request builders stay
/// focused on payloads.
/// </summary>
internal static class AlipanApi
{
    internal const string BaseUrl = "https://openapi.alipan.com";

    internal const string AuthorizeUrl = BaseUrl + "/oauth/authorize";

    internal const string AccessTokenPath = "/oauth/access_token";

    internal const string UserInfoPath = "/oauth/users/info";

    internal const string DriveInfoPath = "/adrive/v1.0/user/getDriveInfo";

    internal const string SpaceInfoPath = "/adrive/v1.0/user/getSpaceInfo";

    internal const string ListPath = "/adrive/v1.0/openFile/list";

    internal const string SearchPath = "/adrive/v1.0/openFile/search";

    internal const string GetPath = "/adrive/v1.0/openFile/get";

    internal const string CreatePath = "/adrive/v1.0/openFile/create";

    internal const string GetUploadUrlPath = "/adrive/v1.0/openFile/getUploadUrl";

    internal const string CompletePath = "/adrive/v1.0/openFile/complete";

    internal const string MovePath = "/adrive/v1.0/openFile/move";

    internal const string DownloadUrlPath = "/adrive/v1.0/openFile/getDownloadUrl";

    internal const string TrashPath = "/adrive/v1.0/openFile/recyclebin/trash";

    /// <summary>Scopes requested at authorization: identity + full file read/write.</summary>
    internal const string DefaultScope = "user:base file:all:read file:all:write";

    /// <summary>Root of a drive listing; the API wants this literal instead of an empty parent id.</summary>
    internal const string RootFolderId = "root";

    // ---------------------------------------------------------------------
    // tickstep/aliyunpan CLI login bridge (api.tickstep.com). The CLI's author
    // hosts the OAuth app registration, so the browser authorize URL points the
    // authorization code at his server and the login token is fetched back from
    // there. Same endpoints the CLI binary itself uses (panlogin package).
    // ---------------------------------------------------------------------

    internal const string TickstepWebHost = "https://api.tickstep.com";

    internal const string TickstepQrCreatePath = "/auth/tickstep/aliyunpan/token/qrcode/create";

    internal const string TickstepTokenLoginPath = "/auth/tickstep/aliyunpan/token/common";

    /// <summary>Client id of the CLI author's registered alipan open-platform app.</summary>
    internal const string CliClientId = "cf9f70e8fc61430f8ec5ab5cadf31375";

    /// <summary>Scope list requested by the CLI login flow (comma form, as the CLI builds it).</summary>
    internal const string CliScope = "user:base,file:all:read,file:all:write,file:share:write,album:shared:read";
}
