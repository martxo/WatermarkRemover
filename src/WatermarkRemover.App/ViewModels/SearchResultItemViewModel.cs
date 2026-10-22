using System.IO;
using WatermarkRemover.Core.Features.Search;

namespace WatermarkRemover.App.ViewModels;

/// <summary>One row in the 快速搜索 result list; formats raw index data for display.</summary>
public sealed class SearchResultItemViewModel
{
    public SearchResultItemViewModel(FileSearchResult result, Action<SearchResultItemViewModel>? open = null)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        // The page owns the open/reveal logic; the row only forwards the double-click, so the
        // template can bind {Binding OpenCommand} without reaching up the visual tree.
        OpenCommand = new RelayCommand(() => open?.Invoke(this), () => open is not null);
    }

    public FileSearchResult Result { get; }

    public RelayCommand OpenCommand { get; }

    public string FullPath => Result.FullPath;

    public string Name => Result.Name;

    public bool IsFolder => Result.IsFolder;

    public string Directory => Path.GetDirectoryName(Result.FullPath) ?? Result.FullPath;

    /// <summary>Segoe MDL2 Assets: folder or generic file glyph.</summary>
    public string Glyph => IsFolder ? "\uE8B7" : "\uE8A5";

    public string SizeText => IsFolder ? string.Empty : FormatSize(Result.Size);

    public string ModifiedText => Result.ModifiedAt?.ToString("yyyy-MM-dd HH:mm") ?? string.Empty;

    private static string FormatSize(long bytes) => bytes switch
    {
        < 0 => string.Empty,
        < 1024 => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):0.##} GB",
    };
}
