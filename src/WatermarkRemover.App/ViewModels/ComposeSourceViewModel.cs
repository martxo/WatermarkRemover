using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace WatermarkRemover.App.ViewModels;

/// <summary>
/// One downloaded link page in the compose gallery: the original bytes (so composing never
/// re-downloads), a frozen thumbnail for the grid, and a checkbox state the owner observes to
/// refresh the preview and command availability.
/// </summary>
public sealed class ComposeSourceViewModel : ObservableObject
{
    private bool _isSelected = true;

    public ComposeSourceViewModel(int number, byte[] bytes, string label)
    {
        Number = number;
        Bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        Label = label;
        (Width, Height) = ReadSize(bytes);
        Thumbnail = CreateThumbnail(bytes);
    }

    public int Number { get; }

    public string Label { get; }

    /// <summary>Pixel size read from the header only (no full decode) — the hint the pack
    /// chooser uses to recognise a bank-card/ID-card shape.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>The untouched page image as downloaded.</summary>
    public byte[] Bytes { get; }

    /// <summary>Frozen 160 px-wide preview for the gallery; null when the bytes are not an image.</summary>
    public ImageSource? Thumbnail { get; }

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
