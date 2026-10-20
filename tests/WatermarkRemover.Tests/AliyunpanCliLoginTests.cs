using System.Net;
using System.Net.Http;
using System.Text.Json;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Netdisk;
using WatermarkRemover.Netdisk.Net;

namespace WatermarkRemover.Tests;

/// <summary>
/// CLI-hosted login bridge: authorize URL shape, ticket/token polling, CLI config parsing and
/// the external-renewal path of the token service (accounts without a refresh token).
/// </summary>
public class AliyunpanCliLoginTests
{
    [Fact]
    public void BuildAuthorizeUrlMirrorsCliShape()
    {
        var url = AliyunpanCliLoginService.BuildAuthorizeUrl("ticket-1");

        var query = url.Query;
        Assert.Contains("client_id=" + AlipanApi.CliClientId, query);
        Assert.Contains("ticket-1", query);
        Assert.Contains("scope=", query);
        Assert.StartsWith(AlipanApi.AuthorizeUrl, url.ToString());
    }

    [Fact]
    public async Task BeginAsyncCreatesTicketAndAuthorizeUrl()
    {
        var stub = new NetdiskHttpStub
        {
            Responder = call => call.Url.Contains(AlipanApi.TickstepQrCreatePath, StringComparison.Ordinal)
                ? NetdiskHttpStub.Json(HttpStatusCode.OK,
                    "{\"code\":0,\"msg\":\"ok\",\"data\":{\"tokenUrl\":\"https://api.tickstep.com/pages/tickstep/aliyunpan/qrcode/login?tokenId=tid-9\",\"expiredTime\":300}}")
                : new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var service = new AliyunpanCliLoginService(new HttpClient(stub));

        var ticket = await service.BeginAsync("device-1");

        Assert.Equal("tid-9", ticket.TicketId);
        Assert.Contains("tid-9", ticket.AuthorizeUrl.ToString());
        Assert.Contains(AlipanApi.CliClientId, ticket.AuthorizeUrl.ToString());
    }

    [Fact]
    public async Task WaitForTokenAsyncPollsUntilReady()
    {
        var calls = 0;
        var stub = new NetdiskHttpStub
        {
            Responder = call => ++calls == 1
                ? NetdiskHttpStub.Json(HttpStatusCode.OK, "{\"code\":500,\"msg\":\"not ready\",\"data\":null}")
                : NetdiskHttpStub.Json(HttpStatusCode.OK,
                    "{\"code\":0,\"msg\":\"ok\",\"data\":{\"openapi\":{\"accessToken\":\"acc-1\",\"expired\":1792454702},\"webapi\":{\"accessToken\":\"w\",\"expired\":1}}}"),
        };
        var service = new AliyunpanCliLoginService(new HttpClient(stub));

        var result = await service.WaitForTokenAsync("tid-9");

        Assert.Equal("acc-1", result.AccessToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792454702), result.ExpiresAt);
    }

    [Fact]
    public void TryReadConfigParsesActiveUser()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wr-cli-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
            {
             "activeUID": "u-2",
             "deviceId": "dev-abc",
             "userList": [
              { "userId": "u-1", "openapiToken": { "accessToken": "stale", "expired": 1 } },
              { "userId": "u-2", "openapiToken": { "accessToken": "tok-2", "expired": 1792454702 } }
             ]
            }
            """);

            var ok = AliyunpanCliRuntime.TryReadConfig(path, out var deviceId, out var token);

            Assert.True(ok);
            Assert.Equal("dev-abc", deviceId);
            Assert.Equal("tok-2", token.AccessToken);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792454702), token.ExpiredAt);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void BuildCredentialsCarriesCliFlagsAndExpiry()
    {
        var baseBag = new NetdiskCredentials(new Dictionary<string, string> { ["kept"] = "yes" });
        var expiry = DateTimeOffset.FromUnixTimeSeconds(1792454702);

        var bag = AliyunpanCliRuntime.BuildCredentials(baseBag, "acc-1", expiry);

        Assert.Equal("yes", bag.Get("kept"));
        Assert.Equal("true", bag.Get(AliyunpanCliRuntime.CliLoginKey));
        Assert.Equal("acc-1", bag.Get(AliyunTokenService.AccessTokenKey));
        Assert.Equal(expiry.ToString("O"), bag.Get(AliyunTokenService.ExpiresAtKey));
    }

    [Fact]
    public async Task RefreshAsyncDelegatesToExternalRenewerWhenNoRefreshToken()
    {
        var externalCalls = 0;
        var renewed = new NetdiskCredentials(new Dictionary<string, string>
        {
            [AliyunTokenService.AccessTokenKey] = "cli-fresh",
        });
        var service = new AliyunTokenService(
            new HttpClient(new NetdiskHttpStub()), string.Empty, string.Empty,
            () => DateTimeOffset.UtcNow,
            ct =>
            {
                externalCalls++;
                return Task.FromResult(renewed);
            });
        var credentials = new NetdiskCredentials(new Dictionary<string, string>
        {
            [AliyunTokenService.AccessTokenKey] = "expired-access",
        });

        var result = await service.RefreshAsync(credentials);

        Assert.Equal(1, externalCalls);
        Assert.Equal("cli-fresh", result.Get(AliyunTokenService.AccessTokenKey));
    }

    [Fact]
    public async Task RefreshAsyncWithoutRefreshTokenOrExternalRenewerThrows()
    {
        var service = new AliyunTokenService(
            new HttpClient(new NetdiskHttpStub()), "app-1", "secret-1");
        var credentials = new NetdiskCredentials(new Dictionary<string, string>
        {
            [AliyunTokenService.AccessTokenKey] = "expired-access",
        });

        var ex = await Assert.ThrowsAsync<NetdiskAuthException>(() => service.RefreshAsync(credentials));

        Assert.Equal("MissingRefreshToken", ex.ProviderCode);
    }

    [Fact]
    public void ProviderOptsIntoCliLogin()
    {
        var provider = new AliyunDriveProvider();

        Assert.True(provider.SupportsCliLogin);
        Assert.Equal("alipan", provider.Id);
    }
}
