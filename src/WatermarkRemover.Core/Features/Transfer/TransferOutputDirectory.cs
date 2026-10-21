using System.IO;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.Core.Features.Transfer;

/// <summary>
/// Resolves where phone-uploaded files are written. Transfers have no source file to sit
/// beside, so the chain is: the configured directory (设置 → 手机传文件保存位置, when set),
/// then a 「手机传文件」 folder on the Desktop, then the app root. Every step is best-effort —
/// an unwritable candidate is skipped silently (same contract as
/// <see cref="WatermarkRemover.Core.Features.Scan.ScanOutputDirectory"/>).
/// </summary>
public static class TransferOutputDirectory
{
    public static string Resolve(AppSettings settings) =>
        Resolve(settings.TransferOutputDirectory, AppFolders.Root);

    /// <summary>Pure resolution logic; exposed for tests.</summary>
    public static string Resolve(string? configuredDirectory, string fallbackRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory) && TryEnsure(configuredDirectory))
            return configuredDirectory;

        string desktopTransfer = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "手机传文件");
        if (TryEnsure(desktopTransfer))
            return desktopTransfer;

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
