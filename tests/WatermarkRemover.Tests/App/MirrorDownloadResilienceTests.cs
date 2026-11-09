using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using WatermarkRemover.App.Updates;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// 70 MB 包在另一台电脑上永远下载失败：Velopack 的 <c>DownloadFile</c> 把 <c>timeout</c> 当秒用
/// （官方契约是分钟），任何超过 30 秒的下载都被取消；而且失败后从头再来，断一次网就前功尽弃。
/// 这里钉死修正后的两个契约：时长按分钟计、断点按 Range 续传。
/// </summary>
public sealed class MirrorDownloadResilienceTests : IDisposable
{
    private const string PackageUrl =
        "https://github.com/martxo/WatermarkRemover/releases/download/v1.16.125/" +
        "WatermarkRemover-1.16.125-full.nupkg";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wm-download-" + Guid.NewGuid().ToString("N"));

    public MirrorDownloadResilienceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    /// <summary>Records each attempt's URL and Range header, then answers from the script.</summary>
    private sealed class Handler(
        Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        private int _calls;

        public List<(string Uri, string? Range)> Calls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.RequestUri?.ToString() ?? string.Empty, request.Headers.Range?.ToString()));
            return respond(request, _calls++, cancellationToken);
        }
    }

    private static HttpResponseMessage Content(HttpStatusCode status, byte[] bytes, long? length)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
        if (length is { } value) response.Content.Headers.ContentLength = value;
        return response;
    }

    private static HttpResponseMessage Partial(byte[] payload, int from)
    {
        HttpResponseMessage response = Content(
            HttpStatusCode.PartialContent, payload[from..], payload.Length - from);
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(
            from, payload.Length - 1, payload.Length);
        return response;
    }

    private static MirrorFileDownloader Downloader(Handler handler)
    {
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return new MirrorFileDownloader(client);
    }

    [Fact]
    public async Task A_download_longer_than_30_seconds_is_not_cancelled_because_timeout_is_minutes()
    {
        string target = Path.Combine(_dir, "package.nupkg");
        byte[] payload = "abcdefgh"u8.ToArray();
        var handler = new Handler(async (_, _, token) =>
        {
            await Task.Delay(300, token);
            return Content(HttpStatusCode.OK, payload, payload.Length);
        });

        // 0.02 分钟 = 1.2 秒。若仍按秒解释，这个超时只有 20 毫秒，延迟 300 毫秒的响应必被取消。
        await Downloader(handler).DownloadFile(PackageUrl, target, null, timeout: 0.02);

        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task A_dropped_download_resumes_with_a_range_request_on_the_next_candidate()
    {
        string target = Path.Combine(_dir, "package.nupkg");
        byte[] payload = "abcdefgh"u8.ToArray();
        var handler = new Handler((_, call, _) => Task.FromResult(call == 0
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new AbortingStream(payload[..3])) { Headers = { ContentLength = payload.Length } },
            }
            : Partial(payload, 3)));

        await Downloader(handler).DownloadFile(PackageUrl, target, null);

        Assert.Equal(2, handler.Calls.Count);
        Assert.Null(handler.Calls[0].Range);
        Assert.Equal("bytes=3-", handler.Calls[1].Range);
        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
        Assert.False(File.Exists(target + ".part"), "完成后 .part 必须并入目标文件");
    }

    [Fact]
    public async Task A_server_that_ignores_range_restarts_from_zero_instead_of_appending()
    {
        string target = Path.Combine(_dir, "package.nupkg");
        byte[] payload = "abcdefgh"u8.ToArray();
        File.WriteAllBytes(target + ".part", "abc"u8.ToArray());
        var handler = new Handler((_, _, _) =>
            Task.FromResult(Content(HttpStatusCode.OK, payload, payload.Length)));

        await Downloader(handler).DownloadFile(PackageUrl, target, null);

        Assert.Equal("bytes=3-", handler.Calls[0].Range);
        // 200 表示服务端没支持 Range：必须整份重写，不能把 abc 拼在前面。
        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task A_stale_partial_is_dropped_and_downloaded_again_after_416()
    {
        string target = Path.Combine(_dir, "package.nupkg");
        byte[] payload = "abcdefgh"u8.ToArray();
        File.WriteAllBytes(target + ".part", new byte[10]); // 比远端还长：过期残片
        var handler = new Handler((_, call, _) => Task.FromResult(call == 0
            ? Content(HttpStatusCode.RequestedRangeNotSatisfiable, Array.Empty<byte>(), 0)
            : Content(HttpStatusCode.OK, payload, payload.Length)));

        await Downloader(handler).DownloadFile(PackageUrl, target, null);

        Assert.Equal(2, handler.Calls.Count);
        Assert.Equal("bytes=10-", handler.Calls[0].Range);
        Assert.Null(handler.Calls[1].Range);
        Assert.Equal(payload, await File.ReadAllBytesAsync(target));
    }

    /// <summary>Returns some bytes, then breaks the connection like a dropped link.</summary>
    private sealed class AbortingStream(byte[] head) : Stream
    {
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_offset >= head.Length) throw new IOException("连接被重置（模拟断流）");
            int count = Math.Min(buffer.Length, head.Length - _offset);
            head.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
