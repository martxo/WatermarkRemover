using Microsoft.Win32;

namespace WatermarkRemover.App.Services;

/// <summary>Per-user logon entry used only by an enabled, authorized automatic backup.</summary>
internal static class AutomaticBackupStartup
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WatermarkRemover.AutomaticBackup";

    public static void Apply(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) return;

        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (enabled)
        {
            string executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定程序路径，不能注册后台启动。");
            key.SetValue(ValueName, $"\"{executable}\" {NetdiskAdminAccess.BackgroundArgument}", RegistryValueKind.String);
            return;
        }

        key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
