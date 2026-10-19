namespace WatermarkRemover.Core.Netdisk;

/// <summary>
/// Reversible protection for credential values stored at rest. The netdisk account values in the
/// settings file must never contain readable tokens, so every secret field goes through this
/// before hitting disk.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Turns plaintext into an opaque stored form.</summary>
    string Protect(string plaintext);

    /// <summary>Reverses <see cref="Protect"/>; throws when the value cannot be restored here (e.g. after a machine move).</summary>
    string Unprotect(string protectedValue);
}
