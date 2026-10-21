using Velopack;
using Velopack.Sources;
using WatermarkRemover.Core.Updates;
// The legacy channel's UpdateInfo record lives in this same namespace; alias the Velopack type
// so every UpdateInfo below unambiguously refers to Velopack's.
using VelopackUpdateInfo = Velopack.UpdateInfo;

namespace WatermarkRemover.App.Updates;

/// <summary>What one Velopack channel query found, shaped for the shared coordinator UI.</summary>
public sealed record VelopackCheckResult(
    bool UpdateAvailable,
    VelopackUpdateInfo? Update,
    Version LatestVersion,
    bool IsDelta,
    long PackageBytes);

/// <summary>
/// The Velopack channel (增量推送, v1.15.0+): checks the GitHub feed for a newer full release,
/// downloads the delta package when one exists (Velopack falls back to full automatically if the
/// delta cannot be applied) and hands over to Update.exe — checksum-verified and atomic. Only
/// usable when the running app was installed by Velopack's Setup.exe; green-directory users stay
/// on the legacy <see cref="UpdateService"/> full-zip channel.
/// </summary>
public sealed class VelopackUpdateService
{
    private static UpdateManager CreateManager() =>
        new(new GithubSource(UpdateConfig.VelopackRepoUrl, accessToken: null, prerelease: false, new MirrorFileDownloader()));

    /// <summary>
    /// True when the running app is Velopack-installed (Update.exe beside the executable), i.e.
    /// the incremental channel is usable. Never throws: detection failures (unpacked dev runs,
    /// test hosts) simply mean "not installed". <see cref="UpdateManager.CheckForUpdatesAsync"/>
    /// would throw for that case, hence this guard exists.
    /// </summary>
    public static bool IsChannelUsable
    {
        get
        {
            try
            {
                return CreateManager().IsInstalled;
            }
            catch
            {
                return false;
            }
        }
    }

    private readonly UpdateManager _manager;

    public VelopackUpdateService() : this(CreateManager())
    {
    }

    /// <summary>Test seam: inject a manager built against a fake source.</summary>
    internal VelopackUpdateService(UpdateManager manager) => _manager = manager;

    /// <summary>
    /// True when this Velopack install carries no local full package — the Portable-zip shape.
    /// Update.exe exists so the Velopack channel runs, but the delta strategy needs the previous
    /// full .nupkg as its diff base ("Windows (installer) is the only thing which ships with a
    /// complete .nupkg", UpdateManager.CreateDeltaUpdateStrategy) — without it EVERY update is a
    /// full download, forever. Setup.exe installs keep the package; a one-time Setup install
    /// turns on deltas from then on.
    /// </summary>
    public static bool IsPortableShapedInstall
    {
        get
        {
            try
            {
                string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
                if (exeDir is null) return false;
                // Velopack layout: <root>\current\App.exe + <root>\packages\*.nupkg. Anything
                // else (green directory, dev publish) doesn't have the packages concept at all.
                if (!string.Equals(Path.GetFileName(exeDir), "current", StringComparison.OrdinalIgnoreCase))
                    return false;
                string? root = Path.GetDirectoryName(exeDir);
                string packages = root is null ? string.Empty : Path.Combine(root, "packages");
                return !Directory.Exists(packages) || !Directory.EnumerateFiles(packages, "*-full.nupkg").Any();
            }
            catch
            {
                return false; // can't tell → don't nag the user with the wrong hint
            }
        }
    }

    /// <summary>
    /// Queries the Velopack feed once. Network failures propagate to the caller — the coordinator
    /// renders them with the same dialog as a legacy check failure.
    /// </summary>
    public async Task<VelopackCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        VelopackUpdateInfo? update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (update is null)
            return new VelopackCheckResult(false, null, UpdateService.CurrentVersion, false, 0);

        bool isDelta = update.BaseRelease is not null && update.DeltasToTarget.Length > 0;
        long packageBytes = isDelta
            ? update.DeltasToTarget.Sum(asset => asset.Size)
            : update.TargetFullRelease.Size;

        Version latest = ToVersion(update.TargetFullRelease.Version);
        return new VelopackCheckResult(true, update, latest, isDelta, packageBytes);
    }

    /// <summary>
    /// Downloads the pending update (delta preferred; Velopack retries with the full package if
    /// the delta turns out unusable), mapping percent-only progress onto the shared record.
    /// </summary>
    public async Task<VelopackUpdateInfo> DownloadAsync(
        VelopackUpdateInfo update,
        IProgress<UpdateProgress> progress,
        CancellationToken cancellationToken)
    {
        bool isDelta = update.BaseRelease is not null && update.DeltasToTarget.Length > 0;
        long packageBytes = isDelta
            ? update.DeltasToTarget.Sum(asset => asset.Size)
            : update.TargetFullRelease.Size;
        string source = isDelta ? "增量更新" : "全量更新";

        await _manager.DownloadUpdatesAsync(
            update,
            p => progress.Report(VelopackProgressMapper.Map(p, isDelta, packageBytes, source)),
            cancellationToken).ConfigureAwait(false);
        return update;
    }

    /// <summary>
    /// Hands over to Velopack's Update.exe: it waits for this process to exit, applies the
    /// checksum-verified package and relaunches. The current process terminates inside the call —
    /// no code after it runs, so close progress windows before calling this.
    /// </summary>
    public void ApplyAndRestart(VelopackUpdateInfo update) =>
        _manager.ApplyUpdatesAndRestart(update.TargetFullRelease);

    private static Version ToVersion(SemanticVersion semantic) =>
        new(semantic.Major, semantic.Minor, semantic.Patch);
}
