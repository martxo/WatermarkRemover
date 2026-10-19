using System.Security.Cryptography;
using System.Text;
using WatermarkRemover.Core.Netdisk;

namespace WatermarkRemover.App.Services;

/// <summary>
/// Protects netdisk credentials at rest with Windows DPAPI (current-user scope): only this Windows
/// account on this machine can restore them. The stored form is base64 of the protected bytes.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("watermarkremover.netdisk.v1");

    public string Protect(string plaintext) =>
        Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser));

    public string Unprotect(string protectedValue) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(
            Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser));
}
