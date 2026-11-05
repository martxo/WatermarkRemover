using System.IO;
using WatermarkRemover.Core.Features.Search;

namespace WatermarkRemover.App.ViewModels;

/// <summary>One row in the 快速搜索 result list; formats raw index data for display.</summary>
public sealed class SearchResultItemViewModel : ObservableObject
{
    private bool _isChecked;

    public SearchResultItemViewModel(
        FileSearchResult result,
        Action<SearchResultItemViewModel>? open = null,
        Action<SearchResultItemViewModel>? openFolder = null,
        Action<SearchResultItemViewModel>? copyPath = null)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        // The page owns the open/reveal/copy logic; the row only forwards its gestures, so the
        // template can bind them without reaching up the visual tree.
        OpenCommand = new RelayCommand(() => open?.Invoke(this), () => open is not null);
        OpenFolderCommand = new RelayCommand(() => openFolder?.Invoke(this), () => openFolder is not null);
        CopyPathCommand = new RelayCommand(() => copyPath?.Invoke(this), () => copyPath is not null);
    }

    public FileSearchResult Result { get; }

    public RelayCommand OpenCommand { get; }

    /// <summary>Clicking the path line opens its containing folder (the app-wide gesture).</summary>
    public RelayCommand OpenFolderCommand { get; }

    /// <summary>The copy icon after the path; copies the full path to the clipboard.</summary>
    public RelayCommand CopyPathCommand { get; }

    /// <summary>Row checkbox for the batch actions (delete appears once something is checked).</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set => SetProperty(ref _isChecked, value);
    }

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
