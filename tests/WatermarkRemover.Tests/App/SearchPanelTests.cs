using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features.Search;
using WatermarkRemover.Index;
using WatermarkRemover.Index.Contracts;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// The 快速搜索 page is a thin shell over an <see cref="IFileSearchProvider"/>: the real one talks
/// to the privileged index host, so tests drive a fake and pin down the page's own contract —
/// empty queries never reach the provider, hits are formatted, and an absent engine degrades to
/// an install hint instead of an exception.
/// </summary>
public class SearchPanelTests
{
    private sealed class FakeProvider : IFileSearchProvider
    {
        public List<(string Query, int Max)> Calls { get; } = new();

        public List<FileSearchResult> Results { get; } = new();

        public string DisplayName => "内置索引";

        public bool IsAvailable { get; set; } = true;

        public Task<IReadOnlyList<FileSearchResult>> SearchAsync(
            string query, int maxResults, CancellationToken cancellationToken = default)
        {
            Calls.Add((query, maxResults));
            return Task.FromResult<IReadOnlyList<FileSearchResult>>(Results.ToArray());
        }
    }

    private sealed class FakeShell : IShellOpenService
    {
        public List<string> Opened { get; } = new();

        public bool TryOpenDirectory(string directory, out string? error)
        {
            Opened.Add(directory);
            error = null;
            return true;
        }
    }

    private sealed class FakeDialogs : IDialogService
    {
        public bool Answer { get; set; } = true;

        public List<string> Confirms { get; } = new();

        public bool Confirm(string message, string title)
        {
            Confirms.Add(message);
            return Answer;
        }

        public bool ConfirmYesNo(string message, string title)
        {
            Confirms.Add(message);
            return Answer;
        }

        public void Info(string message, string title) { }

        public void Warn(string message, string title) { }

        public void Error(string message, string title) { }
    }

    private sealed class FakeRecycleBin : IRecycleBinService
    {
        public List<(string Path, bool IsDirectory)> Deleted { get; } = new();

        /// <summary>Non-null makes every delete fail with this message (locked file, bad share).</summary>
        public string? Failure { get; set; }

        public bool TryDelete(string path, bool isDirectory, out string? error)
        {
            if (Failure is not null)
            {
                error = Failure;
                return false;
            }

            Deleted.Add((path, isDirectory));
            error = null;
            return true;
        }
    }

    private sealed class UpdatableProvider(bool needsUpdate = true) : IFileSearchProvider, IIndexAvailability, IIndexSetup
    {
        public string DisplayName => "内置索引";

        public bool IsAvailable => true;

        public bool IsInstalled => true;

        public bool NeedsUpdate { get; private set; } = needsUpdate;

        public string InstalledVersion => "1.16.6";

        public int Installs { get; private set; }

        public IndexStatusDto? LastStatus => new(true, false, 100, 10, new[] { @"C:\" }, null);

        public Task<bool> CheckAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<bool> InstallAsync(CancellationToken cancellationToken = default)
        {
            Installs++;
            NeedsUpdate = false;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<FileSearchResult>> SearchAsync(
            string query, int maxResults, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FileSearchResult>>(Array.Empty<FileSearchResult>());
    }

    private sealed class MissingEngineProvider : IFileSearchProvider, IIndexAvailability, IIndexSetup
    {
        public string DisplayName => "内置索引";

        public bool IsAvailable { get; private set; }

        public bool IsInstalled => false;

        public bool NeedsUpdate => false;

        public string InstalledVersion => string.Empty;

        public int Installs { get; private set; }

        public IndexStatusDto? LastStatus =>
            new(true, false, 100, 10, new[] { @"C:\" }, null);

        public Task<bool> CheckAvailableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(IsAvailable);

        public Task<bool> InstallAsync(CancellationToken cancellationToken = default)
        {
            Installs++;
            IsAvailable = true;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<FileSearchResult>> SearchAsync(
            string query, int maxResults, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FileSearchResult>>(Array.Empty<FileSearchResult>());
    }

    [Fact]
    public async Task Opening_the_page_installs_a_missing_engine_once()
    {
        var provider = new MissingEngineProvider();
        var vm = new SearchPanelViewModel(provider, new FakeShell());

        // First visit: the page starts the one-time elevated install by itself.
        await vm.ActivateAsync();
        Assert.Equal(1, provider.Installs);
        Assert.True(vm.IsAvailable);

        // Later visits must not nag with another UAC prompt.
        await vm.ActivateAsync();
        Assert.Equal(1, provider.Installs);
    }

    [Fact]
    public async Task A_pending_engine_update_is_offered_even_while_the_engine_runs()
    {
        var provider = new UpdatableProvider();
        var vm = new SearchPanelViewModel(provider, new FakeShell());
        for (int i = 0; i < 100 && !vm.ShowInstallButton; i++) await Task.Delay(10);

        Assert.True(vm.IsAvailable);
        Assert.True(vm.ShowInstallButton);
        Assert.Equal("更新索引引擎（授权一次）", vm.InstallButtonText);

        vm.InstallProviderCommand.Execute(null);
        for (int i = 0; i < 200 && vm.ShowInstallButton; i++) await Task.Delay(10);

        Assert.Equal(1, provider.Installs);
        Assert.False(vm.ShowInstallButton);
    }

    [Fact]
    public async Task A_running_up_to_date_engine_stays_button_free()
    {
        var provider = new UpdatableProvider(needsUpdate: false);
        var vm = new SearchPanelViewModel(provider, new FakeShell());

        vm.SearchText = "x";
        await vm.RunSearchAsync();
        Assert.True(vm.IsAvailable);
        Assert.False(vm.ShowInstallButton); // no UAC button in normal use

        vm.SearchText = string.Empty; // empty query shows the engine status line
        await vm.RunSearchAsync();
        Assert.Contains("实时更新", vm.StatusText);
    }

    private static (SearchPanelViewModel Vm, FakeProvider Provider, FakeShell Shell) NewPanel(bool available = true)
    {
        var provider = new FakeProvider { IsAvailable = available };
        var shell = new FakeShell();
        return (new SearchPanelViewModel(provider, shell), provider, shell);
    }

    [Fact]
    public async Task Empty_query_never_reaches_the_provider()
    {
        var (vm, provider, _) = NewPanel();

        vm.SearchText = "   ";
        await vm.RunSearchAsync();

        Assert.Empty(provider.Calls);
        Assert.Empty(vm.Results);
        Assert.False(vm.IsUnavailable);
    }

    [Fact]
    public async Task Hits_are_mapped_into_rows_and_counted_in_the_status()
    {
        var (vm, provider, _) = NewPanel();
        provider.Results.Add(new FileSearchResult(@"C:\photos\a.png", false, 1536, new DateTime(2026, 10, 20, 8, 30, 0)));
        provider.Results.Add(new FileSearchResult(@"C:\photos\album", true, 0, null));

        vm.SearchText = "a";
        await vm.RunSearchAsync();

        Assert.Single(provider.Calls);
        Assert.Equal("a", provider.Calls[0].Query);
        Assert.Equal(200, provider.Calls[0].Max);
        Assert.Equal("2 项结果", vm.StatusText);

        SearchResultItemViewModel file = vm.Results[0];
        Assert.Equal("a.png", file.Name);
        Assert.Equal(@"C:\photos", file.Directory);
        Assert.Equal("1.5 KB", file.SizeText);
        Assert.Equal("2026-10-20 08:30", file.ModifiedText);
        Assert.Equal("\uE8A5", file.Glyph);

        SearchResultItemViewModel folder = vm.Results[1];
        Assert.True(folder.IsFolder);
        Assert.Equal("\uE8B7", folder.Glyph);
        Assert.Equal(string.Empty, folder.SizeText);
        Assert.Equal(string.Empty, folder.ModifiedText);
    }

    [Fact]
    public async Task Missing_client_shows_the_install_hint_and_stays_empty()
    {
        var (vm, provider, _) = NewPanel(available: false);

        Assert.True(vm.IsUnavailable);

        vm.SearchText = "invoice";
        await vm.RunSearchAsync();

        Assert.Empty(provider.Calls);
        Assert.Empty(vm.Results);
        Assert.Contains("未检测到 内置索引", vm.StatusText);
    }

    [Fact]
    public async Task Checking_multiple_hits_and_deleting_asks_once_and_removes_them()
    {
        var provider = new FakeProvider();
        provider.Results.Add(new FileSearchResult(@"C:\photos\a.png", false, 10, null));
        provider.Results.Add(new FileSearchResult(@"C:\photos\b.pdf", false, 20, null));
        var dialogs = new FakeDialogs();
        var bin = new FakeRecycleBin();
        var vm = new SearchPanelViewModel(provider, new FakeShell(), dialogs, bin);

        vm.SearchText = "a";
        await vm.RunSearchAsync();

        Assert.False(vm.DeleteCheckedCommand.CanExecute(null));
        vm.Results[0].IsChecked = true;
        vm.Results[1].IsChecked = true;

        Assert.Equal(2, vm.CheckedCount);
        Assert.True(vm.HasChecked);
        Assert.True(vm.DeleteCheckedCommand.CanExecute(null));

        vm.DeleteCheckedCommand.Execute(null);

        Assert.Single(dialogs.Confirms);
        Assert.Contains("2 项", dialogs.Confirms[0]);
        Assert.Equal(2, bin.Deleted.Count);
        Assert.Empty(vm.Results);
        Assert.False(vm.HasChecked);
        Assert.Equal("已将 2 项移到回收站", vm.StatusText);
    }

    [Fact]
    public async Task A_declined_batch_delete_keeps_every_row_checked_and_touches_nothing()
    {
        var provider = new FakeProvider();
        provider.Results.Add(new FileSearchResult(@"C:\photos\a.png", false, 10, null));
        var dialogs = new FakeDialogs { Answer = false };
        var bin = new FakeRecycleBin();
        var vm = new SearchPanelViewModel(provider, new FakeShell(), dialogs, bin);

        vm.SearchText = "a";
        await vm.RunSearchAsync();
        vm.Results[0].IsChecked = true;

        vm.DeleteCheckedCommand.Execute(null);

        Assert.Single(dialogs.Confirms);
        Assert.Empty(bin.Deleted);
        Assert.Single(vm.Results);
        Assert.True(vm.Results[0].IsChecked);
    }

    [Fact]
    public async Task A_failed_delete_keeps_the_row_and_reports_the_reason()
    {
        var provider = new FakeProvider();
        provider.Results.Add(new FileSearchResult(@"C:\photos", true, 0, null));
        var bin = new FakeRecycleBin { Failure = "拒绝访问。" };
        var vm = new SearchPanelViewModel(provider, new FakeShell(), new FakeDialogs(), bin);

        vm.SearchText = "photos";
        await vm.RunSearchAsync();
        vm.Results[0].IsChecked = true;

        vm.DeleteCheckedCommand.Execute(null);

        Assert.StartsWith("删除失败：", vm.StatusText);
        Assert.Contains("拒绝访问。", vm.StatusText);
        Assert.Single(vm.Results);
    }

    [Fact]
    public async Task Typing_searches_after_the_debounce_without_enter()
    {
        var (vm, provider, _) = NewPanel();
        vm.DebounceDelay = TimeSpan.Zero;

        vm.SearchText = "report";

        for (int i = 0; i < 200 && provider.Calls.Count == 0; i++)
            await Task.Delay(10);

        Assert.Single(provider.Calls);
        Assert.Equal("report", provider.Calls[0].Query);
    }

    [Fact]
    public async Task A_rows_path_command_reveals_its_parent_folder()
    {
        var (vm, provider, shell) = NewPanel();
        provider.Results.Add(new FileSearchResult(@"C:\photos\a.png", false, 10, null));

        vm.SearchText = "a";
        await vm.RunSearchAsync();

        Assert.True(vm.Results[0].OpenFolderCommand.CanExecute(null));
        vm.Results[0].OpenFolderCommand.Execute(null);

        Assert.Equal(new[] { @"C:\photos" }, shell.Opened);
    }

    [Fact]
    public async Task Row_double_click_forwards_to_the_page_open_action()
    {
        var (vm, provider, shell) = NewPanel();
        provider.Results.Add(new FileSearchResult(@"C:\photos\album", true, 0, null));

        vm.SearchText = "album";
        await vm.RunSearchAsync();

        Assert.True(vm.Results[0].OpenCommand.CanExecute(null));
        vm.Results[0].OpenCommand.Execute(null);

        Assert.Equal(new[] { @"C:\photos\album" }, shell.Opened);
    }

    [Theory]
    [InlineData(512L, "512 B")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(3L * 1024 * 1024, "3 MB")]
    [InlineData(2L * 1024 * 1024 * 1024, "2 GB")]
    public void Sizes_are_formatted_for_display(long bytes, string expected)
    {
        var row = new SearchResultItemViewModel(new FileSearchResult(@"C:\a.bin", false, bytes, null));

        Assert.Equal(expected, row.SizeText);
    }
}
