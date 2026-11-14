using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;

namespace WatermarkRemover.App;

/// <summary>
/// PixPin-style pin: one screenshot floats above every window. Drag anywhere to move it,
/// wheel to zoom, double-click/Esc to close, right-click for copy/save. Pins are independent
/// windows, so several screenshots can be compared at once.
/// </summary>
public partial class PinnedImageWindow : Window
{
    private const double MinSide = 80;
    private static int _cascade;

    private readonly string _sourcePath;

    private PinnedImageWindow(string path, BitmapSource image)
    {
        _sourcePath = path;
        InitializeComponent();

        Preview.Source = image;

        Rect work = SystemParameters.WorkArea;
        double maxWidth = work.Width * 0.7;
        double maxHeight = work.Height * 0.7;
        double scale = Math.Min(1, Math.Min(maxWidth / image.PixelWidth, maxHeight / image.PixelHeight));
        Width = Math.Max(MinSide, image.PixelWidth * scale);
        Height = Math.Max(MinSide, image.PixelHeight * scale);

        int offset = _cascade++ % 8;
        Left = work.Left + (work.Width - Width) / 2 + offset * 18;
        Top = work.Top + (work.Height - Height) / 2 + offset * 18;
    }

    /// <summary>Opens a pinned copy of <paramref name="path"/>; null when the image is unreadable.</summary>
    public static PinnedImageWindow? ShowFor(string path)
    {
        try
        {
            // 带回退的加载器：WebP 截图贴图也能开（WPF 无 WebP 编解码器，SkiaSharp 兜底）。
            if (SkiaBitmapBridge.LoadBitmapSource(path) is not { } image) return null;

            var window = new PinnedImageWindow(path, image);
            window.Show();
            window.Activate();
            return window;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"贴图打开失败：{path}（{ex.Message}）");
            return null;
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnFrameMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            Close();
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released before the drag loop started; nothing to move.
        }
    }

    private void OnWindowMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        Rect work = SystemParameters.WorkArea;
        Width = Math.Clamp(Width * factor, MinSide, work.Width * 1.5);
        Height = Math.Clamp(Height * factor, MinSide, work.Height * 1.5);
        e.Handled = true;
    }

    private void OnFrameRightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem("复制图片", CopyImage));
        menu.Items.Add(CreateMenuItem("另存为…", SaveAs));
        menu.Items.Add(CreateMenuItem("关闭", Close));
        menu.PlacementTarget = Frame;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static MenuItem CreateMenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private void CopyImage()
    {
        if (Preview.Source is not BitmapSource image) return;
        try
        {
            AppClipboard.SetImage(image);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"复制贴图失败：{ex.Message}");
        }
    }

    private void SaveAs()
    {
        string extension = Path.GetExtension(_sourcePath);
        var dialog = new SaveFileDialog
        {
            Title = "另存为",
            Filter = extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? "PNG 图片|*.png"
                : "图片文件|*.png;*.jpg;*.jpeg;*.bmp",
            FileName = Path.GetFileName(_sourcePath),
            InitialDirectory = Path.GetDirectoryName(_sourcePath),
        };

        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.Copy(_sourcePath, dialog.FileName, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"贴图另存失败：{ex.Message}");
        }
    }
}
