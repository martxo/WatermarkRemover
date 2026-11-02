using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SkiaSharp;
using WatermarkRemover.App.Controls;
using WatermarkRemover.App.Editing;
using WatermarkRemover.App.Logging;
using WatermarkRemover.App.Services;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Imaging.Screenshot;

namespace WatermarkRemover.App;

/// <summary>
/// In-app PDF preview overlay. The view is a pure function of
/// <see cref="ViewModels.PdfPreviewViewModel"/>; window-level keys (Esc) are handled by
/// MainWindow. The page plane is the shared <see cref="Controls.ImageSurface"/>, so wheel
/// zoom / drag pan / double-click behave exactly like the screenshot viewer — this control
/// only contributes the film rail, paging arrows, page-zoom buttons and the page operations.
///
/// 标注（阶段 2）挂在同一张平面上：工具条/覆盖层由这里接到
/// <see cref="ViewModels.PdfAnnotationSession"/>，画布坐标 = 页面预览光栅像素，预览与保存
/// 同走 <see cref="ScreenshotComposer"/>——屏幕上看到的就是落盘的内容。
/// </summary>
public partial class PdfPreviewPage : UserControl
{
    /// <summary>覆盖层（草稿层 + 文字浮层），标注时挂到 PageSurface 的覆盖层挂载点。</summary>
    private readonly AnnotationOverlay _overlay = new();

    private bool _wired;

    /// <summary>预览刷新代数：翻页/连续提交时只认最新一次的结果，不覆盖更新的画面。</summary>
    private int _previewGeneration;

    public PdfPreviewPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireAnnotation();
    }

    // --- 页面缩放 --------------------------------------------------------------
    // 滚轮之外的可点入口：用户按了右侧那组缩略图缩放按钮却没反应，是因为它们只管胶片轨。
    // 这三个动作全部作用在页面本身，窗口层的快捷键也走同三个入口。

    internal void ZoomPage(double factor) => PageSurface.ZoomAtCenter(factor);

    internal void FitPage() => PageSurface.Fit();

    /// <summary>几何取证透传：排查「放大图底部丢失」时由宿主窗口定时调用。</summary>
    internal void DumpSurfaceGeometry(string tag) => PageSurface.DumpGeometry(tag);

    internal void ActualSizePage() => PageSurface.ActualSize();

    private void OnZoomInClick(object sender, RoutedEventArgs e) => ZoomPage(1.25);

    private void OnZoomOutClick(object sender, RoutedEventArgs e) => ZoomPage(1 / 1.25);

    private void OnFitClick(object sender, RoutedEventArgs e) => FitPage();

    private void OnActualSizeClick(object sender, RoutedEventArgs e) => ActualSizePage();

    /// <summary>「插入文件」：选一个 PDF，把它的页插到当前页之后。文件选择属于窗口层，
    /// 插入本身（写盘、方向校正、条带刷新）留在视图模型里。</summary>
    private void OnInsertFileClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PdfPreviewViewModel viewModel) return;

        var dialog = new OpenFileDialog
        {
            Title = "插入 PDF 页面",
            Filter = "PDF 文件|*.pdf",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != true) return;

        viewModel.InsertFileAfterCurrent(dialog.FileName);
    }

    // --- 标注（阶段 2）----------------------------------------------------------

    /// <summary>
    /// 把标注接到会话与工具条上。全部编辑逻辑在 <see cref="AnnotationEditSession"/> 里，
    /// 这里只做三件宿主该做的事：注入坐标换算、把工具条事件路由到「当前页」会话、
    /// 把会话变化翻译成界面更新（合成预览 / 工具条状态 / 指针抢占）。
    /// </summary>
    private void WireAnnotation()
    {
        if (_wired || DataContext is not PdfPreviewViewModel vm) return;
        _wired = true;

        _overlay.ToCanvas = plane => PageSurface.PlaneToPixel(plane);
        _overlay.ToPlane = pixel => PageSurface.PixelToPlane(pixel);

        // 工具条永远驱动「当前页」的会话：翻页后由 SwitchSession 重挂，选择不丢。
        Toolbar.ToolClick += (_, tool) => vm.Annotation.Current.SelectTool(tool);
        Toolbar.ColorClick += (_, argb) => vm.Annotation.Current.SetColor(argb);
        Toolbar.WidthClick += (_, dip) => vm.Annotation.Current.SetLineWidth(dip);
        Toolbar.FontSizeClick += (_, size) => vm.Annotation.Current.SetTextFontSize(size);
        Toolbar.UndoClick += (_, _) => vm.Annotation.Current.Undo();
        Toolbar.RedoClick += (_, _) => vm.Annotation.Current.Redo();
        Toolbar.ClearClick += (_, _) => vm.Annotation.Current.Clear();

        vm.AnnotationsChanged += OnAnnotationsChanged;
        vm.AnnotationStylesChanged += OnAnnotationStylesChanged;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        ApplyAnnotationChrome(vm);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not PdfPreviewViewModel vm) return;

        switch (e.PropertyName)
        {
            case nameof(PdfPreviewViewModel.IsAnnotating):
                ApplyAnnotationChrome(vm);
                break;
            case nameof(PdfPreviewViewModel.CurrentImage) when vm.IsAnnotating:
                // 新一页的光栅到位：换当前页会话，并按新底图重算预览。
                SwitchSession(vm);
                _ = RefreshAnnotatedPreviewAsync(vm);
                break;
        }
    }

    private void ApplyAnnotationChrome(PdfPreviewViewModel vm)
    {
        bool editing = vm.IsAnnotating;
        EditBar.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        EditHint.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;

        if (!editing)
        {
            PageSurface.Overlay = null;
            _overlay.Visibility = Visibility.Collapsed;
            return;
        }

        _overlay.Visibility = Visibility.Visible;
        PageSurface.Overlay = _overlay;
        SwitchSession(vm);
        _ = RefreshAnnotatedPreviewAsync(vm);
    }

    /// <summary>翻页/进入标注：把覆盖层指到当前页的会话，并配好坐标换算与指针抢占。</summary>
    private void SwitchSession(PdfPreviewViewModel vm)
    {
        AnnotationEditSession session = vm.Annotation.Current;
        // 画布坐标 = 页面预览光栅像素；粗细/字号按当前缩放换算回像素（DIP ÷ 缩放）。
        session.ScaleAt = _ => 1f / (float)PageSurface.Scale;
        session.CanPlaceText = point => vm.CurrentImage is BitmapSource image
            && point.X >= 0 && point.Y >= 0
            && point.X <= image.PixelWidth && point.Y <= image.PixelHeight;

        _overlay.Session = session;
        _overlay.UpdateCursor();
        // 选了工具才抢占指针；取消选择后缩放/平移立刻恢复（覆盖层透传回平面）。
        PageSurface.OverlayInteractive = session.Tool != ScreenshotTool.None;
        SyncToolbar(vm);
    }

    private void SyncToolbar(PdfPreviewViewModel vm)
    {
        AnnotationEditSession session = vm.Annotation.Current;
        Toolbar.Sync(
            session.Tool,
            session.Style.ColorArgb,
            session.Style.LineWidthDip,
            session.Style.TextFontSizeDip,
            session.HasAnnotations,
            session.CanRedo,
            session.HasAnnotations);
    }

    private void OnAnnotationsChanged(object? sender, EventArgs e)
    {
        if (DataContext is not PdfPreviewViewModel vm) return;
        SyncToolbar(vm);
        if (vm.IsAnnotating) _ = RefreshAnnotatedPreviewAsync(vm);
    }

    private void OnAnnotationStylesChanged(object? sender, EventArgs e)
    {
        if (DataContext is not PdfPreviewViewModel vm) return;
        _overlay.UpdateCursor();
        PageSurface.OverlayInteractive = vm.Annotation.Current.Tool != ScreenshotTool.None;
        SyncToolbar(vm);
    }

    /// <summary>
    /// 重算「页面 + 标注」的合成预览。铁律：预览与保存同走 <see cref="ScreenshotComposer"/>，
    /// 1:1 下屏幕上看到的就是写进 PDF 的内容；<see cref="ImageSurface.ReplaceSource"/>
    /// 保留当前缩放/平移，画标注不会把视角弹回适应窗口。
    /// </summary>
    private async Task RefreshAnnotatedPreviewAsync(PdfPreviewViewModel vm)
    {
        if (!vm.IsAnnotating) return;

        int generation = ++_previewGeneration;
        try
        {
            int page = vm.PageIndex;
            byte[] bytes = await vm.GetPreviewBytesAsync(page).ConfigureAwait(true);
            if (generation != _previewGeneration || !vm.IsAnnotating) return;

            using SKBitmap? baseBitmap = SKBitmap.Decode(bytes);
            if (baseBitmap is null) return;

            using SKBitmap composed = ScreenshotComposer.Compose(
                baseBitmap,
                new SKRectI(0, 0, baseBitmap.Width, baseBitmap.Height),
                vm.Annotation.AnnotationsOf(page));
            if (generation != _previewGeneration || !vm.IsAnnotating) return;

            PageSurface.ReplaceSource(SkiaBitmapBridge.ToBitmapSource(composed));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"标注预览刷新失败：{ex.Message}");
        }
    }

    /// <summary>宿主窗口（Esc 等键）在编辑态下先问这里；返回 true = 已处理。</summary>
    internal bool HandleAnnotationKey(KeyEventArgs e)
    {
        if (DataContext is not PdfPreviewViewModel vm || !vm.IsAnnotating) return false;

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        AnnotationEditSession session = vm.Annotation.Current;

        switch (e.Key)
        {
            case Key.Escape:
                // 与截图标注一致：Esc 先收工具，再退出标注（不直接关窗）。
                if (session.Tool != ScreenshotTool.None) session.SelectTool(ScreenshotTool.None);
                else RequestExitAnnotation();
                break;
            case Key.Z when ctrl:
                session.Undo();
                break;
            case Key.Y when ctrl:
                session.Redo();
                break;
            case Key.S when ctrl:
                _ = vm.SaveOverlayAnnotationsAsync();
                break;
            default:
                // 工具快捷键查注册表——新工具注册后这里自动支持，无需改宿主。
                if (ctrl || AnnotationToolRegistry.Default.FindByKey(e.Key) is not { } tool) return false;
                session.SelectTool(tool.Tool);
                break;
        }

        return true;
    }

    /// <summary>M 键 / 按钮：进入或退出标注（退出前拦未保存的标注）。</summary>
    internal void ToggleAnnotation()
    {
        if (DataContext is not PdfPreviewViewModel vm) return;
        if (vm.IsAnnotating) RequestExitAnnotation();
        else vm.EnterAnnotation();
    }

    private void OnExitAnnotationClick(object sender, RoutedEventArgs e) => ToggleAnnotation();

    private void RequestExitAnnotation()
    {
        if (DataContext is not PdfPreviewViewModel vm) return;
        if (!vm.HasUnsavedAnnotations)
        {
            vm.ExitAnnotation();
            return;
        }

        Window? window = Window.GetWindow(this);
        bool wasTopmost = window is { Topmost: true };
        if (window is not null) window.Topmost = false; // 否则置顶遮罩会把确认框压在身后
        try
        {
            if (MessageBox.Show(
                window,
                "还有未保存的标注，放弃并退出吗？\n（磁盘上的 PDF 不会被修改）",
                "标注",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question) != MessageBoxResult.OK)
            {
                return;
            }
        }
        finally
        {
            if (window is not null) window.Topmost = wasTopmost;
        }

        vm.ExitAnnotation();
    }

    // --- 导出 --------------------------------------------------------------------

    private void OnExportMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OnExportImagePdfClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PdfPreviewViewModel vm) return;

        var dialog = new SaveFileDialog
        {
            Title = "导出图片型 PDF（彻底脱敏）",
            Filter = "PDF 文件|*.pdf",
            FileName = SuggestedName(vm, ".pdf"),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true) return;

        _ = vm.ExportImagePdfAsync(dialog.FileName);
    }

    private void OnExportPagePngClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PdfPreviewViewModel vm) return;

        var dialog = new SaveFileDialog
        {
            Title = "导出当前页 PNG",
            Filter = "PNG 图片|*.png",
            FileName = SuggestedName(vm, ".png"),
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != true) return;

        _ = vm.ExportPagePngAsync(dialog.FileName, vm.PageIndex);
    }

    private static string SuggestedName(PdfPreviewViewModel vm, string extension) =>
        Path.GetFileNameWithoutExtension(vm.Title) + extension;
}
