using System.IO;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Core.Features.Scan;

/// <summary>
/// Resolves where scanned PDFs are written. Scans have no input file to sit beside, so the chain
/// is: the scan-configured output directory (when set), then the Desktop, then the application
/// root. Every step is best-effort — an unwritable candidate is skipped silently.
/// </summary>
public static class ScanOutputDirectory
{
    public static string Resolve(AppSettings settings) =>
        Resolve(settings.ScanOutputDirectory, AppFolders.Root);

    public static string Resolve(string? configuredDirectory, string fallbackRoot) =>
        Resolve(configuredDirectory, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), fallbackRoot);

    /// <summary>Pure resolution logic; exposed for tests.</summary>
    public static string Resolve(string? configuredDirectory, string desktopDirectory, string fallbackRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory) && TryEnsure(configuredDirectory))
            return configuredDirectory;

        if (!string.IsNullOrWhiteSpace(desktopDirectory) && TryEnsure(desktopDirectory))
            return desktopDirectory;

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
