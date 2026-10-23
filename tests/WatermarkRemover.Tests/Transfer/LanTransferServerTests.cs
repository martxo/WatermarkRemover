using WatermarkRemover.Core.Features.Transfer;

namespace WatermarkRemover.Tests;

/// <summary>
/// Covers the parts of the LAN receiver that can be asserted without network I/O: URL/token
/// shape and the pure helpers (filename hygiene, boundary parsing, sender labels).
///
/// No test binds a listener — deliberately. Starting a real <c>HttpListener</c> from the test
/// host made Windows Firewall pop "是否要允许公共网络和专用网络访问此应用？ … testhost" on
/// every run, and every run gets a fresh timestamped binary path, so the remembered decision
/// never applied. The full HTTP round trips (page serving, multipart upload) are verified
/// against a real phone on the LAN.
/// </summary>
public sealed class LanTransferServerTests
{
    [Fact]
    public void Url_carries_the_session_token_in_the_query()
    {
        string url = LanTransferServer.ComposeUrl("192.168.10.31", 52125, "86ea65f9");

        Assert.Equal("http://192.168.10.31:52125/?t=86ea65f9", url);
        Assert.Contains("t=", new Uri(url).Query);
    }

    [Fact]
    public void Tokens_are_eight_lowercase_hex_chars_and_unique_per_session()
    {
        var tokens = Enumerable.Range(0, 20).Select(_ => LanTransferServer.NewToken()).ToList();

        Assert.All(tokens, token => Assert.Matches("^[0-9a-f]{8}$", token));
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
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
