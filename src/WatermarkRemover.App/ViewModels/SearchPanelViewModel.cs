using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Features.Search;
using WatermarkRemover.Index;
using WatermarkRemover.Index.Contracts;

namespace WatermarkRemover.App.ViewModels;

/// <summary>
/// The 快速搜索 page: debounced queries against the file-name index, plus the three actions that
/// make a hit useful (open it, reveal it, copy its path). The page owns no index itself — when the
/// configured provider is unavailable it shows an install hint and stays inert.
/// </summary>
public sealed class SearchPanelViewModel : ObservableObject
{
    private const int MaxResults = 200;

    private readonly IFileSearchProvider _provider;
    private readonly IIndexSetup? _setup;
    private readonly IShellOpenService _shell;
    private readonly IDialogService _dialogs;
    private readonly IRecycleBinService _recycleBin;
    private CancellationTokenSource? _debounceCts;
    private string _searchText = string.Empty;
    private string _statusText = string.Empty;
    private bool _isSearching;
    private bool _isAvailable;
    private bool _isInstalling;
    private bool _availabilityChecked;
    private SearchResultItemViewModel? _selectedResult;

    public SearchPanelViewModel(
        IFileSearchProvider provider,
        IShellOpenService shell,
        IDialogService? dialogs = null,
        IRecycleBinService? recycleBin = null)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _dialogs = dialogs ?? DialogService.Default;
        _recycleBin = recycleBin ?? RecycleBinService.Default;
        _setup = provider as IIndexSetup;

        SearchCommand = new RelayCommand(() => _ = RunSearchAsync(), () => !IsSearching);
        OpenCommand = new RelayCommand(() => OpenSelected(SelectedResult), () => SelectedResult is not null);
        OpenFolderCommand = new RelayCommand(RevealSelected, () => SelectedResult is not null);
        CopyPathCommand = new RelayCommand(CopySelectedPath, () => SelectedResult is not null);
        DeleteCommand = new RelayCommand(DeleteSelected, () => SelectedResult is not null);
        InstallProviderCommand = new RelayCommand(() => _ = InstallAsync(), () => !IsInstalling);

        _isAvailable = _provider.IsAvailable;
        _statusText = _isAvailable ? AvailableStatus() : UnavailableStatus();

        // The pipe is the only truth; the synchronous IsAvailable above is just an initial hint.
        if (provider is IIndexAvailability) _ = RefreshAvailabilityAsync();
        else _availabilityChecked = true;
    }

    /// <summary>Live search waits for typing to settle; tests shorten it to zero.</summary>
    internal TimeSpan DebounceDelay { get; set; } = TimeSpan.FromMilliseconds(280);

    public ObservableCollection<SearchResultItemViewModel> Results { get; } = new();

    public RelayCommand SearchCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand CopyPathCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand InstallProviderCommand { get; }

    public string ProviderName => _provider.DisplayName;

    /// <summary>Bound with UpdateSourceTrigger=PropertyChanged, so every keystroke re-arms the debounce.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value)) _ = DebounceAsync();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set => SetProperty(ref _isSearching, value);
    }

    /// <summary>False shows the install hint instead of an empty list.</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        private set
        {
            if (SetProperty(ref _isAvailable, value))
                OnPropertyChanged(nameof(IsUnavailable));
        }
    }

    public bool IsUnavailable => !IsAvailable;

    /// <summary>
    /// The setup button is visible only when the engine is missing or a newer sidecar is shipped,
    /// and only after the first availability probe — otherwise every visit flickered a "start"
    /// button while the automatic start was still running.
    /// </summary>
    public bool ShowInstallButton =>
        _availabilityChecked && (IsUnavailable || _setup?.NeedsUpdate == true);

    /// <summary>
    /// Text on the setup button. Every variant performs one UAC-authorized install/repair, so the
    /// wording says 授权一次 — "启动索引引擎" read as a no-elevation start and misled users.
    /// </summary>
    public string InstallButtonText
    {
        get
        {
            if (_setup is null) return "启用索引";
            if (_setup.NeedsUpdate) return "更新索引引擎（授权一次）";
            if (_setup.IsInstalled) return "修复索引引擎（授权一次）";
            return "启用全盘索引（授权一次）";
        }
    }

    public bool IsInstalling
    {
        get => _isInstalling;
        private set
        {
            if (SetProperty(ref _isInstalling, value)) InstallProviderCommand.RaiseCanExecuteChanged();
        }
    }

    public SearchResultItemViewModel? SelectedResult
    {
        get => _selectedResult;
        set => SetProperty(ref _selectedResult, value);
    }

    /// <summary>Runs a query immediately, cancelling any pending debounce. Used by Enter and by tests.</summary>
    internal async Task RunSearchAsync(CancellationToken cancellationToken = default)
    {
        _debounceCts?.Cancel();
        await SearchAsync(cancellationToken);
    }

    private async Task DebounceAsync()
    {
        _debounceCts?.Cancel();
        var cts = new CancellationTokenSource();
        _debounceCts = cts;

        try
        {
            if (DebounceDelay > TimeSpan.Zero) await Task.Delay(DebounceDelay, cts.Token);
            await SearchAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke or a manual search.
        }
    }

    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        string query = SearchText.Trim();
        if (query.Length == 0)
        {
            ReplaceResults(Array.Empty<FileSearchResult>());
            StatusText = IsAvailable ? AvailableStatus() : UnavailableStatus();
            return;
        }

        // The engine may have been started (or closed) since the page was built.
        IsAvailable = _provider is IIndexAvailability availability
            ? await availability.CheckAvailableAsync(cancellationToken)
            : _provider.IsAvailable;
        MarkAvailabilityChecked();
        if (!IsAvailable)
        {
            ReplaceResults(Array.Empty<FileSearchResult>());
            StatusText = UnavailableStatus();
            return;
        }

        IsSearching = true;
        StatusText = "正在搜索…";
        try
        {
            IReadOnlyList<FileSearchResult> hits =
                await _provider.SearchAsync(query, MaxResults, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;

            ReplaceResults(hits);
            StatusText = hits.Count == 0 ? "没有匹配的文件" : $"{hits.Count} 项结果";
        }
        catch (OperationCanceledException)
        {
            // A newer query owns the list now.
        }
        catch (Exception ex)
        {
            ReplaceResults(Array.Empty<FileSearchResult>());
            StatusText = "搜索失败：" + ex.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }

    private void ReplaceResults(IReadOnlyList<FileSearchResult> hits)
    {
        Results.Clear();
        foreach (var hit in hits) Results.Add(new SearchResultItemViewModel(hit, OpenSelected));
        SelectedResult = null;
    }

    private void OpenSelected(SearchResultItemViewModel? row)
    {
        if (row is null) return;

        try
        {
            if (row.IsFolder)
            {
                if (!_shell.TryOpenDirectory(row.FullPath, out string? error))
                    StatusText = "打开失败：" + error;
                return;
            }

            Process.Start(new ProcessStartInfo(row.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = "打开失败：" + ex.Message;
        }
    }

    private void RevealSelected()
    {
        if (SelectedResult is null) return;
        if (!_shell.TryOpenDirectory(SelectedResult.Directory, out string? error))
            StatusText = "打开失败：" + error;
    }

    private void CopySelectedPath()
    {
        if (SelectedResult is null) return;

        try
        {
            Clipboard.SetText(SelectedResult.FullPath);
            StatusText = "路径已复制";
        }
        catch (Exception ex)
        {
            StatusText = "复制失败：" + ex.Message;
        }
    }

    /// <summary>
    /// Removes a hit from the list by sending it to the Recycle Bin. Confirm first — the button
    /// sits next to 打开 and a folder takes its whole contents with it. The index host sees the
    /// delete through the USN journal within a second, so no local re-scan is needed.
    /// </summary>
    private void DeleteSelected()
    {
        if (SelectedResult is null) return;
        var row = SelectedResult;

        string detail = row.IsFolder
            ? $"文件夹「{row.Name}」及其中的所有内容都会被移到回收站，之后可在回收站还原。"
            : $"文件「{row.Name}」会被移到回收站，之后可在回收站还原。";
        if (!_dialogs.ConfirmYesNo(detail, "移到回收站")) return;

        if (!_recycleBin.TryDelete(row.FullPath, row.IsFolder, out string? error))
        {
            StatusText = "删除失败：" + error;
            return;
        }

        Results.Remove(row);
        SelectedResult = null;
        StatusText = $"已移到回收站：{row.Name}";
    }

    private async Task RefreshAvailabilityAsync()
    {
        if (_provider is not IIndexAvailability availability) return;

        try
        {
            bool available = await availability.CheckAvailableAsync();
            IsAvailable = available;
            MarkAvailabilityChecked();
            StatusText = available ? AvailableStatus() : UnavailableStatus();
            // Install/upgrade status is derived from the filesystem, so re-read it after a ping.
            OnPropertyChanged(nameof(InstallButtonText));
            OnPropertyChanged(nameof(ShowInstallButton));
        }
        catch (OperationCanceledException)
        {
            // The page is going away; the last status text is still correct.
        }
    }

    private async Task InstallAsync()
    {
        // Only the built-in provider ships an installer; a foreign provider has nothing to set up.
        if (_setup is null) return;

        IsInstalling = true;
        StatusText = "正在请求管理员授权…";
        try
        {
            bool installed = await _setup.InstallAsync();
            if (!installed)
            {
                StatusText = "未获得管理员授权，索引未启用";
                return;
            }

            StatusText = "索引引擎已启动，正在建立索引…";
            await RefreshAvailabilityAsync();
        }
        catch (Exception ex)
        {
            StatusText = "启用失败：" + ex.Message;
        }
        finally
        {
            IsInstalling = false;
        }
    }

    /// <summary>First probe finished: the setup button may now appear or stay hidden.</summary>
    private void MarkAvailabilityChecked()
    {
        if (_availabilityChecked) return;
        _availabilityChecked = true;
        OnPropertyChanged(nameof(ShowInstallButton));
    }

    /// <summary>Status line while the engine answers.</summary>
    private string AvailableStatus()
    {
        IndexStatusDto? status = (_provider as IIndexAvailability)?.LastStatus;
        if (status is null)
            return $"输入文件名即时搜索（{ProviderName}，最多 {MaxResults} 条）";

        if (!string.IsNullOrEmpty(status.Error))
            return status.Error!;

        string counts = $"已索引 {status.Files:N0} 个文件、{status.Folders:N0} 个文件夹";
        return status.Scanning
            ? $"正在建立索引… {counts}"
            : $"索引引擎运行中 · 实时更新 · {counts} · 最多 {MaxResults} 条";
    }

    /// <summary>
    /// Status line when the engine is absent: a setup hint, not an ad for another product. The
    /// wording separates the two things users mixed up — index DATA is always live, while the
    /// engine PROGRAM only needs a one-time authorization when it is missing or too old.
    /// </summary>
    private string UnavailableStatus()
    {
        if (_setup is { NeedsUpdate: true })
            return "索引引擎有新版本：仅引擎程序变化时才需要授权一次，索引数据始终实时更新";
        if (_setup is { IsInstalled: true })
            return "索引引擎未运行：正在自动尝试启动，仍无响应可点击按钮修复";
        if (_setup is not null)
            return $"尚未启用{ProviderName}：授权一次后开机自动运行，实时索引本机 NTFS 磁盘";
        return $"未检测到 {ProviderName}，请安装并保持其在后台运行";
    }
}
