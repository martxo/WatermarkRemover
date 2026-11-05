using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WatermarkRemover.App.ViewModels;
using WatermarkRemover.Core.Features.Screenshot;

namespace WatermarkRemover.App.Controls;

/// <summary>
/// 截图设置 → 触发区域编辑器：左侧示意图里四条边的热区块可按住沿边拖动调位置，
/// 右侧各行改启停 / 厚度 / 长度。全部直接绑定设置工作副本，随「返回应用」持久化。
/// 示意图按主屏工作区等比映射——位置用比例表示，换分辨率不会错位。
/// 厚度/长度在输入时即时写回工作副本并重排示意图，不等失焦才刷新。
/// </summary>
public partial class ScreenshotTriggerEditor : UserControl
{
    /// <summary>示意图尺寸（固定；按主屏工作区等比映射）。</summary>
    private const double MockWidth = 300;

    private const double MockHeight = 190;

    /// <summary>热区在示意图里的最小可视尺寸：再小的配置也保证点得到、看得见。</summary>
    private const double MinZonePixels = 4;

    private ScreenshotTriggerSettings? _trigger;
    private ScreenEdge _dragEdge;
    private double _dragStartFraction;
    private double _dragStartPosition;
    private bool _dragging;

    public ScreenshotTriggerEditor()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => RefreshMockup();
        Loaded += (_, _) => RefreshMockup();
    }

    private ScreenshotTriggerSettings? Trigger => (DataContext as SettingsViewModel)?.Settings.ScreenshotTrigger;

    private void OnEdgeToggleChanged(object sender, RoutedEventArgs e) => RefreshMockup();

    private void OnNumericBoxLostFocus(object sender, RoutedEventArgs e) => RefreshMockup();

    /// <summary>
    /// 输入即刷新：绑定是 LostFocus 的，光标还在框里时源对象并没有更新，旧实现因此要等
    /// 用户点别处才看得到示意图变化。这里把合法数字直接写回工作副本并重排，越界值由
    /// RefreshMockup 里的 Clamp 收敛。空串/半截输入不落值，等下一次合法输入。
    /// </summary>
    private void OnNumericBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { Tag: string tag } box) return;
        ScreenshotTriggerSettings? trigger = Trigger;
        if (trigger is null) return;

        int separator = tag.IndexOf(':');
        if (separator <= 0 || !Enum.TryParse(tag[..separator], out ScreenEdge edge)) return;
        string field = tag[(separator + 1)..];
        if (!int.TryParse(box.Text, out int value)) return;

        if (field == "Thickness") trigger.Of(edge).Thickness = value;
        else trigger.Of(edge).Length = value;
        RefreshMockup();
    }

    /// <summary>按当前配置重排四条热区色块；同时把越界值收敛回合法范围。</summary>
    private void RefreshMockup()
    {
        ScreenshotTriggerSettings? trigger = Trigger;
        if (trigger is null) return;
        _trigger = trigger;

        Rect work = SystemParameters.WorkArea;
        trigger.Clamp(work.Width, work.Height);

        LayoutZone(TopZone, ScreenEdge.Top, trigger, work);
        LayoutZone(BottomZone, ScreenEdge.Bottom, trigger, work);
        LayoutZone(LeftZone, ScreenEdge.Left, trigger, work);
        LayoutZone(RightZone, ScreenEdge.Right, trigger, work);
    }

    private static void LayoutZone(Border zone, ScreenEdge edge, ScreenshotTriggerSettings trigger, Rect work)
    {
        TriggerZoneEdge config = trigger.Of(edge);
        zone.Visibility = config.Enabled ? Visibility.Visible : Visibility.Collapsed;
        if (!config.Enabled) return;

        bool horizontal = edge is ScreenEdge.Top or ScreenEdge.Bottom;
        double span = horizontal ? work.Width : work.Height;   // 沿边总长（屏幕像素）
        double cross = horizontal ? work.Height : work.Width;  // 伸入屏幕方向（屏幕像素）
        double mockSpan = horizontal ? MockWidth : MockHeight;  // 示意图沿边尺寸
        double mockCross = horizontal ? MockHeight : MockWidth; // 示意图伸入方向尺寸

        double lengthPx = Math.Max(MinZonePixels, config.Length / span * mockSpan);
        double thickPx = Math.Max(3, config.Thickness / cross * mockCross);
        double startPx = config.Position * mockSpan - lengthPx / 2;

        if (horizontal)
        {
            zone.Width = lengthPx;
            zone.Height = thickPx;
            zone.Margin = new Thickness(Math.Clamp(startPx, 0, Math.Max(0, mockSpan - lengthPx)), 0, 0, 0);
        }
        else
        {
            zone.Width = thickPx;
            zone.Height = lengthPx;
            zone.Margin = new Thickness(0, Math.Clamp(startPx, 0, Math.Max(0, mockSpan - lengthPx)), 0, 0);
        }
    }

    private void OnZoneMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string name } || !Enum.TryParse(name, out ScreenEdge edge)) return;
        if (_trigger is null) return;

        _dragEdge = edge;
        _dragStartPosition = _trigger.Of(edge).Position;
        _dragStartFraction = PointerFraction(e.GetPosition(ScreenMock), edge);
        _dragging = true;
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnZoneMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || _trigger is null) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag(sender);
            return;
        }

        double fraction = PointerFraction(e.GetPosition(ScreenMock), _dragEdge);
        _trigger.Of(_dragEdge).Position = _dragStartPosition + (fraction - _dragStartFraction);
        RefreshMockup();
        e.Handled = true;
    }

    private void OnZoneMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        EndDrag(sender);
        e.Handled = true;
    }

    private static void EndDrag(object sender)
    {
        if (sender is UIElement element && element.IsMouseCaptured)
            element.ReleaseMouseCapture();
    }

    /// <summary>指针在示意图里的沿边比例（0..1，可越界，由 Clamp 收敛）。</summary>
    private static double PointerFraction(Point point, ScreenEdge edge) =>
        edge is ScreenEdge.Top or ScreenEdge.Bottom
            ? point.X / MockWidth
            : point.Y / MockHeight;
}
