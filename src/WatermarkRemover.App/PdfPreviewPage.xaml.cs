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
/// only contributes the film rail, paging arrows and the page operations around it.
/// </summary>
public partial class PdfPreviewPage : UserControl
{
    public PdfPreviewPage()
    {
        InitializeComponent();
    }

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
