using System.IO;
using System.Net.Http;
using WatermarkRemover.App.Logging;

namespace WatermarkRemover.App.Updates;

/// <summary>
/// HTTP mirror of the cloud update repository (the 腾讯云广州服务器 nginx box). Serves the exact
/// same layout as 阿里云盘 — <c>latest.json</c>, <c>versions.json</c>, <c>history/*.json</c>,
/// <c>files/&lt;sha256&gt;</c> — so every read in <see cref="CloudUpdateService"/> can try the
/// mirror first and fall back to the netdisk API on any failure.
///
/// Why the mirror exists: the Aliyun CDN rejects shared egress IPs with
/// <c>ExceedMaxConcurrency</c> (hotel/office NAT, 出差外地) and its signed-URL hop is slow. A
/// direct domestic HTTP fetch has neither problem. Integrity is not at risk: chunks are
/// content-addressed and SHA-256 verified after download, so a tampered mirror cannot inject
/// anything.
///
/// Circuit breaker: after <see cref="MaxConsecutiveFailures"/> consecutive failures the mirror
/// is bypassed for the rest of the process. Without it, an unreachable mirror would add its
/// connect timeout to EVERY chunk download (998 chunks × 3 s = an hour of dead air).
/// </summary>
internal sealed class MirrorUpdateSource : IDisposable
{
    /// <summary>Consecutive failures before the mirror is bypassed for this process.</summary>
    private const int MaxConsecutiveFailures = 2;

    /// <summary>Per-request budget: a domestic direct connect is tens of ms; 3 s is generous.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private bool _disposed;

    /// <summary>Process-wide breaker state (survives service instances; resets on restart).</summary>
    private static int _consecutiveFailures;
    private static bool _tripped;

    private MirrorUpdateSource(string baseUrl)
    {
        _baseUrl = baseUrl.EndsWith("/") ? baseUrl : baseUrl + "/";
        _http = new HttpClient { Timeout = RequestTimeout };
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

    /// <summary>Downloads a repository file; null on ANY failure (caller falls back to 云盘).</summary>
    public async Task<byte[]?> TryGetBytesAsync(string relativePath, CancellationToken cancellationToken)
    {
        if (IsBypassed) return null;
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(
                _baseUrl + relativePath.TrimStart('/'), HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // 404 on a path the 云盘 has is a real inconsistency — log it once, then fall back.
                RecordFailure();
                AppLog.Warn($"更新镜像 {relativePath} 返回 HTTP {(int)response.StatusCode}，回退阿里云盘");
                return null;
            }
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            ResetFailures();
            AppLog.Info($"更新镜像响应正常：{relativePath}（{bytes.Length} B）");
            return bytes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            RecordFailure();
            return null;
        }
    }

    /// <summary>
    /// Streams a repository file to <paramref name="targetPath"/>; false on any failure.
    /// Used for the chunk downloads (the big ones) so they never buffer fully in memory.
    /// </summary>
    public async Task<bool> TryDownloadToFileAsync(
        string relativePath, string targetPath, CancellationToken cancellationToken)
    {
        if (IsBypassed) return false;
        string temp = targetPath + ".mirror";
        try
        {
            using HttpResponseMessage response = await _http.GetAsync(
                _baseUrl + relativePath.TrimStart('/'), HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                AppLog.Warn($"更新镜像 {relativePath} 返回 HTTP {(int)response.StatusCode}，回退阿里云盘");
                RecordFailure();
                return false;
            }
            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (FileStream target = File.Create(temp))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temp, targetPath, overwrite: true);
            ResetFailures();
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            try { File.Delete(temp); } catch { /* best effort */ }
            RecordFailure();
            return false;
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
