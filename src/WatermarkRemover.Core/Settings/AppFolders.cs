using System.IO;

namespace WatermarkRemover.Core.Settings;

/// <summary>
/// Resolves a writable per-machine root for settings and logs. Prefers the directory next to the
/// executable so the app stays portable; falls back to <c>%APPDATA%\WatermarkRemover</c> when the
/// executable directory is not writable (e.g. the app was placed under Program Files).
/// </summary>
public static class AppFolders
{
    private static string? _cachedRoot;

    /// <summary>Writable root directory, resolved once per process.</summary>
    public static string Root => _cachedRoot ??= ResolveRootFor(
        AppContext.BaseDirectory,
        DefaultFallbackDirectory);

    public static string SettingsFilePath => Path.Combine(Root, "WatermarkRemover.settings.json");

    public static string LogDirectory => Path.Combine(Root, "logs");

    /// <summary>Recognition report JSONL files (kept far longer than logs — training corpus).</summary>
    public static string ReportDirectory => Path.Combine(Root, "reports");

    private static string DefaultFallbackDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "WatermarkRemover");

    /// <summary>
    /// Resolution used by <see cref="Root"/>. A Velopack install replaces the whole
    /// <c>current</c> folder on every update, which used to wipe settings, the netdisk login and
    /// the job journal. Installed copies therefore keep their data in the stable
    /// <c>%APPDATA%\WatermarkRemover</c> folder; a portable/green copy keeps the old
    /// "next to the exe" behaviour.
    /// </summary>
    internal static string ResolveRootFor(string preferredDirectory, string fallbackDirectory)
    {
        if (!IsVelopackInstall(preferredDirectory))
        {
            return ResolveRoot(preferredDirectory, fallbackDirectory);
        }

        try { Directory.CreateDirectory(fallbackDirectory); } catch { /* best effort */ }
        MigrateLegacyData(preferredDirectory, fallbackDirectory);
        return fallbackDirectory;
    }

    /// <summary>
    /// True when <paramref name="directory"/> is the Velopack <c>current</c> folder (its parent
    /// carries <c>Update.exe</c> or the install manifest <c>sq.version</c>).
    /// </summary>
    internal static bool IsVelopackInstall(string directory)
    {
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            string? parent = Path.GetDirectoryName(full);
            return parent is not null &&
                   (File.Exists(Path.Combine(parent, "Update.exe")) ||
                    File.Exists(Path.Combine(parent, "sq.version")));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies the old in-app-folder settings/journal/reports into the stable folder once, so an
    /// upgrade to the fixed layout does not reset the user's configuration.
    /// </summary>
    private static void MigrateLegacyData(string legacyRoot, string stableRoot)
    {
        try
        {
            string legacySettings = Path.Combine(legacyRoot, "WatermarkRemover.settings.json");
            string stableSettings = Path.Combine(stableRoot, "WatermarkRemover.settings.json");
            if (File.Exists(legacySettings) && !File.Exists(stableSettings))
            {
                File.Copy(legacySettings, stableSettings);
            }

            foreach (string name in new[] { "journal", "reports" })
            {
                string source = Path.Combine(legacyRoot, name);
                string target = Path.Combine(stableRoot, name);
                if (Directory.Exists(source) && !Directory.Exists(target))
                {
                    CopyDirectory(source, target);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Migration is best effort: a failure must never stop the app from starting.
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        }

        foreach (string dir in Directory.GetDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }

    /// <summary>
    /// Pure resolution logic: returns <paramref name="preferredDirectory"/> when writable,
    /// otherwise ensures and returns <paramref name="fallbackDirectory"/>. Exposed for tests.
    /// </summary>
    public static string ResolveRoot(string preferredDirectory, string fallbackDirectory)
    {
        if (IsDirectoryWritable(preferredDirectory))
            return preferredDirectory;

        try { Directory.CreateDirectory(fallbackDirectory); } catch { /* best effort */ }
        return fallbackDirectory;
    }

    /// <summary>Creates the root directory if missing. Best effort.</summary>
    public static void EnsureRoot()
    {
        try { Directory.CreateDirectory(Root); } catch { }
    }

    private static bool IsDirectoryWritable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
