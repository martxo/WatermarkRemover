using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App.Services;

/// <summary>
/// 自动备份的出厂默认行为（产品决策 2026-11-16）：只要有已登录的网盘账号，应用启动即自动
/// 监控桌面文件夹并上传到该账号的 /桌面文件备份——不需要管理员模式，也不弹授权确认框。
/// 管理员在设置页明确启用过的配置仍然优先，自动默认只在没有手动配置时兜底。
/// 授权字段只填在内存里的生效配置上（指纹由配置确定性推出，可随时重算），不写入设置文件；
/// 监控目录/账号/云端目的地变化会让指纹变化，服务因此自然切到新配置，状态按指纹隔离。
/// </summary>
public static class AutomaticBackupDefaults
{
    /// <summary>
    /// 返回自动备份的生效配置：管理员的手动配置原样返回；否则由「第一个网盘账号 + 桌面 +
    /// /桌面文件备份」推导，并填好授权字段。没有任何账号时返回 null（调用方需据此停掉服务）。
    /// </summary>
    public static AppSettings? BuildEffective(AppSettings settings, IReadOnlyList<NetdiskAccountState> accounts)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(accounts);

        if (settings.AutomaticBackupEnabled && AutomaticBackupFileFilter.HasValidConsent(settings))
            return settings;

        // 上传账号：设置里已指向的账号若仍存在则沿用（管理员配置过但暂时关闭的场景），
        // 否则用第一个账号——与「快速上传」的 PrimaryAccount 语义一致。
        NetdiskAccountState? account =
            accounts.FirstOrDefault(a =>
                a.ProviderId == settings.AutomaticBackupProviderId.Trim() &&
                a.Name == settings.AutomaticBackupAccountName.Trim())
            ?? accounts.FirstOrDefault();
        if (account is null) return null;

        var effective = settings.Clone();
        effective.AutomaticBackupEnabled = true;
        effective.AutomaticBackupRootDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory);
        effective.AutomaticBackupProviderId = account.ProviderId;
        effective.AutomaticBackupAccountName = account.Name;
        if (string.IsNullOrWhiteSpace(effective.AutomaticBackupDrive))
            effective.AutomaticBackupDrive = "资源库";
        if (string.IsNullOrWhiteSpace(effective.AutomaticBackupRemoteDirectory))
            effective.AutomaticBackupRemoteDirectory = "/桌面文件备份";
        effective.AutomaticBackupConsentFingerprint =
            AutomaticBackupFileFilter.ConsentFingerprint(effective);
        effective.AutomaticBackupConsentedAtUtc = DateTimeOffset.UtcNow;
        return effective;
    }
}
