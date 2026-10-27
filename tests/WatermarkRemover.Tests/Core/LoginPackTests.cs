using System.Text.Json;
using WatermarkRemover.Core.Netdisk;

namespace WatermarkRemover.Tests.Core;

/// <summary>
/// The private login pack moves the aliyunpan tokens to another machine. Device-local paths are
/// stripped so the CLI starts cleanly there; the account token must survive untouched.
/// </summary>
public sealed class LoginPackTests
{
    private const string SampleConfig = """
        {
          "configVer": "1",
          "activeUID": "u1",
          "deviceId": "dev-1",
          "saveDir": "C:/Users/me/Downloads",
          "localWorkdir": "C:/work",
          "proxy": "http://127.0.0.1:7890",
          "userList": [
            {
              "userId": "u1",
              "nickname": "tester",
              "openapiToken": { "accessToken": "open-token", "expired": 1893456000 },
              "webapiToken": { "accessToken": "web-token" }
            }
          ]
        }
        """;

    [Fact]
    public void Create_strips_device_local_paths_and_keeps_the_account_token()
    {
        string pack = LoginPack.Create(SampleConfig);

        using var doc = JsonDocument.Parse(pack);
        JsonElement root = doc.RootElement;
        Assert.True(root.GetProperty("portableLoginPack").GetBoolean());
        Assert.False(root.TryGetProperty("saveDir", out _));
        Assert.False(root.TryGetProperty("localWorkdir", out _));
        Assert.False(root.TryGetProperty("proxy", out _));
        Assert.Equal("open-token",
            root.GetProperty("userList")[0].GetProperty("openapiToken").GetProperty("accessToken").GetString());
    }

    [Fact]
    public void LooksValid_accepts_an_exported_pack_and_rejects_empty_json()
    {
        Assert.True(LoginPack.LooksValid(LoginPack.Create(SampleConfig)));
        Assert.False(LoginPack.LooksValid("{}"));
        Assert.False(LoginPack.LooksValid("not json"));
    }
}
