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

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Close();
            e.Handled = true;
        }
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
        if (WindowState == System.Windows.WindowState.Minimized)
        {
            WindowState = System.Windows.WindowState.Maximized;
        }

        // A rail click inside the open viewer targets the window that already has the
        // foreground; re-activating a maximised AllowsTransparency window only recomposes the
        // whole-screen layered surface for nothing. Take it back solely when it was minimised
        // or another window really holds the foreground.
        if (!IsActive) Activate();
    }
}
