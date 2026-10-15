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

    /// <summary>Name of the release asset that contains the app executable.</summary>
    public const string AssetName = "WatermarkRemover.exe";

    /// <summary>False until <see cref="Repository"/> is filled in with a real repo.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Repository)
        && Repository != "YOUR-GITHUB-USERNAME/WatermarkRemover"
        && Repository.Contains('/', StringComparison.Ordinal);

    public static string LatestReleaseApiUrl => $"https://api.github.com/repos/{Repository}/releases/latest";
}
