using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WatermarkRemover.App.ViewModels;

/// <summary>
/// One tile in the preview's thumbnail strip: the small page image, its "currently shown" ring
/// and the actions the strip offers — click to enlarge that page in the main canvas, the corner
/// rotate button to turn it 90° clockwise, and the delete button under it to drop the page.
/// Both edits save into the PDF immediately.
/// </summary>
public sealed class PdfPreviewThumbnailViewModel : ObservableObject
{
    private readonly PdfPreviewViewModel _owner;
    private ImageSource? _image;
    private bool _isSelected;
    private double _aspect = PdfPreviewViewModel.BaseTileWidth / PdfPreviewViewModel.BaseTileHeight;

    public PdfPreviewThumbnailViewModel(PdfPreviewViewModel owner, int index)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Index = index;
        SelectCommand = new RelayCommand(() => _owner.SelectPage(Index));
        RotateCommand = new RelayCommand(() => _ = _owner.RotatePageAsync(Index), () => CanRotate);
        DeleteCommand = new RelayCommand(() => _ = _owner.DeletePageAsync(Index), () => CanDelete);
    }

    /// <summary>Zero-based page this tile stands for. Shifts when pages are inserted earlier in
    /// the document; commands read it at execute time, so one notification keeps clicks, the
    /// corner actions and the label in step.</summary>
    public int Index { get; private set; }

    /// <summary>Human page number painted on the tile.</summary>
    public string PageLabel => (Index + 1).ToString();

    /// <summary>
    /// Tile width for THIS page: the strip keeps one uniform row height, so each tile's width
    /// follows its own page's aspect. A landscape page (scanned spreads live with /Rotate 270,
    /// which the renderer applies) grows wider instead of letterboxing inside a portrait box —
    /// mixing orientations used to look broken. Falls back to the shared portrait ratio until
    /// the first bitmap arrives.
    /// </summary>
    public double TileWidth => Math.Round(_owner.TileHeight * _aspect);

    public ImageSource? Image
    {
        get => _image;
        private set => SetProperty(ref _image, value);
    }

    /// <summary>True while the main canvas shows this page; the view paints the accent ring.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Page edits are single-flight: one write at a time, never on a closed view. Deleting is
    /// further limited to documents that will keep at least one page — a zero-page PDF is not
    /// a valid document and no reader can open it. Public so the view can bind visuals to them.
    /// </summary>
    public bool CanRotate => !_owner.IsRotating;

    public bool CanDelete => !_owner.IsRotating && _owner.PageCount > 1;

    public RelayCommand SelectCommand { get; }

    public RelayCommand RotateCommand { get; }

    public RelayCommand DeleteCommand { get; }

    /// <summary>Owner side: an insert earlier in the document moved this page — re-point the tile
    /// in place so its image, ring and commands survive untouched. Rebuilding the tile instead is
    /// what made the whole strip blank and repaint after every insert.</summary>
    internal void SetIndex(int index)
    {
        if (Index == index) return;
        Index = index;
        OnPropertyChanged(nameof(Index));
        OnPropertyChanged(nameof(PageLabel));
    }

    /// <summary>Owner side: pushes a freshly rendered tile image into the view and re-derives
    /// this tile's width from the bitmap's true pixel aspect.</summary>
    internal void SetImage(ImageSource? image)
    {
        Image = image;
        if (image is BitmapSource bmp && bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
            _aspect = (double)bmp.PixelWidth / bmp.PixelHeight;
        OnPropertyChanged(nameof(TileWidth));
    }

    /// <summary>Owner side: zoom changed the shared row height — re-measure this tile.</summary>
    internal void RefreshTileWidth() => OnPropertyChanged(nameof(TileWidth));

    /// <summary>Owner side: re-evaluates CanRotate / CanDelete (the single-flight lock changed).</summary>
    internal void RefreshCommands()
    {
        RotateCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }
}
