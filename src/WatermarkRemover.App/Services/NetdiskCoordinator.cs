using System.Collections.ObjectModel;
using Microsoft.Win32;
using System.Windows;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Settings;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Netdisk;

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
    private readonly bool _isNetdiskAdmin;
    private string _summaryText = "未配置网盘账号。";
    private NetdiskWindow? _window;

    public NetdiskCoordinator(
        AppSettings settings, IDialogService dialogs, Func<bool> isBusy, bool isNetdiskAdmin = true)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _isBusy = isBusy ?? throw new ArgumentNullException(nameof(isBusy));
        _isNetdiskAdmin = isNetdiskAdmin;

        Catalog = new NetdiskProviderCatalog();
        Catalog.RegisterDiscovered(typeof(WatermarkRemover.Netdisk.AliyunDriveProvider).Assembly, AppLog.Trace);
        Accounts = new ObservableCollection<NetdiskAccountState>(
            NetdiskAccountPersistence.Decode(settings.NetdiskAccounts, new DpapiSecretProtector(), AppLog.Trace));
        // A portable login pack only writes the stable CLI config; materialize it as a real app
        // account so the 网盘 page and management window can use the same login immediately.
        EnsureCliAccount();
        Settings = new NetdiskAccountSettingViewModel(
            Catalog,
            Accounts,
            Upsert,
            (providerId, name) => Remove(providerId, name),
            AppLog.Trace);
        Transfer = new NetdiskTransferService(
            Catalog,
            (providerId, name) => Accounts.FirstOrDefault(a =>
                a.ProviderId == providerId && a.Name == name),
            Upsert);
        // A login may finish on the 设置 page or in the management window's own panel; both share
        // this state object, so listening here makes the freshly authorized account the one that
        // is opened next (and switches an already-open window) instead of resurrecting the
        // previously selected product.
        Settings.AccountSaved += OnAccountSaved;
        OpenCommand = new RelayCommand(Open, () => _isNetdiskAdmin);
        QuickUploadCommand = new RelayCommand(
            () => _ = QuickUploadAsync(), () => _isNetdiskAdmin && !_isBusy());
        RefreshSummary();
    }

    /// <summary>Registered netdisk products, in registration order.</summary>
    public NetdiskProviderCatalog Catalog { get; }

    /// <summary>Configured accounts, plaintext in memory; secrets are protected on save.</summary>
    public ObservableCollection<NetdiskAccountState> Accounts { get; }

    internal NetdiskTransferService Transfer { get; }

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

    /// <summary>Explicit account deletion; token refreshes use Upsert and never raise this event.</summary>
    public event Action<string, string>? AccountRemoved;

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
        if (!_isNetdiskAdmin)
            return files.Select(f => (f, "当前模式不允许手动上传")).ToList();
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
            failures.AddRange(await Transfer.UploadFilesAsync(CreateUploadTarget(account), files));
            foreach (var (path, error) in failures)
            {
                AppLog.Trace($"网盘上传失败：{path}（{error}）", null);
            }

            SummaryText = failures.Count == 0
                ? $"已上传 {files.Count} 个文件到「{account.Name}」（{provider.DisplayName}）。"
                : $"已上传 {files.Count - failures.Count}/{files.Count} 个文件，" +
                  $"{failures.Count} 个失败（「{account.Name}」）。";
        }
        catch (Exception ex)
        {
            AppLog.Trace($"网盘上传失败：{ex.Message}", ex);
            SummaryText = $"上传失败：{ex.Message}";
            return files.Select(f => (f, ex.Message)).ToList();
        }

        return failures;
    }

    private NetdiskUploadTarget CreateUploadTarget(NetdiskAccountState account) =>
        new(
            account.ProviderId,
            account.Name,
            _settings.NetdiskUploadDrive?.Trim() ?? string.Empty,
            _settings.NetdiskUploadDirectory?.Trim() ?? string.Empty);

    /// <summary>
    /// Adds the stable CLI login as an app account when no 阿里云盘 account is configured. This
    /// is what makes an imported login pack usable by the 网盘 page without another scan.
    /// </summary>
    private void EnsureCliAccount()
    {
        if (Accounts.Any(a => a.ProviderId == "alipan")) return;

        IReadOnlyDictionary<string, string>? values = AliyunpanCliRuntime.TryBuildAccountValues();
        if (values is null) return;

        Accounts.Add(new NetdiskAccountState("alipan", "阿里云盘（本机登录）", values));
        Persist();
        RefreshSummary();
        AppLog.Info("已从本机 aliyunpan 登录状态创建阿里云盘账号");
    }

    /// <summary>Inserts or replaces the account with the same product + name, then persists.</summary>
    public void Upsert(NetdiskAccountState account)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            // Token refresh may finish on the backup worker. Do not block it on Dispatcher.Invoke:
            // the UI thread can be waiting for that same worker during shutdown.
            dispatcher.BeginInvoke(() => Upsert(account));
            return;
        }

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
            foreach (var account in removed)
                AccountRemoved?.Invoke(account.ProviderId, account.Name);
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
        if (!_isNetdiskAdmin)
        {
            SummaryText = string.Empty;
            return;
        }

        SummaryText = Accounts.Count == 0
            ? "未配置网盘账号，请先在「设置 → 网盘设置」中添加。"
            : $"已配置 {Accounts.Count} 个账号：{string.Join("、", Accounts.Select(a => a.Name))}";
    }

    /// <summary>Opens the management window; a second click just activates the open one.</summary>
    private void Open()
    {
        if (!_isNetdiskAdmin) return;

        if (_window is { IsLoaded: true })
        {
            _window.Activate();
            return;
        }

        // Deliberately unowned: an owned window is pinned above its owner forever, which blocked
        // the main window while browsing files side by side. An independent top-level window
        // z-orders normally.
        _window = new NetdiskWindow(
            new NetdiskViewModel(Catalog, Accounts, Upsert, _dialogs, AppLog.Trace,
                requestMoveTarget: NetdiskMoveDialog.Prompt,
                lastProviderId: _settings.NetdiskLastProviderId,
                lastAccountName: _settings.NetdiskLastAccountName,
                rememberSelection: RememberSelection,
                rememberUploadLocation: RememberUploadLocation),
            Settings);
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        AppLog.Info("打开网盘管理窗口");
    }

    /// <summary>
    /// A login/import saved an account: make it the resume target and, when the management window
    /// is open, switch the listing right away — otherwise the page still shows the old product's
    /// (possibly empty) drive, which reads as "login did nothing".
    /// </summary>
    private void OnAccountSaved(string providerId, string accountName)
    {
        RememberSelection(providerId, accountName);
        if (_window is { IsLoaded: true })
        {
            _ = _window.ShowAccountAsync(providerId, accountName);
        }
    }

    /// <summary>
    /// Remembers the connecting account so the next 网盘管理 open resumes it. Written straight to
    /// the settings file: a crash before the next save must not lose the user's selection.
    /// </summary>
    private void RememberSelection(string providerId, string accountName)
    {
        if (string.Equals(_settings.NetdiskLastProviderId, providerId, StringComparison.Ordinal) &&
            string.Equals(_settings.NetdiskLastAccountName, accountName, StringComparison.Ordinal))
        {
            return;
        }

        _settings.NetdiskLastProviderId = providerId;
        _settings.NetdiskLastAccountName = accountName;
        SettingsService.Save(_settings);
    }

    /// <summary>
    /// 在网盘管理窗口的哪个目录上传过，那里就成为「快速上传位置」— 任务行与快速上传的下一次
    /// 默认目标。同样直写设置文件，管理窗口不必先关再开设置页。
    /// </summary>
    private void RememberUploadLocation(string driveName, string remotePath)
    {
        if (string.Equals(_settings.NetdiskUploadDrive, driveName, StringComparison.Ordinal) &&
            string.Equals(_settings.NetdiskUploadDirectory, remotePath, StringComparison.Ordinal))
        {
            return;
        }

        _settings.NetdiskUploadDrive = driveName;
        _settings.NetdiskUploadDirectory = remotePath;
        SettingsService.Save(_settings);
        AppLog.Info($"快速上传位置已更新：{driveName} {remotePath}");
    }

    /// <summary>
    /// Opens the cloud folder picker for the settings page's 「浏览…」 button; the picked
    /// drive + directory land in the settings form. Returns null when cancelled or when no
    /// account is configured (the user is told so).
    /// </summary>
    public (string DriveName, string RemotePath)? BrowseForUploadFolder()
    {
        if (!_isNetdiskAdmin) return null;

        if (!HasAccounts)
        {
            _dialogs.Warn("还没有配置网盘账号。请先在「设置 → 网盘设置」中添加一个账号。", "选择上传目录");
            return null;
        }

        return NetdiskFolderPickerWindow.Prompt(
            Application.Current?.MainWindow,
            Catalog,
            Accounts,
            _settings.NetdiskLastProviderId,
            _settings.NetdiskLastAccountName,
            AppLog.Trace);
    }

    /// <summary>Upload picked files to the first configured account's root folder.</summary>
    private async Task QuickUploadAsync()
    {
        if (!_isNetdiskAdmin) return;

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
            foreach (var path in files)
            {
                await Transfer.UploadFileAsync(CreateUploadTarget(account), path);
                AppLog.Trace($"网盘快速上传：{path}", null);
            }

            SummaryText = $"已上传 {files.Length} 个文件到「{account.Name}」（{provider.DisplayName}）。";
        }
        catch (Exception ex)
        {
            AppLog.Trace($"网盘快速上传失败：{ex.Message}", ex);
            SummaryText = $"上传失败：{ex.Message}";
        }
    }
}
