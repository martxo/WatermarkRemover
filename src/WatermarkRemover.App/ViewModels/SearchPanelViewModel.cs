using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Features.Search;

namespace WatermarkRemover.App.ViewModels;

/// <summary>
/// The 快速搜索 page: debounced queries against the file-name index, plus the three actions that
/// make a hit useful (open it, reveal it, copy its path). The page owns no index itself — when the
/// configured provider is unavailable it shows an install hint and stays inert.
/// </summary>
public sealed class SearchPanelViewModel : ObservableObject
{
    private const int MaxResults = 200;
    private const string ProviderSite = "https://www.voidtools.com/";

    private readonly IFileSearchProvider _provider;
    private readonly IShellOpenService _shell;
    private CancellationTokenSource? _debounceCts;
    private string _searchText = string.Empty;
    private string _statusText = string.Empty;
    private bool _isSearching;
    private bool _isAvailable;
    private SearchResultItemViewModel? _selectedResult;

    public SearchPanelViewModel(IFileSearchProvider provider, IShellOpenService shell)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));

        SearchCommand = new RelayCommand(() => _ = RunSearchAsync(), () => !IsSearching);
        OpenCommand = new RelayCommand(() => OpenSelected(SelectedResult), () => SelectedResult is not null);
        OpenFolderCommand = new RelayCommand(RevealSelected, () => SelectedResult is not null);
        CopyPathCommand = new RelayCommand(CopySelectedPath, () => SelectedResult is not null);
        InstallProviderCommand = new RelayCommand(OpenProviderSite);

        _isAvailable = _provider.IsAvailable;
        _statusText = DefaultStatus(_isAvailable);
    }

    /// <summary>Live search waits for typing to settle; tests shorten it to zero.</summary>
    internal TimeSpan DebounceDelay { get; set; } = TimeSpan.FromMilliseconds(280);

    public ObservableCollection<SearchResultItemViewModel> Results { get; } = new();

    public RelayCommand SearchCommand { get; }

    public RelayCommand OpenCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand CopyPathCommand { get; }

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
            StatusText = DefaultStatus(IsAvailable);
            return;
        }

        // Everything may have been started (or closed) since the page was built.
        IsAvailable = _provider.IsAvailable;
        if (!IsAvailable)
        {
            ReplaceResults(Array.Empty<FileSearchResult>());
            StatusText = $"未检测到 {ProviderName}，请安装并保持其在后台运行";
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

    private static void OpenProviderSite()
    {
        try
        {
            Process.Start(new ProcessStartInfo(ProviderSite) { UseShellExecute = true });
        }
        catch
        {
            // The hint text already carries the address; a failed browser launch is not fatal.
        }
    }

    private string DefaultStatus(bool available) => available
        ? $"输入文件名即时搜索（{ProviderName} 索引，最多 {MaxResults} 条）"
        : $"未检测到 {ProviderName}：安装并保持其在后台运行后可全盘秒搜";
}
