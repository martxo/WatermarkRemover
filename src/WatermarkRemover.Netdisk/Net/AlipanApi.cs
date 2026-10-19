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

    internal const string CreatePath = "/adrive/v1.0/openFile/create";

    internal const string GetUploadUrlPath = "/adrive/v1.0/openFile/getUploadUrl";

    internal const string CompletePath = "/adrive/v1.0/openFile/complete";

    internal const string DownloadUrlPath = "/adrive/v1.0/openFile/getDownloadUrl";

    internal const string TrashPath = "/adrive/v1.0/openFile/recyclebin/trash";

    /// <summary>Scopes requested at authorization: identity + full file read/write.</summary>
    internal const string DefaultScope = "user:base file:all:read file:all:write";

    /// <summary>Root of a drive listing; the API wants this literal instead of an empty parent id.</summary>
    internal const string RootFolderId = "root";
}
