using System.Net;
using System.Net.Http;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Netdisk;
using WatermarkRemover.Netdisk.Net;

namespace WatermarkRemover.Tests;

/// <summary>Session behavior against a stubbed platform: token rejection replay, upload pipeline.</summary>
public class AliyunDriveSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static NetdiskCredentials Credentials() =>
        new(new Dictionary<string, string>
        {
            [AliyunDriveProvider.AppIdKey] = "app-1",
            [AliyunDriveProvider.AppSecretKey] = "secret-1",
            [AliyunTokenService.AccessTokenKey] = "old-access",
            [AliyunTokenService.RefreshTokenKey] = NetdiskHttpStub.FakeJwt(new { sub = "alice" }),
            [AliyunTokenService.ExpiresAtKey] = Now.AddHours(1).ToString("O"),
        });

    private static bool IsTokenCall(NetdiskHttpStub.StubCall call) =>
        call.Url.EndsWith(AlipanApi.AccessTokenPath, StringComparison.Ordinal);

    private static bool IsListCall(NetdiskHttpStub.StubCall call) =>
        call.Url.EndsWith(AlipanApi.ListPath, StringComparison.Ordinal);

    private static Task<AliyunDriveSession> OpenAsync(NetdiskHttpStub stub, NetdiskCredentials? credentials = null)
    {
        var http = new HttpClient(stub);
        var tokens = new AliyunTokenService(http, "app-1", "secret-1", () => Now);
        return AliyunDriveSession.OpenAsync(http, tokens, credentials ?? Credentials(), () => Now, CancellationToken.None);
    }

    /// <summary>Routes the bootstrap calls (users/info + getDriveInfo + token) and hands everything else to <paramref name="onApi"/>.</summary>
    private static Func<NetdiskHttpStub.StubCall, HttpResponseMessage> Router(
        Func<NetdiskHttpStub.StubCall, HttpResponseMessage> onApi)
    {
        return call => call.Url switch
        {
            var url when url.EndsWith(AlipanApi.UserInfoPath, StringComparison.Ordinal) =>
                NetdiskHttpStub.Json(HttpStatusCode.OK, """{ "id": "u1", "name": "张三" }"""),
            var url when url.EndsWith(AlipanApi.DriveInfoPath, StringComparison.Ordinal) =>
                NetdiskHttpStub.Json(HttpStatusCode.OK, """{ "default_drive_id": "drive-1" }"""),
            var url when url.EndsWith(AlipanApi.AccessTokenPath, StringComparison.Ordinal) =>
                NetdiskHttpStub.Json(HttpStatusCode.OK,
                    $"{{\"access_token\":\"new-access\",\"refresh_token\":\"{NetdiskHttpStub.FakeJwt(new { sub = "alice" })}\",\"expires_in\":7200}}"),
            _ => onApi(call),
        };
    }

    /// <summary>Synchronous progress sink; Progress&lt;T&gt; posts to a sync context and would race the assertions.</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    [Fact]
    public async Task OpenConnectsAndMapsAccount()
    {
        var stub = new NetdiskHttpStub { Responder = Router(_ => new HttpResponseMessage(HttpStatusCode.NotFound)) };

        await using var session = await OpenAsync(stub);

        Assert.Equal(new NetdiskAccountInfo("u1", "张三"), session.Account);
    }

    [Fact]
    public async Task TokenRejectionRefreshesOnceAndReplays()
    {
        var listCalls = 0;
        var stub = new NetdiskHttpStub
        {
            Responder = Router(call =>
            {
                if (IsListCall(call))
                {
                    listCalls++;
                    return listCalls == 1
                        ? NetdiskHttpStub.Json(HttpStatusCode.Unauthorized, """{ "code": "AccessTokenExpired", "message": "expired" }""")
                        : NetdiskHttpStub.Json(HttpStatusCode.OK,
                            """{ "items": [ { "file_id": "f1", "name": "a.pdf", "type": "file", "size": 1 } ], "next_marker": "" }""");
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }),
        };

        await using var session = await OpenAsync(stub);
        var page = await session.ListAsync(string.Empty);

        Assert.Single(page.Items);
        Assert.Equal(2, stub.CountCalls(IsListCall));
        Assert.Equal(1, stub.CountCalls(IsTokenCall));
    }

    [Fact]
    public async Task SecondTokenRejectionRaisesAuthException()
    {
        var stub = new NetdiskHttpStub
        {
            Responder = Router(call => IsListCall(call)
                ? NetdiskHttpStub.Json(HttpStatusCode.Unauthorized, """{ "code": "AccessTokenExpired", "message": "expired" }""")
                : new HttpResponseMessage(HttpStatusCode.NotFound)),
        };

        await using var session = await OpenAsync(stub);
        var ex = await Assert.ThrowsAsync<NetdiskAuthException>(() => session.ListAsync("root"));

        Assert.Equal("AccessTokenExpired", ex.ProviderCode);
        // 重试恰好一次：2 次 list + 1 次刷新，绝不三连击。
        Assert.Equal(2, stub.CountCalls(IsListCall));
        Assert.Equal(1, stub.CountCalls(IsTokenCall));
    }

    [Fact]
    public async Task ConnectWithoutTokensIsRejected()
    {
        var stub = new NetdiskHttpStub();
        var http = new HttpClient(stub);
        var provider = new AliyunDriveProvider(http, () => Now);

        var ex = await Assert.ThrowsAsync<NetdiskAuthException>(() =>
            provider.ConnectAsync(NetdiskCredentials.Empty));

        Assert.Equal("MissingCredentials", ex.ProviderCode);
        Assert.Empty(stub.Calls);
    }

    [Fact]
    public async Task UploadRunsCreatePutCompleteAndReportsProgress()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"netdisk-upload-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(temp, new byte[] { 1, 2, 3, 4, 5 });
        try
        {
            var stub = new NetdiskHttpStub
            {
                Responder = Router(call => call.Url switch
                {
                    var url when url.EndsWith(AlipanApi.CreatePath, StringComparison.Ordinal) =>
                        NetdiskHttpStub.Json(HttpStatusCode.OK,
                            """{ "file_id": "f1", "upload_id": "u1", "part_info_list": [ { "part_number": 1, "upload_url": "https://upload.test/part1" } ] }"""),
                    var url when url == "https://upload.test/part1" =>
                        new HttpResponseMessage(HttpStatusCode.OK),
                    var url when url.EndsWith(AlipanApi.CompletePath, StringComparison.Ordinal) =>
                        NetdiskHttpStub.Json(HttpStatusCode.OK,
                            """{ "file_id": "f1", "name": "a.bin", "type": "file", "size": 5 }"""),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                }),
            };
            await using var session = await OpenAsync(stub);
            var reports = new List<NetdiskTransferProgress>();
            var progress = new SyncProgress<NetdiskTransferProgress>(reports.Add);

            var file = await session.UploadAsync(string.Empty, temp, progress);

            Assert.Equal("f1", file.Id);
            Assert.Equal("a.bin", file.Name);
            Assert.Single(reports);
            Assert.Equal(5L, reports[0].BytesTransferred);
            Assert.Equal(5L, reports[0].TotalBytes);
            Assert.Equal(1, stub.CountCalls(c => c.Url == "https://upload.test/part1"));
            // create 携带分片清单，complete 收尾。
            var create = stub.Calls.Single(c => c.Url.EndsWith(AlipanApi.CreatePath, StringComparison.Ordinal));
            Assert.Contains("part_info_list", create.Body);
            Assert.Equal(1, stub.CountCalls(c => c.Url.EndsWith(AlipanApi.CompletePath, StringComparison.Ordinal)));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public async Task ExpiredUploadUrlIsRenewedOnce()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"netdisk-upload-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(temp, new byte[] { 9, 9 });
        try
        {
            var putCalls = 0;
            var stub = new NetdiskHttpStub
            {
                Responder = Router(call => call.Url switch
                {
                    var url when url.EndsWith(AlipanApi.CreatePath, StringComparison.Ordinal) =>
                        NetdiskHttpStub.Json(HttpStatusCode.OK,
                            """{ "file_id": "f1", "upload_id": "u1", "part_info_list": [ { "part_number": 1, "upload_url": "https://upload.test/part1" } ] }"""),
                    "https://upload.test/part1" =>
                        ++putCalls == 1
                            ? NetdiskHttpStub.Json(HttpStatusCode.Forbidden, """{ "code": "PreSignedUrlExpired", "message": "expired" }""")
                            : new HttpResponseMessage(HttpStatusCode.OK),
                    "https://upload.test/part1b" => new HttpResponseMessage(HttpStatusCode.OK),
                    var url when url.EndsWith(AlipanApi.GetUploadUrlPath, StringComparison.Ordinal) =>
                        NetdiskHttpStub.Json(HttpStatusCode.OK,
                            """{ "part_info_list": [ { "part_number": 1, "upload_url": "https://upload.test/part1b" } ] }"""),
                    var url when url.EndsWith(AlipanApi.CompletePath, StringComparison.Ordinal) =>
                        NetdiskHttpStub.Json(HttpStatusCode.OK, """{ "file_id": "f1", "name": "b.bin", "type": "file", "size": 2 }"""),
                    _ => new HttpResponseMessage(HttpStatusCode.NotFound),
                }),
            };
            await using var session = await OpenAsync(stub);

            var file = await session.UploadAsync(string.Empty, temp);

            Assert.Equal("f1", file.Id);
            Assert.Equal(1, stub.CountCalls(c => c.Url.EndsWith(AlipanApi.GetUploadUrlPath, StringComparison.Ordinal)));
            Assert.Equal(1, stub.CountCalls(c => c.Url == "https://upload.test/part1b"));
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
