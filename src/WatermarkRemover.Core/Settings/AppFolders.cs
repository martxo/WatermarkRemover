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
    public static string Root => _cachedRoot ??= ResolveRoot(
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
