using WatermarkRemover.App.Services;

namespace WatermarkRemover.App;

/// <summary>
/// Screen-level image viewer window: borderless, maximized over everything (taskbar included),
/// semi-transparent dark veil. Shows one shared <see cref="ViewModels.PdfPreviewViewModel"/> —
/// the same instance the row's thumbnail grid binds, so the opened page and the rail stay in
/// sync. Esc or the close button leaves it; the strip itself keeps living on the row.
/// </summary>
public partial class FullscreenViewerWindow : System.Windows.Window
{
    /// <summary>The viewer currently on screen, or null. The window is modal, so at most one
    /// exists; the reference is what stops a second tile click from stacking a copy on top.</summary>
    private static FullscreenViewerWindow? _current;

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

    /// <summary>
    /// 窗口一创建就铺满显示器物理边界（含任务栏那条）。用 WindowState=Maximized 时无边框窗口
    /// 只铺到工作区，底部会露出桌面、页面也因此少一截——这是用户反馈"下方被裁剪"的根因。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ScreenCoverage.CoverScreenOf(this);
        // 换到另一块缩放到显示器上时重新铺满。
        DpiChanged += (_, _) => ScreenCoverage.CoverScreenOf(this);
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case System.Windows.Input.Key.Escape:
                Close();
                break;
            // 与看图窗口同一套缩放键：0 适应、1 原始、+/- 缩放。
            case System.Windows.Input.Key.D0:
            case System.Windows.Input.Key.NumPad0:
                PageView.FitPage();
                break;
            case System.Windows.Input.Key.D1:
            case System.Windows.Input.Key.NumPad1:
                PageView.ActualSizePage();
                break;
            case System.Windows.Input.Key.OemPlus:
            case System.Windows.Input.Key.Add:
                PageView.ZoomPage(1.15);
                break;
            case System.Windows.Input.Key.OemMinus:
            case System.Windows.Input.Key.Subtract:
                PageView.ZoomPage(1 / 1.15);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Modal open: the viewer covers the screen until the user leaves it. Clicking a rail tile
    /// raises EnlargeRequested again — including the tiles INSIDE this window — so without a
    /// guard every click built another maximised transparent window over the open one (dozens of
    /// pages meant dozens of stacked windows and a visible stutter while each new full-screen
    /// layered surface was composed). The click has already rendered its page into the shared
    /// view model, so bringing the open viewer forward is all that is needed: its canvas binds
    /// that same instance and switches page by itself.
    /// </summary>
    public static void ShowFor(ViewModels.PdfPreviewViewModel strip)
    {
        if (_current is { } open && ReferenceEquals(open._strip, strip))
        {
            open.RestoreAndActivate();
            return;
        }

        var window = new FullscreenViewerWindow(strip);
        _current = window;
        // While the viewer is up the strip prefetches neighbouring pages and lets the tile fill
        // yield, so rail clicks stay a cache hit + a short render.
        strip.SetViewerOpen(true);
        try
        {
            window.ShowDialog();
        }
        finally
        {
            strip.SetViewerOpen(false);
            if (ReferenceEquals(_current, window)) _current = null;
        }
    }

    /// <summary>Pulls the open viewer back to the front (another app may hold the foreground).</summary>
    private void RestoreAndActivate()
    {
        // 窗口不再用 WindowState=Maximized（那会停在任务栏外）：重新按显示器边界铺满即可。
        ScreenCoverage.CoverScreenOf(this);

        // A rail click inside the open viewer targets the window that already has the
        // foreground; re-activating a maximised AllowsTransparency window only recomposes the
        // whole-screen layered surface for nothing. Take it back solely when it was minimised
        // or another window really holds the foreground.
        if (!IsActive) Activate();
    }
}
