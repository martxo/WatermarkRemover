using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;
using WatermarkRemover.App.Controls;
using WatermarkRemover.App.Editing;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Features.Screenshot;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Imaging.Encoding;
using WatermarkRemover.Imaging.Screenshot;

namespace WatermarkRemover.App;

/// <summary>
/// Markup editor for an already-saved screenshot (Tendedero's long-press action). It reuses the
/// same annotation model/composer as the live overlay, so colours, widths, multi-line text and
/// mosaic match the image produced during normal capture. Saving always writes a NEW file and
/// leaves the original untouched; the file format follows the screenshot export setting
/// (PNG by default, WebP/JPEG selectable in 截图设置).
/// </summary>
public partial class ImageMarkupWindow : Window
{
    private readonly string _sourcePath;
    private readonly SKBitmap _original;
    private readonly Action<string, int, int> _saved;
    private readonly AppSettings _settings;

    // 画布坐标 = 原图像素；粗细/字号按显示缩放换算回图像素（/ _scale）。
    private readonly AnnotationEditSession _edit = new()
    {
        StrokeFloor = 1f,
        MinMarkSpan = 3f,
        MinPenStep = 1f,
        RequireBothSides = false,
    };

    private double _scale = 1;
    private bool _drawing;

    public ImageMarkupWindow(string sourcePath, Action<string, int, int> saved, AppSettings? settings = null)
    {
        _sourcePath = sourcePath;
        _saved = saved ?? throw new ArgumentNullException(nameof(saved));
        _settings = settings ?? new AppSettings();

        SKBitmap? decoded = SKBitmap.Decode(sourcePath);
        if (decoded is null) throw new InvalidOperationException("无法读取图片");
        _original = decoded;

        InitializeComponent();

        PreviewImage.Source = SkiaBitmapBridge.ToBitmapSource(_original);
        WireToolbar();

        _edit.ScaleAt = _ => 1f / (float)_scale;
        _edit.CanPlaceText = point => point.X >= 0 && point.Y >= 0
            && point.X <= _original.Width && point.Y <= _original.Height;
        // 与旧实现一致的通知分工：历史变化才重算合成预览，草稿/样式/文字锚点各自处理。
        _edit.HistoryChanged += (_, _) =>
        {
            RefreshPreview();
            UpdateToolbar();
        };
        _edit.DraftChanged += (_, _) => DrawDraft();
        _edit.StyleChanged += (_, _) =>
        {
            UpdateToolbar();
            UpdateCanvasCursor();
        };
        _edit.TextAnchorChanged += OnTextAnchorChanged;

        Loaded += (_, _) => LayoutStage();
        Viewport.SizeChanged += (_, _) => LayoutStage();
        UpdateToolbar();
    }

    private void WireToolbar()
    {
        Toolbar.ToolClick += (_, tool) => _edit.SelectTool(tool);
        Toolbar.ColorClick += (_, argb) => _edit.SetColor(argb);
        Toolbar.WidthClick += (_, dip) => _edit.SetLineWidth(dip);
        Toolbar.FontSizeClick += (_, size) => _edit.SetTextFontSize(size);
        Toolbar.UndoClick += (_, _) => _edit.Undo();
        Toolbar.RedoClick += (_, _) => _edit.Redo();
        Toolbar.ClearClick += (_, _) => _edit.Clear();
        Editor.Completed += OnTextEditorCompleted;
    }

    /// <summary>文字浮层结束：文本 = 提交；null = 取消。</summary>
    private void OnTextEditorCompleted(object? sender, string? text)
    {
        if (text is null) _edit.CancelText();
        else _edit.CommitText(text);
    }

    // --- Layout ----------------------------------------------------------------

    private void LayoutStage()
    {
        if (Viewport.ActualWidth < 10 || Viewport.ActualHeight < 10) return;

        double availableWidth = Math.Max(40, Viewport.ActualWidth - 28);
        double availableHeight = Math.Max(40, Viewport.ActualHeight - 28);
        _scale = Math.Min(availableWidth / _original.Width, availableHeight / _original.Height);
        _scale = Math.Clamp(_scale, 0.05, 2.0);

        double width = _original.Width * _scale;
        double height = _original.Height * _scale;
        Stage.Width = width;
        Stage.Height = height;
        PreviewImage.Width = width;
        PreviewImage.Height = height;
        DraftLayer.Width = width;
        DraftLayer.Height = height;
        DrawDraft();
    }

    private Point ImagePoint(Point display) => new(display.X / _scale, display.Y / _scale);

    private Point DisplayPoint(Point image) => new(image.X * _scale, image.Y * _scale);

    // --- Preview / annotations -------------------------------------------------

    private void RefreshPreview()
    {
        if (_edit.Annotations.Count == 0)
        {
            PreviewImage.Source = SkiaBitmapBridge.ToBitmapSource(_original);
            return;
        }

        using SKBitmap composed = ScreenshotComposer.Compose(
            _original,
            new SKRectI(0, 0, _original.Width, _original.Height),
            _edit.Annotations);
        PreviewImage.Source = SkiaBitmapBridge.ToBitmapSource(composed);
    }

    private void UpdateCanvasCursor()
    {
        DraftLayer.Cursor = _edit.Tool switch
        {
            ScreenshotTool.Text => Cursors.IBeam,
            ScreenshotTool.None => Cursors.Arrow,
            _ => Cursors.Cross,
        };
    }

    private void UpdateToolbar()
    {
        Toolbar.Sync(
            _edit.Tool,
            _edit.Style.ColorArgb,
            _edit.Style.LineWidthDip,
            _edit.Style.TextFontSizeDip,
            _edit.HasAnnotations,
            _edit.CanRedo,
            _edit.HasAnnotations);
        StatusText.Text = _edit.HasAnnotations ? $"已添加 {_edit.Annotations.Count} 个标注" : "按住 Shift+Enter 换行";
    }

    // --- Canvas drawing --------------------------------------------------------

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Editor.IsActive)
        {
            Editor.Submit();
            e.Handled = true;
            return;
        }

        Point image = ImagePoint(e.GetPosition(DraftLayer));
        if (image.X < 0 || image.Y < 0 || image.X > _original.Width || image.Y > _original.Height) return;
        if (_edit.Tool == ScreenshotTool.None) return;

        // 是否需要拖拽由工具自己声明（文字 = 点击放置），宿主不按 Kind 分支。
        bool needsDrag = AnnotationToolRegistry.Default.Find(_edit.Tool) is { NeedsDrag: true };
        _edit.Begin(image);
        if (needsDrag)
        {
            _drawing = true;
            DraftLayer.CaptureMouse();
        }

        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_drawing) return;

        Point image = ImagePoint(e.GetPosition(DraftLayer));
        _edit.Update(image);
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing) return;

        _drawing = false;
        DraftLayer.ReleaseMouseCapture();
        _edit.Commit();
        e.Handled = true;
    }

    private void DrawDraft()
    {
        if (_edit.Draft is not { } draft)
        {
            DraftLayer.Children.Clear();
            return;
        }

        // 实时预览用显示坐标（图像素 × _scale），粗细按 DIP；观感与旧实现一致。
        DraftShapeRenderer.Render(DraftLayer, draft, DisplayPoint, new DraftShapeRenderer.Options(
            DraftShapeRenderer.MarkColor(_edit.Style.ColorArgb),
            _edit.Style.LineWidthDip,
            Color.FromArgb(24, 0, 0, 0),
            Color.FromRgb(160, 160, 160),
            new DoubleCollection { 3, 2 }));
    }

    // --- Text ------------------------------------------------------------------

    private void OnTextAnchorChanged(object? sender, EventArgs e)
    {
        if (_edit.TextAnchor is not { } anchor)
        {
            if (Editor.IsActive) Editor.Hide();
            return;
        }

        Point display = DisplayPoint(anchor);
        Editor.Start(display, _edit.Style.TextFontSizeDip,
            DraftShapeRenderer.MarkColor(_edit.Style.ColorArgb), Editor.Width, Editor.Height);
    }

    // --- Toolbar actions -------------------------------------------------------

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (Editor.IsActive) Editor.Submit();
        if (_edit.Annotations.Count == 0)
        {
            StatusText.Text = "请先添加标注再保存";
            return;
        }

        try
        {
            using SKBitmap composed = ScreenshotComposer.Compose(
                _original,
                new SKRectI(0, 0, _original.Width, _original.Height),
                _edit.Annotations);

            string directory = Path.GetDirectoryName(_sourcePath) ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(directory);
            string stem = Path.GetFileNameWithoutExtension(_sourcePath);

            // 导出格式/质量跟随截图设置（默认 PNG；WebP/JPEG 见设置页）。编码失败自动回退
            // PNG，Note 非空时在状态行说明，不静默换格式。
            ImageEncodeResult encoded = ImageEncoderService.Default.Encode(
                composed,
                ImageExportFormats.ParseOrDefault(_settings.ScreenshotImageFormat),
                _settings.ScreenshotImageQuality);
            string fileName = ScreenshotNaming.EnsureUnique(
                directory, $"{stem}_标注_{DateTime.Now:HHmmss}{encoded.FileExtension}");
            string target = Path.Combine(directory, fileName);
            File.WriteAllBytes(target, encoded.Bytes);

            _saved(target, composed.Width, composed.Height);
            if (encoded.Note is { Length: > 0 } note) StatusText.Text = note;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"保存失败：{ex.Message}";
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (Editor.IsKeyboardFocusWithin) return;

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _edit.Undo();
            e.Handled = true;
        }
        else if (e.Key == Key.Y && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _edit.Redo();
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OnSaveClick(sender, e);
            e.Handled = true;
        }
        // 工具快捷键查注册表——新工具注册后这里自动支持，无需改宿主。
        else if (AnnotationToolRegistry.Default.FindByKey(e.Key) is { } tool)
        {
            _edit.SelectTool(tool.Tool);
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _original.Dispose();
    }
}
