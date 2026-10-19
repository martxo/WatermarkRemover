namespace WatermarkRemover.Core.Netdisk;

/// <summary>Failure reported by a netdisk product, mapped from its error response.</summary>
public class NetdiskException : Exception
{
    public NetdiskException(string message, string? providerCode = null, Exception? inner = null)
        : base(message, inner)
    {
        ProviderCode = providerCode;
    }

    /// <summary>Product-specific error code (e.g. AccessTokenExpired), for logs and retry decisions.</summary>
    public string? ProviderCode { get; }
}

/// <summary>Credentials rejected or expired beyond refresh — the account needs re-authorization.</summary>
public sealed class NetdiskAuthException : NetdiskException
{
    public NetdiskAuthException(string message, string? providerCode = null, Exception? inner = null)
        : base(message, providerCode, inner)
    {
    }
}
