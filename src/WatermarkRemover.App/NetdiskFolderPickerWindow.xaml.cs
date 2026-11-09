using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using WatermarkRemover.Core.Netdisk;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.App;

/// <summary>
/// Cloud folder picker for the settings page's 「快速上传位置 → 浏览…」 button: pick an account
/// (auto-connected), browse into a folder, confirm. Returns the same notation the setting speaks —
/// drive name + "/a/b" path (root = empty) — so the result is written back verbatim.
/// </summary>
public partial class NetdiskFolderPickerWindow : Window, INotifyPropertyChanged
{
    /// <summary>One row of the account selector; keeps the saved state for connecting.</summary>
    public sealed record AccountOption(string Label, NetdiskAccountState State);

    private readonly NetdiskProviderCatalog _catalog;
    private readonly Action<string, Exception?> _trace;
    private readonly List<(string Id, string Name)> _path = new() { (string.Empty, "根目录") };
    private INetdiskSession? _session;
    private bool _loading;
    private NetdiskDrive? _selectedDrive;
    private AccountOption? _selectedAccount;
    private string _currentPathText = "根目录";
    private string _statusText = "正在连接…";

    private NetdiskFolderPickerWindow(
        NetdiskProviderCatalog catalog,
        IReadOnlyCollection<NetdiskAccountState> accounts,
        string? lastProviderId,
        string? lastAccountName,
        Action<string, Exception?> trace)
    {
        _catalog = catalog;
        _trace = trace;
        InitializeComponent();
        DataContext = this;

        AccountOptions = accounts
            .Select(a =>
            {
                var provider = _catalog.All.FirstOrDefault(p => p.Id == a.ProviderId);
                return new AccountOption($"{provider?.DisplayName ?? a.ProviderId} · {a.Name}", a);
            })
            .ToList();
        _selectedAccount = AccountOptions.FirstOrDefault(o =>
                              o.State.ProviderId == lastProviderId && o.State.Name == lastAccountName)
                          ?? AccountOptions.FirstOrDefault();

        Loaded += (_, _) => _ = RunAsync(ConnectAsync);
#pragma warning disable CS4014 // Closed handler: fire-and-forget dispose on window teardown
        Closed += async (_, _) =>
        {
            if (_session is not null) await _session.DisposeAsync();
        };
#pragma warning restore CS4014
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Picked location, filled only when the user confirms.</summary>
    public (string DriveName, string RemotePath)? Result { get; private set; }

    public IReadOnlyList<AccountOption> AccountOptions { get; }

    public ObservableCollection<NetdiskFile> Folders { get; } = new();

    public AccountOption? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (SetProperty(ref _selectedAccount, value) && !_loading)
            {
                _ = RunAsync(ConnectAsync);
            }
        }
    }

    public ObservableCollection<NetdiskDrive> Drives { get; } = new();

    public NetdiskDrive? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (SetProperty(ref _selectedDrive, value) && !_loading &&
                _session is not null && value is not null && value.Id != _session.CurrentDriveId)
            {
                _ = RunAsync(async () =>
                {
                    await _session.SwitchDriveAsync(value.Id);
                    _path.Clear();
                    _path.Add((string.Empty, "根目录"));
                    await ListFoldersAsync(string.Empty);
                });
            }
        }
    }

    public Visibility HasDrivesVisibility =>
        Drives.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    public string CurrentPathText
    {
        get => _currentPathText;
        private set => SetProperty(ref _currentPathText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>Shows the picker and returns the picked (drive, path), or null when cancelled.</summary>
    public static (string DriveName, string RemotePath)? Prompt(
        Window? owner,
        NetdiskProviderCatalog catalog,
        IReadOnlyCollection<NetdiskAccountState> accounts,
        string? lastProviderId,
        string? lastAccountName,
        Action<string, Exception?> trace)
    {
        var dialog = new NetdiskFolderPickerWindow(catalog, accounts, lastProviderId, lastAccountName, trace);
        if (owner is { IsLoaded: true }) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Result;
    }

    private async Task ConnectAsync()
    {
        var option = _selectedAccount;
        var provider = _catalog.All.FirstOrDefault(p => p.Id == option?.State.ProviderId);
        if (option is null || provider is null)
        {
            StatusText = "没有可连接的账号。";
            return;
        }

        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }

        StatusText = $"正在连接 {option.Label}…";
        Folders.Clear();
        Drives.Clear();
        RaisePropertyChanged(nameof(HasDrivesVisibility));
        _path.Clear();
        _path.Add((string.Empty, "根目录"));

        _session = await provider.ConnectAsync(option.State.ToCredentials());
        foreach (var drive in _session.Drives)
        {
            Drives.Add(drive);
        }

        _loading = true;
        try
        {
            _selectedDrive = Drives.FirstOrDefault(d => d.Id == _session.CurrentDriveId) ?? Drives.FirstOrDefault();
            RaisePropertyChanged(nameof(SelectedDrive));
        }
        finally
        {
            _loading = false;
        }

        RaisePropertyChanged(nameof(HasDrivesVisibility));
        StatusText = "已连接。双击进入文件夹，选好后点「使用此目录」。";
        await ListFoldersAsync(string.Empty);
    }

    private async Task ListFoldersAsync(string folderId)
    {
        if (_session is null) return;

        Folders.Clear();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await _session.ListAsync(folderId, cancellationToken: default);
            foreach (var file in page.Items.Where(f => f.IsFolder)
                         .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                Folders.Add(file);
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null && ++pages < 20);

        CurrentPathText = NetdiskViewModel.BuildRemotePath(_path) is { Length: > 0 } path ? path : "根目录";
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (_loading) return;
        _loading = true;
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            _trace($"选择上传目录失败：{ex.Message}", ex);
            StatusText = $"操作失败：{ex.Message}";
        }
        finally
        {
            _loading = false;
        }
    }

    private async Task EnterAsync(NetdiskFile? folder)
    {
        if (folder is not { IsFolder: true }) return;

        _path.Add((folder.Id, folder.Name));
        await ListFoldersAsync(folder.Id);
    }

    private void OnFolderDoubleClick(object sender, MouseButtonEventArgs e) =>
        _ = RunAsync(() => EnterAsync(FolderList.SelectedItem as NetdiskFile));

    private void OnFolderListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _ = RunAsync(() => EnterAsync(FolderList.SelectedItem as NetdiskFile));
            e.Handled = true;
        }
    }

    private void OnUpClick(object sender, RoutedEventArgs e)
    {
        if (_path.Count <= 1) return;

        _ = RunAsync(async () =>
        {
            _path.RemoveAt(_path.Count - 1);
            await ListFoldersAsync(_path[^1].Id);
        });
    }

    private void OnPickClick(object sender, RoutedEventArgs e)
    {
        Result = (SelectedDrive?.Name ?? string.Empty, NetdiskViewModel.BuildRemotePath(_path));
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
        return true;
    }

    /// <summary>Not one of DependencyObject.OnPropertyChanged — a plain INPC raise.</summary>
    private void RaisePropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
