using System.Windows;
using System.Windows.Input;
using SkiaSharp;
using WatermarkRemover.App.Services;
using WatermarkRemover.Core.Features.Screenshot;
using WatermarkRemover.Core.Settings;
using WatermarkRemover.Imaging.Screenshot;

namespace WatermarkRemover.App;

/// <summary>
/// Manual long screenshot: the user scrolls the target window, then presses 追加下一屏. The
/// window captures the same region again, finds the overlap with the stitched image and appends
/// only the new rows. This avoids synthetic wheel injection, which many apps ignore or mis-handle.
/// </summary>
public partial class LongScreenshotWindow : Window
{
    private readonly ScreenshotRect _region;
    private readonly AppSettings _settings;
    private readonly Action<ScreenshotCaptureResult> _complete;
    private readonly Action _cancel;
    private readonly int _firstFrameHeight;

    private SKBitmap _stitched;
    private bool _busy;
    private bool _finished;

    public LongScreenshotWindow(
        SKBitmap firstFrame,
        ScreenshotRect region,
        AppSettings settings,
        Action<ScreenshotCaptureResult> complete,
        Action cancel)
    {
        _stitched = firstFrame;
        _firstFrameHeight = firstFrame.Height;
        _region = region;
        _settings = settings;
        _complete = complete;
        _cancel = cancel;

        InitializeComponent();
        UpdatePreview();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateLayout();
        Rect work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - ActualWidth) / 2;
        Top = work.Top + 24;
    }

    private void UpdatePreview()
    {
        PreviewImage.Source = SkiaBitmapBridge.ToBitmapSource(_stitched);
        SizeText.Text = $"{_stitched.Width} × {_stitched.Height} px";
    }

    private async void OnAppendClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _finished) return;

        _busy = true;
        AppendButton.IsEnabled = false;
        FinishButton.IsEnabled = false;
        StatusText.Text = "正在捕获下一屏…";

        try
        {
            // Hide this panel first: it is topmost and may sit over the selected region.
            Hide();
            await Task.Delay(180);
            using DesktopCapture desktop = await Task.Run(ScreenCaptureService.Capture).ConfigureAwait(true);
            using SKBitmap frame = CropRegion(desktop.Bitmap);

            int overlap = ScreenshotLongStitcher.FindOverlap(_stitched, frame);
            if (overlap <= 0)
            {
                StatusText.Text = "未找到重叠区域：请确认已向下滚动，且本次滚动不超过一屏的 80%。";
            }
            else if (overlap >= frame.Height - 2)
            {
                StatusText.Text = "画面没有变化，请先在目标窗口向下滚动。";
            }
            else
            {
                int added = frame.Height - overlap;
                if (_stitched.Height + added > ScreenshotLongStitcher.MaxHeight)
                {
                    StatusText.Text = $"已达到 {ScreenshotLongStitcher.MaxHeight}px 上限，请完成保存。";
                }
                else
                {
                    SKBitmap next = ScreenshotLongStitcher.Append(_stitched, frame, overlap);
                    _stitched.Dispose();
                    _stitched = next;
                    UpdatePreview();
                    StatusText.Text = $"已追加 {added}px，当前高度 {_stitched.Height}px。";
                }
            }
        }
        catch (Exception ex)
        {
            Logging.AppLog.Warn($"长截图追加失败：{ex.Message}");
            StatusText.Text = $"追加失败：{ex.Message}";
        }
        finally
        {
            _busy = false;
            AppendButton.IsEnabled = true;
            FinishButton.IsEnabled = true;
            Show();
            Activate();
        }
    }

    /// <summary>Crops the fixed selection out of a fresh full-desktop capture.</summary>
    private SKBitmap CropRegion(SKBitmap desktop)
    {
        int left = Math.Clamp((int)Math.Round(_region.Left), 0, desktop.Width - 1);
        int top = Math.Clamp((int)Math.Round(_region.Top), 0, desktop.Height - 1);
        int right = Math.Clamp((int)Math.Round(_region.Right), left + 1, desktop.Width);
        int bottom = Math.Clamp((int)Math.Round(_region.Bottom), top + 1, desktop.Height);
        return ScreenshotComposer.Compose(
            desktop, new SKRectI(left, top, right, bottom), Array.Empty<ScreenshotAnnotation>());
    }

    private void OnFinishClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _finished) return;
        if (_stitched.Height <= _firstFrameHeight)
        {
            StatusText.Text = "请至少追加一屏后再保存。";
            return;
        }

        _finished = true;
        ScreenshotCaptureResult result = ScreenshotOverlaySession.SaveAndCopy(
            _stitched, _settings, _stitched.Width, _stitched.Height);
        Logging.AppLog.Info($"长截图完成：{_stitched.Width}×{_stitched.Height}");
        _complete(result);
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelAndClose();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CancelAndClose();
            e.Handled = true;
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_finished)
        {
            _finished = true;
            _cancel();
        }

        _stitched.Dispose();
    }

    private void CancelAndClose()
    {
        if (_finished) return;
        _finished = true;
        _cancel();
        Close();
    }
}
