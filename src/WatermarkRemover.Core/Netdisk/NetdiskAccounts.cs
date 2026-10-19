namespace WatermarkRemover.Core.Netdisk;

/// <summary>One configured 网盘 account in memory: product id, user label, plaintext credential bag.</summary>
public sealed record NetdiskAccountState(string ProviderId, string Name, IReadOnlyDictionary<string, string> Values)
{
    public NetdiskCredentials ToCredentials() => new(Values);
}

/// <summary>JSON shape stored inside the settings file; secret values carry the <c>dpapi:</c> prefix.</summary>
public sealed record PersistedNetdiskAccount(string ProviderId, string Name, Dictionary<string, string> Values);

/// <summary>
/// Converts account state to/from the persisted shape. Which values get protected comes from the
/// provider's credential field declarations, so a new product's secrets are encrypted without any
/// change here; decoding is prefix-driven and therefore provider-independent.
/// </summary>
public static class NetdiskAccountPersistence
{
    public const string ProtectedPrefix = "dpapi:";

    public static List<PersistedNetdiskAccount> Encode(
        IEnumerable<NetdiskAccountState> accounts,
        IEnumerable<INetdiskProvider> providers,
        ISecretProtector protector)
    {
        var records = new List<PersistedNetdiskAccount>();
        foreach (var account in accounts)
        {
            var secretKeys = SecretKeys(providers, account.ProviderId);
            var values = new Dictionary<string, string>();
            foreach (var (key, value) in account.Values)
            {
                values[key] = secretKeys.Contains(key) && !value.StartsWith(ProtectedPrefix, StringComparison.Ordinal)
                    ? ProtectedPrefix + protector.Protect(value)
                    : value;
            }

            records.Add(new PersistedNetdiskAccount(account.ProviderId, account.Name, values));
        }

        return records;
    }

    public static List<NetdiskAccountState> Decode(
        IEnumerable<PersistedNetdiskAccount> records,
        ISecretProtector protector,
        Action<string, Exception?>? trace = null)
    {
        var accounts = new List<NetdiskAccountState>();
        foreach (var record in records)
        {
            try
            {
                var values = new Dictionary<string, string>();
                foreach (var (key, value) in record.Values)
                {
                    values[key] = value.StartsWith(ProtectedPrefix, StringComparison.Ordinal)
                        ? protector.Unprotect(value[ProtectedPrefix.Length..])
                        : value;
                }

                accounts.Add(new NetdiskAccountState(record.ProviderId, record.Name, values));
            }
            catch (Exception ex)
            {
                // A value that cannot be restored (machine move / different Windows account) skips
                // just that account — the user re-enters it instead of the app failing to start.
                trace?.Invoke($"网盘账号「{record.Name}」凭证无法解密，已跳过。", ex);
            }
        }

        return accounts;
    }

    private static HashSet<string> SecretKeys(IEnumerable<INetdiskProvider> providers, string providerId)
    {
        var provider = providers.FirstOrDefault(p => p.Id == providerId);
        return provider is null
            ? new HashSet<string>()
            : provider.CredentialFields
                .Where(f => f.Kind == NetdiskFieldKind.Secret)
                .Select(f => f.Key)
                .ToHashSet(StringComparer.Ordinal);
    }
}
