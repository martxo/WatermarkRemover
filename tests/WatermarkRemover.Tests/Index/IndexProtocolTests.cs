using WatermarkRemover.Core.Features.Search;
using WatermarkRemover.Index;
using WatermarkRemover.Index.Contracts;
using WatermarkRemover.Index.Ipc;
using WatermarkRemover.Index.Setup;

namespace WatermarkRemover.Tests.Index;

/// <summary>
/// Wire contract of the index pipe: one JSON line per request/response, camelCase, nulls omitted.
/// The app and the elevated host are separate processes, so this format is the only coupling.
/// </summary>
public class IndexProtocolTests
{
    [Fact]
    public void Requests_serialise_in_camel_case_and_omit_nulls()
    {
        string json = IndexJson.Serialize(new IndexRequest { Cmd = "search", Query = "报告" });

        Assert.Equal("{\"cmd\":\"search\",\"query\":\"报告\",\"max\":200}", json);
        Assert.DoesNotContain("maxResults", json);
    }

    [Fact]
    public void Responses_round_trip_status_and_hits()
    {
        var response = new IndexResponse
        {
            Ok = true,
            Status = new IndexStatusDto(true, false, 120, 8, new[] { @"C:\" }, null),
            Results = new[]
            {
                new IndexHitDto(@"C:\a\b.pdf", false, 2048, new DateTime(2026, 10, 22, 9, 30, 0)),
                new IndexHitDto(@"C:\a", true, 0, null),
            },
        };

        string json = IndexJson.Serialize(response);
        IndexResponse? back = IndexJson.Deserialize<IndexResponse>(json);

        Assert.NotNull(back);
        Assert.True(back!.Ok);
        Assert.Equal(120, back.Status!.Files);
        Assert.Equal(new[] { @"C:\" }, back.Status.Volumes);
        Assert.Equal(2, back.Results!.Count);
        Assert.Equal(2048, back.Results[0].Size);
        Assert.Equal(new DateTime(2026, 10, 22, 9, 30, 0), back.Results[0].ModifiedAt);
        Assert.True(back.Results[1].IsFolder);
        Assert.Null(back.Results[1].ModifiedAt);
    }

    [Fact]
    public void Failure_serialises_only_ok_and_error()
    {
        string json = IndexJson.Serialize(IndexResponse.Failure("引擎未启动"));

        Assert.Equal("{\"ok\":false,\"error\":\"引擎未启动\"}", json);
    }

    private sealed class FakeClient : IIndexClient
    {
        public IndexResponse? Next { get; set; }

        public IndexRequest? LastRequest { get; private set; }

        public Task<IndexResponse?> SendAsync(IndexRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(Next);
        }
    }

    [Fact]
    public async Task Provider_ping_exposes_the_live_status()
    {
        var client = new FakeClient
        {
            Next = new IndexResponse
            {
                Ok = true,
                Status = new IndexStatusDto(true, false, 10, 2, new[] { @"C:\" }, null),
            },
        };
        var provider = new IndexServiceSearchProvider(client, new IndexSetupService(() => Path.GetTempPath()));

        Assert.False(provider.IsAvailable);
        Assert.True(await provider.CheckAvailableAsync());
        Assert.True(provider.IsAvailable);
        Assert.Equal("status", client.LastRequest!.Cmd);
        Assert.Equal(10, provider.LastStatus!.Files);
    }

    [Fact]
    public async Task Provider_maps_hits_and_flags_a_failed_engine()
    {
        var client = new FakeClient
        {
            Next = new IndexResponse
            {
                Ok = true,
                Results = new[] { new IndexHitDto(@"C:\a\b.pdf", false, 2048, null) },
            },
        };
        var provider = new IndexServiceSearchProvider(client, new IndexSetupService(() => Path.GetTempPath()));

        IReadOnlyList<FileSearchResult> hits = await provider.SearchAsync("b", 50);

        Assert.Single(hits);
        Assert.Equal(@"C:\a\b.pdf", hits[0].FullPath);
        Assert.Equal("search", client.LastRequest!.Cmd);
        Assert.Equal("b", client.LastRequest.Query);
        Assert.Equal(50, client.LastRequest.Max);
        Assert.True(provider.IsAvailable);

        client.Next = IndexResponse.Failure("boom");
        hits = await provider.SearchAsync("c", 50);

        Assert.Empty(hits);
        Assert.False(provider.IsAvailable);
    }
}
