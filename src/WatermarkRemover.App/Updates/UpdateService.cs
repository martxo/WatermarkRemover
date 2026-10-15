using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.App.Updates;

/// <summary>An available update on the release channel.</summary>
public sealed record UpdateInfo(Version Version, string DownloadUrl, string ReleasePageUrl, long SizeBytes);

/// <summary>
/// Talks to the GitHub Releases API: checks whether a newer version exists and downloads its
/// executable asset. No library dependencies beyond System.Net and System.Text.Json.
/// </summary>
public sealed class UpdateService
{
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
    /// Returns the newest available update, or <c>null</c> when the channel has no releases, no
    /// usable asset, or nothing newer than the running build.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        if (!UpdateConfig.IsConfigured) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        using var response = await Client.GetAsync(
            UpdateConfig.LatestReleaseApiUrl,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound) return null; // no releases yet
        response.EnsureSuccessStatusCode();

        await using Stream body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token).ConfigureAwait(false);
        JsonElement root = document.RootElement;

        var releaseVersion = UpdateVersion.ParseReleaseTag(
            root.TryGetProperty("tag_name", out JsonElement tag) ? tag.GetString() : null);
        if (releaseVersion == null || !UpdateVersion.IsNewer(CurrentVersion, releaseVersion))
            return null;

        string? downloadUrl = null;
        long sizeBytes = 0;
        if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out JsonElement n) ? n.GetString() : null;
                if (!string.Equals(name, UpdateConfig.AssetName, StringComparison.OrdinalIgnoreCase)) continue;

                downloadUrl = asset.TryGetProperty("browser_download_url", out JsonElement u) ? u.GetString() : null;
                sizeBytes = asset.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long l) ? l : 0;
                break;
            }
        }

        if (string.IsNullOrEmpty(downloadUrl)) return null;

        string pageUrl = root.TryGetProperty("html_url", out JsonElement html) ? html.GetString() ?? string.Empty : string.Empty;
        return new UpdateInfo(releaseVersion, downloadUrl, pageUrl, sizeBytes);
    }

    /// <summary>Downloads the update executable to a temp file, reporting progress in percent.</summary>
    public static async Task<string> DownloadUpdateAsync(
        UpdateInfo info,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        string targetPath = Path.Combine(Path.GetTempPath(), $"WatermarkRemover-{info.Version}.exe");

        using var response = await Client.GetAsync(
            info.DownloadUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long totalBytes = response.Content.Headers.ContentLength ?? info.SizeBytes;
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            downloaded += read;
            if (totalBytes > 0)
                progress.Report(Math.Min(100.0, downloaded * 100.0 / totalBytes));
        }

        return targetPath;
    }
}
