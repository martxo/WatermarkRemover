using System.IO;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Core.Features.Scan;

/// <summary>
/// Resolves where scanned PDFs are written. Scans have no input file to sit beside, so the chain
/// is: the scan-configured output directory (when set), then the Pictures folder, then the
/// application root. Every step is best-effort — an unwritable candidate is skipped silently.
/// </summary>
public static class ScanOutputDirectory
{
    public static string Resolve(AppSettings settings) =>
        Resolve(settings.ScanOutputDirectory, AppFolders.Root);

    public static string Resolve(string? configuredDirectory, string fallbackRoot) =>
        Resolve(configuredDirectory, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), fallbackRoot);

    /// <summary>Pure resolution logic; exposed for tests.</summary>
    public static string Resolve(string? configuredDirectory, string picturesDirectory, string fallbackRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory) && TryEnsure(configuredDirectory))
            return configuredDirectory;

        if (!string.IsNullOrWhiteSpace(picturesDirectory) && TryEnsure(picturesDirectory))
            return picturesDirectory;

        TryEnsure(fallbackRoot);
        return fallbackRoot;
    }

    private static bool TryEnsure(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
