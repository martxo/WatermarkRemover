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
    string? Sha256Hex,
    bool IsSetup = false);

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
        string? setupUrl = null;
        long setupSize = 0;
        string? setupSha256 = null;
        if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                if (string.Equals(name, UpdateConfig.AssetName, StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                    sizeBytes = asset.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long l) ? l : 0;
                    sha256Hex = ParseSha256(asset.TryGetProperty("digest", out JsonElement d) ? d.GetString() : null);
                    continue;
                }
                if (string.Equals(name, UpdateConfig.SetupAssetName, StringComparison.OrdinalIgnoreCase))
                {
                    setupUrl = asset.TryGetProperty("browser_download_url", out JsonElement su) ? su.GetString() : null;
                    setupSize = asset.TryGetProperty("size", out JsonElement ss) && ss.TryGetInt64(out long sl) ? sl : 0;
                    setupSha256 = ParseSha256(asset.TryGetProperty("digest", out JsonElement sd) ? sd.GetString() : null);
                }
            }
        }

        // Releases no longer carry the legacy zip, so a green copy finds nothing to self-replace.
        // The Velopack Setup on the same release is its way forward: installing it puts the app
        // in the standard location, which has the incremental feed from then on.
        bool isSetup = false;
        if (string.IsNullOrEmpty(downloadUrl) && !string.IsNullOrEmpty(setupUrl))
        {
            downloadUrl = setupUrl;
            sizeBytes = setupSize;
            sha256Hex = setupSha256;
            isSetup = true;
        }

        if (string.IsNullOrEmpty(downloadUrl))
            return new UpdateCheckResult(
                UpdateCheckStatus.NoUpdateSource, null, releaseVersion,
                $"发布通道最新发布为 v{releaseVersion}，但未附带可安装的更新包" +
                $"（{UpdateConfig.AssetName} 或 {UpdateConfig.SetupAssetName}）。");

        if (!UpdateVersion.IsNewer(CurrentVersion, releaseVersion))
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, releaseVersion, null);

        return new UpdateCheckResult(
            UpdateCheckStatus.UpdateAvailable,
            new UpdateInfo(releaseVersion, downloadUrl, pageUrl, sizeBytes, sha256Hex, isSetup),
            releaseVersion,
            null);
    }

    /// <summary>
    /// Downloads the update package to a temp file, reporting phased progress: which channel is
    /// being used, bytes received and transfer speed. Tries the direct GitHub link first and falls
    /// back to accelerators; the downloaded bytes must match the API-published SHA256 digest
    /// before the file is handed over.
    /// </summary>
    public static async Task<string> DownloadUpdateAsync(
        UpdateInfo info,
        IProgress<UpdateProgress> progress,
        CancellationToken cancellationToken)
    {
        // Zip for the legacy channel, exe for a Velopack Setup fallback — the extension decides
        // how the applier opens the file, so it must follow the actual asset.
        string extension = info.IsSetup ? ".exe" : Path.GetExtension(UpdateConfig.AssetName);
        string targetPath = Path.Combine(Path.GetTempPath(), $"WatermarkRemover-{info.Version}{extension}");
        Exception? lastError = null;

        foreach ((string url, string sourceName) in DownloadCandidates(info.DownloadUrl))
        {
            try
            {
                progress.Report(new UpdateProgress(UpdateStage.Downloading, 0, 0, info.SizeBytes, null, sourceName));
                await DownloadToFileAsync(url, targetPath, info, progress, sourceName, cancellationToken).ConfigureAwait(false);

                // Byte-count unknown in advance can leave the last tick short of 100%; close the
                // gap before the verification stage so the bar reads complete.
                progress.Report(new UpdateProgress(
                    UpdateStage.Downloading, 100, info.SizeBytes > 0 ? info.SizeBytes : new FileInfo(targetPath).Length,
                    info.SizeBytes > 0 ? info.SizeBytes : null, null, sourceName));

                progress.Report(new UpdateProgress(UpdateStage.Verifying, 100, 0, null, null, null));
                VerifyIntegrity(targetPath, info);
                progress.Report(new UpdateProgress(UpdateStage.ReadyToRestart, 100, 0, null, null, null));
                return targetPath;
            }
            catch (OperationCanceledException)
            {
                // User cancelled — never retry behind their back, and never leave the partial
                // download behind either.
                TryDelete(targetPath);
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                // The UI must not look stuck: say why the next channel is being tried.
                progress.Report(new UpdateProgress(UpdateStage.Downloading, 0, 0, info.SizeBytes, null,
                    $"{sourceName}失败，正在切换备用通道…"));
            }
        }

        TryDelete(targetPath);
        throw new IOException($"下载更新失败：{lastError?.Message ?? "未知错误"}", lastError);
    }

    /// <summary>
    /// Direct link first, then accelerators for networks where github.com is unreliable. Each
    /// candidate carries the name the progress UI shows.
    /// </summary>
    private static IEnumerable<(string Url, string SourceName)> DownloadCandidates(string url)
    {
        yield return (url, "官方下载");
        foreach (string mirror in UpdateConfig.DownloadMirrors)
            yield return (mirror + url, $"备用通道 {new Uri(mirror).Host}");
    }

    private static async Task DownloadToFileAsync(
        string url,
        string targetPath,
        UpdateInfo info,
        IProgress<UpdateProgress> progress,
        string sourceName,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DownloadTimeout);

        using var response = await Client.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long? totalBytes = response.Content.Headers.ContentLength ?? (info.SizeBytes > 0 ? info.SizeBytes : null);
        await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        await using var target = new FileStream(
            targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long lastReportedBytes = 0;
        var lastReported = TimeSpan.Zero;
        double speedBytesPerSecond = 0;
        const double SpeedSmoothing = 0.2; // EMA weight for the transfer-speed estimate

        while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            downloaded += read;

            // Throttle the reports: an 80 KB chunk per tick would flood the dispatcher and make
            // the UI less responsive, not more.
            TimeSpan elapsed = stopwatch.Elapsed;
            if (elapsed - lastReported < TimeSpan.FromMilliseconds(200) && lastReported != TimeSpan.Zero)
                continue;

            double instantSpeed = downloaded > lastReportedBytes && elapsed > TimeSpan.Zero
                ? (downloaded - lastReportedBytes) / (elapsed - lastReported).TotalSeconds
                : speedBytesPerSecond;
            speedBytesPerSecond = speedBytesPerSecond <= 0
                ? instantSpeed
                : speedBytesPerSecond + SpeedSmoothing * (instantSpeed - speedBytesPerSecond);
            lastReportedBytes = downloaded;
            lastReported = elapsed;

            double? percent = totalBytes is > 0 ? Math.Min(100.0, downloaded * 100.0 / totalBytes.Value) : null;
            progress.Report(new UpdateProgress(
                UpdateStage.Downloading, percent, downloaded, totalBytes,
                speedBytesPerSecond > 0 ? speedBytesPerSecond : null, sourceName));
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
