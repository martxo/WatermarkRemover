using System.Security.Cryptography;
using System.Text;

namespace WatermarkRemover.Netdisk;

/// <summary>
/// Crypto pieces the 天翼云盘 private API expects. Verified against AList's 189 driver
/// (drivers/189/help.go): the upload API wants an AES-ECB encrypted query, an HMAC-SHA1
/// signature over that ciphertext, and the AES key RSA-encrypted (PKCS#1 v1.5).
/// </summary>
internal static class TianyiCrypto
{
    private const string B64Map = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private const string Base36Map = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary>RSA/PKCS#1 v1.5 over the server-provided base64 body (PEM without the header lines).</summary>
    public static string RsaEncryptBase64(string publicKeyBase64, string plainText)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
        var cipher = rsa.Encrypt(Encoding.UTF8.GetBytes(plainText), RSAEncryptionPadding.Pkcs1);
        return Convert.ToBase64String(cipher);
    }

    /// <summary>Same encryption, then base64→hex the way the login page does.</summary>
    public static string RsaEncryptHex(string publicKeyBase64, string plainText) =>
        B64ToHex(RsaEncryptBase64(publicKeyBase64, plainText));

    /// <summary>Base64 → lowercase hex, byte for byte (the login page's b64tohex).</summary>
    internal static string B64ToHex(string base64)
    {
        var builder = new StringBuilder(base64.Length);
        var carry = 0;
        var state = 0;
        foreach (var ch in base64)
        {
            if (ch == '=')
            {
                continue;
            }

            var value = B64Map.IndexOf(ch);
            switch (state)
            {
                case 0:
                    state = 1;
                    builder.Append(Base36Map[value >> 2]);
                    carry = 3 & value;
                    break;
                case 1:
                    state = 2;
                    builder.Append(Base36Map[(carry << 2) | (value >> 4)]);
                    carry = 15 & value;
                    break;
                case 2:
                    state = 3;
                    builder.Append(Base36Map[carry]);
                    builder.Append(Base36Map[value >> 2]);
                    carry = 3 & value;
                    break;
                default:
                    state = 0;
                    builder.Append(Base36Map[(carry << 2) | (value >> 4)]);
                    builder.Append(Base36Map[15 & value]);
                    break;
            }
        }

        if (state == 1)
        {
            builder.Append(Base36Map[carry << 2]);
        }

        return builder.ToString();
    }

    /// <summary>AES/ECB/PKCS7 (the CLI API encrypts block by block with no IV).</summary>
    public static byte[] AesEcbEncrypt(byte[] data, byte[] key)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        using var encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(data, 0, data.Length);
    }

    public static string HmacSha1Hex(string data, string secret)
    {
        using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    /// <summary>
    /// The per-request AES/HMAC secret. Mirrors AList's template
    /// "xxxxxxxxxxxx4xxxyxxxxxxxxxxxxxxx": 32 random hex-ish chars, truncated to 16..31.
    /// </summary>
    public static string RandomSecretKey()
    {
        const string template = "xxxxxxxxxxxx4xxxyxxxxxxxxxxxxxxx";
        var builder = new StringBuilder(template.Length);
        foreach (var ch in template)
        {
            builder.Append(ch switch
            {
                'x' => "0123456789abcdef"[RandomNumberGenerator.GetInt32(16)],
                'y' => "89ab"[RandomNumberGenerator.GetInt32(4)],
                _ => ch,
            });
        }

        var length = 16 + RandomNumberGenerator.GetInt32(16);
        return builder.ToString(0, Math.Min(length, builder.Length));
    }

    /// <summary>Form string in the shape the signature covers: k=v joined with '&amp;' (unencoded).</summary>
    public static string BuildQuery(IReadOnlyDictionary<string, string> form) =>
        string.Join("&", form.Select(pair => $"{pair.Key}={pair.Value}"));

    public static string RandomCacheBuster() =>
        "0." + RandomNumberGenerator.GetInt32(1, 100_000_000);
}
