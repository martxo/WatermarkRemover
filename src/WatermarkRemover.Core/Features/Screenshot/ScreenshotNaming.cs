using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Core.Features.Screenshot;

/// <summary>
/// Where a screenshot goes and what it is called. Both rules are shared with the settings page
/// (the caption shows the effective folder), so they must not live inside the overlay window.
/// </summary>
public static class ScreenshotNaming
{
    /// <summary>Subfolder created under the user's Pictures when no custom folder is set.</summary>
    public const string DefaultFolderName = "Screenshots";

    public const string Extension = ".png";

    /// <summary>
    /// Effective save folder: the configured directory, or <c>图片\Screenshots</c>. The
    /// Pictures fallback drops to the Desktop/temp on machines where the shell has no Pictures
    /// folder, so a screenshot is never lost for want of a path.
    /// </summary>
    public static string ResolveDirectory(AppSettings settings)
    {
        string configured = settings.ScreenshotOutputDirectory?.Trim() ?? string.Empty;
        if (configured.Length != 0) return configured;

        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures))
            pictures = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(pictures))
            pictures = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        return Path.Combine(pictures, DefaultFolderName);
    }

    /// <summary>「截图_20261031_143512.png」— second-resolution timestamp, sortable at a glance.</summary>
    public static string BuildFileName(DateTime timestamp) =>
        $"截图_{timestamp:yyyyMMdd_HHmmss}{Extension}";

    /// <summary>Adds <c>_2</c>, <c>_3</c>… until the name is free in the folder.</summary>
    public static string EnsureUnique(string directory, string fileName)
    {
        string candidate = fileName;
        if (!File.Exists(Path.Combine(directory, candidate))) return candidate;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int index = 2; index < 10000; index++)
        {
            candidate = $"{stem}_{index}{extension}";
            if (!File.Exists(Path.Combine(directory, candidate))) return candidate;
        }

        return $"{stem}_{DateTime.Now.Ticks}{extension}";
    }
}
