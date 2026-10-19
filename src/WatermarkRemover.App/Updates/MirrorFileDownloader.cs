using System.IO;
using System.Net;
using System.Net.Http;

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
    /// Each candidate writes from scratch (Create truncates), so a failed attempt never leaves a
    /// partial download that a later candidate would append to.
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                TryDelete(targetFile);
            }
        }
        throw new IOException($"下载更新包失败：{lastError?.Message ?? "未知错误"}", lastError);
    }

    private async Task DownloadToFileAsync(
        string url, string targetFile, Action<int>? progress,
        IDictionary<string, string>? headers, double timeout, CancellationToken cancelToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeout));

        using var response = await SendAsync(HttpMethod.Get, url, headers, timeout, timeoutCts.Token)
            .ConfigureAwait(false);

        long? totalBytes = response.Content.Headers.ContentLength;
        await using Stream source = await response.Content
            .ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
        await using var target = new FileStream(
            targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long received = 0;
        int lastReportedPercent = -1;
        int read;
        while ((read = await source.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), timeoutCts.Token).ConfigureAwait(false);
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
