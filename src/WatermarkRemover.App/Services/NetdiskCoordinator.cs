using System.Collections.ObjectModel;
using Microsoft.Win32;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Settings;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Core.Settings;

namespace WatermarkRemover.App.Services;

/// <summary>
/// The 网盘 section's state and lifecycle: product discovery, account persistence (secrets through
/// DPAPI), the management window and quick upload. Products are discovered from the Netdisk
/// assembly, so a new provider shows up here and in the settings form with no change.
/// </summary>
public sealed class NetdiskCoordinator : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly Func<bool> _isBusy;
    private string _summaryText = "未配置网盘账号。";
    private NetdiskWindow? _window;

    public NetdiskCoordinator(AppSettings settings, IDialogService dialogs, Func<bool> isBusy)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _isBusy = isBusy ?? throw new ArgumentNullException(nameof(isBusy));

        Catalog = new NetdiskProviderCatalog();
        Catalog.RegisterDiscovered(typeof(WatermarkRemover.Netdisk.AliyunDriveProvider).Assembly, AppLog.Trace);
        Accounts = new ObservableCollection<NetdiskAccountState>(
            NetdiskAccountPersistence.Decode(settings.NetdiskAccounts, new DpapiSecretProtector(), AppLog.Trace));
        Settings = new NetdiskAccountSettingViewModel(
            Catalog,
            Accounts,
            Upsert,
            (providerId, name) => Remove(providerId, name),
            AppLog.Trace);
        OpenCommand = new RelayCommand(Open);
        QuickUploadCommand = new RelayCommand(() => _ = QuickUploadAsync(), () => !_isBusy());
        RefreshSummary();
    }

    /// <summary>Registered netdisk products, in registration order.</summary>
    public NetdiskProviderCatalog Catalog { get; }

    /// <summary>Configured accounts, plaintext in memory; secrets are protected on save.</summary>
    public ObservableCollection<NetdiskAccountState> Accounts { get; }

    /// <summary>The 「网盘设置」 group state: dynamic credential form, test connection, OAuth.</summary>
    public NetdiskAccountSettingViewModel Settings { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand QuickUploadCommand { get; }

    /// <summary>Status line shown on the 网盘 page.</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    /// <summary>The host's busy state changed — re-evaluate the quick-upload gate.</summary>
    public void RefreshCommandState() => QuickUploadCommand.RaiseCanExecuteChanged();

    /// <summary>True when an account is configured — gates the row and batch upload entry points.</summary>
    public bool HasAccounts => Accounts.Count > 0;

    /// <summary>Account every picker-free upload targets (the first one configured).</summary>
    public string PrimaryAccountName => Accounts.Count > 0 ? Accounts[0].Name : string.Empty;

    /// <summary>
    /// Uploads already-produced files to the first account's root without a file picker — this is
    /// what the job rows call. One error entry per failed path; an empty list means everything
    /// landed. Never throws: the row chip needs a result, not an exception.
    /// </summary>
    public async Task<IReadOnlyList<(string Path, string Error)>> UploadFilesAsync(IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0) return Array.Empty<(string, string)>();

        if (!HasAccounts)
            return files.Select(f => (f, "未配置网盘账号")).ToList();

        var account = Accounts[0];
        var provider = Catalog.All.FirstOrDefault(p => p.Id == account.ProviderId);
        if (provider is null)
            return files.Select(f => (f, $"未找到网盘产品「{account.ProviderId}」")).ToList();

        var failures = new List<(string Path, string Error)>();
        SummaryText = $"正在上传 {files.Count} 个文件到「{account.Name}」…";
        try
        {
            await using var session = await provider.ConnectAsync(account.ToCredentials());
            foreach (string path in files)
            {
                try
                {
                    await session.UploadAsync(string.Empty, path);
                    AppLog.Trace($"网盘上传：{path}", null);
                }
                catch (Exception ex)
                {
                    AppLog.Trace($"网盘上传失败：{path}（{ex.Message}）", ex);
                    failures.Add((path, ex.Message));
                }
            }

            // Tokens may have rotated during the upload — write the latest bag back.
            Upsert(new NetdiskAccountState(
                account.ProviderId, account.Name, session.CurrentCredentials.Values));
            SummaryText = failures.Count == 0
                ? $"已上传 {files.Count} 个文件到「{account.Name}」（{provider.DisplayName}）。"
                : $"已上传 {files.Count - failures.Count}/{files.Count} 个文件，" +
                  $"{failures.Count} 个失败（「{account.Name}」）。";
        }
        catch (Exception ex)
        {
            // The connection itself failed: every file in this batch carries the same reason.
            AppLog.Trace($"网盘上传失败：{ex.Message}", ex);
            SummaryText = $"上传失败：{ex.Message}";
            return files.Select(f => (f, ex.Message)).ToList();
        }

        return failures;
    }

    /// <summary>Inserts or replaces the account with the same product + name, then persists.</summary>
    public void Upsert(NetdiskAccountState account)
    {
        var existing = Accounts.FirstOrDefault(a => a.ProviderId == account.ProviderId && a.Name == account.Name);
        if (existing is null)
        {
            Accounts.Add(account);
        }
        else
        {
            Accounts[Accounts.IndexOf(existing)] = account;
        }

        Persist();
        RefreshSummary();
    }

    /// <summary>Removes the account and persists; returns whether anything was removed.</summary>
    public bool Remove(string providerId, string name)
    {
        var removed = Accounts
            .Where(a => a.ProviderId == providerId && a.Name == name)
            .ToList();
        foreach (var account in removed)
        {
            Accounts.Remove(account);
        }

        if (removed.Count > 0)
        {
            Persist();
            RefreshSummary();
        }

        return removed.Count > 0;
    }

    /// <summary>Secrets go through DPAPI before they reach the settings file.</summary>
    private void Persist()
    {
        _settings.NetdiskAccounts = NetdiskAccountPersistence.Encode(
            Accounts, Catalog.All, new DpapiSecretProtector());
        if (SettingsService.Save(_settings))
            AppLog.Info($"网盘账号已保存（{Accounts.Count} 个）");
        else
            AppLog.Error("网盘账号保存失败（目录不可写？）");
    }

    private void RefreshSummary()
    {
        SummaryText = Accounts.Count == 0
            ? "未配置网盘账号，请先在「设置 → 网盘设置」中添加。"
            : $"已配置 {Accounts.Count} 个账号：{string.Join("、", Accounts.Select(a => a.Name))}";
    }

    /// <summary>Opens the management window; a second click just activates the open one.</summary>
    private void Open()
    {
        if (_window is { IsLoaded: true })
        {
            _window.Activate();
            return;
        }

        // Deliberately unowned: an owned window is pinned above its owner forever, which blocked
        // the main window while browsing files side by side. An independent top-level window
        // z-orders normally.
        _window = new NetdiskWindow(
            new NetdiskViewModel(Catalog, Accounts, Upsert, _dialogs, AppLog.Trace),
            Settings);
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        AppLog.Info("打开网盘管理窗口");
    }

    /// <summary>Upload picked files to the first configured account's root folder.</summary>
    private async Task QuickUploadAsync()
    {
        if (Accounts.Count == 0)
        {
            SummaryText = "未配置网盘账号，请先在「设置 → 网盘设置」中添加。";
            return;
        }

        var dialog = new OpenFileDialog { Multiselect = true, Title = "选择要上传到网盘的文件" };
        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        var account = Accounts[0];
        var provider = Catalog.All.FirstOrDefault(p => p.Id == account.ProviderId);
        if (provider is null)
        {
            SummaryText = $"未找到网盘产品「{account.ProviderId}」。";
            return;
        }

        var files = dialog.FileNames;
        SummaryText = $"正在上传 {files.Length} 个文件到「{account.Name}」…";
        try
        {
            await using var session = await provider.ConnectAsync(account.ToCredentials());
            foreach (var path in files)
            {
                await session.UploadAsync(string.Empty, path);
                AppLog.Trace($"网盘快速上传：{path}", null);
            }

            // Tokens may have rotated during the upload — write the latest bag back.
            Upsert(new NetdiskAccountState(
                account.ProviderId, account.Name, session.CurrentCredentials.Values));
            SummaryText = $"已上传 {files.Length} 个文件到「{account.Name}」（{provider.DisplayName}）。";
        }
        catch (Exception ex)
        {
            AppLog.Trace($"网盘快速上传失败：{ex.Message}", ex);
            SummaryText = $"上传失败：{ex.Message}";
        }
    }
}
