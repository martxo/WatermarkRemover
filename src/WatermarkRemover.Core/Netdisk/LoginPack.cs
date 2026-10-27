using System.Text.Json;
using System.Text.Json.Nodes;

namespace WatermarkRemover.Core.Netdisk;

/// <summary>
/// Portable 阿里云盘 login state. The aliyunpan CLI config is plain JSON (device id + tokens),
/// so it can be exported on one machine and imported on another. Device-local paths are stripped
/// so the CLI starts with defaults there; the account-level token/drive data is kept.
/// </summary>
public static class LoginPack
{
    public const string FileName = "login-pack.json";

    /// <summary>Config entries that describe the exporting machine, not the account.</summary>
    private static readonly string[] DeviceLocalKeys =
    {
        "saveDir",
        "localWorkdir",
        "proxy",
        "localAddrs",
        "preferIPType",
        "updateCheckInfo",
        "cacheSize",
        "maxDownloadParallel",
        "maxUploadParallel",
        "maxDownloadRate",
        "maxUploadRate",
        "fileRecordConfig",
        "videoFileExtensions",
    };

    /// <summary>Builds a portable pack from the CLI's raw config JSON.</summary>
    public static string Create(string cliConfigJson)
    {
        JsonObject root = JsonNode.Parse(cliConfigJson) as JsonObject
            ?? throw new FormatException("aliyunpan 配置不是 JSON 对象。");
        foreach (string key in DeviceLocalKeys)
        {
            root.Remove(key);
        }

        root["portableLoginPack"] = true;
        root["exportedAt"] = DateTimeOffset.UtcNow.ToString("O");
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>True when the pack contains at least one usable openapi token.</summary>
    public static bool LooksValid(string packJson)
    {
        try
        {
            if (JsonNode.Parse(packJson) is not JsonObject root ||
                root["userList"] is not JsonArray users)
            {
                return false;
            }

            foreach (JsonNode? user in users)
            {
                if (user is JsonObject account &&
                    account["openapiToken"] is JsonObject token)
                {
                    string? value = token["accessToken"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(value))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}
