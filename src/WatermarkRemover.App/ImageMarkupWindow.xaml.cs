using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Line = System.Windows.Shapes.Line;
using Polygon = System.Windows.Shapes.Polygon;
using Polyline = System.Windows.Shapes.Polyline;
using Rectangle = System.Windows.Shapes.Rectangle;
using SkiaSharp;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Features.Screenshot;
using WatermarkRemover.Imaging.Screenshot;

namespace WatermarkRemover.App;

/// <summary>
/// Markup editor for an already-saved screenshot (Tendedero's long-press action). It reuses the
/// same annotation model/composer as the live overlay, so colours, widths, multi-line text and
/// mosaic match the PNG produced during normal capture. Saving always writes a NEW file and
/// leaves the original untouched.
/// </summary>
public partial class ImageMarkupWindow : Window
{
    private const double MinimumMarkSize = 3;

    private readonly string _sourcePath;
    private readonly SKBitmap _original;
    private readonly Action<string, int, int> _saved;
    private readonly List<ScreenshotAnnotation> _annotations = new();
    private readonly List<ScreenshotAnnotation> _redo = new();
    private readonly SolidColorBrush _markBrush = new(Color.FromRgb(229, 57, 53));

    private double _scale = 1;
    private ScreenshotTool _tool;
    private uint _colorArgb = ScreenshotAnnotation.DefaultColorArgb;
    private float _lineWidthDip = 2.5f;
    private float _fontSizeDip = 16f;
    private ScreenshotAnnotation? _draft;
    private Point _startImage;
    private bool _drawing;
    private bool _textEditing;
    private Point? _textImagePoint;

    public ImageMarkupWindow(string sourcePath, Action<string, int, int> saved)
    {
        _sourcePath = sourcePath;
        _saved = saved ?? throw new ArgumentNullException(nameof(saved));

        SKBitmap? decoded = SKBitmap.Decode(sourcePath);
        if (decoded is null) throw new InvalidOperationException("无法读取图片");
        _original = decoded;

        InitializeComponent();

        PreviewImage.Source = SkiaBitmapBridge.ToBitmapSource(_original);
        WireToolbar();

        Loaded += (_, _) => LayoutStage();
        Viewport.SizeChanged += (_, _) => LayoutStage();
        UpdateToolbar();
    }

    private void WireToolbar()
    {
        RectTool.Click += (_, _) => SetTool(ScreenshotTool.Rectangle);
        ArrowTool.Click += (_, _) => SetTool(ScreenshotTool.Arrow);
        PenTool.Click += (_, _) => SetTool(ScreenshotTool.Pen);
        TextTool.Click += (_, _) => SetTool(ScreenshotTool.Text);
        MosaicTool.Click += (_, _) => SetTool(ScreenshotTool.Mosaic);

        ColorRed.Click += (_, _) => SetColor(0xFFE53935);
        ColorYellow.Click += (_, _) => SetColor(0xFFFBC02D);
        ColorBlue.Click += (_, _) => SetColor(0xFF1E88E5);
        ColorGreen.Click += (_, _) => SetColor(0xFF43A047);
        ColorBlack.Click += (_, _) => SetColor(0xFF111111);

        ThinWidth.Click += (_, _) => SetLineWidth(1.5f);
        MediumWidth.Click += (_, _) => SetLineWidth(3f);
        ThickWidth.Click += (_, _) => SetLineWidth(5f);

        TextSizeBox.SelectionChanged += (_, _) =>
        {
            _fontSizeDip = TextSizeBox.SelectedIndex switch
            {
                0 => 12f,
                2 => 20f,
                _ => 16f,
            };
        };
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
        if (_annotations.Count == 0)
        {
            PreviewImage.Source = SkiaBitmapBridge.ToBitmapSource(_original);
            return;
        }

        using SKBitmap composed = ScreenshotComposer.Compose(
            _original,
            new SKRectI(0, 0, _original.Width, _original.Height),
            _annotations);
        PreviewImage.Source = SkiaBitmapBridge.ToBitmapSource(composed);
    }

    private void SetTool(ScreenshotTool tool)
    {
        _tool = _tool == tool ? ScreenshotTool.None : tool;
        DraftLayer.Cursor = _tool switch
        {
            ScreenshotTool.Text => Cursors.IBeam,
            ScreenshotTool.None => Cursors.Arrow,
            _ => Cursors.Cross,
        };
        UpdateToolbar();
    }

    private void SetColor(uint argb)
    {
        _colorArgb = argb;
        UpdateToolbar();
    }

    private void SetLineWidth(float dip)
    {
        _lineWidthDip = dip;
        UpdateToolbar();
    }

    private void UpdateToolbar()
    {
        RectTool.IsChecked = _tool == ScreenshotTool.Rectangle;
        ArrowTool.IsChecked = _tool == ScreenshotTool.Arrow;
        PenTool.IsChecked = _tool == ScreenshotTool.Pen;
        TextTool.IsChecked = _tool == ScreenshotTool.Text;
        MosaicTool.IsChecked = _tool == ScreenshotTool.Mosaic;

        ThinWidth.IsChecked = Math.Abs(_lineWidthDip - 1.5f) < 0.01f;
        MediumWidth.IsChecked = Math.Abs(_lineWidthDip - 3f) < 0.01f;
        ThickWidth.IsChecked = Math.Abs(_lineWidthDip - 5f) < 0.01f;

        SetSwatch(ColorRed, 0xFFE53935);
        SetSwatch(ColorYellow, 0xFFFBC02D);
        SetSwatch(ColorBlue, 0xFF1E88E5);
        SetSwatch(ColorGreen, 0xFF43A047);
        SetSwatch(ColorBlack, 0xFF111111);

        UndoButton.IsEnabled = _annotations.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
        ClearButton.IsEnabled = _annotations.Count > 0;
        StatusText.Text = _annotations.Count == 0 ? "按住 Shift+Enter 换行" : $"已添加 {_annotations.Count} 个标注";
    }

    private void SetSwatch(Button swatch, uint argb) =>
        swatch.Opacity = _colorArgb == argb ? 1.0 : 0.45;

    // --- Canvas drawing --------------------------------------------------------

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_textEditing)
        {
            CommitText();
            e.Handled = true;
            return;
        }

        Point image = ImagePoint(e.GetPosition(DraftLayer));
        if (image.X < 0 || image.Y < 0 || image.X > _original.Width || image.Y > _original.Height) return;
        if (_tool == ScreenshotTool.None) return;

        if (_tool == ScreenshotTool.Text)
        {
            BeginText(image);
            e.Handled = true;
            return;
        }

        _drawing = true;
        _startImage = image;
        _draft = NewDraft(image, image);
        DrawDraft();
        DraftLayer.CaptureMouse();
        e.Handled = true;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_drawing || _draft is null) return;

        Point image = ImagePoint(e.GetPosition(DraftLayer));
        if (_draft.Kind == ScreenshotAnnotationKind.Pen)
        {
            var last = _draft.Points[^1];
            if (Math.Abs(last.X - image.X) < 1 && Math.Abs(last.Y - image.Y) < 1) return;
            var points = new List<ScreenshotPointF>(_draft.Points)
            {
                new((float)image.X, (float)image.Y),
            };
            _draft = Styled(_draft.Kind, points, _draft.Thickness);
        }
        else
        {
            _draft = Styled(_draft.Kind, new[]
            {
                new ScreenshotPointF((float)_startImage.X, (float)_startImage.Y),
                new ScreenshotPointF((float)image.X, (float)image.Y),
            }, _draft.Thickness);
        }

        DrawDraft();
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing || _draft is null) return;

        _drawing = false;
        DraftLayer.ReleaseMouseCapture();

        ScreenshotAnnotation draft = _draft;
        _draft = null;
        DrawDraft();

        bool keep;
        if (draft.Kind == ScreenshotAnnotationKind.Pen)
        {
            keep = draft.Points.Count >= 2;
        }
        else
        {
            double width = Math.Abs(draft.Points[1].X - draft.Points[0].X);
            double height = Math.Abs(draft.Points[1].Y - draft.Points[0].Y);
            keep = width >= MinimumMarkSize || height >= MinimumMarkSize;
        }

        if (!keep) return;

        _annotations.Add(draft);
        _redo.Clear();
        RefreshPreview();
        UpdateToolbar();
        e.Handled = true;
    }

    private ScreenshotAnnotation NewDraft(Point start, Point end)
    {
        float thickness = Math.Max(1, _lineWidthDip / (float)_scale);
        return Styled(_tool switch
        {
            ScreenshotTool.Arrow => ScreenshotAnnotationKind.Arrow,
            ScreenshotTool.Pen => ScreenshotAnnotationKind.Pen,
            ScreenshotTool.Mosaic => ScreenshotAnnotationKind.Mosaic,
            _ => ScreenshotAnnotationKind.Rectangle,
        }, new[]
        {
            new ScreenshotPointF((float)start.X, (float)start.Y),
            new ScreenshotPointF((float)end.X, (float)end.Y),
        }, thickness);
    }

    private ScreenshotAnnotation Styled(
        ScreenshotAnnotationKind kind,
        IReadOnlyList<ScreenshotPointF> points,
        float thickness) => new()
        {
            Kind = kind,
            Points = points,
            ColorArgb = _colorArgb,
            Thickness = thickness,
        };

    private void DrawDraft()
    {
        DraftLayer.Children.Clear();
        if (_draft is null) return;

        _markBrush.Color = MarkColor(_colorArgb);
        double thickness = _lineWidthDip;
        Point first = DisplayPoint(new Point(_draft.Points[0].X, _draft.Points[0].Y));

        switch (_draft.Kind)
        {
            case ScreenshotAnnotationKind.Pen:
            {
                var polyline = new Polyline
                {
                    Stroke = _markBrush,
                    StrokeThickness = thickness,
                    StrokeLineJoin = PenLineJoin.Round,
                };
                foreach (ScreenshotPointF point in _draft.Points)
                    polyline.Points.Add(DisplayPoint(new Point(point.X, point.Y)));
                DraftLayer.Children.Add(polyline);
                break;
            }

            case ScreenshotAnnotationKind.Arrow:
            {
                Point last = DisplayPoint(new Point(_draft.Points[^1].X, _draft.Points[^1].Y));
                DraftLayer.Children.Add(new Line
                {
                    X1 = first.X, Y1 = first.Y, X2 = last.X, Y2 = last.Y,
                    Stroke = _markBrush,
                    StrokeThickness = thickness,
                });
                DrawArrowHead(first, last, thickness);
                break;
            }

            case ScreenshotAnnotationKind.Mosaic:
            {
                Point last = DisplayPoint(new Point(_draft.Points[^1].X, _draft.Points[^1].Y));
                var rect = new Rectangle
                {
                    Stroke = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 3, 2 },
                    Fill = new SolidColorBrush(Color.FromArgb(24, 0, 0, 0)),
                    Width = Math.Abs(last.X - first.X),
                    Height = Math.Abs(last.Y - first.Y),
                };
                Canvas.SetLeft(rect, Math.Min(first.X, last.X));
                Canvas.SetTop(rect, Math.Min(first.Y, last.Y));
                DraftLayer.Children.Add(rect);
                break;
            }

            default:
            {
                Point last = DisplayPoint(new Point(_draft.Points[^1].X, _draft.Points[^1].Y));
                var rect = new Rectangle
                {
                    Stroke = _markBrush,
                    StrokeThickness = thickness,
                    Width = Math.Abs(last.X - first.X),
                    Height = Math.Abs(last.Y - first.Y),
                };
                Canvas.SetLeft(rect, Math.Min(first.X, last.X));
                Canvas.SetTop(rect, Math.Min(first.Y, last.Y));
                DraftLayer.Children.Add(rect);
                break;
            }
        }
    }

    private void DrawArrowHead(Point start, Point end, double thickness)
    {
        double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        double head = Math.Max(10, thickness * 3.2);
        var polygon = new Polygon
        {
            Fill = _markBrush,
            Points =
            {
                end,
                new Point(
                    end.X - head * Math.Cos(angle - Math.PI / 7),
                    end.Y - head * Math.Sin(angle - Math.PI / 7)),
                new Point(
                    end.X - head * Math.Cos(angle + Math.PI / 7),
                    end.Y - head * Math.Sin(angle + Math.PI / 7)),
            },
        };
        DraftLayer.Children.Add(polygon);
    }

    private static Color MarkColor(uint argb) => Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    // --- Text ------------------------------------------------------------------

    private void BeginText(Point imagePoint)
    {
        _textImagePoint = imagePoint;
        _textEditing = true;
        Point display = DisplayPoint(imagePoint);
        TextEditor.Margin = new Thickness(display.X, display.Y, 0, 0);
        TextEditor.Text = string.Empty;
        TextEditor.FontSize = _fontSizeDip;
        TextEditor.Foreground = new SolidColorBrush(MarkColor(_colorArgb));
        TextEditor.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Keyboard.Focus(TextEditor);
            TextEditor.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CommitText()
    {
        if (!_textEditing) return;

        string text = TextEditor.Text;
        Point? anchor = _textImagePoint;
        HideTextEditor();
        if (anchor is not { } point || string.IsNullOrWhiteSpace(text)) return;

        _annotations.Add(new ScreenshotAnnotation
        {
            Kind = ScreenshotAnnotationKind.Text,
            Points = new[] { new ScreenshotPointF((float)point.X, (float)point.Y) },
            Text = text.Trim(),
            FontSize = Math.Max(10, _fontSizeDip / (float)_scale),
            ColorArgb = _colorArgb,
            Thickness = 1f,
        });
        _redo.Clear();
        RefreshPreview();
        UpdateToolbar();
    }

    private void CancelText()
    {
        HideTextEditor();
    }

    private void HideTextEditor()
    {
        _textEditing = false;
        _textImagePoint = null;
        TextEditor.Visibility = Visibility.Collapsed;
        Keyboard.Focus(DraftLayer);
    }

    private void OnTextEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            CommitText();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelText();
            e.Handled = true;
        }
    }

    private void OnTextEditorLostFocus(object sender, RoutedEventArgs e)
    {
        if (_textEditing) CommitText();
    }

    // --- Toolbar actions -------------------------------------------------------

    private void OnUndoClick(object sender, RoutedEventArgs e)
    {
        if (_annotations.Count == 0) return;
        int last = _annotations.Count - 1;
        _redo.Add(_annotations[last]);
        _annotations.RemoveAt(last);
        RefreshPreview();
        UpdateToolbar();
    }

    private void OnRedoClick(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        _annotations.Add(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        RefreshPreview();
        UpdateToolbar();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        if (_annotations.Count == 0) return;
        _annotations.Clear();
        _redo.Clear();
        RefreshPreview();
        UpdateToolbar();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_textEditing) CommitText();
        if (_annotations.Count == 0)
        {
            StatusText.Text = "请先添加标注再保存";
            return;
        }

        try
        {
            using SKBitmap composed = ScreenshotComposer.Compose(
                _original,
                new SKRectI(0, 0, _original.Width, _original.Height),
                _annotations);

            string directory = Path.GetDirectoryName(_sourcePath) ?? AppContext.BaseDirectory;
            Directory.CreateDirectory(directory);
            string stem = Path.GetFileNameWithoutExtension(_sourcePath);
            string fileName = ScreenshotNaming.EnsureUnique(
                directory, $"{stem}_标注_{DateTime.Now:HHmmss}.png");
            string target = Path.Combine(directory, fileName);

            using SKImage image = SKImage.FromBitmap(composed);
            using SKData? data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data is null) throw new InvalidOperationException("PNG 编码失败");
            File.WriteAllBytes(target, data.ToArray());

            _saved(target, composed.Width, composed.Height);
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
        if (TextEditor.IsKeyboardFocusWithin) return;

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OnUndoClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Y && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OnRedoClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            OnSaveClick(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.R) SetTool(ScreenshotTool.Rectangle);
        else if (e.Key == Key.A) SetTool(ScreenshotTool.Arrow);
        else if (e.Key == Key.P) SetTool(ScreenshotTool.Pen);
        else if (e.Key == Key.T) SetTool(ScreenshotTool.Text);
        else if (e.Key == Key.M) SetTool(ScreenshotTool.Mosaic);
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _original.Dispose();
    }
}
