using System.Security.Principal;

namespace WatermarkRemover.App;

/// <summary>Process-wide gate for the elevated netdisk administration mode.</summary>
internal static class NetdiskAdminAccess
{
    public const string AdminArgument = "--netdisk-admin";
    public const string BackgroundArgument = "--background";

    public static bool IsRequested { get; private set; }

    public static bool IsBackground { get; private set; }

    public static bool IsEnabled { get; private set; }

    public static void Initialize(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        IsRequested = args.Contains(AdminArgument, StringComparer.OrdinalIgnoreCase);
        IsBackground = args.Contains(BackgroundArgument, StringComparer.OrdinalIgnoreCase);
        IsEnabled = IsRequested && IsAdministrator();
    }

    /// <summary>
    /// Relaunches this app with <see cref="AdminArgument"/> through the Windows UAC prompt, so
    /// the user never has to find "以管理员身份运行" themselves: one hotkey, one Windows
    /// confirmation. The current instance keeps running until the elevated copy signals the
    /// single-instance exit event — a cancelled UAC therefore loses nothing.
    /// Returns false with a null error when the user cancelled the prompt.
    /// </summary>
    public static bool TryLaunchElevated(out string? error)
    {
        error = null;
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return false;

        try
        {
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = AdminArgument,
                    UseShellExecute = true,
                    Verb = "runas",
                });
            return process is not null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: the user clicked "No" on the Windows prompt — not an error.
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
