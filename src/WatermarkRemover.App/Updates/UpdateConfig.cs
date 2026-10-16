namespace WatermarkRemover.App.Updates;

/// <summary>
/// Release channel configuration. Publishing an update: build the Release single-file exe,
/// create a GitHub Release whose tag is the version (e.g. <c>v1.1.0</c>) and upload the exe as
/// an asset named <see cref="AssetName"/>. Clients pick it up automatically.
/// </summary>
public static class UpdateConfig
{
    /// <summary>GitHub repository hosting the releases, as "owner/repo".</summary>
    public const string Repository = "martxo/WatermarkRemover";

    /// <summary>
    /// Name of the release asset that contains the app. Since v1.7.0 the app ships as a zip
    /// (executable + Models\ OCR models), so an update can refresh both. The bare
    /// <c>WatermarkRemover.exe</c> is still published alongside it: releases keep both assets so
    /// pre-1.7 clients — which only look for the exe — can still upgrade onto the new channel.
    /// </summary>
    public const string AssetName = "WatermarkRemover.zip";

    /// <summary>
    /// Fallback download accelerators for networks where github.com is unreliable. Each entry
    /// is a prefix prepended to the direct download URL. The GitHub API digest still guards
    /// integrity — a mirror can withhold the file, never alter it.
    /// </summary>
    public static readonly string[] DownloadMirrors =
    {
        "https://ghfast.top/",
        "https://ghproxy.net/",
    };

    /// <summary>False until <see cref="Repository"/> is filled in with a real repo.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Repository)
        && Repository != "YOUR-GITHUB-USERNAME/WatermarkRemover"
        && Repository.Contains('/', StringComparison.Ordinal);

    public static string LatestReleaseApiUrl => $"https://api.github.com/repos/{Repository}/releases/latest";
}
