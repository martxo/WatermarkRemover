using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features.Search;

namespace WatermarkRemover.Tests.App;

/// <summary>
/// The 快速搜索 page is a thin shell over an <see cref="IFileSearchProvider"/>: the real one asks
/// a running Everything client, so tests drive a fake and pin down the page's own contract —
/// empty queries never reach the provider, hits are formatted, and an absent client degrades to
/// an install hint instead of an exception.
/// </summary>
public class SearchPanelTests
{
    private sealed class FakeProvider : IFileSearchProvider
    {
        public List<(string Query, int Max)> Calls { get; } = new();

        public List<FileSearchResult> Results { get; } = new();

        public string DisplayName => "Everything";

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
        Assert.Null(vm.SelectedResult);

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
        Assert.Contains("未检测到 Everything", vm.StatusText);
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
    public void Selection_gates_the_action_commands_and_reveal_uses_the_shell()
    {
        var (vm, _, shell) = NewPanel();
        vm.Results.Add(new SearchResultItemViewModel(
            new FileSearchResult(@"C:\photos\a.png", false, 10, null)));

        Assert.False(vm.OpenCommand.CanExecute(null));
        Assert.False(vm.OpenFolderCommand.CanExecute(null));

        vm.SelectedResult = vm.Results[0];

        Assert.True(vm.OpenCommand.CanExecute(null));
        Assert.True(vm.OpenFolderCommand.CanExecute(null));

        vm.OpenFolderCommand.Execute(null);

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
