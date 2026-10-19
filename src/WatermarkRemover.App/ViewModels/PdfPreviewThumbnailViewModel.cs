using System.Windows.Media;

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

    public PdfPreviewThumbnailViewModel(PdfPreviewViewModel owner, int index)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        Index = index;
        SelectCommand = new RelayCommand(() => _owner.SelectPage(Index));
        RotateCommand = new RelayCommand(() => _ = _owner.RotatePageAsync(Index), () => CanRotate);
        DeleteCommand = new RelayCommand(() => _ = _owner.DeletePageAsync(Index), () => CanDelete);
    }

    /// <summary>Zero-based page this tile stands for.</summary>
    public int Index { get; }

    /// <summary>Human page number painted on the tile.</summary>
    public string PageLabel => (Index + 1).ToString();

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

    /// <summary>Owner side: pushes a freshly rendered tile image into the view.</summary>
    internal void SetImage(ImageSource? image) => Image = image;

    /// <summary>Owner side: re-evaluates CanRotate / CanDelete (the single-flight lock changed).</summary>
    internal void RefreshCommands()
    {
        RotateCommand.RaiseCanExecuteChanged();
        DeleteCommand.RaiseCanExecuteChanged();
    }
}
