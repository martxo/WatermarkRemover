using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WatermarkRemover.App.ViewModels;

namespace WatermarkRemover.App;

/// <summary>
/// In-app PDF preview overlay. The view is a pure function of
/// <see cref="ViewModels.PdfPreviewViewModel"/>; window-level keys (Esc) are handled by
/// MainWindow. The page plane is the shared <see cref="Controls.ImageSurface"/>, so wheel
/// zoom / drag pan / double-click behave exactly like the screenshot viewer — this control
/// only contributes the film rail, paging arrows, page-zoom buttons and the page operations.
/// </summary>
public partial class PdfPreviewPage : UserControl
{
    public PdfPreviewPage()
    {
        InitializeComponent();
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
}
