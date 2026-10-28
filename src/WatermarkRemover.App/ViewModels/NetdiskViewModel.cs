using System.Collections.ObjectModel;
using System.IO;
using Microsoft.Win32;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Netdisk;

namespace WatermarkRemover.App.ViewModels;

/// <summary>One row in the netdisk file list.</summary>
public sealed class NetdiskItemViewModel
{
    public NetdiskItemViewModel(NetdiskFile file, string? location = null)
    {
        File = file;
        Location = location ?? string.Empty;
    }

    public NetdiskFile File { get; }

    /// <summary>Name of the containing folder; only filled for search results.</summary>
    public string Location { get; }

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
    private readonly Func<string, string?>? _requestMoveTarget;
    private readonly List<(string Id, string Name)> _path = new() { (string.Empty, "根目录") };
    private IReadOnlyList<NetdiskItemViewModel> _selectedItems = Array.Empty<NetdiskItemViewModel>();

    private INetdiskProvider? _selectedProvider;
    private string? _selectedAccountName;
    private INetdiskSession? _session;
    private NetdiskAccountState? _currentAccount;
    private NetdiskItemViewModel? _selectedItem;
    private NetdiskDrive? _selectedDrive;
    private bool _suppressDriveSwitch;
    private string _newFolderName = string.Empty;
    private string _searchText = string.Empty;
    private readonly Dictionary<string, string> _locationCache = new(StringComparer.Ordinal);
    private string _statusText = "选择产品与账号后点击「连接」。";
    private bool _isBusy;
    private int _progressPercent;
    private string _quotaText = string.Empty;

    public NetdiskViewModel(
        NetdiskProviderCatalog catalog,
        ObservableCollection<NetdiskAccountState> accounts,
        Action<NetdiskAccountState> upsertAccount,
        IDialogService dialogs,
        Action<string, Exception?> trace,
        Func<string, string?>? requestMoveTarget = null)
    {
        _catalog = catalog;
        _accounts = accounts;
        _upsertAccount = upsertAccount;
        _dialogs = dialogs;
        _trace = trace;
        _requestMoveTarget = requestMoveTarget;

        // The auth panel can save/delete accounts while this window is open; keep the list live.
        _accounts.CollectionChanged += (_, _) => RefreshAccountNames();

        ConnectCommand = new RelayCommand(() => _ = RunAsync(ConnectAsync), () => !IsBusy && SelectedProvider is not null && SelectedAccountName is not null);
        RefreshCommand = new RelayCommand(() => _ = RunAsync(() => ListAsync(CurrentFolderId)), () => !IsBusy && _session is not null);
        SearchCommand = new RelayCommand(() => _ = RunAsync(SearchAsync), () => !IsBusy && _session is not null);
        UploadCommand = new RelayCommand(() => _ = RunAsync(UploadAsync), () => !IsBusy && _session is not null);
        DownloadCommand = new RelayCommand(() => _ = RunAsync(DownloadAsync),
            () => !IsBusy && _session is not null && EffectiveSelection.Any(i => !i.File.IsFolder));
        NewFolderCommand = new RelayCommand(() => _ = RunAsync(NewFolderAsync), () => !IsBusy && _session is not null && !string.IsNullOrWhiteSpace(NewFolderName));
        DeleteCommand = new RelayCommand(() => _ = RunAsync(DeleteAsync),
            () => !IsBusy && _session is not null && EffectiveSelection.Count > 0);
        MoveCommand = new RelayCommand(() => _ = RunAsync(MoveAsync),
            () => !IsBusy && _session is not null && EffectiveSelection.Count > 0);
        OpenFolderCommand = new RelayCommand<NetdiskItemViewModel>(item => _ = RunAsync(() => OpenFolderAsync(item)), item => item is { File.IsFolder: true });
        NavigateToCommand = new RelayCommand<NetdiskBreadcrumbItem>(item => _ = RunAsync(() => NavigateToAsync(item)));

        SelectedProvider = Providers.FirstOrDefault();
        RebuildBreadcrumbs();
    }

    public IReadOnlyList<INetdiskProvider> Providers => _catalog.All;

    public ObservableCollection<string> AccountNames { get; } = new();

    public ObservableCollection<NetdiskItemViewModel> Items { get; } = new();

    public ObservableCollection<NetdiskBreadcrumbItem> Breadcrumbs { get; } = new();

    /// <summary>Storage spaces of the connected account (阿里云盘: 备份盘/资源库/相册).</summary>
    public ObservableCollection<NetdiskDrive> Drives { get; } = new();

    /// <summary>Whether the drive selector is worth showing (more than one storage space).</summary>
    public bool HasMultipleDrives => Drives.Count > 1;

    /// <summary>Picking a drive switches the session over and reloads its root listing.</summary>
    public NetdiskDrive? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (SetProperty(ref _selectedDrive, value) && !_suppressDriveSwitch)
            {
                _ = RunAsync(SwitchDriveAsync);
            }
        }
    }

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

    /// <summary>Rows currently selected in the grid (multi-select).</summary>
    public IReadOnlyList<NetdiskItemViewModel> SelectedItems => _selectedItems;

    /// <summary>Status-line suffix such as "已选 3 项".</summary>
    public string SelectionText => _selectedItems.Count == 0 ? string.Empty : $"已选 {_selectedItems.Count} 项";

    /// <summary>Syncs the grid's extended selection into the view model.</summary>
    public void SetSelection(IEnumerable<NetdiskItemViewModel> items)
    {
        _selectedItems = items.Distinct().ToList();
        OnPropertyChanged(nameof(SelectedItems));
        OnPropertyChanged(nameof(SelectionText));
        DownloadCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
        MoveCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Falls back to the single SelectedItem when the grid has not reported a selection (tests,
    /// keyboard use), so one-item operations keep working exactly as before.
    /// </summary>
    private IReadOnlyList<NetdiskItemViewModel> EffectiveSelection =>
        _selectedItems.Count > 0
            ? _selectedItems
            : SelectedItem is { } one
                ? new[] { one }
                : Array.Empty<NetdiskItemViewModel>();

    /// <summary>Current folder as "/a/b"; the move dialog starts from here.</summary>
    public string CurrentPathText => "/" + string.Join("/", _path.Skip(1).Select(p => p.Name));

    public string NewFolderName { get => _newFolderName; set => SetProperty(ref _newFolderName, value); }

    /// <summary>Keyword for the drive search box; empty means "reload current folder".</summary>
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }

    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    public string QuotaText { get => _quotaText; set => SetProperty(ref _quotaText, value); }

    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

    public int ProgressPercent { get => _progressPercent; set => SetProperty(ref _progressPercent, value); }

    public RelayCommand ConnectCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand SearchCommand { get; }

    public RelayCommand UploadCommand { get; }

    public RelayCommand DownloadCommand { get; }

    public RelayCommand NewFolderCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand MoveCommand { get; }

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
        RebuildDriveOptions();
        StatusText = $"已连接：{_session.Account.DisplayName}";
        await ListAsync(string.Empty);
        await RefreshQuotaAsync();
    }

    /// <summary>Fills the drive selector from the session; multi-drive products offer switching.</summary>
    private void RebuildDriveOptions()
    {
        Drives.Clear();
        foreach (var drive in _session?.Drives ?? (IReadOnlyList<NetdiskDrive>)Array.Empty<NetdiskDrive>())
        {
            Drives.Add(drive);
        }

        _suppressDriveSwitch = true;
        SelectedDrive = Drives.FirstOrDefault(d => d.Id == _session?.CurrentDriveId) ?? Drives.FirstOrDefault();
        _suppressDriveSwitch = false;
        OnPropertyChanged(nameof(HasMultipleDrives));
    }

    /// <summary>Points the session at another drive and reloads the listing from its root.</summary>
    private async Task SwitchDriveAsync()
    {
        if (_session is null || SelectedDrive is null ||
            SelectedDrive.Id == _session.CurrentDriveId)
        {
            return;
        }

        await _session.SwitchDriveAsync(SelectedDrive.Id);
        _path.Clear();
        _path.Add((string.Empty, "根目录"));
        StatusText = $"已切换到「{SelectedDrive.Name}」。";
        await ListAsync(string.Empty);
    }

    private async Task ListAsync(string folderId)
    {
        var session = _session ?? throw new InvalidOperationException("尚未连接网盘。");
        var page = await session.ListAsync(folderId);
        FillItems(page);
        RebuildBreadcrumbs();
    }

    /// <summary>Searches the current drive by name; empty keyword reloads the current folder.</summary>
    private async Task SearchAsync()
    {
        if (_session is null)
        {
            return;
        }

        var keyword = SearchText?.Trim() ?? string.Empty;
        if (keyword.Length == 0)
        {
            await ListAsync(_path[^1].Id);
            return;
        }

        StatusText = $"正在搜索「{keyword}」…";
        var page = await _session.SearchAsync(keyword);
        var locations = await ResolveLocationsAsync(page.Items);
        FillItems(page, locations);
        StatusText = $"搜索「{keyword}」：{page.Items.Count} 个结果（当前盘）。双击文件夹可进入。";
    }

    /// <summary>
    /// Names the parent folders behind search results so same-named files are tellable apart.
    /// Folder ids are resolved once per session and reused across searches.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ResolveLocationsAsync(IReadOnlyList<NetdiskFile> items)
    {
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parentId in items.Select(f => f.ParentId).Where(p => !string.IsNullOrEmpty(p)).Distinct())
        {
            if (_locationCache.TryGetValue(parentId!, out var cached))
            {
                known[parentId!] = cached;
                continue;
            }

            var name = await _session!.GetNodeNameAsync(parentId!);
            if (!string.IsNullOrEmpty(name))
            {
                _locationCache[parentId!] = name!;
                known[parentId!] = name!;
            }
        }

        return known;
    }

    private void FillItems(NetdiskPage page, IReadOnlyDictionary<string, string>? locations = null)
    {
        Items.Clear();
        foreach (var file in page.Items.OrderByDescending(f => f.IsFolder).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var location = locations is not null && file.ParentId is not null &&
                           locations.TryGetValue(file.ParentId, out var name)
                ? name
                : null;
            Items.Add(new NetdiskItemViewModel(file, location));
        }

        SetSelection(Array.Empty<NetdiskItemViewModel>());
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

        OnPropertyChanged(nameof(CurrentPathText));
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
        var files = EffectiveSelection.Where(i => !i.File.IsFolder).ToList();
        if (files.Count == 0)
        {
            StatusText = "请选择要下载的文件（文件夹请进入后再下载）。";
            return;
        }

        string? singlePath = null;
        string? destinationDir = null;
        if (files.Count == 1)
        {
            var dialog = new SaveFileDialog { FileName = files[0].File.Name, Title = "下载到…" };
            if (dialog.ShowDialog() != true) return;
            singlePath = dialog.FileName;
        }
        else
        {
            var dialog = new OpenFolderDialog { Title = "选择下载目录" };
            if (dialog.ShowDialog() != true) return;
            destinationDir = dialog.FolderName;
        }

        for (var i = 0; i < files.Count; i++)
        {
            var item = files[i];
            string target = singlePath ?? UniquePath(destinationDir!, item.File.Name);
            StatusText = $"正在下载 {item.File.Name}（{i + 1}/{files.Count}）…";
            var progress = new Progress<NetdiskTransferProgress>(p =>
            {
                if (p.TotalBytes > 0)
                {
                    ProgressPercent = (int)(p.BytesTransferred * 100 / p.TotalBytes.Value);
                }
            });
            await session.DownloadAsync(item.File.Id, target, progress);
            AppendFileLog("下载", target);
        }

        StatusText = files.Count == 1
            ? $"已下载到 {singlePath}"
            : $"已下载 {files.Count} 个文件到 {destinationDir}";
    }

    private static string UniquePath(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        if (!File.Exists(path)) return path;

        string baseName = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        for (var i = 1; ; i++)
        {
            path = Path.Combine(directory, $"{baseName} ({i}){extension}");
            if (!File.Exists(path)) return path;
        }
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
        var items = EffectiveSelection;
        if (items.Count == 0)
        {
            StatusText = "请先选择要删除的文件或文件夹。";
            return;
        }

        string message = items.Count == 1
            ? $"确定把「{items[0].File.Name}」移入回收站吗？"
            : $"确定把选中的 {items.Count} 项移入回收站吗？";
        if (!_dialogs.Confirm(message, "删除确认"))
        {
            return;
        }

        await session.DeleteAsync(items.Select(i => i.File.Id).ToList());
        StatusText = $"已把 {items.Count} 项移入回收站。";
        await ListAsync(CurrentFolderId);
        SetSelection(Array.Empty<NetdiskItemViewModel>());
    }

    /// <summary>Moves the selected rows into a typed remote folder (created when missing).</summary>
    private async Task MoveAsync()
    {
        var session = _session ?? throw new InvalidOperationException("尚未连接网盘。");
        var items = EffectiveSelection;
        if (items.Count == 0)
        {
            StatusText = "请先选择要移动的文件或文件夹。";
            return;
        }

        if (_requestMoveTarget is null)
        {
            StatusText = "当前窗口不支持移动。";
            return;
        }

        string? target = _requestMoveTarget(CurrentPathText);
        if (string.IsNullOrWhiteSpace(target)) return;
        target = target.Trim();

        StatusText = $"正在移动 {items.Count} 项到 {target}…";
        string targetId = await session.EnsureFolderAsync(target);
        if (string.Equals(targetId, CurrentFolderId, StringComparison.Ordinal))
        {
            StatusText = "目标目录就是当前目录，无需移动。";
            return;
        }

        await session.MoveAsync(items.Select(i => i.File.Id).ToList(), targetId);
        StatusText = $"已移动 {items.Count} 项到 {target}。";
        await ListAsync(CurrentFolderId);
        SetSelection(Array.Empty<NetdiskItemViewModel>());
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
