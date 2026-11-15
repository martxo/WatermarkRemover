using System.IO;
using System.Net;
using System.Net.Http;
using WatermarkRemover.App.Logging;

namespace WatermarkRemover.App.Updates;

/// <summary>
/// HTTP mirror of the update repository (the 腾讯云广州服务器 nginx box). Serves the exact same
/// layout as the old 阿里云盘 repository — <c>latest.json</c>, <c>versions.json</c>,
/// <c>history/&lt;版本&gt;.json</c>, <c>files/&lt;sha256&gt;</c> — and is now the PRIMARY update
/// channel (see <see cref="MirrorUpdateService"/>): file-level chunks, no account, no CDN hop.
///
/// Integrity is not at risk: chunks are content-addressed and SHA-256 verified after download,
/// so a tampered mirror cannot inject anything.
///
/// Timeouts are per call (there is no global HttpClient timeout): a manifest read is small and
/// must fail fast so the GitHub fallback starts early, while a chunk streams for as long as the
/// link needs. A 404 is a well-formed answer from a reachable server ("that file is not here") —
/// it never counts toward the circuit breaker, which exists purely for connectivity failures
/// (connect refused, DNS dead, read stalled): after <see cref="MaxConsecutiveFailures"/> of
/// those the mirror is bypassed for the rest of the process, because an unreachable mirror would
/// otherwise add its timeout to every request before the fallback even starts.
/// </summary>
internal sealed class MirrorUpdateSource : IDisposable
{
    /// <summary>Consecutive connectivity failures before the mirror is bypassed for this process.</summary>
    private const int MaxConsecutiveFailures = 2;

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private bool _disposed;

    /// <summary>Process-wide breaker state (survives service instances; resets on restart).</summary>
    private static int _consecutiveFailures;
    private static bool _tripped;

    private MirrorUpdateSource(string baseUrl)
    {
        _baseUrl = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
        // 超时按调用单独给（见各方法的 timeout 参数）：这里不能再挂一个全局超时，
        // 否则它会在清单读取之前就把整条请求连掉。
        _http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    /// <summary>Creates a source when a URL is configured, else null (mirror disabled).</summary>
    public static MirrorUpdateSource? TryCreate(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return new MirrorUpdateSource(uri.AbsoluteUri);
    }

    /// <summary>False once the breaker tripped — callers skip the mirror without any I/O.</summary>
    public static bool IsBypassed => Volatile.Read(ref _tripped);

    /// <summary>
    /// Downloads a repository file; null on ANY failure (404 included — the caller treats
    /// "missing" and "unreachable" the same way: fall back to GitHub).
    /// </summary>
    public async Task<byte[]?> TryGetBytesAsync(
        string relativePath, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (IsBypassed) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(
                _baseUrl + relativePath.TrimStart('/'), HttpCompletionOption.ResponseHeadersRead,
                cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 404 = 服务器明确说"没有这个文件"（可选文件 versions.json、保留窗口外的
                // history 清单都是正常答案）：不计熔断。其它状态码（5xx、被网关拦…）算故障。
                if (response.StatusCode != HttpStatusCode.NotFound)
                {
                    AppLog.Warn($"更新镜像 {relativePath} 返回 HTTP {(int)response.StatusCode}");
                    RecordFailure();
                }
                return null;
            }
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            ResetFailures();
            return bytes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            AppLog.Warn($"更新镜像 {relativePath} 读取失败（{timeout.TotalSeconds:F0}s 超时）：{ex.Message}");
            RecordFailure();
            return null;
        }
    }

    /// <summary>
    /// Streams a repository file to <paramref name="targetPath"/>. Used for the chunk downloads
    /// (the big ones) so they never buffer fully in memory. <see cref="MirrorFetchResult.Missing"/>
    /// (404) is permanent — the caller must not retry it — while
    /// <see cref="MirrorFetchResult.Unreachable"/> is a connectivity failure that may succeed on
    /// a later attempt and counts toward the breaker.
    /// </summary>
    public async Task<MirrorFetchResult> TryDownloadToFileAsync(
        string relativePath, TimeSpan timeout, string targetPath, CancellationToken cancellationToken)
    {
        if (IsBypassed) return MirrorFetchResult.Unreachable;
        string temp = targetPath + ".mirror";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(
                _baseUrl + relativePath.TrimStart('/'), HttpCompletionOption.ResponseHeadersRead,
                cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // 镜像上没有这个分块 = 清单与文件不同步（保留窗口算错了）。重试没有意义，
                    // 直接让这次更新失败、改走 GitHub；这也不是连通性问题，不计熔断。
                    return MirrorFetchResult.Missing;
                }

                AppLog.Warn($"更新镜像 {relativePath} 返回 HTTP {(int)response.StatusCode}");
                RecordFailure();
                return MirrorFetchResult.Unreachable;
            }
            await using (Stream source = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
            await using (FileStream target = File.Create(temp))
            {
                await source.CopyToAsync(target, cts.Token).ConfigureAwait(false);
            }
            File.Move(temp, targetPath, overwrite: true);
            ResetFailures();
            return MirrorFetchResult.Ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            AppLog.Warn($"更新镜像 {relativePath} 下载失败（{timeout.TotalSeconds:F0}s 超时）：{ex.Message}");
            RecordFailure();
            return MirrorFetchResult.Unreachable;
        }
    }

    private static void RecordFailure()
    {
        if (Interlocked.Increment(ref _consecutiveFailures) >= MaxConsecutiveFailures)
        {
            Volatile.Write(ref _tripped, true);
            AppLog.Warn("更新镜像连续失败，本次运行内不再尝试（重启后恢复）");
        }
    }

    private static void ResetFailures() => Volatile.Write(ref _consecutiveFailures, 0);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}

/// <summary>Outcome of one mirror fetch: downloaded, definitely-not-there, or unreachable.</summary>
internal enum MirrorFetchResult
{
    Ok,
    Missing,
    Unreachable,
}
