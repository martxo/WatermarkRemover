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

    [Fact]
    public void Protect_round_trips_with_the_same_passphrase()
    {
        string plain = LoginPack.Create(SampleConfig);
        string sealedPack = LoginPack.Protect(plain, "correct horse battery staple");

        Assert.True(LoginPack.IsProtected(sealedPack));
        Assert.DoesNotContain("open-token", sealedPack); // no token in the clear
        Assert.Equal(plain, LoginPack.Unprotect(sealedPack, "correct horse battery staple"));
    }

    [Fact]
    public void Protect_refuses_an_empty_passphrase()
    {
        // An empty passphrase would mean the cloud copy is effectively plaintext.
        Assert.Throws<ArgumentException>(() => LoginPack.Protect(LoginPack.Create(SampleConfig), " "));
    }

    [Fact]
    public void Unprotect_rejects_a_wrong_passphrase_and_a_missing_one()
    {
        string sealedPack = LoginPack.Protect(LoginPack.Create(SampleConfig), "right-key");

        InvalidOperationException wrong =
            Assert.Throws<InvalidOperationException>(() => LoginPack.Unprotect(sealedPack, "wrong-key"));
        Assert.Contains("口令不正确", wrong.Message);

        InvalidOperationException missing =
            Assert.Throws<InvalidOperationException>(() => LoginPack.Unprotect(sealedPack, null));
        Assert.Contains("需要输入", missing.Message);
    }

    [Fact]
    public void Legacy_plaintext_packs_still_import()
    {
        string plain = LoginPack.Create(SampleConfig);

        Assert.False(LoginPack.IsProtected(plain));
        Assert.Equal(plain, LoginPack.Unprotect(plain, null)); // backward compatibility
    }
}
