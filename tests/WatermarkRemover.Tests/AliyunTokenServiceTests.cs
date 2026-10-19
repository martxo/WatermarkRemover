using System.Net;
using System.Net.Http;
using System.Text.Json;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Netdisk;
using WatermarkRemover.Netdisk.Net;

namespace WatermarkRemover.Tests;

/// <summary>Token lifecycle decisions: rotation, single-flight refresh, sub guard, expiry margin.</summary>
public class AliyunTokenServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static NetdiskCredentials Credentials(string refreshToken = "old-refresh", string accessToken = "old-access") =>
        new(new Dictionary<string, string>
        {
            [AliyunDriveProvider.AppIdKey] = "app-1",
            [AliyunDriveProvider.AppSecretKey] = "secret-1",
            [AliyunTokenService.AccessTokenKey] = accessToken,
            [AliyunTokenService.RefreshTokenKey] = refreshToken,
        });

    private static AliyunTokenService Service(NetdiskHttpStub stub) =>
        new(new HttpClient(stub), "app-1", "secret-1", () => Now);

    [Fact]
    public void NeedsRefreshFollowsExpiryMargin()
    {
        var margin = TimeSpan.FromMinutes(5);
        var fresh = Credentials().With(AliyunTokenService.ExpiresAtKey, Now.AddHours(1).ToString("O"));
        var stale = Credentials().With(AliyunTokenService.ExpiresAtKey, Now.AddMinutes(2).ToString("O"));
        var noExpiry = Credentials();
        var noToken = Credentials(accessToken: string.Empty);

        Assert.False(AliyunTokenService.NeedsRefresh(fresh, Now, margin));
        Assert.True(AliyunTokenService.NeedsRefresh(stale, Now, margin));
        Assert.False(AliyunTokenService.NeedsRefresh(noExpiry, Now, margin));
        Assert.True(AliyunTokenService.NeedsRefresh(noToken, Now, margin));
    }

    [Fact]
    public void ReadJwtSubParsesPayload()
    {
        var jwt = NetdiskHttpStub.FakeJwt(new { sub = "alice" });

        Assert.Equal("alice", AliyunTokenService.ReadJwtSub(jwt));
        Assert.Null(AliyunTokenService.ReadJwtSub("not-a-jwt"));
        Assert.Null(AliyunTokenService.ReadJwtSub("only.two"));
    }

    [Fact]
    public async Task RefreshRotatesTokensAndKeepsAppCredentials()
    {
        var stub = new NetdiskHttpStub
        {
            Responder = call => call.Url.EndsWith(AlipanApi.AccessTokenPath, StringComparison.Ordinal)
                ? NetdiskHttpStub.Json(HttpStatusCode.OK,
                    $"{{\"access_token\":\"new-access\",\"refresh_token\":\"{NetdiskHttpStub.FakeJwt(new { sub = "alice" })}\",\"expires_in\":7200}}")
                : new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        var result = await Service(stub).RefreshAsync(Credentials());

        Assert.Equal("new-access", result.Get(AliyunTokenService.AccessTokenKey));
        Assert.Equal(Now.AddSeconds(7200), DateTimeOffset.Parse(result.Get(AliyunTokenService.ExpiresAtKey)!));
        Assert.Equal("app-1", result.Get(AliyunDriveProvider.AppIdKey));
        Assert.Equal("secret-1", result.Get(AliyunDriveProvider.AppSecretKey));
        Assert.NotEqual("old-refresh", result.Get(AliyunTokenService.RefreshTokenKey));

        // The grant payload must be the refresh grant with the client credentials attached.
        var body = JsonDocumentParse(stub.Calls.Single().Body!);
        Assert.Equal("refresh_token", body.GetProperty("grant_type").GetString());
        Assert.Equal("old-refresh", body.GetProperty("refresh_token").GetString());
        Assert.Equal("app-1", body.GetProperty("client_id").GetString());
    }

    [Fact]
    public async Task RefreshRejectsMismatchedSubject()
    {
        var stub = new NetdiskHttpStub
        {
            Responder = _ => NetdiskHttpStub.Json(HttpStatusCode.OK,
                $"{{\"access_token\":\"new-access\",\"refresh_token\":\"{NetdiskHttpStub.FakeJwt(new { sub = "bob" })}\",\"expires_in\":7200}}"),
        };

        var ex = await Assert.ThrowsAsync<NetdiskAuthException>(() => Service(stub).RefreshAsync(
            Credentials(refreshToken: NetdiskHttpStub.FakeJwt(new { sub = "alice" }))));

        Assert.Equal("RefreshTokenSubjectMismatch", ex.ProviderCode);
    }

    [Fact]
    public async Task ConcurrentRefreshHitsTokenEndpointOnce()
    {
        var gate = new TaskCompletionSource();
        var stub = new NetdiskHttpStub
        {
            Responder = call =>
            {
                if (!call.Url.EndsWith(AlipanApi.AccessTokenPath, StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                // Hold the first rotation open until both callers are inside the service.
#pragma warning disable xUnit1031 // the sync stub handler must block until the gate opens
                gate.Task.GetAwaiter().GetResult();
#pragma warning restore xUnit1031
                return NetdiskHttpStub.Json(HttpStatusCode.OK,
                    $"{{\"access_token\":\"new-access\",\"refresh_token\":\"{NetdiskHttpStub.FakeJwt(new { sub = "alice" })}\",\"expires_in\":7200}}");
            },
        };
        var service = Service(stub);

        var first = Task.Run(() => service.RefreshAsync(Credentials()));
        var second = Task.Run(() => service.RefreshAsync(Credentials()));
        await Task.Delay(50);
        gate.SetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, stub.CountCalls(c => c.Url.EndsWith(AlipanApi.AccessTokenPath, StringComparison.Ordinal)));
        Assert.Equal("new-access", results[0].Get(AliyunTokenService.AccessTokenKey));
        Assert.Equal(results[0].Get(AliyunTokenService.RefreshTokenKey), results[1].Get(AliyunTokenService.RefreshTokenKey));
    }

    [Fact]
    public async Task ExchangeCodeReturnsTokensWithExpiry()
    {
        var stub = new NetdiskHttpStub
        {
            Responder = _ => NetdiskHttpStub.Json(HttpStatusCode.OK,
                $"{{\"access_token\":\"at\",\"refresh_token\":\"{NetdiskHttpStub.FakeJwt(new { sub = "alice" })}\",\"expires_in\":3600}}"),
        };

        var appCredentials = new NetdiskCredentials(new Dictionary<string, string>
        {
            [AliyunDriveProvider.AppIdKey] = "app-1",
            [AliyunDriveProvider.AppSecretKey] = "secret-1",
            [AliyunDriveProvider.RedirectUriKey] = "http://localhost",
        });
        var result = await Service(stub).ExchangeCodeAsync(appCredentials, "the-code");

        Assert.Equal("at", result.Get(AliyunTokenService.AccessTokenKey));
        Assert.Equal(Now.AddSeconds(3600), DateTimeOffset.Parse(result.Get(AliyunTokenService.ExpiresAtKey)!));
        Assert.Equal("http://localhost", result.Get(AliyunDriveProvider.RedirectUriKey));

        var body = JsonDocumentParse(stub.Calls.Single().Body!);
        Assert.Equal("authorization_code", body.GetProperty("grant_type").GetString());
        Assert.Equal("the-code", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RefreshWithoutRefreshTokenIsAnAuthError()
    {
        var stub = new NetdiskHttpStub();
        var credentials = Credentials().With(AliyunTokenService.RefreshTokenKey, string.Empty);

        var ex = await Assert.ThrowsAsync<NetdiskAuthException>(
            () => Service(stub).RefreshAsync(credentials));

        Assert.Equal("MissingRefreshToken", ex.ProviderCode);
        Assert.Empty(stub.Calls);
    }

    private static JsonElement JsonDocumentParse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
