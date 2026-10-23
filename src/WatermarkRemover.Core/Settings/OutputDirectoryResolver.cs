using System.IO;

namespace WatermarkRemover.Core.Settings;

/// <summary>
/// Resolves a writable directory for products that have no source file to sit beside (scans,
/// phone uploads): the configured directory, then the Desktop, then the application root.
/// Every step is best-effort — an unwritable candidate is skipped silently.
/// </summary>
public static class OutputDirectoryResolver
{
    /// <summary>Resolves the configured directory, falling back to the Desktop and then <paramref name="fallbackRoot"/>.</summary>
    public static string Resolve(string? configuredDirectory, string fallbackRoot) =>
        Resolve(
            configuredDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            fallbackRoot);

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
