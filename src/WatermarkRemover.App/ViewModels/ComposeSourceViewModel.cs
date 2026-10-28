using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;
using WatermarkRemover.App.Imaging;

namespace WatermarkRemover.App.ViewModels;

/// <summary>One row move request produced by the gallery drag-reorder behaviour.</summary>
public sealed record ComposePageMove(int From, int To);

/// <summary>
/// One downloaded link page in the compose gallery: the original bytes, a rotation state that
/// re-encodes from the original, a frozen thumbnail for the grid, and a checkbox state the owner
/// observes to refresh the preview and command availability.
/// </summary>
public sealed class ComposeSourceViewModel : ObservableObject
{
    private readonly byte[] _originalBytes;
    private byte[] _bytes;
    private string _label;
    private int _width;
    private int _height;
    private int _quarterTurns;
    private ImageSource? _thumbnail;
    private bool _isSelected = true;

    public ComposeSourceViewModel(int number, byte[] bytes, string label)
    {
        Number = number;
        _originalBytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        _bytes = bytes;
        _label = label;
        (_width, _height) = ReadSize(bytes);
        _thumbnail = CreateThumbnail(bytes);

        RotateLeftCommand = new RelayCommand(() => Rotate(-1));
        RotateRightCommand = new RelayCommand(() => Rotate(1));
    }

    public int Number { get; }

    /// <summary>Display label such as "第 2 张"; renumbered when the user drags pages around.</summary>
    public string Label
    {
        get => _label;
        private set => SetProperty(ref _label, value);
    }

    /// <summary>Pixel width after the current rotation.</summary>
    public int Width
    {
        get => _width;
        private set => SetProperty(ref _width, value);
    }

    /// <summary>Pixel height after the current rotation.</summary>
    public int Height
    {
        get => _height;
        private set => SetProperty(ref _height, value);
    }

    /// <summary>The page image with the current rotation applied (original bytes at 0°).</summary>
    public byte[] Bytes
    {
        get => _bytes;
        private set => SetProperty(ref _bytes, value);
    }

    /// <summary>Frozen 160 px-wide preview for the gallery; null when the bytes are not an image.</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set => SetProperty(ref _thumbnail, value);
    }

    public RelayCommand RotateLeftCommand { get; }

    public RelayCommand RotateRightCommand { get; }

    /// <summary>Raised on the UI thread after a rotation; the owner re-renders the preview.</summary>
    public event EventHandler? Rotated;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Raised on the UI thread when the checkbox changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Renumbers the gallery after a drag or a partial merge.</summary>
    public void SetLabel(string label) => Label = label;

    /// <summary>
    /// Rotates 90°; the bytes are always rebuilt from the original snapshot, so four turns are
    /// lossless and switching direction never compounds encoding artefacts.
    /// </summary>
    private void Rotate(int delta)
    {
        _quarterTurns = ((_quarterTurns + delta) % 4 + 4) % 4;
        Bytes = _quarterTurns == 0
            ? _originalBytes
            : ImageRotation.Rotate(_originalBytes, _quarterTurns);
        (int width, int height) = ReadSize(Bytes);
        Width = width;
        Height = height;
        Thumbnail = CreateThumbnail(Bytes);
        Rotated?.Invoke(this, EventArgs.Empty);
    }

    private static (int Width, int Height) ReadSize(byte[] bytes)
    {
        try
        {
            using SKData data = SKData.CreateCopy(bytes);
            using SKCodec? codec = SKCodec.Create(data);
            return codec is null ? (0, 0) : (codec.Info.Width, codec.Info.Height);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static ImageSource? CreateThumbnail(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = new MemoryStream(bytes, writable: false);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.EndInit();
            image.Freeze(); // decoding happens on a worker thread; frozen is safe to bind
            return image;
        }
        catch
        {
            return null;
        }
    }
}
