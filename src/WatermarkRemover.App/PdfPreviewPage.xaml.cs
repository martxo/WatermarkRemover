using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;

namespace WatermarkRemover.App;

/// <summary>
/// In-app PDF preview overlay. The view is a pure function of
/// <see cref="ViewModels.PdfPreviewViewModel"/>; window-level keys (Esc) are handled by
/// MainWindow. On top of the shared view model this adds the image-viewer staples the fit-only
/// canvas was missing: wheel zoom anchored at the cursor, drag to pan, double click to return
/// to fit. Zoom 1 always means "fit the viewport"; a page change resets to fit.
/// </summary>
public partial class PdfPreviewPage : UserControl
{
    private const double ZoomStep = 1.2;
    private const double MaxZoom = 8;

    private ViewModels.PdfPreviewViewModel? _hooked;
    private double _zoom = 1;
    private double _fitWidth;
    private double _fitHeight;
    private bool _dragging;
    private Point _dragOrigin;
    private double _startH;
    private double _startV;

    public PdfPreviewPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookViewModel();
        ZoomHost.SizeChanged += (_, _) => RecomputeFit();
        HookViewModel();
    }

    private ViewModels.PdfPreviewViewModel? Vm => DataContext as ViewModels.PdfPreviewViewModel;

    /// <summary>Only CurrentImage matters here; follow it so every page swap refits.</summary>
    private void HookViewModel()
    {
        if (ReferenceEquals(_hooked, Vm)) return;
        if (_hooked is { } old) old.PropertyChanged -= OnViewModelPropertyChanged;
        _hooked = Vm;
        if (_hooked is { } vm) vm.PropertyChanged += OnViewModelPropertyChanged;
        RecomputeFit();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.PdfPreviewViewModel.CurrentImage))
        {
            _zoom = 1;
            RecomputeFit();
        }
    }

    /// <summary>Sizes the fit box from the viewport and the page's aspect ratio.</summary>
    private void RecomputeFit()
    {
        var image = Vm?.CurrentImage;
        if (image is null || ZoomHost.ViewportWidth < 1 || ZoomHost.ViewportHeight < 1) return;

        double scale = Math.Min(ZoomHost.ViewportWidth / image.Width, ZoomHost.ViewportHeight / image.Height);
        _fitWidth = image.Width * scale;
        _fitHeight = image.Height * scale;
        ApplyZoomSize();
        if (_zoom <= 1) ResetScroll();
    }

    private void ApplyZoomSize()
    {
        if (_fitWidth <= 0) return;
        PageImage.Width = _fitWidth * _zoom;
        PageImage.Height = _fitHeight * _zoom;
    }

    private void ResetScroll()
    {
        ZoomHost.ScrollToHorizontalOffset(0);
        ZoomHost.ScrollToVerticalOffset(0);
    }

    /// <summary>Wheel zoom clamped to [fit, 8x fit], keeping the pixel under the cursor fixed.</summary>
    private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if (_fitWidth <= 0) return;
        double previous = _zoom;
        double next = Math.Clamp(previous * (e.Delta > 0 ? ZoomStep : 1 / ZoomStep), 1, MaxZoom);
        if (Math.Abs(next - previous) < 0.0001)
        {
            e.Handled = true;
            return;
        }

        var cursor = e.GetPosition(ZoomHost);
        double contentX = ZoomHost.HorizontalOffset + cursor.X;
        double contentY = ZoomHost.VerticalOffset + cursor.Y;

        _zoom = next;
        ApplyZoomSize();
        ZoomHost.UpdateLayout();

        double k = _zoom / previous;
        ZoomHost.ScrollToHorizontalOffset(contentX * k - cursor.X);
        ZoomHost.ScrollToVerticalOffset(contentY * k - cursor.Y);
        e.Handled = true;
    }

    /// <summary>Double click returns to fit; a single press starts a pan once zoomed in.</summary>
    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is ScrollBar) return; // let the scrollbars do their own thing

        if (e.ClickCount == 2)
        {
            _dragging = false;
            _zoom = 1;
            ApplyZoomSize();
            ResetScroll();
            return;
        }

        if (_zoom > 1)
        {
            _dragging = true;
            _dragOrigin = e.GetPosition(ZoomHost);
            _startH = ZoomHost.HorizontalOffset;
            _startV = ZoomHost.VerticalOffset;
            ZoomHost.CaptureMouse();
            Cursor = Cursors.ScrollAll;
        }
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var position = e.GetPosition(ZoomHost);
        ZoomHost.ScrollToHorizontalOffset(_startH - (position.X - _dragOrigin.X));
        ZoomHost.ScrollToVerticalOffset(_startV - (position.Y - _dragOrigin.Y));
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ZoomHost.ReleaseMouseCapture();
        Cursor = null;
    }
}
