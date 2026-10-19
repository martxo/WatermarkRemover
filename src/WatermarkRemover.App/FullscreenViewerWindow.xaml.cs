namespace WatermarkRemover.App;

/// <summary>
/// Screen-level image viewer window: borderless, maximized over everything (taskbar included),
/// semi-transparent dark veil. Shows one shared <see cref="ViewModels.PdfPreviewViewModel"/> —
/// the same instance the row's thumbnail grid binds, so the opened page and the rail stay in
/// sync. Esc or the close button leaves it; the strip itself keeps living on the row.
/// </summary>
public partial class FullscreenViewerWindow : System.Windows.Window
{
    private readonly ViewModels.PdfPreviewViewModel _strip;

    public FullscreenViewerWindow(ViewModels.PdfPreviewViewModel strip)
    {
        InitializeComponent();
        _strip = strip;
        DataContext = strip;
        strip.RequestClose += OnStripRequestClose;
        Closed += (_, _) => strip.RequestClose -= OnStripRequestClose;
    }

    /// <summary>The viewer's close button — the strip asks to leave, the window obliges.</summary>
    private void OnStripRequestClose(object? sender, EventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    /// <summary>Modal open: the viewer covers the screen until the user leaves it.</summary>
    public static void ShowFor(ViewModels.PdfPreviewViewModel strip) =>
        new FullscreenViewerWindow(strip).ShowDialog();
}
