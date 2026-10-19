using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Netdisk;

namespace WatermarkRemover.Tests;

/// <summary>
/// The 网盘 extension contract and what actually lands on disk: plugging in a new product must
/// require only a class, and secrets must be stored as dpapi: ciphertext through the real protector.
/// </summary>
public class NetdiskExtensionPointTests
{
    [Fact]
    public void FakeNetdiskProviderIsRegisteredByDiscoveryAlone()
    {
        var catalog = new NetdiskProviderCatalog();

        catalog.RegisterDiscovered(typeof(NetdiskExtensionPointTests).Assembly);

        Assert.Contains(catalog.All, p => p.Id == "fake-netdisk");
    }

    [Fact]
    public void PersistedSecretsAreDpapiCiphertextAndRoundTrip()
    {
        var protector = new DpapiSecretProtector();
        var providers = new INetdiskProvider[]
        {
            new FakeNetdiskProvider(
                new NetdiskCredentialField("app_id", "App ID"),
                new NetdiskCredentialField("app_secret", "App Secret", NetdiskFieldKind.Secret)),
        };
        var account = new NetdiskAccountState("fake-netdisk", "我的账号", new Dictionary<string, string>
        {
            ["app_id"] = "plain-id",
            ["app_secret"] = "s3cret",
        });

        // What settings.json receives: the secret is protected, the plain field is untouched.
        var record = Assert.Single(NetdiskAccountPersistence.Encode(new[] { account }, providers, protector));
        Assert.Equal("plain-id", record.Values["app_id"]);
        Assert.StartsWith(NetdiskAccountPersistence.ProtectedPrefix, record.Values["app_secret"]);
        Assert.DoesNotContain("s3cret", record.Values["app_secret"], StringComparison.Ordinal);

        var restored = Assert.Single(NetdiskAccountPersistence.Decode(new[] { record }, protector));
        Assert.Equal("s3cret", restored.Values["app_secret"]);
    }

    /// <summary>
    /// The plug-in stub: only the interface is implemented, nothing registers it by hand. The
    /// parameterless constructor is what discovery's Activator.CreateInstance requires.
    /// </summary>
    public sealed class FakeNetdiskProvider : INetdiskProvider
    {
        public FakeNetdiskProvider()
        {
            CredentialFields = Array.Empty<NetdiskCredentialField>();
        }

        public FakeNetdiskProvider(params NetdiskCredentialField[] fields) => CredentialFields = fields;

        public string Id => "fake-netdisk";

        public string DisplayName => "测试网盘";

        public IReadOnlyList<NetdiskCredentialField> CredentialFields { get; }

        public INetdiskOAuthFlow? OAuth => null;

        public Task<INetdiskSession> ConnectAsync(
            NetdiskCredentials credentials,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
