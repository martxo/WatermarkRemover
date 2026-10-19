namespace WatermarkRemover.Core.Netdisk;

/// <summary>How the settings form should render a credential field.</summary>
public enum NetdiskFieldKind
{
    Text,
    Secret,
}

/// <summary>
/// One credential entry a product needs, declared by the provider. Key is machine-facing (stored in
/// settings), Label is what the form shows.
/// </summary>
public sealed record NetdiskCredentialField(
    string Key,
    string Label,
    NetdiskFieldKind Kind = NetdiskFieldKind.Text,
    bool Required = true,
    string? Hint = null);

/// <summary>The credential bag for one account of one product.</summary>
public sealed record NetdiskCredentials(IReadOnlyDictionary<string, string> Values)
{
    public static readonly NetdiskCredentials Empty = new(new Dictionary<string, string>());

    public string? Get(string key) => Values.TryGetValue(key, out var value) ? value : null;

    public NetdiskCredentials With(string key, string value)
    {
        var copy = new Dictionary<string, string>(Values) { [key] = value };
        return new NetdiskCredentials(copy);
    }
}
