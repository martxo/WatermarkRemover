using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Netdisk;

namespace WatermarkRemover.App.ViewModels;

/// <summary>One row in the netdisk file list.</summary>
public sealed class NetdiskItemViewModel
{
    public NetdiskItemViewModel(NetdiskFile file) => File = file;

    public NetdiskFile File { get; }

    public string Name => File.Name;

    public string Kind => File.IsFolder ? "文件夹" : "文件";

    public string SizeText => File.IsFolder ? string.Empty : FormatBytes(File.Size);

    public string ModifiedText => File.ModifiedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty;

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }
}

/// <summary>One clickable breadcrumb segment; its index selects the folder level to jump to.</summary>
public sealed record NetdiskBreadcrumbItem(string Name, int Index);

/// <summary>
/// The 网盘 management window: pick product + account, browse folders, upload / download /
/// create / delete. File operations delegate to the shared <see cref="INetdiskSession"/>
/// vocabulary, so the window works against any registered product unchanged.
/// </summary>
public sealed class NetdiskViewModel : ObservableObject
{
    private readonly NetdiskProviderCatalog _catalog;
    private readonly ObservableCollection<NetdiskAccountState> _accounts;
    private readonly Action<NetdiskAccountState> _upsertAccount;
    private readonly IDialogService _dialogs;
    private readonly Action<string, Exception?> _trace;
    private readonly List<(string Id, string Name)> _path = new() { (string.Empty, "根目录") };

    private INetdiskProvider? _selectedProvider;
    private string? _selectedAccountName;
    private INetdiskSession? _session;
    private NetdiskAccountState? _currentAccount;
    private NetdiskItemViewModel? _selectedItem;
    private string _newFolderName = string.Empty;
    private string _statusText = "选择产品与账号后点击「连接」。";
    private bool _isBusy;
    private int _progressPercent;
    private string _quotaText = string.Empty;

    public NetdiskViewModel(
        NetdiskProviderCatalog catalog,
        ObservableCollection<NetdiskAccountState> accounts,
        Action<NetdiskAccountState> upsertAccount,
        IDialogService dialogs,
        Action<string, Exception?> trace)
    {
        _catalog = catalog;
        _accounts = accounts;
        _upsertAccount = upsertAccount;
        _dialogs = dialogs;
        _trace = trace;

        // The auth panel can save/delete accounts while this window is open; keep the list live.
        _accounts.CollectionChanged += (_, _) => RefreshAccountNames();

        ConnectCommand = new RelayCommand(() => _ = RunAsync(ConnectAsync), () => !IsBusy && SelectedProvider is not null && SelectedAccountName is not null);
        RefreshCommand = new RelayCommand(() => _ = RunAsync(() => ListAsync(CurrentFolderId)), () => !IsBusy && _session is not null);
        UploadCommand = new RelayCommand(() => _ = RunAsync(UploadAsync), () => !IsBusy && _session is not null);
        DownloadCommand = new RelayCommand(() => _ = RunAsync(DownloadAsync), () => !IsBusy && _session is not null && SelectedItem is { File.IsFolder: false });
        NewFolderCommand = new RelayCommand(() => _ = RunAsync(NewFolderAsync), () => !IsBusy && _session is not null && !string.IsNullOrWhiteSpace(NewFolderName));
        DeleteCommand = new RelayCommand(() => _ = RunAsync(DeleteAsync), () => !IsBusy && _session is not null && SelectedItem is not null);
        OpenFolderCommand = new RelayCommand<NetdiskItemViewModel>(item => _ = RunAsync(() => OpenFolderAsync(item)), item => item is { File.IsFolder: true });
        NavigateToCommand = new RelayCommand<NetdiskBreadcrumbItem>(item => _ = RunAsync(() => NavigateToAsync(item)));

        SelectedProvider = Providers.FirstOrDefault();
        RebuildBreadcrumbs();
    }

    public IReadOnlyList<INetdiskProvider> Providers => _catalog.All;

    public ObservableCollection<string> AccountNames { get; } = new();

    public ObservableCollection<NetdiskItemViewModel> Items { get; } = new();

    public ObservableCollection<NetdiskBreadcrumbItem> Breadcrumbs { get; } = new();

    public INetdiskProvider? SelectedProvider
    {
        get => _selectedProvider;
        set
        {
            if (SetProperty(ref _selectedProvider, value))
            {
                RefreshAccountNames();
            }
        }
    }

    public string? SelectedAccountName { get => _selectedAccountName; set => SetProperty(ref _selectedAccountName, value); }

    public NetdiskItemViewModel? SelectedItem { get => _selectedItem; set => SetProperty(ref _selectedItem, value); }

    public string NewFolderName { get => _newFolderName; set => SetProperty(ref _newFolderName, value); }

    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    public string QuotaText { get => _quotaText; set => SetProperty(ref _quotaText, value); }

    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

    public int ProgressPercent { get => _progressPercent; set => SetProperty(ref _progressPercent, value); }

    public RelayCommand ConnectCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand UploadCommand { get; }

    public RelayCommand DownloadCommand { get; }

    public RelayCommand NewFolderCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand<NetdiskItemViewModel> OpenFolderCommand { get; }

    public RelayCommand<NetdiskBreadcrumbItem> NavigateToCommand { get; }

    /// <summary>Called by the window on load: connects immediately when an account is configured.</summary>
    public async Task InitializeAsync()
    {
        if (SelectedProvider is null || SelectedAccountName is null)
        {
            return;
        }

        await RunAsync(ConnectAsync);
    }

    private string CurrentFolderId => _path.Count == 0 ? string.Empty : _path[^1].Id;

    private void RefreshAccountNames()
    {
        var current = _selectedAccountName;
        AccountNames.Clear();
        foreach (var name in _accounts
            .Where(a => a.ProviderId == _selectedProvider?.Id)
            .Select(a => a.Name)
            .Distinct(StringComparer.Ordinal))
        {
            AccountNames.Add(name);
        }

        _selectedAccountName = current is not null && AccountNames.Contains(current)
            ? current
            : AccountNames.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedAccountName));
    }

    /// <summary>
    /// Tokens rotate on refresh (the old refresh token is invalidated by the product), so every
    /// successful operation writes the session's latest bag back into the saved account.
    /// </summary>
    private void PersistRotatedCredentials()
    {
        if (_session is null || _currentAccount is null)
        {
            return;
        }

        var updated = new NetdiskAccountState(
            _currentAccount.ProviderId,
            _currentAccount.Name,
            _session.CurrentCredentials.Values);
        _currentAccount = updated;
        _upsertAccount(updated);
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ProgressPercent = 0;
        try
        {
            await operation();
            PersistRotatedCredentials();
        }
        catch (Exception ex)
        {
            _trace($"网盘操作失败：{ex.Message}", ex);
            StatusText = $"操作失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ConnectAsync()
    {
        var provider = SelectedProvider;
        var account = _accounts.FirstOrDefault(a => a.ProviderId == provider?.Id && a.Name == SelectedAccountName);
        if (provider is null || account is null)
        {
            StatusText = "请先选择产品与账号（账号在「设置 → 网盘设置」中添加）。";
            return;
        }

        StatusText = $"正在连接 {provider.DisplayName}…";
        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        _session = await provider.ConnectAsync(account.ToCredentials());
        _currentAccount = account;
        _path.Clear();
        _path.Add((string.Empty, "根目录"));
        StatusText = $"已连接：{_session.Account.DisplayName}";
        await ListAsync(string.Empty);
        await RefreshQuotaAsync();
    }

    private async Task ListAsync(string folderId)
    {
        var session = _session ?? throw new InvalidOperationException("尚未连接网盘。");
        var page = await session.ListAsync(folderId);

        Items.Clear();
        foreach (var file in page.Items.OrderByDescending(f => f.IsFolder).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            Items.Add(new NetdiskItemViewModel(file));
        }

        RebuildBreadcrumbs();
    }

    private async Task OpenFolderAsync(NetdiskItemViewModel? item)
    {
        if (item is not { File.IsFolder: true })
        {
            return;
        }

        _path.Add((item.File.Id, item.File.Name));
        await ListAsync(item.File.Id);
    }

    private async Task NavigateToAsync(NetdiskBreadcrumbItem? item)
    {
        if (item is null || item.Index < 0 || item.Index >= _path.Count)
        {
            return;
        }

        _path.RemoveRange(item.Index + 1, _path.Count - item.Index - 1);
        await ListAsync(_path[^1].Id);
    }

    private void RebuildBreadcrumbs()
    {
        Breadcrumbs.Clear();
        for (var i = 0; i < _path.Count; i++)
        {
            Breadcrumbs.Add(new NetdiskBreadcrumbItem(_path[i].Name, i));
        }
    }

    private async Task UploadAsync()
    {
        var session = _session ?? throw new InvalidOperationException("尚未连接网盘。");
        var dialog = new OpenFileDialog { Multiselect = true, Title = "选择要上传的文件" };
        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        var files = dialog.FileNames;
        for (var i = 0; i < files.Length; i++)
        {
            var localPath = files[i];
            StatusText = $"正在上传 {Path.GetFileName(localPath)}（{i + 1}/{files.Length}）…";
            var progress = new Progress<NetdiskTransferProgress>(p =>
            {
                if (p.TotalBytes > 0)
                {
                    ProgressPercent = (int)(p.BytesTransferred * 100 / p.TotalBytes.Value);
                }
            });
            await session.UploadAsync(CurrentFolderId, localPath, progress);
            AppendFileLog("上传", localPath);
        }

        StatusText = $"已上传 {files.Length} 个文件。";
        await ListAsync(CurrentFolderId);
        await RefreshQuotaAsync();
    }

    private async Task DownloadAsync()
    {
        var session = _session ?? throw new InvalidOperationException("尚未连接网盘。");
        var item = SelectedItem;
        if (item is not { File.IsFolder: false })
        {
            return;
        }

        var dialog = new SaveFileDialog { FileName = item.File.Name, Title = "下载到…" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        StatusText = $"正在下载 {item.File.Name}…";
        var progress = new Progress<NetdiskTransferProgress>(p =>
        {
            if (p.TotalBytes > 0)
            {
                ProgressPercent = (int)(p.BytesTransferred * 100 / p.TotalBytes.Value);
            }
        });
        await session.DownloadAsync(item.File.Id, dialog.FileName, progress);
        AppendFileLog("下载", dialog.FileName);
        StatusText = $"已下载到 {dialog.FileName}";
    }

    private async Task NewFolderAsync()
    {
        var session = _session ?? throw new InvalidOperationException("尚未连接网盘。");
        var name = NewFolderName.Trim();
        await session.CreateFolderAsync(CurrentFolderId, name);
        NewFolderName = string.Empty;
        StatusText = $"已创建文件夹「{name}」。";
        await ListAsync(CurrentFolderId);
    }

    private async Task DeleteAsync()
    {
        var session = _session ?? throw new InvalidOperationException("尚未连接网盘。");
        var item = SelectedItem;
        if (item is null)
        {
            return;
        }

        if (!_dialogs.Confirm($"确定把「{item.File.Name}」移入回收站吗？", "删除确认"))
        {
            return;
        }

        await session.DeleteAsync(new[] { item.File.Id });
        StatusText = $"已把「{item.File.Name}」移入回收站。";
        await ListAsync(CurrentFolderId);
    }

    private async Task RefreshQuotaAsync()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var quota = await _session.GetQuotaAsync();
            QuotaText = $"容量：{NetdiskItemViewModel.FormatBytes(quota.UsedBytes)} / {NetdiskItemViewModel.FormatBytes(quota.TotalBytes)}";
        }
        catch (Exception ex)
        {
            _trace("读取网盘容量失败", ex);
        }
    }

    private void AppendFileLog(string action, string path) =>
        _trace($"网盘{action}：{path}", null);
}
