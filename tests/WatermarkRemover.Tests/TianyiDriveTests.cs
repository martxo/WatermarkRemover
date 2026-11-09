using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Netdisk;

namespace WatermarkRemover.Tests;

/// <summary>
/// 天翼云盘 (cloud.189.cn) private-API mapping: list paging/order, folder creation read-back,
/// batch DELETE payloads, session-expiry typing, and the crypto the signed upload depends on.
/// </summary>
public class TianyiDriveTests
{
    private static TianyiDriveSession NewSession(NetdiskHttpStub stub) =>
        new(new HttpClient(stub), "SESSION_KEY=abc; COOKIE_LOGIN_USER=xyz",
            new NetdiskCredentials(new Dictionary<string, string>()),
            new NetdiskAccountInfo("tianyi", "天翼云盘用户"));

    private const string ListJson = """
        {
          "res_code": 0,
          "fileListAO": {
            "count": 3,
            "folderList": [
              { "id": 111, "name": "照片", "lastOpTime": "2026-11-01 09:30:00" }
            ],
            "fileList": [
              { "id": 222, "name": "征信.pdf", "size": 2048, "lastOpTime": "2026-11-05 18:00:00" },
              { "id": 333, "name": "报告.docx", "size": 4096, "lastOpTime": "2026-11-06 08:00:00" }
            ]
          }
        }
        """;

    [Fact]
    public async Task ListUsesPersonalRootAndMapsFoldersBeforeFiles()
    {
        var stub = new NetdiskHttpStub { Responder = _ => NetdiskHttpStub.Json(HttpStatusCode.OK, ListJson) };
        await using var session = NewSession(stub);

        var page = await session.ListAsync(string.Empty);

        Assert.Equal(3, page.Items.Count);
        Assert.True(page.Items[0].IsFolder);
        Assert.Equal("111", page.Items[0].Id);
        Assert.Equal("征信.pdf", page.Items[1].Name);
        Assert.Equal(2048, page.Items[1].Size);
        Assert.Null(page.NextCursor);
        var url = stub.Calls[0].Url;
        Assert.Contains("folderId=-11", url);
        Assert.Contains("pageNum=1", url);
    }

    [Fact]
    public async Task ListReturnsNextPageWhenThePageIsFull()
    {
        var stub = new NetdiskHttpStub { Responder = _ => NetdiskHttpStub.Json(HttpStatusCode.OK, ListJson) };
        await using var session = NewSession(stub);

        var page = await session.ListAsync(string.Empty, pageSize: 2);

        Assert.Equal("2", page.NextCursor);
    }

    [Fact]
    public async Task ExpiredSessionBecomesReauthError()
    {
        var stub = new NetdiskHttpStub
        {
            Responder = _ => NetdiskHttpStub.Json(HttpStatusCode.BadRequest,
                """{ "errorCode": "InvalidSessionKey", "errorMsg": "cookieUserSession is null or invalid" }"""),
        };
        await using var session = NewSession(stub);

        var ex = await Assert.ThrowsAsync<NetdiskAuthException>(() => session.ListAsync(string.Empty));

        Assert.Contains("Cookie", ex.Message);
        Assert.Equal("TianyiSessionExpired", ex.ProviderCode);
    }

    [Fact]
    public async Task CreateFolderPostsParentAndReadsBackTheId()
    {
        var stub = new NetdiskHttpStub
        {
            Responder = call => call.Url.Contains("createFolder.action")
                ? NetdiskHttpStub.Json(HttpStatusCode.OK, """{ "res_code": 0 }""")
                : NetdiskHttpStub.Json(HttpStatusCode.OK, """
                    { "res_code": 0, "fileListAO": { "count": 1,
                      "folderList": [ { "id": 777, "name": "归档", "lastOpTime": "2026-11-06 12:00:00" } ],
                      "fileList": [] } }
                    """),
        };
        await using var session = NewSession(stub);

        var folder = await session.CreateFolderAsync("", "归档");

        Assert.Equal("777", folder.Id);
        Assert.True(folder.IsFolder);
        Assert.Contains("parentFolderId=-11", stub.Calls[0].Body);
        Assert.Contains("folderName=", stub.Calls[0].Body);
    }

    [Fact]
    public async Task DeleteSendsBatchTaskWithCachedName()
    {
        var stub = new NetdiskHttpStub
        {
            Responder = call => call.Url.Contains("createBatchTask.action")
                ? NetdiskHttpStub.Json(HttpStatusCode.OK, """{ "res_code": 0 }""")
                : NetdiskHttpStub.Json(HttpStatusCode.OK, ListJson),
        };
        await using var session = NewSession(stub);

        await session.ListAsync(string.Empty);
        await session.DeleteAsync(new[] { "222" });

        var body = stub.Calls[^1].Body!;
        Assert.Contains("type=DELETE", body);
        Assert.Contains("222", body);
        Assert.Contains(Uri.EscapeDataString("征信.pdf"), body);
    }

    [Fact]
    public async Task MoveTargetsTheFolderId()
    {
        var stub = new NetdiskHttpStub
        {
            Responder = call => call.Url.Contains("createBatchTask.action")
                ? NetdiskHttpStub.Json(HttpStatusCode.OK, """{ "res_code": 0 }""")
                : NetdiskHttpStub.Json(HttpStatusCode.OK, ListJson),
        };
        await using var session = NewSession(stub);

        await session.ListAsync(string.Empty);
        await session.MoveAsync(new[] { "222" }, "999");

        var body = stub.Calls[^1].Body!;
        Assert.Contains("type=MOVE", body);
        Assert.Contains("targetFolderId=999", body);
    }

    [Fact]
    public async Task SearchIsReportedUnsupported()
    {
        await using var session = NewSession(new NetdiskHttpStub());
        await Assert.ThrowsAsync<NotSupportedException>(() => session.SearchAsync("x"));
    }

    [Fact]
    public async Task ProbeDiscoversFamilySpaceAsASecondDrive()
    {
        await using var session = NewSession(NewFamilyStub());

        await session.ProbeAsync();

        Assert.Equal(2, session.Drives.Count);
        Assert.Equal("我的空间", session.Drives[0].Name);
        Assert.Equal("family:539931872", session.Drives[1].Id);
        Assert.Equal("家庭空间（我的家）", session.Drives[1].Name);
    }

    [Fact]
    public async Task FamilyListingResolvesTheFamilyRootAndPrefixesNodeIds()
    {
        var stub = NewFamilyStub();
        await using var session = NewSession(stub);
        await session.ProbeAsync();

        await session.SwitchDriveAsync("family:539931872");
        var page = await session.ListAsync(string.Empty);

        var file = Assert.Single(page.Items);
        Assert.Equal("f:888", file.Id);
        Assert.Equal("共享相册", file.Name);
        Assert.True(file.IsFolder);

        var listCalls = stub.Calls.Where(c => c.Url.Contains("family/file/listFiles")).ToList();
        Assert.Equal(2, listCalls.Count);
        Assert.Contains("needPath=true", listCalls[0].Url);
        Assert.Contains("folderId=777", listCalls[1].Url);
        Assert.Equal("TOKEN", listCalls[1].Headers?["AccessToken"]);
        Assert.Contains("Signature", listCalls[1].Headers!.Keys);
        Assert.Contains("familyId=539931872", listCalls[1].Url);
    }

    private static NetdiskHttpStub NewFamilyStub()
    {
        const string personalList = ListJson;
        return new NetdiskHttpStub
        {
            Responder = call =>
            {
                if (call.Url.Contains("getUserBriefInfo", StringComparison.Ordinal))
                    return NetdiskHttpStub.Json(HttpStatusCode.OK, """{ "sessionKey": "web-session" }""");
                if (call.Url.Contains("getAccessTokenBySsKey", StringComparison.Ordinal))
                    return NetdiskHttpStub.Json(HttpStatusCode.OK, """{ "accessToken": "TOKEN" }""");
                if (call.Url.Contains("getFamilyList", StringComparison.Ordinal))
                    return NetdiskHttpStub.Json(HttpStatusCode.OK,
                        """{ "familyInfoResp": [ { "familyId": 539931872, "remarkName": "我的家" } ] }""");
                if (call.Url.Contains("family/file/listFiles", StringComparison.Ordinal))
                {
                    return call.Url.Contains("needPath=true", StringComparison.Ordinal)
                        ? NetdiskHttpStub.Json(HttpStatusCode.OK, """
                            { "path": [ { "fileId": "-11", "fileName": "全部文件" },
                                        { "fileId": "777", "fileName": "家庭云" } ] }
                            """)
                        : NetdiskHttpStub.Json(HttpStatusCode.OK, """
                            { "fileListAO": { "count": 1,
                              "folderList": [ { "id": 888, "name": "共享相册", "lastOpTime": "2026-11-06 12:00:00" } ],
                              "fileList": [] } }
                            """);
                }

                return NetdiskHttpStub.Json(HttpStatusCode.OK, personalList);
            },
        };
    }

    [Fact]
    public async Task ProviderRejectsMissingOrMalformedCookie()
    {
        var provider = new TianyiDriveProvider();

        await Assert.ThrowsAsync<NetdiskAuthException>(() => provider.ConnectAsync(NetdiskCredentials.Empty));
        await Assert.ThrowsAsync<NetdiskAuthException>(() => provider.ConnectAsync(
            new NetdiskCredentials(new Dictionary<string, string> { [TianyiDriveProvider.CookieKey] = "not-a-cookie" })));
    }

    [Fact]
    public void HarvestedCookieIsWrittenIntoTheAccount()
    {
        var catalog = new NetdiskProviderCatalog();
        catalog.Register(new TianyiDriveProvider());
        var accounts = new ObservableCollection<NetdiskAccountState>();
        var viewModel = new NetdiskAccountSettingViewModel(
            catalog, accounts, accounts.Add, (_, _) => { }, (_, _) => { });

        Assert.True(viewModel.IsTianyi);
        viewModel.ApplyTianyiCookie("SESSION_KEY=abc123; COOKIE_LOGIN_USER=xyz");

        var account = Assert.Single(accounts);
        Assert.Equal("tianyi", account.ProviderId);
        Assert.Equal("天翼云盘", account.Name);
        Assert.Equal("SESSION_KEY=abc123; COOKIE_LOGIN_USER=xyz",
            account.Values[TianyiDriveProvider.CookieKey]);
    }

    [Fact]
    public void ProviderIsDiscoveredWithTheOtherProducts()
    {
        var catalog = new NetdiskProviderCatalog();
        catalog.RegisterDiscovered(typeof(AliyunDriveProvider).Assembly);

        Assert.Contains(catalog.All, p => p.Id == "tianyi");
        var provider = catalog.All.First(p => p.Id == "tianyi");
        Assert.Equal("天翼云盘", provider.DisplayName);
        Assert.True(provider.CredentialFields.Single().Kind == NetdiskFieldKind.Secret);
    }

    [Fact]
    public void HmacSha1MatchesRfc2202Vector()
    {
        Assert.Equal(
            "de7c9b85b8b78aa6bc8a7a36f70a90701c9db4d9",
            TianyiCrypto.HmacSha1Hex("The quick brown fox jumps over the lazy dog", "key"));
    }

    [Fact]
    public void AesEcbMatchesKnownVector()
    {
        var key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
        var plain = Convert.FromHexString("6bc1bee22e409f96e93d7e117393172a");

        var cipher = TianyiCrypto.AesEcbEncrypt(plain, key);

        // PKCS7 appends a full block; the first block must match the published AES-ECB vector.
        Assert.Equal("3ad77bb40d7a3660a89ecaf32466ef97", Convert.ToHexString(cipher[..16]).ToLowerInvariant());
        Assert.Equal(32, cipher.Length);
    }

    [Fact]
    public void Base64ToHexIsByteExact()
    {
        foreach (var bytes in new[]
                 {
                     new byte[] { 0x01, 0x02, 0x03 },
                     Encoding.UTF8.GetBytes("Hello"),
                     Enumerable.Range(0, 64).Select(i => (byte)i).ToArray(),
                 })
        {
            var base64 = Convert.ToBase64String(bytes);
            Assert.Equal(Convert.ToHexString(bytes).ToLowerInvariant(), TianyiCrypto.B64ToHex(base64));
        }
    }

    [Fact]
    public void RsaEncryptProducesDecryptablePkcs1Ciphertext()
    {
        using var rsa = RSA.Create(2048);
        var publicKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());

        var cipher = Convert.FromBase64String(TianyiCrypto.RsaEncryptBase64(publicKey, "secret-key"));

        Assert.Equal("secret-key", Encoding.UTF8.GetString(rsa.Decrypt(cipher, RSAEncryptionPadding.Pkcs1)));
    }
}
