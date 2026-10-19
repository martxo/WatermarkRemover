using System.Net;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Netdisk.Net;

namespace WatermarkRemover.Tests;

/// <summary>JSON sample → model mapping, error mapping and the 分片布局 arithmetic.</summary>
public class AlipanMappingTests
{
    [Fact]
    public void ListPageMapsItemsAndMarker()
    {
        const string json = """
        {
          "items": [
            { "file_id": "d1", "name": "资料", "type": "folder", "size": 0, "updated_at": "2026-10-01T08:00:00Z" },
            { "file_id": "f1", "name": "扫描.pdf", "type": "file", "size": 12345, "updated_at": "2026-10-02T09:30:00+08:00" }
          ],
          "next_marker": "cursor-1"
        }
        """;

        var page = AlipanJson.ParseListPage(json);

        Assert.Equal("cursor-1", page.NextCursor);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal(new NetdiskFile("d1", "资料", true, 0, DateTimeOffset.Parse("2026-10-01T08:00:00Z")), page.Items[0]);
        var file = page.Items[1];
        Assert.Equal("f1", file.Id);
        Assert.False(file.IsFolder);
        Assert.Equal(12345, file.Size);
        Assert.Equal(DateTimeOffset.Parse("2026-10-02T09:30:00+08:00"), file.ModifiedAt);
    }

    [Fact]
    public void ListPageTreatsEmptyMarkerAsLastPage()
    {
        var page = AlipanJson.ParseListPage("""{ "items": [], "next_marker": "" }""");

        Assert.Null(page.NextCursor);
        Assert.Empty(page.Items);
    }

    [Fact]
    public void SpaceInfoMapsQuotaFromNestedAndFlatShapes()
    {
        var nested = AlipanJson.ParseSpaceInfo(
            """{ "personal_space_info": { "used_size": 123, "total_size": 456 } }""");
        var flat = AlipanJson.ParseSpaceInfo("""{ "used_size": 7, "total_size": 9 }""");

        Assert.Equal(new NetdiskQuota(123, 456), nested);
        Assert.Equal(new NetdiskQuota(7, 9), flat);
    }

    [Fact]
    public void FileNodeMappingAcceptsFieldAliases()
    {
        // create 响应的 folder 节点 + 平台不同版本的字段别名（id/user_id、modified_at）。
        var folder = AlipanJson.ParseFileNode(
            System.Text.Json.JsonDocument.Parse("""{ "file_id": "d9", "name": "合同", "type": "folder" }""").RootElement);
        var aliased = AlipanJson.ParseUserInfo(
            """{ "user_id": "u2", "nickname": "李四" }""");

        Assert.Equal(new NetdiskFile("d9", "合同", true, 0, null), folder);
        Assert.Equal(new NetdiskAccountInfo("u2", "李四"), aliased);
    }

    [Fact]
    public void MapErrorPrefersAuthExceptionForTokenCodes()
    {
        var tokenError = AlipanJson.MapError(401,
            """{ "code": "AccessTokenExpired", "message": "token expired" }""", "fallback");
        var oauthError = AlipanJson.MapError(400,
            """{ "error": "invalid_grant", "error_description": "refresh token revoked" }""", "fallback");
        var plain401 = AlipanJson.MapError(401, string.Empty, "fallback");
        var otherError = AlipanJson.MapError(400,
            """{ "code": "AlreadyExist.File", "message": "文件已存在" }""", "fallback");

        Assert.IsType<NetdiskAuthException>(tokenError);
        Assert.Equal("AccessTokenExpired", tokenError.ProviderCode);
        Assert.Equal("token expired", tokenError.Message);
        Assert.Equal("invalid_grant", oauthError.ProviderCode);
        Assert.IsType<NetdiskAuthException>(plain401);
        Assert.Equal("Unauthorized", plain401.ProviderCode);
        Assert.IsType<NetdiskException>(otherError);
        Assert.IsNotType<NetdiskAuthException>(otherError);
    }

    [Fact]
    public void TokenRejectedCodesTriggerRefresh()
    {
        Assert.True(AlipanJson.IsTokenRejected("AccessTokenInvalid"));
        Assert.True(AlipanJson.IsTokenRejected("AccessTokenExpired"));
        Assert.True(AlipanJson.IsTokenRejected("I400JD"));
        Assert.False(AlipanJson.IsTokenRejected("AlreadyExist.File"));
        Assert.True(AlipanJson.IsAuthError("RefreshTokenExpired"));
        Assert.False(AlipanJson.IsAuthError("AlreadyExist.File"));
    }

    [Fact]
    public void PartPlanSplitsAndTotals()
    {
        var exact = AlipanUpload.PlanParts(8, 4);
        var remainder = AlipanUpload.PlanParts(10, 4);
        var small = AlipanUpload.PlanParts(3, 4);
        var empty = AlipanUpload.PlanParts(0, 4);

        Assert.Equal(new[] { (1L, 0L, 4L), (2L, 4L, 4L) }, exact.Select(p => ((long)p.PartNumber, p.Offset, p.Length)));
        Assert.Equal(new[] { (1L, 0L, 4L), (2L, 4L, 4L), (3L, 8L, 2L) }, remainder.Select(p => ((long)p.PartNumber, p.Offset, p.Length)));
        Assert.Equal(new[] { (1L, 0L, 3L) }, small.Select(p => ((long)p.PartNumber, p.Offset, p.Length)));
        Assert.Equal(new[] { (1L, 0L, 0L) }, empty.Select(p => ((long)p.PartNumber, p.Offset, p.Length)));

        // 进度计算基准：分片长度之和必须等于文件大小，各分片首尾相接。
        var plans = new[] { (Plan: exact, Total: 8L), (Plan: remainder, Total: 10L), (Plan: small, Total: 3L), (Plan: empty, Total: 0L) };
        foreach (var (plan, total) in plans)
        {
            Assert.Equal(total, plan.Sum(p => p.Length));
            for (var i = 1; i < plan.Count; i++)
            {
                Assert.Equal(plan[i - 1].Offset + plan[i - 1].Length, plan[i].Offset);
            }
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => AlipanUpload.PlanParts(10, 0));
    }

    [Fact]
    public void PartInfoListMapsNumbersToUrls()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            """{ "part_info_list": [ { "part_number": 2, "upload_url": "https://u/2" }, { "part_number": 1, "upload_url": "https://u/1" } ] }""");

        var urls = AlipanJson.ParsePartInfoList(doc.RootElement);

        Assert.Equal("https://u/1", urls[1]);
        Assert.Equal("https://u/2", urls[2]);
        Assert.Equal(2, urls.Count);
    }
}
