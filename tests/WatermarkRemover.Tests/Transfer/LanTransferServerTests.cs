using System.Net;
using WatermarkRemover.Core.Features.Transfer;

namespace WatermarkRemover.Tests;

/// <summary>
/// Covers the parts of the LAN receiver that can be asserted without network I/O: URL/token
/// shape, lifecycle guards, and the two pure helpers (filename hygiene, boundary parsing).
/// The full HTTP round trips (page serving, multipart upload) are verified against a real
/// phone on the LAN — the test host on the dev box breaks async socket I/O, so end-to-end
/// socket tests cannot run reliably here.
/// </summary>
public sealed class LanTransferServerTests : IDisposable
{
    private readonly string _directory;
    private readonly LanTransferServer _server;

    public LanTransferServerTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"transfer-tests-{Guid.NewGuid():N}");
        _server = new LanTransferServer(new LanTransferServerOptions { PreferredPort = 0 });
        _server.Start(_directory);
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Start_PublishesUrlWithTokenAndPort()
    {
        Assert.True(_server.Port > 0);
        var uri = new Uri(_server.Url);
        Assert.StartsWith($"http://", _server.Url);
        Assert.Contains("t=", uri.Query);
    }

    [Fact]
    public void Start_Twice_Throws()
    {
        using var second = new LanTransferServer(new LanTransferServerOptions { PreferredPort = 0 });
        second.Start(Path.Combine(_directory, "second"));
        Assert.Throws<InvalidOperationException>(() => second.Start(Path.Combine(_directory, "third")));
    }

    [Theory]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15", "192.168.10.31", "iPhone · …31")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; M2012K11AC Build/KQ1A) AppleWebKit/537.36", "192.168.10.20", "Android 14 · …20")]
    [InlineData("Mozilla/5.0 (Linux; Android 13) MicroMessenger/8.0.49", "192.168.1.7", "Android 13 微信 · …7")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/126", "10.0.0.2", "Windows · …2")]
    [InlineData(null, "192.168.10.31", "手机 · …31")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X)", null, "iPhone")]
    public void DescribeSender_CombinesDeviceWithAddressTail(string? userAgent, string? address, string expected)
    {
        Assert.Equal(expected, LanTransferServer.DescribeSender(userAgent, address));
    }

    [Fact]
    public void DescribeSender_Ipv6Address_UsesLastSegmentAsTail()
    {
        Assert.Equal("手机 · …17b5",
            LanTransferServer.DescribeSender(null, "2409:8a5c:7e8c:e011:c51d:c477:dbde:17b5"));
    }

    [Theory]
    [InlineData("photo.png", "photo.png")]
    [InlineData("..\\..\\x.txt", "x.txt")]
    [InlineData("a/b/c.bin", "c.bin")]
    [InlineData("  spaced  .jpg", "spaced  .jpg")]
    [InlineData("", "未命名")]
    public void SanitizeFileName_StripsPathsAndInvalidChars(string input, string expected) =>
        Assert.Equal(expected, LanTransferServer.SanitizeFileName(input));

    [Theory]
    [InlineData("multipart/form-data; boundary=----WebKitFormBoundaryABC", "----WebKitFormBoundaryABC")]
    [InlineData("multipart/form-data; boundary=\"quoted\"", "quoted")]
    [InlineData("application/json", null)]
    [InlineData(null, null)]
    public void MultipartBoundary_ParsesContentType(string? contentType, string? expected) =>
        Assert.Equal(expected, LanTransferServer.MultipartBoundary(contentType));
}
