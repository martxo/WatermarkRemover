using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace WatermarkRemover.App.Updates;

/// <summary>
/// Candidate URLs for one Velopack download, in try order. Extracted as a pure function so the
/// mirror policy is unit-testable without any HTTP traffic: GitHub release downloads try the
/// direct link first and fall back to the accelerator prefixes (the same list as the legacy
/// channel), while API calls are never mirrored — the accelerators only proxy file downloads.
/// </summary>
public static class MirrorCandidates
{
    /// <summary>
    /// A GitHub file download ("https://github.com/owner/repo/releases/download/…"). API hosts
    /// such as api.github.com must go direct: mirrors cannot proxy them.
    /// </summary>
    private const string GitHubDownloadPrefix = "https://github.com/";

    private const string ReleasesDownloadSegment = "/releases/download/";

    /// <summary>Direct link first, then each configured mirror prefix, in order.</summary>
    public static IReadOnlyList<string> For(string url)
    {
        bool isFileDownload = url.StartsWith(GitHubDownloadPrefix, StringComparison.OrdinalIgnoreCase)
            && url.Contains(ReleasesDownloadSegment, StringComparison.OrdinalIgnoreCase);
        if (!isFileDownload)
            return new[] { url };

        var candidates = new List<string>(1 + UpdateConfig.DownloadMirrors.Length) { url };
        candidates.AddRange(UpdateConfig.DownloadMirrors.Select(mirror => mirror + url));
        return candidates;
    }
}

/// <summary>
/// Velopack's HTTP seam (<see cref="Velopack.Sources.IFileDownloader"/>) wired to the same
/// mirror-retry policy as the legacy channel: GitHub release downloads fall back to accelerator
/// prefixes when the direct link fails; everything else goes direct. Package integrity is
/// guaranteed by Velopack's built-in SHA256 verification, so a misbehaving mirror can withhold a
/// file but never alter it.
/// </summary>
public sealed class MirrorFileDownloader : Velopack.Sources.IFileDownloader
{
    private static readonly HttpClient Client = CreateClient();

    private readonly HttpClient _client;

    /// <summary>Test seam: inject an HttpClient backed by a fake handler.</summary>
    public MirrorFileDownloader(HttpClient? client = null) => _client = client ?? Client;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        // GitHub rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"WatermarkRemover/{UpdateService.CurrentVersion}");
        return client;
    }

    /// <summary>Download manifest/feed content as text (releases.win.json).</summary>
    public async Task<string> DownloadString(
        string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        Exception? lastError = null;
        foreach (string candidate in MirrorCandidates.For(url))
        {
            try
            {
                using var response = await SendAsync(HttpMethod.Get, candidate, headers, timeout, default)
                    .ConfigureAwait(false);
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }
        }
        throw new IOException($"下载更新清单失败：{lastError?.Message ?? "未知错误"}", lastError);
    }

    /// <summary>Download manifest/feed content as raw bytes.</summary>
    public async Task<byte[]> DownloadBytes(
        string url, IDictionary<string, string>? headers = null, double timeout = 30)
    {
        Exception? lastError = null;
        foreach (string candidate in MirrorCandidates.For(url))
        {
            try
            {
                using var response = await SendAsync(HttpMethod.Get, candidate, headers, timeout, default)
                    .ConfigureAwait(false);
                return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }
        }
        throw new IOException($"下载更新清单失败：{lastError?.Message ?? "未知错误"}", lastError);
    }

    /// <summary>
    /// Download an update package to a file, reporting integer percent when the size is known.
    /// <para><paramref name="timeout"/> is Velopack's contract — the maximum time in <b>minutes</b>
    /// for the whole download. Reading it as seconds (the old code) cancelled every package that
    /// took longer than 30 s: a small delta survived, a 70 MB full package never did.</para>
    /// <para>Bytes land in <c>targetFile + ".part"</c> and a leftover partial is resumed with an
    /// HTTP Range request, so a dropped connection or a mirror switch costs only the bytes in
    /// flight instead of the whole package.</para>
    /// </summary>
    public async Task DownloadFile(
        string url, string targetFile, Action<int>? progress,
        IDictionary<string, string>? headers = null, double timeout = 30,
        CancellationToken cancelToken = default)
    {
        Exception? lastError = null;
        foreach (string candidate in MirrorCandidates.For(url))
        {
            try
            {
                await DownloadToFileAsync(candidate, targetFile, progress, headers, timeout, cancelToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancelToken.IsCancellationRequested)
            {
                // User cancelled: the partial file stays so the next attempt resumes it.
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                // Keep the partial file — the next candidate (or the next run) resumes it.
            }
        }
        throw new IOException($"下载更新包失败：{lastError?.Message ?? "未知错误"}", lastError);
    }

    /// <summary>Suffix of the resumable download file kept beside the package target.</summary>
    private const string PartialSuffix = ".part";

    /// <summary>No new bytes for this long means the link is dead, not just slow.</summary>
    private const int StallTimeoutSeconds = 60;

    private async Task DownloadToFileAsync(
        string url, string targetFile, Action<int>? progress,
        IDictionary<string, string>? headers, double timeoutMinutes, CancellationToken cancelToken)
    {
        string partialPath = targetFile + PartialSuffix;

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
        // Velopack documents this parameter in minutes (IFileDownloader.DownloadFile).
        overall.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, timeoutMinutes)));

        for (var attempt = 0; ; attempt++)
        {
            long existing = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (headers is not null)
            {
                foreach ((string name, string value) in headers)
                    request.Headers.TryAddWithoutValidation(name, value);
            }
            if (existing > 0)
                request.Headers.Range = new RangeHeaderValue(existing, null);

            using HttpResponseMessage response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, overall.Token)
                .ConfigureAwait(false);

            // A partial that is already longer than the remote object (stale/corrupt): drop it and
            // start over once instead of failing the whole candidate.
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && attempt == 0)
            {
                TryDelete(partialPath);
                continue;
            }

            response.EnsureSuccessStatusCode();

            bool resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            long? contentLength = response.Content.Headers.ContentLength;
            long? totalBytes = contentLength is null
                ? null
                : resumed ? existing + contentLength.Value : contentLength.Value;
            long received = resumed ? existing : 0;
            int lastReportedPercent = -1;

            await using (Stream source = await response.Content
                             .ReadAsStreamAsync(overall.Token).ConfigureAwait(false))
            await using (var target = new FileStream(
                             partialPath,
                             resumed ? FileMode.Append : FileMode.Create,
                             FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await ReadWithStallGuardAsync(source, buffer, overall.Token)
                           .ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), overall.Token).ConfigureAwait(false);
                    received += read;

                    // Velopack already dedupes and rounds its progress callbacks; still, only report
                    // when the integer percent moves to keep the dispatcher quiet on fast links.
                    if (progress is not null && totalBytes is > 0)
                    {
                        int percent = (int)Math.Min(100, received * 100 / totalBytes.Value);
                        if (percent != lastReportedPercent)
                        {
                            lastReportedPercent = percent;
                            progress(percent);
                        }
                    }
                }
            }

            // A truncated body would otherwise reach Velopack as a "complete" package and fail
            // there with a hash error; fail here so the partial is kept for a resume.
            if (totalBytes is { } expected && received < expected)
            {
                throw new IOException($"下载不完整：已收到 {received}/{expected} 字节。");
            }

            File.Move(partialPath, targetFile, overwrite: true);
            return;
        }
    }

    /// <summary>
    /// Reads once, failing only when the stream goes quiet — a total deadline is what used to
    /// kill multi-minute downloads, so the guard is a stall (no bytes) timeout instead.
    /// </summary>
    private static async Task<int> ReadWithStallGuardAsync(
        Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(TimeSpan.FromSeconds(StallTimeoutSeconds));
        try
        {
            return await source.ReadAsync(buffer.AsMemory(), stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"下载停滞：{StallTimeoutSeconds} 秒内没有收到新数据。");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, IDictionary<string, string>? headers,
        double timeoutSeconds, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var request = new HttpRequestMessage(method, url);
        if (headers is not null)
        {
            foreach ((string name, string value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);
        }

        HttpResponseMessage response = await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
