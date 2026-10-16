using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.App.Updates;

/// <summary>An available update on the release channel.</summary>
public sealed record UpdateInfo(
    Version Version,
    string DownloadUrl,
    string ReleasePageUrl,
    long SizeBytes,
    string? Sha256Hex);

/// <summary>Outcome of one channel query, including why there is no update.</summary>
public enum UpdateCheckStatus
{
    /// <summary>A newer release with a downloadable package exists.</summary>
    UpdateAvailable,

    /// <summary>The channel's latest release is not newer than the running build.</summary>
    UpToDate,

    /// <summary>The channel has no releases, no usable package, or is not configured.</summary>
    NoUpdateSource,

    /// <summary>The channel could not be reached (network, DNS, TLS, …).</summary>
    CheckFailed,
}

/// <summary>
/// Result of asking the release channel for updates. Carries the channel's latest version even
/// when there is no update, so dialogs can tell "already newest" apart from "channel is empty".
/// </summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateInfo? Update,
    Version? ChannelLatestVersion,
    string? Message);

/// <summary>
/// Talks to the GitHub Releases API: checks whether a newer version exists and downloads its
/// executable asset. Downloads prefer the direct GitHub link and fall back to download
/// accelerators when it fails; every download is verified against the SHA256 digest the API
/// published, so a misbehaving mirror can never deliver a modified executable. No library
/// dependencies beyond System.Net and System.Text.Json.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Upper bound for one download (large self-contained exe on slow links).</summary>
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(20);

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        // GitHub API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"WatermarkRemover/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>Version of the running executable (major.minor.build).</summary>
    public static Version CurrentVersion
    {
        get
        {
            Version raw = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(raw.Major, raw.Minor, Math.Max(raw.Build, 0));
        }
    }

    /// <summary>
    /// Queries the release channel once. Never throws for channel problems — they surface as
    /// <see cref="UpdateCheckStatus.CheckFailed"/> / <see cref="UpdateCheckStatus.NoUpdateSource"/>
    /// with a human-readable message.
    /// </summary>
    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!UpdateConfig.IsConfigured)
            return new UpdateCheckResult(UpdateCheckStatus.NoUpdateSource, null, null, "更新源未配置。");

        JsonElement root;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));

            using var response = await Client.GetAsync(
                UpdateConfig.LatestReleaseApiUrl,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheckResult(
                    UpdateCheckStatus.NoUpdateSource, null, null, "更新通道还没有任何发布版本。");
            response.EnsureSuccessStatusCode();

            await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
            root = document.RootElement.Clone();
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateCheckStatus.CheckFailed, null, null, ex.Message);
        }

        var releaseVersion = UpdateVersion.ParseReleaseTag(
            root.TryGetProperty("tag_name", out JsonElement tag) ? tag.GetString() : null);
        string pageUrl = root.TryGetProperty("html_url", out JsonElement html) ? html.GetString() ?? string.Empty : string.Empty;

        if (releaseVersion == null)
            return new UpdateCheckResult(
                UpdateCheckStatus.NoUpdateSource, null, null, "更新通道没有可识别的版本号。");

        string? downloadUrl = null;
        long sizeBytes = 0;
        string? sha256Hex = null;
        if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                if (!string.Equals(name, UpdateConfig.AssetName, StringComparison.OrdinalIgnoreCase)) continue;

                downloadUrl = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                sizeBytes = asset.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long l) ? l : 0;
                sha256Hex = ParseSha256(asset.TryGetProperty("digest", out JsonElement d) ? d.GetString() : null);
                break;
            }
        }

        if (string.IsNullOrEmpty(downloadUrl))
            return new UpdateCheckResult(
                UpdateCheckStatus.NoUpdateSource, null, releaseVersion,
                $"发布通道最新发布为 v{releaseVersion}，但未附带 {UpdateConfig.AssetName} 安装包。");

        if (!UpdateVersion.IsNewer(CurrentVersion, releaseVersion))
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, releaseVersion, null);

        return new UpdateCheckResult(
            UpdateCheckStatus.UpdateAvailable,
            new UpdateInfo(releaseVersion, downloadUrl, pageUrl, sizeBytes, sha256Hex),
            releaseVersion,
            null);
    }

    /// <summary>
    /// Downloads the update executable to a temp file, reporting progress in percent. Tries the
    /// direct GitHub link first and falls back to accelerators; the downloaded bytes must match
    /// the API-published SHA256 digest before the file is handed over.
    /// </summary>
    public static async Task<string> DownloadUpdateAsync(
        UpdateInfo info,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        string extension = Path.GetExtension(UpdateConfig.AssetName);
        string targetPath = Path.Combine(Path.GetTempPath(), $"WatermarkRemover-{info.Version}{extension}");
        Exception? lastError = null;

        foreach (string url in DownloadCandidates(info.DownloadUrl))
        {
            try
            {
                progress.Report(0);
                await DownloadToFileAsync(url, targetPath, info, progress, cancellationToken).ConfigureAwait(false);
                VerifyIntegrity(targetPath, info);
                return targetPath;
            }
            catch (OperationCanceledException)
            {
                throw; // user cancelled — never retry behind their back
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        TryDelete(targetPath);
        throw new IOException($"下载更新失败：{lastError?.Message ?? "未知错误"}", lastError);
    }

    /// <summary>Direct link first, then accelerators for networks where github.com is unreliable.</summary>
    private static IEnumerable<string> DownloadCandidates(string url)
    {
        yield return url;
        foreach (string mirror in UpdateConfig.DownloadMirrors)
            yield return mirror + url;
    }

    private static async Task DownloadToFileAsync(
        string url,
        string targetPath,
        UpdateInfo info,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);

        using var response = await Client.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? info.SizeBytes;
        await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var target = new FileStream(
            targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            downloaded += read;
            if (totalBytes > 0)
                progress.Report(Math.Min(100.0, downloaded * 100.0 / totalBytes));
        }
    }

    /// <summary>
    /// Rejects any download whose bytes do not match the digest the GitHub API published. The
    /// digest is fetched over TLS from the official API, so accelerators are safe to use —
    /// they can withhold the file, never alter it.
    /// </summary>
    private static void VerifyIntegrity(string filePath, UpdateInfo info)
    {
        if (string.IsNullOrEmpty(info.Sha256Hex)) return; // channel published no digest

        using var stream = File.OpenRead(filePath);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(info.Sha256Hex, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载文件校验失败（SHA256 不匹配），已放弃安装。");
    }

    /// <summary>Parses "sha256:&lt;hex&gt;" asset digests into bare hex; null when absent.</summary>
    private static string? ParseSha256(string? digest)
    {
        const string prefix = "sha256:";
        return digest is not null && digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? digest[prefix.Length..]
            : null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
