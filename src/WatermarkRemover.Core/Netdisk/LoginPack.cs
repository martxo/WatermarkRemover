using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WatermarkRemover.Core.Netdisk;

/// <summary>
/// Portable 阿里云盘 login state. The aliyunpan CLI config is plain JSON (device id + tokens),
/// so it can be exported on one machine and imported on another. Device-local paths are stripped
/// so the CLI starts with defaults there; the account-level token/drive data is kept.
///
/// A pack carries live account tokens, so it never leaves a machine in the clear: <see cref="Protect"/>
/// wraps it in an AES-256-GCM envelope keyed by a passphrase (PBKDF2-SHA256). DPAPI cannot be used
/// here — the whole point is moving the pack to another PC. Legacy plaintext packs still import
/// (backward compatibility), but every new export is encrypted.
/// </summary>
public static class LoginPack
{
    /// <summary>Marker of the encrypted envelope; its absence means a legacy plaintext pack.</summary>
    public const string EnvelopeFormat = "wr-login-pack";

    private const int EnvelopeVersion = 2;
    private const int DefaultIterations = 210_000;

    /// <summary>Upper bound accepted on import: a hostile pack must not pin a CPU for minutes.</summary>
    private const int MaxIterations = 1_000_000;

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

    /// <summary>True for an encrypted envelope (as opposed to a legacy plaintext pack).</summary>
    public static bool IsProtected(string packText)
    {
        try
        {
            return JsonNode.Parse(packText) is JsonObject root &&
                   root["format"]?.GetValue<string>() == EnvelopeFormat;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Encrypts a plaintext pack with AES-256-GCM; the key comes from the passphrase via
    /// PBKDF2-SHA256. The envelope is self-contained (salt, nonce, tag, iterations), so the file
    /// is portable and only the passphrase has to travel separately.
    /// </summary>
    public static string Protect(string packJson, string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
            throw new ArgumentException("导出登录状态需要口令：口令为空会让明文令牌上传到云盘。", nameof(passphrase));

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] plain = Encoding.UTF8.GetBytes(packJson);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[16];

        using (var key = new AesGcm(DeriveKey(passphrase, salt, DefaultIterations), tag.Length))
        {
            key.Encrypt(nonce, plain, cipher, tag, associatedData: null);
        }

        var envelope = new JsonObject
        {
            ["format"] = EnvelopeFormat,
            ["version"] = EnvelopeVersion,
            ["algorithm"] = "AES-256-GCM/PBKDF2-SHA256",
            ["iterations"] = DefaultIterations,
            ["salt"] = Convert.ToBase64String(salt),
            ["nonce"] = Convert.ToBase64String(nonce),
            ["tag"] = Convert.ToBase64String(tag),
            ["payload"] = Convert.ToBase64String(cipher),
        };
        return envelope.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// Reverses <see cref="Protect"/>. A legacy plaintext pack is returned unchanged so old exports
    /// keep working; an encrypted one needs the right passphrase and throws a readable message
    /// otherwise.
    /// </summary>
    public static string Unprotect(string packText, string? passphrase)
    {
        if (!IsProtected(packText)) return packText; // legacy plaintext

        if (string.IsNullOrWhiteSpace(passphrase))
        {
            throw new InvalidOperationException(
                "这个登录包已加密，需要输入导出时设置的口令。");
        }

        JsonObject root = JsonNode.Parse(packText) as JsonObject
            ?? throw new FormatException("登录包不是 JSON 对象。");

        int iterations = root["iterations"]?.GetValue<int>() ?? DefaultIterations;
        if (iterations is < 10_000 or > MaxIterations)
            throw new InvalidOperationException("登录包的加密参数异常，已拒绝导入。");

        byte[] salt = Convert.FromBase64String(root["salt"]?.GetValue<string>() ?? string.Empty);
        byte[] nonce = Convert.FromBase64String(root["nonce"]?.GetValue<string>() ?? string.Empty);
        byte[] tag = Convert.FromBase64String(root["tag"]?.GetValue<string>() ?? string.Empty);
        byte[] cipher = Convert.FromBase64String(root["payload"]?.GetValue<string>() ?? string.Empty);
        if (salt.Length < 8 || nonce.Length != 12 || tag.Length != 16 || cipher.Length == 0)
            throw new InvalidOperationException("登录包内容不完整，已拒绝导入。");

        byte[] plain = new byte[cipher.Length];
        try
        {
            using var key = new AesGcm(DeriveKey(passphrase, salt, iterations), tag.Length);
            key.Decrypt(nonce, cipher, tag, plain, associatedData: null);
        }
        catch (CryptographicException)
        {
            throw new InvalidOperationException("口令不正确，无法解开这个登录包。");
        }

        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(passphrase), salt, iterations, HashAlgorithmName.SHA256, 32);
}
