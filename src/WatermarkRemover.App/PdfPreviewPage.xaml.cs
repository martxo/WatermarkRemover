using System.Windows.Controls;

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
}
