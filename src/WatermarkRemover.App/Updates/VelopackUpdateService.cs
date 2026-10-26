using System.IO;
using Velopack;
using Velopack.Locators;
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
    /// True when Velopack itself considers this a portable (unzipped) install — the
    /// <c>.portable</c> marker file beside <c>Update.exe</c>, which only the Portable zip
    /// contains. This is Velopack's own verdict (<see cref="UpdateManager.IsPortable"/>), not a
    /// guess from folder shape: a Setup.exe install never carries the marker, even when its
    /// package cache has been emptied by a cleaner, so the hint can no longer call an installed
    /// app "便携版" by mistake.
    /// </summary>
    public static bool IsPortableInstall
    {
        get
        {
            try
            {
                return CreateManager().IsPortable;
            }
            catch
            {
                return false; // not installed / startup hooks not run → no portable claim
            }
        }
    }

    /// <summary>
    /// The package cache Velopack actually uses. Usually <c>&lt;root&gt;\packages</c>, but Velopack
    /// redirects it to <c>%LOCALAPPDATA%\&lt;AppId&gt;\packages</c> when the install root is not
    /// writable — guessing the path from the exe location would then scan the wrong folder.
    /// </summary>
    public static string? PackagesDirectory
    {
        get
        {
            try
            {
                return VelopackLocator.Current.PackagesDir;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// True when the cache holds a full nupkg — the diff base every delta needs
    /// (<see cref="UpdateManager.CreateDeltaUpdateStrategy"/> disables deltas without it).
    /// Distinct from <see cref="IsPortableInstall"/>: an installed user can lose this file to a
    /// cleaner while still being a Setup install, and a single full download restores it.
    /// </summary>
    public static bool HasLocalBasePackage
    {
        get
        {
            try
            {
                string? dir = PackagesDirectory;
                return dir is not null && Directory.Exists(dir) &&
                       Directory.EnumerateFiles(dir, "*-full.nupkg").Any();
            }
            catch
            {
                return false;
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
    /// The newest downloaded package that still needs Update.exe to be applied. Non-null after a
    /// background download in an earlier session that the user closed instead of restarting
    /// into: Velopack never applies a staged package on its own, so without this the old version
    /// kept running forever no matter how often the app was restarted.
    /// </summary>
    public static VelopackAsset? PendingRestart
    {
        get
        {
            try
            {
                return CreateManager().UpdatePendingRestart;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Where Velopack believes this install lives; null for green/unzipped copies.</summary>
    public static string? InstallRoot
    {
        get
        {
            try
            {
                return VelopackLocator.Current.RootAppDir;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>The version recorded in the install manifest (<c>current\sq.version</c>), which
    /// is what Velopack compares against the feed. Null when the app is not installed.</summary>
    public static Version? InstalledManifestVersion
    {
        get
        {
            try
            {
                SemanticVersion? version = CreateManager().CurrentVersion;
                return version is null
                    ? null
                    : new Version(version.Major, version.Minor, version.Patch);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// One-line install identity for logs and failure dialogs: where this process actually runs,
    /// where Velopack believes the install root is, and what each side thinks the version is.
    /// A machine stuck on an old version needs exactly these facts — a stale shortcut or a
    /// second install is otherwise indistinguishable from a failed update.
    /// </summary>
    public static string DescribeInstall()
    {
        try
        {
            IVelopackLocator locator = VelopackLocator.Current;
            UpdateManager manager = CreateManager();
            return $"运行位置={Environment.ProcessPath}；安装根目录={locator.RootAppDir}；" +
                   $"应用目录={locator.AppContentDir}；Update.exe={locator.UpdateExePath}；" +
                   $"包缓存={locator.PackagesDir}；清单版本={manager.CurrentVersion}；" +
                   $"已安装={manager.IsInstalled}；便携版={manager.IsPortable}；" +
                   $"程序集版本={UpdateService.CurrentVersion}";
        }
        catch (Exception ex)
        {
            return $"运行位置={Environment.ProcessPath}；读取安装信息失败：{ex.Message}";
        }
    }

    /// <summary>Applies a package that was downloaded earlier, possibly by a previous process.</summary>
    public void ApplyPendingAndRestart(VelopackAsset pending) =>
        _manager.ApplyUpdatesAndRestart(pending);

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
