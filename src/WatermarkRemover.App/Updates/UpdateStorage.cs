using System.IO;
using Velopack.Locators;
using WatermarkRemover.Core.Updates;

namespace WatermarkRemover.App.Updates;

/// <summary>One scanned location: where update packages land and what leftovers sit there.</summary>
public sealed record UpdateResidueLocation(
    string Title,
    string Path,
    bool Exists,
    int ResidueCount,
    long ResidueBytes,
    string Summary);

/// <summary>
/// Where the two update channels park their packages, and what of that is safe to clean.
///
/// The Velopack channel keeps full/delta nupkgs in its package cache (usually
/// <c>&lt;root&gt;\packages</c>, or <c>%LOCALAPPDATA%\&lt;AppId&gt;\packages</c> when the install
/// root is not writable — always ask <see cref="GetVelopackPackagesDirectory"/> rather than
/// guessing). The latest full
/// package is the diff base every future delta needs (UpdateManager.CreateDeltaUpdateStrategy:
/// without it deltas are disabled and every update becomes a full download), so cleanup keeps
/// the CURRENT and the LATEST full and treats everything else — old versions, deltas already
/// applied, partial downloads — as residue. The legacy green-directory channel streams into
/// %TEMP%: interrupted zips, staging dirs and update scripts are pure residue.
///
/// Cleanup is deliberately conservative: files in use or locked are skipped, not deleted.
/// </summary>
public static class UpdateStorage
{
    private const string PackageId = UpdateConfig.VelopackPackId;

    /// <summary>Velopack's package cache. Asks Velopack first: it redirects the cache to
    /// <c>%LOCALAPPDATA%\&lt;AppId&gt;\packages</c> when the install root is not writable, so
    /// <c>&lt;install-root&gt;\packages</c> is only the common case. Null for green directories
    /// (they have no package cache).</summary>
    public static string? GetVelopackPackagesDirectory()
    {
        try
        {
            string? authoritative = VelopackLocator.Current.PackagesDir;
            if (!string.IsNullOrEmpty(authoritative)) return authoritative;
        }
        catch (Exception)
        {
            // startup hooks not run (tests, dev tools) → fall through to the path heuristic
        }

        try
        {
            string? exeDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (exeDir is null ||
                !string.Equals(Path.GetFileName(exeDir), "current", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string? root = Path.GetDirectoryName(exeDir);
            return root is null ? null : Path.Combine(root, "packages");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The legacy channel's landing spot: full zips, staging dirs and update scripts in %TEMP%.</summary>
    public static string GetLegacyDownloadDirectory() => Path.GetTempPath();

    /// <summary>Scans both channels. Read-only; safe to call from any thread.</summary>
    public static IReadOnlyList<UpdateResidueLocation> Describe(
        string? velopackPackagesDir = null, string? legacyTempDir = null)
    {
        var locations = new List<UpdateResidueLocation>();
        string? packages = velopackPackagesDir ?? GetVelopackPackagesDirectory();
        if (packages is not null)
            locations.Add(DescribeVelopack(packages));

        locations.Add(DescribeLegacy(legacyTempDir ?? GetLegacyDownloadDirectory()));
        return locations;
    }

    /// <summary>Deletes residue from both channels. Files in use are skipped. Returns what was freed.</summary>
    public static (int Deleted, long FreedBytes) Clean(
        string? velopackPackagesDir = null, string? legacyTempDir = null)
    {
        int deleted = 0;
        long freed = 0;

        string? packages = velopackPackagesDir ?? GetVelopackPackagesDirectory();
        if (packages is not null)
            foreach (string path in CollectVelopackResidue(packages))
                if (TryFree(path, ref deleted, ref freed)) { }

        foreach (string path in CollectLegacyResidue(legacyTempDir ?? GetLegacyDownloadDirectory()))
            TryFree(path, ref deleted, ref freed);

        return (deleted, freed);
    }

    /// <summary>Sizes the path BEFORE deleting (size is unrecoverable afterwards); skips locked files.</summary>
    private static bool TryFree(string path, ref int deleted, ref long freed)
    {
        long size = SizeOf(path);
        if (!DeletePath(path)) return false;
        deleted++;
        freed += size;
        return true;
    }

    // ── Velopack packages cache ────────────────────────────────────────────────────────────────

    private static UpdateResidueLocation DescribeVelopack(string dir) =>
        Build("安装包缓存", dir, CollectVelopackResidue(dir));

    private static IReadOnlyList<string> CollectVelopackResidue(string dir)
    {
        var residue = new List<string>();
        if (!TryScan(dir, out var files)) return residue;

        // Keep the latest full (the delta base) and the current version's full; the rest goes.
        var fulls = new List<(string Path, Version Version)>();
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            if (name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) &&
                TryParsePackageVersion(name, "full", out var version))
            {
                fulls.Add((file, version));
                continue;
            }

            // deltas already applied, partial downloads (.tmp/.download), anything unexpected
            residue.Add(file);
        }

        if (fulls.Count > 0)
        {
            Version latest = fulls.Max(f => f.Version);
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            keep.Add(fulls.First(f => f.Version == latest).Path);

            string currentMarker = $"{PackageId}-{UpdateService.CurrentVersion}-full.nupkg";
            string currentPath = Path.Combine(dir, currentMarker);
            if (File.Exists(currentPath)) keep.Add(currentPath);

            residue.AddRange(fulls.Where(f => !keep.Contains(f.Path)).Select(f => f.Path));
        }

        return residue;
    }

    // ── Legacy green-directory channel (%TEMP%) ───────────────────────────────────────────────

    private static UpdateResidueLocation DescribeLegacy(string dir) =>
        Build("更新下载临时区", dir, CollectLegacyResidue(dir));

    private static IReadOnlyList<string> CollectLegacyResidue(string dir)
    {
        var residue = new List<string>();
        if (!TryScan(dir, out var files)) return residue;

        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            if (name.StartsWith($"WatermarkRemover-", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                residue.Add(file); // the full package itself — applied (and replaced) on update
            }
            else if (name.StartsWith("wr-update-", StringComparison.OrdinalIgnoreCase) &&
                     name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            {
                residue.Add(file);
            }
        }

        // staging dirs from an interrupted apply — enumerate dirs separately from files
        try
        {
            foreach (string subdir in Directory.EnumerateDirectories(dir, "wr-staging-*"))
                residue.Add(subdir);
        }
        catch (Exception)
        {
            // unreadable temp dir → nothing to report there
        }

        return residue;
    }

    // ── shared helpers ─────────────────────────────────────────────────────────────────────────

    private static UpdateResidueLocation Build(string title, string dir, IReadOnlyList<string> residue)
    {
        bool exists = Directory.Exists(dir);
        long bytes = residue.Sum(SizeOf);
        string summary = !exists
            ? "目录不存在，暂无残留。"
            : residue.Count == 0
                ? "没有可清理的残留。"
                : $"{residue.Count} 项残留 · 共 {FormatBytes(bytes)}";
        return new UpdateResidueLocation(title, dir, exists, residue.Count, bytes, summary);
    }

    private static bool TryScan(string dir, out string[] files)
    {
        files = Array.Empty<string>();
        try
        {
            if (!Directory.Exists(dir)) return false;
            files = Directory.GetFiles(dir);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary><c>{PackageId}-{version}-{full|delta}.nupkg</c> → version.</summary>
    internal static bool TryParsePackageVersion(string fileName, string kind, out Version version)
    {
        version = new Version(0, 0);
        string body = Path.GetFileNameWithoutExtension(fileName); // …-{version}-{kind}
        string prefix = $"{PackageId}-";
        if (!body.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        string rest = body[prefix.Length..];
        string suffix = $"-{kind}";
        if (!rest.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
        return Version.TryParse(rest[..^suffix.Length], out version) && version.Major >= 0;
    }

    private static long SizeOf(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(SizeOf);
            if (File.Exists(path))
                return new FileInfo(path).Length;
        }
        catch (Exception)
        {
        }

        return 0;
    }

    private static bool DeletePath(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
            else return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // in use or locked — skip, never force
        }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 20 => $"{bytes / 1048576.0:F1} MB",
        >= 1L << 10 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B",
    };
}
