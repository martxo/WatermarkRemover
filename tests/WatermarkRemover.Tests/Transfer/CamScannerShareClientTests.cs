using WatermarkRemover.Core.Features.Transfer;

namespace WatermarkRemover.Tests;

/// <summary>
/// Pure-logic coverage for the CamScanner share pipeline: link extraction from received text,
/// the /s/{eid}/{sid} path split, and the share-info JSON shape. The network chain itself
/// (short-link hops, query, page downloads) is verified live against cs8.intsig.net.
/// </summary>
public sealed class CamScannerShareClientTests
{
    [Theory]
    [InlineData("https://link.camscanner.com/26wI5DHRUR")]
    [InlineData("https://www.camscanner.com/s/MHgxNzRlYmEyM2U0/72ED61CN")]
    public void TryExtractShareLink_BareLink_Matches(string link)
    {
        Assert.True(CamScannerShareClient.TryExtractShareLink(link, out string extracted));
        Assert.Equal(link, extracted);
    }

    [Fact]
    public void TryExtractShareLink_LinkInsideText_TrimsTrailingPunctuation()
    {
        const string text = "这是文件 https://link.camscanner.com/26wI5DHRUR。 请查收";

        Assert.True(CamScannerShareClient.TryExtractShareLink(text, out string link));
        Assert.Equal("https://link.camscanner.com/26wI5DHRUR", link);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("普通文本，没有链接")]
    [InlineData("https://example.com/s/abc/def")]
    [InlineData("https://www.camscanner.com/intl/upgrade")]
    public void TryExtractShareLink_NonShareText_ReturnsFalse(string? text)
    {
        Assert.False(CamScannerShareClient.TryExtractShareLink(text, out string link));
        Assert.Equal(string.Empty, link);
    }

    [Fact]
    public void ParseSharePath_SplitUrlIntoIds()
    {
        var (eid, sid) = CamScannerShareClient.ParseSharePath(
            new Uri("https://www.camscanner.com/s/MHgxNzRlYmEyM2U0/72ED61CN"))!.Value;

        Assert.Equal("MHgxNzRlYmEyM2U0", eid);
        Assert.Equal("72ED61CN", sid);
    }

    [Theory]
    [InlineData("https://www.camscanner.com/")]
    [InlineData("https://www.camscanner.com/s/abc")] // too few segments
    public void ParseSharePath_NotShareShape_ReturnsNull(string url)
    {
        Assert.Null(CamScannerShareClient.ParseSharePath(new Uri(url)));
    }

    [Fact]
    public void ParseShareInfo_RealShape_ReadsTitleAndPages()
    {
        const string json = """
            {"error_code":0,"error_msg":"",
             "data":{"share_info":{"title":"扫描全能王 2026-9-30 14.36","doc_id":"x"},
                     "doc_info":{"file_size":1009997,
                                 "page_list":[{"file_name":"RNT63H2N2AWQUrTBDJfFeDV9"},{"file_name":"PAGE2"}]}}}
            """;

        var info = CamScannerShareClient.ParseShareInfo(json);

        Assert.Equal("扫描全能王 2026-9-30 14.36", info.Title);
        Assert.Equal(new[] { "RNT63H2N2AWQUrTBDJfFeDV9", "PAGE2" }, info.PageFileNames);
        Assert.Equal("扫描全能王 2026-9-30 14.36.pdf", CamScannerShareClient.SuggestFileName(info));
    }

    [Fact]
    public void ParseShareInfo_ErrorCode_ThrowsWithServerMessage()
    {
        const string json = """{"error_code":403,"error_msg":"need extract code"}""";

        var ex = Assert.Throws<InvalidOperationException>(() => CamScannerShareClient.ParseShareInfo(json));
        Assert.Contains("need extract code", ex.Message);
    }

    [Fact]
    public void ParseShareInfo_EmptyPageList_Throws()
    {
        const string json = """
            {"error_code":0,"data":{"share_info":{"title":"t"},"doc_info":{"page_list":[]}}}
            """;

        Assert.Throws<InvalidOperationException>(() => CamScannerShareClient.ParseShareInfo(json));
    }

    [Theory]
    [InlineData("""{"error_code":0,"error_msg":""}""")]                 // success without data payload
    [InlineData("""{"error_code":0,"data":null}""")]                    // null data
    [InlineData("""{"error_code":0,"data":{}}""")]                      // data without doc_info
    [InlineData("""{"error_code":0,"data":{"doc_info":{}}}""")]         // doc_info without page_list
    public void ParseShareInfo_UnexpectedShape_ThrowsFriendlyWithSnippet(string json)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CamScannerShareClient.ParseShareInfo(json));
        Assert.Contains("意外的内容格式", ex.Message);
        Assert.Contains(json, ex.Message); // body snippet travels with the message
    }

    [Fact]
    public void SuggestFileName_BlankTitle_FallsBack()
    {
        var info = new CamScannerShareClient.ShareDocumentInfo("  ", new[] { "a" });

        Assert.Equal("分享文档.pdf", CamScannerShareClient.SuggestFileName(info));
    }
}
