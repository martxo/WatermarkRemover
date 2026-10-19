using WatermarkRemover.Core.Netdisk;

namespace WatermarkRemover.Tests;

/// <summary>Settings persistence for 网盘 accounts: which values are protected and how they come back.</summary>
public class NetdiskAccountPersistenceTests
{
    /// <summary>Reversible stand-in for DPAPI: the tests assert on the protecting behavior, not on DPAPI itself.</summary>
    private sealed class StubProtector(bool reject = false) : ISecretProtector
    {
        public List<string> ProtectedPlaintexts { get; } = new();

        public string Protect(string plaintext)
        {
            ProtectedPlaintexts.Add(plaintext);
            return new string(plaintext.Reverse().ToArray());
        }

        public string Unprotect(string protectedValue)
        {
            if (reject)
            {
                throw new InvalidOperationException("cannot decrypt on this machine");
            }

            return new string(protectedValue.Reverse().ToArray());
        }
    }

    private sealed class FakeProvider : INetdiskProvider
    {
        public FakeProvider(string id, params NetdiskCredentialField[] fields)
        {
            Id = id;
            CredentialFields = fields;
        }

        public string Id { get; }

        public string DisplayName => Id;

        public IReadOnlyList<NetdiskCredentialField> CredentialFields { get; }

        public INetdiskOAuthFlow? OAuth => null;

        public Task<INetdiskSession> ConnectAsync(
            NetdiskCredentials credentials,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static INetdiskProvider[] ProvidersWith(INetdiskProvider provider) => new[] { provider };

    private static NetdiskAccountState Account() => new(
        "alipan",
        "我的账号",
        new Dictionary<string, string>
        {
            ["app_id"] = "app-1",
            ["access_token"] = "secret-token",
        });

    /// <summary>Field-wise compare: record equality on the values dictionary would be reference-based.</summary>
    private static void AssertRestored(NetdiskAccountState account)
    {
        Assert.Equal("alipan", account.ProviderId);
        Assert.Equal("我的账号", account.Name);
        Assert.Equal("app-1", account.Values["app_id"]);
        Assert.Equal("secret-token", account.Values["access_token"]);
    }

    [Fact]
    public void EncodeProtectsOnlyDeclaredSecretFields()
    {
        var protector = new StubProtector();
        var providers = ProvidersWith(new FakeProvider("alipan",
            new NetdiskCredentialField("app_id", "App ID"),
            new NetdiskCredentialField("access_token", "Token", NetdiskFieldKind.Secret)));

        var records = NetdiskAccountPersistence.Encode(new[] { Account() }, providers, protector);

        var record = Assert.Single(records);
        Assert.Equal("app-1", record.Values["app_id"]);
        Assert.StartsWith(NetdiskAccountPersistence.ProtectedPrefix, record.Values["access_token"]);
        Assert.DoesNotContain("secret-token", record.Values["access_token"], StringComparison.Ordinal);
        Assert.Equal(new[] { "secret-token" }, protector.ProtectedPlaintexts);
    }

    [Fact]
    public void RoundTripRestoresPlaintext()
    {
        var protector = new StubProtector();
        var providers = ProvidersWith(new FakeProvider("alipan",
            new NetdiskCredentialField("access_token", "Token", NetdiskFieldKind.Secret)));

        var decoded = NetdiskAccountPersistence.Decode(
            NetdiskAccountPersistence.Encode(new[] { Account() }, providers, protector),
            protector);

        AssertRestored(Assert.Single(decoded));
    }

    [Fact]
    public void DecodeIsProviderIndependentAndTolerant()
    {
        var protector = new StubProtector();
        var records = NetdiskAccountPersistence.Encode(
            new[] { Account() },
            ProvidersWith(new FakeProvider("alipan",
                new NetdiskCredentialField("access_token", "Token", NetdiskFieldKind.Secret))),
            protector);

        // No provider list at decode time: the dpapi: prefix alone marks the value as protected.
        var decoded = NetdiskAccountPersistence.Decode(records, protector);

        AssertRestored(Assert.Single(decoded));
    }

    [Fact]
    public void DecodeSkipsAccountWhoseSecretCannotBeRestored()
    {
        var protector = new StubProtector(reject: true);
        string? traceMessage = null;
        var records = new[]
        {
            new PersistedNetdiskAccount("alipan", "坏账号", new Dictionary<string, string>
            {
                ["access_token"] = NetdiskAccountPersistence.ProtectedPrefix + "opaque",
            }),
        };

        var decoded = NetdiskAccountPersistence.Decode(records, protector, (message, _) => traceMessage = message);

        Assert.Empty(decoded);
        Assert.Contains("坏账号", traceMessage);
    }

    [Fact]
    public void EncodeUnknownProviderStoresValuesUnchanged()
    {
        var protector = new StubProtector();

        var record = Assert.Single(NetdiskAccountPersistence.Encode(
            new[] { Account() }, Array.Empty<INetdiskProvider>(), protector));

        Assert.Equal("secret-token", record.Values["access_token"]);
        Assert.Empty(protector.ProtectedPlaintexts);
    }
}
