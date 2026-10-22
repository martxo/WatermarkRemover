using System.Diagnostics;
using System.Security.Principal;

namespace WatermarkRemover.Index.Setup;

/// <summary>Stable on-disk contract shared by the app, the installer switch and the host.</summary>
public static class IndexLayout
{
    public const string TaskName = @"WatermarkRemover\FileIndex";
    public const string ExeFileName = "WatermarkRemover.Indexer.exe";
    public const string VersionFileName = "version.txt";

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WatermarkRemover");

    public static string InstallDirectory => Path.Combine(DataDirectory, "Indexer");

    public static string InstalledExe => Path.Combine(InstallDirectory, ExeFileName);

    public static string VersionFile => Path.Combine(InstallDirectory, VersionFileName);

    /// <summary>Where per-volume snapshots live (written by the elevated host only).</summary>
    public static string StateDirectory => Path.Combine(DataDirectory, "Index");
}

/// <summary>
/// App-side lifecycle for the index host: detect, start, and (first time / engine update) run the
/// elevated installer. The app itself never asks for elevation during normal use.
/// </summary>
public sealed class IndexSetupService
{
    private readonly Func<string> _baseDirectory;

    public IndexSetupService()
        : this(() => AppContext.BaseDirectory)
    {
    }

    public IndexSetupService(Func<string> baseDirectory)
    {
        _baseDirectory = baseDirectory;
    }

    /// <summary>The engine shipped beside the app (sidecar, excluded from the single-file bundle).</summary>
    public string SidecarPath => Path.Combine(_baseDirectory(), IndexLayout.ExeFileName);

    public bool SidecarExists => File.Exists(SidecarPath);

    public bool IsInstalled => File.Exists(IndexLayout.InstalledExe) && File.Exists(IndexLayout.VersionFile);

    public string InstalledVersion => ReadVersion(IndexLayout.InstalledExe);

    public string ShippedVersion => ReadVersion(SidecarPath);

    public bool NeedsUpdate =>
        IsInstalled
        && ShippedVersion.Length > 0
        && InstalledVersion.Length > 0
        && !string.Equals(ShippedVersion, InstalledVersion, StringComparison.OrdinalIgnoreCase);

    /// <summary>Starts the installed host through its logon task — no elevation prompt.</summary>
    public bool TryStartInstalled()
    {
        if (!IsInstalled) return false;

        try
        {
            var startInfo = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("/Run");
            startInfo.ArgumentList.Add("/TN");
            startInfo.ArgumentList.Add(IndexLayout.TaskName);

            using Process? process = Process.Start(startInfo);
            if (process is null) return false;

            process.WaitForExit(5000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Runs <c>--install</c> from the shipped sidecar. Returns false when the user declines UAC,
    /// when the sidecar is missing, or when the installer reports a failure.
    /// </summary>
    public async Task<bool> InstallAsync(CancellationToken cancellationToken = default)
    {
        if (!SidecarExists) return false;

        var startInfo = new ProcessStartInfo(SidecarPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = "--install",
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        try
        {
            using Process? process = Process.Start(startInfo);
            if (process is null) return false;

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the UAC prompt was dismissed. Not an error state for the page.
            return false;
        }
    }

    public static bool IsElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string ReadVersion(string path)
    {
        try
        {
            if (!File.Exists(path)) return string.Empty;
            return FileVersionInfo.GetVersionInfo(path).FileVersion ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
